using System.Collections.Concurrent;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class NetworkBrowsingChecks
{
    private static readonly TimeSpan NetworkBatch = TimeSpan.FromMilliseconds(17), NetworkEventRetry = TimeSpan.FromSeconds(7);
    private static DesktopSession EventSession(IEngineConnectionClient client, NetworkEventClock clock, ConnectionRecoveryOptions? options = null)
        => new(client, options, clock, reconciliation: new() { BatchInterval = NetworkBatch,
            RetryDelay = NetworkEventRetry, ReplayOverlap = TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1) });
    private static EngineEvent NetworkEvent(string? id = null, string action = "connect", long? nanos = 100_123_456_789)
        => new("network", action, id, new Dictionary<string, string> { ["name"] = "private-untrusted-name", ["secret"] = "private-attribute" },
            null, new(100, nanos));
    private static async Task<ListCall> EventNetworkList(ControlledConnection engine, NetworkEventClock clock)
    { (await clock.Next(NetworkBatch)).Release(); return await engine.NextList(); }
    private static async Task<NetworkObservation> OpenNetworkEvents(EventNetworkConnection engine)
    {
        var opening = await engine.NextOpening();
        var events = new NetworkObservation(); opening.Succeed(events); return events;
    }

    public static async Task NetworkSnapshotObservationBoundaries()
    {
        var engine = new EventNetworkConnection("startup"); var clock = new NetworkEventClock();
        var client = new NetworkGatedClient();
        await using var session = EventSession(client, clock);
        var events = new NetworkObservation();
        int bridged = 0;
        // Force the shared coordinator to consume observations before BeginNetworkReads runs.
        session.ContainersChanged += (_, _) =>
        {
            if (session.ContainerSnapshot.ConnectionGeneration == 0 || Interlocked.Exchange(ref bridged, 1) != 0) return;
            var opening = engine.NextOpening().GetAwaiter().GetResult();
            opening.Succeed(events);
            events.Emit(NetworkEvent(A, "disconnect")).GetAwaiter().GetResult();
            events.Barrier().GetAwaiter().GetResult();
            Check(engine.ListCalls == 0, "Observation startup bridge precedes network coordinator creation");
        };
        session.Start();
        // Finish Start's own notification before allowing connection-owned startup notifications.
        client.Ready.SetResult(EngineConnectionAttempt.Connected(engine));
        var initial = await engine.NextList();
        await events.Emit(NetworkEvent(B, "destroy")); await events.Barrier();
        Check(!initial.Token.IsCancellationRequested && engine.ListCalls == 1);
        initial.SucceedNames(A, A);
        await State(session, s => s.State == ResourceReadState.Ready && s.Networks.Count == 1);
        var after = await EventNetworkList(engine, clock);
        await events.Emit(NetworkEvent(C, "connect")); await events.Barrier();
        after.SucceedNames(B);
        await State(session, s => s.Networks.Single().Id == B);
        (await EventNetworkList(engine, clock)).SucceedNames(C);
        await State(session, s => s.Networks.Single().Id == C);
        int calls = engine.ListCalls;
        await events.Emit(new("future-resource", "destroy", C, null, null, new(101, null))); await events.Barrier();
        Check(engine.ListCalls == calls && engine.OpenCalls == 1 && session.NetworkSnapshot.Networks.Single().Id == C,
            "Only authoritative lists replace rows; unknown resource types retain cursor-only behavior");
    }

    public static async Task NetworkStartupBridgeWithoutConcurrentObservation()
    {
        var engine = new EventNetworkConnection("bridge-only"); var clock = new NetworkEventClock();
        var client = new NetworkGatedClient();
        await using var session = EventSession(client, clock);
        var events = new NetworkObservation();
        int bridged = 0;
        session.ContainersChanged += (_, _) =>
        {
            if (session.ContainerSnapshot.ConnectionGeneration == 0 || Interlocked.Exchange(ref bridged, 1) != 0) return;
            engine.NextOpening().GetAwaiter().GetResult().Succeed(events);
            events.Emit(NetworkEvent(A, "create")).GetAwaiter().GetResult();
            events.Barrier().GetAwaiter().GetResult();
            Check(engine.ListCalls == 0);
        };
        session.Start(); client.Ready.SetResult(EngineConnectionAttempt.Connected(engine));
        (await engine.NextList()).SucceedNames(A);
        await State(session, s => s.IsAuthoritative);
        // No later observation can mask a lost startup bridge.
        (await EventNetworkList(engine, clock)).SucceedNames(B);
        await State(session, s => s.Networks.Single().Id == B);
        Check(engine.ListCalls == 2 && engine.OpenCalls == 1);
    }

    public static async Task NetworkActorIdentityAndConservativeDetail()
    {
        const string full = "  private/Δ\\cluster:%?#😀  ";
        var engine = new EventNetworkConnection("actor"); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenNetworkEvents(engine); await Load(session, engine, full, B);
        session.SelectNetwork(full); (await engine.NextInspection()).Succeed(Detail(full));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        foreach (var unrelated in new[] { B, full.Trim(), full[..10], full.ToLowerInvariant() })
        {
            await events.Emit(NetworkEvent(unrelated, "future-action") with
                { ActorAttributes = new Dictionary<string, string> { ["name"] = full, ["driver"] = "fake" } });
            await events.Barrier();
            Check(session.NetworkSnapshot.Inspection?.RawJson == Raw && session.NetworkSnapshot.SelectedNetworkId == full,
                "Actor attributes, prefixes, trimming and case cannot substitute for exact ordinal Actor.ID");
            (await EventNetworkList(engine, clock)).SucceedNames(full, B);
        }
        session.RefreshNetworkInspection(); (await engine.NextInspection()).Succeed(Detail(full));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        foreach (var identity in new string?[] { full, null, "", "  ", ".", "..", "bad\nname", "\ud800", new('x', 4097) })
        {
            await events.Emit(NetworkEvent(identity, "future-action")); await events.Barrier();
            Check(session.NetworkSnapshot.Inspection is null && session.NetworkSnapshot.InspectionState == ResourceReadState.Loading
                && session.NetworkSnapshot.SelectedNetworkId == full,
                "Exact or unusable identity clears requested raw detail without establishing absence");
            (await EventNetworkList(engine, clock)).SucceedNames(full, B);
            var detail = await engine.NextInspection(); Check(detail.Id == full);
            detail.Succeed(Detail(full)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        }
        session.ClearNetworkInspection();
        await events.Emit(NetworkEvent(null, "prune")); await events.Barrier();
        (await EventNetworkList(engine, clock)).SucceedNames(full, B);
        Check(session.NetworkSnapshot.InspectionState == ResourceReadState.NotRequested && session.NetworkSnapshot.Inspection is null);
    }

    public static async Task NetworkEventFailureContentAndObsoleteFailures()
    {
        var engine = new EventNetworkConnection("failures", honorCancellation: false); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenNetworkEvents(engine); await Load(session, engine, A, B);
        session.SelectNetwork(A);
        (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.AccessDenied));
        await State(session, s => s.InspectionState == ResourceReadState.Failed);
        await events.Emit(NetworkEvent(A, "future-action")); await events.Barrier();
        Check(session.NetworkSnapshot.InspectionFailure is null && session.NetworkSnapshot.Inspection is null
            && session.NetworkSnapshot.InspectionState == ResourceReadState.Loading);
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        foreach (var kind in new[] { EngineResourceFailureKind.AccessDenied, EngineResourceFailureKind.InvalidRequest,
            EngineResourceFailureKind.DaemonFailure, EngineResourceFailureKind.MalformedData, EngineResourceFailureKind.NotFound })
        {
            session.RefreshNetworkInspection(); var obsolete = await engine.NextInspection();
            await events.Emit(NetworkEvent(A, "destroy")); await events.Barrier();
            Check(!obsolete.Token.IsCancellationRequested && session.NetworkSnapshot.SelectedNetworkId == A);
            obsolete.Fail(Failure(kind));
            var pending = await EventNetworkList(engine, clock);
            Check(session.NetworkSnapshot.InspectionFailure is null && session.NetworkSnapshot.SelectedNetworkId == A);
            pending.SucceedNames(A, B);
            var replacement = await engine.NextInspection();
            Check(session.NetworkSnapshot.Inspection is null && replacement.Id == A);
            replacement.Succeed(Detail(A) with { Containers = new Dictionary<string, NetworkAttachment>(),
                Ipam = new(null, Array.Empty<NetworkIpamConfiguration>(), null) });
            await State(session, s => s.InspectionState == ResourceReadState.Ready);
            Check(session.NetworkSnapshot.Inspection!.RawJson == Raw
                && session.NetworkSnapshot.Inspection.Containers!.Count == 0
                && session.NetworkSnapshot.Inspection.Ipam is { Options: null, Config.Count: 0 });
        }
    }

    public static async Task NetworkMissingCapabilityKeepsSharedObservation()
    {
        var engine = new NetworklessConnection(); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start();
        await Until(() => session.VolumeSnapshot.State == ResourceReadState.Ready && session.NetworkSnapshot.ConnectionGeneration > 0);
        Check(session.NetworkSnapshot.State == ResourceReadState.Unavailable && engine.OpenCalls == 1);
        await engine.Events.Emit(NetworkEvent(A)); await engine.Events.Barrier();
        Check(engine.VolumeCalls == 1 && session.NetworkSnapshot.InspectionState == ResourceReadState.Unavailable);
        await engine.Events.Emit(new("volume", "mount", "exact-volume", null, null, new(100, 100_123_456_789)));
        await engine.Events.Barrier(); (await clock.Next(NetworkBatch)).Release();
        await Until(() => engine.VolumeCalls == 2);
        Check(engine.OpenCalls == 1 && session.NetworkSnapshot.State == ResourceReadState.Unavailable);
        await session.DisconnectAsync().WaitAsync(Limit);
        Check(engine.Events.DisposeCount == 1 && engine.DisposeCount == 1);
    }

    public static async Task NetworkSelectedObservationFences()
    {
        var engine = new EventNetworkConnection("detail", honorCancellation: false); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenNetworkEvents(engine); await Load(session, engine, A, B);
        session.SelectNetwork(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        await events.Emit(NetworkEvent(B, "connect")); await events.Barrier();
        Check(session.NetworkSnapshot.Inspection?.RawJson == Raw, "Unrelated identity keeps selected content");
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        session.RefreshNetworkInspection(); var stale = await engine.NextInspection();
        await events.Emit(NetworkEvent(A, "disconnect")); await events.Barrier();
        Check(session.NetworkSnapshot.Inspection is null && session.NetworkSnapshot.InspectionState == ResourceReadState.Loading);
        stale.Succeed(Detail(A, "{\"stale\":true}"));
        var list = await EventNetworkList(engine, clock); list.SucceedNames(A, B);
        var replacement = await engine.NextInspection();
        Check(session.NetworkSnapshot.Inspection is null && replacement.Id == A);
        replacement.Succeed(Detail(A)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(session.NetworkSnapshot.Inspection!.RawJson == Raw && session.NetworkSnapshot.SelectedNetworkId == A);
        session.ClearNetworkInspection();
        await events.Emit(NetworkEvent(null, "unknown-action")); await events.Barrier();
        Check(session.NetworkSnapshot.InspectionState == ResourceReadState.NotRequested, "Missing identity cannot reopen explicitly cleared detail");
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        session.RefreshNetworkInspection(); var removed = await engine.NextInspection();
        await events.Emit(NetworkEvent(A, "destroy")); await events.Barrier();
        removed.Fail(Failure(EngineResourceFailureKind.NotFound));
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        Check(session.NetworkSnapshot.SelectedNetworkId == A, "Stale NotFound is fenced like a stale success");
        (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.NotFound));
        await State(session, s => s.SelectedNetworkId is null);
        Check(session.NetworkSnapshot.Networks.Single().Id == B && session.NetworkSnapshot.Inspection is null);
        session.SelectNetwork(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        await events.Emit(NetworkEvent(B, "destroy")); await events.Barrier();
        (await EventNetworkList(engine, clock)).SucceedNames();
        await State(session, s => s.SelectedNetworkId is null);
        Check(session.NetworkSnapshot.Networks.Count == 0 && session.NetworkSnapshot.Inspection is null);
    }

    public static async Task NetworkBurstCoalescingAndFairness()
    {
        var engine = new EventNetworkConnection("burst", honorCancellation: false); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenNetworkEvents(engine); await Load(session, engine, A);
        session.SelectNetwork(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        int notifications = 0; session.NetworksChanged += (_, _) => Interlocked.Increment(ref notifications);
        for (int index = 0; index < 2000; index++) await events.Emit(NetworkEvent(A, index % 2 == 0 ? "connect" : "disconnect"));
        await events.Barrier();
        Check(engine.ListCalls == 1 && notifications == 1, "Duplicate same-timestamp burst coalesces pending list/detail and notifications");
        var list = await EventNetworkList(engine, clock);
        for (int index = 0; index < 1000; index++) await events.Emit(NetworkEvent(A, "destroy"));
        await events.Barrier();
        Check(!list.Token.IsCancellationRequested && engine.ListCalls == 2);
        list.SucceedNames(A, B);
        var detail = await engine.NextInspection();
        Check(engine.ListCalls == 2 && detail.Id == A && session.NetworkSnapshot.Networks.Count == 2,
            "Sustained invalidation allows useful list publication and a requested detail turn");
        await events.Emit(NetworkEvent(null, "anything")); await events.Barrier();
        detail.Succeed(Detail(A, "{\"stale\":true}"));
        var next = await EventNetworkList(engine, clock); next.SucceedNames(A, B);
        (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(engine.ListCalls == 3 && engine.InspectionCalls == 3 && engine.MaximumConcurrent == 1
            && events.MaximumBuffered <= 32 && session.NetworkSnapshot.Inspection!.RawJson == Raw);
        await events.Emit(NetworkEvent(A)); await events.Barrier();
        var waiting = await clock.Next(NetworkBatch);
        session.RefreshNetworks(); waiting.Release();
        (await engine.NextList()).SucceedNames(A);
        (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
    }

    public static async Task NetworkObservationFailureAndReopening()
    {
        var engine = new EventNetworkConnection("observation"); var clock = new NetworkEventClock(); var client = new ControlledClient(engine);
        await using var session = EventSession(client, clock);
        session.Start();
        var failure = new EngineEventFailure(EngineEventFailureKind.AccessDenied, "Safe observation failure", 403, "private daemon response");
        (await engine.NextOpening()).Fail(failure);
        await Load(session, engine, A);
        Check(session.Snapshot.EventFailure == failure && session.Snapshot.EventRecoveryPending && client.Calls == 1);
        session.SelectNetwork(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.RefreshNetworks(); await Load(session, engine, A, B);
        (await clock.Next(NetworkEventRetry)).Release();
        var events = await OpenNetworkEvents(engine);
        var reopenedDetail = await engine.NextInspection();
        Check(session.NetworkSnapshot.Inspection is null && client.Calls == 1 && session.Snapshot.EventFailure is null);
        reopenedDetail.Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var fresh = await EventNetworkList(engine, clock);
        await events.Emit(NetworkEvent(A, "disconnect")); await events.Barrier();
        fresh.SucceedNames(A, B); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        (await EventNetworkList(engine, clock)).SucceedNames(B);
        await State(session, s => s.SelectedNetworkId is null);
        events.End(new(EngineEventFailureKind.MalformedEvent, "Safe malformed-event failure"));
        await Until(() => session.Snapshot.EventFailure?.Kind == EngineEventFailureKind.MalformedEvent);
        (await EventNetworkList(engine, clock)).SucceedNames(B);
        await State(session, s => s.Networks.Count == 1);
        session.RefreshNetworks(); await Load(session, engine, B);
        Check(session.EngineState == EngineConnectionState.Connected && client.Calls == 1);
        session.SelectNetwork(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.ClearNetworkInspection(); int inspections = engine.InspectionCalls;
        (await clock.Next(NetworkEventRetry)).Release();
        await OpenNetworkEvents(engine);
        (await EventNetworkList(engine, clock)).SucceedNames(B);
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(session.NetworkSnapshot.InspectionState == ResourceReadState.NotRequested && engine.InspectionCalls == inspections,
            "Reopening cannot undo an explicit detail clear");
    }

    public static async Task NetworkObservationOpeningFallbackAndJoin()
    {
        var engine = new EventNetworkConnection("late-opening", honorOpeningCancellation: false); var clock = new NetworkEventClock();
        var session = EventSession(new ControlledClient(engine), clock, new() { OperationTimeout = TimeSpan.FromMilliseconds(120), MaximumRecoveryAttempts = 0 });
        session.Start(); var opening = await engine.NextOpening();
        Check(engine.ListCalls == 0, "Network snapshot waits for observation establishment");
        var initial = await engine.NextList();
        Check(opening.Token.IsCancellationRequested, "Opening deadline provides bounded snapshot fallback even for a non-cooperative opener");
        initial.SucceedNames(A); await State(session, s => s.State == ResourceReadState.Ready);
        session.RefreshNetworks(); await Load(session, engine, B);
        var disconnect = session.DisconnectAsync();
        Check(!disconnect.IsCompleted && engine.DisposeCount == 0);
        var late = new NetworkObservation(); opening.Succeed(late);
        await disconnect.WaitAsync(Limit);
        Check(late.DisposeCount == 1 && engine.DisposeCount == 1 && !engine.DisposedWithActiveRead
            && session.NetworkSnapshot.State == ResourceReadState.Unavailable);
        await session.DisposeAsync();
    }

    public static async Task NetworkReconnectOverlapAndJoinedReplacement()
    {
        var old = new EventNetworkConnection("old", honorCancellation: false);
        var fresh = new EventNetworkConnection("new"); var clock = new NetworkEventClock(); var client = new ControlledClient(old, fresh);
        await using var session = EventSession(client, clock);
        session.Start(); var events = await OpenNetworkEvents(old); await Load(session, old, A);
        session.SelectNetwork(A); var stale = await old.NextInspection();
        await events.Emit(NetworkEvent(A, nanos: 100_123_456_789)); await events.Barrier();
        long generation = session.NetworkSnapshot.ConnectionGeneration;
        events.End(new(EngineEventFailureKind.TransportLoss, "Continuity lost"));
        await State(session, s => s.State == ResourceReadState.Unavailable);
        Check(stale.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1);
        stale.Succeed(Detail(A));
        await Until(() => session.EngineState == EngineConnectionState.Lost);
        (await clock.Next(TimeSpan.FromSeconds(3))).Release();
        var resumed = await fresh.NextOpening();
        Check(resumed.Since == new EngineEventCursor(98, 123_456_689), "Network events participate in exact shared cursor and fractional overlap");
        var overlap = new NetworkObservation(); resumed.Succeed(overlap);
        var initial = await fresh.NextList();
        await overlap.Emit(NetworkEvent(B, nanos: 100_123_456_789)); await overlap.Emit(NetworkEvent(B, nanos: 100_123_456_789));
        await overlap.Barrier(); initial.SucceedNames(B);
        (await EventNetworkList(fresh, clock)).SucceedNames(C);
        await State(session, s => s.Networks.Single().Id == C);
        Check(old.DisposeCount == 1 && events.DisposeCount == 1 && fresh.OpenCalls == 1
            && session.NetworkSnapshot.ConnectionGeneration > generation && session.NetworkSnapshot.Inspection is null);
        session.SelectNetwork(C); var late = await fresh.NextInspection();
        var disconnect = session.DisconnectAsync(); await disconnect.WaitAsync(Limit);
        Check(late.Token.IsCancellationRequested && overlap.DisposeCount == 1 && fresh.DisposeCount == 1);
    }

    public static async Task NetworkObservationsCoexistWithCommandsAndLogs()
    {
        var engine = new AllCapabilitiesConnection("coexistence"); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); await Load(session, engine, A);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var command = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        var imageCommand = session.ExecuteImageOperationAsync(new(new(session.ImageSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ImageId)));
        await Task.WhenAll(engine.CommandStarted.Task, engine.ImageCommandStarted.Task).WaitAsync(Limit);
        int containerReads = engine.ContainerListCalls;
        await engine.Events.Emit(NetworkEvent(A, "destroy")); await engine.Events.Barrier();
        (await EventNetworkList(engine, clock)).SucceedNames(B);
        await State(session, s => s.Networks.Single().Id == B);
        Check(!engine.CommandToken.IsCancellationRequested && !engine.LogToken.IsCancellationRequested
            && !engine.ImageCommandToken.IsCancellationRequested && !imageCommand.IsCompleted
            && !engine.EventToken.IsCancellationRequested && !command.IsCompleted
            && engine.ContainerListCalls == containerReads && engine.ImageListCalls == 1 && engine.VolumeListCalls == 1 && engine.EventCalls == 1
            && session.ContainerSnapshot.State == ResourceReadState.Ready
            && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming,
            "Network events use one shared observation without disturbing containers, commands or Logs");
        // Shared container observations still reconcile independently on the same subscription.
        await engine.Events.Emit(new("container", "start", AllCapabilitiesConnection.ContainerId,
            null, null, new(100, 100_123_456_789))); await engine.Events.Barrier();
        (await clock.Next(NetworkBatch)).Release();
        await Until(() => engine.ContainerListCalls > containerReads);
        Check(engine.ListCalls == 2 && engine.EventCalls == 1 && !engine.CommandToken.IsCancellationRequested);
        await engine.Events.Emit(new("image", "future-action", AllCapabilitiesConnection.ImageId,
            null, null, new(100, 100_123_456_789))); await engine.Events.Barrier();
        (await clock.Next(NetworkBatch)).Release();
        await Until(() => engine.ImageListCalls > 1);
        Check(engine.ListCalls == 2 && engine.EventCalls == 1 && !engine.ImageCommandToken.IsCancellationRequested);
        await session.DisconnectAsync().WaitAsync(Limit); await Task.WhenAll(command, imageCommand).WaitAsync(Limit);
        Check(engine.Events.Disposed && engine.Logs.Disposed && engine.DisposeCount == 1 && !engine.DisposedWithActiveRead);
    }

    public static async Task NetworkInvalidatedDeadlineAndReplacementCleanup()
    {
        // Event supersession fences content without disabling the active request's deadline.
        var expired = new EventNetworkConnection("expired", honorCancellation: false); var clock = new NetworkEventClock();
        var timeout = EventSession(new ControlledClient(expired), clock, new() { OperationTimeout = TimeSpan.FromMilliseconds(150), MaximumRecoveryAttempts = 0 });
        timeout.Start(); var observations = await OpenNetworkEvents(expired); await Load(timeout, expired, A);
        timeout.SelectNetwork(A); var stalled = await expired.NextInspection();
        await observations.Emit(NetworkEvent(A)); await observations.Barrier();
        await State(timeout, s => s.State == ResourceReadState.Unavailable);
        Check(stalled.Token.IsCancellationRequested && expired.DisposeCount == 0
            && timeout.NetworkSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.TransportLoss);
        stalled.Succeed(Detail(A));
        await Until(() => timeout.EngineState == EngineConnectionState.Lost);
        Check(expired.DisposeCount == 1 && observations.DisposeCount == 1 && timeout.NetworkSnapshot.Inspection is null);
        await timeout.DisposeAsync();

        var broken = new EventNetworkConnection("broken", honorCancellation: false); var transportClock = new NetworkEventClock();
        var lost = EventSession(new ControlledClient(broken), transportClock, new() { MaximumRecoveryAttempts = 0 });
        lost.Start(); var brokenEvents = await OpenNetworkEvents(broken); await Load(lost, broken, A);
        lost.SelectNetwork(A); var obsolete = await broken.NextInspection();
        await brokenEvents.Emit(NetworkEvent(A)); await brokenEvents.Barrier();
        obsolete.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        await Until(() => lost.EngineState == EngineConnectionState.Lost);
        Check(lost.NetworkSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.TransportLoss
            && broken.DisposeCount == 1 && brokenEvents.DisposeCount == 1,
            "Event-obsolete content still carries valid current connection continuity evidence");
        await lost.DisposeAsync();

        foreach (var action in new[] { "retry", "disconnect", "dispose" })
        {
            var old = new EventNetworkConnection(action, honorCancellation: false);
            var fresh = new EventNetworkConnection("replacement"); var timing = new NetworkEventClock(); var client = new ControlledClient(old, fresh);
            var session = EventSession(client, timing);
            session.Start(); var events = await OpenNetworkEvents(old); await Load(session, old, A);
            long generation = session.NetworkSnapshot.ConnectionGeneration;
            session.SelectNetwork(A); var late = await old.NextInspection();
            await events.Emit(NetworkEvent(A)); await events.Barrier();
            Task? ending = null;
            if (action == "retry") session.Retry();
            else ending = action == "disconnect" ? session.DisconnectAsync() : session.DisposeAsync().AsTask();
            Check(late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1
                && session.NetworkSnapshot.State == ResourceReadState.Unavailable);
            if (ending is not null) Check(!ending.IsCompleted);
            late.Succeed(Detail(A, "{\"late\":true}"));
            if (ending is not null) await ending.WaitAsync(Limit);
            else
            {
                var opening = await fresh.NextOpening();
                Check(opening.Since == new EngineEventCursor(98, 123_456_689));
                opening.Succeed(new NetworkObservation()); await Load(session, fresh, B);
                Check(session.NetworkSnapshot.ConnectionGeneration > generation && session.NetworkSnapshot.Inspection is null);
            }
            Check(old.DisposeCount == 1 && events.DisposeCount == 1 && !old.DisposedWithActiveRead
                && session.NetworkSnapshot.Inspection is null);
            await session.DisposeAsync();
        }
    }

    private sealed class NetworklessConnection : IEngineConnection, IEngineEventSource, IVolumeReader
    {
        public EngineMetadata Metadata => new MissingConnection().Metadata;
        public NetworkObservation Events { get; } = new();
        public int OpenCalls, VolumeCalls, DisposeCount;
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<EngineConnectionFailure?>(null);
        public ValueTask DisposeAsync() { ++DisposeCount; return ValueTask.CompletedTask; }
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        { ++OpenCalls; return Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(Events)); }
        public Task<EngineResourceResult<VolumeListing>> ListVolumesAsync(CancellationToken cancellationToken = default)
        { ++VolumeCalls; return Task.FromResult(EngineResourceResult<VolumeListing>.Success(new(Array.Empty<VolumeSummary>(), null))); }
        public Task<EngineResourceResult<VolumeInspection>> InspectVolumeAsync(string name, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No volume inspection requested");
    }

    private sealed class NetworkGatedClient : IEngineConnectionClient
    {
        public TaskCompletionSource<EngineConnectionAttempt> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
            => await Ready.Task.WaitAsync(cancellationToken);
    }

    private sealed class EventNetworkConnection(string version, bool honorCancellation = true, bool honorOpeningCancellation = true)
        : ControlledConnection(version, honorCancellation), IEngineEventSource
    {
        private readonly Channel<NetworkOpening> openings = Channel.CreateUnbounded<NetworkOpening>();
        public int OpenCalls;
        public override EngineMetadata Metadata => base.Metadata with { EngineId = "same-network-engine" };
        public async Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref OpenCalls); var opening = new NetworkOpening(since, cancellationToken); openings.Writer.TryWrite(opening);
            return await (honorOpeningCancellation ? opening.Completion.Task.WaitAsync(cancellationToken) : opening.Completion.Task);
        }
        public async Task<NetworkOpening> NextOpening() => await openings.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class NetworkOpening(EngineEventCursor? since, CancellationToken token)
    {
        public EngineEventCursor? Since { get; } = since;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineEventSubscriptionAttempt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(NetworkObservation observation) => Completion.SetResult(EngineEventSubscriptionAttempt.Subscribed(observation));
        public void Fail(EngineEventFailure failure) => Completion.SetResult(EngineEventSubscriptionAttempt.Failed(failure));
    }
    private class NetworkObservation : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(32)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object sync = new();
        private TaskCompletionSource progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sent, consumed;
        public int DisposeCount, MaximumBuffered;
        public NetworkObservation() => Events = new NetworkObservedReader(this);
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
        public virtual ValueTask DisposeAsync()
        { Interlocked.Increment(ref DisposeCount); End(null); return ValueTask.CompletedTask; }
        private sealed class NetworkObservedReader(NetworkObservation owner) : ChannelReader<EngineEvent>
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
    private sealed class NetworkEventClock : IConnectionDelay
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
