using System.Collections.Concurrent;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class ReconciliationChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Batch = TimeSpan.FromMilliseconds(17), EventRetry = TimeSpan.FromSeconds(7), Cooldown = TimeSpan.FromSeconds(19);
    private const string A = "container-a", B = "container-b", C = "container-c";
    private static ContainerReconciliationOptions Tuning(int retries = 2) => new()
    { BatchInterval = Batch, RetryDelay = EventRetry, RecoveryCooldown = Cooldown, MaximumRecoveryAttempts = retries,
        ReplayOverlap = TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1) };
    private static ContainerSummary Row(string id, string state = "exited", string? name = null) => new(id,
        [name ?? "/authoritative-name"], "authoritative-image", null, state, state, null, null, null, null, new("healthy", 0));
    private static ContainerInspection Detail(string id, string raw = "{\"actual\":true}") => new(id, "/authoritative-name",
        null, null, null, null, null, null, null, raw);
    private static EngineEvent Event(string? id = A, string action = "start", long? nanos = 100_123_456_789)
        => new("container", action, id, new Dictionary<string, string> { ["name"] = "never-an-authoritative-name" },
            null, new(100, nanos));
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static DesktopSession Session(Client client, Clock clock, int retries = 2, ConnectionRecoveryOptions? options = null)
        => new(client, options, clock, reconciliation: Tuning(retries));
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
    private static async Task<Subscription> Open(Connection connection)
    {
        var opening = await connection.NextOpen();
        var subscription = new Subscription();
        opening.Succeed(subscription);
        return subscription;
    }
    private static async Task<Subscription> Load(DesktopSession session, Connection connection, params ContainerSummary[] rows)
    {
        var subscription = await Open(connection);
        (await connection.NextList()).Succeed(rows);
        await State(session, s => s.State == ResourceReadState.Ready);
        return subscription;
    }
    private static async Task<ListCall> EventList(Connection connection, Clock clock)
    { (await clock.Next(Batch)).Release(); return await connection.NextList(); }

    public static async Task SnapshotBoundariesAndAuthority()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        session.Start();
        var opening = await engine.NextOpen();
        session.RefreshContainers(); // Even deliberate refresh waits for initial observation setup.
        Check(engine.ListCalls == 0, "Event observation must open before the initial authoritative snapshot");
        var events = new Subscription();
        await events.Emit(Event(A, "create")); // Already buffered when observation opens.
        opening.Succeed(events);
        var initial = await engine.NextList();
        await events.Emit(Event(B, "create"));
        await events.Barrier();
        Check(!initial.Token.IsCancellationRequested && session.ContainerSnapshot.Containers.Count == 0,
            "Events during the initial snapshot neither cancel it nor fabricate rows");
        initial.Succeed(); // Deliberately stale snapshot: both creations must be reconciled afterward.
        var reconciled = await EventList(engine, clock);
        reconciled.Succeed(Row(A), Row(B));
        await State(session, s => s.Containers.Count == 2);
        Check(session.ContainerSnapshot.Containers.All(r => r.Names![0] == "/authoritative-name"), "Actor attributes may not patch resource properties");
        foreach (string action in new[] { "start", "stop", "rename", "health_status: unhealthy", "future-action" })
            await events.Emit(Event(null, action));
        await events.Barrier();
        var after = await EventList(engine, clock);
        after.Succeed(Row(B, "running", "/actual-rename"), Row(C));
        await State(session, s => s.Containers.Count == 2 && s.Containers[0].State == "running");
        Check(session.ContainerSnapshot.Containers[0].Names![0] == "/actual-rename" && engine.ListCalls == 3 && engine.MaximumConcurrent == 1,
            "Unknown actions and missing identities must safely reconcile with bounded serialized reads");
    }

    public static async Task BurstProgressAndDetails()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        session.Start(); var events = await Load(session, engine, Row(A));
        session.SelectContainer(A); var inspection = await engine.NextInspection();
        for (int i = 0; i < 3000; ++i) await events.Emit(Event(A, "duplicate", 100_123_456_789));
        await events.Barrier();
        Check(!inspection.Token.IsCancellationRequested && engine.InspectionCalls == 1 && engine.ListCalls == 1,
            "Sustained duplicate timestamps must not cancel reads or accumulate per-event requests");
        inspection.Succeed(Detail(A, "{\"stale\":true}"));
        var list = await EventList(engine, clock);
        Check(session.ContainerSnapshot.Inspection is null, "Inspection invalidated during I/O must not publish stale JSON");
        for (int i = 0; i < 3000; ++i) await events.Emit(Event(A, "duplicate", 100_123_456_789));
        await events.Barrier();
        Check(!list.Token.IsCancellationRequested && engine.ListCalls == 2, "Events cannot starve a list by repeatedly superseding it");
        list.Succeed(Row(A, "running"));
        var fairDetail = await engine.NextInspection();
        Check(engine.ListCalls == 2 && fairDetail.Identity == A, "Requested detail receives a turn while list invalidations continue");
        fairDetail.Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var finalList = await EventList(engine, clock); finalList.Succeed(Row(A, "running"));
        await State(session, s => s.State == ResourceReadState.Ready && s.Containers[0].State == "running");
        Check(engine.ListCalls == 3 && engine.InspectionCalls == 2 && engine.MaximumConcurrent == 1 && events.MaximumBuffered <= 32,
            "Thousands of events retain only bounded delivery and pending list/detail work");
    }

    public static async Task DeletionAndCloseRaces()
    {
        var engine = new Connection(honorReadCancellation: false); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        session.Start(); var events = await Load(session, engine, Row(A), Row(B));
        session.SelectContainer(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.RefreshContainerInspection(); var deleted = await engine.NextInspection();
        await events.Emit(Event(A, "destroy")); await events.Barrier();
        Check(!deleted.Token.IsCancellationRequested && session.ContainerSnapshot.Inspection is null,
            "Deletion observation invalidates exposed detail content without canceling its read");
        deleted.Succeed(Detail(A, "{\"late\":true}"));
        var absence = await EventList(engine, clock); absence.Succeed(Row(B));
        await State(session, s => s.SelectedContainerId is null);
        Check(session.ContainerSnapshot.Inspection is null && engine.InspectionCalls == 2, "Authoritative absence clears selection and pending detail rereads");

        session.SelectContainer(B); var close = await engine.NextInspection();
        await events.Emit(Event(B)); await events.Barrier();
        session.ClearContainerInspection();
        Check(close.Token.IsCancellationRequested, "Deliberate detail close still cancels its read");
        close.Succeed(Detail(B));
        var closedList = await EventList(engine, clock); closedList.Succeed(Row(B));
        await State(session, s => s.State == ResourceReadState.Ready && s.InspectionState == ResourceReadState.NotRequested);
        await events.Emit(Event(null, "future-action")); await events.Barrier();
        var missingIdentity = await EventList(engine, clock); missingIdentity.Succeed(Row(B));
        Check(engine.InspectionCalls == 3 && session.ContainerSnapshot.Inspection is null,
            "Background synchronization must not reopen a closed detail, even with missing event identity");
    }

    public static async Task ReconnectOverlapAndFullResync()
    {
        var old = new Connection(); var fresh = new Connection(); var clock = new Clock(); var client = new Client(old, fresh);
        await using var session = Session(client, clock);
        session.Start(); var events = await Load(session, old, Row(A));
        await events.Emit(Event(A, nanos: 100_123_456_789));
        await events.Emit(Event(A, nanos: 100_123_456_789));
        await events.Emit(Event(A, nanos: 90_000_000_001));
        await events.Emit(new("image", "ignored-resource", null, null, null, new(-1, -1)));
        await events.Barrier();
        var buffered = await clock.Next(Batch);
        long generation = session.ContainerSnapshot.ConnectionGeneration;
        var failure = new EngineEventFailure(EngineEventFailureKind.TransportLoss, "Event pipe continuity lost", 503, "PRIVATE original daemon message");
        events.End(failure);
        await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
        Check(buffered.Token.IsCancellationRequested && events.DisposeCount == 1 && old.DisposeCount == 1
            && session.ContainerSnapshot.Containers.Count == 0 && session.Snapshot.Failure?.DaemonMessage == failure.DaemonMessage,
            "Transport event loss cancels buffered reconciliation, disposes observation, and invalidates Engine state");
        (await clock.Next(TimeSpan.FromSeconds(3))).Release();
        var opening = await fresh.NextOpen();
        Check(opening.Since == new EngineEventCursor(98, 123_456_689), "Reconnect overlap preserves exact nanoseconds and ignores duplicates/older/invalid timestamps");
        var resumed = new Subscription(); await resumed.Emit(Event(C, "create")); opening.Succeed(resumed);
        var full = await fresh.NextList();
        Check(session.ContainerSnapshot.ConnectionGeneration > generation && session.ContainerSnapshot.Containers.Count == 0,
            "Reconnect always starts a new empty authoritative session rather than trusting replay");
        await resumed.Barrier(); full.Succeed(Row(B));
        var concurrent = await EventList(fresh, clock); concurrent.Succeed(Row(B), Row(C));
        await State(session, s => s.Containers.Count == 2);
        Check(session.ContainerSnapshot.Containers.All(r => r.Id != A) && fresh.ListCalls == 2 && client.Calls == 2,
            "A fresh full snapshot replaces old rows and concurrent replay is reconciled against it");
    }

    public static async Task FailureDiagnosticsAndBoundedRecovery()
    {
        foreach (var kind in Enum.GetValues<EngineEventFailureKind>().Where(k => k != EngineEventFailureKind.TransportLoss))
        {
            var engine = new Connection(); var clock = new Clock();
            await using var session = Session(new(engine), clock, retries: 0);
            session.Start(); var opening = await engine.NextOpen();
            var failure = new EngineEventFailure(kind, "Classified observation failure", 403, "PRIVATE daemon body");
            opening.Fail(failure); (await engine.NextList()).Succeed(Row(A));
            await State(session, s => s.State == ResourceReadState.Ready);
            Check(ReferenceEquals(session.Snapshot.EventFailure, failure) && !session.Snapshot.EventRecoveryPending
                && session.EngineState == EngineConnectionState.Connected, "Opening failure remains classified without disabling browsing");
            session.RefreshContainers(); (await engine.NextList()).Succeed(Row(B));
            await State(session, s => s.Containers.Single().Id == B);
            Check(engine.OpenCalls == 1, "Disabled observation retries must not become a tight loop");
        }
        var retryEngine = new Connection(); var retryClock = new Clock();
        await using var recovering = Session(new(retryEngine), retryClock, retries: 1);
        recovering.Start(); var stream = await Load(recovering, retryEngine, Row(A));
        stream.End(null); // Null completion without session cancellation is still an unexpected ending.
        await ConnectionState(recovering, s => s.EventFailure?.Kind == EngineEventFailureKind.UnexpectedEnd);
        var firstRetry = await retryClock.Next(EventRetry);
        Check(stream.DisposeCount == 1 && retryEngine.OpenCalls == 1, "A completed subscription is disposed before delayed replacement");
        var refresh = await EventList(retryEngine, retryClock); refresh.Succeed(Row(B));
        firstRetry.Release();
        (await retryEngine.NextOpen()).Fail(new(EngineEventFailureKind.MalformedEvent, "Malformed observation", 500, "PRIVATE body"));
        await ConnectionState(recovering, s => s.EventFailure?.Kind == EngineEventFailureKind.MalformedEvent);
        var cooldown = await retryClock.Next(Cooldown);
        Check(retryEngine.OpenCalls == 2 && recovering.Snapshot.EventRecoveryPending, "Repeated failures enter a configured bounded cooldown");
        var duringFailure = await EventList(retryEngine, retryClock); duringFailure.Succeed(Row(B));
        cooldown.Release(); await Open(retryEngine);
        await ConnectionState(recovering, s => s.EventFailure is null);
        (await EventList(retryEngine, retryClock)).Succeed(Row(C));
        await State(recovering, s => s.Containers.Single().Id == C);
    }

    public static async Task EngineIdentityAndOpeningTransportFailure()
    {
        var old = new Connection(); var changed = new Connection(engineId: "different-engine"); var clock = new Clock();
        await using var session = Session(new(old, changed), clock);
        session.Start(); var events = await Load(session, old, Row(A));
        await events.Emit(Event()); await events.Barrier();
        var batch = await clock.Next(Batch);
        session.Retry();
        var next = await changed.NextOpen();
        Check(batch.Token.IsCancellationRequested && next.Since is null && events.DisposeCount == 1,
            "A different selected Engine identity must not inherit the previous replay cursor");
        next.Fail(new(EngineEventFailureKind.TransportLoss, "Opening continuity lost"));
        await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
        Check(changed.ListCalls == 0 && changed.DisposeCount == 1, "Opening transport failure requires renewed negotiation before any snapshot");
    }

    public static async Task LateOpeningReadsAndShutdown()
    {
        var old = new Connection(honorOpeningCancellation: false); var fresh = new Connection(honorReadCancellation: false);
        var clock = new Clock(); var client = new Client(old, fresh);
        var session = Session(client, clock); session.Start();
        var lateOpening = await old.NextOpen(); session.Retry();
        Check(lateOpening.Token.IsCancellationRequested && client.Calls == 1 && old.DisposeCount == 0,
            "Replacement cancels and joins in-progress opening before disposing its connection");
        var lateStream = new Subscription(); await lateStream.Emit(Event()); lateOpening.Succeed(lateStream);
        var currentOpening = await fresh.NextOpen();
        Check(lateStream.DisposeCount == 1 && old.DisposeCount == 1 && old.ListCalls == 0 && currentOpening.Since is null,
            "Late subscriptions are owned and disposed without observing or publishing their data");
        currentOpening.Succeed(new Subscription());
        var lateList = await fresh.NextList();
        var ending = session.DisposeAsync().AsTask();
        Check(lateList.Token.IsCancellationRequested && !ending.IsCompleted && session.ContainerSnapshot.State == ResourceReadState.Unavailable,
            "Shutdown cancels observation and joins readers that ignore cancellation");
        lateList.Succeed(Row(A)); await ending.WaitAsync(Limit); await session.DisposeAsync();
        Check(fresh.DisposeCount == 1 && !fresh.DisposedWithRead && !fresh.DisposedWithSubscription
            && session.ContainerSnapshot.Containers.Count == 0, "Late read results cannot publish after shutdown; connection disposal follows joined work");

        var timed = new Connection(honorOpeningCancellation: false); var timer = new Clock();
        await using var timeout = Session(new(timed), timer, retries: 0, options: new() { OperationTimeout = TimeSpan.FromMilliseconds(100) });
        timeout.Start(); var deadline = await timed.NextOpen(); await deadline.Canceled.Task.WaitAsync(Limit);
        var timedStream = new Subscription(); deadline.Succeed(timedStream);
        (await timed.NextList()).Succeed(Row(B));
        await State(timeout, s => s.State == ResourceReadState.Ready);
        Check(timedStream.DisposeCount == 1 && timeout.Snapshot.EventFailure?.Kind == EngineEventFailureKind.OpeningDeadline,
            "A late opening after deadline is disposed and yields a classified observation diagnostic");
    }

    public static async Task EventLossJoinsOutstandingReads()
    {
        foreach (bool inspect in new[] { false, true })
        {
            var engine = new Connection(honorReadCancellation: false); var clock = new Clock();
            await using var session = Session(new(engine), clock, options: new() { MaximumRecoveryAttempts = 0 });
            session.Start(); var events = await Open(engine); var list = await engine.NextList();
            InspectionCall? detail = null;
            if (inspect)
            {
                list.Succeed(Row(A)); await State(session, s => s.State == ResourceReadState.Ready);
                session.SelectContainer(A); detail = await engine.NextInspection();
            }
            events.End(new(EngineEventFailureKind.TransportLoss, "Event transport replaced"));
            await State(session, s => s.State == ResourceReadState.Unavailable);
            bool canceledBeforeDisposal = (detail?.Token ?? list.Token).IsCancellationRequested && engine.DisposeCount == 0;
            if (detail is not null) detail.Succeed(Detail(A)); else list.Succeed(Row(A));
            await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
            Check(canceledBeforeDisposal && engine.DisposeCount == 1 && !engine.DisposedWithRead
                && !engine.DisposedWithSubscription && session.ContainerSnapshot.Containers.Count == 0 && session.ContainerSnapshot.Inspection is null,
                "Event continuity loss cancels and joins outstanding reads; late successful results cannot publish");
        }
    }

    public static async Task PresentationAutomaticInvalidation()
    {
        var engine = new Connection(); var clock = new Clock(); var ui = new Dispatcher();
        await using var session = Session(new(engine), clock);
        using var view = new ContainerBrowserView(session, ui.Post);
        view.Attach(); session.Start(); var events = await Load(session, engine, Row(A)); await ui.Settle(view);
        ContainerInspection Diagnostics(bool oom, long restarts, string raw) => Detail(A, raw) with
        { State = new("running", true, false, false, false, 0, null, null, null, new("healthy", 0), oom), RestartCount = restarts };
        view.Select(A); view.OpenSelected(); (await engine.NextInspection()).Succeed(Diagnostics(true, 7, "{\"private\":\"Inspect value\"}"));
        await State(session, s => s.InspectionState == ResourceReadState.Ready); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
        Check(view.DetailOpen && view.Inspect.Rows.Count > 0 && view.Overview.Contains("OOMKilled: yes")
            && view.Overview.Contains("Restart count: 7"), "Existing presentation loaded actual authoritative diagnostics and Inspect");
        await events.Emit(Event(A, "health_status: unhealthy")); await events.Barrier(); ui.Pump();
        Check(view.DetailOpen && view.Inspect.Rows.Count == 0 && view.Overview == "", "Automatic observation immediately clears obsolete Inspect content");
        var refresh = await EventList(engine, clock); refresh.Succeed(Row(A, "running"));
        (await engine.NextInspection()).Succeed(Diagnostics(false, 0, "{\"updated\":true}"));
        await State(session, s => s.InspectionState == ResourceReadState.Ready); await ui.Settle(view);
        Check(view.Rows.Single().State == "running" && view.Inspect.Rows.Any(row => row.Text.Contains("updated"))
            && view.Overview.Contains("OOMKilled: no") && view.Overview.Contains("Restart count: 0"),
            "Automatic authoritative updates reach the existing presentation without user refresh");
        await events.Emit(Event(A, "destroy")); await events.Barrier();
        (await EventList(engine, clock)).Succeed();
        await State(session, s => s.SelectedContainerId is null); await ui.Settle(view);
        Check(view.SelectedId is null && !view.DetailOpen && view.Inspect.Rows.Count == 0 && view.Rows.Count == 0 && view.Overview == "",
            "External deletion clears row selection, detail and Inspect presentation");
    }

    private sealed class Client(params Connection[] connections) : IEngineConnectionClient
    {
        public int Calls;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(EngineConnectionAttempt.Connected(connections[Interlocked.Increment(ref Calls) - 1])); }
    }
    private sealed class Connection(bool honorReadCancellation = true, bool honorOpeningCancellation = true, string engineId = "same-engine")
        : IEngineConnection, IContainerReader, IEngineEventSource
    {
        private readonly Channel<OpenCall> openings = Channel.CreateUnbounded<OpenCall>();
        private readonly Channel<ListCall> lists = Channel.CreateUnbounded<ListCall>();
        private readonly Channel<InspectionCall> inspections = Channel.CreateUnbounded<InspectionCall>();
        private Subscription? subscription;
        private int active;
        public int OpenCalls, ListCalls, InspectionCalls, MaximumConcurrent, DisposeCount;
        public bool DisposedWithRead, DisposedWithSubscription;
        public EngineMetadata Metadata { get; } = new(EngineEndpoint.LocalWindows, "simulated",
            new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 40)), null, null, null, engineId,
            null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public async Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        {
            Check(subscription is null || subscription.DisposeCount == 1, "Undisposed subscription cannot be replaced");
            Interlocked.Increment(ref OpenCalls);
            var call = new OpenCall(since, cancellationToken); openings.Writer.TryWrite(call);
            var result = await (honorOpeningCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task);
            subscription = (Subscription?)result.Subscription;
            return result;
        }
        private void Enter()
        { Check(DisposeCount == 0, "No read after connection disposal"); MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref active)); }
        public async Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref ListCalls); var call = new ListCall(cancellationToken); lists.Writer.TryWrite(call);
            try { return await (honorReadCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string id, CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref InspectionCalls); var call = new InspectionCall(id, cancellationToken); inspections.Writer.TryWrite(call);
            try { return await (honorReadCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            finally { Interlocked.Decrement(ref active); }
        }
        public ValueTask DisposeAsync()
        { DisposedWithRead = active != 0; DisposedWithSubscription = subscription is { DisposeCount: 0 }; Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
        public async Task<OpenCall> NextOpen() => await openings.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public async Task<ListCall> NextList() => await lists.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public async Task<InspectionCall> NextInspection() => await inspections.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class OpenCall
    {
        public OpenCall(EngineEventCursor? since, CancellationToken token)
        { Since = since; Token = token; token.Register(() => Canceled.TrySetResult()); }
        public EngineEventCursor? Since { get; }
        public CancellationToken Token { get; }
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EngineEventSubscriptionAttempt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(Subscription subscription) => Completion.SetResult(EngineEventSubscriptionAttempt.Subscribed(subscription));
        public void Fail(EngineEventFailure failure) => Completion.SetResult(EngineEventSubscriptionAttempt.Failed(failure));
    }
    private sealed class ListCall(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<IReadOnlyList<ContainerSummary>>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(params ContainerSummary[] rows) => Completion.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(rows));
    }
    private sealed class InspectionCall(string id, CancellationToken token)
    {
        public string Identity { get; } = id;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<ContainerInspection>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(ContainerInspection detail) => Completion.SetResult(EngineResourceResult<ContainerInspection>.Success(detail));
    }
    private sealed class Subscription : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(32)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private TaskCompletionSource progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int consumed, sent;
        public int DisposeCount, MaximumBuffered;
        public Subscription() => Events = new ObservedReader(this);
        public ChannelReader<EngineEvent> Events { get; }
        public Task<EngineEventFailure?> Completion => completion.Task;
        public async Task Emit(EngineEvent observation)
        {
            await channel.Writer.WriteAsync(observation).AsTask().WaitAsync(Limit);
            ++sent; MaximumBuffered = Math.Max(MaximumBuffered, channel.Reader.Count);
        }
        public async Task Barrier()
        {
            // Reading this harmless marker implies all preceding container observations were processed.
            await Emit(new("image", "barrier", null, null, null, new(null, null)));
            int target = sent;
            while (true)
            {
                Task pending;
                lock (gate) { if (consumed >= target) return; pending = progress.Task; }
                await pending.WaitAsync(Limit);
            }
        }
        public void End(EngineEventFailure? failure) { completion.TrySetResult(failure); channel.Writer.TryComplete(); }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref DisposeCount, 1) == 0)
            { End(null); while (channel.Reader.TryRead(out _)) { } }
            return ValueTask.CompletedTask;
        }
        private sealed class ObservedReader(Subscription owner) : ChannelReader<EngineEvent>
        {
            public override bool TryRead(out EngineEvent item)
            {
                if (!owner.channel.Reader.TryRead(out item!)) return false;
                lock (owner.gate)
                {
                    ++owner.consumed; owner.progress.TrySetResult();
                    owner.progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                return true;
            }
            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => owner.channel.Reader.WaitToReadAsync(cancellationToken);
        }
    }
    private sealed class Clock : IConnectionDelay
    {
        private readonly ConcurrentDictionary<TimeSpan, Channel<Wait>> waits = new();
        public async Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            var wait = new Wait(token); waits.GetOrAdd(delay, _ => Channel.CreateUnbounded<Wait>()).Writer.TryWrite(wait);
            await wait.Completion.Task.WaitAsync(token);
        }
        public async Task<Wait> Next(TimeSpan delay) => await waits.GetOrAdd(delay, _ => Channel.CreateUnbounded<Wait>()).Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class Wait(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Completion.TrySetResult();
    }
    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> work = new();
        public bool Post(Action action) { work.Enqueue(action); return true; }
        public void Pump() { while (work.TryDequeue(out var action)) action(); }
        public async Task Settle(ContainerBrowserView view)
        {
            using var timeout = new CancellationTokenSource(Limit);
            while (true)
            {
                Pump(); if (!view.Filtering && !view.Inspect.Busy && work.IsEmpty) return;
                await Task.Delay(1, timeout.Token);
            }
        }
    }
}
