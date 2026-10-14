using System.Collections.Concurrent;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class VolumeBrowsingChecks
{
    private static readonly TimeSpan VolumeBatch = TimeSpan.FromMilliseconds(17), VolumeEventRetry = TimeSpan.FromSeconds(7);
    private static DesktopSession EventSession(IEngineConnectionClient client, VolumeEventClock clock, ConnectionRecoveryOptions? options = null)
        => new(client, options, clock, reconciliation: new() { BatchInterval = VolumeBatch,
            RetryDelay = VolumeEventRetry, ReplayOverlap = TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1) });
    private static EngineEvent VolumeEvent(string? id = null, string action = "mount", long? nanos = 100_123_456_789)
        => new("volume", action, id, new Dictionary<string, string> { ["name"] = "private-untrusted-name", ["secret"] = "private-attribute" },
            null, new(100, nanos));
    private static async Task<ListCall> EventVolumeList(ControlledConnection engine, VolumeEventClock clock)
    { (await clock.Next(VolumeBatch)).Release(); return await engine.NextList(); }
    private static async Task<VolumeObservation> OpenVolumeEvents(EventVolumeConnection engine)
    {
        var opening = await engine.NextOpening();
        var events = new VolumeObservation(); opening.Succeed(events); return events;
    }

    public static async Task VolumeSnapshotObservationBoundaries()
    {
        var engine = new EventVolumeConnection("startup"); var clock = new VolumeEventClock();
        var client = new VolumeGatedClient();
        await using var session = EventSession(client, clock);
        var events = new VolumeObservation();
        int bridged = 0;
        // Force the shared coordinator to consume observations before BeginVolumeReads runs.
        session.ContainersChanged += (_, _) =>
        {
            if (session.ContainerSnapshot.ConnectionGeneration == 0 || Interlocked.Exchange(ref bridged, 1) != 0) return;
            var opening = engine.NextOpening().GetAwaiter().GetResult();
            opening.Succeed(events);
            events.Emit(VolumeEvent(A, "unmount")).GetAwaiter().GetResult();
            events.Barrier().GetAwaiter().GetResult();
            Check(engine.ListCalls == 0, "Observation startup bridge precedes volume coordinator creation");
        };
        session.Start();
        // Finish Start's own notification before allowing connection-owned startup notifications.
        client.Ready.SetResult(EngineConnectionAttempt.Connected(engine));
        var initial = await engine.NextList();
        await events.Emit(VolumeEvent(B, "delete")); await events.Barrier();
        Check(!initial.Token.IsCancellationRequested && engine.ListCalls == 1);
        initial.SucceedNames(A, A);
        await State(session, s => s.State == ResourceReadState.Ready && s.Volumes.Count == 1);
        var after = await EventVolumeList(engine, clock);
        await events.Emit(VolumeEvent(C, "mount")); await events.Barrier();
        after.SucceedNames(B);
        await State(session, s => s.Volumes.Single().Name == B);
        (await EventVolumeList(engine, clock)).SucceedNames(C);
        await State(session, s => s.Volumes.Single().Name == C);
        int calls = engine.ListCalls;
        await events.Emit(new("future-resource", "delete", C, null, null, new(101, null))); await events.Barrier();
        Check(engine.ListCalls == calls && engine.OpenCalls == 1 && session.VolumeSnapshot.Volumes.Single().Name == C,
            "Only authoritative lists replace rows; unknown resource types retain cursor-only behavior");
    }

    public static async Task VolumeStartupBridgeWithoutConcurrentObservation()
    {
        var engine = new EventVolumeConnection("bridge-only"); var clock = new VolumeEventClock();
        var client = new VolumeGatedClient();
        await using var session = EventSession(client, clock);
        var events = new VolumeObservation();
        int bridged = 0;
        session.ContainersChanged += (_, _) =>
        {
            if (session.ContainerSnapshot.ConnectionGeneration == 0 || Interlocked.Exchange(ref bridged, 1) != 0) return;
            engine.NextOpening().GetAwaiter().GetResult().Succeed(events);
            events.Emit(VolumeEvent(A, "create")).GetAwaiter().GetResult();
            events.Barrier().GetAwaiter().GetResult();
            Check(engine.ListCalls == 0);
        };
        session.Start(); client.Ready.SetResult(EngineConnectionAttempt.Connected(engine));
        (await engine.NextList()).SucceedNames(A);
        await State(session, s => s.IsAuthoritative);
        // No later observation can mask a lost startup bridge.
        (await EventVolumeList(engine, clock)).SucceedNames(B);
        await State(session, s => s.Volumes.Single().Name == B);
        Check(engine.ListCalls == 2 && engine.OpenCalls == 1);
    }

    public static async Task VolumeActorIdentityAndConservativeDetail()
    {
        const string full = "  private/Δ\\cluster:%?#😀  ";
        var engine = new EventVolumeConnection("actor"); var clock = new VolumeEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenVolumeEvents(engine); await Load(session, engine, full, B);
        session.SelectVolume(full); (await engine.NextInspection()).Succeed(Detail(full));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        foreach (var unrelated in new[] { B, full.Trim(), full[..10], full.ToLowerInvariant() })
        {
            await events.Emit(VolumeEvent(unrelated, "future-action") with
                { ActorAttributes = new Dictionary<string, string> { ["name"] = full, ["driver"] = "fake" } });
            await events.Barrier();
            Check(session.VolumeSnapshot.Inspection?.RawJson == Raw && session.VolumeSnapshot.SelectedVolumeName == full,
                "Actor attributes, prefixes, trimming and case cannot substitute for exact ordinal Actor.ID");
            (await EventVolumeList(engine, clock)).SucceedNames(full, B);
        }
        session.RefreshVolumeInspection(); (await engine.NextInspection()).Succeed(Detail(full));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        foreach (var identity in new string?[] { full, null, "", "  ", ".", "..", "bad\nname", "\ud800", new('x', 4097) })
        {
            await events.Emit(VolumeEvent(identity, "future-action")); await events.Barrier();
            Check(session.VolumeSnapshot.Inspection is null && session.VolumeSnapshot.InspectionState == ResourceReadState.Loading
                && session.VolumeSnapshot.SelectedVolumeName == full,
                "Exact or unusable identity clears requested raw detail without establishing absence");
            (await EventVolumeList(engine, clock)).SucceedNames(full, B);
            var detail = await engine.NextInspection(); Check(detail.Name == full);
            detail.Succeed(Detail(full)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        }
        session.ClearVolumeInspection();
        await events.Emit(VolumeEvent(null, "prune")); await events.Barrier();
        (await EventVolumeList(engine, clock)).SucceedNames(full, B);
        Check(session.VolumeSnapshot.InspectionState == ResourceReadState.NotRequested && session.VolumeSnapshot.Inspection is null);
    }

    public static async Task VolumeEventListingEvidenceAndObsoleteFailures()
    {
        var engine = new EventVolumeConnection("evidence", honorReadCancellation: false); var clock = new VolumeEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenVolumeEvents(engine); await Load(session, engine, A, B);
        session.SelectVolume(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        foreach (var kind in new[] { EngineResourceFailureKind.AccessDenied, EngineResourceFailureKind.InvalidRequest,
            EngineResourceFailureKind.DaemonFailure, EngineResourceFailureKind.MalformedData, EngineResourceFailureKind.NotFound })
        {
            session.RefreshVolumeInspection(); var obsolete = await engine.NextInspection();
            await events.Emit(VolumeEvent(A, "destroy")); await events.Barrier();
            Check(!obsolete.Token.IsCancellationRequested && session.VolumeSnapshot.SelectedVolumeName == A
                && session.VolumeSnapshot.Volumes.Any(row => row.Name == A));
            obsolete.Fail(Failure(kind));
            var warnings = new[] { "private warning\n unchanged", "", "private warning\n unchanged" };
            var pending = await EventVolumeList(engine, clock);
            pending.Succeed(new VolumeListing(new[] { Row(B) with { Driver = "new driver" }, Row(C) }, warnings));
            var replacement = await engine.NextInspection();
            Check(session.VolumeSnapshot.SelectedVolumeName == A && session.VolumeSnapshot.InspectionFailure is null
                && session.VolumeSnapshot.HasRetainedRows && !session.VolumeSnapshot.IsAuthoritative
                && session.VolumeSnapshot.Completeness == VolumeListCompleteness.Incomplete
                && session.VolumeSnapshot.Volumes.Any(row => row.Name == A)
                && session.VolumeSnapshot.LatestListing!.Volumes!.Count == 2
                && session.VolumeSnapshot.Warnings!.SequenceEqual(warnings));
            warnings[0] = "mutated";
            Check(session.VolumeSnapshot.Warnings![0] == "private warning\n unchanged");
            replacement.Succeed(Detail(A)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        }
        foreach (bool unavailable in new[] { true, false })
        {
            await events.Emit(VolumeEvent(A, "delete")); await events.Barrier();
            (await EventVolumeList(engine, clock)).Succeed(new VolumeListing(unavailable ? null : Array.Empty<VolumeSummary>(),
                new[] { "private fetch failure" }));
            var detail = await engine.NextInspection();
            Check(session.VolumeSnapshot.SelectedVolumeName == A && session.VolumeSnapshot.HasRetainedRows
                && !session.VolumeSnapshot.IsAuthoritative && session.VolumeSnapshot.Volumes.Any(row => row.Name == A)
                && (unavailable ? session.VolumeSnapshot.LatestListing!.Volumes is null
                    : session.VolumeSnapshot.LatestListing!.Volumes!.Count == 0));
            detail.Succeed(Detail(A)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        }
        // An exact-name current Inspect NotFound supplies independent absence evidence.
        await events.Emit(VolumeEvent(A, "unknown-action")); await events.Barrier();
        (await EventVolumeList(engine, clock)).Succeed(new VolumeListing(null, new[] { "last private warning" }));
        var missing = await engine.NextInspection();
        var latest = session.VolumeSnapshot.LatestListing;
        missing.Fail(Failure(EngineResourceFailureKind.NotFound));
        await State(session, s => s.SelectedVolumeName is null);
        Check(ReferenceEquals(latest, session.VolumeSnapshot.LatestListing) && session.VolumeSnapshot.Inspection is null
            && session.VolumeSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.NotFound
            && !session.VolumeSnapshot.Volumes.Any(row => row.Name == A));
        session.SelectVolume(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        await events.Emit(VolumeEvent(B, "destroy")); await events.Barrier();
        Check(session.VolumeSnapshot.SelectedVolumeName == B);
        (await EventVolumeList(engine, clock)).SucceedNames();
        await State(session, s => s.SelectedVolumeName is null && s.IsAuthoritative);
        Check(session.VolumeSnapshot.Volumes.Count == 0 && session.VolumeSnapshot.Warnings is null);
    }

    public static async Task VolumeSelectedObservationFences()
    {
        var engine = new EventVolumeConnection("detail", honorReadCancellation: false); var clock = new VolumeEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenVolumeEvents(engine); await Load(session, engine, A, B);
        session.SelectVolume(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        await events.Emit(VolumeEvent(B, "mount")); await events.Barrier();
        Check(session.VolumeSnapshot.Inspection?.RawJson == Raw, "Unrelated identity keeps selected content");
        (await EventVolumeList(engine, clock)).SucceedNames(A, B);
        session.RefreshVolumeInspection(); var stale = await engine.NextInspection();
        await events.Emit(VolumeEvent(A, "unmount")); await events.Barrier();
        Check(session.VolumeSnapshot.Inspection is null && session.VolumeSnapshot.InspectionState == ResourceReadState.Loading);
        stale.Succeed(Detail(A, "{\"stale\":true}"));
        var list = await EventVolumeList(engine, clock); list.SucceedNames(A, B);
        var replacement = await engine.NextInspection();
        Check(session.VolumeSnapshot.Inspection is null && replacement.Name == A);
        replacement.Succeed(Detail(A)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(session.VolumeSnapshot.Inspection!.RawJson == Raw && session.VolumeSnapshot.SelectedVolumeName == A);
        session.ClearVolumeInspection();
        await events.Emit(VolumeEvent(null, "unknown-action")); await events.Barrier();
        Check(session.VolumeSnapshot.InspectionState == ResourceReadState.NotRequested, "Missing identity cannot reopen explicitly cleared detail");
        (await EventVolumeList(engine, clock)).SucceedNames(A, B);
        session.RefreshVolumeInspection(); var removed = await engine.NextInspection();
        await events.Emit(VolumeEvent(A, "delete")); await events.Barrier();
        removed.Fail(Failure(EngineResourceFailureKind.NotFound));
        (await EventVolumeList(engine, clock)).SucceedNames(A, B);
        Check(session.VolumeSnapshot.SelectedVolumeName == A, "Stale NotFound is fenced like a stale success");
        (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.NotFound));
        await State(session, s => s.SelectedVolumeName is null);
        Check(session.VolumeSnapshot.Volumes.Single().Name == B && session.VolumeSnapshot.Inspection is null);
        session.SelectVolume(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        await events.Emit(VolumeEvent(B, "delete")); await events.Barrier();
        (await EventVolumeList(engine, clock)).SucceedNames();
        await State(session, s => s.SelectedVolumeName is null);
        Check(session.VolumeSnapshot.Volumes.Count == 0 && session.VolumeSnapshot.Inspection is null);
    }

    public static async Task VolumeBurstCoalescingAndFairness()
    {
        var engine = new EventVolumeConnection("burst", honorReadCancellation: false); var clock = new VolumeEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenVolumeEvents(engine); await Load(session, engine, A);
        session.SelectVolume(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        int notifications = 0; session.VolumesChanged += (_, _) => Interlocked.Increment(ref notifications);
        for (int index = 0; index < 2000; index++) await events.Emit(VolumeEvent(A, index % 2 == 0 ? "mount" : "unmount"));
        await events.Barrier();
        Check(engine.ListCalls == 1 && notifications == 1, "Duplicate same-timestamp burst coalesces pending list/detail and notifications");
        var list = await EventVolumeList(engine, clock);
        for (int index = 0; index < 1000; index++) await events.Emit(VolumeEvent(A, "delete"));
        await events.Barrier();
        Check(!list.Token.IsCancellationRequested && engine.ListCalls == 2);
        list.SucceedNames(A, B);
        var detail = await engine.NextInspection();
        Check(engine.ListCalls == 2 && detail.Name == A && session.VolumeSnapshot.Volumes.Count == 2,
            "Sustained invalidation allows useful list publication and a requested detail turn");
        await events.Emit(VolumeEvent(null, "anything")); await events.Barrier();
        detail.Succeed(Detail(A, "{\"stale\":true}"));
        var next = await EventVolumeList(engine, clock); next.SucceedNames(A, B);
        (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(engine.ListCalls == 3 && engine.InspectionCalls == 3 && engine.MaximumConcurrent == 1
            && events.MaximumBuffered <= 32 && session.VolumeSnapshot.Inspection!.RawJson == Raw);
        await events.Emit(VolumeEvent(A)); await events.Barrier();
        var waiting = await clock.Next(VolumeBatch);
        session.RefreshVolumes(); waiting.Release();
        (await engine.NextList()).SucceedNames(A);
        (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
    }

    public static async Task VolumeObservationFailureAndReopening()
    {
        var engine = new EventVolumeConnection("observation"); var clock = new VolumeEventClock(); var client = new ControlledClient(engine);
        await using var session = EventSession(client, clock);
        session.Start();
        var failure = new EngineEventFailure(EngineEventFailureKind.AccessDenied, "Safe observation failure", 403, "private daemon response");
        (await engine.NextOpening()).Fail(failure);
        await Load(session, engine, A);
        Check(session.Snapshot.EventFailure == failure && session.Snapshot.EventRecoveryPending && client.Calls == 1);
        session.SelectVolume(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.RefreshVolumes(); await Load(session, engine, A, B);
        (await clock.Next(VolumeEventRetry)).Release();
        var events = await OpenVolumeEvents(engine);
        var reopenedDetail = await engine.NextInspection();
        Check(session.VolumeSnapshot.Inspection is null && client.Calls == 1 && session.Snapshot.EventFailure is null);
        reopenedDetail.Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var fresh = await EventVolumeList(engine, clock);
        await events.Emit(VolumeEvent(A, "unmount")); await events.Barrier();
        fresh.SucceedNames(A, B); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        (await EventVolumeList(engine, clock)).SucceedNames(B);
        await State(session, s => s.SelectedVolumeName is null);
        events.End(new(EngineEventFailureKind.MalformedEvent, "Safe malformed-event failure"));
        await Until(() => session.Snapshot.EventFailure?.Kind == EngineEventFailureKind.MalformedEvent);
        (await EventVolumeList(engine, clock)).SucceedNames(B);
        await State(session, s => s.Volumes.Count == 1);
        session.RefreshVolumes(); await Load(session, engine, B);
        Check(session.EngineState == EngineConnectionState.Connected && client.Calls == 1);
    }

    public static async Task VolumeObservationOpeningFallbackAndJoin()
    {
        var engine = new EventVolumeConnection("late-opening", honorOpeningCancellation: false); var clock = new VolumeEventClock();
        var session = EventSession(new ControlledClient(engine), clock, new() { OperationTimeout = TimeSpan.FromMilliseconds(120), MaximumRecoveryAttempts = 0 });
        session.Start(); var opening = await engine.NextOpening();
        Check(engine.ListCalls == 0, "Volume snapshot waits for observation establishment");
        var initial = await engine.NextList();
        Check(opening.Token.IsCancellationRequested, "Opening deadline provides bounded snapshot fallback even for a non-cooperative opener");
        initial.SucceedNames(A); await State(session, s => s.State == ResourceReadState.Ready);
        session.RefreshVolumes(); await Load(session, engine, B);
        var disconnect = session.DisconnectAsync();
        Check(!disconnect.IsCompleted && engine.DisposeCount == 0);
        var late = new VolumeObservation(); opening.Succeed(late);
        await disconnect.WaitAsync(Limit);
        Check(late.DisposeCount == 1 && engine.DisposeCount == 1 && !engine.DisposedWithActiveRead
            && session.VolumeSnapshot.State == ResourceReadState.Unavailable);
        await session.DisposeAsync();
    }

    public static async Task VolumeReconnectOverlapAndJoinedReplacement()
    {
        var old = new EventVolumeConnection("old", honorReadCancellation: false);
        var fresh = new EventVolumeConnection("new"); var clock = new VolumeEventClock(); var client = new ControlledClient(old, fresh);
        await using var session = EventSession(client, clock);
        session.Start(); var events = await OpenVolumeEvents(old); await Load(session, old, A);
        session.SelectVolume(A); var stale = await old.NextInspection();
        await events.Emit(VolumeEvent(A, nanos: 100_123_456_789)); await events.Barrier();
        long generation = session.VolumeSnapshot.ConnectionGeneration;
        events.End(new(EngineEventFailureKind.TransportLoss, "Continuity lost"));
        await State(session, s => s.State == ResourceReadState.Unavailable);
        Check(stale.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1);
        stale.Succeed(Detail(A));
        await Until(() => session.EngineState == EngineConnectionState.Lost);
        (await clock.Next(TimeSpan.FromSeconds(3))).Release();
        var resumed = await fresh.NextOpening();
        Check(resumed.Since == new EngineEventCursor(98, 123_456_689), "Volume events participate in exact shared cursor and fractional overlap");
        var overlap = new VolumeObservation(); resumed.Succeed(overlap);
        var initial = await fresh.NextList();
        await overlap.Emit(VolumeEvent(B, nanos: 100_123_456_789)); await overlap.Emit(VolumeEvent(B, nanos: 100_123_456_789));
        await overlap.Barrier(); initial.SucceedNames(B);
        (await EventVolumeList(fresh, clock)).SucceedNames(C);
        await State(session, s => s.Volumes.Single().Name == C);
        Check(old.DisposeCount == 1 && events.DisposeCount == 1 && fresh.OpenCalls == 1
            && session.VolumeSnapshot.ConnectionGeneration > generation && session.VolumeSnapshot.Inspection is null);
        session.SelectVolume(C); var late = await fresh.NextInspection();
        var disconnect = session.DisconnectAsync(); await disconnect.WaitAsync(Limit);
        Check(late.Token.IsCancellationRequested && overlap.DisposeCount == 1 && fresh.DisposeCount == 1);
    }

    public static async Task VolumeObservationsCoexistWithCommandsAndLogs()
    {
        var engine = new AllCapabilitiesConnection("coexistence"); var clock = new VolumeEventClock();
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
        await engine.Events.Emit(VolumeEvent(A, "delete")); await engine.Events.Barrier();
        (await EventVolumeList(engine, clock)).SucceedNames(B);
        await State(session, s => s.Volumes.Single().Name == B);
        Check(!engine.CommandToken.IsCancellationRequested && !engine.LogToken.IsCancellationRequested
            && !engine.ImageCommandToken.IsCancellationRequested && !imageCommand.IsCompleted
            && !engine.EventToken.IsCancellationRequested && !command.IsCompleted
            && engine.ContainerListCalls == containerReads && engine.ImageListCalls == 1 && engine.EventCalls == 1
            && session.ContainerSnapshot.State == ResourceReadState.Ready
            && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming,
            "Volume events use one shared observation without disturbing containers, commands or Logs");
        // Shared container observations still reconcile independently on the same subscription.
        await engine.Events.Emit(new("container", "start", AllCapabilitiesConnection.ContainerId,
            null, null, new(100, 100_123_456_789))); await engine.Events.Barrier();
        (await clock.Next(VolumeBatch)).Release();
        await Until(() => engine.ContainerListCalls > containerReads);
        Check(engine.ListCalls == 2 && engine.EventCalls == 1 && !engine.CommandToken.IsCancellationRequested);
        await engine.Events.Emit(new("image", "future-action", AllCapabilitiesConnection.ImageId,
            null, null, new(100, 100_123_456_789))); await engine.Events.Barrier();
        (await clock.Next(VolumeBatch)).Release();
        await Until(() => engine.ImageListCalls > 1);
        Check(engine.ListCalls == 2 && engine.EventCalls == 1 && !engine.ImageCommandToken.IsCancellationRequested);
        await session.DisconnectAsync().WaitAsync(Limit); await Task.WhenAll(command, imageCommand).WaitAsync(Limit);
        Check(engine.Events.Disposed && engine.Logs.Disposed && engine.DisposeCount == 1 && !engine.DisposedWithActiveRead);
    }

    public static async Task VolumeInvalidatedDeadlineAndReplacementCleanup()
    {
        // Event supersession fences content without disabling the active request's deadline.
        var expired = new EventVolumeConnection("expired", honorReadCancellation: false); var clock = new VolumeEventClock();
        var timeout = EventSession(new ControlledClient(expired), clock, new() { OperationTimeout = TimeSpan.FromMilliseconds(150), MaximumRecoveryAttempts = 0 });
        timeout.Start(); var observations = await OpenVolumeEvents(expired); await Load(timeout, expired, A);
        timeout.SelectVolume(A); var stalled = await expired.NextInspection();
        await observations.Emit(VolumeEvent(A)); await observations.Barrier();
        await State(timeout, s => s.State == ResourceReadState.Unavailable);
        Check(stalled.Token.IsCancellationRequested && expired.DisposeCount == 0
            && timeout.VolumeSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.TransportLoss);
        stalled.Succeed(Detail(A));
        await Until(() => timeout.EngineState == EngineConnectionState.Lost);
        Check(expired.DisposeCount == 1 && observations.DisposeCount == 1 && timeout.VolumeSnapshot.Inspection is null);
        await timeout.DisposeAsync();

        var broken = new EventVolumeConnection("broken", honorReadCancellation: false); var transportClock = new VolumeEventClock();
        var lost = EventSession(new ControlledClient(broken), transportClock, new() { MaximumRecoveryAttempts = 0 });
        lost.Start(); var brokenEvents = await OpenVolumeEvents(broken); await Load(lost, broken, A);
        lost.SelectVolume(A); var obsolete = await broken.NextInspection();
        await brokenEvents.Emit(VolumeEvent(A)); await brokenEvents.Barrier();
        obsolete.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        await Until(() => lost.EngineState == EngineConnectionState.Lost);
        Check(lost.VolumeSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.TransportLoss
            && broken.DisposeCount == 1 && brokenEvents.DisposeCount == 1,
            "Event-obsolete content still carries valid current connection continuity evidence");
        await lost.DisposeAsync();

        foreach (var action in new[] { "retry", "disconnect", "dispose" })
        {
            var old = new EventVolumeConnection(action, honorReadCancellation: false);
            var fresh = new EventVolumeConnection("replacement"); var timing = new VolumeEventClock(); var client = new ControlledClient(old, fresh);
            var session = EventSession(client, timing);
            session.Start(); var events = await OpenVolumeEvents(old); await Load(session, old, A);
            long generation = session.VolumeSnapshot.ConnectionGeneration;
            session.SelectVolume(A); var late = await old.NextInspection();
            await events.Emit(VolumeEvent(A)); await events.Barrier();
            Task? ending = null;
            if (action == "retry") session.Retry();
            else ending = action == "disconnect" ? session.DisconnectAsync() : session.DisposeAsync().AsTask();
            Check(late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1
                && session.VolumeSnapshot.State == ResourceReadState.Unavailable);
            if (ending is not null) Check(!ending.IsCompleted);
            late.Succeed(Detail(A, "{\"late\":true}"));
            if (ending is not null) await ending.WaitAsync(Limit);
            else
            {
                var opening = await fresh.NextOpening();
                Check(opening.Since == new EngineEventCursor(98, 123_456_689));
                opening.Succeed(new VolumeObservation()); await Load(session, fresh, B);
                Check(session.VolumeSnapshot.ConnectionGeneration > generation && session.VolumeSnapshot.Inspection is null);
            }
            Check(old.DisposeCount == 1 && events.DisposeCount == 1 && !old.DisposedWithActiveRead
                && session.VolumeSnapshot.Inspection is null);
            await session.DisposeAsync();
        }
    }

    private sealed class VolumeGatedClient : IEngineConnectionClient
    {
        public TaskCompletionSource<EngineConnectionAttempt> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
            => await Ready.Task.WaitAsync(cancellationToken);
    }

    private sealed class EventVolumeConnection(string version, bool honorReadCancellation = true, bool honorOpeningCancellation = true)
        : ControlledConnection(version, honorReadCancellation), IEngineEventSource
    {
        private readonly Channel<VolumeOpening> openings = Channel.CreateUnbounded<VolumeOpening>();
        public int OpenCalls;
        public override EngineMetadata Metadata => base.Metadata with { EngineId = "same-volume-engine" };
        public async Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref OpenCalls); var opening = new VolumeOpening(since, cancellationToken); openings.Writer.TryWrite(opening);
            return await (honorOpeningCancellation ? opening.Completion.Task.WaitAsync(cancellationToken) : opening.Completion.Task);
        }
        public async Task<VolumeOpening> NextOpening() => await openings.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class VolumeOpening(EngineEventCursor? since, CancellationToken token)
    {
        public EngineEventCursor? Since { get; } = since;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineEventSubscriptionAttempt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(VolumeObservation observation) => Completion.SetResult(EngineEventSubscriptionAttempt.Subscribed(observation));
        public void Fail(EngineEventFailure failure) => Completion.SetResult(EngineEventSubscriptionAttempt.Failed(failure));
    }
    private class VolumeObservation : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(32)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object sync = new();
        private TaskCompletionSource progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sent, consumed;
        public int DisposeCount, MaximumBuffered;
        public VolumeObservation() => Events = new VolumeObservedReader(this);
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
        private sealed class VolumeObservedReader(VolumeObservation owner) : ChannelReader<EngineEvent>
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
    private sealed class VolumeEventClock : IConnectionDelay
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
