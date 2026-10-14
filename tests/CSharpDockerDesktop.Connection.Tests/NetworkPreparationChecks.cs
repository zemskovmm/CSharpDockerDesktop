using System.Collections.ObjectModel;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class NetworkBrowsingChecks
{
    private static NetworkDeletionTarget PreparationTarget(DesktopSession session, string? id = null)
        => new(session.NetworkSnapshot.ConnectionGeneration, id ?? A);
    private static NetworkInspection EmptyNetwork(string? id = null) => Detail(id ?? A) with
        { Containers = new Dictionary<string, NetworkAttachment>() };
    private static async Task PreparationState(DesktopSession session, Func<NetworkDeletionPreparationSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? _, EventArgs __)
        { if (session.NetworkDeletionPreparation is { } state && predicate(state)) reached.TrySetResult(); }
        session.NetworkDeletionPreparationChanged += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.NetworkDeletionPreparationChanged -= Observe; }
    }
    private static async Task<NetworkDeletionPreparationSnapshot> Prepare(DesktopSession session, ControlledConnection engine,
        NetworkInspection evidence, string? id = null)
    {
        var task = session.PrepareNetworkDeletionAsync(PreparationTarget(session, id));
        var call = await engine.NextInspection(); Check(call.Id == (id ?? A)); call.Succeed(evidence);
        return await task.WaitAsync(Limit);
    }

    public static async Task NetworkPreparationEvidenceAndEligibility()
    {
        const string opaque = "  Private/Δ\\cluster:%?#😀  ";
        var engine = new PreparationCommandConnection();
        await using var session = new DesktopSession(new ControlledClient(engine), delay: new ControlledDelay());
        session.NetworkDeletionPreparationChanged += (_, _) => throw new InvalidOperationException("private observer");
        session.Start(); await Load(session, engine, A, B, opaque);
        var empty = await Prepare(session, engine, EmptyNetwork());
        Check(empty.IsPrecheckEligible && session.IsNetworkDeletionPreparationCurrent(empty.Identity));
        var attachments = new Dictionary<string, NetworkAttachment>(StringComparer.Ordinal)
        { [opaque] = new("private container", "endpoint", "mac", "ipv4", null), ["opaque-driver-endpoint"] = new(null, null, null, null, null) };
        var labels = new Dictionary<string, string> { ["private"] = "secret" };
        var attached = await Prepare(session, engine, EmptyNetwork(opaque) with { Containers = attachments, Labels = labels }, opaque);
        attachments.Clear(); labels.Clear();
        Check(attached.HasUsableEvidence && !attached.IsPrecheckEligible
            && attached.Reasons.SequenceEqual(new[] { NetworkDeletionBlockReason.AttachmentsRemain })
            && attached.Inspection!.Containers is ReadOnlyDictionary<string, NetworkAttachment>
            && attached.Inspection.Containers[opaque].EndpointId == "endpoint"
            && attached.Inspection.Containers["opaque-driver-endpoint"].Name is null
            && attached.Inspection.Labels!["private"] == "secret" && attached.Inspection.RawJson == Raw);
        var unavailable = await Prepare(session, engine, Detail(A));
        Check(unavailable.HasUsableEvidence && unavailable.Inspection!.Containers is null
            && unavailable.Reasons.Contains(NetworkDeletionBlockReason.AttachmentsUnavailable));
        foreach (var name in new string?[] { null, "", " ", "bad\nname", "\ud800" })
        {
            var missing = await Prepare(session, engine, EmptyNetwork() with { Name = name });
            Check(!missing.IsPrecheckEligible && missing.Reasons.Contains(NetworkDeletionBlockReason.NameUnavailable));
        }
        foreach (string name in new[] { "bridge", "host", "none" })
        {
            // Either source's known built-in evidence must block, even if the other contradicts it.
            var builtIn = await Prepare(session, engine, EmptyNetwork() with { Name = name });
            Check(builtIn.HasUsableEvidence && builtIn.Reasons.Contains(NetworkDeletionBlockReason.BuiltInNetwork));
            session.RefreshNetworks(); (await engine.NextList()).Succeed(new[] { Row(A) with { Name = name } });
            await State(session, s => s.IsAuthoritative);
            builtIn = await Prepare(session, engine, EmptyNetwork());
            Check(!builtIn.IsPrecheckEligible && builtIn.Reasons.Contains(NetworkDeletionBlockReason.BuiltInNetwork));
            session.RefreshNetworks(); await Load(session, engine, A, B, opaque);
        }
        var badAttachments = await Prepare(session, engine, EmptyNetwork() with
            { Containers = new Dictionary<string, NetworkAttachment> { ["bad\nkey"] = null! } });
        Check(badAttachments.Reasons.Contains(NetworkDeletionBlockReason.AttachmentEvidenceInvalid));
        foreach (var mismatch in new[] { opaque.Trim(), opaque.ToLowerInvariant(), opaque[..10], B })
        {
            var bad = await Prepare(session, engine, EmptyNetwork(mismatch), opaque);
            Check(bad.Phase == NetworkPreparationPhase.Failed && bad.Failure?.ResourceFailure?.Kind == EngineResourceFailureKind.MalformedData
                && bad.Inspection!.Id == mismatch && !bad.IsPrecheckEligible);
        }
        Check(engine.NetworkCommandCalls == 0);
    }

    public static async Task NetworkPreparationAdmissionFreshnessAndSelection()
    {
        var engine = new ControlledConnection("selection");
        await using var session = new DesktopSession(new ControlledClient(engine), delay: new ControlledDelay());
        session.Start(); await Load(session, engine, A, B);
        session.SelectNetwork(A); (await engine.NextInspection()).Succeed(EmptyNetwork());
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        int calls = engine.InspectionCalls;
        var pending = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var fresh = await engine.NextInspection();
        Check(engine.InspectionCalls == calls + 1 && session.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Loading);
        session.SelectNetwork(B); (await engine.NextInspection()).Succeed(EmptyNetwork(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        fresh.Succeed(EmptyNetwork()); var captured = await pending.WaitAsync(Limit);
        Check(captured.IsPrecheckEligible && captured.Target.Id == A && session.NetworkSnapshot.SelectedNetworkId == B);
        foreach (var bad in new[] { "", ".", "..", "bad\nidentity", "\ud800", new string('x', 4097) })
            Check((await session.PrepareNetworkDeletionAsync(PreparationTarget(session, bad))).Failure?.Kind == NetworkPreparationFailureKind.InvalidRequest);
        Check((await session.PrepareNetworkDeletionAsync(PreparationTarget(session, A[..12]))).Failure?.Kind == NetworkPreparationFailureKind.TargetAbsent);
        Check((await session.PrepareNetworkDeletionAsync(new(captured.Target.ConnectionGeneration - 1, A))).Failure?.Kind == NetworkPreparationFailureKind.StaleConnection);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check((await session.PrepareNetworkDeletionAsync(PreparationTarget(session), canceled.Token)).Failure?.Kind == NetworkPreparationFailureKind.Canceled);
        Check(ReferenceEquals(session.NetworkDeletionPreparation, captured));
        await session.CloseNetworkDeletionPreparationAsync(new(captured.Identity.PreparationId + 1, 1));
        Check(session.IsNetworkDeletionPreparationCurrent(captured.Identity));
        session.InvalidateNetworkDeletionPreparation(captured.Identity);
        Check(!session.IsNetworkDeletionPreparationCurrent(captured.Identity)
            && session.NetworkDeletionPreparation!.Failure?.Kind == NetworkPreparationFailureKind.ExplicitInvalidation);
        var renewed = await Prepare(session, engine, EmptyNetwork());
        session.InvalidateNetworkDeletionPreparation(captured.Identity);
        Check(session.IsNetworkDeletionPreparationCurrent(renewed.Identity));
        await session.CloseNetworkDeletionPreparationAsync(renewed.Identity with { Revision = 1 });
        Check(!session.IsNetworkDeletionPreparationCurrent(renewed.Identity));
        await using var missing = new DesktopSession(new ControlledClient(new MissingConnection()), delay: new ControlledDelay());
        missing.Start(); await Until(() => missing.NetworkSnapshot.ConnectionGeneration > 0);
        Check((await missing.PrepareNetworkDeletionAsync(PreparationTarget(missing))).Failure?.Kind == NetworkPreparationFailureKind.CapabilityUnavailable);
    }

    public static async Task NetworkPreparationEventFencesAndGaps()
    {
        var engine = new EventNetworkConnection("event-preparation", false); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); var events = await OpenNetworkEvents(engine); await Load(session, engine, A, B);
        await events.Emit(NetworkEvent(A)); await events.Barrier();
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        var captured = await Prepare(session, engine, EmptyNetwork());
        foreach (string unrelated in new[] { B, A[..12], A.ToUpperInvariant() })
        {
            await events.Emit(NetworkEvent(unrelated)); await events.Barrier();
            Check(session.IsNetworkDeletionPreparationCurrent(captured.Identity));
            (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        }
        // Fence a queued target before its Inspect can be invoked.
        var blocker = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var blockedRead = await engine.NextInspection();
        var waiting = session.PrepareNetworkDeletionAsync(PreparationTarget(session, B));
        int beforeWaiting = engine.InspectionCalls;
        await events.Emit(NetworkEvent(B)); await events.Barrier();
        Check(session.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Invalidated);
        blockedRead.Succeed(EmptyNetwork()); await blocker.WaitAsync(Limit);
        Check((await waiting.WaitAsync(Limit)).Failure?.Kind == NetworkPreparationFailureKind.ObservedChange && engine.InspectionCalls == beforeWaiting);
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        foreach (var id in new string?[] { A, null, "", " ", ".", "..", "bad\nid", "\ud800", new('x', 4097) })
        {
            foreach (bool failLate in new[] { false, true })
            {
                var task = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var call = await engine.NextInspection();
                int notifications = 0; void Count(object? _, EventArgs __) => Interlocked.Increment(ref notifications);
                session.NetworkDeletionPreparationChanged += Count;
                for (int index = 0; index < 20; index++) await events.Emit(NetworkEvent(id));
                await events.Barrier(); session.NetworkDeletionPreparationChanged -= Count;
                Check(session.NetworkDeletionPreparation!.Failure?.Kind == NetworkPreparationFailureKind.ObservedChange
                    && !call.Token.IsCancellationRequested && notifications == 1);
                if (failLate) call.Fail(Failure(EngineResourceFailureKind.NotFound)); else call.Succeed(EmptyNetwork());
                Check((await task.WaitAsync(Limit)).Phase == NetworkPreparationPhase.Invalidated);
                (await EventNetworkList(engine, clock)).SucceedNames(A, B);
            }
        }
        captured = await Prepare(session, engine, EmptyNetwork());
        await events.Emit(NetworkEvent(A)); await events.Barrier();
        Check(!session.IsNetworkDeletionPreparationCurrent(captured.Identity)
            && session.NetworkDeletionPreparation!.Inspection is not null);
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        captured = await Prepare(session, engine, EmptyNetwork());
        events.End(new(EngineEventFailureKind.MalformedEvent, "private observation gap"));
        await PreparationState(session, s => s.Phase == NetworkPreparationPhase.Invalidated);
        Check(!session.IsNetworkDeletionPreparationCurrent(captured.Identity));
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        (await clock.Next(NetworkEventRetry)).Release();
        var reopening = await engine.NextOpening(); var reopened = new JoinedGapObservation(); reopening.Succeed(reopened);
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        Check(session.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Invalidated);
        captured = await Prepare(session, engine, EmptyNetwork());
        Check(captured.IsPrecheckEligible);
        reopened.End(new(EngineEventFailureKind.MalformedEvent, "gap before slow disposal"));
        await reopened.DisposalStarted.Task.WaitAsync(Limit);
        try { Check(!session.IsNetworkDeletionPreparationCurrent(captured.Identity), "Gap fences before non-cooperative subscription disposal"); }
        finally { reopened.ReleaseDisposal.TrySetResult(); }
    }

    public static async Task NetworkPreparationDisappearanceAndFailures()
    {
        var engine = new ControlledConnection("disappearance", false);
        await using var session = new DesktopSession(new ControlledClient(engine), delay: new ControlledDelay());
        session.Start(); await Load(session, engine, A, B);
        var captured = await Prepare(session, engine, EmptyNetwork());
        session.RefreshNetworks(); (await engine.NextList()).Fail(Failure(EngineResourceFailureKind.AccessDenied));
        await State(session, s => s.State == ResourceReadState.Failed);
        Check(session.IsNetworkDeletionPreparationCurrent(captured.Identity));
        session.RefreshNetworks(); await Load(session, engine, B);
        Check(!session.IsNetworkDeletionPreparationCurrent(captured.Identity)
            && session.NetworkDeletionPreparation!.Failure?.Kind == NetworkPreparationFailureKind.TargetAbsent);
        session.RefreshNetworks(); await Load(session, engine, A, B);
        var task = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var call = await engine.NextInspection();
        session.SelectNetwork(A); (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.NotFound));
        await PreparationState(session, s => s.Failure?.Kind == NetworkPreparationFailureKind.TargetAbsent);
        call.Succeed(EmptyNetwork()); Check((await task.WaitAsync(Limit)).Phase == NetworkPreparationPhase.Invalidated);
        session.RefreshNetworks(); await Load(session, engine, A, B);
        foreach (var kind in new[] { EngineResourceFailureKind.AccessDenied, EngineResourceFailureKind.Conflict,
            EngineResourceFailureKind.InvalidRequest, EngineResourceFailureKind.DaemonFailure, EngineResourceFailureKind.MalformedData })
        {
            task = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var failure = Failure(kind);
            (await engine.NextInspection()).Fail(failure); var failed = await task.WaitAsync(Limit);
            Check(failed.Phase == NetworkPreparationPhase.Failed && ReferenceEquals(failed.Failure?.ResourceFailure, failure) && !failed.IsPrecheckEligible);
        }
        task = session.PrepareNetworkDeletionAsync(PreparationTarget(session));
        (await engine.NextInspection()).Completion.TrySetException(new InvalidOperationException("private throw"));
        Check((await task.WaitAsync(Limit)).Failure?.ResourceFailure?.Description.Contains("private") == false);
        task = session.PrepareNetworkDeletionAsync(PreparationTarget(session));
        (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.NotFound));
        Check((await task.WaitAsync(Limit)).Failure?.ResourceFailure?.Kind == EngineResourceFailureKind.NotFound
            && session.NetworkSnapshot.Networks.All(row => row.Id != A));
    }

    public static async Task NetworkPreparationCapacityCancellationAndJoin()
    {
        var engine = new ControlledConnection("capacity", false);
        await using var session = new DesktopSession(new ControlledClient(engine), delay: new ControlledDelay());
        session.Start(); await Load(session, engine, A, B);
        var first = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var call = await engine.NextInspection();
        using var cancel = new CancellationTokenSource();
        var queued = session.PrepareNetworkDeletionAsync(PreparationTarget(session, B), cancel.Token);
        var retained = session.NetworkDeletionPreparation;
        Check(call.Token.IsCancellationRequested && !first.IsCompleted && !queued.IsCompleted);
        for (int index = 0; index < 100; index++)
            Check((await session.PrepareNetworkDeletionAsync(PreparationTarget(session))).Failure?.Kind == NetworkPreparationFailureKind.CapacityExceeded);
        Check(ReferenceEquals(session.NetworkDeletionPreparation, retained) && engine.InspectionCalls == 1);
        cancel.Cancel(); Check(session.NetworkDeletionPreparation!.Failure?.Kind == NetworkPreparationFailureKind.Canceled);
        call.Succeed(EmptyNetwork()); Check((await first.WaitAsync(Limit)).Failure?.Kind == NetworkPreparationFailureKind.Superseded);
        Check((await queued.WaitAsync(Limit)).Failure?.Kind == NetworkPreparationFailureKind.Canceled && engine.InspectionCalls == 1);
        // Superseding cancellation executes this registration synchronously inside admission,
        // canceling the new caller before its linked-token callback is registered.
        var synchronous = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var synchronousCall = await engine.NextInspection();
        using var synchronousCaller = new CancellationTokenSource();
        using var synchronousRegistration = synchronousCall.Token.Register(synchronousCaller.Cancel);
        var synchronouslyCanceled = session.PrepareNetworkDeletionAsync(PreparationTarget(session, B), synchronousCaller.Token);
        Check(session.NetworkDeletionPreparation!.Failure?.Kind == NetworkPreparationFailureKind.Canceled);
        synchronousCall.Succeed(EmptyNetwork()); await synchronous.WaitAsync(Limit);
        Check((await synchronouslyCanceled.WaitAsync(Limit)).Failure?.Kind == NetworkPreparationFailureKind.Canceled && engine.InspectionCalls == 2);
        var active = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); call = await engine.NextInspection();
        var identity = session.NetworkDeletionPreparation!.Identity;
        var closing = session.CloseNetworkDeletionPreparationAsync(identity);
        Check(!closing.IsCompleted && call.Token.IsCancellationRequested && !session.IsNetworkDeletionPreparationCurrent(identity));
        call.Succeed(EmptyNetwork()); await closing.WaitAsync(Limit);
        Check((await active).Phase == NetworkPreparationPhase.Invalidated);
        active = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); call = await engine.NextInspection();
        using var registration = call.Token.Register(() => Check(session.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Invalidated));
        closing = session.DisconnectAsync(); Check(!closing.IsCompleted && engine.DisposeCount == 0);
        call.Fail(Failure(EngineResourceFailureKind.NotFound)); await active.WaitAsync(Limit); await closing.WaitAsync(Limit);
        Check(engine.DisposeCount == 1 && !engine.DisposedWithActiveRead && engine.MaximumConcurrent == 1);
    }

    public static async Task NetworkPreparationDeadlineRecoveryAndReconnection()
    {
        foreach (string mode in new[] { "deadline", "waiting-deadline", "event-deadline", "transport", "retry", "dispose" })
        {
            var old = new ControlledConnection("old", false); var fresh = new ControlledConnection("fresh");
            var clock = new ControlledDelay(); var client = new ControlledClient(old, fresh);
            var session = new DesktopSession(client, new() { OperationTimeout = TimeSpan.FromMilliseconds(600) }, clock);
            session.Start(); await Load(session, old, A, B); long generation = session.NetworkSnapshot.ConnectionGeneration;
            var pending = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var call = await old.NextInspection();
            Task<NetworkDeletionPreparationSnapshot>? replacement = mode == "waiting-deadline"
                ? session.PrepareNetworkDeletionAsync(PreparationTarget(session, B)) : null;
            Task? closing = null;
            if (mode == "event-deadline") session.InvalidateNetworkDeletionPreparation(session.NetworkDeletionPreparation!.Identity);
            if (mode == "transport") call.Fail(Failure(EngineResourceFailureKind.TransportLoss));
            else if (mode == "retry") session.Retry();
            else if (mode == "dispose") closing = session.DisposeAsync().AsTask();
            if (mode.Contains("deadline"))
            {
                await State(session, s => s.State == ResourceReadState.Unavailable);
                Check(old.DisposeCount == 0 && call.Token.IsCancellationRequested && !pending.IsCompleted && client.Calls == 1, mode);
                if (mode == "deadline") Check(session.NetworkDeletionPreparation!.Failure?.Kind == NetworkPreparationFailureKind.Deadline);
                if (mode == "waiting-deadline") Check(old.InspectionCalls == 1);
            }
            else if (mode != "transport")
            {
                await State(session, s => s.State == ResourceReadState.Unavailable);
                Check(old.DisposeCount == 0 && call.Token.IsCancellationRequested && client.Calls == 1, mode);
            }
            if (mode != "transport") call.Succeed(EmptyNetwork());
            Check(!(await pending.WaitAsync(Limit)).IsPrecheckEligible, mode);
            if (replacement is not null) Check(!(await replacement.WaitAsync(Limit)).IsPrecheckEligible && old.InspectionCalls == 1);
            if (closing is not null) await closing.WaitAsync(Limit);
            else
            {
                if (mode != "retry")
                {
                    await Until(() => session.EngineState == EngineConnectionState.Lost);
                    PendingDelay recovery;
                    do { recovery = await clock.Next(); } while (recovery.Duration != TimeSpan.FromSeconds(3) || recovery.Token.IsCancellationRequested);
                    recovery.Release();
                }
                await Load(session, fresh, A);
                Check(fresh.InspectionCalls == 0 && session.NetworkSnapshot.ConnectionGeneration > generation
                    && !session.IsNetworkDeletionPreparationCurrent(session.NetworkDeletionPreparation!.Identity));
                Check((await session.PrepareNetworkDeletionAsync(new(generation, A))).Failure?.Kind == NetworkPreparationFailureKind.StaleConnection);
                Check((await Prepare(session, fresh, EmptyNetwork())).IsPrecheckEligible);
            }
            await session.DisposeAsync(); Check(old.DisposeCount == 1 && !old.DisposedWithActiveRead, mode);
        }
    }

    public static async Task NetworkPreparationCoexistence()
    {
        var engine = new PreparationCommandConnection(); var clock = new NetworkEventClock();
        await using var session = EventSession(new ControlledClient(engine), clock);
        session.Start(); await Load(session, engine, A, B);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var pending = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var call = await engine.NextInspection();
        session.RefreshNetworks(); var list = await engine.NextList();
        Check(engine.MaximumConcurrent == 2 && engine.EventCalls == 1 && !engine.EventToken.IsCancellationRequested && !engine.LogToken.IsCancellationRequested);
        list.SucceedNames(A, B); await State(session, s => s.IsAuthoritative);
        session.SelectNetwork(B); (await engine.NextInspection()).Succeed(EmptyNetwork(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.RefreshContainers(); session.RefreshImages(); session.RefreshVolumes();
        call.Succeed(EmptyNetwork()); Check((await pending.WaitAsync(Limit)).IsPrecheckEligible);
        Check(engine.NetworkCommandCalls == 0 && engine.CommandCalls == 0 && engine.ImageCommandCalls == 0
            && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming && engine.MaximumConcurrent == 2);
    }

    private sealed class JoinedGapObservation : NetworkObservation
    {
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask DisposeAsync()
        {
            DisposalStarted.TrySetResult();
            await ReleaseDisposal.Task.ConfigureAwait(false);
            await base.DisposeAsync();
        }
    }

    private sealed class PreparationCommandConnection() : AllCapabilitiesConnection("preparation", false), INetworkCommands
    {
        public int NetworkCommandCalls;
        public Task<NetworkCommandResult> DeleteNetworkAsync(string id, CancellationToken cancellationToken = default)
        { ++NetworkCommandCalls; throw new InvalidOperationException("Preparation must not dispatch deletion"); }
    }
}
