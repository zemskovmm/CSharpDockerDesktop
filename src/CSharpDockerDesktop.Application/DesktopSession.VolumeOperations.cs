using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IVolumeOperations
{
    private readonly VolumeOperationOptions volumeOperationOptions;
    private VolumeOperationSnapshot? volumeOperation, notifiedVolumeOperation;
    private Task<VolumeOperationSnapshot>? volumeOperationCompletion;
    private long nextVolumeOperation;
    private EngineEndpoint? volumeOperationEndpoint;
    private string? volumeOperationEngineId;

    public event EventHandler? VolumeOperationChanged;
    public VolumeOperationSnapshot? VolumeOperation { get { lock (gate) return volumeOperation; } }

    public Task<VolumeOperationSnapshot> ExecuteVolumeOperationAsync(VolumeOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        Task<VolumeOperationSnapshot> completion;
        lock (gate)
        {
            // Retain one receipt, not an unbounded request history. Older receipts cannot match
            // the single current preparation; consumption and supersession fence their reuse.
            if (volumeOperation?.Request == request && volumeOperationCompletion is not null)
                return volumeOperationCompletion;
            if (cancellationToken.IsCancellationRequested)
                return RejectVolumeOperation(request, VolumeOperationRejection.Canceled, VolumeCommandFailureKind.Canceled);
            if (disposed || volumeReads is not { Reader: not null } reads || !CurrentVolumesLocked(reads)
                || request.Target.ConnectionGeneration != volumeSnapshot.ConnectionGeneration)
                return RejectVolumeOperation(request, VolumeOperationRejection.StaleConnection, VolumeCommandFailureKind.StaleConnection);
            if (UsableVolumeEventIdentity(request.Target.Name) is null)
                return RejectVolumeOperation(request, VolumeOperationRejection.InvalidRequest, VolumeCommandFailureKind.InvalidRequest);
            if (reads.Containers.Connection is not IVolumeCommands commands)
                return RejectVolumeOperation(request, VolumeOperationRejection.CapabilityUnavailable, VolumeCommandFailureKind.InvalidRequest);
            if (volumeOperation is { Pending: true } || volumeOperationCompletion?.IsCompleted == false)
                return RejectVolumeOperation(request, VolumeOperationRejection.CapacityExceeded, VolumeCommandFailureKind.CapacityExceeded);
            if (preparation is not { } prepared || prepared.Reads != reads.Containers
                || prepared.Snapshot.Target != request.Target || prepared.Snapshot.Identity != request.Preparation
                || !prepared.Snapshot.HasUsableEvidence || !CurrentEventsLocked(prepared.Reads))
                return RejectVolumeOperation(request, VolumeOperationRejection.PreparationNotCurrent, VolumeCommandFailureKind.InvalidRequest);
            var evidence = prepared.Snapshot;
            // Validation and consumption share the same gate with events, close, and supersession.
            prepared.Snapshot = evidence with { Identity = evidence.Identity with { Revision = evidence.Identity.Revision + 1 },
                Phase = VolumePreparationPhase.Consumed };
            volumeOperation = new(++nextVolumeOperation, request, VolumeOperationPhase.CommandPending, evidence)
            { Endpoint = reads.Containers.Connection.Metadata.Endpoint, EngineId = reads.Containers.Connection.Metadata.EngineId };
            volumeOperationEndpoint = reads.Containers.Connection.Metadata.Endpoint;
            volumeOperationEngineId = reads.Containers.Connection.Metadata.EngineId;
            FenceVolumeOperationReadsLocked(reads);
            long id = volumeOperation.OperationId;
            completion = Task.Run(() => RunVolumeOperationAsync(reads, commands, id, request, cancellationToken));
            volumeOperationCompletion = completion;
            reads.OperationWorkflow = completion;
        }
        NotifyVolumesChanged();
        return completion;
    }

    private static Task<VolumeOperationSnapshot> RejectVolumeOperation(VolumeOperationRequest request,
        VolumeOperationRejection reason, VolumeCommandFailureKind kind) => Task.FromResult(new VolumeOperationSnapshot(0, request,
            VolumeOperationPhase.Completed, CommandOutcome: new(VolumeCommandDisposition.NotDispatched,
                Failure: new(kind, "The volume operation was not admitted.")),
            Resolution: VolumeOperationResolution.NotDispatched, Rejection: reason));

    private bool CurrentVolumeOperationLocked(VolumeReads reads, long id)
        => CurrentVolumesLocked(reads) && volumeOperation?.OperationId == id;

    private void FenceVolumeOperationReadsLocked(VolumeReads reads)
    {
        ++reads.ObservationRevision;
        ++reads.ListRevision;
        ++reads.DetailRevision;
        if (!reads.OperationReadActive) reads.Active?.Cancel();
        InvalidateVolumeEventDetailLocked(reads, null);
        reads.Signal.Writer.TryWrite(0);
    }

    private async Task<VolumeOperationSnapshot> RunVolumeOperationAsync(VolumeReads reads, IVolumeCommands commands,
        long id, VolumeOperationRequest request, CancellationToken caller)
    {
        try
        {
            VolumeCommandResult outcome;
            using (var deadline = new CancellationTokenSource())
            using (var command = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, caller, deadline.Token))
            {
                lock (gate)
                {
                    if (!CurrentVolumeOperationLocked(reads, id)) return volumeOperation!;
                    outcome = command.IsCancellationRequested
                        ? new(VolumeCommandDisposition.NotDispatched, Failure: new(VolumeCommandFailureKind.Canceled, "Canceled before dispatch."))
                        : new(VolumeCommandDisposition.Uncertain);
                    volumeOperation = volumeOperation! with { CommandOutcome = outcome };
                }
                using var callerRegistration = caller.Register(() =>
                {
                    lock (gate)
                        if (volumeOperation?.OperationId == id)
                            volumeOperation = volumeOperation with { Interruption = volumeOperation.Interruption == VolumeOperationInterruption.CommandDeadline
                                ? VolumeOperationInterruption.CommandDeadline : VolumeOperationInterruption.CallerCanceled };
                    NotifyVolumeOperationChanged();
                });
                // A separate clock still expires after caller cancellation, even for non-cooperative commands.
                using var deadlineRegistration = deadline.Token.Register(() =>
                {
                    lock (gate)
                    {
                        if (!CurrentVolumeOperationLocked(reads, id)) return;
                        volumeOperation = volumeOperation! with { Interruption = VolumeOperationInterruption.CommandDeadline,
                            CommandOutcome = new(VolumeCommandDisposition.Uncertain,
                                Failure: new(VolumeCommandFailureKind.Deadline, "The volume command exceeded its deadline.")) };
                        VolumeTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                            "The volume command exceeded its deadline."), false);
                    }
                    NotifyContainerBrowsers();
                });
                if (outcome.RequiresReconciliation)
                {
                    deadline.CancelAfter(volumeOperationOptions.CommandTimeout);
                    try { outcome = await commands.DeleteVolumeAsync(request.Target.Name, command.Token).ConfigureAwait(false); }
                    catch (VolumeCommandCanceledException canceled) { outcome = canceled.Outcome; }
                    catch (OperationCanceledException)
                    { outcome = new(VolumeCommandDisposition.Uncertain, Failure: new(caller.IsCancellationRequested
                        ? VolumeCommandFailureKind.Canceled : deadline.IsCancellationRequested ? VolumeCommandFailureKind.Deadline
                        : VolumeCommandFailureKind.StaleConnection, "The command was interrupted; authoritative observation is required.")); }
                    catch (Exception)
                    { outcome = new(VolumeCommandDisposition.Uncertain, Failure: new(VolumeCommandFailureKind.UnexpectedResponse,
                        "The command did not return classified evidence; authoritative observation is required.")); }
                }
            }
            bool lost;
            Task<VolumeObservation>? first = null;
            lock (gate)
            {
                if (volumeOperation?.OperationId != id) return volumeOperation!;
                // Late protocol evidence remains useful, even after teardown. It cannot publish
                // resource state or make an acknowledgement into proof of deletion.
                volumeOperation = volumeOperation with { CommandOutcome = outcome };
                if (!CurrentVolumeOperationLocked(reads, id))
                {
                    if (!outcome.RequiresReconciliation)
                        volumeOperation = volumeOperation with { Resolution = VolumeOperationResolution.NotDispatched };
                    return volumeOperation;
                }
                volumeOperation = volumeOperation with {
                    Phase = outcome.RequiresReconciliation ? VolumeOperationPhase.Reconciling : VolumeOperationPhase.Completed,
                    Resolution = outcome.RequiresReconciliation ? null : VolumeOperationResolution.NotDispatched };
                lost = outcome.Failure?.Kind is VolumeCommandFailureKind.TransportFailure or VolumeCommandFailureKind.Deadline
                    or VolumeCommandFailureKind.StaleConnection or VolumeCommandFailureKind.ConnectionDisposed;
                if (outcome.RequiresReconciliation && !lost)
                {
                    FenceVolumeOperationReadsLocked(reads);
                    first = QueueVolumeObservationAsync(reads, request.Target.Name,
                        outcome.Failure?.Kind == VolumeCommandFailureKind.Conflict);
                }
                else if (!lost) { reads.Refresh = true; reads.Signal.Writer.TryWrite(0); }
                if (lost) VolumeTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                    outcome.Failure!.Description, outcome.HttpStatusCode, outcome.DaemonMessage), false);
            }
            if (lost) NotifyContainerBrowsers();
            else NotifyVolumesChanged();
            if (!lost && outcome.RequiresReconciliation) await ObserveVolumeOperationAsync(reads, id, first).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
        finally
        {
            lock (gate)
                if (volumeOperation?.OperationId == id && volumeOperation.Pending)
                    volumeOperation = volumeOperation with { Phase = VolumeOperationPhase.Completed, Resolution = VolumeOperationResolution.Unresolved };
            NotifyVolumeOperationChanged();
        }
        lock (gate) return volumeOperation!;
    }

    private async Task ObserveVolumeOperationAsync(VolumeReads reads, long id, Task<VolumeObservation>? first = null)
    {
        // The budget is per connection observation pass. Retry never repeats the mutation.
        for (int attempt = 0; attempt < volumeOperationOptions.MaximumObservations; ++attempt)
        {
            string name;
            bool dependencies;
            lock (gate)
            {
                if (!CurrentVolumeOperationLocked(reads, id)) return;
                name = volumeOperation!.Request.Target.Name;
                dependencies = volumeOperation.CommandOutcome?.Failure?.Kind == VolumeCommandFailureKind.Conflict;
            }
            var observed = await (attempt == 0 && first is not null ? first
                : QueueVolumeObservationAsync(reads, name, dependencies)).ConfigureAwait(false);
            bool finished;
            lock (gate)
            {
                if (!CurrentVolumeOperationLocked(reads, id)) return;
                var current = volumeOperation!;
                bool stale = observed.Revision != reads.ObservationRevision;
                bool authoritative = !stale && observed.Exists.HasValue;
                var dependencyState = !dependencies ? VolumeDependencyObservationState.NotRequested : stale
                    ? VolumeDependencyObservationState.Invalidated : observed.DependencyFailure is not null
                        ? VolumeDependencyObservationState.Failed : observed.Dependencies?.Completeness switch {
                            VolumeDependencyCompleteness.Complete => VolumeDependencyObservationState.Complete,
                            VolumeDependencyCompleteness.Incomplete => VolumeDependencyObservationState.Incomplete,
                            _ => VolumeDependencyObservationState.Unavailable };
                bool resolved = authoritative && (!dependencies || dependencyState == VolumeDependencyObservationState.Complete);
                finished = resolved || attempt + 1 == volumeOperationOptions.MaximumObservations;
                volumeOperation = current with { Phase = finished ? VolumeOperationPhase.Completed : VolumeOperationPhase.Reconciling,
                    Resolution = !finished ? null : !resolved ? VolumeOperationResolution.Unresolved : observed.Exists == false
                        ? VolumeOperationResolution.ObservedAbsent : VolumeOperationResolution.ObservedPresent,
                    ObservedConnectionGeneration = stale ? current.ObservedConnectionGeneration : volumeSnapshot.ConnectionGeneration,
                    ObservedExists = authoritative ? observed.Exists : null,
                    ObservedListing = observed.Listing, ObservationListFailure = observed.ListFailure,
                    ObservationFailure = observed.Failure, ObservationInvalidated = stale, Observations = current.Observations + 1,
                    RefreshedDependencies = observed.Dependencies ?? current.RefreshedDependencies,
                    DependencyObservationState = dependencyState, DependencyFailure = observed.DependencyFailure,
                    DependencyRefreshAttempts = current.DependencyRefreshAttempts + (dependencies ? 1 : 0) };
            }
            NotifyVolumeOperationChanged();
            if (finished) return;
            await delay.DelayAsync(volumeOperationOptions.FollowupInterval, reads.Lifetime.Token).ConfigureAwait(false);
        }
    }

    private Task<VolumeObservation> QueueVolumeObservationAsync(VolumeReads reads, string name, bool dependencies)
    {
        lock (gate)
        {
            reads.Lifetime.Token.ThrowIfCancellationRequested();
            var observation = new VolumeObservationRequest(name, dependencies);
            reads.OperationObservation = observation; // One operation awaits one slot; never a queue.
            reads.Signal.Writer.TryWrite(0);
            return observation.Completion.Task.WaitAsync(reads.Lifetime.Token);
        }
    }

    private void ResumeVolumeOperationLocked(VolumeReads reads)
    {
        if (volumeOperation is not { Resolution: VolumeOperationResolution.Unresolved, CommandOutcome.RequiresReconciliation: true } current
            || string.IsNullOrWhiteSpace(volumeOperationEngineId) || volumeOperationEndpoint != reads.Containers.Connection.Metadata.Endpoint
            || volumeOperationEngineId != reads.Containers.Connection.Metadata.EngineId) return;
        volumeOperation = current with { Phase = VolumeOperationPhase.Reconciling, Resolution = null };
        long id = current.OperationId;
        var task = Task.Run(async () =>
        {
            try { await ObserveVolumeOperationAsync(reads, id).ConfigureAwait(false); }
            catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
            finally
            {
                lock (gate)
                    if (volumeOperation?.OperationId == id && volumeOperation.Pending)
                        volumeOperation = volumeOperation with { Phase = VolumeOperationPhase.Completed, Resolution = VolumeOperationResolution.Unresolved };
                NotifyVolumeOperationChanged();
            }
            lock (gate) return volumeOperation!;
        });
        reads.OperationWorkflow = task;
        volumeOperationCompletion = task;
    }

    private void InvalidateVolumeOperationLocked()
    {
        if (volumeOperation is not { Pending: true } current) return;
        volumeOperation = current with { Phase = VolumeOperationPhase.Completed,
            Interruption = current.Interruption ?? VolumeOperationInterruption.ConnectionChanged,
            ObservedExists = null, Resolution = current.CommandOutcome is null
                ? VolumeOperationResolution.NotDispatched : VolumeOperationResolution.Unresolved,
            CommandOutcome = current.CommandOutcome ?? new(VolumeCommandDisposition.NotDispatched,
                Failure: new(VolumeCommandFailureKind.StaleConnection, "The connection changed before dispatch.")) };
    }

    private void NotifyVolumeOperationChanged()
    {
        lock (gate)
        {
            if (ReferenceEquals(notifiedVolumeOperation, volumeOperation)) return;
            notifiedVolumeOperation = volumeOperation;
        }
        NotifyImageObservers(VolumeOperationChanged);
    }

    private sealed record VolumeObservation(bool? Exists, VolumeListing? Listing, EngineResourceFailure? ListFailure,
        EngineResourceFailure? Failure, VolumeDependencies? Dependencies, EngineResourceFailure? DependencyFailure, long Revision);
    private sealed class VolumeObservationRequest(string name, bool dependencies)
    {
        public string Name { get; } = name;
        public bool Dependencies { get; } = dependencies;
        public TaskCompletionSource<VolumeObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The volume scheduler serializes browsing and command observation, yielding browsing turns
    // between operation attempts. Dependencies share that bounded slot after a daemon conflict.
    private async Task ReadVolumeObservationAsync(VolumeReads reads, VolumeObservationRequest observation)
    {
        long epoch, listRevision;
        using var deadline = new CancellationTokenSource();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, deadline.Token);
        lock (gate)
        {
            epoch = reads.ObservationRevision; listRevision = reads.ListRevision;
            reads.OperationReadActive = true; reads.LastReadWasOperation = true;
            reads.Active = request; reads.ActiveDeadline = deadline;
        }
        using var deadlineRegistration = deadline.Token.Register(() => VolumeReadDeadline(reads, request, true));
        deadline.CancelAfter(options.OperationTimeout);
        try
        {
            var result = await reads.Reader!.ListVolumesAsync(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            var listing = result.Value is null ? null : CopyVolumeListing(result.Value);
            bool? exists = listing is { Volumes: not null, HasWarnings: false }
                && !listing.Volumes.Any(row => StringComparer.Ordinal.Equals(row.Name, observation.Name)) ? false : null;
            var failure = result.Failure;
            VolumeDependencies? dependencies = null;
            EngineResourceFailure? dependencyFailure = null;
            if (exists is null && failure?.Kind != EngineResourceFailureKind.TransportLoss)
            {
                var detail = await reads.Reader.InspectVolumeAsync(observation.Name, request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                failure = detail.Failure;
                if (failure?.Kind == EngineResourceFailureKind.NotFound) exists = false;
                else if (detail.Value is { } value)
                {
                    if (StringComparer.Ordinal.Equals(value.Name, observation.Name)) exists = true;
                    else failure = new(EngineResourceFailureKind.MalformedData, "The volume observation returned a different identity.");
                }
            }
            if (observation.Dependencies && failure?.Kind != EngineResourceFailureKind.TransportLoss)
            {
                var related = await ((IVolumeDependencyReader)reads.Containers.Connection)
                    .QueryVolumeDependenciesAsync(observation.Name, request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                dependencyFailure = related.Failure;
                dependencies = related.Value;
                if (dependencies is not null && !StringComparer.Ordinal.Equals(dependencies.VolumeName, observation.Name))
                {
                    dependencies = null;
                    dependencyFailure = new(EngineResourceFailureKind.MalformedData, "The dependency observation returned a different volume identity.");
                }
            }
            lock (gate)
            {
                if (!CurrentVolumesLocked(reads) || request.IsCancellationRequested) return;
                var transport = new[] { result.Failure, failure, dependencyFailure }
                    .FirstOrDefault(item => item?.Kind == EngineResourceFailureKind.TransportLoss);
                if (transport is not null)
                {
                    if (volumeOperation is { Pending: true } current)
                        volumeOperation = current with { ObservedListing = listing, ObservationListFailure = result.Failure,
                            ObservationFailure = failure, DependencyFailure = dependencyFailure,
                            RefreshedDependencies = dependencies ?? current.RefreshedDependencies, ObservationInvalidated = true,
                            DependencyObservationState = observation.Dependencies ? VolumeDependencyObservationState.Failed
                                : current.DependencyObservationState };
                    VolumeTransportLostLocked(reads, transport, false);
                    return;
                }
                bool stale = epoch != reads.ObservationRevision;
                if (!stale && listRevision == reads.ListRevision)
                {
                    if (listing is not null) ApplyVolumeListingLocked(reads, listing);
                    else volumeSnapshot = volumeSnapshot with { State = ResourceReadState.Failed,
                        Failure = result.Failure, Completeness = VolumeListCompleteness.Unavailable,
                        HasRetainedRows = volumeSnapshot.Volumes.Count > 0 };
                    if (exists == false)
                    {
                        volumeSnapshot = volumeSnapshot with { Volumes = Array.AsReadOnly(volumeSnapshot.Volumes
                            .Where(row => !StringComparer.Ordinal.Equals(row.Name, observation.Name)).ToArray()) };
                        if (StringComparer.Ordinal.Equals(volumeSnapshot.SelectedVolumeName, observation.Name))
                            RequestVolumeInspectionLocked(reads, null, false);
                    }
                }
                observation.Completion.TrySetResult(new(exists, listing, result.Failure, failure, dependencies, dependencyFailure, epoch));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate)
                if (CurrentVolumesLocked(reads)) observation.Completion.TrySetResult(new(null, null, null,
                    new(EngineResourceFailureKind.MalformedData, "The volume observation did not return classified evidence."),
                    null, observation.Dependencies ? new(EngineResourceFailureKind.MalformedData,
                        "The dependency observation did not complete.") : null, epoch));
        }
        finally
        {
            if (deadline.IsCancellationRequested) VolumeReadDeadline(reads, request, true);
            lock (gate) { reads.OperationReadActive = false; reads.Active = null; reads.ActiveDeadline = null; }
            NotifyContainerBrowsers();
        }
    }
}
