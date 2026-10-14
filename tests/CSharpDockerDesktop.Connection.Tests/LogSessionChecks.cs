using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class LogSessionChecks
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private static void Check(bool value, string message = "Logs session assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!predicate()) await Task.Delay(2, deadline.Token);
    }
    private static ContainerLogTimestamp Stamp(int nanos = 123456789) => new("2026-10-14T00:00:00.123456789Z", 100, nanos);
    private static ContainerLogOutput Output(long sequence, string text, ContainerLogStream stream = ContainerLogStream.Stdout,
        ContainerLogTimestamp? stamp = null) => new(sequence, stream, text, sequence, ContainerLogBoundary.MultiplexedFrame,
            true, true, stamp);
    private static DesktopSession Session(Client client, ContainerLogsOptions? logs = null)
        => new(client, new() { RetryDelay = TimeSpan.FromMilliseconds(5), LivenessInterval = TimeSpan.FromHours(1) },
            reconciliation: new() { BatchInterval = TimeSpan.FromMilliseconds(2) },
            logOptions: logs ?? new() { NotificationInterval = TimeSpan.FromMilliseconds(20), RetryDelay = TimeSpan.FromMilliseconds(5) });
    private static async Task Ready(DesktopSession session)
    { session.Start(); await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready); }

    public static async Task AssemblyBoundsAndNotifications()
    {
        var connection = new Connection(); var subscription = new Subscription(A);
        connection.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(subscription));
        var options = new ContainerLogsOptions { MaximumEntries = 12, MaximumCharacters = 120,
            MaximumEntryCharacters = 20, MaximumPageEntries = 12, MaximumReplayCharacters = 100,
            MaximumReplayEntries = 12, NotificationInterval = TimeSpan.FromMilliseconds(40) };
        await using var session = Session(new(connection), options); await Ready(session);
        int notifications = 0; session.ContainerLogsChanged += (_, _) => Interlocked.Increment(ref notifications);
        Check(!session.OpenContainerLogs("aaaa", out var missing) && missing?.Kind == ContainerLogFailureKind.NotFound);
        Check(!session.OpenContainerLogs(A, out var invalid, tailLines: -1) && invalid?.Kind == ContainerLogFailureKind.InvalidRequest);
        Check(session.OpenContainerLogs(A, follow: false, tailLines: 7));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        // Boundaries are deliberately changed between fragments; frame endings are not line endings.
        await subscription.Write(Output(1, "alpha\r", stamp: Stamp()));
        await subscription.Write(Output(2, "err\n", ContainerLogStream.Stderr, Stamp(123456790)));
        await subscription.Write(Output(3, "\n🙂", stamp: Stamp()));
        await subscription.Write(Output(4, "", stamp: Stamp()));
        await subscription.Write(Output(5, "tail"));
        subscription.End();
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Completed);
        var page = session.ReadContainerLogs(maximumEntries: 12);
        Check(page.Entries.Count == 3 && page.Entries[0].Text == "alpha\r\n" && page.Entries[1].Text == "err\n"
            && page.Entries[2].Text == "🙂tail" && page.Entries[2].Partial);
        Check(page.Entries[0].Timestamp == Stamp() && page.Entries[1].Stream == ContainerLogStream.Stderr);
        Check(connection.Requests.Single().TailLines == 7 && !connection.Requests.Single().Follow);
        await Until(() => subscription.Disposed);
        await Task.Delay(60); Check(notifications <= 3, "Fragments must not notify individually");
        var high = new Subscription(A); connection.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(high));
        Check(session.OpenContainerLogs(A));
        var producer = Task.Run(async () =>
        {
            for (int index = 0; index < 20_000; ++index) await high.Write(Output(index, "🙂123456789012345678901234567890\r\n", stamp: Stamp()));
            high.End();
        });
        await producer.WaitAsync(Limit); await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Completed);
        Check(session.ContainerLogs.RetainedCharacters <= 120 && session.ContainerLogs.RetainedEntries <= 12
            && session.ContainerLogs.EvictedEntries > 10_000);
        Check(session.ReadContainerLogs(maximumEntries: 12).Entries.All(e => !e.Text.Contains('\uFFFD')));
        Check(notifications < 200, "High-volume notification work must remain coalesced");
        var immutable = session.ReadContainerLogs(maximumEntries: 12);
        session.ClearContainerLogs(); Check(session.ContainerLogs.RetainedEntries == 0 && immutable.Entries.Count > 0);
    }

    public static async Task ClearCloseAndLateReplacement()
    {
        var connection = new Connection(); var first = new Subscription(A);
        connection.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(first));
        var session = Session(new(connection)); await Ready(session); Check(session.OpenContainerLogs(A));
        await first.Write(Output(1, "old-partial", stamp: Stamp()));
        await Until(() => session.ContainerLogs.RetainedCharacters > 0);
        var before = session.ReadContainerLogs(); session.ClearContainerLogs();
        await first.Write(Output(2, "fresh\n", stamp: Stamp()));
        await Until(() => session.ContainerLogs.RetainedCharacters == 6);
        Check(session.ReadContainerLogs().Entries.Single().Text == "fresh\n"
            && session.ContainerLogs.LastObservedCursor == Stamp().Cursor && before.ViewRevision < session.ContainerLogs.ViewRevision);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ContainerLogSubscriptionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Open = (_, _) => { opened.TrySetResult(); return late.Task; };
        Check(session.OpenContainerLogs(A)); await opened.Task.WaitAsync(Limit); Check(first.Disposed);
        Check(session.OpenContainerLogs(B)); Check(session.OpenContainerLogs(A));
        var closing = session.CloseContainerLogsAsync(); await Task.Delay(20); Check(!closing.IsCompleted);
        var obsolete = new Subscription(A); await obsolete.Write(Output(999, "queued-stale\n")); late.SetResult(ContainerLogSubscriptionAttempt.Subscribed(obsolete));
        await closing.WaitAsync(Limit); Check(obsolete.Disposed && connection.Requests.Count == 2);
        await obsolete.Write(Output(3, "stale\n")); Check(session.ContainerLogs.Phase == ContainerLogsPhase.Closed && session.ReadContainerLogs().Entries.Count == 0);
        // Connection shutdown must wait for late Logs opening, even if the source ignores cancellation.
        var another = new TaskCompletionSource<ContainerLogSubscriptionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Open = (_, _) => another.Task; Check(session.OpenContainerLogs(A)); await Until(() => connection.Requests.Count == 3);
        var disposing = session.DisposeAsync().AsTask(); await Task.Delay(20); Check(!disposing.IsCompleted && !connection.Disposed);
        var final = new Subscription(A); another.SetResult(ContainerLogSubscriptionAttempt.Subscribed(final));
        await disposing.WaitAsync(Limit); Check(final.Disposed && connection.Disposed);
        await session.DisposeAsync();
    }

    public static async Task TerminalDeletionSelectionAndEof()
    {
        foreach (var kind in new[] { ContainerLogFailureKind.LoggingUnavailable, ContainerLogFailureKind.AccessDenied,
            ContainerLogFailureKind.MalformedOutput, ContainerLogFailureKind.DaemonError, ContainerLogFailureKind.SubscriptionLimit,
            ContainerLogFailureKind.InvalidRequest, ContainerLogFailureKind.NotFound })
        {
            var failure = new ContainerLogFailure(kind, "fixed", 500, "deliberate private daemon content", true);
            var connection = new Connection { Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Failed(failure)) };
            await using var session = Session(new(connection)); await Ready(session); session.SelectContainer(A); Check(session.OpenContainerLogs(A));
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Failed);
            Check(ReferenceEquals(session.ContainerLogs.Failure, failure) && connection.Requests.Count == 1);
            if (kind == ContainerLogFailureKind.NotFound) Check(!session.ContainerSnapshot.Containers.Any(c => c.Id == A));
        }
        foreach (bool follow in new[] { false, true })
        {
            var connection = new Connection(); var logs = new Subscription(A) { IsTty = true };
            connection.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(logs));
            await using var session = Session(new(connection)); await Ready(session); Check(session.OpenContainerLogs(A, follow));
            await logs.Write(Output(1, "combined\n", ContainerLogStream.Combined)); logs.End();
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Completed);
            await Task.Delay(25); Check(connection.Requests.Count == 1 && session.ContainerLogs.IsTty == true
                && session.ReadContainerLogs().Entries.Single().Stream == ContainerLogStream.Combined);
        }
        foreach (bool selection in new[] { false, true })
        {
            var connection = new Connection(); var logs = new Subscription(A);
            connection.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(logs));
            await using var session = Session(new(connection)); await Ready(session); session.SelectContainer(A);
            Check(session.OpenContainerLogs(A)); await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            if (selection) session.SelectContainer(B);
            else { connection.Rows = [Row(B)]; session.RefreshContainers(); }
            await Until(() => logs.Disposed); Check(session.ContainerLogs.Phase is ContainerLogsPhase.Closed or ContainerLogsPhase.Failed);
            await logs.Write(Output(1, "late\n")); Check(session.ReadContainerLogs().Entries.Count == 0);
        }
    }

    public static async Task RecoveryPrecisionMultiplicityAndAuthority()
    {
        var old = new Connection(); var fresh = new Connection();
        var first = new Subscription(A); var resumed = new Subscription(A);
        old.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(first));
        fresh.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(resumed));
        var list = new TaskCompletionSource<EngineResourceResult<IReadOnlyList<ContainerSummary>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fresh.List = _ => list.Task;
        await using var session = Session(new(old, fresh)); await Ready(session); Check(session.OpenContainerLogs(A));
        for (int i = 0; i < 2; ++i) await first.Write(Output(i, "same\n", stamp: Stamp()));
        await Until(() => session.ContainerLogs.RetainedEntries == 2);
        session.ClearContainerLogs();
        first.End(new(ContainerLogTermination.Failed, new(ContainerLogFailureKind.TransportLoss, "lost")));
        await Until(() => old.Disposed && session.ContainerSnapshot.State == ResourceReadState.Loading);
        Check(fresh.Requests.Count == 0, "Recovery needs fresh authoritative evidence");
        await first.Write(Output(99, "obsolete\n", stamp: Stamp(123456800)));
        list.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(fresh.Rows));
        await Until(() => fresh.Requests.Count == 1 && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        Check(fresh.Requests.Single().Since == new ContainerLogCursor(99, 123456789));
        // Different fragments/boundary IDs; identical messages and identical timestamps keep multiplicity.
        await resumed.Write(Output(500, "sa", stamp: Stamp()));
        await resumed.Write(Output(900, "me\nsame\n", stamp: Stamp()));
        await resumed.Write(Output(2, "same\n", stamp: Stamp()));
        await resumed.Write(Output(3, "new\n", stamp: Stamp(123456900)));
        resumed.End();
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Completed);
        Check(session.ReadContainerLogs().Entries.Select(e => e.Text).SequenceEqual(new[] { "same\n", "new\n" }), "Only replay occurrences may be suppressed");
        Check(session.ContainerLogs.LastObservedCursor == new ContainerLogCursor(100, 123456900)
            && session.ContainerLogs.ConnectionGeneration == session.ContainerSnapshot.ConnectionGeneration);
    }

    public static async Task DisposalJoinAndUnfinishedEviction()
    {
        var connection = new Connection();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new Subscription(A) { DisposeRelease = release };
        connection.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(logs));
        await using var session = Session(new(connection), new() { MaximumEntries = 3, MaximumCharacters = 60,
            MaximumEntryCharacters = 20, MaximumPageEntries = 3, NotificationInterval = TimeSpan.FromMilliseconds(10) });
        await Ready(session); Check(session.OpenContainerLogs(A));
        await logs.Write(Output(1, "unfinished", stamp: Stamp()));
        for (int i = 0; i < 8; ++i) await logs.Write(Output(i + 2, "err\n", ContainerLogStream.Stderr, Stamp()));
        await Until(() => session.ContainerLogs.EvictedEntries >= 6);
        Check(session.ContainerLogs.RetainedCharacters <= 60 && session.ContainerLogs.DiscardedCharacters >= 10);
        var revision = session.ContainerLogs.ViewRevision;
        await logs.Write(Output(20, "tail\n", stamp: Stamp()));
        await Until(() => session.ContainerLogs.ViewRevision > revision);
        Check(session.ReadContainerLogs(maximumEntries: 3).Entries.All(e => e.Stream == ContainerLogStream.Stderr),
            "Evicted unfinished content must not reappear at line completion");
        // Closing must join asynchronous subscription disposal, not only cancel its reader.
        var closing = session.CloseContainerLogsAsync(); await logs.DisposeStarted.Task.WaitAsync(Limit);
        Check(!closing.IsCompleted && session.ReadContainerLogs(maximumEntries: 3).Entries.Count == 0);
        var disconnecting = session.DisconnectAsync(); Check(!disconnecting.IsCompleted && !connection.Disposed);
        release.SetResult(); await Task.WhenAll(closing, disconnecting).WaitAsync(Limit);
        Check(logs.Disposed && connection.Disposed);
    }

    public static async Task RecoveryEvidenceAndCancellation()
    {
        // Missing/budget-exhausted comparison data preserves ambiguous replay and declares uncertainty.
        var old = new Connection(); var next = new Connection(); var first = new Subscription(A); var replay = new Subscription(A);
        old.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(first));
        next.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(replay));
        await using (var session = Session(new(old, next), new() { MaximumReplayEntries = 1,
            RetryDelay = TimeSpan.FromMilliseconds(5), NotificationInterval = TimeSpan.FromMilliseconds(10) }))
        {
            await Ready(session); Check(session.OpenContainerLogs(A, tailLines: 0));
            await first.Write(Output(1, "same\n", stamp: Stamp())); await first.Write(Output(2, "same\n", stamp: Stamp()));
            await Until(() => session.ContainerLogs.RetainedEntries == 2);
            first.End(new(ContainerLogTermination.Failed, new(ContainerLogFailureKind.TransportLoss, "lost")));
            await Until(() => next.Requests.Count == 1 && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            Check(next.Requests.Single().TailLines == 1 && next.Requests.Single().Since == new ContainerLogCursor(99, 123456789));
            await replay.Write(Output(5, "same\n", stamp: Stamp())); replay.End();
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Completed);
            Check(session.ContainerLogs.Continuity == ContainerLogsContinuity.Uncertain && session.ContainerLogs.RetainedEntries == 3);
        }
        // A canceled user request cannot revive after replacement list completion.
        var secondOld = new Connection(); var secondNew = new Connection(); var active = new Subscription(A);
        secondOld.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(active));
        var list = new TaskCompletionSource<EngineResourceResult<IReadOnlyList<ContainerSummary>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        secondNew.List = _ => list.Task;
        await using var canceled = Session(new(secondOld, secondNew)); await Ready(canceled); Check(canceled.OpenContainerLogs(A));
        await Until(() => canceled.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        active.End(new(ContainerLogTermination.Failed, new(ContainerLogFailureKind.TransportLoss, "lost")));
        await Until(() => secondOld.Disposed && canceled.ContainerSnapshot.State == ResourceReadState.Loading);
        await canceled.CloseContainerLogsAsync(); list.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(secondNew.Rows));
        await Until(() => canceled.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(secondNew.Requests.Count == 0 && canceled.ContainerLogs.Phase == ContainerLogsPhase.Closed);
    }

    public static async Task RecoveryLimitsIdentityAndCoexistence()
    {
        foreach (bool changed in new[] { false, true })
        {
            var first = new Connection(); var second = new Connection { EngineId = changed ? "different" : "same" };
            var one = new Subscription(A); var two = new Subscription(A);
            first.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(one));
            second.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(two));
            await using var session = Session(new(first, second), new() { MaximumRecoveryAttempts = 1,
                RetryDelay = TimeSpan.FromMilliseconds(5), NotificationInterval = TimeSpan.FromMilliseconds(10) });
            await Ready(session); Check(session.OpenContainerLogs(A));
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            await one.Write(Output(1, "untimestamped\n")); await Until(() => session.ContainerLogs.RetainedEntries == 1);
            one.End(new(ContainerLogTermination.Failed, new(ContainerLogFailureKind.TransportLoss, "lost")));
            await Until(() => changed ? session.ContainerLogs.Phase == ContainerLogsPhase.Failed : second.Requests.Count == 1);
            if (changed) { Check(second.Requests.Count == 0); continue; }
            Check(second.Requests.Single().Since is null && session.ContainerLogs.Continuity == ContainerLogsContinuity.Uncertain);
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            // Browsing, selection detail and an operation use their existing owner beside Logs.
            session.SelectContainer(A); // selection is itself an ownership boundary; explicitly reopen afterward.
            await session.CloseContainerLogsAsync(); var three = new Subscription(A);
            second.Open = (_, _) => Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(three));
            Check(session.OpenContainerLogs(A)); session.RefreshContainers();
            await second.EmitEvent(new("container", "start", A, null, null, new(1, 1_000_000_001)));
            await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
            var operation = await session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration, A), ContainerCommand.Start));
            Check(second.Commands == 1 && second.EventsOpened == 1 && operation.Resolution == ContainerOperationResolution.ObservedRequestedState);
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            var disconnecting = session.DisconnectAsync(); await disconnecting.WaitAsync(Limit);
            Check(three.Disposed && second.Disposed && session.ContainerLogs.Phase == ContainerLogsPhase.Closed);
        }
        // Exhaustion terminates log recovery without creating its own connection or looping permanently.
        var connections = Enumerable.Range(0, 3).Select(_ => new Connection { Open = (_, _) => Task.FromResult(
            ContainerLogSubscriptionAttempt.Failed(new(ContainerLogFailureKind.TransportLoss, "lost"))) }).ToArray();
        var client = new Client(connections);
        await using var bounded = Session(client, new() { MaximumRecoveryAttempts = 1,
            RetryDelay = TimeSpan.FromMilliseconds(5), NotificationInterval = TimeSpan.FromMilliseconds(10) });
        await Ready(bounded); Check(bounded.OpenContainerLogs(A));
        await Until(() => bounded.ContainerLogs.Phase == ContainerLogsPhase.Failed);
        await Task.Delay(30); Check(client.Calls == 2 && connections.Sum(c => c.Requests.Count) == 2);
    }

    private static ContainerSummary Row(string id) => new(id, null, null, null, "running", null, null, null, null, null, null);
    private sealed class Client(params Connection[] connections) : IEngineConnectionClient
    {
        public int Calls;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineConnectionAttempt.Connected(connections[Math.Min(Interlocked.Increment(ref Calls) - 1, connections.Length - 1)]));
    }
    private sealed class Connection : IEngineConnection, IContainerReader, IContainerLogSource, IContainerCommands, IEngineEventSource
    {
        public string EngineId = "same";
        public bool Disposed;
        public int Commands;
        public int EventsOpened;
        private EventSubscription? events;
        public IReadOnlyList<ContainerSummary> Rows = [Row(A), Row(B)];
        public Func<CancellationToken, Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>>>? List;
        public Func<ContainerLogRequest, CancellationToken, Task<ContainerLogSubscriptionAttempt>> Open = (_, _) => throw new InvalidOperationException();
        public List<ContainerLogRequest> Requests { get; } = [];
        public EngineMetadata Metadata => new(EngineEndpoint.LocalWindows, "fixture", new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 56)),
            "linux", "x64", null, EngineId, null, null, "linux", null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public async ValueTask DisposeAsync() { Disposed = true; if (events is not null) await events.DisposeAsync(); }
        public Task EmitEvent(EngineEvent value) => events!.Write(value);
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        { ++EventsOpened; events = new(cancellationToken); return Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(events)); }
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
            => List?.Invoke(cancellationToken) ?? Task.FromResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(Rows));
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<ContainerInspection>.Success(new(containerId, null, null, null, null,
                new("running", true, false, false, false, 0, null, null, null, null), null, null, null, "{}")));
        public Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request, CancellationToken cancellationToken = default)
        { lock (Requests) Requests.Add(request); return Open(request, cancellationToken); }
        public Task<ContainerCommandResult> StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
        { ++Commands; return Task.FromResult(new ContainerCommandResult(ContainerCommand.Start, ContainerCommandDisposition.Acknowledged)); }
        public Task<ContainerCommandResult> StopContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContainerCommandResult> RestartContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContainerCommandResult> DeleteContainerAsync(string containerId, bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class EventSubscription : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(8);
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration registration;
        public EventSubscription(CancellationToken token) => registration = token.Register(() => { channel.Writer.TryComplete(); completion.TrySetResult(null); });
        public Task Write(EngineEvent value) => channel.Writer.WriteAsync(value).AsTask();
        public ChannelReader<EngineEvent> Events => channel.Reader;
        public Task<EngineEventFailure?> Completion => completion.Task;
        public ValueTask DisposeAsync() { registration.Dispose(); channel.Writer.TryComplete(); completion.TrySetResult(null); return ValueTask.CompletedTask; }
    }
    private sealed class Subscription(string containerId) : IContainerLogSubscription
    {
        private readonly Channel<ContainerLogOutput> channel = Channel.CreateBounded<ContainerLogOutput>(32);
        private readonly TaskCompletionSource<ContainerLogCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ContainerId => containerId;
        public bool IsTty { get; init; }
        public bool Disposed;
        public TaskCompletionSource? DisposeRelease { get; init; }
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChannelReader<ContainerLogOutput> Output => channel.Reader;
        public Task<ContainerLogCompletion> Completion => completion.Task;
        public async Task Write(ContainerLogOutput item) { if (!Disposed) { try { await channel.Writer.WriteAsync(item); } catch (ChannelClosedException) { } } }
        public void End(ContainerLogCompletion? ending = null)
        { channel.Writer.TryComplete(); completion.TrySetResult(ending ?? new(ContainerLogTermination.CleanEnd)); }
        public async ValueTask DisposeAsync()
        {
            Disposed = true; End(new(ContainerLogTermination.Canceled)); DisposeStarted.TrySetResult();
            if (DisposeRelease is { } release) await release.Task;
        }
    }
}
