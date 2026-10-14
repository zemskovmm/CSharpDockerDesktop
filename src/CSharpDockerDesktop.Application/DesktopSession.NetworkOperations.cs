using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : INetworkOperations
{
    private readonly NetworkOperationOptions networkOperationOptions;
    private NetworkOperationSnapshot? networkOperation, notifiedNetworkOperation;
    private Task<NetworkOperationSnapshot>? networkOperationCompletion;
    private long nextNetworkOperation;
    private EngineEndpoint? networkOperationEndpoint;
    private string? networkOperationEngineId;

    public event EventHandler? NetworkOperationChanged;
    public NetworkOperationSnapshot? NetworkOperation { get { lock (gate) return networkOperation; } }

    public Task<NetworkOperationSnapshot> ExecuteNetworkOperationAsync(NetworkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        Task<NetworkOperationSnapshot> completion;
        lock (gate)
        {
            // Retain one receipt, not an unbounded request history. Older receipts cannot match
            // the single current preparation; consumption and supersession fence their reuse.
            if (networkOperation?.Request == request && networkOperationCompletion is not null)
                return networkOperationCompletion;
            if (cancellationToken.IsCancellationRequested)
                return RejectNetworkOperation(request, NetworkOperationRejection.Canceled, NetworkCommandFailureKind.Canceled);
            if (disposed || networkReads is not { Reader: not null } reads || !CurrentNetworksLocked(reads)
                || request.Target.ConnectionGeneration != networkSnapshot.ConnectionGeneration)
                return RejectNetworkOperation(request, NetworkOperationRejection.StaleConnection, NetworkCommandFailureKind.StaleConnection);
            if (UsableNetworkEventIdentity(request.Target.Id) is null)
                return RejectNetworkOperation(request, NetworkOperationRejection.InvalidRequest, NetworkCommandFailureKind.InvalidRequest);
            if (reads.Containers.Connection is not INetworkCommands commands)
                return RejectNetworkOperation(request, NetworkOperationRejection.CapabilityUnavailable, NetworkCommandFailureKind.InvalidRequest);
            if (networkOperation is { Pending: true } || networkOperationCompletion?.IsCompleted == false)
                return RejectNetworkOperation(request, NetworkOperationRejection.CapacityExceeded, NetworkCommandFailureKind.CapacityExceeded);
            if (networkPreparation is not { } prepared || prepared.Reads != reads
                || prepared.Snapshot.Target != request.Target || prepared.Snapshot.Identity != request.Preparation
                || !prepared.Snapshot.IsPrecheckEligible || !CurrentNetworksLocked(prepared.Reads))
                return RejectNetworkOperation(request, NetworkOperationRejection.PreparationNotCurrent, NetworkCommandFailureKind.InvalidRequest);
            var evidence = prepared.Snapshot;
            // Validation and consumption share the same gate with events, close, and supersession.
            prepared.Snapshot = evidence with { Identity = evidence.Identity with { Revision = evidence.Identity.Revision + 1 },
                Phase = NetworkPreparationPhase.Consumed };
            networkOperation = new(++nextNetworkOperation, request, NetworkOperationPhase.CommandPending, evidence);
            networkOperationEndpoint = reads.Containers.Connection.Metadata.Endpoint;
            networkOperationEngineId = reads.Containers.Connection.Metadata.EngineId;
            FenceNetworkOperationReadsLocked(reads);
            long id = networkOperation.OperationId;
            completion = Task.Run(() => RunNetworkOperationAsync(reads, commands, id, request, cancellationToken));
            networkOperationCompletion = completion;
            reads.OperationWorkflow = completion;
        }
        NotifyNetworksChanged();
        return completion;
    }

    private static Task<NetworkOperationSnapshot> RejectNetworkOperation(NetworkOperationRequest request,
        NetworkOperationRejection reason, NetworkCommandFailureKind kind) => Task.FromResult(new NetworkOperationSnapshot(0, request,
            NetworkOperationPhase.Completed, CommandOutcome: new(NetworkCommandDisposition.NotDispatched,
                Failure: new(kind, "The network operation was not admitted.")),
            Resolution: NetworkOperationResolution.NotDispatched, Rejection: reason));

    private bool CurrentNetworkOperationLocked(NetworkReads reads, long id)
        => CurrentNetworksLocked(reads) && networkOperation?.OperationId == id;

    private void FenceNetworkOperationReadsLocked(NetworkReads reads)
    {
        ++reads.ObservationRevision;
        ++reads.ListRevision;
        ++reads.DetailRevision;
        if (!reads.OperationReadActive) reads.Active?.Cancel();
        InvalidateNetworkEventDetailLocked(reads, null);
        reads.Signal.Writer.TryWrite(0);
    }

    private async Task<NetworkOperationSnapshot> RunNetworkOperationAsync(NetworkReads reads, INetworkCommands commands,
        long id, NetworkOperationRequest request, CancellationToken caller)
    {
        try
        {
            NetworkCommandResult outcome;
            using (var deadline = new CancellationTokenSource())
            using (var command = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, caller, deadline.Token))
            {
                lock (gate)
                {
                    if (!CurrentNetworkOperationLocked(reads, id)) return networkOperation!;
                    outcome = command.IsCancellationRequested
                        ? new(NetworkCommandDisposition.NotDispatched, Failure: new(NetworkCommandFailureKind.Canceled, "Canceled before dispatch."))
                        : new(NetworkCommandDisposition.Uncertain);
                    networkOperation = networkOperation! with { CommandOutcome = outcome };
                }
                using var callerRegistration = caller.Register(() =>
                {
                    lock (gate)
                        if (networkOperation?.OperationId == id)
                            networkOperation = networkOperation with { Interruption = networkOperation.Interruption == NetworkOperationInterruption.CommandDeadline
                                ? NetworkOperationInterruption.CommandDeadline : NetworkOperationInterruption.CallerCanceled };
                    NotifyNetworkOperationChanged();
                });
                // A separate clock still expires after caller cancellation, even for non-cooperative commands.
                using var deadlineRegistration = deadline.Token.Register(() =>
                {
                    lock (gate)
                    {
                        if (!CurrentNetworkOperationLocked(reads, id)) return;
                        networkOperation = networkOperation! with { Interruption = NetworkOperationInterruption.CommandDeadline,
                            CommandOutcome = new(NetworkCommandDisposition.Uncertain,
                                Failure: new(NetworkCommandFailureKind.Deadline, "The network command exceeded its deadline.")) };
                        NetworkTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                            "The network command exceeded its deadline."), false);
                    }
                    NotifyContainerBrowsers();
                });
                if (outcome.RequiresReconciliation)
                {
                    deadline.CancelAfter(networkOperationOptions.CommandTimeout);
                    try { outcome = await commands.DeleteNetworkAsync(request.Target.Id, command.Token).ConfigureAwait(false); }
                    catch (NetworkCommandCanceledException canceled) { outcome = canceled.Outcome; }
                    catch (OperationCanceledException)
                    { outcome = new(NetworkCommandDisposition.Uncertain, Failure: new(caller.IsCancellationRequested
                        ? NetworkCommandFailureKind.Canceled : deadline.IsCancellationRequested ? NetworkCommandFailureKind.Deadline
                        : NetworkCommandFailureKind.StaleConnection, "The command was interrupted; authoritative observation is required.")); }
                    catch (Exception)
                    { outcome = new(NetworkCommandDisposition.Uncertain, Failure: new(NetworkCommandFailureKind.UnexpectedResponse,
                        "The command did not return classified evidence; authoritative observation is required.")); }
                }
            }
            bool lost;
            Task<NetworkObservation>? first = null;
            lock (gate)
            {
                if (networkOperation?.OperationId != id) return networkOperation!;
                // Late protocol evidence remains useful, even after teardown. It cannot publish
                // resource state or make an acknowledgement into proof of deletion.
                networkOperation = networkOperation with { CommandOutcome = outcome };
                if (!CurrentNetworkOperationLocked(reads, id))
                {
                    if (!outcome.RequiresReconciliation)
                        networkOperation = networkOperation with { Resolution = NetworkOperationResolution.NotDispatched };
                    return networkOperation;
                }
                networkOperation = networkOperation with {
                    Phase = outcome.RequiresReconciliation ? NetworkOperationPhase.Reconciling : NetworkOperationPhase.Completed,
                    Resolution = outcome.RequiresReconciliation ? null : NetworkOperationResolution.NotDispatched };
                lost = outcome.Failure?.Kind is NetworkCommandFailureKind.TransportFailure or NetworkCommandFailureKind.Deadline
                    or NetworkCommandFailureKind.StaleConnection or NetworkCommandFailureKind.ConnectionDisposed;
                if (outcome.RequiresReconciliation && !lost)
                {
                    FenceNetworkOperationReadsLocked(reads);
                    first = QueueNetworkObservationAsync(reads, request.Target.Id,
                        outcome.Failure?.Kind == NetworkCommandFailureKind.Conflict);
                }
                else if (!lost) { reads.Refresh = true; reads.Signal.Writer.TryWrite(0); }
                if (lost) NetworkTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                    outcome.Failure!.Description, outcome.HttpStatusCode, outcome.DaemonMessage), false);
            }
            if (lost) NotifyContainerBrowsers();
            else NotifyNetworksChanged();
            if (!lost && outcome.RequiresReconciliation) await ObserveNetworkOperationAsync(reads, id, first).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
        finally
        {
            lock (gate)
                if (networkOperation?.OperationId == id && networkOperation.Pending)
                    networkOperation = networkOperation with { Phase = NetworkOperationPhase.Completed, Resolution = NetworkOperationResolution.Unresolved };
            NotifyNetworkOperationChanged();
        }
        lock (gate) return networkOperation!;
    }

    private async Task ObserveNetworkOperationAsync(NetworkReads reads, long id, Task<NetworkObservation>? first = null)
    {
        // The budget is per connection observation pass. Retry never repeats the mutation.
        for (int attempt = 0; attempt < networkOperationOptions.MaximumObservations; ++attempt)
        {
            string targetId;
            bool dependencies;
            lock (gate)
            {
                if (!CurrentNetworkOperationLocked(reads, id)) return;
                targetId = networkOperation!.Request.Target.Id;
                dependencies = networkOperation.CommandOutcome?.Failure?.Kind == NetworkCommandFailureKind.Conflict;
            }
            var observed = await (attempt == 0 && first is not null ? first
                : QueueNetworkObservationAsync(reads, targetId, dependencies)).ConfigureAwait(false);
            bool finished;
            lock (gate)
            {
                if (!CurrentNetworkOperationLocked(reads, id)) return;
                var current = networkOperation!;
                bool stale = observed.Revision != reads.ObservationRevision;
                bool authoritative = !stale && observed.Exists.HasValue;
                var dependencyState = !dependencies ? NetworkAttachmentObservationState.NotRequested : stale
                    ? NetworkAttachmentObservationState.Invalidated : observed.AttachmentState;
                bool resolved = authoritative && (!dependencies || dependencyState == NetworkAttachmentObservationState.Complete);
                finished = resolved || attempt + 1 == networkOperationOptions.MaximumObservations;
                networkOperation = current with { Phase = finished ? NetworkOperationPhase.Completed : NetworkOperationPhase.Reconciling,
                    Resolution = !finished ? null : !resolved ? NetworkOperationResolution.Unresolved : observed.Exists == false
                        ? NetworkOperationResolution.ObservedAbsent : NetworkOperationResolution.ObservedPresent,
                    ObservedConnectionGeneration = stale ? current.ObservedConnectionGeneration : networkSnapshot.ConnectionGeneration,
                    ObservedExists = authoritative ? observed.Exists : null,
                    ObservedListing = observed.Listing, ObservationListFailure = observed.ListFailure,
                    ObservationFailure = observed.Failure, ObservationInvalidated = stale, Observations = current.Observations + 1,
                    RefreshedInspection = observed.Inspection,
                    AttachmentObservationState = dependencyState, AttachmentFailure = observed.AttachmentFailure,
                    AttachmentRefreshAttempts = current.AttachmentRefreshAttempts + (dependencies ? 1 : 0) };
            }
            NotifyNetworkOperationChanged();
            if (finished) return;
            await delay.DelayAsync(networkOperationOptions.FollowupInterval, reads.Lifetime.Token).ConfigureAwait(false);
        }
    }

    private Task<NetworkObservation> QueueNetworkObservationAsync(NetworkReads reads, string targetId, bool dependencies)
    {
        lock (gate)
        {
            reads.Lifetime.Token.ThrowIfCancellationRequested();
            var observation = new NetworkObservationRequest(targetId, dependencies);
            reads.OperationObservation = observation; // One operation awaits one slot; never a queue.
            reads.Signal.Writer.TryWrite(0);
            return observation.Completion.Task.WaitAsync(reads.Lifetime.Token);
        }
    }

    private void ResumeNetworkOperationLocked(NetworkReads reads)
    {
        if (networkOperation is not { Resolution: NetworkOperationResolution.Unresolved, CommandOutcome.RequiresReconciliation: true } current
            || string.IsNullOrWhiteSpace(networkOperationEngineId) || networkOperationEndpoint != reads.Containers.Connection.Metadata.Endpoint
            || networkOperationEngineId != reads.Containers.Connection.Metadata.EngineId) return;
        networkOperation = current with { Phase = NetworkOperationPhase.Reconciling, Resolution = null };
        long id = current.OperationId;
        var task = Task.Run(async () =>
        {
            try { await ObserveNetworkOperationAsync(reads, id).ConfigureAwait(false); }
            catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
            finally
            {
                lock (gate)
                    if (networkOperation?.OperationId == id && networkOperation.Pending)
                        networkOperation = networkOperation with { Phase = NetworkOperationPhase.Completed, Resolution = NetworkOperationResolution.Unresolved };
                NotifyNetworkOperationChanged();
            }
            lock (gate) return networkOperation!;
        });
        reads.OperationWorkflow = task;
        networkOperationCompletion = task;
    }

    private void InvalidateNetworkOperationLocked()
    {
        if (networkOperation is not { Pending: true } current) return;
        networkOperation = current with { Phase = NetworkOperationPhase.Completed,
            Interruption = current.Interruption ?? NetworkOperationInterruption.ConnectionChanged,
            ObservedExists = null, Resolution = current.CommandOutcome is null
                ? NetworkOperationResolution.NotDispatched : NetworkOperationResolution.Unresolved,
            CommandOutcome = current.CommandOutcome ?? new(NetworkCommandDisposition.NotDispatched,
                Failure: new(NetworkCommandFailureKind.StaleConnection, "The connection changed before dispatch.")) };
    }

    private void NotifyNetworkOperationChanged()
    {
        lock (gate)
        {
            if (ReferenceEquals(notifiedNetworkOperation, networkOperation)) return;
            notifiedNetworkOperation = networkOperation;
        }
        NotifyImageObservers(NetworkOperationChanged);
    }

    private sealed record NetworkObservation(bool? Exists, IReadOnlyList<NetworkSummary>? Listing,
        EngineResourceFailure? ListFailure, EngineResourceFailure? Failure, NetworkInspection? Inspection,
        NetworkAttachmentObservationState AttachmentState, EngineResourceFailure? AttachmentFailure, long Revision);
    private sealed class NetworkObservationRequest(string targetId, bool dependencies)
    {
        public string TargetId { get; } = targetId;
        public bool Dependencies { get; } = dependencies;
        public TaskCompletionSource<NetworkObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // One shared scheduler slot: full-list then exact-target Inspect, independently of selection.
    // Browsing receives a turn between attempts; events fence the whole observation epoch.
    private async Task ReadNetworkObservationAsync(NetworkReads reads, NetworkObservationRequest observation)
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
        using var deadlineRegistration = deadline.Token.Register(() => NetworkReadDeadline(reads, request, true));
        deadline.CancelAfter(options.OperationTimeout);
        try
        {
            var result = await reads.Reader!.ListNetworksAsync(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            var listing = result.Value is null ? null : CopyNetworkListing(result.Value);
            NetworkInspection? inspection = null;
            EngineResourceFailure? failure = null;
            if (result.Failure?.Kind != EngineResourceFailureKind.TransportLoss)
            {
                var detail = await reads.Reader.InspectNetworkAsync(observation.TargetId, request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                failure = detail.Failure;
                if (detail.Value is { } value)
                {
                    inspection = CopyNetworkInspection(value);
                    if (!StringComparer.Ordinal.Equals(value.Id, observation.TargetId))
                        failure = new(EngineResourceFailureKind.MalformedData, "The network observation returned a different identity.");
                }
            }
            bool? exists = failure is null && inspection is not null ? true
                : failure?.Kind == EngineResourceFailureKind.NotFound ? false : null;
            // Two independently timed authoritative reads can disagree during a race. Keep that unresolved.
            if (result.Failure is not null || listing is null || exists.HasValue
                && listing.Any(row => StringComparer.Ordinal.Equals(row.Id, observation.TargetId)) != exists.Value)
                exists = null;
            var attachmentState = !observation.Dependencies ? NetworkAttachmentObservationState.NotRequested
                : failure is not null ? failure.Kind == EngineResourceFailureKind.MalformedData
                    ? NetworkAttachmentObservationState.Malformed : NetworkAttachmentObservationState.Failed
                : inspection?.Containers is null ? NetworkAttachmentObservationState.Unavailable
                : inspection.Containers.Any(pair => UsableNetworkEventIdentity(pair.Key) is null || pair.Value is null)
                    ? NetworkAttachmentObservationState.Malformed : NetworkAttachmentObservationState.Complete;
            var attachmentFailure = observation.Dependencies ? failure : null;
            lock (gate)
            {
                if (!CurrentNetworksLocked(reads) || request.IsCancellationRequested) return;
                var transport = new[] { result.Failure, failure }.FirstOrDefault(item => item?.Kind == EngineResourceFailureKind.TransportLoss);
                if (transport is not null)
                {
                    if (networkOperation is { Pending: true } current)
                        networkOperation = current with { ObservedListing = listing, ObservationListFailure = result.Failure,
                            ObservationFailure = failure, AttachmentFailure = attachmentFailure,
                            RefreshedInspection = observation.Dependencies ? inspection : null, ObservationInvalidated = true,
                            AttachmentObservationState = observation.Dependencies ? NetworkAttachmentObservationState.Failed
                                : current.AttachmentObservationState };
                    NetworkTransportLostLocked(reads, transport, false);
                    return;
                }
                bool stale = epoch != reads.ObservationRevision;
                if (!stale && listRevision == reads.ListRevision)
                {
                    if (listing is not null && result.Failure is null) ApplyNetworkListingLocked(reads, listing);
                    else networkSnapshot = networkSnapshot with { State = ResourceReadState.Failed, Failure = result.Failure };
                    if (exists == false) RemovePreparedNetworkLocked(observation.TargetId);
                }
                observation.Completion.TrySetResult(new(exists, listing, result.Failure, failure,
                    observation.Dependencies ? inspection : null, attachmentState, attachmentFailure, epoch));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate)
                if (CurrentNetworksLocked(reads)) observation.Completion.TrySetResult(new(null, null, null,
                    new(EngineResourceFailureKind.MalformedData, "The network observation did not return classified evidence."),
                    null, observation.Dependencies ? NetworkAttachmentObservationState.Failed : NetworkAttachmentObservationState.NotRequested,
                    null, epoch));
        }
        finally
        {
            if (deadline.IsCancellationRequested) NetworkReadDeadline(reads, request, true);
            lock (gate) { reads.OperationReadActive = false; reads.Active = null; reads.ActiveDeadline = null; }
            NotifyContainerBrowsers();
        }
    }
}
