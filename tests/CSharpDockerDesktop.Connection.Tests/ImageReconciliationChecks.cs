using System.Collections.Concurrent;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class ImageBrowsingChecks
{
    private static readonly TimeSpan ImageBatch = TimeSpan.FromMilliseconds(17), ImageEventRetry = TimeSpan.FromSeconds(7);
    private static DesktopSession EventSession(IEngineConnectionClient client, ImageEventClock clock, ConnectionRecoveryOptions? options = null)
        => new(client, options, clock, reconciliation: new() { BatchInterval = ImageBatch,
            RetryDelay = ImageEventRetry, ReplayOverlap = TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1) });
    private static EngineEvent ImageEvent(string? id = null, string action = "tag", long? nanos = 100_123_456_789)
        => new("image", action, id, new Dictionary<string, string> { ["name"] = "private-event-tag", ["secret"] = "private-attribute" },
            null, new(100, nanos));
    private static async Task<ListCall> EventImageList(ControlledConnection engine, ImageEventClock clock)
    { (await clock.Next(ImageBatch)).Release(); return await engine.NextList(); }
    private static async Task<ImageObservation> OpenImageEvents(EventImageConnection engine)
    {
        var opening = await engine.NextOpening();
        var events = new ImageObservation(); opening.Succeed(events); return events;
    }

    public static async Task ImageSnapshotObservationBoundaries()
    {
        var engine = new EventImageConnection("startup"); var clock = new ImageEventClock();
        var client = new ImageGatedClient();
        await using var session = EventSession(client, clock);
        var events = new ImageObservation();
        int bridged = 0;
        // Force the shared coordinator to consume observations before BeginImageReads runs.
        session.ContainersChanged += (_, _) =>
        {
            if (session.ContainerSnapshot.ConnectionGeneration == 0 || Interlocked.Exchange(ref bridged, 1) != 0) return;
            var opening = engine.NextOpening().GetAwaiter().GetResult();
            opening.Succeed(events);
            events.Emit(ImageEvent(A, "untag")).GetAwaiter().GetResult();
            events.Barrier().GetAwaiter().GetResult();
            Check(engine.ListCalls == 0, "Observation startup bridge precedes image coordinator creation");
        };
        session.Start();
        // Finish Start's own notification before allowing connection-owned startup notifications.
        client.Ready.SetResult(EngineConnectionAttempt.Connected(engine));
        var initial = await engine.NextList();
        await events.Emit(ImageEvent(B, "delete")); await events.Barrier();
        Check(!initial.Token.IsCancellationRequested && engine.ListCalls == 1);
        initial.Succeed(A, A);
        await State(session, s => s.State == ResourceReadState.Ready && s.Images.Count == 1);
        var after = await EventImageList(engine, clock);
        await events.Emit(ImageEvent(C, "tag")); await events.Barrier();
        after.Succeed(B);
        await State(session, s => s.Images.Single().Id == B);
        (await EventImageList(engine, clock)).Succeed(C);
        await State(session, s => s.Images.Single().Id == C);
        int calls = engine.ListCalls;
        await events.Emit(new("future-resource", "delete", C, null, null, new(101, null))); await events.Barrier();
        Check(engine.ListCalls == calls && engine.OpenCalls == 1 && session.ImageSnapshot.Images.Single().Id == C,
            "Only authoritative lists replace rows; unknown resource types retain cursor-only behavior");
    }

    public static async Task ImageSelectedObservationFences()
    {
        var engine = new EventImageConnection("detail", honorReadCancellation: false); var clock = new ImageEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenImageEvents(engine); await Load(session, engine, A, B);
        session.SelectImage(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        await events.Emit(ImageEvent(B, "tag")); await events.Barrier();
        Check(session.ImageSnapshot.Inspection?.RawJson == Raw, "Unrelated identity keeps selected content");
        (await EventImageList(engine, clock)).Succeed(A, B);
        session.RefreshImageInspection(); var stale = await engine.NextInspection();
        await events.Emit(ImageEvent(A, "untag")); await events.Barrier();
        Check(session.ImageSnapshot.Inspection is null && session.ImageSnapshot.InspectionState == ResourceReadState.Loading);
        stale.Succeed(Detail(A, "{\"stale\":true}"));
        var list = await EventImageList(engine, clock); list.Succeed(A, B);
        var replacement = await engine.NextInspection();
        Check(session.ImageSnapshot.Inspection is null && replacement.Identity == A);
        replacement.Succeed(Detail(A)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(session.ImageSnapshot.Inspection!.RawJson == Raw && session.ImageSnapshot.SelectedImageId == A);
        session.ClearImageInspection();
        await events.Emit(ImageEvent(null, "unknown-action")); await events.Barrier();
        Check(session.ImageSnapshot.InspectionState == ResourceReadState.NotRequested, "Missing identity cannot reopen explicitly cleared detail");
        (await EventImageList(engine, clock)).Succeed(A, B);
        session.RefreshImageInspection(); var removed = await engine.NextInspection();
        await events.Emit(ImageEvent(A, "delete")); await events.Barrier();
        removed.Fail(Failure(EngineResourceFailureKind.NotFound));
        (await EventImageList(engine, clock)).Succeed(A, B);
        Check(session.ImageSnapshot.SelectedImageId == A, "Stale NotFound is fenced like a stale success");
        (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.NotFound));
        await State(session, s => s.SelectedImageId is null);
        Check(session.ImageSnapshot.Images.Single().Id == B && session.ImageSnapshot.Inspection is null);
        session.SelectImage(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        await events.Emit(ImageEvent(B, "delete")); await events.Barrier();
        (await EventImageList(engine, clock)).Succeed();
        await State(session, s => s.SelectedImageId is null);
        Check(session.ImageSnapshot.Images.Count == 0 && session.ImageSnapshot.Inspection is null);
    }

    public static async Task ImageBurstCoalescingAndFairness()
    {
        var engine = new EventImageConnection("burst", honorReadCancellation: false); var clock = new ImageEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenImageEvents(engine); await Load(session, engine, A);
        session.SelectImage(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        int notifications = 0; session.ImagesChanged += (_, _) => Interlocked.Increment(ref notifications);
        for (int index = 0; index < 2000; index++) await events.Emit(ImageEvent(A, index % 2 == 0 ? "tag" : "untag"));
        await events.Barrier();
        Check(engine.ListCalls == 1 && notifications == 1, "Duplicate same-timestamp burst coalesces pending list/detail and notifications");
        var list = await EventImageList(engine, clock);
        for (int index = 0; index < 1000; index++) await events.Emit(ImageEvent(A, "delete"));
        await events.Barrier();
        Check(!list.Token.IsCancellationRequested && engine.ListCalls == 2);
        list.Succeed(A, B);
        var detail = await engine.NextInspection();
        Check(engine.ListCalls == 2 && detail.Identity == A && session.ImageSnapshot.Images.Count == 2,
            "Sustained invalidation allows useful list publication and a requested detail turn");
        await events.Emit(ImageEvent(null, "anything")); await events.Barrier();
        detail.Succeed(Detail(A, "{\"stale\":true}"));
        var next = await EventImageList(engine, clock); next.Succeed(A, B);
        (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(engine.ListCalls == 3 && engine.InspectionCalls == 3 && engine.MaximumConcurrent == 1
            && events.MaximumBuffered <= 32 && session.ImageSnapshot.Inspection!.RawJson == Raw);
        await events.Emit(ImageEvent(A)); await events.Barrier();
        var waiting = await clock.Next(ImageBatch);
        session.RefreshImages(); waiting.Release();
        (await engine.NextList()).Succeed(A);
        (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
    }

    public static async Task ImageObservationFailureAndReopening()
    {
        var engine = new EventImageConnection("observation"); var clock = new ImageEventClock(); var client = new ControlledClient(engine);
        await using var session = EventSession(client, clock);
        session.Start();
        var failure = new EngineEventFailure(EngineEventFailureKind.AccessDenied, "Safe observation failure", 403, "private daemon response");
        (await engine.NextOpening()).Fail(failure);
        await Load(session, engine, A);
        Check(session.Snapshot.EventFailure == failure && session.Snapshot.EventRecoveryPending && client.Calls == 1);
        session.SelectImage(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.RefreshImages(); await Load(session, engine, A, B);
        (await clock.Next(ImageEventRetry)).Release();
        var events = await OpenImageEvents(engine);
        var reopenedDetail = await engine.NextInspection();
        Check(session.ImageSnapshot.Inspection is null && client.Calls == 1 && session.Snapshot.EventFailure is null);
        reopenedDetail.Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var fresh = await EventImageList(engine, clock);
        await events.Emit(ImageEvent(A, "untag")); await events.Barrier();
        fresh.Succeed(A, B); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        (await EventImageList(engine, clock)).Succeed(B);
        await State(session, s => s.SelectedImageId is null);
        events.End(new(EngineEventFailureKind.MalformedEvent, "Safe malformed-event failure"));
        await ConnectionState(session, s => s.EventFailure?.Kind == EngineEventFailureKind.MalformedEvent);
        (await EventImageList(engine, clock)).Succeed(B);
        await State(session, s => s.Images.Count == 1);
        session.RefreshImages(); await Load(session, engine, B);
        Check(session.EngineState == EngineConnectionState.Connected && client.Calls == 1);
    }

    public static async Task ImageObservationOpeningFallbackAndJoin()
    {
        var engine = new EventImageConnection("late-opening", honorOpeningCancellation: false); var clock = new ImageEventClock();
        var session = EventSession(new ControlledClient(engine), clock, new() { OperationTimeout = TimeSpan.FromMilliseconds(120), MaximumRecoveryAttempts = 0 });
        session.Start(); var opening = await engine.NextOpening();
        Check(engine.ListCalls == 0, "Image snapshot waits for observation establishment");
        var initial = await engine.NextList();
        Check(opening.Token.IsCancellationRequested, "Opening deadline provides bounded snapshot fallback even for a non-cooperative opener");
        initial.Succeed(A); await State(session, s => s.State == ResourceReadState.Ready);
        session.RefreshImages(); await Load(session, engine, B);
        var disconnect = session.DisconnectAsync();
        Check(!disconnect.IsCompleted && engine.DisposeCount == 0);
        var late = new ImageObservation(); opening.Succeed(late);
        await disconnect.WaitAsync(Limit);
        Check(late.DisposeCount == 1 && engine.DisposeCount == 1 && !engine.DisposedWithActiveRead
            && session.ImageSnapshot.State == ResourceReadState.Unavailable);
        await session.DisposeAsync();
    }

    public static async Task ImageReconnectOverlapAndJoinedReplacement()
    {
        var old = new EventImageConnection("old", honorReadCancellation: false);
        var fresh = new EventImageConnection("new"); var clock = new ImageEventClock(); var client = new ControlledClient(old, fresh);
        await using var session = EventSession(client, clock);
        session.Start(); var events = await OpenImageEvents(old); await Load(session, old, A);
        session.SelectImage(A); var stale = await old.NextInspection();
        await events.Emit(ImageEvent(A, nanos: 100_123_456_789)); await events.Barrier();
        long generation = session.ImageSnapshot.ConnectionGeneration;
        events.End(new(EngineEventFailureKind.TransportLoss, "Continuity lost"));
        await State(session, s => s.State == ResourceReadState.Unavailable);
        Check(stale.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1);
        stale.Succeed(Detail(A));
        await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
        (await clock.Next(TimeSpan.FromSeconds(3))).Release();
        var resumed = await fresh.NextOpening();
        Check(resumed.Since == new EngineEventCursor(98, 123_456_689), "Image events participate in exact shared cursor and fractional overlap");
        var overlap = new ImageObservation(); resumed.Succeed(overlap);
        var initial = await fresh.NextList();
        await overlap.Emit(ImageEvent(B, nanos: 100_123_456_789)); await overlap.Emit(ImageEvent(B, nanos: 100_123_456_789));
        await overlap.Barrier(); initial.Succeed(B);
        (await EventImageList(fresh, clock)).Succeed(C);
        await State(session, s => s.Images.Single().Id == C);
        Check(old.DisposeCount == 1 && events.DisposeCount == 1 && fresh.OpenCalls == 1
            && session.ImageSnapshot.ConnectionGeneration > generation && session.ImageSnapshot.Inspection is null);
        session.SelectImage(C); var late = await fresh.NextInspection();
        var disconnect = session.DisconnectAsync(); await disconnect.WaitAsync(Limit);
        Check(late.Token.IsCancellationRequested && overlap.DisposeCount == 1 && fresh.DisposeCount == 1);
    }

    public static async Task ImageObservationsCoexistWithCommandsAndLogs()
    {
        var engine = new AllCapabilitiesConnection("coexistence"); var clock = new ImageEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); await Load(session, engine, A);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var command = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        await engine.CommandStarted.Task.WaitAsync(Limit);
        int containerReads = engine.ContainerListCalls;
        await engine.Events.Emit(ImageEvent(A, "delete")); await engine.Events.Barrier();
        (await EventImageList(engine, clock)).Succeed(B);
        await State(session, s => s.Images.Single().Id == B);
        Check(!engine.CommandToken.IsCancellationRequested && !engine.LogToken.IsCancellationRequested
            && !engine.EventToken.IsCancellationRequested && !command.IsCompleted
            && engine.ContainerListCalls == containerReads && engine.EventCalls == 1
            && session.ContainerSnapshot.State == ResourceReadState.Ready
            && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming,
            "Image events use one shared observation without disturbing containers, commands or Logs");
        // Shared container observations still reconcile independently on the same subscription.
        await engine.Events.Emit(new("container", "start", AllCapabilitiesConnection.ContainerId,
            null, null, new(100, 100_123_456_789))); await engine.Events.Barrier();
        (await clock.Next(ImageBatch)).Release();
        await Until(() => engine.ContainerListCalls > containerReads);
        Check(engine.ListCalls == 2 && engine.EventCalls == 1 && !engine.CommandToken.IsCancellationRequested);
        await session.DisconnectAsync().WaitAsync(Limit); await command.WaitAsync(Limit);
        Check(engine.Events.Disposed && engine.Logs.Disposed && engine.DisposeCount == 1 && !engine.DisposedWithActiveRead);
    }

    public static async Task ImageInvalidatedDeadlineAndReplacementCleanup()
    {
        // Event supersession fences content without disabling the active request's deadline.
        var expired = new EventImageConnection("expired", honorReadCancellation: false); var clock = new ImageEventClock();
        var timeout = EventSession(new ControlledClient(expired), clock, new() { OperationTimeout = TimeSpan.FromMilliseconds(150), MaximumRecoveryAttempts = 0 });
        timeout.Start(); var observations = await OpenImageEvents(expired); await Load(timeout, expired, A);
        timeout.SelectImage(A); var stalled = await expired.NextInspection();
        await observations.Emit(ImageEvent(A)); await observations.Barrier();
        await State(timeout, s => s.State == ResourceReadState.Unavailable);
        Check(stalled.Token.IsCancellationRequested && expired.DisposeCount == 0
            && timeout.ImageSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.TransportLoss);
        stalled.Succeed(Detail(A));
        await ConnectionState(timeout, s => s.State == EngineConnectionState.Lost);
        Check(expired.DisposeCount == 1 && observations.DisposeCount == 1 && timeout.ImageSnapshot.Inspection is null);
        await timeout.DisposeAsync();

        var broken = new EventImageConnection("broken", honorReadCancellation: false); var transportClock = new ImageEventClock();
        var lost = EventSession(new ControlledClient(broken), transportClock, new() { MaximumRecoveryAttempts = 0 });
        lost.Start(); var brokenEvents = await OpenImageEvents(broken); await Load(lost, broken, A);
        lost.SelectImage(A); var obsolete = await broken.NextInspection();
        await brokenEvents.Emit(ImageEvent(A)); await brokenEvents.Barrier();
        obsolete.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        await ConnectionState(lost, s => s.State == EngineConnectionState.Lost);
        Check(lost.ImageSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.TransportLoss
            && broken.DisposeCount == 1 && brokenEvents.DisposeCount == 1,
            "Event-obsolete content still carries valid current connection continuity evidence");
        await lost.DisposeAsync();

        foreach (var action in new[] { "retry", "disconnect", "dispose" })
        {
            var old = new EventImageConnection(action, honorReadCancellation: false);
            var fresh = new EventImageConnection("replacement"); var timing = new ImageEventClock(); var client = new ControlledClient(old, fresh);
            var session = EventSession(client, timing);
            session.Start(); var events = await OpenImageEvents(old); await Load(session, old, A);
            long generation = session.ImageSnapshot.ConnectionGeneration;
            session.SelectImage(A); var late = await old.NextInspection();
            await events.Emit(ImageEvent(A)); await events.Barrier();
            Task? ending = null;
            if (action == "retry") session.Retry();
            else ending = action == "disconnect" ? session.DisconnectAsync() : session.DisposeAsync().AsTask();
            Check(late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1
                && session.ImageSnapshot.State == ResourceReadState.Unavailable);
            if (ending is not null) Check(!ending.IsCompleted);
            late.Succeed(Detail(A, "{\"late\":true}"));
            if (ending is not null) await ending.WaitAsync(Limit);
            else
            {
                var opening = await fresh.NextOpening();
                Check(opening.Since == new EngineEventCursor(98, 123_456_689));
                opening.Succeed(new ImageObservation()); await Load(session, fresh, B);
                Check(session.ImageSnapshot.ConnectionGeneration > generation && session.ImageSnapshot.Inspection is null);
            }
            Check(old.DisposeCount == 1 && events.DisposeCount == 1 && !old.DisposedWithActiveRead
                && session.ImageSnapshot.Inspection is null);
            await session.DisposeAsync();
        }
    }

    private sealed class ImageGatedClient : IEngineConnectionClient
    {
        public TaskCompletionSource<EngineConnectionAttempt> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
            => await Ready.Task.WaitAsync(cancellationToken);
    }

    private sealed class EventImageConnection(string version, bool honorReadCancellation = true, bool honorOpeningCancellation = true)
        : ControlledConnection(version, honorReadCancellation), IEngineEventSource
    {
        private readonly Channel<ImageOpening> openings = Channel.CreateUnbounded<ImageOpening>();
        public int OpenCalls;
        public override EngineMetadata Metadata => base.Metadata with { EngineId = "same-image-engine" };
        public async Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref OpenCalls); var opening = new ImageOpening(since, cancellationToken); openings.Writer.TryWrite(opening);
            return await (honorOpeningCancellation ? opening.Completion.Task.WaitAsync(cancellationToken) : opening.Completion.Task);
        }
        public async Task<ImageOpening> NextOpening() => await openings.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class ImageOpening(EngineEventCursor? since, CancellationToken token)
    {
        public EngineEventCursor? Since { get; } = since;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineEventSubscriptionAttempt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(ImageObservation observation) => Completion.SetResult(EngineEventSubscriptionAttempt.Subscribed(observation));
        public void Fail(EngineEventFailure failure) => Completion.SetResult(EngineEventSubscriptionAttempt.Failed(failure));
    }
    private class ImageObservation : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(32)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object sync = new();
        private TaskCompletionSource progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sent, consumed;
        public int DisposeCount, MaximumBuffered;
        public ImageObservation() => Events = new ImageObservedReader(this);
        public ChannelReader<EngineEvent> Events { get; }
        public Task<EngineEventFailure?> Completion => completion.Task;
        public async Task Emit(EngineEvent observation)
        {
            await channel.Writer.WriteAsync(observation).AsTask().WaitAsync(Limit);
            ++sent; MaximumBuffered = Math.Max(MaximumBuffered, channel.Reader.Count);
        }
        public async Task Barrier()
        {
            await Emit(new("ignored-barrier", "barrier", null, null, null, new(null, null)));
            int target = sent;
            while (true)
            {
                Task wait;
                lock (sync) { if (consumed >= target) return; wait = progress.Task; }
                await wait.WaitAsync(Limit);
            }
        }
        public void End(EngineEventFailure? failure) { completion.TrySetResult(failure); channel.Writer.TryComplete(); }
        public ValueTask DisposeAsync()
        { Interlocked.Increment(ref DisposeCount); End(null); return ValueTask.CompletedTask; }
        private sealed class ImageObservedReader(ImageObservation owner) : ChannelReader<EngineEvent>
        {
            public override bool TryRead(out EngineEvent item)
            {
                if (!owner.channel.Reader.TryRead(out item!)) return false;
                lock (owner.sync)
                {
                    ++owner.consumed; owner.progress.TrySetResult();
                    owner.progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                return true;
            }
            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
                => owner.channel.Reader.WaitToReadAsync(cancellationToken);
        }
    }
    private sealed class ImageEventClock : IConnectionDelay
    {
        private readonly ConcurrentDictionary<TimeSpan, Channel<PendingDelay>> waits = new();
        public async Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            var pending = new PendingDelay(duration, token); waits.GetOrAdd(duration, _ => Channel.CreateUnbounded<PendingDelay>()).Writer.TryWrite(pending);
            await pending.Completion.Task.WaitAsync(token);
        }
        public async Task<PendingDelay> Next(TimeSpan duration)
            => await waits.GetOrAdd(duration, _ => Channel.CreateUnbounded<PendingDelay>()).Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
}
