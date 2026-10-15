using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class ImageBrowsingChecks
{
    private static readonly TimeSpan ImageFollowup = TimeSpan.FromMilliseconds(23);
    private static DesktopSession DeleteSession(ControlledClient client, ImageEventClock clock,
        int observations = 3, int recovery = 0, int readMilliseconds = 20000, int commandMilliseconds = 30000)
        => new(client, new() { MaximumRecoveryAttempts = recovery, RetryDelay = ImageEventRetry,
            OperationTimeout = TimeSpan.FromMilliseconds(readMilliseconds) }, clock,
            reconciliation: new() { BatchInterval = ImageBatch },
            imageOperationOptions: new() { MaximumObservations = observations, FollowupInterval = ImageFollowup,
                CommandTimeout = TimeSpan.FromMilliseconds(commandMilliseconds) });
    private static ImageOperationRequest DeleteRequest(DesktopSession session, string? id = null)
        => new(new(session.ImageSnapshot.ConnectionGeneration, id ?? A));
    private static async Task DeleteState(DesktopSession session, Func<ImageOperationSnapshot?, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.ImageOperation)) reached.TrySetResult(); }
        session.ImageOperationChanged += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.ImageOperationChanged -= Observe; }
    }
    private static async Task<ListCall> DeleteList(ControlledConnection connection)
    {
        while (true) { var call = await connection.NextList(); if (!call.Token.IsCancellationRequested) return call; }
    }
    private static async Task DeleteLoad(DesktopSession session, DeleteConnection connection, params string[] ids)
    { session.Start(); (await DeleteList(connection)).Succeed(ids); await State(session, s => s.State == ResourceReadState.Ready); }
    private static async Task DeleteObserve(DesktopSession session, ControlledConnection connection, params string[] ids)
    {
        await DeleteState(session, s => s?.Phase == ImageOperationPhase.Reconciling);
        (await DeleteList(connection)).Succeed(ids);
        if (ids.Contains(A)) (await connection.NextInspection()).Succeed(Detail(A));
    }

    public static async Task ImageDeleteAcknowledgedAndParents()
    {
        var engine = new DeleteConnection(); var clock = new ImageEventClock();
        await using var session = DeleteSession(new ControlledClient(engine), clock);
        await DeleteLoad(session, engine, A, B, C);
        session.SelectImage(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.ImageOperationChanged += (_, _) => throw new InvalidOperationException("observer failure");
        var pending = session.ExecuteImageOperationAsync(DeleteRequest(session));
        var call = await engine.NextDelete();
        Check(call.Identity == A && session.ImageOperation!.Phase == ImageOperationPhase.CommandPending);
        var records = new List<ImageRemovalRecord> { new(Deleted: A), new(Deleted: B) };
        call.Completion.SetResult(new(ImageCommandDisposition.Acknowledged, 200, RemovalRecords: records));
        await DeleteState(session, o => o?.Phase == ImageOperationPhase.Reconciling);
        Check(!pending.IsCompleted, "Removal records cannot independently establish absence");
        records.Clear();
        await DeleteObserve(session, engine, C);
        var result = await pending.WaitAsync(Limit);
        Check(result.Resolution == ImageOperationResolution.ObservedAbsent && result.CommandOutcome!.RemovalRecords!.Count == 2
            && session.ImageSnapshot.Images.Single().Id == C && session.ImageSnapshot.SelectedImageId is null
            && session.ImageSnapshot.Inspection is null && result.ObservedExists == false,
            "Full authoritative refresh includes parent pruning, immutable command evidence and cleared parent selection");
        Check(engine.MaximumConcurrent == 1 && engine.DeleteCalls == 1);
    }

    public static async Task ImageDeleteConflictUntagAndEmpty()
    {
        foreach (int scenario in new[] { 0, 1, 2, 3 })
        {
            var engine = new DeleteConnection(); var clock = new ImageEventClock();
            await using var session = DeleteSession(new ControlledClient(engine), clock);
            await DeleteLoad(session, engine, A);
            var pending = session.ExecuteImageOperationAsync(DeleteRequest(session));
            var call = await engine.NextDelete();
            var outcome = scenario == 0 ? new ImageCommandResult(ImageCommandDisposition.DaemonResponse, 409,
                "private dependency conflict", Failure: new(ImageCommandFailureKind.Conflict, "Image has dependencies"))
                : new(ImageCommandDisposition.Acknowledged, 200, RemovalRecords: scenario == 1
                    ? [new(Untagged: "private/image:tag")] : scenario == 2 ? [] : [new(Deleted: B)],
                    ResponseBodyUnavailable: scenario == 3);
            call.Completion.SetResult(outcome);
            await DeleteObserve(session, engine, A);
            var result = await pending.WaitAsync(Limit);
            Check(result.Resolution == ImageOperationResolution.ObservedPresent && result.ObservedExists == true
                && result.CommandOutcome!.Disposition == outcome.Disposition && result.CommandOutcome.DaemonMessage == outcome.DaemonMessage
                && result.CommandOutcome.Failure == outcome.Failure && result.CommandOutcome.ResponseBodyUnavailable == outcome.ResponseBodyUnavailable,
                "Conflict, untag-only, empty and parent-only responses retain evidence independently of current existence");
            Check(engine.DeleteCalls == 1 && engine.MaximumConcurrent == 1, "No escalation or repeated mutation");
        }
        var missing = new DeleteConnection(); var timer = new ImageEventClock();
        await using var absent = DeleteSession(new ControlledClient(missing), timer);
        await DeleteLoad(absent, missing, A);
        var operation = absent.ExecuteImageOperationAsync(DeleteRequest(absent));
        (await missing.NextDelete()).Completion.SetResult(new(ImageCommandDisposition.DaemonResponse, 404, "already gone",
            Failure: new(ImageCommandFailureKind.NotFound, "Image missing")));
        await DeleteObserve(absent, missing);
        Check((await operation.WaitAsync(Limit)).Resolution == ImageOperationResolution.ObservedAbsent,
            "NotFound command still requires authoritative absence");
    }

    public static async Task ImageDeleteAdmissionBounds()
    {
        var engine = new DeleteConnection(); var clock = new ImageEventClock();
        await using var session = DeleteSession(new ControlledClient(engine), clock);
        await DeleteLoad(session, engine, A, B);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var stale = DeleteRequest(session) with { Target = new(session.ImageSnapshot.ConnectionGeneration - 1, A) };
        Check((await session.ExecuteImageOperationAsync(stale)).CommandOutcome!.Failure!.Kind == ImageCommandFailureKind.StaleConnection);
        Check((await session.ExecuteImageOperationAsync(DeleteRequest(session, C))).CommandOutcome!.Failure!.Kind == ImageCommandFailureKind.NotFound);
        Check((await session.ExecuteImageOperationAsync(DeleteRequest(session, "aaaa"))).Resolution == ImageOperationResolution.NotDispatched);
        Check((await session.ExecuteImageOperationAsync(DeleteRequest(session), canceled.Token)).CommandOutcome!.Failure!.Kind == ImageCommandFailureKind.Canceled
            && engine.DeleteCalls == 0);
        var request = DeleteRequest(session); var pending = session.ExecuteImageOperationAsync(request);
        var command = await engine.NextDelete();
        Check(ReferenceEquals(pending, session.ExecuteImageOperationAsync(request)), "Identical pending request shares completion");
        for (int i = 0; i < 100; ++i)
            Check((await session.ExecuteImageOperationAsync(DeleteRequest(session, B))).CommandOutcome!.Failure!.Kind == ImageCommandFailureKind.CapacityExceeded);
        Check(engine.DeleteCalls == 1 && !pending.IsCompleted);
        command.Acknowledge(); await DeleteObserve(session, engine); await pending.WaitAsync(Limit);
        var readsOnly = new ControlledConnection("no-delete");
        await using var noCapability = new DesktopSession(new ControlledClient(readsOnly), delay: new ControlledDelay());
        noCapability.Start(); await Load(noCapability, readsOnly, A);
        Check((await noCapability.ExecuteImageOperationAsync(DeleteRequest(noCapability))).Resolution == ImageOperationResolution.NotDispatched);
    }

    public static async Task ImageDeleteDeselectionAndCallerCancellation()
    {
        var engine = new DeleteConnection(); var clock = new ImageEventClock();
        await using var session = DeleteSession(new ControlledClient(engine), clock);
        await DeleteLoad(session, engine, A);
        session.SelectImage(A); (await engine.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        using var caller = new CancellationTokenSource();
        var operation = session.ExecuteImageOperationAsync(DeleteRequest(session), caller.Token);
        var call = await engine.NextDelete();
        session.ClearImageInspection(); session.SelectImage(null); caller.Cancel();
        await DeleteObserve(session, engine);
        var result = await operation.WaitAsync(Limit);
        Check(result.Resolution == ImageOperationResolution.ObservedAbsent
            && ReferenceEquals(result.CommandOutcome, call.CancellationOutcome) && result.CommandOutcome!.ResponseBodyUnavailable
            && result.CommandOutcome.DaemonMessage == "private cancellation evidence"
            && session.ImageSnapshot.SelectedImageId is null && engine.DeleteCalls == 1,
            "Caller cancellation preserves classified outcome and session-owned reconciliation survives deselection/closed Inspect");
    }

    public static async Task ImageDeleteReadAndEventFences()
    {
        var engine = new DeleteConnection(honorReads: false); var clock = new ImageEventClock();
        await using var session = DeleteSession(new ControlledClient(engine), clock);
        await DeleteLoad(session, engine, A, B);
        session.SelectImage(A); var oldDetail = await engine.NextInspection();
        var operation = session.ExecuteImageOperationAsync(DeleteRequest(session));
        var command = await engine.NextDelete(); command.Acknowledge();
        await DeleteState(session, o => o?.Phase == ImageOperationPhase.Reconciling);
        oldDetail.Succeed(Detail(A, "{\"obsolete\":true}"));
        var observationList = await engine.NextList(); observationList.Succeed(A, B);
        var staleObservation = await engine.NextInspection();
        await engine.Events.Emit(ImageEvent(A, "delete")); await engine.Events.Barrier();
        staleObservation.Succeed(Detail(A, "{\"event-obsolete\":true}"));
        var followup = await clock.Next(ImageFollowup);
        Check(session.ImageOperation!.Resolution is null && session.ImageOperation.ObservedExists is null
            && session.ImageSnapshot.Inspection is null, "Event-obsolete successful observation cannot resolve or republish Inspect");
        session.ClearImageInspection();
        followup.Release();
        // A fair event browsing turn occurs before the second operation turn.
        (await clock.Next(ImageBatch)).Release();
        (await engine.NextList()).Succeed(B);
        var next = await engine.NextList(); next.Succeed(B);
        Check((await operation.WaitAsync(Limit)).Resolution == ImageOperationResolution.ObservedAbsent
            && session.ImageSnapshot.SelectedImageId is null && engine.MaximumConcurrent == 1);
    }

    public static async Task ImageDeleteResponseLossRecoveryAndEngineFence()
    {
        foreach (string? identity in new string?[] { "same-image-engine", "unrelated-engine", null })
        {
            var old = new DeleteConnection(); var fresh = new DeleteConnection(engineId: identity); var clock = new ImageEventClock();
            await using var session = DeleteSession(new ControlledClient(old, fresh), clock, recovery: 1);
            await DeleteLoad(session, old, A, B);
            var pending = session.ExecuteImageOperationAsync(DeleteRequest(session));
            (await old.NextDelete()).Completion.SetResult(new(ImageCommandDisposition.Uncertain,
                Failure: new(ImageCommandFailureKind.TransportFailure, "Lost deletion response"), ResponseBodyUnavailable: true));
            await pending.WaitAsync(Limit);
            await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
            (await clock.Next(ImageEventRetry)).Release();
            (await DeleteList(fresh)).Succeed(B);
            if (identity == "same-image-engine")
            {
                (await DeleteList(fresh)).Succeed(B);
                await DeleteState(session, s => s?.Resolution == ImageOperationResolution.ObservedAbsent);
                Check(session.ImageOperation!.Request.Target.ConnectionGeneration < session.ImageOperation.ObservedConnectionGeneration);
            }
            else
            {
                await State(session, s => s.State == ResourceReadState.Ready);
                Check(session.ImageOperation!.Resolution == ImageOperationResolution.Unresolved,
                    "An unrelated/unknown Engine cannot resolve a retained operation");
            }
            Check(old.DeleteCalls == 1 && fresh.DeleteCalls == 0 && old.DisposeCount == 1
                && session.ImageOperation!.CommandOutcome!.Disposition == ImageCommandDisposition.Uncertain,
                "Deletion applied before response loss is observed after recovery without mutation replay");
        }
    }

    public static async Task ImageDeleteBoundedFailedObservations()
    {
        var engine = new DeleteConnection(); var clock = new ImageEventClock();
        await using var session = DeleteSession(new ControlledClient(engine), clock, observations: 2);
        await DeleteLoad(session, engine, A);
        var pending = session.ExecuteImageOperationAsync(DeleteRequest(session)); (await engine.NextDelete()).Acknowledge();
        await DeleteState(session, o => o?.Phase == ImageOperationPhase.Reconciling);
        var denied = Failure(EngineResourceFailureKind.AccessDenied);
        (await DeleteList(engine)).Fail(denied); (await clock.Next(ImageFollowup)).Release();
        (await DeleteList(engine)).Fail(denied);
        var result = await pending.WaitAsync(Limit);
        Check(result.Resolution == ImageOperationResolution.Unresolved && result.Observations == 2
            && ReferenceEquals(result.ObservationFailure, denied) && result.ObservedExists is null && engine.DeleteCalls == 1,
            "Bounded failed observations preserve original read failure independently of command evidence");
    }

    public static async Task ImageDeleteDeadlinesAndLateShutdown()
    {
        foreach (int scenario in new[] { 0, 1, 2 })
        {
            bool stalledCommand = scenario != 2;
            var engine = new DeleteConnection(honorReads: false, honorCommands: false); var clock = new ImageEventClock();
            var session = DeleteSession(new ControlledClient(engine), clock,
                readMilliseconds: stalledCommand ? 20000 : 100, commandMilliseconds: stalledCommand ? 100 : 30000);
            await DeleteLoad(session, engine, A);
            using var caller = new CancellationTokenSource();
            var pending = session.ExecuteImageOperationAsync(DeleteRequest(session), caller.Token); var command = await engine.NextDelete();
            if (scenario == 1) caller.Cancel();
            ListCall? list = null;
            if (!stalledCommand)
            {
                command.Acknowledge(); await DeleteState(session, o => o?.Phase == ImageOperationPhase.Reconciling);
                list = await engine.NextList();
                session.RefreshImages(); // Supersession must not disable an operation-read deadline.
            }
            await State(session, s => s.State == ResourceReadState.Unavailable);
            Check(session.ImageOperation!.Resolution == ImageOperationResolution.Unresolved && engine.DisposeCount == 0,
                "Deadline invalidates publication while cancellation-ignoring work remains owned");
            var ending = session.DisposeAsync().AsTask(); Check(!ending.IsCompleted);
            if (stalledCommand)
            {
                command.Acknowledge();
                // The dispatch fence may have started an ordinary pre-response list.
                if (engine.ListCalls > 1) (await engine.NextList()).Succeed(A);
            }
            else list!.Succeed(A);
            await ending.WaitAsync(Limit); await pending.WaitAsync(Limit);
            Check(engine.DisposeCount == 1 && !engine.DisposedWithDelete && !engine.DisposedWithActiveRead
                && session.ImageSnapshot.Images.Count == 0 && session.ImageSnapshot.Inspection is null
                && session.ImageOperation!.Resolution == ImageOperationResolution.Unresolved,
                "Shutdown joins command/read late completion before release and rejects obsolete acknowledgement/state");
        }
    }

    public static async Task ImageDeleteDeadlineCancellationEvidence()
    {
        var old = new DeleteConnection(); var fresh = new DeleteConnection(); var clock = new ImageEventClock();
        await using var session = DeleteSession(new ControlledClient(old, fresh), clock, recovery: 1, commandMilliseconds: 100);
        await DeleteLoad(session, old, A);
        var pending = session.ExecuteImageOperationAsync(DeleteRequest(session)); var command = await old.NextDelete();
        var result = await pending.WaitAsync(Limit);
        Check(result.Resolution == ImageOperationResolution.Unresolved && ReferenceEquals(result.CommandOutcome, command.CancellationOutcome),
            "Automatic deadline recovery retains the exact classified cancellation outcome");
        await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
        (await clock.Next(ImageEventRetry)).Release();
        (await DeleteList(fresh)).Succeed(); (await DeleteList(fresh)).Succeed();
        await DeleteState(session, o => o?.Resolution == ImageOperationResolution.ObservedAbsent);
        Check(ReferenceEquals(session.ImageOperation!.CommandOutcome, command.CancellationOutcome) && fresh.DeleteCalls == 0,
            "Recovered observation resolves current absence without rewriting canceled protocol evidence or replaying deletion");
    }

    public static async Task ImageDeleteDisconnectJoins()
    {
        foreach (bool inspect in new[] { false, true })
        {
            var engine = new DeleteConnection(honorReads: false, honorCommands: false); var clock = new ImageEventClock();
            var session = DeleteSession(new ControlledClient(engine), clock);
            await DeleteLoad(session, engine, A);
            var pending = session.ExecuteImageOperationAsync(DeleteRequest(session)); var command = await engine.NextDelete();
            InspectionCall? target = null;
            ListCall? blocked = null;
            if (inspect)
            {
                command.Acknowledge(); await DeleteState(session, o => o?.Phase == ImageOperationPhase.Reconciling);
                var list = await engine.NextList(); list.Succeed(A);
                target = await engine.NextInspection();
            }
            else if (engine.ListCalls > 1) blocked = await engine.NextList();
            var ending = session.DisconnectAsync();
            Check(!ending.IsCompleted && engine.DisposeCount == 0 && session.EngineState == EngineConnectionState.NotConnected);
            if (inspect) target!.Succeed(Detail(A));
            else
            {
                command.Acknowledge();
                if (blocked is not null) blocked.Succeed(A);
                else if (engine.ListCalls > 1) (await engine.NextList()).Succeed(A);
            }
            await ending.WaitAsync(Limit); await pending.WaitAsync(Limit);
            Check(!engine.DisposedWithDelete && !engine.DisposedWithActiveRead && engine.DisposeCount == 1
                && session.ImageOperation!.Resolution == ImageOperationResolution.Unresolved);
            await session.DisposeAsync();
        }
    }

    public static async Task ImageDeletePreResponseReadFenceAndNotFoundInspect()
    {
        var engine = new DeleteConnection(honorReads: false); var clock = new ImageEventClock();
        await using var session = DeleteSession(new ControlledClient(engine), clock);
        await DeleteLoad(session, engine, A, B);
        var pending = session.ExecuteImageOperationAsync(DeleteRequest(session));
        var command = await engine.NextDelete();
        session.RefreshImages(); var beforeResponse = await engine.NextList();
        command.Acknowledge(); await DeleteState(session, o => o?.Phase == ImageOperationPhase.Reconciling);
        beforeResponse.Succeed(B);
        var authoritative = await engine.NextList();
        Check(!pending.IsCompleted && session.ImageOperation!.ObservedExists is null,
            "A pre-response list proving absence is fenced from operation outcome and browsing publication");
        authoritative.Succeed(A, B);
        var target = await engine.NextInspection(); target.Fail(Failure(EngineResourceFailureKind.NotFound));
        var result = await pending.WaitAsync(Limit);
        Check(result.Resolution == ImageOperationResolution.ObservedAbsent
            && result.ObservationFailure!.Kind == EngineResourceFailureKind.NotFound
            && session.ImageSnapshot.Images.Single().Id == B && engine.MaximumConcurrent == 1,
            "Post-response target NotFound supersedes its list row without inferring absence from command evidence");
    }

    public static async Task ImageDeleteJoinedRetryAndCoexistence()
    {
        var old = new DeleteConnection(honorCommands: false); var fresh = new DeleteConnection(); var clock = new ImageEventClock();
        var client = new ControlledClient(old, fresh);
        await using var session = DeleteSession(client, clock);
        await DeleteLoad(session, old, A);
        var pending = session.ExecuteImageOperationAsync(DeleteRequest(session)); var late = await old.NextDelete();
        session.Retry();
        Check(late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1);
        late.Acknowledge(); await pending.WaitAsync(Limit);
        (await DeleteList(fresh)).Succeed(); (await DeleteList(fresh)).Succeed();
        await DeleteState(session, s => s?.Resolution == ImageOperationResolution.ObservedAbsent);
        Check(fresh.DeleteCalls == 0 && old.DisposeCount == 1 && !old.DisposedWithDelete
            && session.ImageOperation!.CommandOutcome!.Disposition == ImageCommandDisposition.Acknowledged
            && session.ImageOperation.Endpoint == old.Metadata.Endpoint
            && session.ImageOperation.EngineId == old.Metadata.EngineId,
            "Retry retains historical acknowledgement and joins the original command; fresh observation establishes absence without replay");

        var all = new ImageDeleteAllConnection(); var timing = new ImageEventClock();
        await using var together = DeleteSession(new ControlledClient(all), timing);
        together.Start(); await Load(together, all, A, B);
        await Until(() => together.ContainerSnapshot.State == ResourceReadState.Ready);
        together.SelectContainer(AllCapabilitiesConnection.ContainerId);
        await Until(() => together.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
        Check(together.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => together.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var container = together.ExecuteContainerOperationAsync(new(new(together.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        await all.CommandStarted.Task.WaitAsync(Limit);
        var image = together.ExecuteImageOperationAsync(DeleteRequest(together));
        await all.DeleteStarted.Task.WaitAsync(Limit);
        all.DeleteDone.SetResult(new(ImageCommandDisposition.Acknowledged, 200));
        await DeleteObserve(together, all, B); await image.WaitAsync(Limit);
        Check(!container.IsCompleted && !all.CommandToken.IsCancellationRequested && !all.Logs.Disposed
            && !all.Events.Disposed && together.ContainerSnapshot.Inspection is not null,
            "Image deletion/parent snapshot refresh preserves independent container commands, Inspect, events and Logs");
        await together.DisconnectAsync().WaitAsync(Limit); await container.WaitAsync(Limit);
        Check(all.DisposeCount == 1 && !all.DisposedWithActiveRead && all.Logs.Disposed && all.Events.Disposed,
            "Shared shutdown joins all borrowed capability work before releasing the connection");
    }

    private sealed class ImageDeleteAllConnection() : AllCapabilitiesConnection("image-delete-all"), IImageCommands
    {
        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImageCommandResult> DeleteDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ImageCommandResult> DeleteImageAsync(string reference, CancellationToken cancellationToken = default)
        { Check(reference == A); DeleteStarted.TrySetResult(); return await DeleteDone.Task.WaitAsync(cancellationToken); }
    }

    private sealed class DeleteConnection(bool honorReads = true, bool honorCommands = true, string? engineId = "same-image-engine")
        : ControlledConnection("delete", honorReads), IImageCommands, IEngineEventSource
    {
        private readonly Channel<ImageDeleteCall> deletes = Channel.CreateUnbounded<ImageDeleteCall>();
        private int activeDeletes;
        public int DeleteCalls;
        public bool DisposedWithDelete;
        public ImageObservation Events { get; } = new();
        public override EngineMetadata Metadata => base.Metadata with { EngineId = engineId };
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(Events));
        public async Task<ImageCommandResult> DeleteImageAsync(string reference, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DeleteCalls); Interlocked.Increment(ref activeDeletes);
            var call = new ImageDeleteCall(reference, cancellationToken); deletes.Writer.TryWrite(call);
            try { return await (honorCommands ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new ImageCommandCanceledException(call.CancellationOutcome, cancellationToken); }
            finally { Interlocked.Decrement(ref activeDeletes); }
        }
        public async Task<ImageDeleteCall> NextDelete() => await deletes.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public override ValueTask DisposeAsync()
        { DisposedWithDelete |= activeDeletes != 0; return base.DisposeAsync(); }
    }
    private sealed class ImageDeleteCall(string identity, CancellationToken token)
    {
        public string Identity { get; } = identity;
        public CancellationToken Token { get; } = token;
        public ImageCommandResult CancellationOutcome { get; } = new(ImageCommandDisposition.Uncertain, 200,
            "private cancellation evidence", Failure: new(ImageCommandFailureKind.Canceled, "Command canceled"), ResponseBodyUnavailable: true);
        public TaskCompletionSource<ImageCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Acknowledge() => Completion.SetResult(new(ImageCommandDisposition.Acknowledged, 200, RemovalRecords: []));
    }
}
