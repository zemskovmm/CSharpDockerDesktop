using System.Collections.Concurrent;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class OperationCoordinatorChecks
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Followup = TimeSpan.FromMilliseconds(23), Batch = TimeSpan.FromMilliseconds(17), Retry = TimeSpan.FromSeconds(7);
    private static readonly DateTimeOffset Before = DateTimeOffset.FromUnixTimeSeconds(100);
    private static ContainerSummary Row(string id = A, string state = "exited") => new(id, ["/actual"], "image", null,
        state, state, null, null, null, null, null);
    private static ContainerInspection Detail(string id = A, string state = "exited", DateTimeOffset? started = null) => new(id,
        "/actual", "image", null, null, new(state, state is "running" or "restarting", false,
            state == "restarting", false, 0, null, started ?? Before, null, null), null, null, null, "{\"private\":\"actual Inspect\"}");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static DesktopSession Session(Client client, Clock clock, int observations = 3, int recovery = 0)
        => new(client, new() { MaximumRecoveryAttempts = recovery, RetryDelay = Retry }, clock,
            reconciliation: new() { BatchInterval = Batch },
            operationOptions: new() { MaximumObservations = observations, FollowupInterval = Followup });
    private static ContainerOperationRequest Request(DesktopSession session, ContainerCommand command = ContainerCommand.Start,
        string id = A, bool force = false, int? grace = null) => new(new(session.ContainerSnapshot.ConnectionGeneration, id), command, force, grace);
    private static async Task State(DesktopSession session, Func<ContainerBrowsingSnapshot, bool> predicate)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.ContainerSnapshot)) done.TrySetResult(); }
        session.ContainersChanged += Observe;
        try { Observe(null, EventArgs.Empty); await done.Task.WaitAsync(Limit); }
        finally { session.ContainersChanged -= Observe; }
    }
    private static async Task Operation(DesktopSession session, Func<ContainerOperationSnapshot?, bool> predicate)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.ContainerOperation)) done.TrySetResult(); }
        session.ContainerOperationChanged += Observe;
        try { Observe(null, EventArgs.Empty); await done.Task.WaitAsync(Limit); }
        finally { session.ContainerOperationChanged -= Observe; }
    }
    private static async Task Load(DesktopSession session, Connection engine, params ContainerSummary[] rows)
    {
        session.Start(); (await engine.NextList()).Succeed(rows);
        await State(session, s => s.State == ResourceReadState.Ready);
    }
    private static async Task Observe(Connection engine, string state = "running", string id = A, DateTimeOffset? started = null,
        params ContainerSummary[] others)
    {
        (await engine.NextList()).Succeed([Row(id, state), ..others]);
        var inspect = await engine.NextInspection();
        Check(inspect.Identity == id, "Reconciliation inspects the operation target, independently of selection");
        inspect.Succeed(Detail(id, state, started));
    }

    public static async Task AcknowledgedTransitionsAndRestartEvidence()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        await Load(session, engine, Row());
        var retained = new List<ContainerOperationSnapshot>();
        session.ContainerOperationChanged += (_, _) => { if (session.ContainerOperation is { } current) lock (retained) retained.Add(current); };
        var start = session.ExecuteContainerOperationAsync(Request(session));
        var command = await engine.NextCommand();
        Check(command.Command == ContainerCommand.Start && command.Identity == A && session.ContainerOperation!.Phase == ContainerOperationPhase.CommandPending,
            "Explicit Start exposes full identity and pending command");
        command.Acknowledge();
        await Observe(engine, "restarting");
        var followup = await clock.Next(Followup);
        Check(!start.IsCompleted && session.ContainerOperation!.ObservedState!.Restarting == true,
            "Acknowledgment and transitional running flag cannot claim the requested stable state");
        followup.Release(); await Observe(engine);
        var started = await start.WaitAsync(Limit);
        Check(started.Resolution == ContainerOperationResolution.ObservedRequestedState && started.Observations == 2
            && started.CommandOutcome!.Acknowledgement == ContainerCommandAcknowledgement.Accepted && session.ContainerSnapshot.SelectedContainerId is null,
            "Start completes from bounded authoritative observations without selecting the target");
        Check(retained.First().Phase == ContainerOperationPhase.CommandPending && retained.Any(s => s.Phase == ContainerOperationPhase.Reconciling),
            "Immutable notifications expose command and reconciliation phases");

        var stop = session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Stop, grace: 8));
        command = await engine.NextCommand(); Check(command.Grace == 8, "Explicit grace is forwarded unchanged");
        command.Acknowledge(ContainerCommandAcknowledgement.AlreadyInRequestedState);
        await Observe(engine, "exited");
        Check((await stop.WaitAsync(Limit)).Resolution == ContainerOperationResolution.ObservedRequestedState, "Stop requires actual stopped state even after 304");

        var restart = session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Restart));
        await Observe(engine, "running", started: Before);
        command = await engine.NextCommand(); command.Acknowledge();
        await Observe(engine, "running", started: Before);
        followup = await clock.Next(Followup);
        Check(!restart.IsCompleted, "A pre-restart running state cannot prove restart from HTTP success");
        followup.Release(); await Observe(engine, "running", started: Before.AddSeconds(1));
        Check((await restart.WaitAsync(Limit)).Resolution == ContainerOperationResolution.ObservedRequestedState && engine.CommandCalls == 3
            && engine.MaximumReads == 1 && engine.MaximumCommands == 1, "Restart needs fresh start-time evidence and work remains bounded");
    }

    public static async Task RejectionsDeleteAndExplicitForce()
    {
        foreach (var kind in new[] { ContainerCommandFailureKind.Conflict, ContainerCommandFailureKind.NotFound,
            ContainerCommandFailureKind.AccessDenied, ContainerCommandFailureKind.InvalidRequest, ContainerCommandFailureKind.DaemonFailure })
        {
            var engine = new Connection(); var clock = new Clock();
            await using var session = Session(new(engine), clock);
            await Load(session, engine, Row(state: "running"));
            var request = Request(session, ContainerCommand.Delete);
            var operation = session.ExecuteContainerOperationAsync(request);
            var command = await engine.NextCommand();
            Check(command.Command == ContainerCommand.Delete && !command.Force && command.Grace is null, "Default Delete never stops first or escalates force");
            var outcome = new ContainerCommandResult(ContainerCommand.Delete, ContainerCommandDisposition.DaemonResponse,
                kind == ContainerCommandFailureKind.Conflict ? 409 : 404, "PRIVATE daemon original message", Failure: new(kind, "Classified command failure"));
            command.Completion.SetResult(outcome);
            if (kind == ContainerCommandFailureKind.NotFound) (await engine.NextList()).Succeed();
            else await Observe(engine);
            var result = await operation.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, outcome) && result.Observations == 1 && engine.CommandCalls == 1,
                "Every daemon rejection is reconciled once and original classified evidence is preserved without replay");
            Check(result.Resolution == (kind == ContainerCommandFailureKind.NotFound ? ContainerOperationResolution.ObservedRequestedState
                : ContainerOperationResolution.ObservedDifferentState), "Observed absence is distinct from a preserved command failure");
        }
        foreach (bool force in new[] { false, true })
        {
            var engine = new Connection(); var clock = new Clock();
            await using var session = Session(new(engine), clock);
            await Load(session, engine, Row(state: force ? "running" : "exited"));
            var operation = session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Delete, force: force));
            var command = await engine.NextCommand(); Check(command.Force == force, "Force Delete requires explicit caller choice");
            command.Acknowledge(); (await engine.NextList()).Succeed();
            Check((await operation.WaitAsync(Limit)).ObservedExists == false && engine.CommandCalls == 1 && session.ContainerSnapshot.Containers.Count == 0,
                "Acknowledged Delete finishes only from authoritative disappearance");
        }
        // Volumes remain an invariant of IContainerCommands: its production protocol tests assert v=false for both choices.
    }

    public static async Task CancellationAndUnknownOutcomes()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock, observations: 2);
        await Load(session, engine, Row());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var before = await session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Delete), canceled.Token);
        Check(before.CommandOutcome!.Disposition == ContainerCommandDisposition.NotDispatched && engine.CommandCalls == 0 && session.ContainerOperation is null,
            "Cancellation before request acceptance emits no mutation");
        foreach (bool dispatched in new[] { false, true })
        {
            var pending = session.ExecuteContainerOperationAsync(Request(session));
            var command = await engine.NextCommand();
            var outcome = new ContainerCommandResult(ContainerCommand.Start, dispatched ? ContainerCommandDisposition.Uncertain : ContainerCommandDisposition.NotDispatched,
                Failure: new(ContainerCommandFailureKind.Canceled, "Canceled normally"));
            command.Completion.SetException(new ContainerCommandCanceledException(outcome, new CancellationToken(true)));
            if (dispatched) await Observe(engine);
            var result = await pending.WaitAsync(Limit);
            Check(ReferenceEquals(result.CommandOutcome, outcome) && result.Resolution == (dispatched ? ContainerOperationResolution.ObservedRequestedState
                : ContainerOperationResolution.NotDispatched), "Cancellation exception Outcome controls reconciliation, never exception type alone");
        }
        using var caller = new CancellationTokenSource();
        var after = session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Stop), caller.Token);
        var dispatchedCall = await engine.NextCommand(); caller.Cancel();
        await Observe(engine, "exited");
        Check((await after.WaitAsync(Limit)).CommandOutcome!.Failure!.Kind == ContainerCommandFailureKind.Canceled
            && after.Result.Resolution == ContainerOperationResolution.ObservedRequestedState, "Caller cancellation after possible dispatch still observes applied mutation");
        var uncertain = session.ExecuteContainerOperationAsync(Request(session));
        (await engine.NextCommand()).Completion.SetResult(new(ContainerCommand.Start, ContainerCommandDisposition.Uncertain,
            Failure: new(ContainerCommandFailureKind.UnexpectedResponse, "Uncertain response")));
        await Observe(engine, "exited"); (await clock.Next(Followup)).Release(); await Observe(engine, "exited");
        var unresolved = await uncertain.WaitAsync(Limit);
        Check(unresolved.Resolution == ContainerOperationResolution.Unresolved && unresolved.Observations == 2 && unresolved.ObservedState!.Running == false,
            "An unestablished transition reaches bounded explicit uncertainty without claiming success or replaying");
    }

    public static async Task DuplicateBoundsAndGenerationValidation()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        await Load(session, engine, Row(), Row(B));
        var request = Request(session);
        var first = session.ExecuteContainerOperationAsync(request);
        var command = await engine.NextCommand();
        for (int i = 0; i < 1000; ++i) Check(ReferenceEquals(first, session.ExecuteContainerOperationAsync(request)), "Pending duplicates share the original operation task");
        var different = await session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Stop, B));
        Check(different.CommandOutcome!.Failure!.Kind == ContainerCommandFailureKind.CapacityExceeded && engine.CommandCalls == 1
            && session.ContainerOperation!.Request == request, "Single-resource capacity has no retained backlog or duplicate dispatch");
        command.Acknowledge();
        var read = await engine.NextList();
        Check(ReferenceEquals(first, session.ExecuteContainerOperationAsync(request)), "Duplicate remains coalesced while reconciliation is pending");
        read.Succeed(Row(state: "running"), Row(B)); (await engine.NextInspection()).Succeed(Detail(state: "running")); await first.WaitAsync(Limit);
        foreach (var invalid in new[] { request with { Target = new(request.Target.ConnectionGeneration - 1, A) },
            request with { Target = new(request.Target.ConnectionGeneration, A[..12]) }, request with { ForceDelete = true },
            request with { GracePeriodSeconds = -1 } })
        {
            var result = await session.ExecuteContainerOperationAsync(invalid);
            Check(result.Resolution == ContainerOperationResolution.NotDispatched, "Stale, partial identity and invalid parameters never dispatch");
        }
        Check(engine.CommandCalls == 1 && session.ContainerOperation!.OperationId == first.Result.OperationId,
            "Rejected invocations do not overwrite the one retained operation");
    }

    public static async Task StaleReadsEventRacesAndDeselectedTargets()
    {
        var engine = new Connection(honorReads: false); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        await Load(session, engine, Row(), Row(B));
        session.RefreshContainers(); var stale = await engine.NextList();
        var operation = session.ExecuteContainerOperationAsync(Request(session));
        var command = await engine.NextCommand(); command.Acknowledge();
        await Operation(session, o => o?.Phase == ContainerOperationPhase.Reconciling);
        Check(stale.Token.IsCancellationRequested && !operation.IsCompleted, "Pre-command read is invalidated across dispatch/completion");
        stale.Succeed(Row(state: "running"), Row(B));
        var fresh = await engine.NextList();
        Check(!operation.IsCompleted && session.ContainerOperation!.ObservedState is null, "A late pre-command success cannot establish operation outcome");
        fresh.Succeed(Row(state: "running"), Row(B));
        var inspecting = await engine.NextInspection();
        await engine.Events.Emit(new("container", "die", A, null, null, new(200, 200_000_000_000)));
        await engine.Events.Barrier();
        inspecting.Succeed(Detail(state: "running"));
        var followup = await clock.Next(Followup);
        Check(!operation.IsCompleted && session.ContainerOperation!.ObservedState is null,
            "An event crossing authoritative observation invalidates its result; event payloads cannot claim final state");
        (await engine.NextList()).Succeed(Row(), Row(B));
        followup.Release(); await Observe(engine, "running", others: [Row(B)]);
        var result = await operation.WaitAsync(Limit);
        Check(result.Resolution == ContainerOperationResolution.ObservedRequestedState && engine.CommandCalls == 1 && engine.MaximumReads == 1,
            "Command/event races converge with serialized rereads and no mutation replay");

        session.SelectContainer(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var detailB = session.ContainerSnapshot.Inspection;
        var stop = session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Stop));
        (await engine.NextCommand()).Acknowledge(); await Observe(engine, "exited", others: [Row(B)]); await stop.WaitAsync(Limit);
        Check(session.ContainerSnapshot.SelectedContainerId == B && ReferenceEquals(session.ContainerSnapshot.Inspection, detailB),
            "Reconciliation targets a deselected container without changing or clearing unrelated selection/details");
    }

    public static async Task SelectedInspectionDisappearanceAndNavigation()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        await Load(session, engine, Row(), Row(B));
        session.SelectContainer(A); (await engine.NextInspection()).Succeed(Detail());
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var start = session.ExecuteContainerOperationAsync(Request(session));
        var command = await engine.NextCommand();
        Check(session.ContainerSnapshot.Inspection is null, "Dispatch immediately invalidates stale selected details/Inspect");
        command.Acknowledge();
        await Observe(engine, "running", others: [Row(B)]); await start.WaitAsync(Limit);
        Check(session.ContainerSnapshot.Inspection!.State!.Running == true && session.ContainerSnapshot.SelectedContainerId == A,
            "Reconciliation refreshes selected authoritative inspection");
        var deleting = session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Delete, force: true));
        (await engine.NextCommand()).Acknowledge(); (await engine.NextList()).Succeed(Row(state: "running"), Row(B));
        var vanished = await engine.NextInspection();
        vanished.Completion.SetResult(EngineResourceResult<ContainerInspection>.Failed(new(EngineResourceFailureKind.NotFound, "Gone", 404, "PRIVATE disappearance")));
        var deleted = await deleting.WaitAsync(Limit);
        Check(deleted.Resolution == ContainerOperationResolution.ObservedRequestedState
            && deleted.ObservationFailure?.Kind == EngineResourceFailureKind.NotFound
            && deleted.ObservationFailure.DaemonMessage == "PRIVATE disappearance", "Authoritative absence preserves the original classified inspection failure");
        Check(session.ContainerSnapshot.SelectedContainerId is null && session.ContainerSnapshot.Inspection is null
            && session.ContainerSnapshot.Containers.All(c => c.Id != A), "Target inspection not-found removes stale row, selection and raw Inspect");

        // Selection changes and closing details during target observation never get reversed by its late completion.
        session.RefreshContainers(); (await engine.NextList()).Succeed(Row(), Row(B)); await State(session, s => s.Containers.Count == 2);
        session.SelectContainer(A); (await engine.NextInspection()).Succeed(Detail()); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var stopping = session.ExecuteContainerOperationAsync(Request(session, ContainerCommand.Stop));
        (await engine.NextCommand()).Acknowledge();
        (await engine.NextList()).Succeed(Row(), Row(B)); var target = await engine.NextInspection();
        session.SelectContainer(B); session.ClearContainerInspection(); session.RefreshContainers();
        Check(!target.Token.IsCancellationRequested && session.ContainerSnapshot.SelectedContainerId == B && session.ContainerSnapshot.Inspection is null,
            "Navigation and manual refresh remain immediate and do not cancel target verification");
        target.Succeed(Detail()); await stopping.WaitAsync(Limit);
        (await engine.NextList()).Succeed(Row(), Row(B)); await State(session, s => s.State == ResourceReadState.Ready);
        Check(session.ContainerSnapshot.SelectedContainerId == B && session.ContainerSnapshot.InspectionState == ResourceReadState.NotRequested,
            "Target verification cannot reopen a closed or replaced detail view");
    }

    public static async Task LostResponseRecoveryWithoutReplay()
    {
        var old = new Connection(); var fresh = new Connection(); var clock = new Clock(); var client = new Client(old, fresh);
        await using var session = Session(client, clock, recovery: 1);
        await Load(session, old, Row());
        var request = Request(session);
        var operation = session.ExecuteContainerOperationAsync(request);
        (await old.NextCommand()).Completion.SetResult(new(ContainerCommand.Start, ContainerCommandDisposition.Uncertain,
            Failure: new(ContainerCommandFailureKind.TransportFailure, "Applied mutation response lost")));
        var unresolved = await operation.WaitAsync(Limit);
        var retry = await clock.Next(Retry);
        Check(unresolved.Resolution == ContainerOperationResolution.Unresolved && old.DisposeCount == 1 && old.CommandCalls == 1,
            "Command-detected transport loss enters existing connection recovery with explicit uncertain evidence");
        retry.Release();
        (await fresh.NextList()).Succeed(Row(state: "running")); // Fresh normal connection snapshot.
        await Observe(fresh);
        await Operation(session, o => o?.Resolution == ContainerOperationResolution.ObservedRequestedState);
        Check(fresh.CommandCalls == 0 && session.ContainerOperation!.Request == request
            && session.ContainerOperation.ObservedConnectionGeneration != request.Target.ConnectionGeneration
            && session.ContainerSnapshot.Containers.Single().State == "running", "Recovered authoritative reads recognize applied mutation without redispatching it");
        Check(!old.DisposedWithWork && !fresh.DisposedWithWork, "Recovery disposes the previous borrowed connection only after joined work");
    }

    public static async Task JoinedRetryDisconnectAndLateShutdown()
    {
        foreach (bool retry in new[] { false, true })
        {
            var old = new Connection(honorCommands: false); var fresh = new Connection(); var clock = new Clock(); var client = new Client(old, fresh);
            var session = Session(client, clock);
            await Load(session, old, Row());
            var operation = session.ExecuteContainerOperationAsync(Request(session)); var late = await old.NextCommand();
            Task ending;
            if (retry) { session.Retry(); ending = Task.CompletedTask; }
            else ending = session.DisposeAsync().AsTask();
            Check(late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1 && (retry || !ending.IsCompleted),
                "Retry/shutdown cancels and joins a cancellation-ignoring command before releasing the borrowed connection");
            late.Acknowledge(); await operation.WaitAsync(Limit);
            if (retry)
            {
                (await fresh.NextList()).Succeed(Row()); await Observe(fresh, "running");
                await Operation(session, o => o?.Resolution == ContainerOperationResolution.ObservedRequestedState);
                Check(fresh.CommandCalls == 0 && session.ContainerOperation!.CommandOutcome!.Disposition == ContainerCommandDisposition.Uncertain,
                    "Late original-generation acknowledgment is rejected; replacement observes without dispatch");
                await session.DisposeAsync();
            }
            else
            {
                await ending.WaitAsync(Limit);
                Check(session.ContainerSnapshot.Containers.Count == 0 && session.ContainerOperation!.Resolution == ContainerOperationResolution.Unresolved,
                    "Late completion cannot republish container state after shutdown");
            }
            Check(old.DisposeCount == 1 && !old.DisposedWithWork, "Connection disposal follows joined command work");
        }
        // Join target follow-up reads as well as commands.
        var ignored = new Connection(honorReads: false); var timer = new Clock(); var closing = Session(new(ignored), timer);
        await Load(closing, ignored, Row());
        var pending = closing.ExecuteContainerOperationAsync(Request(closing)); (await ignored.NextCommand()).Acknowledge();
        (await ignored.NextList()).Succeed(Row(state: "running")); var target = await ignored.NextInspection();
        var shutdown = closing.DisposeAsync().AsTask();
        Check(target.Token.IsCancellationRequested && !shutdown.IsCompleted && ignored.DisposeCount == 0, "Shutdown joins cancellation-ignoring target inspection");
        target.Succeed(Detail(state: "running")); await shutdown.WaitAsync(Limit); await pending.WaitAsync(Limit);
        Check(!ignored.DisposedWithWork && closing.ContainerSnapshot.Inspection is null, "Late target reads are rejected before disposing connection");
    }

    public static async Task ObservationFailuresAndCanceledPreflight()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock, observations: 2);
        await Load(session, engine, Row());
        var operation = session.ExecuteContainerOperationAsync(Request(session));
        (await engine.NextCommand()).Acknowledge();
        var failure = new EngineResourceFailure(EngineResourceFailureKind.AccessDenied, "Observation denied", 403, "PRIVATE observation message");
        (await engine.NextList()).Completion.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Failed(failure));
        (await clock.Next(Followup)).Release();
        (await engine.NextList()).Completion.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Failed(failure));
        var unresolved = await operation.WaitAsync(Limit);
        Check(unresolved.Resolution == ContainerOperationResolution.Unresolved && ReferenceEquals(unresolved.ObservationFailure, failure)
            && session.ContainerSnapshot.State == ResourceReadState.Failed && engine.CommandCalls == 1,
            "Read failures remain distinct with original daemon content and bounded uncertain resolution");
        session.RefreshContainers(); (await engine.NextList()).Succeed(Row()); await State(session, s => s.State == ResourceReadState.Ready);
        var deadline = session.ExecuteContainerOperationAsync(Request(session));
        (await engine.NextCommand()).Completion.SetResult(new(ContainerCommand.Start, ContainerCommandDisposition.Uncertain,
            Failure: new(ContainerCommandFailureKind.Deadline, "Response deadline")));
        await Observe(engine);
        Check((await deadline.WaitAsync(Limit)).Resolution == ContainerOperationResolution.ObservedRequestedState,
            "Uncertain response evidence can establish current requested state from observation without command replay");

        var beforeEngine = new Connection(honorReads: false); var beforeClock = new Clock();
        await using var beforeSession = Session(new(beforeEngine), beforeClock);
        await Load(beforeSession, beforeEngine, Row());
        using var cancel = new CancellationTokenSource();
        var before = beforeSession.ExecuteContainerOperationAsync(Request(beforeSession, ContainerCommand.Restart), cancel.Token);
        var preflight = await beforeEngine.NextList(); cancel.Cancel();
        var canceled = await before.WaitAsync(Limit);
        Check(canceled.Resolution == ContainerOperationResolution.NotDispatched && beforeEngine.CommandCalls == 0,
            "Cancellation during restart preflight completes normally before dispatch even when reads ignore cancellation");
        preflight.Succeed(Row()); (await beforeEngine.NextInspection()).Succeed(Detail());
        await State(beforeSession, s => s.State == ResourceReadState.Ready);
        Check(beforeSession.ContainerOperation!.Resolution == ContainerOperationResolution.NotDispatched,
            "Late preflight read does not change the canceled command outcome");
    }

    public static async Task DisconnectAndUnrelatedInspectionProgress()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        await Load(session, engine, Row(), Row(B));
        session.SelectContainer(B); var interrupted = await engine.NextInspection();
        var operation = session.ExecuteContainerOperationAsync(Request(session));
        (await engine.NextCommand()).Acknowledge();
        Check(interrupted.Token.IsCancellationRequested && session.ContainerSnapshot.SelectedContainerId == B,
            "Dispatch cancels a pre-command read without changing unrelated selection");
        await Operation(session, o => o?.Phase == ContainerOperationPhase.Reconciling);
        await Observe(engine, others: [Row(B)]); await operation.WaitAsync(Limit);
        var resumed = await engine.NextInspection(); Check(resumed.Identity == B, "Interrupted unrelated inspection is retained for a new read");
        resumed.Succeed(Detail(B)); await State(session, s => s.InspectionState == ResourceReadState.Ready);

        var old = new Connection(honorCommands: false); var fresh = new Connection(); var timer = new Clock(); var client = new Client(old, fresh);
        await using var disconnecting = Session(client, timer);
        await Load(disconnecting, old, Row());
        var pending = disconnecting.ExecuteContainerOperationAsync(Request(disconnecting)); var late = await old.NextCommand();
        var disconnected = disconnecting.DisconnectAsync();
        Check(disconnecting.EngineState == EngineConnectionState.NotConnected && late.Token.IsCancellationRequested
            && !disconnected.IsCompleted && old.DisposeCount == 0, "Explicit disconnect invalidates immediately and joins outstanding borrowed work");
        disconnecting.Start();
        Check(client.Calls == 1, "A new Start remains serialized behind the disconnected connection's outstanding command");
        late.Acknowledge(); await pending.WaitAsync(Limit); await disconnected.WaitAsync(Limit);
        (await fresh.NextList()).Succeed(Row()); await Observe(fresh);
        await Operation(disconnecting, o => o?.Resolution == ContainerOperationResolution.ObservedRequestedState);
        Check(old.DisposeCount == 1 && !old.DisposedWithWork && fresh.CommandCalls == 0,
            "Disconnect/reconnect joins the prior generation and obtains authoritative state without replay");
    }

    public static async Task ObservationLossAndJoinedLateList()
    {
        var engine = new Connection(); var clock = new Clock();
        await using var session = Session(new(engine), clock);
        await Load(session, engine, Row());
        var operation = session.ExecuteContainerOperationAsync(Request(session)); (await engine.NextCommand()).Acknowledge();
        (await engine.NextList()).Completion.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Failed(
            new(EngineResourceFailureKind.TransportLoss, "Reconciliation connection lost")));
        await State(session, s => s.State == ResourceReadState.Unavailable); await operation.WaitAsync(Limit);
        Check(session.ContainerOperation!.Resolution == ContainerOperationResolution.Unresolved,
            "Transport loss during reconciliation publishes connection invalidation and unresolved operation");

        var lateEngine = new Connection(honorReads: false); var timer = new Clock(); var closing = Session(new(lateEngine), timer);
        await Load(closing, lateEngine, Row());
        var pending = closing.ExecuteContainerOperationAsync(Request(closing)); (await lateEngine.NextCommand()).Acknowledge();
        var list = await lateEngine.NextList(); var ending = closing.DisposeAsync().AsTask();
        Check(list.Token.IsCancellationRequested && !ending.IsCompleted && lateEngine.DisposeCount == 0,
            "Shutdown joins an operation list that ignores cancellation");
        list.Succeed(Row(state: "running")); await pending.WaitAsync(Limit); await ending.WaitAsync(Limit);
        Check(lateEngine.InspectionCalls == 0 && !lateEngine.DisposedWithWork && closing.ContainerSnapshot.Containers.Count == 0,
            "A late canceled operation list cannot start new inspection work or publish after shutdown");
    }

    private sealed class Client(params Connection[] connections) : IEngineConnectionClient
    {
        public int Calls;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(EngineConnectionAttempt.Connected(connections[Interlocked.Increment(ref Calls) - 1])); }
    }
    private sealed class Connection(bool honorReads = true, bool honorCommands = true) : IEngineConnection, IContainerReader, IContainerCommands, IEngineEventSource
    {
        private readonly Channel<ListCall> lists = Channel.CreateUnbounded<ListCall>();
        private readonly Channel<InspectionCall> inspections = Channel.CreateUnbounded<InspectionCall>();
        private readonly Channel<CommandCall> commands = Channel.CreateUnbounded<CommandCall>();
        private int reads, commandWork;
        public int CommandCalls, InspectionCalls, MaximumCommands, MaximumReads, DisposeCount;
        public bool DisposedWithWork;
        public Subscription Events { get; } = new();
        public EngineMetadata Metadata { get; } = new(EngineEndpoint.LocalWindows, "simulated", new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 40)),
            null, null, null, "same-engine", null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(Events));
        public async Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
        {
            Check(DisposeCount == 0, "No read on disposed connection"); MaximumReads = Math.Max(MaximumReads, Interlocked.Increment(ref reads));
            var call = new ListCall(cancellationToken); lists.Writer.TryWrite(call);
            try { return await (honorReads ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            finally { Interlocked.Decrement(ref reads); }
        }
        public async Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string id, CancellationToken cancellationToken = default)
        {
            Check(DisposeCount == 0, "No inspection on disposed connection"); MaximumReads = Math.Max(MaximumReads, Interlocked.Increment(ref reads));
            Interlocked.Increment(ref InspectionCalls);
            var call = new InspectionCall(id, cancellationToken); inspections.Writer.TryWrite(call);
            try { return await (honorReads ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            finally { Interlocked.Decrement(ref reads); }
        }
        private async Task<ContainerCommandResult> Command(ContainerCommand action, string id, int? grace, bool force, CancellationToken token)
        {
            Check(DisposeCount == 0, "No command on disposed connection"); Interlocked.Increment(ref CommandCalls);
            MaximumCommands = Math.Max(MaximumCommands, Interlocked.Increment(ref commandWork));
            var call = new CommandCall(action, id, grace, force, token); commands.Writer.TryWrite(call);
            try { return await (honorCommands ? call.Completion.Task.WaitAsync(token) : call.Completion.Task); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { throw new ContainerCommandCanceledException(new(action, ContainerCommandDisposition.Uncertain, Failure: new(ContainerCommandFailureKind.Canceled, "Command cancellation")), token); }
            finally { Interlocked.Decrement(ref commandWork); }
        }
        public Task<ContainerCommandResult> StartContainerAsync(string identity, CancellationToken cancellationToken = default) => Command(ContainerCommand.Start, identity, null, false, cancellationToken);
        public Task<ContainerCommandResult> StopContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => Command(ContainerCommand.Stop, identity, gracePeriodSeconds, false, cancellationToken);
        public Task<ContainerCommandResult> RestartContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => Command(ContainerCommand.Restart, identity, gracePeriodSeconds, false, cancellationToken);
        public Task<ContainerCommandResult> DeleteContainerAsync(string identity, bool force = false, CancellationToken cancellationToken = default) => Command(ContainerCommand.Delete, identity, null, force, cancellationToken);
        public ValueTask DisposeAsync()
        { DisposedWithWork = reads != 0 || commandWork != 0 || Events.DisposeCount == 0; ++DisposeCount; return ValueTask.CompletedTask; }
        public async Task<ListCall> NextList()
        {
            while (true) { var call = await lists.Reader.ReadAsync().AsTask().WaitAsync(Limit); if (!honorReads || !call.Token.IsCancellationRequested) return call; }
        }
        public async Task<InspectionCall> NextInspection()
        {
            while (true) { var call = await inspections.Reader.ReadAsync().AsTask().WaitAsync(Limit); if (!honorReads || !call.Token.IsCancellationRequested) return call; }
        }
        public async Task<CommandCall> NextCommand() => await commands.Reader.ReadAsync().AsTask().WaitAsync(Limit);
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
        public void Succeed(ContainerInspection value) => Completion.SetResult(EngineResourceResult<ContainerInspection>.Success(value));
    }
    private sealed record CommandCall(ContainerCommand Command, string Identity, int? Grace, bool Force, CancellationToken Token)
    {
        public TaskCompletionSource<ContainerCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Acknowledge(ContainerCommandAcknowledgement acknowledgement = ContainerCommandAcknowledgement.Accepted)
            => Completion.SetResult(new(Command, ContainerCommandDisposition.Acknowledged, 204, Acknowledgement: acknowledgement));
    }
    private sealed class Subscription : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(32);
        private readonly object gate = new();
        private TaskCompletionSource progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int consumed, sent;
        public int DisposeCount;
        public Subscription() => Events = new ObservedReader(this);
        public ChannelReader<EngineEvent> Events { get; }
        public Task<EngineEventFailure?> Completion { get; } = Task.FromResult<EngineEventFailure?>(null);
        public async Task Emit(EngineEvent observation) { await channel.Writer.WriteAsync(observation); ++sent; }
        public async Task Barrier()
        {
            await Emit(new("image", "barrier", null, null, null, new(null, null)));
            while (true)
            {
                Task pending;
                lock (gate) { if (consumed >= sent) return; pending = progress.Task; }
                await pending.WaitAsync(Limit);
            }
        }
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref DisposeCount, 1); channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
        private sealed class ObservedReader(Subscription owner) : ChannelReader<EngineEvent>
        {
            public override bool TryRead(out EngineEvent item)
            {
                if (!owner.channel.Reader.TryRead(out item!)) return false;
                lock (owner.gate) { ++owner.consumed; owner.progress.TrySetResult(); owner.progress = new(TaskCreationOptions.RunContinuationsAsynchronously); }
                return true;
            }
            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => owner.channel.Reader.WaitToReadAsync(cancellationToken);
        }
    }
    private sealed class Clock : IConnectionDelay
    {
        private readonly ConcurrentDictionary<TimeSpan, Channel<Wait>> waits = new();
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        { var wait = new Wait(cancellationToken); waits.GetOrAdd(delay, _ => Channel.CreateUnbounded<Wait>()).Writer.TryWrite(wait); await wait.Done.Task.WaitAsync(cancellationToken); }
        public async Task<Wait> Next(TimeSpan delay) => await waits.GetOrAdd(delay, _ => Channel.CreateUnbounded<Wait>()).Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class Wait(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Done.TrySetResult();
    }
}
