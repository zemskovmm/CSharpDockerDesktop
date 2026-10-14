using System.Collections.ObjectModel;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class NetworkBrowsingChecks
{
    private static readonly TimeSpan NetworkFollowup = TimeSpan.FromMilliseconds(29);
    private static DesktopSession NetworkDeletionSession(IEngineConnectionClient client, NetworkEventClock clock,
        int observations = 2, int commandMilliseconds = 30000, int readMilliseconds = 10000, int recovery = 0)
        => new(client, new() { OperationTimeout = TimeSpan.FromMilliseconds(readMilliseconds), MaximumRecoveryAttempts = recovery }, clock,
            reconciliation: new() { BatchInterval = NetworkBatch, RetryDelay = NetworkEventRetry },
            networkOperationOptions: new() { CommandTimeout = TimeSpan.FromMilliseconds(commandMilliseconds),
                FollowupInterval = NetworkFollowup, MaximumObservations = observations });
    private static async Task ReadyNetworkDeletion(DesktopSession session, NetworkDeleteConnection engine, params string[] ids)
    {
        session.Start(); await Load(session, engine, ids.Length == 0 ? new[] { A, B } : ids);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready);
        await engine.Events.Barrier();
    }
    private static async Task<NetworkOperationRequest> NetworkDeletionRequest(DesktopSession session, ControlledConnection engine, string? id = null)
    {
        var prepared = await Prepare(session, engine, EmptyNetwork(id), id);
        Check(prepared.IsPrecheckEligible);
        return new(prepared.Target, prepared.Identity);
    }
    private static async Task NetworkOperationState(DesktopSession session, Func<NetworkOperationSnapshot, bool> predicate)
        => await Until(() => session.NetworkOperation is { } value && predicate(value));
    private static async Task ObserveNetwork(ControlledConnection engine, bool present, string? id = null, NetworkInspection? evidence = null)
    {
        var target = id ?? A;
        (await engine.NextList()).SucceedNames(present ? new[] { target, B } : new[] { B });
        var inspect = await engine.NextInspection(); Check(inspect.Id == target);
        if (present) inspect.Succeed(evidence ?? EmptyNetwork(target));
        else inspect.Fail(Failure(EngineResourceFailureKind.NotFound));
    }

    public static async Task NetworkDeletionAtomicAdmissionAndReplay()
    {
        var engine = new NetworkDeleteConnection(); var clock = new NetworkEventClock();
        await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
        await ReadyNetworkDeletion(session, engine);
        var request = await NetworkDeletionRequest(session, engine);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); Check((await session.ExecuteNetworkOperationAsync(request, canceled.Token)).Rejection == NetworkOperationRejection.Canceled);
        }
        Check((await session.ExecuteNetworkOperationAsync(request with { Preparation = request.Preparation with { Revision = 1 } })).Rejection
            == NetworkOperationRejection.PreparationNotCurrent);
        Check((await session.ExecuteNetworkOperationAsync(request with { Target = request.Target with { Id = B } })).Rejection
            == NetworkOperationRejection.PreparationNotCurrent);
        Check(engine.DeleteCalls == 0 && session.IsNetworkDeletionPreparationCurrent(request.Preparation));
        // Every concurrent contender uses the same exact receipt and gets the one admitted completion.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        { await barrier.Task; return session.ExecuteNetworkOperationAsync(request); })).ToArray();
        barrier.SetResult(); var receipts = await Task.WhenAll(contenders).WaitAsync(Limit);
        var pending = receipts[0]; Check(receipts.All(item => ReferenceEquals(item, pending)));
        var call = await engine.NextDelete();
        Check(session.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Consumed
            && !session.IsNetworkDeletionPreparationCurrent(request.Preparation));
        for (int index = 0; index < 64; ++index)
        {
            Check((await session.ExecuteNetworkOperationAsync(request with { Target = request.Target with { Id = B } })).Rejection == NetworkOperationRejection.CapacityExceeded);
            Check((await session.PrepareNetworkDeletionAsync(PreparationTarget(session, B))).Failure?.Kind == NetworkPreparationFailureKind.CapacityExceeded);
        }
        call.Acknowledge(); await ObserveNetwork(engine, false);
        var result = await pending.WaitAsync(Limit);
        Check(result.Resolution == NetworkOperationResolution.ObservedAbsent && result.Preparation!.Identity == request.Preparation
            && result.Preparation.Phase == NetworkPreparationPhase.Completed && engine.DeleteCalls == 1
            && ReferenceEquals(pending, session.ExecuteNetworkOperationAsync(request)));
        var next = await NetworkDeletionRequest(session, engine, B);
        var second = session.ExecuteNetworkOperationAsync(next); (await engine.NextDelete()).Acknowledge(); await ObserveNetwork(engine, true, B);
        await second.WaitAsync(Limit);
        var retained = session.NetworkOperation;
        Check((await session.ExecuteNetworkOperationAsync(request)).Rejection == NetworkOperationRejection.PreparationNotCurrent
            && ReferenceEquals(retained, session.NetworkOperation) && engine.DeleteCalls == 2);
    }

    public static async Task NetworkDeletionEligibilityAndStaleness()
    {
        var engine = new NetworkDeleteConnection(); var clock = new NetworkEventClock();
        await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
        await ReadyNetworkDeletion(session, engine);
        foreach (var evidence in new[] { EmptyNetwork() with { Name = "bridge" }, EmptyNetwork() with { Name = "host" },
            EmptyNetwork() with { Name = "none" }, Detail(A), EmptyNetwork() with { Name = "contradiction" },
            EmptyNetwork() with { Containers = new Dictionary<string, NetworkAttachment> { [B] = new(null, null, null, null, null) } },
            EmptyNetwork() with { Containers = new Dictionary<string, NetworkAttachment> { ["bad\nkey"] = null! } }, EmptyNetwork(B) })
        {
            var blocked = await Prepare(session, engine, evidence);
            Check((await session.ExecuteNetworkOperationAsync(new(blocked.Target, blocked.Identity))).Rejection == NetworkOperationRejection.PreparationNotCurrent);
            Check(ReferenceEquals(session.NetworkDeletionPreparation, blocked));
        }
        var request = await NetworkDeletionRequest(session, engine);
        await session.CloseNetworkDeletionPreparationAsync(request.Preparation);
        Check((await session.ExecuteNetworkOperationAsync(request)).Rejection == NetworkOperationRejection.PreparationNotCurrent);
        request = await NetworkDeletionRequest(session, engine);
        session.InvalidateNetworkDeletionPreparation(request.Preparation);
        Check((await session.ExecuteNetworkOperationAsync(request)).Rejection == NetworkOperationRejection.PreparationNotCurrent);
        request = await NetworkDeletionRequest(session, engine);
        await NetworkDeletionRequest(session, engine, B);
        Check((await session.ExecuteNetworkOperationAsync(request)).Rejection == NetworkOperationRejection.PreparationNotCurrent);
        var loadingTask = session.PrepareNetworkDeletionAsync(PreparationTarget(session)); var loading = await engine.NextInspection();
        Check((await session.ExecuteNetworkOperationAsync(new(PreparationTarget(session), session.NetworkDeletionPreparation!.Identity))).Rejection
            == NetworkOperationRejection.PreparationNotCurrent);
        loading.Fail(Failure(EngineResourceFailureKind.AccessDenied)); var failed = await loadingTask;
        Check((await session.ExecuteNetworkOperationAsync(new(failed.Target, failed.Identity))).Rejection == NetworkOperationRejection.PreparationNotCurrent);
        request = await NetworkDeletionRequest(session, engine);
        await engine.Events.Emit(NetworkEvent(A)); await engine.Events.Barrier();
        Check((await session.ExecuteNetworkOperationAsync(request)).Rejection == NetworkOperationRejection.PreparationNotCurrent);
        Check((await session.ExecuteNetworkOperationAsync(request with { Target = request.Target with { ConnectionGeneration = -1 } })).Rejection
            == NetworkOperationRejection.StaleConnection);
        Check((await session.ExecuteNetworkOperationAsync(request with { Target = request.Target with { Id = "bad\nid" } })).Rejection
            == NetworkOperationRejection.InvalidRequest && engine.DeleteCalls == 0 && session.NetworkOperation is null);
        (await EventNetworkList(engine, clock)).SucceedNames(A, B);
        var missing = new ControlledConnection("no-command");
        await using var noCommand = NetworkDeletionSession(new ControlledClient(missing), new NetworkEventClock());
        noCommand.Start(); await Load(noCommand, missing, A);
        var useful = await NetworkDeletionRequest(noCommand, missing);
        Check((await noCommand.ExecuteNetworkOperationAsync(useful)).Rejection == NetworkOperationRejection.CapabilityUnavailable
            && noCommand.IsNetworkDeletionPreparationCurrent(useful.Preparation));
    }

    public static async Task NetworkDeletionExactIdentityAndSelection()
    {
        const string exact = "  private/Δ\\cluster:%?#😀  ";
        var engine = new NetworkDeleteConnection(); var clock = new NetworkEventClock();
        await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
        await ReadyNetworkDeletion(session, engine, exact, B);
        var request = await NetworkDeletionRequest(session, engine, exact);
        session.SelectNetwork(B); (await engine.NextInspection()).Succeed(Detail(B)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.ClearNetworkInspection();
        session.NetworkOperationChanged += (_, _) => throw new InvalidOperationException("private observer");
        var pending = session.ExecuteNetworkOperationAsync(request); var command = await engine.NextDelete();
        session.SelectNetwork(null); session.ClearNetworkInspection();
        Check(command.Id == exact && session.NetworkOperation!.Preparation!.Inspection!.RawJson == Raw);
        command.Acknowledge();
        var list = await engine.NextList(); session.SelectNetwork(B); list.SucceedNames(B);
        var target = await engine.NextInspection(); Check(target.Id == exact && !target.Token.IsCancellationRequested);
        session.SelectNetwork(null); target.Fail(Failure(EngineResourceFailureKind.NotFound));
        var result = await pending.WaitAsync(Limit);
        Check(result.Request.Target.Id == exact && result.Resolution == NetworkOperationResolution.ObservedAbsent && engine.DeleteCalls == 1);
    }

    public static async Task NetworkDeletionProtocolOutcomesAndCancellation()
    {
        foreach (var disposition in Enum.GetValues<NetworkCommandDisposition>())
        {
            var engine = new NetworkDeleteConnection(); var clock = new NetworkEventClock();
            await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
            await ReadyNetworkDeletion(session, engine);
            var pending = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, engine)); var call = await engine.NextDelete();
            var original = new NetworkCommandResult(disposition, 404, "private daemon message\noriginal", "private body",
                disposition == NetworkCommandDisposition.DaemonResponse ? new(NetworkCommandFailureKind.NotFound, "not found") : null, true);
            call.Completion.SetResult(original);
            if (disposition == NetworkCommandDisposition.NotDispatched) (await engine.NextList()).SucceedNames(A);
            else await ObserveNetwork(engine, disposition == NetworkCommandDisposition.Acknowledged);
            var result = await pending.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, original) && engine.DeleteCalls == 1
                && result.Resolution == (disposition == NetworkCommandDisposition.NotDispatched ? NetworkOperationResolution.NotDispatched
                    : disposition == NetworkCommandDisposition.Acknowledged ? NetworkOperationResolution.ObservedPresent : NetworkOperationResolution.ObservedAbsent));
        }
        foreach (bool noDispatch in new[] { false, true })
        {
            var engine = new NetworkDeleteConnection(canceledNoDispatch: noDispatch); var clock = new NetworkEventClock();
            await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
            await ReadyNetworkDeletion(session, engine); using var cancel = new CancellationTokenSource();
            var pending = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, engine), cancel.Token);
            var call = await engine.NextDelete(); cancel.Cancel();
            if (noDispatch) (await engine.NextList()).SucceedNames(A); else await ObserveNetwork(engine, false);
            var result = await pending.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, call.CancellationOutcome) && result.Interruption == NetworkOperationInterruption.CallerCanceled
                && result.Resolution == (noDispatch ? NetworkOperationResolution.NotDispatched : NetworkOperationResolution.ObservedAbsent));
        }
    }

    public static async Task NetworkDeletionConflictAttachments()
    {
        foreach (var state in new[] { NetworkAttachmentObservationState.Complete, NetworkAttachmentObservationState.Unavailable,
            NetworkAttachmentObservationState.Malformed, NetworkAttachmentObservationState.Failed })
        {
            var engine = new NetworkDeleteConnection(); var clock = new NetworkEventClock();
            await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
            await ReadyNetworkDeletion(session, engine);
            var pending = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, engine));
            var original = new NetworkCommandResult(NetworkCommandDisposition.DaemonResponse, 409, "private attached", "private conflict",
                new(NetworkCommandFailureKind.Conflict, "Dependency conflict"));
            (await engine.NextDelete()).Completion.SetResult(original);
            int attempts = state == NetworkAttachmentObservationState.Complete ? 1 : 2;
            var attachments = new Dictionary<string, NetworkAttachment> { [B] = new("private", "endpoint", null, null, null) };
            for (int attempt = 0; attempt < attempts; ++attempt)
            {
                if (attempt != 0) (await clock.Next(NetworkFollowup)).Release();
                (await engine.NextList()).SucceedNames(A, B);
                var detail = await engine.NextInspection();
                if (state == NetworkAttachmentObservationState.Failed) detail.Fail(Failure(EngineResourceFailureKind.AccessDenied));
                else detail.Succeed(EmptyNetwork() with { Containers = state == NetworkAttachmentObservationState.Unavailable ? null
                    : state == NetworkAttachmentObservationState.Malformed ? new Dictionary<string, NetworkAttachment> { ["bad\nkey"] = null! } : attachments });
            }
            var result = await pending.WaitAsync(Limit); attachments.Clear();
            Check(ReferenceEquals(result.CommandOutcome, original) && result.AttachmentObservationState == state
                && result.AttachmentRefreshAttempts == attempts && engine.DeleteCalls == 1
                && session.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Consumed
                && result.Resolution == (state == NetworkAttachmentObservationState.Complete ? NetworkOperationResolution.ObservedPresent : NetworkOperationResolution.Unresolved));
            if (state == NetworkAttachmentObservationState.Complete)
                Check(result.RefreshedInspection!.Containers is ReadOnlyDictionary<string, NetworkAttachment>
                    && result.RefreshedInspection.Containers[B].EndpointId == "endpoint");
        }
    }

    public static async Task NetworkDeletionContradictoryAndFailedObservations()
    {
        foreach (var kind in new[] { "contradiction", "identity", "list-failure", "detail-failure" })
        {
            var engine = new NetworkDeleteConnection(); var clock = new NetworkEventClock();
            await using var session = NetworkDeletionSession(new ControlledClient(engine), clock, observations: 1);
            await ReadyNetworkDeletion(session, engine);
            var pending = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, engine)); (await engine.NextDelete()).Acknowledge();
            var list = await engine.NextList();
            if (kind == "list-failure") list.Fail(Failure(EngineResourceFailureKind.AccessDenied)); else list.SucceedNames(A, B);
            var detail = await engine.NextInspection();
            if (kind == "contradiction") detail.Fail(Failure(EngineResourceFailureKind.NotFound));
            else if (kind == "detail-failure") detail.Fail(Failure(EngineResourceFailureKind.AccessDenied));
            else detail.Succeed(EmptyNetwork(kind == "identity" ? B : A));
            var result = await pending.WaitAsync(Limit);
            Check(result.Resolution == NetworkOperationResolution.Unresolved && result.ObservedExists is null && result.Observations == 1);
        }
    }

    public static async Task NetworkDeletionReadFencesAndEvents()
    {
        foreach (bool beforeCommand in new[] { false, true })
        {
            var engine = new NetworkDeleteConnection(honorReads: false); var clock = new NetworkEventClock();
            await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
            await ReadyNetworkDeletion(session, engine);
            var request = await NetworkDeletionRequest(session, engine);
            ListCall? old = null;
            if (beforeCommand) { session.RefreshNetworks(); old = await engine.NextList(); }
            var pending = session.ExecuteNetworkOperationAsync(request); var command = await engine.NextDelete();
            if (!beforeCommand) { session.RefreshNetworks(); old = await engine.NextList(); }
            command.Acknowledge(); await NetworkOperationState(session, o => o.Phase == NetworkOperationPhase.Reconciling);
            old!.SucceedNames();
            var list = await engine.NextList();
            Check(!pending.IsCompleted && session.NetworkOperation!.ObservedExists is null && session.NetworkSnapshot.Networks.Any(row => row.Id == A));
            list.SucceedNames(A, B); var obsolete = await engine.NextInspection();
            await engine.Events.Emit(NetworkEvent(A)); await engine.Events.Barrier();
            Check(!obsolete.Token.IsCancellationRequested);
            obsolete.Fail(Failure(EngineResourceFailureKind.NotFound));
            await NetworkOperationState(session, o => o.ObservationInvalidated && o.Observations == 1);
            Check(session.NetworkSnapshot.Networks.Any(row => row.Id == A) && !pending.IsCompleted);
            // Browsing gets its event refresh turn before the bounded operation follow-up.
            var browsing = await EventNetworkList(engine, clock); browsing.SucceedNames(A, B);
            await State(session, state => state.IsAuthoritative);
            (await clock.Next(NetworkFollowup)).Release(); await ObserveNetwork(engine, false);
            var result = await pending.WaitAsync(Limit);
            Check(result.Resolution == NetworkOperationResolution.ObservedAbsent && result.Observations == 2 && engine.MaximumConcurrent == 1);
        }
    }

    public static async Task NetworkDeletionConflictEventAndTransportFences()
    {
        foreach (bool transport in new[] { false, true })
        {
            var engine = new NetworkDeleteConnection(honorReads: false); var clock = new NetworkEventClock();
            await using var session = NetworkDeletionSession(new ControlledClient(engine), clock, observations: 1);
            await ReadyNetworkDeletion(session, engine);
            var pending = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, engine));
            var original = new NetworkCommandResult(NetworkCommandDisposition.DaemonResponse, 409, "private conflict", "original body",
                new(NetworkCommandFailureKind.Conflict, "Conflict"));
            (await engine.NextDelete()).Completion.SetResult(original);
            (await engine.NextList()).SucceedNames(A, B); var late = await engine.NextInspection();
            await engine.Events.Emit(NetworkEvent(A)); await engine.Events.Barrier();
            if (transport) late.Fail(Failure(EngineResourceFailureKind.TransportLoss)); else late.Succeed(EmptyNetwork());
            var result = await pending.WaitAsync(Limit);
            Check(result.Resolution == NetworkOperationResolution.Unresolved && result.ObservationInvalidated
                && ReferenceEquals(result.CommandOutcome, original)
                && result.AttachmentObservationState == (transport ? NetworkAttachmentObservationState.Failed : NetworkAttachmentObservationState.Invalidated));
            if (transport) { await Until(() => session.EngineState == EngineConnectionState.Lost); Check(engine.DisposeCount == 1); }
        }
    }

    public static async Task NetworkDeletionRecoveryIdentity()
    {
        foreach (var mode in new[] { "same", "different", "unknown", "empty", "endpoint", "unknown-origin" })
        {
            var old = new NetworkDeleteConnection(engineId: mode == "unknown-origin" ? null : "same-engine");
            var fresh = new NetworkDeleteConnection(engineId: mode == "different" ? "other-engine" : mode == "unknown" ? null
                : mode == "empty" ? "" : "same-engine", actualEndpoint: mode == "endpoint"
                    ? new EngineEndpoint("npipe://./pipe/other", EngineTransport.WindowsNamedPipe) : null);
            var clock = new NetworkEventClock(); var client = new ControlledClient(old, fresh);
            await using var session = NetworkDeletionSession(client, clock, recovery: 1);
            await ReadyNetworkDeletion(session, old); var request = await NetworkDeletionRequest(session, old);
            var pending = session.ExecuteNetworkOperationAsync(request);
            var original = new NetworkCommandResult(NetworkCommandDisposition.Uncertain, DaemonMessage: "private response loss",
                Failure: new(NetworkCommandFailureKind.TransportFailure, "Transport lost"));
            (await old.NextDelete()).Completion.SetResult(original); await pending.WaitAsync(Limit);
            await Until(() => session.EngineState == EngineConnectionState.Lost);
            (await clock.Next(TimeSpan.FromSeconds(3))).Release(); await Load(session, fresh, A, B);
            if (mode == "same")
            {
                await ObserveNetwork(fresh, false); await NetworkOperationState(session, o => o.Resolution == NetworkOperationResolution.ObservedAbsent);
                Check(session.NetworkOperation!.ObservedConnectionGeneration > request.Target.ConnectionGeneration);
            }
            else Check(session.NetworkOperation!.Resolution == NetworkOperationResolution.Unresolved && session.NetworkOperation.ObservedConnectionGeneration is null);
            Check(old.DeleteCalls == 1 && fresh.DeleteCalls == 0 && old.DisposeCount == 1 && !old.DisposedWithActiveRead
                && session.NetworkOperation!.Request == request && ReferenceEquals(session.NetworkOperation.CommandOutcome, original));
        }
    }

    public static async Task NetworkDeletionCommandDeadlinesAndLateEvidence()
    {
        foreach (bool honor in new[] { false, true })
        foreach (bool cancelCaller in new[] { false, true })
        {
            var engine = new NetworkDeleteConnection(honorCommands: honor); var clock = new NetworkEventClock();
            var session = NetworkDeletionSession(new ControlledClient(engine), clock, commandMilliseconds: 200);
            await ReadyNetworkDeletion(session, engine); using var caller = new CancellationTokenSource();
            var pending = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, engine), caller.Token);
            var call = await engine.NextDelete();
            if (cancelCaller) caller.Cancel();
            if (honor && cancelCaller)
            {
                await ObserveNetwork(engine, false);
                Check((await pending.WaitAsync(Limit)).Interruption == NetworkOperationInterruption.CallerCanceled);
            }
            else
            {
                await NetworkOperationState(session, o => o.Resolution == NetworkOperationResolution.Unresolved);
                Check(call.Token.IsCancellationRequested && session.NetworkOperation!.Interruption == NetworkOperationInterruption.CommandDeadline);
                if (!honor)
                {
                    Check(engine.DisposeCount == 0 && !pending.IsCompleted);
                    var ending = session.DisposeAsync().AsTask(); Check(!ending.IsCompleted);
                    var original = new NetworkCommandResult(NetworkCommandDisposition.Acknowledged, 204, "private late acknowledgement");
                    call.Completion.SetResult(original); await ending.WaitAsync(Limit); await pending.WaitAsync(Limit);
                    Check(ReferenceEquals(session.NetworkOperation!.CommandOutcome, original) && engine.DisposeCount == 1 && !engine.DisposedWithActiveRead
                        && session.NetworkOperation.Resolution == NetworkOperationResolution.Unresolved && session.NetworkSnapshot.Networks.Count == 0);
                }
                else Check(ReferenceEquals((await pending.WaitAsync(Limit)).CommandOutcome, call.CancellationOutcome));
            }
            await session.DisposeAsync();
        }
    }

    public static async Task NetworkDeletionNonCooperativeReadsAndTeardown()
    {
        foreach (string mode in new[] { "deadline", "disconnect", "dispose", "retry" })
        foreach (bool detail in new[] { false, true })
        {
            var old = new NetworkDeleteConnection(honorReads: false); var fresh = new NetworkDeleteConnection(); var clock = new NetworkEventClock();
            var client = new ControlledClient(old, fresh);
            var session = NetworkDeletionSession(client, clock, readMilliseconds: mode == "deadline" ? 200 : 10000);
            await ReadyNetworkDeletion(session, old);
            var pending = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, old)); (await old.NextDelete()).Acknowledge();
            var list = await old.NextList(); InspectionCall? inspect = null;
            if (detail) { list.SucceedNames(A, B); inspect = await old.NextInspection(); }
            Task? ending = null;
            if (mode == "deadline")
            {
                session.RefreshNetworks(); // Refresh/selection cannot cancel the operation or disable its deadline.
                session.SelectNetwork(null);
                Check(!(inspect?.Token ?? list.Token).IsCancellationRequested);
                await NetworkOperationState(session, o => o.Resolution == NetworkOperationResolution.Unresolved);
                ending = session.DisconnectAsync();
            }
            else if (mode == "disconnect") ending = session.DisconnectAsync();
            else if (mode == "dispose") ending = session.DisposeAsync().AsTask();
            else session.Retry();
            Check(old.DisposeCount == 0 && (inspect?.Token ?? list.Token).IsCancellationRequested && client.Calls == 1);
            if (ending is not null) Check(!ending.IsCompleted);
            if (inspect is not null) inspect.Fail(Failure(EngineResourceFailureKind.NotFound)); else list.SucceedNames();
            if (ending is not null) await ending.WaitAsync(Limit);
            else
            {
                await Load(session, fresh, A, B); await ObserveNetwork(fresh, false);
                await NetworkOperationState(session, o => o.Resolution == NetworkOperationResolution.ObservedAbsent);
            }
            await pending.WaitAsync(Limit);
            Check(old.DisposeCount == 1 && !old.DisposedWithActiveRead && fresh.DeleteCalls == 0 && session.NetworkSnapshot.Inspection is null);
            await session.DisposeAsync();
        }
    }

    public static async Task NetworkDeletionRetryJoinsCommandAndPreparation()
    {
        var old = new NetworkDeleteConnection(honorReads: false, honorCommands: false); var fresh = new NetworkDeleteConnection(honorReads: false);
        var clock = new NetworkEventClock(); var client = new ControlledClient(old, fresh);
        await using var session = NetworkDeletionSession(client, clock);
        await ReadyNetworkDeletion(session, old); var request = await NetworkDeletionRequest(session, old);
        var pending = session.ExecuteNetworkOperationAsync(request); var command = await old.NextDelete();
        session.Retry(); Check(old.DisposeCount == 0 && client.Calls == 1 && command.Token.IsCancellationRequested);
        command.Acknowledge(); await pending.WaitAsync(Limit);
        await Load(session, fresh, A, B); await ObserveNetwork(fresh, false);
        await NetworkOperationState(session, o => o.Resolution == NetworkOperationResolution.ObservedAbsent);
        Check(old.DeleteCalls == 1 && fresh.DeleteCalls == 0 && old.DisposeCount == 1 && !old.DisposedWithActiveRead);

        await session.ExecuteNetworkOperationAsync(request).WaitAsync(Limit); // Join the resumed receipt before admitting fresh work.
        // A later explicit preparation retains its own connection borrow through asynchronous teardown.
        var next = session.PrepareNetworkDeletionAsync(PreparationTarget(session, B)); var read = await fresh.NextInspection();
        var closing = session.CloseNetworkDeletionPreparationAsync(session.NetworkDeletionPreparation!.Identity);
        var ending = session.DisconnectAsync();
        Check(!closing.IsCompleted && !ending.IsCompleted && fresh.DisposeCount == 0 && read.Token.IsCancellationRequested);
        read.Succeed(EmptyNetwork(B));
        await Task.WhenAll(closing, ending).WaitAsync(Limit);
        Check((await next).Phase == NetworkPreparationPhase.Invalidated && fresh.DisposeCount == 1 && !fresh.DisposedWithActiveRead);
    }

    public static async Task NetworkDeletionCoexistenceAndLateCallerResponse()
    {
        var engine = new NetworkDeleteConnection(honorCommands: false); var clock = new NetworkEventClock();
        await using var session = NetworkDeletionSession(new ControlledClient(engine), clock);
        await ReadyNetworkDeletion(session, engine);
        session.SelectContainer(AllCapabilitiesConnection.ContainerId); await Until(() => session.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId)); await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var container = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration, AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        var image = session.ExecuteImageOperationAsync(new(new(session.ImageSnapshot.ConnectionGeneration, AllCapabilitiesConnection.ImageId)));
        await Task.WhenAll(engine.CommandStarted.Task, engine.ImageCommandStarted.Task).WaitAsync(Limit);
        using var caller = new CancellationTokenSource();
        var network = session.ExecuteNetworkOperationAsync(await NetworkDeletionRequest(session, engine), caller.Token); var call = await engine.NextDelete();
        caller.Cancel(); var original = new NetworkCommandResult(NetworkCommandDisposition.Uncertain, DaemonMessage: "private late canceled response");
        call.Completion.SetResult(original); await ObserveNetwork(engine, false); var result = await network.WaitAsync(Limit);
        Check(ReferenceEquals(result.CommandOutcome, original) && result.Interruption == NetworkOperationInterruption.CallerCanceled
            && result.Resolution == NetworkOperationResolution.ObservedAbsent && !container.IsCompleted && !image.IsCompleted
            && !engine.CommandToken.IsCancellationRequested && !engine.ImageCommandToken.IsCancellationRequested
            && !engine.Events.Disposed && !engine.Logs.Disposed && session.ContainerSnapshot.Inspection is not null);
        session.RefreshImages(); session.RefreshVolumes(); session.RefreshContainers();
        await session.DisconnectAsync().WaitAsync(Limit); await Task.WhenAll(container, image).WaitAsync(Limit);
        Check(engine.DisposeCount == 1 && !engine.DisposedWithActiveRead && engine.Events.Disposed && engine.Logs.Disposed);
    }

    private sealed class NetworkDeleteConnection(bool honorReads = true, bool honorCommands = true, bool canceledNoDispatch = false,
        string? engineId = "same-engine", EngineEndpoint? actualEndpoint = null)
        : AllCapabilitiesConnection("delete-network", honorReads), INetworkCommands
    {
        private readonly Channel<NetworkDeleteCall> deletes = Channel.CreateUnbounded<NetworkDeleteCall>();
        private int active;
        public int DeleteCalls;
        public override EngineMetadata Metadata => base.Metadata with { EngineId = engineId, Endpoint = actualEndpoint ?? base.Metadata.Endpoint };
        public async Task<NetworkCommandResult> DeleteNetworkAsync(string id, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DeleteCalls); Interlocked.Increment(ref active);
            var call = new NetworkDeleteCall(id, cancellationToken, canceledNoDispatch); deletes.Writer.TryWrite(call);
            try { return await (honorCommands ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new NetworkCommandCanceledException(call.CancellationOutcome, cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task<NetworkDeleteCall> NextDelete() => await deletes.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public override ValueTask DisposeAsync() { DisposedWithActiveRead |= active != 0; return base.DisposeAsync(); }
    }
    private sealed class NetworkDeleteCall(string id, CancellationToken token, bool noDispatch)
    {
        public string Id { get; } = id;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<NetworkCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NetworkCommandResult CancellationOutcome { get; } = new(noDispatch ? NetworkCommandDisposition.NotDispatched : NetworkCommandDisposition.Uncertain,
            204, "private cancellation evidence", "private body", new(NetworkCommandFailureKind.Canceled, "Canceled"), true);
        public void Acknowledge() => Completion.SetResult(new(NetworkCommandDisposition.Acknowledged, 204));
    }
}
