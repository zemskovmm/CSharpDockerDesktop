using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class VolumeBrowsingChecks
{
    private static readonly TimeSpan VolumeFollowup = TimeSpan.FromMilliseconds(29);
    private static DesktopSession DeletionSession(IEngineConnectionClient client, VolumeEventClock clock,
        int observations = 2, int commandMilliseconds = 30000, int readMilliseconds = 10000, int recovery = 0)
        => new(client, new() { OperationTimeout = TimeSpan.FromMilliseconds(readMilliseconds), MaximumRecoveryAttempts = recovery }, clock,
            reconciliation: new() { BatchInterval = VolumeBatch, RetryDelay = VolumeEventRetry },
            volumeOperationOptions: new() { CommandTimeout = TimeSpan.FromMilliseconds(commandMilliseconds),
                FollowupInterval = VolumeFollowup, MaximumObservations = observations });
    private static async Task ReadyDeletion(DesktopSession session, VolumeDeleteConnection engine, params string[] names)
    {
        session.Start(); await Load(session, engine, names.Length == 0 ? new[] { A, B } : names);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready);
        await engine.Events.Barrier();
    }
    private static async Task<VolumeOperationRequest> DeletionRequest(DesktopSession session, VolumeDeleteConnection engine,
        string? name = null, VolumeDependencies? evidence = null)
    {
        var task = session.PrepareVolumeDeletionAsync(Target(session, name));
        (await engine.NextDependency()).Succeed(evidence ?? Evidence(name));
        var prepared = await task.WaitAsync(Limit);
        return new(prepared.Target, prepared.Identity);
    }
    private static async Task OperationState(DesktopSession session, Func<VolumeOperationSnapshot, bool> predicate)
        => await Until(() => session.VolumeOperation is { } value && predicate(value));
    private static async Task ObserveAbsent(VolumeDeleteConnection engine) => (await engine.NextList()).SucceedNames();
    private static async Task ObservePresent(VolumeDeleteConnection engine, string? name = null)
    {
        (await engine.NextList()).SucceedNames(name ?? A, B);
        var inspect = await engine.NextInspection(); Check(inspect.Name == (name ?? A)); inspect.Succeed(Detail(name ?? A));
    }

    public static async Task DeletionPreparationAdmissionAndReplay()
    {
        var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
        await using var session = DeletionSession(new ControlledClient(engine), clock);
        await ReadyDeletion(session, engine);
        var request = await DeletionRequest(session, engine);
        Check((await session.ExecuteVolumeOperationAsync(request with { Preparation = request.Preparation with { Revision = 1 } })).Rejection
            == VolumeOperationRejection.PreparationNotCurrent, "Only the exact completed revision admits deletion");
        Check((await session.ExecuteVolumeOperationAsync(request with { Target = Target(session, B) })).Rejection
            == VolumeOperationRejection.PreparationNotCurrent);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); Check((await session.ExecuteVolumeOperationAsync(request, canceled.Token)).Rejection == VolumeOperationRejection.Canceled);
        }
        Check(session.IsVolumeDeletionPreparationCurrent(request.Preparation) && engine.DeleteCalls == 0);
        var pending = session.ExecuteVolumeOperationAsync(request);
        var call = await engine.NextDelete();
        Check(session.VolumeDeletionPreparation!.Phase == VolumePreparationPhase.Consumed
            && !session.IsVolumeDeletionPreparationCurrent(request.Preparation));
        var duplicate = session.ExecuteVolumeOperationAsync(request);
        Check(ReferenceEquals(pending, duplicate));
        for (int i = 0; i < 100; ++i)
        {
            Check((await session.ExecuteVolumeOperationAsync(request with { Target = Target(session, B) })).Rejection == VolumeOperationRejection.CapacityExceeded);
            Check((await session.PrepareVolumeDeletionAsync(Target(session, B))).Failure?.Kind == VolumePreparationFailureKind.CapacityExceeded);
        }
        call.Acknowledge(); await ObserveAbsent(engine);
        var completed = await pending.WaitAsync(Limit);
        Check(completed.Resolution == VolumeOperationResolution.ObservedAbsent && ReferenceEquals(pending, session.ExecuteVolumeOperationAsync(request))
            && engine.DeleteCalls == 1 && completed.Preparation!.Identity == request.Preparation
            && completed.Preparation.Phase == VolumePreparationPhase.Completed);
        var next = await DeletionRequest(session, engine, B);
        var second = session.ExecuteVolumeOperationAsync(next); (await engine.NextDelete()).Acknowledge(); await ObserveAbsent(engine); await second.WaitAsync(Limit);
        Check((await session.ExecuteVolumeOperationAsync(request)).Rejection == VolumeOperationRejection.PreparationNotCurrent
            && engine.DeleteCalls == 2, "Old consumed receipts cannot be replayed after the retained operation changes");
    }

    public static async Task DeletionStaleInvalidatedAndUnavailablePreparations()
    {
        var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
        await using var session = DeletionSession(new ControlledClient(engine), clock);
        await ReadyDeletion(session, engine);
        var request = await DeletionRequest(session, engine);
        await session.CloseVolumeDeletionPreparationAsync(request.Preparation);
        Check((await session.ExecuteVolumeOperationAsync(request)).Rejection == VolumeOperationRejection.PreparationNotCurrent);
        request = await DeletionRequest(session, engine);
        var replaced = await DeletionRequest(session, engine, B);
        Check((await session.ExecuteVolumeOperationAsync(request)).Rejection == VolumeOperationRejection.PreparationNotCurrent);
        var loadingTask = session.PrepareVolumeDeletionAsync(Target(session)); var loading = await engine.NextDependency();
        Check((await session.ExecuteVolumeOperationAsync(new(Target(session), session.VolumeDeletionPreparation!.Identity))).Rejection
            == VolumeOperationRejection.PreparationNotCurrent);
        loading.Succeed(Evidence()); await loadingTask;
        request = await DeletionRequest(session, engine, evidence: new(A, null, Array.Empty<string>()));
        Check((await session.ExecuteVolumeOperationAsync(request)).Rejection == VolumeOperationRejection.PreparationNotCurrent);
        request = await DeletionRequest(session, engine);
        await engine.Events.Emit(VolumeEvent(A)); await engine.Events.Barrier();
        Check((await session.ExecuteVolumeOperationAsync(request)).Rejection == VolumeOperationRejection.PreparationNotCurrent);
        Check((await session.ExecuteVolumeOperationAsync(request with { Target = request.Target with { ConnectionGeneration = -1 } })).Rejection
            == VolumeOperationRejection.StaleConnection);
        Check((await session.ExecuteVolumeOperationAsync(request with { Target = Target(session, "bad\nname") })).Rejection
            == VolumeOperationRejection.InvalidRequest && engine.DeleteCalls == 0 && session.VolumeOperation is null);
    }

    public static async Task DeletionExactIncompleteEvidenceAndSelectionIndependence()
    {
        const string exact = "  Data/Δ\\:%?#😀  ";
        var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
        await using var session = DeletionSession(new ControlledClient(engine), clock);
        await ReadyDeletion(session, engine, exact, B);
        var evidence = new VolumeDependencies(exact, new[] {
            new VolumeContainerDependency("full-running", new[] { "/run" }, null, "running", true),
            new VolumeContainerDependency("full-stopped", null, "/stopped", "exited", false) }, new[] { "unknown-full-id" });
        var request = await DeletionRequest(session, engine, exact, evidence);
        session.SelectVolume(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, state => state.InspectionState == ResourceReadState.Ready);
        session.VolumeOperationChanged += (_, _) => throw new InvalidOperationException("private observer");
        var pending = session.ExecuteVolumeOperationAsync(request); var call = await engine.NextDelete();
        session.SelectVolume(null); session.ClearVolumeInspection();
        Check(call.Name == exact && session.VolumeOperation!.Preparation!.Dependencies!.Completeness == VolumeDependencyCompleteness.Incomplete
            && session.VolumeOperation.Preparation.Dependencies.Containers!.Select(item => item.Running).SequenceEqual(new bool?[] { true, false }));
        call.Acknowledge(); await ObserveAbsent(engine);
        var result = await pending.WaitAsync(Limit);
        Check(result.Request.Target.Name == exact && result.Resolution == VolumeOperationResolution.ObservedAbsent
            && result.Preparation!.Dependencies!.UnverifiedContainerIds.Single() == "unknown-full-id" && engine.DeleteCalls == 1);
    }

    public static async Task DeletionCommandDispositionsAndObservations()
    {
        foreach (var disposition in Enum.GetValues<VolumeCommandDisposition>())
        {
            var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
            await using var session = DeletionSession(new ControlledClient(engine), clock);
            await ReadyDeletion(session, engine);
            var request = await DeletionRequest(session, engine);
            var pending = session.ExecuteVolumeOperationAsync(request); var call = await engine.NextDelete();
            var original = new VolumeCommandResult(disposition, 404, "private original daemon\nmessage", "{\"private\":true}",
                disposition == VolumeCommandDisposition.DaemonResponse ? new(VolumeCommandFailureKind.NotFound, "daemon response") : null,
                ErrorBodyUnavailable: true);
            call.Completion.SetResult(original);
            if (disposition == VolumeCommandDisposition.NotDispatched)
            { (await engine.NextList()).SucceedNames(A); }
            else if (disposition == VolumeCommandDisposition.Acknowledged) await ObservePresent(engine);
            else await ObserveAbsent(engine);
            var result = await pending.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, original) && result.Resolution == (disposition == VolumeCommandDisposition.NotDispatched
                ? VolumeOperationResolution.NotDispatched : disposition == VolumeCommandDisposition.Acknowledged
                    ? VolumeOperationResolution.ObservedPresent : VolumeOperationResolution.ObservedAbsent)
                && engine.DeleteCalls == 1, "Acknowledgement is not absence; uncertain/daemon response remains independent of observed absence");
        }
    }

    public static async Task DeletionIncompleteListsAndExactNotFound()
    {
        foreach (bool inspectNotFound in new[] { false, true })
        {
            var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
            await using var session = DeletionSession(new ControlledClient(engine), clock);
            await ReadyDeletion(session, engine);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine)); (await engine.NextDelete()).Acknowledge();
            (await engine.NextList()).Succeed(new(null, new[] { "private incomplete listing" }));
            var target = await engine.NextInspection(); Check(target.Name == A);
            if (inspectNotFound) target.Fail(Failure(EngineResourceFailureKind.NotFound));
            else target.Fail(Failure(EngineResourceFailureKind.AccessDenied));
            if (!inspectNotFound)
            {
                (await clock.Next(VolumeFollowup)).Release();
                (await engine.NextList()).Succeed(new(Array.Empty<VolumeSummary>(), new[] { "private warning" }));
                (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.AccessDenied));
            }
            var result = await pending.WaitAsync(Limit);
            Check(result.Resolution == (inspectNotFound ? VolumeOperationResolution.ObservedAbsent : VolumeOperationResolution.Unresolved)
                && result.ObservedExists == (inspectNotFound ? false : null) && result.ObservedListing!.HasWarnings
                && result.ObservationFailure!.Kind == (inspectNotFound ? EngineResourceFailureKind.NotFound : EngineResourceFailureKind.AccessDenied)
                && result.Observations == (inspectNotFound ? 1 : 2));
            Check(inspectNotFound ? !session.VolumeSnapshot.Volumes.Any(row => row.Name == A)
                : session.VolumeSnapshot.Volumes.Any(row => row.Name == A), "Null/warning lists cannot prove absence or clear useful rows");
        }
    }

    public static async Task DeletionCallerCancellationEvidence()
    {
        foreach (bool classifiedNoDispatch in new[] { false, true })
        {
            var engine = new VolumeDeleteConnection(canceledNoDispatch: classifiedNoDispatch); var clock = new VolumeEventClock();
            await using var session = DeletionSession(new ControlledClient(engine), clock);
            await ReadyDeletion(session, engine);
            using var caller = new CancellationTokenSource();
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine), caller.Token);
            var call = await engine.NextDelete(); caller.Cancel(); session.SelectVolume(null);
            if (!classifiedNoDispatch) await ObserveAbsent(engine);
            else (await engine.NextList()).SucceedNames(A);
            var result = await pending.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, call.CancellationOutcome)
                && result.Interruption == VolumeOperationInterruption.CallerCanceled && result.CommandOutcome!.ErrorBodyUnavailable
                && result.Resolution == (classifiedNoDispatch ? VolumeOperationResolution.NotDispatched : VolumeOperationResolution.ObservedAbsent)
                && engine.DeleteCalls == 1);
        }
    }

    public static async Task DeletionConflictRefresh()
    {
        foreach (var completeness in Enum.GetValues<VolumeDependencyCompleteness>())
        {
            var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
            await using var session = DeletionSession(new ControlledClient(engine), clock);
            await ReadyDeletion(session, engine);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine));
            var original = new VolumeCommandResult(VolumeCommandDisposition.DaemonResponse, 409,
                "volume in use by private container", "original daemon body", new(VolumeCommandFailureKind.Conflict, "Dependency conflict"));
            (await engine.NextDelete()).Completion.SetResult(original);
            int attempts = completeness == VolumeDependencyCompleteness.Complete ? 1 : 2;
            for (int attempt = 0; attempt < attempts; ++attempt)
            {
                if (attempt != 0) (await clock.Next(VolumeFollowup)).Release();
                await ObservePresent(engine);
                var query = await engine.NextDependency(); Check(query.Name == A);
                query.Succeed(new(A, completeness == VolumeDependencyCompleteness.Unavailable ? null
                    : new[] { new VolumeContainerDependency("full-stopped-conflict", null, null, "exited", false) },
                    completeness == VolumeDependencyCompleteness.Incomplete ? new[] { "unknown-id" } : Array.Empty<string>()));
            }
            var result = await pending.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, original) && result.ObservedExists == true
                && result.DependencyRefreshAttempts == attempts && result.RefreshedDependencies!.Completeness == completeness
                && result.Resolution == (completeness == VolumeDependencyCompleteness.Complete
                    ? VolumeOperationResolution.ObservedPresent : VolumeOperationResolution.Unresolved)
                && engine.DeleteCalls == 1 && session.VolumeDeletionPreparation!.Phase == VolumePreparationPhase.Consumed,
                "Conflict refresh retains current running/stopped evidence without renewed confirmation or resend");
        }
    }

    public static async Task DeletionConflictFailureAndDeadline()
    {
        foreach (bool deadline in new[] { false, true })
        {
            var engine = new VolumeDeleteConnection(honorDependencies: !deadline); var clock = new VolumeEventClock();
            var session = DeletionSession(new ControlledClient(engine), clock, observations: 1, readMilliseconds: deadline ? 200 : 10000);
            await ReadyDeletion(session, engine);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine));
            (await engine.NextDelete()).Completion.SetResult(new(VolumeCommandDisposition.DaemonResponse, 409,
                Failure: new(VolumeCommandFailureKind.Conflict, "conflict")));
            await ObservePresent(engine); var dependency = await engine.NextDependency();
            if (!deadline)
            {
                var failure = Failure(EngineResourceFailureKind.AccessDenied); dependency.Fail(failure);
                var result = await pending.WaitAsync(Limit);
                Check(result.DependencyObservationState == VolumeDependencyObservationState.Failed
                    && ReferenceEquals(result.DependencyFailure, failure) && result.Resolution == VolumeOperationResolution.Unresolved);
            }
            else
            {
                await OperationState(session, o => o.Resolution == VolumeOperationResolution.Unresolved);
                var ending = session.DisconnectAsync(); Check(!ending.IsCompleted && engine.DisposeCount == 0 && dependency.Token.IsCancellationRequested);
                dependency.Succeed(Evidence()); await ending.WaitAsync(Limit); await pending.WaitAsync(Limit);
                Check(engine.DisposeCount == 1 && !engine.DisposedWithActiveRead && session.VolumeOperation!.RefreshedDependencies is null);
            }
            await session.DisposeAsync();
        }
    }

    public static async Task DeletionPreCommandAndEventReadFences()
    {
        var engine = new VolumeDeleteConnection(honorReads: false); var clock = new VolumeEventClock();
        await using var session = DeletionSession(new ControlledClient(engine), clock);
        await ReadyDeletion(session, engine);
        var request = await DeletionRequest(session, engine);
        session.RefreshVolumes(); var beforeCommand = await engine.NextList();
        var pending = session.ExecuteVolumeOperationAsync(request); var command = await engine.NextDelete();
        command.Acknowledge(); await OperationState(session, o => o.Phase == VolumeOperationPhase.Reconciling);
        beforeCommand.SucceedNames();
        var current = await engine.NextList(); Check(!pending.IsCompleted && session.VolumeOperation!.ObservedExists is null);
        current.SucceedNames(A, B); var obsolete = await engine.NextInspection();
        await engine.Events.Emit(VolumeEvent(A, "delete")); await engine.Events.Barrier();
        obsolete.Succeed(Detail(A, "{\"obsolete\":true}"));
        var followup = await clock.Next(VolumeFollowup);
        Check(session.VolumeOperation!.ObservedExists is null && session.VolumeOperation.Resolution is null
            && session.VolumeSnapshot.Inspection is null, "An event during observation fences same-name stale presence");
        // Browse gets a fair turn while the operation waits its follow-up interval.
        (await clock.Next(VolumeBatch)).Release(); (await engine.NextList()).SucceedNames(B);
        followup.Release(); (await engine.NextList()).SucceedNames(B);
        Check((await pending.WaitAsync(Limit)).Resolution == VolumeOperationResolution.ObservedAbsent && engine.MaximumConcurrent == 1);
    }

    public static async Task DeletionPreResponseAndRecreationObservations()
    {
        var engine = new VolumeDeleteConnection(honorReads: false); var clock = new VolumeEventClock();
        await using var session = DeletionSession(new ControlledClient(engine), clock);
        await ReadyDeletion(session, engine);
        var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine)); var command = await engine.NextDelete();
        session.RefreshVolumes(); var beforeResponse = await engine.NextList();
        command.Acknowledge(); await OperationState(session, o => o.Phase == VolumeOperationPhase.Reconciling);
        beforeResponse.SucceedNames(B);
        var postResponse = await engine.NextList(); Check(!pending.IsCompleted && session.VolumeOperation!.ObservedExists is null);
        postResponse.SucceedNames(A, B); (await engine.NextInspection()).Succeed(Detail(A, "{\"recreated\":true}"));
        var result = await pending.WaitAsync(Limit);
        Check(result.Resolution == VolumeOperationResolution.ObservedPresent && result.CommandOutcome!.Disposition == VolumeCommandDisposition.Acknowledged
            && result.Request.Target.Name == A, "Same-name recreation can only be reported as current presence, never command failure or immutable identity");
    }

    public static async Task DeletionResponseLossAndRecoveryIdentity()
    {
        foreach (string? identity in new string?[] { "same-volume-delete-engine", "different-engine", null, "" })
        {
            var old = new VolumeDeleteConnection(); var fresh = new VolumeDeleteConnection(engineId: identity); var clock = new VolumeEventClock();
            await using var session = DeletionSession(new ControlledClient(old, fresh), clock, recovery: 1);
            await ReadyDeletion(session, old);
            var request = await DeletionRequest(session, old);
            var pending = session.ExecuteVolumeOperationAsync(request);
            (await old.NextDelete()).Completion.SetResult(new(VolumeCommandDisposition.Uncertain,
                DaemonMessage: "private lost-response evidence", Failure: new(VolumeCommandFailureKind.TransportFailure, "lost response")));
            Check((await pending.WaitAsync(Limit)).Resolution == VolumeOperationResolution.Unresolved);
            await Until(() => session.EngineState == EngineConnectionState.Lost);
            (await clock.Next(TimeSpan.FromSeconds(3))).Release();
            (await fresh.NextList()).SucceedNames(B);
            if (identity == "same-volume-delete-engine")
            {
                (await fresh.NextList()).SucceedNames(B);
                await OperationState(session, o => o.Resolution == VolumeOperationResolution.ObservedAbsent);
                Check(session.VolumeOperation!.ObservedConnectionGeneration > request.Target.ConnectionGeneration);
            }
            else
            {
                await State(session, s => s.IsAuthoritative);
                Check(session.VolumeOperation!.Resolution == VolumeOperationResolution.Unresolved);
            }
            Check(old.DeleteCalls == 1 && fresh.DeleteCalls == 0 && old.DisposeCount == 1
                && session.VolumeOperation!.CommandOutcome!.DaemonMessage == "private lost-response evidence");
        }
    }

    public static async Task DeletionCommandDeadlineAndLateTeardown()
    {
        foreach (bool cancelCaller in new[] { false, true })
        {
            var engine = new VolumeDeleteConnection(honorCommands: false); var clock = new VolumeEventClock();
            var session = DeletionSession(new ControlledClient(engine), clock, commandMilliseconds: 200);
            await ReadyDeletion(session, engine);
            using var caller = new CancellationTokenSource();
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine), caller.Token); var command = await engine.NextDelete();
            if (cancelCaller) caller.Cancel();
            await OperationState(session, o => o.Resolution == VolumeOperationResolution.Unresolved);
            Check(command.Token.IsCancellationRequested && engine.DisposeCount == 0 && !pending.IsCompleted
                && session.VolumeOperation!.Interruption == VolumeOperationInterruption.CommandDeadline);
            var ending = session.DisposeAsync().AsTask(); Check(!ending.IsCompleted);
            var late = new VolumeCommandResult(VolumeCommandDisposition.Acknowledged, 204, "late private daemon evidence");
            command.Completion.SetResult(late); await ending.WaitAsync(Limit); await pending.WaitAsync(Limit);
            Check(engine.DisposeCount == 1 && !engine.DisposedWithActiveRead && ReferenceEquals(session.VolumeOperation!.CommandOutcome, late)
                && session.VolumeOperation.Resolution == VolumeOperationResolution.Unresolved && session.VolumeSnapshot.Volumes.Count == 0,
                "Late acknowledgement is private protocol evidence; teardown joins before releasing and never proves absence");
        }
    }

    public static async Task DeletionNonCooperativeObservationAndRetry()
    {
        foreach (string mode in new[] { "deadline", "disconnect", "dispose", "retry" })
        {
            var old = new VolumeDeleteConnection(honorReads: false); var fresh = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
            var client = new ControlledClient(old, fresh);
            var session = DeletionSession(client, clock, readMilliseconds: mode == "deadline" ? 200 : 10000);
            await ReadyDeletion(session, old);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, old)); (await old.NextDelete()).Acknowledge();
            (await old.NextList()).SucceedNames(A); var late = await old.NextInspection();
            Task? ending = null;
            if (mode == "deadline")
            {
                session.RefreshVolumes(); // User refresh cannot cancel or disable the independent observation deadline.
                await OperationState(session, o => o.Resolution == VolumeOperationResolution.Unresolved);
                ending = session.DisconnectAsync();
            }
            else if (mode == "disconnect") ending = session.DisconnectAsync();
            else if (mode == "dispose") ending = session.DisposeAsync().AsTask();
            else session.Retry();
            Check(old.DisposeCount == 0 && late.Token.IsCancellationRequested && client.Calls == 1);
            if (ending is not null) Check(!ending.IsCompleted);
            late.Fail(Failure(EngineResourceFailureKind.NotFound));
            if (ending is not null) await ending.WaitAsync(Limit);
            else
            {
                (await fresh.NextList()).SucceedNames(B); (await fresh.NextList()).SucceedNames(B);
                await OperationState(session, o => o.Resolution == VolumeOperationResolution.ObservedAbsent);
            }
            await pending.WaitAsync(Limit);
            Check(old.DisposeCount == 1 && !old.DisposedWithActiveRead && fresh.DeleteCalls == 0 && session.VolumeSnapshot.Inspection is null);
            await session.DisposeAsync();
        }
    }

    public static async Task DeletionCommandRetryAndCapabilityCoexistence()
    {
        var old = new VolumeDeleteConnection(honorCommands: false); var fresh = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
        var client = new ControlledClient(old, fresh);
        await using (var session = DeletionSession(client, clock))
        {
            await ReadyDeletion(session, old);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, old)); var late = await old.NextDelete();
            session.Retry(); Check(old.DisposeCount == 0 && client.Calls == 1 && late.Token.IsCancellationRequested);
            late.Acknowledge(); await pending.WaitAsync(Limit);
            (await fresh.NextList()).SucceedNames(B); (await fresh.NextList()).SucceedNames(B);
            await OperationState(session, o => o.Resolution == VolumeOperationResolution.ObservedAbsent);
            Check(old.DeleteCalls == 1 && fresh.DeleteCalls == 0 && old.DisposeCount == 1 && !old.DisposedWithActiveRead);
        }
        var engine = new VolumeDeleteConnection(); var timing = new VolumeEventClock();
        await using var together = DeletionSession(new ControlledClient(engine), timing);
        await ReadyDeletion(together, engine);
        together.SelectContainer(AllCapabilitiesConnection.ContainerId);
        await Until(() => together.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
        Check(together.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => together.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var container = together.ExecuteContainerOperationAsync(new(new(together.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        var image = together.ExecuteImageOperationAsync(new(new(together.ImageSnapshot.ConnectionGeneration, AllCapabilitiesConnection.ImageId)));
        await Task.WhenAll(engine.CommandStarted.Task, engine.ImageCommandStarted.Task).WaitAsync(Limit);
        var volume = together.ExecuteVolumeOperationAsync(await DeletionRequest(together, engine)); (await engine.NextDelete()).Acknowledge();
        await ObserveAbsent(engine); await volume.WaitAsync(Limit);
        Check(!container.IsCompleted && !image.IsCompleted && !engine.CommandToken.IsCancellationRequested && !engine.ImageCommandToken.IsCancellationRequested
            && !engine.Events.Disposed && !engine.Logs.Disposed && together.ContainerSnapshot.Inspection is not null,
            "Volume deletion leaves browsing, events, Logs and container/image operations independently owned");
        await together.DisconnectAsync().WaitAsync(Limit); await Task.WhenAll(container, image).WaitAsync(Limit);
        Check(engine.DisposeCount == 1 && !engine.DisposedWithActiveRead && engine.Events.Disposed && engine.Logs.Disposed);
    }

    public static async Task DeletionAtomicConsumptionAndMissingCapabilities()
    {
        var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
        await using (var session = DeletionSession(new ControlledClient(engine), clock))
        {
            await ReadyDeletion(session, engine);
            var request = await DeletionRequest(session, engine);
            var contenders = Enumerable.Range(0, 64).Select(_ => Task.Run(() => session.ExecuteVolumeOperationAsync(request))).ToArray();
            var command = await engine.NextDelete(); command.Acknowledge(); await ObserveAbsent(engine);
            var results = await Task.WhenAll(contenders).WaitAsync(Limit);
            Check(results.All(item => item.OperationId == results[0].OperationId && item.Resolution == VolumeOperationResolution.ObservedAbsent)
                && engine.DeleteCalls == 1 && session.VolumeDeletionPreparation!.Phase == VolumePreparationPhase.Consumed,
                "Concurrent admission atomically consumes a single preparation and shares one mutation receipt");
        }
        var missing = new DependencyPreparationConnection("no-volume-commands"); var timing = new VolumeEventClock();
        await using var unavailable = PreparationSession(new ControlledClient(missing), timing);
        await ReadyPreparation(unavailable, missing);
        var task = unavailable.PrepareVolumeDeletionAsync(Target(unavailable)); (await missing.NextDependency()).Succeed(Evidence());
        var prepared = await task;
        var rejected = await unavailable.ExecuteVolumeOperationAsync(new(prepared.Target, prepared.Identity));
        Check(rejected.Rejection == VolumeOperationRejection.CapabilityUnavailable
            && unavailable.IsVolumeDeletionPreparationCurrent(prepared.Identity) && unavailable.VolumeOperation is null);
    }

    public static async Task DeletionRecoveryEndpointAndUnknownOrigin()
    {
        foreach (bool unknownOrigin in new[] { false, true })
        {
            var old = new VolumeDeleteConnection(engineId: unknownOrigin ? null : "same-volume-delete-engine");
            var fresh = new VolumeDeleteConnection(engineEndpoint: unknownOrigin ? null
                : new EngineEndpoint("npipe://./pipe/another_engine", EngineTransport.WindowsNamedPipe));
            var clock = new VolumeEventClock();
            await using var session = DeletionSession(new ControlledClient(old, fresh), clock, recovery: 1);
            await ReadyDeletion(session, old);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, old));
            (await old.NextDelete()).Completion.SetResult(new(VolumeCommandDisposition.Uncertain,
                Failure: new(VolumeCommandFailureKind.TransportFailure, "lost")));
            await pending.WaitAsync(Limit); await Until(() => session.EngineState == EngineConnectionState.Lost);
            (await clock.Next(TimeSpan.FromSeconds(3))).Release(); (await fresh.NextList()).SucceedNames();
            await State(session, state => state.IsAuthoritative);
            Check(session.VolumeOperation!.Resolution == VolumeOperationResolution.Unresolved
                && session.VolumeOperation.ObservedConnectionGeneration is null && fresh.ListCalls == 1 && fresh.DeleteCalls == 0,
                "A changed actual endpoint or unknown original Engine prevents recovery from establishing previous command results");
        }
    }

    public static async Task DeletionDeadlineCancellationAndLateCallerResponse()
    {
        var old = new VolumeDeleteConnection(); var fresh = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
        await using (var session = DeletionSession(new ControlledClient(old, fresh), clock, commandMilliseconds: 200, recovery: 1))
        {
            await ReadyDeletion(session, old);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, old)); var command = await old.NextDelete();
            var result = await pending.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, command.CancellationOutcome)
                && result.Interruption == VolumeOperationInterruption.CommandDeadline && result.Resolution == VolumeOperationResolution.Unresolved);
            await Until(() => session.EngineState == EngineConnectionState.Lost);
            (await clock.Next(TimeSpan.FromSeconds(3))).Release();
            (await fresh.NextList()).SucceedNames(); (await fresh.NextList()).SucceedNames();
            await OperationState(session, operation => operation.Resolution == VolumeOperationResolution.ObservedAbsent);
            Check(ReferenceEquals(session.VolumeOperation!.CommandOutcome, command.CancellationOutcome) && fresh.DeleteCalls == 0);
        }
        var lateEngine = new VolumeDeleteConnection(honorCommands: false); var timing = new VolumeEventClock();
        await using var lateSession = DeletionSession(new ControlledClient(lateEngine), timing);
        await ReadyDeletion(lateSession, lateEngine);
        using var caller = new CancellationTokenSource();
        var latePending = lateSession.ExecuteVolumeOperationAsync(await DeletionRequest(lateSession, lateEngine), caller.Token);
        var late = await lateEngine.NextDelete(); caller.Cancel();
        Check(!latePending.IsCompleted && late.Token.IsCancellationRequested && lateEngine.DisposeCount == 0);
        late.Acknowledge(); await ObserveAbsent(lateEngine);
        var lateResult = await latePending.WaitAsync(Limit);
        Check(lateResult.Resolution == VolumeOperationResolution.ObservedAbsent
            && lateResult.Interruption == VolumeOperationInterruption.CallerCanceled && lateEngine.DeleteCalls == 1,
            "Cancellation-ignoring command completion remains owned and session observation outlives the caller token");
    }

    public static async Task DeletionConflictEventFenceAndTransportEvidence()
    {
        foreach (bool transport in new[] { false, true })
        {
            var engine = new VolumeDeleteConnection(); var clock = new VolumeEventClock();
            await using var session = DeletionSession(new ControlledClient(engine), clock);
            await ReadyDeletion(session, engine);
            var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine));
            (await engine.NextDelete()).Completion.SetResult(new(VolumeCommandDisposition.DaemonResponse, 409,
                Failure: new(VolumeCommandFailureKind.Conflict, "conflict")));
            await ObservePresent(engine); var dependency = await engine.NextDependency();
            if (transport)
            {
                var failure = Failure(EngineResourceFailureKind.TransportLoss); dependency.Fail(failure);
                var result = await pending.WaitAsync(Limit);
                Check(result.Resolution == VolumeOperationResolution.Unresolved && ReferenceEquals(result.DependencyFailure, failure)
                    && result.ObservationInvalidated && result.CommandOutcome!.Failure!.Kind == VolumeCommandFailureKind.Conflict);
            }
            else
            {
                await engine.Events.Emit(new("container", "create", "new-full-container", null, null, new(200, null)));
                await engine.Events.Barrier(); dependency.Succeed(Evidence());
                var followup = await clock.Next(VolumeFollowup);
                Check(session.VolumeOperation!.ObservationInvalidated && session.VolumeOperation.ObservedExists is null
                    && session.VolumeOperation.DependencyObservationState == VolumeDependencyObservationState.Invalidated);
                followup.Release(); await ObservePresent(engine); (await engine.NextDependency()).Succeed(Evidence());
                var result = await pending.WaitAsync(Limit);
                Check(result.Resolution == VolumeOperationResolution.ObservedPresent && !result.ObservationInvalidated
                    && engine.DeleteCalls == 1 && engine.MaximumDependencies == 1);
            }
        }
    }

    public static async Task DeletionNonCooperativeListDeadline()
    {
        var engine = new VolumeDeleteConnection(honorReads: false); var clock = new VolumeEventClock();
        var session = DeletionSession(new ControlledClient(engine), clock, readMilliseconds: 200);
        await ReadyDeletion(session, engine);
        var pending = session.ExecuteVolumeOperationAsync(await DeletionRequest(session, engine)); (await engine.NextDelete()).Acknowledge();
        var late = await engine.NextList(); session.RefreshVolumes();
        await OperationState(session, operation => operation.Resolution == VolumeOperationResolution.Unresolved);
        Check(session.VolumeOperation!.ObservationFailure?.Kind == EngineResourceFailureKind.TransportLoss && late.Token.IsCancellationRequested);
        var ending = session.DisposeAsync().AsTask(); Check(!ending.IsCompleted && engine.DisposeCount == 0);
        late.SucceedNames(); await ending.WaitAsync(Limit); await pending.WaitAsync(Limit);
        Check(session.VolumeOperation!.ObservedExists is null && session.VolumeSnapshot.Volumes.Count == 0 && !engine.DisposedWithActiveRead);
    }

    private sealed class VolumeDeleteConnection(bool honorReads = true, bool honorCommands = true,
        bool honorDependencies = true, bool canceledNoDispatch = false, string? engineId = "same-volume-delete-engine", EngineEndpoint? engineEndpoint = null)
        : AllCapabilitiesConnection("volume-delete", honorReads), IVolumeCommands, IVolumeDependencyReader
    {
        private readonly Channel<VolumeDeleteCall> deletes = Channel.CreateUnbounded<VolumeDeleteCall>();
        private readonly Channel<DependencyCall> dependencies = Channel.CreateUnbounded<DependencyCall>();
        private int activeCommands, activeDependencies;
        public int DeleteCalls, DependencyCalls, MaximumDependencies;
        public override EngineMetadata Metadata => base.Metadata with { EngineId = engineId, Endpoint = engineEndpoint ?? base.Metadata.Endpoint };
        public async Task<VolumeCommandResult> DeleteVolumeAsync(string name, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DeleteCalls); Interlocked.Increment(ref activeCommands);
            var call = new VolumeDeleteCall(name, cancellationToken, canceledNoDispatch); deletes.Writer.TryWrite(call);
            try { return await (honorCommands ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new VolumeCommandCanceledException(call.CancellationOutcome, cancellationToken); }
            finally { Interlocked.Decrement(ref activeCommands); }
        }
        public async Task<EngineResourceResult<VolumeDependencies>> QueryVolumeDependenciesAsync(string name, CancellationToken cancellationToken = default)
        {
            int active = Interlocked.Increment(ref activeDependencies); MaximumDependencies = Math.Max(MaximumDependencies, active);
            Interlocked.Increment(ref DependencyCalls); var call = new DependencyCall(name, cancellationToken); dependencies.Writer.TryWrite(call);
            try { return await (honorDependencies ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            finally { Interlocked.Decrement(ref activeDependencies); }
        }
        public async Task<DependencyCall> NextDependency() => await dependencies.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public async Task<VolumeDeleteCall> NextDelete() => await deletes.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public override ValueTask DisposeAsync()
        { DisposedWithActiveRead |= activeCommands != 0 || activeDependencies != 0; return base.DisposeAsync(); }
    }
    private sealed class VolumeDeleteCall(string name, CancellationToken token, bool canceledNoDispatch)
    {
        public string Name { get; } = name;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<VolumeCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VolumeCommandResult CancellationOutcome { get; } = new(canceledNoDispatch ? VolumeCommandDisposition.NotDispatched : VolumeCommandDisposition.Uncertain,
            204, "private cancellation evidence", "private original body", new(VolumeCommandFailureKind.Canceled, "Canceled command"), ErrorBodyUnavailable: true);
        public void Acknowledge() => Completion.SetResult(new(VolumeCommandDisposition.Acknowledged, 204));
    }
}
