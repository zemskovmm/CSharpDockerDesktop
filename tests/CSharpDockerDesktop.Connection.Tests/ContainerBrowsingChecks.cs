using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class ContainerBrowsingChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    private static readonly string A = new('a', 64), B = new('b', 64), C = new('c', 64);
    private const string Raw = "{\n  \"Id\": \"full-id\", \"Config\": {\"Env\": [\"SECRET=private\"]}, \"Unknown\": true\n}";
    private static ContainerSummary Row(string id) => new(id, ["/private-name"], "private-image", null,
        "exited", "Exited", null, null, null, null, null);
    private static ContainerInspection Detail(string id, string raw = Raw) => new(id, "/private-name", null,
        null, null, null, null, null, null, raw);
    private static EngineResourceFailure Failure(EngineResourceFailureKind kind) => new(kind,
        "classified resource failure", 503, "original private daemon message\nunchanged");
    private static void Check(bool condition, string message = "Container browsing assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task State(DesktopSession session, Func<ContainerBrowsingSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.ContainerSnapshot)) reached.TrySetResult(); }
        session.ContainersChanged += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.ContainersChanged -= Observe; }
    }
    private static async Task ConnectionState(DesktopSession session, Func<ConnectionSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.Snapshot)) reached.TrySetResult(); }
        session.Changed += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.Changed -= Observe; }
    }
    private static async Task Load(DesktopSession session, ControlledConnection connection, params string[] ids)
    {
        (await connection.NextList()).Succeed(ids);
        await State(session, s => s.State == ResourceReadState.Ready);
    }

    public static async Task InitialEmptyAndReplacement()
    {
        var connection = new ControlledConnection("initial");
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        IContainerBrowser browser = session;
        Check(browser.ContainerSnapshot.State == ResourceReadState.Unavailable);
        session.Start();
        var initial = await connection.NextList();
        Check(browser.ContainerSnapshot.State == ResourceReadState.Loading && browser.ContainerSnapshot.Containers.Count == 0);
        initial.Succeed();
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(browser.ContainerSnapshot.Containers.Count == 0 && session.EngineState == EngineConnectionState.Connected);
        browser.RefreshContainers();
        await Load(session, connection, A, B);
        browser.SelectContainer(A);
        var inspect = await connection.NextInspection();
        Check(inspect.Identity == A && browser.ContainerSnapshot.InspectionState == ResourceReadState.Loading);
        inspect.Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(browser.ContainerSnapshot.Inspection!.Id == A && browser.ContainerSnapshot.Inspection.RawJson == Raw);
        browser.RefreshContainers();
        await Load(session, connection, B, C, C);
        Check(browser.ContainerSnapshot.Containers.Select(row => row.Id).SequenceEqual(new[] { B, C }));
        Check(browser.ContainerSnapshot.SelectedContainerId is null && browser.ContainerSnapshot.Inspection is null);
        browser.SelectContainer(A); // A user selection from an older rendered snapshot is harmless.
        Check(browser.ContainerSnapshot.SelectedContainerId is null && connection.InspectionCalls == 1);
        browser.RefreshContainers();
        await Load(session, connection, C);
        Check(browser.ContainerSnapshot.Containers.Single().Id == C && connection.MaximumConcurrent == 1);
    }

    public static async Task PreservedFailuresAndDisappearance()
    {
        foreach (var kind in Enum.GetValues<EngineResourceFailureKind>().Where(k => k != EngineResourceFailureKind.TransportLoss))
        {
            var connection = new ControlledConnection("errors");
            var client = new ControlledClient(connection);
            await using var session = new DesktopSession(client, delay: new ControlledDelay());
            session.Start();
            var failure = Failure(kind);
            (await connection.NextList()).Fail(failure);
            await State(session, s => s.State == ResourceReadState.Failed);
            Check(ReferenceEquals(session.ContainerSnapshot.Failure, failure));
            Check(client.Calls == 1 && session.EngineState == EngineConnectionState.Connected);
            session.RefreshContainers();
            await Load(session, connection, A, B);
            session.SelectContainer(A);
            (await connection.NextInspection()).Succeed(Detail(A));
            await State(session, s => s.InspectionState == ResourceReadState.Ready);
            session.RefreshContainerInspection();
            (await connection.NextInspection()).Fail(failure);
            await State(session, s => s.InspectionState == ResourceReadState.Failed);
            Check(ReferenceEquals(session.ContainerSnapshot.InspectionFailure, failure) && session.ContainerSnapshot.Inspection is null);
            Check(session.EngineState == EngineConnectionState.Connected && client.Calls == 1);
            if (kind == EngineResourceFailureKind.NotFound)
                Check(session.ContainerSnapshot.SelectedContainerId is null
                    && session.ContainerSnapshot.Containers.Select(row => row.Id).SequenceEqual(new[] { B }));
            else Check(session.ContainerSnapshot.SelectedContainerId == A && session.ContainerSnapshot.Containers.Count == 2);
        }
    }

    public static async Task SelectionRacesAndClearing()
    {
        var connection = new ControlledConnection("races", honorCancellation: false);
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        var observed = new List<string>();
        session.ContainersChanged += (_, _) => { if (session.ContainerSnapshot.Inspection is { } value) observed.Add(value.Id); };
        session.Start(); await Load(session, connection, A, B, C);
        session.SelectContainer(A);
        var first = await connection.NextInspection();
        for (int index = 0; index < 200; index++) session.SelectContainer(index % 2 == 0 ? B : C);
        Check(first.Token.IsCancellationRequested && connection.InspectionCalls == 1 && connection.MaximumConcurrent == 1);
        first.Succeed(Detail(A)); // Reader deliberately completes successfully after cancellation.
        var latest = await connection.NextInspection();
        Check(latest.Identity == C && !observed.Contains(A));
        session.SelectContainer(null);
        Check(latest.Token.IsCancellationRequested && session.ContainerSnapshot.Inspection is null);
        latest.Succeed(Detail(C));
        session.SelectContainer(B);
        var retained = await connection.NextInspection();
        session.ClearContainerInspection();
        Check(retained.Token.IsCancellationRequested && session.ContainerSnapshot.SelectedContainerId == B
            && session.ContainerSnapshot.InspectionState == ResourceReadState.NotRequested);
        session.RefreshContainers(); // Refresh must not reopen an explicitly cleared inspection.
        retained.Fail(Failure(EngineResourceFailureKind.NotFound)); // A superseded error cannot remove the latest row.
        await Load(session, connection, A, B, C);
        Check(session.ContainerSnapshot.InspectionState == ResourceReadState.NotRequested && connection.InspectionCalls == 3);
        session.RefreshContainerInspection();
        var reload = await connection.NextInspection();
        Check(reload.Identity == B && session.ContainerSnapshot.Containers.Any(row => row.Id == B));
        reload.Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(observed.All(id => id == B) && session.ContainerSnapshot.Inspection!.RawJson == Raw);

        session.RefreshContainerInspection();
        var vanishing = await connection.NextInspection();
        session.RefreshContainers();
        Check(vanishing.Token.IsCancellationRequested);
        vanishing.Succeed(Detail(B));
        await Load(session, connection, A);
        Check(session.ContainerSnapshot.SelectedContainerId is null && session.ContainerSnapshot.Inspection is null
            && connection.InspectionCalls == 5, "Fresh absence must cancel the pending detail reload");
    }

    public static async Task BoundedRefreshAndDeadline()
    {
        var connection = new ControlledConnection("bounded", honorCancellation: false);
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        session.Start();
        var first = await connection.NextList();
        for (int index = 0; index < 1000; index++) session.RefreshContainers();
        Check(first.Token.IsCancellationRequested && connection.ListCalls == 1);
        first.Succeed(A);
        var latest = await connection.NextList();
        Check(session.ContainerSnapshot.Containers.Count == 0 && connection.ListCalls == 2 && connection.MaximumConcurrent == 1);
        latest.Succeed(B);
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(session.ContainerSnapshot.Containers.Single().Id == B);

        var timed = new ControlledConnection("deadline");
        await using var timeout = new DesktopSession(new ControlledClient(timed), new()
            { OperationTimeout = TimeSpan.FromMilliseconds(100), MaximumRecoveryAttempts = 0 }, new ControlledDelay());
        timeout.Start();
        var pending = await timed.NextList();
        await ConnectionState(timeout, s => s.State == EngineConnectionState.Lost);
        Check(pending.Token.IsCancellationRequested && timed.DisposeCount == 1
            && timeout.ContainerSnapshot.Failure?.Kind == EngineResourceFailureKind.TransportLoss);
    }

    public static async Task ReconnectInvalidationAndLateReads()
    {
        var old = new ControlledConnection("old", honorCancellation: false);
        var current = new ControlledConnection("new");
        var client = new ControlledClient(old, current);
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        session.Start(); await Load(session, old, A);
        long generation = session.ContainerSnapshot.ConnectionGeneration;
        session.SelectContainer(A);
        var late = await old.NextInspection();
        session.Retry();
        Check(late.Token.IsCancellationRequested && session.ContainerSnapshot.State == ResourceReadState.Unavailable
            && session.ContainerSnapshot.Containers.Count == 0 && session.ContainerSnapshot.SelectedContainerId is null);
        Check(old.DisposeCount == 0 && client.Calls == 1, "Retained connection must outlive the canceled reader");
        late.Succeed(Detail(A));
        var initial = await current.NextList();
        Check(old.DisposeCount == 1 && !old.DisposedWithActiveRead && session.ContainerSnapshot.ConnectionGeneration > generation
            && session.ContainerSnapshot.State == ResourceReadState.Loading && session.ContainerSnapshot.Inspection is null);
        initial.Succeed(B);
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(session.ContainerSnapshot.Containers.Single().Id == B && session.Snapshot.Metadata?.EngineVersion == "new");
    }

    public static async Task ResourceDetectedLossAndRecovery()
    {
        foreach (bool inspect in new[] { false, true })
        {
            var old = new ControlledConnection("before-loss");
            var fresh = new ControlledConnection("renegotiated");
            var clock = new ControlledDelay();
            var client = new ControlledClient(old, fresh);
            await using var session = new DesktopSession(client, delay: clock);
            session.Start();
            var initial = await old.NextList();
            var liveness = await clock.Next();
            long generation = session.ContainerSnapshot.ConnectionGeneration;
            var failure = Failure(EngineResourceFailureKind.TransportLoss);
            if (inspect)
            {
                initial.Succeed(A);
                await State(session, s => s.State == ResourceReadState.Ready);
                session.SelectContainer(A);
                (await old.NextInspection()).Fail(failure);
            }
            else initial.Fail(failure);
            await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
            Check(liveness.Token.IsCancellationRequested && old.DisposeCount == 1 && session.ContainerSnapshot.Containers.Count == 0);
            Check(ReferenceEquals(inspect ? session.ContainerSnapshot.InspectionFailure : session.ContainerSnapshot.Failure, failure));
            Check(session.Snapshot.Failure?.DaemonMessage == failure.DaemonMessage);
            var retry = await clock.Next();
            session.RefreshContainers(); session.SelectContainer(null); // No resource reads while disconnected.
            Check(client.Calls == 1 && old.ListCalls == 1);
            retry.Release();
            var snapshot = await fresh.NextList();
            Check(session.ContainerSnapshot.ConnectionGeneration > generation && session.ContainerSnapshot.State == ResourceReadState.Loading);
            snapshot.Succeed(B);
            await State(session, s => s.State == ResourceReadState.Ready);
            Check(client.Calls == 2 && session.Snapshot.Metadata?.EngineVersion == "renegotiated"
                && session.ContainerSnapshot.Containers.Single().Id == B && fresh.Metadata.Api.Negotiated == new EngineApiVersion(1, 40));
        }
    }

    public static async Task LivenessLossAndShutdown()
    {
        var clock = new ControlledDelay();
        var connection = new ControlledConnection("liveness", honorCancellation: false)
        { PingFailure = new(EngineConnectionFailureKind.TransientTransportFailure, "readiness lost") };
        var session = new DesktopSession(new ControlledClient(connection), new() { MaximumRecoveryAttempts = 0 }, clock);
        session.Start(); await Load(session, connection, A);
        session.SelectContainer(A);
        var detail = await connection.NextInspection();
        (await clock.Next()).Release();
        await State(session, s => s.State == ResourceReadState.Unavailable);
        Check(detail.Token.IsCancellationRequested && session.ContainerSnapshot.Inspection is null);
        detail.Succeed(Detail(A));
        await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
        Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead);
        await session.DisposeAsync();

        foreach (bool inspect in new[] { false, true })
        {
            var closing = new ControlledConnection("shutdown", honorCancellation: false);
            var client = new ControlledClient(closing);
            var shutdown = new DesktopSession(client, delay: new ControlledDelay());
            shutdown.Start();
            var list = await closing.NextList();
            InspectionCall? inspection = null;
            if (inspect)
            {
                list.Succeed(A); await State(shutdown, s => s.State == ResourceReadState.Ready);
                shutdown.SelectContainer(A); inspection = await closing.NextInspection();
            }
            var completion = shutdown.DisposeAsync().AsTask();
            Check((inspection?.Token ?? list.Token).IsCancellationRequested && !completion.IsCompleted
                && shutdown.ContainerSnapshot.State == ResourceReadState.Unavailable && shutdown.ContainerSnapshot.Containers.Count == 0);
            if (inspection is not null) inspection.Succeed(Detail(A)); else list.Succeed(A);
            await completion.WaitAsync(Limit);
            await shutdown.DisposeAsync();
            Check(closing.DisposeCount == 1 && !closing.DisposedWithActiveRead && shutdown.ContainerSnapshot.Inspection is null);
            shutdown.Start(); shutdown.RefreshContainers(); shutdown.SelectContainer(A);
            Check(client.Calls == 1 && closing.ListCalls == 1);
        }
    }

    private sealed class ControlledClient(params ControlledConnection[] connections) : IEngineConnectionClient
    {
        public int Calls;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(EngineConnectionAttempt.Connected(connections[Interlocked.Increment(ref Calls) - 1]));
        }
    }
    private sealed class ControlledConnection(string version, bool honorCancellation = true) : IEngineConnection, IContainerReader
    {
        private readonly Channel<ListCall> lists = Channel.CreateUnbounded<ListCall>();
        private readonly Channel<InspectionCall> inspections = Channel.CreateUnbounded<InspectionCall>();
        private int active;
        public int MaximumConcurrent, DisposeCount, ListCalls, InspectionCalls;
        public bool DisposedWithActiveRead;
        public EngineConnectionFailure? PingFailure;
        public EngineMetadata Metadata { get; } = new(EngineEndpoint.LocalWindows, version,
            new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 40)),
            null, null, null, null, null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(PingFailure);
        public ValueTask DisposeAsync()
        { DisposedWithActiveRead |= Volatile.Read(ref active) != 0; Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
        public async Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref ListCalls);
            try
            {
                var call = new ListCall(cancellationToken); lists.Writer.TryWrite(call);
                return await (honorCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref InspectionCalls);
            try
            {
                var call = new InspectionCall(identity, cancellationToken); inspections.Writer.TryWrite(call);
                return await (honorCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        private void Enter() { Check(DisposeCount == 0); MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref active)); }
        public async Task<ListCall> NextList() => await lists.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public async Task<InspectionCall> NextInspection() => await inspections.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class ListCall(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<IReadOnlyList<ContainerSummary>>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(params string[] ids) => Completion.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(ids.Select(Row).ToArray()));
        public void Fail(EngineResourceFailure failure) => Completion.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Failed(failure));
    }
    private sealed class InspectionCall(string identity, CancellationToken token)
    {
        public string Identity { get; } = identity;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<ContainerInspection>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(ContainerInspection value) => Completion.SetResult(EngineResourceResult<ContainerInspection>.Success(value));
        public void Fail(EngineResourceFailure failure) => Completion.SetResult(EngineResourceResult<ContainerInspection>.Failed(failure));
    }
    private sealed class ControlledDelay : IConnectionDelay
    {
        private readonly Channel<PendingDelay> waits = Channel.CreateUnbounded<PendingDelay>();
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(cancellationToken); waits.Writer.TryWrite(pending);
            await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        public async Task<PendingDelay> Next() => await waits.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class PendingDelay(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Completion.TrySetResult();
    }
}
