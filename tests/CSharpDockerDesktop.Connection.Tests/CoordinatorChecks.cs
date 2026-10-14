using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class CoordinatorChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    private static EngineConnectionFailure Failure(EngineConnectionFailureKind kind = EngineConnectionFailureKind.MissingEndpoint)
        => new(kind, "classified failure", 503, "private daemon message");
    private static void Check(bool condition, string message = "Coordinator assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task State(DesktopSession session, Func<ConnectionSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.Snapshot)) reached.TrySetResult(); }
        session.Changed += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.Changed -= Observe; }
    }

    public static async Task InitialRecovery()
    {
        var clock = new ControlledDelay(); var connection = new StubConnection("recovered");
        var client = new StubClient((index, _) => Task.FromResult(index == 1
            ? EngineConnectionAttempt.Failed(Failure()) : EngineConnectionAttempt.Connected(connection)));
        await using var session = new DesktopSession(client, delay: clock);
        session.Start(); session.Start();
        var retry = await clock.Next();
        Check(session.Snapshot.State == EngineConnectionState.Unavailable && session.Snapshot.AutomaticRecoveryPending);
        retry.Release();
        await State(session, s => s.State == EngineConnectionState.Connected);
        Check(session.Snapshot.Metadata?.EngineVersion == "recovered" && client.Calls == 2 && client.MaximumConcurrent == 1);
        await session.DisposeAsync(); await session.DisposeAsync();
        Check(connection.DisposeCount == 1);
    }

    public static async Task HealthyAndLost()
    {
        var clock = new ControlledDelay(); int checks = 0;
        var first = new StubConnection("old", _ => Task.FromResult(++checks == 3 ? Failure(EngineConnectionFailureKind.TransientTransportFailure) : null));
        var second = new StubConnection("new");
        var client = new StubClient((index, _) => Task.FromResult(EngineConnectionAttempt.Connected(index == 1 ? first : second)));
        await using var session = new DesktopSession(client, delay: clock);
        session.Start();
        for (int index = 0; index < 3; index++)
        {
            var ping = await clock.Next();
            Check(client.Calls == 1 && ReferenceEquals(session.Snapshot.Metadata, first.Metadata));
            ping.Release();
        }
        var recovery = await clock.Next();
        Check(session.Snapshot.State == EngineConnectionState.Lost && first.DisposeCount == 1);
        recovery.Release();
        await State(session, s => s.Metadata?.EngineVersion == "new");
        Check(client.Calls == 2 && client.MaximumConcurrent == 1 && session.Snapshot.Failure is null);
    }

    public static async Task FailuresAndBounds()
    {
        foreach (var kind in Enum.GetValues<EngineConnectionFailureKind>())
        {
            var failure = Failure(kind);
            await using var session = new DesktopSession(new StubClient((_, _) => Task.FromResult(EngineConnectionAttempt.Failed(failure))),
                new() { MaximumRecoveryAttempts = 0 });
            session.Start();
            await State(session, s => s.Failure is not null);
            Check(ReferenceEquals(session.Snapshot.Failure, failure));
            Check(!session.Snapshot.AutomaticRecoveryPending);
            Check((session.EngineState == EngineConnectionState.Unsupported) == (kind is EngineConnectionFailureKind.UnsupportedEndpoint or EngineConnectionFailureKind.IncompatibleApi));
        }
        var clock = new ControlledDelay();
        var client = new StubClient((_, _) => Task.FromResult(EngineConnectionAttempt.Failed(Failure())));
        await using var bounded = new DesktopSession(client, new() { MaximumRecoveryAttempts = 2 }, clock);
        bounded.Start();
        (await clock.Next()).Release(); (await clock.Next()).Release();
        var cooldown = await clock.Next();
        Check(bounded.Snapshot.RecoveryAttempt == 2 && bounded.Snapshot.AutomaticRecoveryPending);
        Check(cooldown.Duration == TimeSpan.FromSeconds(30));
        Check(client.Calls == 3);
        cooldown.Release();
        await State(bounded, s => s.RecoveryAttempt == 0 && s.AutomaticRecoveryPending);
        Check(client.Calls == 4);
    }

    public static async Task RetryRace()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateResult = new TaskCompletionSource<EngineConnectionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new StubConnection("stale"); var latest = new StubConnection("latest");
        var client = new StubClient(async (index, token) =>
        {
            if (index == 1) { started.SetResult(token); return await lateResult.Task; }
            Check(stale.DisposeCount == 1, "Superseded connection must be disposed before a new handshake");
            return EngineConnectionAttempt.Connected(latest);
        });
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        var observed = new List<string>();
        session.Changed += (_, _) => { if (session.Snapshot.Metadata is { } m) observed.Add(m.EngineVersion); };
        session.Start(); var oldToken = await started.Task.WaitAsync(Limit);
        session.Retry(); session.Retry(); session.Retry();
        Check(oldToken.IsCancellationRequested && client.Calls == 1);
        lateResult.SetResult(EngineConnectionAttempt.Connected(stale));
        await State(session, s => s.Metadata?.EngineVersion == "latest");
        Check(client.Calls == 2 && client.MaximumConcurrent == 1 && !observed.Contains("stale"));
    }

    public static async Task Shutdown()
    {
        var clock = new ControlledDelay();
        var checking = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new StubConnection("first", async token => { checking.TrySetResult(token); await Task.Delay(Timeout.Infinite, token); return null; });
        var second = new StubConnection("second");
        var client = new StubClient((index, _) => Task.FromResult(EngineConnectionAttempt.Connected(index == 1 ? first : second)));
        var session = new DesktopSession(client, delay: clock);
        session.Start(); (await clock.Next()).Release();
        var token = await checking.Task.WaitAsync(Limit);
        session.Retry();
        await State(session, s => s.Metadata?.EngineVersion == "second");
        Check(token.IsCancellationRequested && first.DisposeCount == 1);
        var delay = await clock.Next();
        await session.DisposeAsync(); await session.DisposeAsync();
        Check(delay.Token.IsCancellationRequested && second.DisposeCount == 1);
        session.Start(); session.Retry();
        Check(client.Calls == 2);
    }

    public static async Task DeadlinesAndRecoveryShutdown()
    {
        var late = new StubConnection("expired");
        var client = new StubClient(async (_, token) =>
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => canceled.TrySetResult());
            await canceled.Task;
            return EngineConnectionAttempt.Connected(late);
        });
        await using var expired = new DesktopSession(client, new() { OperationTimeout = TimeSpan.FromMilliseconds(25), MaximumRecoveryAttempts = 0 });
        expired.Start();
        await State(expired, s => s.State == EngineConnectionState.Unavailable);
        Check(late.DisposeCount == 1 && expired.Snapshot.Metadata is null);

        var clock = new ControlledDelay();
        var recovered = new StubConnection("ping expired", async token =>
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => canceled.TrySetResult());
            await canceled.Task;
            return null; // Deliberately reports success after its deadline.
        });
        await using var pingSession = new DesktopSession(new StubClient((_, _) => Task.FromResult(EngineConnectionAttempt.Connected(recovered))),
            new() { OperationTimeout = TimeSpan.FromMilliseconds(25), MaximumRecoveryAttempts = 0 }, clock);
        pingSession.Start(); (await clock.Next()).Release();
        await State(pingSession, s => s.State == EngineConnectionState.Lost);
        Check(recovered.DisposeCount == 1);

        var recoveryClock = new ControlledDelay();
        var missingClient = new StubClient((_, _) => Task.FromResult(EngineConnectionAttempt.Failed(Failure())));
        var missing = new DesktopSession(missingClient, delay: recoveryClock);
        missing.Start(); var scheduled = await recoveryClock.Next();
        await missing.DisposeAsync();
        Check(scheduled.Token.IsCancellationRequested && missingClient.Calls == 1);
    }

    public static async Task ShutdownLateSuccess()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateResult = new TaskCompletionSource<EngineConnectionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new StubConnection("late shutdown");
        var client = new StubClient(async (_, token) => { started.SetResult(token); return await lateResult.Task; });
        var session = new DesktopSession(client);
        session.Start(); var token = await started.Task.WaitAsync(Limit);
        var shutdown = session.DisposeAsync().AsTask();
        Check(token.IsCancellationRequested && !shutdown.IsCompleted);
        lateResult.SetResult(EngineConnectionAttempt.Connected(connection));
        await shutdown.WaitAsync(Limit);
        Check(connection.DisposeCount == 1 && session.Snapshot.Metadata is null && client.Calls == 1);
    }

    private sealed class StubClient(Func<int, CancellationToken, Task<EngineConnectionAttempt>> connect) : IEngineConnectionClient
    {
        private int calls, active, maximum;
        public int Calls => Volatile.Read(ref calls);
        public int MaximumConcurrent => Volatile.Read(ref maximum);
        public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        {
            var concurrency = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, concurrency);
            try { return await connect(Interlocked.Increment(ref calls), cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private sealed class StubConnection(string version, Func<CancellationToken, Task<EngineConnectionFailure?>>? ping = null) : IEngineConnection
    {
        public int DisposeCount;
        public EngineMetadata Metadata { get; } = new(EngineEndpoint.LocalWindows, version,
            new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 56)),
            null, null, null, null, null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default)
            => ping?.Invoke(cancellationToken) ?? Task.FromResult<EngineConnectionFailure?>(null);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }
    private sealed class ControlledDelay : IConnectionDelay
    {
        private readonly Channel<PendingDelay> waits = Channel.CreateUnbounded<PendingDelay>();
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(cancellationToken, delay);
            await waits.Writer.WriteAsync(pending, cancellationToken);
            await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        public async Task<PendingDelay> Next() => await waits.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class PendingDelay(CancellationToken token, TimeSpan duration)
    {
        public CancellationToken Token { get; } = token;
        public TimeSpan Duration { get; } = duration;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Completion.TrySetResult();
    }
}
