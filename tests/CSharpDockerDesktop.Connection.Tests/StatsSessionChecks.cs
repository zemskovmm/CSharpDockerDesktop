using System.Collections.Immutable;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class StatsSessionChecks
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static void Check(bool value, string message = "Stats session assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!predicate()) await Task.Delay(1, deadline.Token);
    }
    private static DesktopSession Session(Client client, ManualTime time, ContainerStatsOptions? options = null,
        TimeSpan? openingTimeout = null) => new(client,
        new() { LivenessInterval = TimeSpan.FromHours(1), RetryDelay = TimeSpan.FromMilliseconds(2),
            OperationTimeout = openingTimeout ?? TimeSpan.FromSeconds(20) },
        reconciliation: new() { BatchInterval = TimeSpan.FromMilliseconds(1) },
        statsOptions: options, statsTimeProvider: time);
    private static async Task Ready(DesktopSession session, string id = A)
    {
        session.Start(); await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
        session.SelectContainer(id); await Until(() => session.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
    }
    private static async Task Tick(ManualTime time, int seconds = 1)
    {
        await Until(() => time.ActiveTimers > 0);
        time.Advance(TimeSpan.FromSeconds(seconds));
        await Task.Delay(3);
    }
    private static ContainerStatsSample Sample(long second, ulong total = 100, ulong usage = 0, string id = A,
        bool missingMemory = false) => new(id, "linux", new("daemon evidence", second, 0), null,
        new(total, total * 10, 2, null, null, null), null, null,
        missingMemory ? null : new(usage, 1000, null, null,
            ImmutableDictionary<string, ulong?>.Empty.Add("inactive_file", 0), null, null, null),
        ImmutableDictionary<string, ContainerNetworkCounters?>.Empty,
        ImmutableArray<ContainerBlockIoCounter>.Empty, null);

    public static async Task AdmissionCadenceZerosAndBounds()
    {
        var connection = new Connection(); var time = new ManualTime(); var stream = new Subscription(A);
        connection.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(stream));
        await using var session = Session(new(connection), time,
            new() { MaximumEntries = 12, MaximumReadEntries = 4 });
        session.Start(); await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(!session.OpenContainerStats(A) && connection.Opens == 0, "An inactive selected view must not open Stats");
        session.SelectContainer(A); await Until(() => session.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
        Check(!session.OpenContainerStats("aaaa"));
        Check(session.OpenContainerStats(A)); await Until(() => connection.Opens == 1);
        Check(session.ContainerStats.Current is null, "Opening is not running/freshness evidence");
        int notices = 0; session.ContainerStatsChanged += (_, _) => Interlocked.Increment(ref notices);
        for (int i = 0; i < 10_000; ++i) await stream.Write(Sample(100 + i, (ulong)(100 + i)));
        await Until(() => session.ContainerStats.ProcessedSamples == 10_000);
        Check(session.ContainerStats.Current is null && notices == 0);
        await Tick(time); await Until(() => session.ContainerStats.Current is not null);
        Check(session.ContainerStats.Current!.MemoryUsageBytes.Value == 0
            && session.ContainerStats.Current.NetworkReceivedBytesPerSecond.Value == 0
            && notices == 1, "Raw baselines must advance during a coalesced display burst");
        var before = session.ReadContainerStats();
        for (int i = 0; i < 25; ++i)
        {
            await stream.Write(Sample(20_000 + i, (ulong)(20_000 + i), missingMemory: true));
            await Until(() => session.ContainerStats.ProcessedSamples == 10_001 + i);
            await Tick(time);
        }
        Check(session.ContainerStats.Current!.MemoryUsageBytes.Value is null
            && session.ContainerStats.Current.MemoryUsageBytes.Availability == ContainerStatsAvailability.MissingEvidence);
        Check(session.ContainerStats.RetainedEntries == 12 && session.ContainerStats.EvictedEntries > 0);
        Check(session.ReadContainerStats(maximumEntries: 1024).Entries.Count == 4 && before.Entries.Count == 2);
        var copied = session.ReadContainerStats();
        Check(copied.Entries is not ContainerStatsHistoryEntry[], "History copies must not expose mutable arrays");
        await session.CloseContainerStatsAsync();
        Check(session.ContainerStats.Current is null && session.ReadContainerStats().Entries.Count == 0 && copied.Entries.Count > 0);
    }

    public static async Task ReplacementsLateOpenAndJoinedClose()
    {
        var connection = new Connection(); var time = new ManualTime();
        var late = new TaskCompletionSource<ContainerStatsSubscriptionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Open = (_, _) => late.Task;
        var session = Session(new(connection), time); await Ready(session);
        Check(session.OpenContainerStats(A)); await Until(() => connection.Opens == 1);
        for (int i = 0; i < 1000; ++i) Check(session.OpenContainerStats(A));
        Check(connection.Opens == 1, "Replacement must have one running and one latest pending request");
        var closing = session.CloseContainerStatsAsync(); Check(!closing.IsCompleted);
        var obsolete = new Subscription(A); await obsolete.Write(Sample(100));
        late.SetResult(ContainerStatsSubscriptionAttempt.Subscribed(obsolete));
        await closing.WaitAsync(Limit); Check(obsolete.Disposed && connection.Opens == 1);
        Check(session.ContainerStats.Phase == ContainerStatsPhase.Closed && session.ContainerStats.ProcessedSamples == 0);
        // Closing joins only pre-existing work, even if a later view starts immediately.
        var first = new Subscription(A) { DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var second = new Subscription(A);
        connection.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(first));
        Check(session.OpenContainerStats(A)); await Until(() => connection.Opens == 2);
        closing = session.CloseContainerStatsAsync(); await Until(() => first.Disposed);
        connection.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(second));
        Check(session.OpenContainerStats(A)); first.DisposeRelease.SetResult();
        await closing.WaitAsync(Limit); await Until(() => connection.Opens == 3);
        Check(!second.Disposed); await session.DisposeAsync(); Check(second.Disposed && connection.Disposed);
    }

    public static async Task SelectionDisconnectDisposalAndLateFailures()
    {
        foreach (int action in new[] { 0, 1, 2, 3 })
        {
            var connection = new Connection(); var time = new ManualTime();
            var late = new TaskCompletionSource<ContainerStatsSubscriptionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken captured = default;
            connection.Open = (_, token) => { captured = token; return late.Task; };
            var session = Session(new(connection), time); await Ready(session);
            Check(session.OpenContainerStats(A)); await Until(() => connection.Opens == 1);
            Task? join = action switch { 0 => session.CloseContainerStatsAsync(), 1 => session.DisconnectAsync(),
                2 => session.DisposeAsync().AsTask(), _ => null };
            if (action == 3) session.SelectContainer(B);
            Check(captured.IsCancellationRequested && session.ContainerStats.Current is null
                && session.ContainerStats.Phase == ContainerStatsPhase.Closed);
            if (join is not null) Check(!join.IsCompleted);
            late.SetResult(ContainerStatsSubscriptionAttempt.Failed(new(ContainerStatsFailureKind.TransportLoss, "obsolete")));
            if (join is not null) await join.WaitAsync(Limit);
            Check(session.ContainerStats.Failure is null && connection.Opens == 1);
            await session.DisposeAsync();
        }
        // Noncooperative late successful opening is owned until disposal finishes.
        var c = new Connection(); var t = new ManualTime();
        var opening = new TaskCompletionSource<ContainerStatsSubscriptionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Open = (_, _) => opening.Task;
        var s = Session(new(c), t); await Ready(s); Check(s.OpenContainerStats(A)); await Until(() => c.Opens == 1);
        var dispose = s.DisposeAsync().AsTask(); Check(!dispose.IsCompleted && !c.Disposed);
        var lateStream = new Subscription(A) { DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        opening.SetResult(ContainerStatsSubscriptionAttempt.Subscribed(lateStream)); await Until(() => lateStream.Disposed);
        Check(!dispose.IsCompleted && !c.Disposed); lateStream.DisposeRelease.SetResult();
        await dispose.WaitAsync(Limit); Check(c.Disposed);
    }

    public static async Task LifecycleAuthorityAndStalePublications()
    {
        var c = new Connection(); var t = new ManualTime(); var streams = new List<Subscription>();
        c.Open = (id, _) => { var stream = new Subscription(id); streams.Add(stream); return Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(stream)); };
        await using var s = Session(new(c), t); await Ready(s); Check(s.OpenContainerStats(A)); await Until(() => c.Opens == 1);
        await streams[0].Write(Sample(100)); await Until(() => s.ContainerStats.ProcessedSamples == 1); await Tick(t);
        Check(s.ContainerStats.Current is not null);
        var blockedList = new TaskCompletionSource<EngineResourceResult<IReadOnlyList<ContainerSummary>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.List = _ => blockedList.Task;
        await c.Emit("die"); await Until(() => s.ContainerStats.Current is null);
        Check(s.ContainerStats.Phase == ContainerStatsPhase.AwaitingLifecycle);
        await streams[0].Write(Sample(101, 200));
        c.State = State("exited", false); c.Rows = [Row(A, "exited"), Row(B)];
        blockedList.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(c.Rows)); c.List = null;
        await Until(() => s.ContainerSnapshot.InspectionState == ResourceReadState.Ready
            && s.ContainerStats.Phase == ContainerStatsPhase.Inactive);
        Check(c.Opens == 1 && s.ContainerStats.Current is null);
        c.State = State(); c.Rows = [Row(A), Row(B)]; c.Restarts = 1;
        await c.Emit("start"); await Until(() => c.Opens == 2);
        await streams[1].Write(Sample(102, 300)); await Until(() => s.ContainerStats.ProcessedSamples == 2); await Tick(t);
        Check(s.ContainerStats.Current!.CpuPercent.Availability == ContainerStatsAvailability.BaselineRequired,
            "Restart must not bridge CPU evidence");
        // Current authoritative contradiction revokes the measurement, despite positive counters.
        c.State = State("running", false); await c.Emit("update");
        await Until(() => s.ContainerStats.Phase == ContainerStatsPhase.Inactive); Check(c.Opens == 2);
        c.State = State(); c.Rows = [Row(A, "unknown"), Row(B)]; s.RefreshContainers();
        await Until(() => s.ContainerSnapshot.State == ResourceReadState.Ready
            && s.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
        Check(c.Opens == 2 && s.ContainerStats.Current is null);
        c.Rows = [Row(B)]; s.RefreshContainers(); await Until(() => s.ContainerStats.Phase == ContainerStatsPhase.Closed);
        Check(s.ReadContainerStats().Entries.Count == 0);
    }

    public static async Task LateOpeningLifecycleAndInspectionFences()
    {
        var c = new Connection(); var t = new ManualTime();
        var opening = new TaskCompletionSource<ContainerStatsSubscriptionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Open = (_, _) => opening.Task;
        await using var s = Session(new(c), t); await Ready(s); Check(s.OpenContainerStats(A)); await Until(() => c.Opens == 1);
        c.State = State("exited", false); c.Rows = [Row(A, "exited"), Row(B)]; await c.Emit("stop");
        await Until(() => s.ContainerStats.Phase == ContainerStatsPhase.Inactive);
        var old = new Subscription(A); await old.Write(Sample(100)); opening.SetResult(ContainerStatsSubscriptionAttempt.Subscribed(old));
        await Until(() => old.Disposed); Check(s.ContainerStats.Phase == ContainerStatsPhase.Inactive && s.ContainerStats.ProcessedSamples == 0);
        // An inspection begun before a lifecycle event cannot authorize a replacement.
        var inspection = new TaskCompletionSource<EngineResourceResult<ContainerInspection>>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Inspect = (_, _) => inspection.Task; c.State = State(); c.Rows = [Row(A), Row(B)];
        s.RefreshContainers(); await Until(() => c.Inspections >= 3);
        await c.Emit("die"); c.State = State("exited", false); c.Rows = [Row(A, "exited"), Row(B)];
        c.Inspect = null;
        inspection.SetResult(EngineResourceResult<ContainerInspection>.Success(Inspection(A, State())));
        await Until(() => s.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
        Check(s.ContainerStats.Current is null && c.Opens == 1);
    }

    public static async Task StalledEmptyCompletedAndPermanentFailures()
    {
        var c = new Connection(); var t = new ManualTime(); var stream = new Subscription(A);
        c.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(stream));
        await using var s = Session(new(c), t); await Ready(s); Check(s.OpenContainerStats(A)); await Until(() => c.Opens == 1);
        await Tick(t, 6); Check(s.ContainerStats.Phase == ContainerStatsPhase.Stalled && s.ContainerStats.Current is null);
        await stream.Write(Sample(100)); await Until(() => s.ContainerStats.ProcessedSamples == 1); await Tick(t);
        Check(s.ContainerStats.Current is not null);
        await Tick(t, 6); Check(s.ContainerStats.Current is null);
        await stream.Write(Sample(101, 1000)); await Until(() => s.ContainerStats.ProcessedSamples == 2); await Tick(t);
        Check(s.ContainerStats.Current!.CpuPercent.Availability == ContainerStatsAvailability.BaselineRequired);
        stream.End(); await Until(() => s.ContainerStats.Phase == ContainerStatsPhase.Completed);
        Check(s.ContainerStats.Current is null && s.ContainerStats.Completion!.Termination == ContainerStatsTermination.CleanEnd
            && s.ReadContainerStats().Entries.Any(e => e.Boundary == ContainerStatsBoundary.SampleGap));
        foreach (var kind in new[] { ContainerStatsFailureKind.AccessDenied, ContainerStatsFailureKind.DaemonError,
            ContainerStatsFailureKind.IncompatibleApi, ContainerStatsFailureKind.NotFound, ContainerStatsFailureKind.MalformedSample,
            ContainerStatsFailureKind.SubscriptionLimit, ContainerStatsFailureKind.InvalidRequest })
        {
            var failure = new ContainerStatsFailure(kind, "safe diagnostic", 500, "deliberate private daemon content", true);
            c.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Failed(failure));
            Check(s.OpenContainerStats(A)); await Until(() => s.ContainerStats.Phase == ContainerStatsPhase.Failed);
            int count = c.Opens; await c.Emit("update"); await Until(() => s.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
            Check(c.Opens == count && ReferenceEquals(s.ContainerStats.Failure, failure));
        }
        foreach (int mode in new[] { 0, 1, 2 })
        {
            var finite = new Subscription(A);
            c.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(finite));
            int calls = c.Opens; Check(s.OpenContainerStats(A));
            await Until(() => c.Opens > calls);
            if (mode != 0) await finite.Write(Sample(200));
            var ending = mode == 2 ? new ContainerStatsCompletion(ContainerStatsTermination.Failed,
                new(ContainerStatsFailureKind.MalformedSample, "safe classified stream failure")) : new(ContainerStatsTermination.CleanEnd);
            finite.End(ending);
            await Until(() => s.ContainerStats.Completion == ending);
            Check(s.ContainerStats.Current is null
                && s.ReadContainerStats().Entries.Count(e => e.Measurements is not null) == (mode == 0 ? 0 : 1));
        }
        var delayedDisplay = new Subscription(A);
        c.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(delayedDisplay));
        int previousCalls = c.Opens; Check(s.OpenContainerStats(A)); await Until(() => c.Opens > previousCalls);
        await Tick(t, 6); Check(s.ContainerStats.Phase == ContainerStatsPhase.Stalled);
        await delayedDisplay.Write(Sample(300)); await Until(() => s.ContainerStats.ProcessedSamples == 1);
        t.ElapseWithoutTimers(TimeSpan.FromSeconds(6));
        await delayedDisplay.Write(Sample(301, 200)); await Until(() => s.ContainerStats.ProcessedSamples == 2);
        await Tick(t);
        Check(s.ContainerStats.Current!.CpuPercent.Availability == ContainerStatsAvailability.BaselineRequired,
            "A delayed display loop must not bridge a known raw sample gap");
    }

    public static async Task RecoveryContextResetLimitsAndClose()
    {
        var old = new Connection(); var fresh = new Connection(); var t = new ManualTime();
        var first = new Subscription(A); var second = new Subscription(A);
        old.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(first));
        fresh.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(second));
        var list = new TaskCompletionSource<EngineResourceResult<IReadOnlyList<ContainerSummary>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fresh.List = _ => list.Task;
        await using var s = Session(new(old, fresh), t); await Ready(s); Check(s.OpenContainerStats(A));
        await first.Write(Sample(100)); await Until(() => s.ContainerStats.ProcessedSamples == 1); await Tick(t);
        long previousGeneration = s.ContainerStats.ConnectionGeneration;
        first.End(new(ContainerStatsTermination.Failed, new(ContainerStatsFailureKind.TransportLoss, "lost")));
        await Until(() => old.Disposed && s.ContainerSnapshot.State == ResourceReadState.Loading);
        Check(fresh.Opens == 0 && s.ContainerStats.Current is null);
        list.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(fresh.Rows));
        await Until(() => fresh.Opens == 1);
        await second.Write(Sample(101, 200)); await Until(() => s.ContainerStats.ProcessedSamples == 2); await Tick(t);
        Check(s.ContainerStats.ConnectionGeneration != previousGeneration
            && s.ContainerStats.Current!.CpuPercent.Availability == ContainerStatsAvailability.BaselineRequired
            && s.ReadContainerStats().Entries.Count(e => e.Measurements is not null) == 1);
        // A closed request must not be revived by the owner's subsequent connection recovery.
        await s.CloseContainerStatsAsync(); s.Retry(); await Until(() => fresh.Disposed);
        Check(s.ContainerStats.Phase == ContainerStatsPhase.Closed);
        foreach (bool changed in new[] { false, true })
        {
            var a = new Connection(); var b = new Connection { EngineId = changed ? "other" : "same" };
            a.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Failed(new(ContainerStatsFailureKind.TransportLoss, "lost")));
            b.Open = a.Open;
            await using var bounded = Session(new(a, b), new(), new() { MaximumRecoveryAttempts = 1 });
            await Ready(bounded); Check(bounded.OpenContainerStats(A));
            await Until(() => bounded.ContainerStats.Phase == ContainerStatsPhase.Failed);
            Check(a.Opens == 1 && b.Opens == (changed ? 0 : 1));
        }
        // Clearing selection while recovery is waiting for authoritative data revokes the old view.
        var before = new Connection(); var after = new Connection(); var clock = new ManualTime();
        var active = new Subscription(A);
        before.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(active));
        after.Open = (_, _) => throw new InvalidOperationException("A deselected view must not recover");
        var pending = new TaskCompletionSource<EngineResourceResult<IReadOnlyList<ContainerSummary>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        after.List = _ => pending.Task;
        var connectionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationClient = new Client(before, after) { BeforeConnect = (call, _) => call == 2 ? connectionRelease.Task : Task.CompletedTask };
        await using var navigation = Session(navigationClient, clock); await Ready(navigation);
        Check(navigation.OpenContainerStats(A)); await Until(() => before.Opens == 1);
        active.End(new(ContainerStatsTermination.Failed, new(ContainerStatsFailureKind.TransportLoss, "lost")));
        await Until(() => before.Disposed && navigation.ContainerSnapshot.State == ResourceReadState.Unavailable);
        navigation.SelectContainer(null);
        Check(navigation.ContainerStats.Phase == ContainerStatsPhase.Closed);
        connectionRelease.SetResult();
        pending.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(after.Rows));
        await Until(() => navigation.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(navigation.ContainerStats.Phase == ContainerStatsPhase.Closed && after.Opens == 0);
    }

    public static async Task OpeningDeadlineRetentionAndCoexistence()
    {
        var c = new Connection(); var t = new ManualTime(); var stream = new Subscription(A);
        c.Open = (_, _) => Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(stream));
        await using var s = Session(new(c), t); await Ready(s);
        Check(s.OpenContainerLogs(A)); Check(s.OpenContainerStats(A)); await Until(() => c.Opens == 1);
        for (int second = 0; second < 1800; ++second)
        {
            // Repeated/backward daemon dates cannot control retention. Every raw sample is still calculated.
            await stream.Write(Sample(second % 2 == 0 ? 100 : 99, 100));
            long expected = second + 1;
            await Until(() => s.ContainerStats.ProcessedSamples == expected);
            await Tick(t);
        }
        var history = s.ReadContainerStats(maximumEntries: 1024);
        Check(s.ContainerStats.RetainedEntries <= 301 && history.Entries.Count <= 300
            && s.ContainerStats.EvictedEntries > 1400 && s.ContainerStats.Current!.CpuPercent.Value is null);
        Check(history.Entries.All(e => t.GetUtcNow() - e.ObservedAt <= TimeSpan.FromMinutes(5)));
        Check(c.LogOpens == 1 && !c.Disposed && s.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var command = await s.ExecuteContainerOperationAsync(new(new(s.ContainerSnapshot.ConnectionGeneration, A), ContainerCommand.Start));
        Check(command.Resolution == ContainerOperationResolution.ObservedRequestedState && c.Commands == 1);
        await Tick(t, 301); Check(s.ReadContainerStats().Entries.Count <= 1 && s.ContainerStats.Current is null);
        await s.DisconnectAsync(); Check(stream.Disposed && c.LogDisposed && c.Disposed);
        // The Application deadline fences an opener even if it ignores cancellation.
        var slow = new Connection(); var time = new ManualTime();
        var late = new TaskCompletionSource<ContainerStatsSubscriptionAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.Open = (_, _) => late.Task;
        var session = Session(new(slow), time, new() { MaximumRecoveryAttempts = 0 }, TimeSpan.FromSeconds(2));
        await Ready(session); Check(session.OpenContainerStats(A)); await Until(() => slow.Opens == 1);
        await Tick(time, 3);
        Check(session.ContainerStats.Phase == ContainerStatsPhase.Failed
            && session.ContainerStats.Failure!.Kind == ContainerStatsFailureKind.OpeningDeadline);
        var dispose = session.DisposeAsync().AsTask(); Check(!dispose.IsCompleted);
        var obsolete = new Subscription(A); late.SetResult(ContainerStatsSubscriptionAttempt.Subscribed(obsolete));
        await dispose.WaitAsync(Limit); Check(obsolete.Disposed);
    }

    private static ContainerSummary Row(string id, string state = "running") => new(id, null, null, null, state, null, null, null, null, null, null);
    private static ContainerRuntimeState State(string status = "running", bool running = true)
        => new(status, running, false, false, false, 0, null, DateTimeOffset.UnixEpoch, null, null);
    private static ContainerInspection Inspection(string id, ContainerRuntimeState state, long restarts = 0)
        => new(id, null, null, null, null, state, null, null, null, "{}", restarts);
    private sealed class Client(params Connection[] connections) : IEngineConnectionClient
    {
        private int calls;
        public Func<int, CancellationToken, Task>? BeforeConnect;
        public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref calls);
            if (BeforeConnect is { } wait) await wait(call, cancellationToken);
            return EngineConnectionAttempt.Connected(connections[Math.Min(call - 1, connections.Length - 1)]);
        }
    }
    private sealed class Connection : IEngineConnection, IContainerReader, IContainerStatsSource, IEngineEventSource, IContainerLogSource, IContainerCommands
    {
        public string EngineId = "same";
        public bool Disposed, LogDisposed;
        public int Opens, Inspections, LogOpens, Commands;
        public long Restarts;
        public ContainerRuntimeState State = StatsSessionChecks.State();
        public IReadOnlyList<ContainerSummary> Rows = [Row(A), Row(B)];
        public Func<CancellationToken, Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>>>? List;
        public Func<string, CancellationToken, Task<EngineResourceResult<ContainerInspection>>>? Inspect;
        public Func<string, CancellationToken, Task<ContainerStatsSubscriptionAttempt>> Open = (_, _) => throw new InvalidOperationException();
        private EventStream? events;
        public EngineMetadata Metadata => new(EngineEndpoint.LocalWindows, "fixture", new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 56)),
            "linux", "x64", null, EngineId, null, null, "linux", null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public async ValueTask DisposeAsync() { Disposed = true; if (events is not null) await events.DisposeAsync(); }
        public Task<ContainerStatsSubscriptionAttempt> SubscribeStatsAsync(string containerId, CancellationToken cancellationToken = default)
        { var result = Open(containerId, cancellationToken); Interlocked.Increment(ref Opens); return result; }
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
            => List?.Invoke(cancellationToken) ?? Task.FromResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(Rows));
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Inspections); return Inspect?.Invoke(containerId, cancellationToken) ?? Task.FromResult(EngineResourceResult<ContainerInspection>.Success(Inspection(containerId, State, Restarts))); }
        public Task Emit(string action) => events!.Write(new("container", action, A, null, null, new(1, 1_000_000_001)));
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        { events = new(cancellationToken); return Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(events)); }
        public Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request, CancellationToken cancellationToken = default)
        { ++LogOpens; return Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(new Logs(this, request.ContainerId))); }
        public Task<ContainerCommandResult> StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
        { ++Commands; return Task.FromResult(new ContainerCommandResult(ContainerCommand.Start, ContainerCommandDisposition.Acknowledged)); }
        public Task<ContainerCommandResult> StopContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContainerCommandResult> RestartContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContainerCommandResult> DeleteContainerAsync(string containerId, bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class EventStream : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(8);
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration registration;
        public EventStream(CancellationToken token) => registration = token.Register(() => { channel.Writer.TryComplete(); completion.TrySetResult(null); });
        public Task Write(EngineEvent value) => channel.Writer.WriteAsync(value).AsTask();
        public ChannelReader<EngineEvent> Events => channel.Reader;
        public Task<EngineEventFailure?> Completion => completion.Task;
        public ValueTask DisposeAsync() { registration.Dispose(); channel.Writer.TryComplete(); completion.TrySetResult(null); return ValueTask.CompletedTask; }
    }
    private sealed class Subscription(string id) : IContainerStatsSubscription
    {
        private readonly Channel<ContainerStatsSample> channel = Channel.CreateBounded<ContainerStatsSample>(32);
        private readonly TaskCompletionSource<ContainerStatsCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public TaskCompletionSource? DisposeRelease;
        public string ContainerId => id;
        public ChannelReader<ContainerStatsSample> Samples => channel.Reader;
        public Task<ContainerStatsCompletion> Completion => completion.Task;
        public async Task Write(ContainerStatsSample sample)
        { if (!Disposed) { try { await channel.Writer.WriteAsync(sample); } catch (ChannelClosedException) { } } }
        public void End(ContainerStatsCompletion? ending = null)
        { channel.Writer.TryComplete(); completion.TrySetResult(ending ?? new(ContainerStatsTermination.CleanEnd)); }
        public async ValueTask DisposeAsync()
        { Disposed = true; End(new(ContainerStatsTermination.Canceled)); if (DisposeRelease is { } release) await release.Task; }
    }
    private sealed class Logs(Connection owner, string id) : IContainerLogSubscription
    {
        private readonly Channel<ContainerLogOutput> channel = Channel.CreateBounded<ContainerLogOutput>(1);
        public string ContainerId => id;
        public bool IsTty => false;
        public ChannelReader<ContainerLogOutput> Output => channel.Reader;
        public Task<ContainerLogCompletion> Completion => Task.FromResult(new ContainerLogCompletion(ContainerLogTermination.Canceled));
        public ValueTask DisposeAsync() { owner.LogDisposed = true; channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object gate = new();
        private long ticks;
        private readonly HashSet<Timer> timers = [];
        public int ActiveTimers { get { lock (gate) return timers.Count(t => t.Due != long.MaxValue); } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate) { var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        }
        public void Advance(TimeSpan duration)
        {
            List<Timer> due;
            lock (gate)
            {
                ticks += duration.Ticks;
                due = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in due) timer.Due = timer.Period > TimeSpan.Zero ? ticks + timer.Period.Ticks : long.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        public void ElapseWithoutTimers(TimeSpan duration) { lock (gate) ticks += duration.Ticks; }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            public TimeSpan Period;
            public TimerCallback Callback => callback;
            public object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner.gate) { Due = dueTime < TimeSpan.Zero ? long.MaxValue : owner.ticks + dueTime.Ticks; Period = period; return true; } }
            public void Dispose() { lock (owner.gate) { Due = long.MaxValue; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
