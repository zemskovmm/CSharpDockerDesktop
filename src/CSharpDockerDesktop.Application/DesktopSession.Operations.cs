using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IContainerOperations
{
    private ContainerOperationSnapshot? containerOperation;
    private long nextOperation;
    private Task<ContainerOperationSnapshot>? operationCompletion;
    private EngineEndpoint? operationEndpoint;
    private string? operationEngineId;
    private DateTimeOffset? operationStartedAt;

    public event EventHandler? ContainerOperationChanged;
    public ContainerOperationSnapshot? ContainerOperation { get { lock (gate) return containerOperation; } }

    public Task<ContainerOperationSnapshot> ExecuteContainerOperationAsync(ContainerOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        Task<ContainerOperationSnapshot> completion;
        lock (gate)
        {
            if (containerOperation is { } pending && (pending.Pending || operationCompletion?.IsCompleted == false))
                return pending.Request == request ? operationCompletion! : RejectedOperation(request,
                    ContainerCommandFailureKind.CapacityExceeded, "Another container operation is pending.");
            if (cancellationToken.IsCancellationRequested)
                return RejectedOperation(request, ContainerCommandFailureKind.Canceled, "The operation was canceled before dispatch.");
            if (disposed || containerReads is not { Reader: not null } reads || reads.Lifetime.IsCancellationRequested
                || request.Target.ConnectionGeneration != containerSnapshot.ConnectionGeneration)
                return RejectedOperation(request, ContainerCommandFailureKind.StaleConnection, "Select a container from the current connection.");
            if (reads.Connection is not IContainerCommands commands)
                return RejectedOperation(request, ContainerCommandFailureKind.InvalidRequest, "The connection does not offer lifecycle commands.");
            // Require the complete identity present in this generation, never a short ID/name lookup.
            if (string.IsNullOrEmpty(request.Target.Id) || !containerSnapshot.Containers.Any(c => c.Id == request.Target.Id)
                || !Enum.IsDefined(request.Command) || request.GracePeriodSeconds < 0
                || request.ForceDelete && request.Command != ContainerCommand.Delete
                || request.GracePeriodSeconds is not null && request.Command is not (ContainerCommand.Stop or ContainerCommand.Restart))
                return RejectedOperation(request, ContainerCommandFailureKind.InvalidRequest, "Supply a current full container identity and parameters for the requested action.");
            var id = ++nextOperation;
            containerOperation = new(id, request, ContainerOperationPhase.CommandPending)
            { Endpoint = reads.Connection.Metadata.Endpoint, EngineId = reads.Connection.Metadata.EngineId };
            operationEndpoint = reads.Connection.Metadata.Endpoint;
            operationEngineId = reads.Connection.Metadata.EngineId;
            operationStartedAt = null;
            FenceOperationReadsLocked(reads, request.Target.Id);
            completion = Task.Run(() => RunContainerOperationAsync(reads, commands, id, request, cancellationToken));
            operationCompletion = completion;
            reads.OperationWorkflow = completion;
        }
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        return completion;
    }

    private static Task<ContainerOperationSnapshot> RejectedOperation(ContainerOperationRequest request,
        ContainerCommandFailureKind kind, string description) => Task.FromResult(new ContainerOperationSnapshot(0, request,
            ContainerOperationPhase.Completed, new(request.Command, ContainerCommandDisposition.NotDispatched,
                Failure: new(kind, description)), ContainerOperationResolution.NotDispatched));

    private bool CurrentOperationLocked(ContainerReads reads, long id)
        => CurrentEventsLocked(reads) && containerOperation?.OperationId == id;

    // Reject reads begun before either side of dispatch, and clear exposed obsolete Inspect immediately.
    private void FenceOperationReadsLocked(ContainerReads reads, string identity)
    {
        if (reads.Active is not null && !reads.InspectActive && !reads.OperationReadActive) reads.Refresh = true;
        ++reads.ObservationRevision;
        ++reads.ListRevision;
        ++reads.DetailRevision;
        if (reads.InspectActive && containerSnapshot.SelectedContainerId is not null
            && containerSnapshot.InspectionState == ResourceReadState.Loading) reads.Inspect = true;
        if (!reads.OperationReadActive) reads.Active?.Cancel();
        InvalidateEventDetailLocked(reads, identity);
        reads.Signal.Writer.TryWrite(0);
    }

    private async Task<ContainerOperationSnapshot> RunContainerOperationAsync(ContainerReads reads,
        IContainerCommands commands, long id, ContainerOperationRequest request, CancellationToken caller)
    {
        try
        {
            if (request.Command == ContainerCommand.Restart)
            {
                // A running state alone cannot prove a restart. Capture a pre-dispatch start-time fence.
                OperationObservation before;
                try { before = await QueueOperationObservationAsync(reads, request.Target.Id).WaitAsync(caller).ConfigureAwait(false); }
                catch (OperationCanceledException) when (caller.IsCancellationRequested && !reads.Lifetime.IsCancellationRequested)
                {
                    lock (gate)
                    {
                        if (CurrentOperationLocked(reads, id))
                            containerOperation = containerOperation! with { Phase = ContainerOperationPhase.Completed,
                                Resolution = ContainerOperationResolution.NotDispatched, CommandOutcome = new(request.Command,
                                    ContainerCommandDisposition.NotDispatched, Failure: new(ContainerCommandFailureKind.Canceled,
                                        "The operation was canceled before dispatch.")) };
                        return containerOperation!;
                    }
                }
                lock (gate)
                {
                    if (!CurrentOperationLocked(reads, id)) return containerOperation!;
                    operationStartedAt = before.State?.StartedAt;
                }
            }
            using var command = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, caller);
            command.CancelAfter(operationOptions.CommandTimeout);
            ContainerCommandResult outcome;
            if (command.IsCancellationRequested)
                outcome = new(request.Command, ContainerCommandDisposition.NotDispatched,
                    Failure: new(ContainerCommandFailureKind.Canceled, "The operation was canceled before dispatch."));
            else
            {
                lock (gate)
                {
                    if (!CurrentOperationLocked(reads, id)) return containerOperation!;
                    FenceOperationReadsLocked(reads, request.Target.Id);
                    // From capability invocation onward, replacement cannot assume non-dispatch.
                    containerOperation = containerOperation! with { CommandOutcome = new(request.Command,
                        ContainerCommandDisposition.Uncertain) };
                }
                try
                {
                    outcome = await (request.Command switch
                    {
                        ContainerCommand.Start => commands.StartContainerAsync(request.Target.Id, command.Token),
                        ContainerCommand.Stop => commands.StopContainerAsync(request.Target.Id, request.GracePeriodSeconds, command.Token),
                        ContainerCommand.Restart => commands.RestartContainerAsync(request.Target.Id, request.GracePeriodSeconds, command.Token),
                        _ => commands.DeleteContainerAsync(request.Target.Id, request.ForceDelete, command.Token)
                    }).ConfigureAwait(false);
                }
                catch (ContainerCommandCanceledException canceled) { outcome = canceled.Outcome; }
                catch (OperationCanceledException)
                {
                    // A capability that cannot prove non-dispatch must be treated conservatively.
                    outcome = new(request.Command, ContainerCommandDisposition.Uncertain, Failure: new(
                        caller.IsCancellationRequested ? ContainerCommandFailureKind.Canceled : ContainerCommandFailureKind.Deadline,
                        "The command was interrupted; authoritative observation is required."));
                }
            }
            bool lost;
            Task<OperationObservation>? firstObservation = null;
            lock (gate)
            {
                if (!CurrentOperationLocked(reads, id))
                {
                    // Preserve the original connection's late protocol evidence without publishing resource state.
                    if (containerOperation?.OperationId == id)
                        containerOperation = containerOperation with { CommandOutcome = outcome };
                    return containerOperation!;
                }
                lost = outcome.Failure?.Kind is ContainerCommandFailureKind.TransportFailure
                    or ContainerCommandFailureKind.StaleConnection or ContainerCommandFailureKind.ConnectionDisposed;
                containerOperation = containerOperation! with { CommandOutcome = outcome,
                    Phase = outcome.RequiresReconciliation ? ContainerOperationPhase.Reconciling : ContainerOperationPhase.Completed,
                    Resolution = outcome.RequiresReconciliation ? null : ContainerOperationResolution.NotDispatched };
                if (!lost && outcome.RequiresReconciliation)
                    firstObservation = QueueOperationObservationAsync(reads, request.Target.Id, signal: false);
                // Queue before canceling: cancellation continuations may run synchronously and reenter the read loop.
                FenceOperationReadsLocked(reads, request.Target.Id);
                if (lost)
                    ResourceTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                        outcome.Failure!.Description, outcome.HttpStatusCode, outcome.DaemonMessage), false);
            }
            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
            NotifyContainerBrowsers();
            if (!lost && outcome.RequiresReconciliation) await ObserveContainerOperationAsync(reads, id, firstObservation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                if (containerOperation?.OperationId == id && containerOperation.Pending)
                    containerOperation = containerOperation with { Phase = ContainerOperationPhase.Completed,
                        Resolution = ContainerOperationResolution.Unresolved };
            }
            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        }
        lock (gate) return containerOperation!;
    }

    private async Task ObserveContainerOperationAsync(ContainerReads reads, long id, Task<OperationObservation>? firstObservation = null)
    {
        for (int attempt = 0; attempt < operationOptions.MaximumObservations; ++attempt)
        {
            string identity;
            lock (gate)
            {
                if (!CurrentOperationLocked(reads, id)) return;
                identity = containerOperation!.Request.Target.Id;
            }
            var observation = await (attempt == 0 && firstObservation is not null
                ? firstObservation : QueueOperationObservationAsync(reads, identity)).ConfigureAwait(false);
            bool finished;
            lock (gate)
            {
                if (!CurrentOperationLocked(reads, id)) return;
                var current = containerOperation!;
                bool stale = observation.Invalidated || observation.Revision != reads.ObservationRevision;
                bool authoritative = observation.Failure is null
                    || observation.Exists == false && observation.Failure.Kind == EngineResourceFailureKind.NotFound;
                bool established = !stale && authoritative && MatchesRequest(current.Request.Command, observation);
                bool rejected = current.CommandOutcome?.Disposition == ContainerCommandDisposition.DaemonResponse;
                finished = established || rejected && !stale || attempt + 1 == operationOptions.MaximumObservations;
                containerOperation = current with
                {
                    Phase = finished ? ContainerOperationPhase.Completed : ContainerOperationPhase.Reconciling,
                    Resolution = !finished ? null : established ? ContainerOperationResolution.ObservedRequestedState
                        : rejected && !stale && authoritative
                            ? ContainerOperationResolution.ObservedDifferentState : ContainerOperationResolution.Unresolved,
                    ObservedConnectionGeneration = stale ? current.ObservedConnectionGeneration : containerSnapshot.ConnectionGeneration,
                    ObservedExists = stale ? current.ObservedExists : observation.Exists,
                    ObservedState = stale ? current.ObservedState : observation.State,
                    ObservationFailure = observation.Failure,
                    Observations = current.Observations + 1
                };
            }
            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
            if (finished) return;
            await delay.DelayAsync(operationOptions.FollowupInterval, reads.Lifetime.Token).ConfigureAwait(false);
        }
    }

    private bool MatchesRequest(ContainerCommand command, OperationObservation observation)
    {
        if (command == ContainerCommand.Delete) return observation.Exists == false;
        if (observation.Exists != true || observation.State is not { } state) return false;
        bool running = state.Running == true && state.Restarting == false && state.Paused == false && state.Dead == false;
        return command switch
        {
            ContainerCommand.Start => running,
            ContainerCommand.Stop => state.Running == false && state.Restarting == false,
            ContainerCommand.Restart => running && operationStartedAt is { } before && state.StartedAt is { } after && after > before,
            _ => false
        };
    }

    private Task<OperationObservation> QueueOperationObservationAsync(ContainerReads reads, string identity, bool signal = true)
    {
        lock (gate)
        {
            reads.Lifetime.Token.ThrowIfCancellationRequested();
            var request = new OperationObservationRequest(identity);
            reads.OperationObservation = request;
            if (signal) reads.Signal.Writer.TryWrite(0);
            return request.Completion.Task.WaitAsync(reads.Lifetime.Token);
        }
    }

    private void ResumeContainerOperationLocked(ContainerReads reads)
    {
        if (containerOperation is not { Resolution: ContainerOperationResolution.Unresolved, CommandOutcome.RequiresReconciliation: true } current
            || string.IsNullOrWhiteSpace(operationEngineId) || operationEndpoint != reads.Connection.Metadata.Endpoint || operationEngineId != reads.Connection.Metadata.EngineId || reads.Reader is null) return;
        containerOperation = current with { Phase = ContainerOperationPhase.Reconciling, Resolution = null };
        // Reconnection only observes. The original request retains its original scoped identity.
        var id = current.OperationId;
        var task = Task.Run(async () =>
        {
            try { await ObserveContainerOperationAsync(reads, id).ConfigureAwait(false); }
            catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
            finally
            {
                lock (gate)
                    if (containerOperation?.OperationId == id && containerOperation.Pending)
                        containerOperation = containerOperation with { Phase = ContainerOperationPhase.Completed,
                            Resolution = ContainerOperationResolution.Unresolved };
                ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
            }
            lock (gate) return containerOperation!;
        });
        operationCompletion = task;
        reads.OperationWorkflow = task;
    }

    private void InvalidateContainerOperationLocked()
    {
        if (containerOperation is { Pending: true } current)
            containerOperation = current with { Phase = ContainerOperationPhase.Completed, Resolution = ContainerOperationResolution.Unresolved };
    }

    private sealed record OperationObservation(bool? Exists = null, ContainerRuntimeState? State = null,
        EngineResourceFailure? Failure = null, bool Invalidated = false, long Revision = 0);
    private sealed class OperationObservationRequest(string identity)
    {
        public string Identity { get; } = identity;
        public TaskCompletionSource<OperationObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Runs in the browsing read loop: list and target inspection never overlap another container read.
    private async Task ReadOperationObservationAsync(ContainerReads reads, OperationObservationRequest observation)
    {
        long epoch, detailRevision, listRevision;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token);
        request.CancelAfter(options.OperationTimeout);
        lock (gate)
        {
            epoch = reads.ObservationRevision;
            detailRevision = reads.DetailRevision;
            listRevision = reads.ListRevision;
            reads.OperationReadActive = true;
            reads.Active = request;
        }
        try
        {
            var list = await reads.Reader!.ListContainersAsync(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            ContainerInspection? inspection = null;
            EngineResourceFailure? failure = list.Failure;
            bool? exists = null;
            if (list.IsSuccess)
            {
                exists = list.Value!.Any(c => c.Id == observation.Identity);
                if (exists == true)
                {
                    var detail = await reads.Reader.InspectContainerAsync(observation.Identity, request.Token).ConfigureAwait(false);
                    if (detail.Failure?.Kind == EngineResourceFailureKind.NotFound)
                    { exists = false; failure = detail.Failure; }
                    else { inspection = detail.Value; failure = detail.Failure; }
                }
            }
            lock (gate)
            {
                if (!CurrentEventsLocked(reads)) return;
                if (request.IsCancellationRequested)
                    failure = new(EngineResourceFailureKind.TransportLoss, "The operation observation exceeded its deadline.");
                if (failure?.Kind == EngineResourceFailureKind.TransportLoss)
                {
                    ResourceTransportLostLocked(reads, failure, false);
                    return;
                }
                bool stale = epoch != reads.ObservationRevision;
                if (!stale && list.Failure is { } listFailure && listRevision == reads.ListRevision)
                    containerSnapshot = containerSnapshot with { State = ResourceReadState.Failed, Failure = listFailure };
                if (!stale && list.IsSuccess && listRevision == reads.ListRevision)
                {
                    reads.Refresh = false;
                    reads.EventRefresh = false;
                    var rows = list.Value!.DistinctBy(c => c.Id, StringComparer.Ordinal)
                        .Where(c => exists != false || c.Id != observation.Identity)
                        .Select(c => c.Id == observation.Identity && inspection?.State is { } state
                            ? c with { State = state.Status ?? c.State, Health = state.Health ?? c.Health } : c).ToArray();
                    containerSnapshot = containerSnapshot with { State = ResourceReadState.Ready,
                        Containers = Array.AsReadOnly(rows), Failure = null };
                    ReconcileLogsLocked(reads);
                    if (containerSnapshot.SelectedContainerId is { } selected && !rows.Any(c => c.Id == selected))
                        RequestInspectionLocked(reads, null, false);
                }
                if (!stale && containerSnapshot.SelectedContainerId == observation.Identity && detailRevision == reads.DetailRevision
                    && containerSnapshot.InspectionState != ResourceReadState.NotRequested)
                {
                    reads.Inspect = false;
                    containerSnapshot = containerSnapshot with { Inspection = inspection,
                        InspectionState = failure is null ? ResourceReadState.Ready : ResourceReadState.Failed,
                        InspectionFailure = failure };
                }
                if (!stale && exists == false)
                {
                    if (logsRequest?.ContainerId == observation.Identity) CloseLogsLocked(ContainerLogsPhase.Failed,
                        new(ContainerLogFailureKind.NotFound, "The container is no longer available."));
                    containerSnapshot = containerSnapshot with { Containers = Array.AsReadOnly(
                        containerSnapshot.Containers.Where(c => c.Id != observation.Identity).ToArray()) };
                    if (containerSnapshot.SelectedContainerId == observation.Identity) RequestInspectionLocked(reads, null, false);
                }
                ReconcileStatsLocked(reads);
                ReconcileTerminalLocked(reads);
                observation.Completion.TrySetResult(new(exists, inspection?.State, failure, stale, epoch));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            lock (gate)
                if (CurrentEventsLocked(reads)) ResourceTransportLostLocked(reads,
                    new(EngineResourceFailureKind.TransportLoss, "The operation observation exceeded its deadline."), false);
        }
        finally
        {
            lock (gate) { reads.OperationReadActive = false; reads.Active = null; }
            NotifyContainerBrowsers();
            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
