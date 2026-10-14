using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IVolumeDeletionPreparation
{
    private PreparationRequest? preparation;
    private long nextPreparation;
    private VolumeDeletionPreparationSnapshot? notifiedPreparation;

    public event EventHandler? VolumeDeletionPreparationChanged;
    public VolumeDeletionPreparationSnapshot? VolumeDeletionPreparation { get { lock (gate) return preparation?.Snapshot; } }

    public bool IsVolumeDeletionPreparationCurrent(VolumePreparationIdentity identity)
    {
        lock (gate) return preparation is { } current && current.Snapshot.Identity == identity
            && current.Snapshot.HasUsableEvidence && CurrentEventsLocked(current.Reads);
    }

    public Task<VolumeDeletionPreparationSnapshot> PrepareVolumeDeletionAsync(VolumeDeletionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        PreparationRequest request;
        lock (gate)
        {
            VolumePreparationFailureKind? failure = cancellationToken.IsCancellationRequested ? VolumePreparationFailureKind.Canceled
                : disposed || containerReads is null || !CurrentEventsLocked(containerReads)
                    || target.ConnectionGeneration != containerSnapshot.ConnectionGeneration ? VolumePreparationFailureKind.StaleConnection
                : UsableVolumeEventIdentity(target.Name) is null ? VolumePreparationFailureKind.InvalidRequest
                : containerReads.Connection is not IVolumeDependencyReader ? VolumePreparationFailureKind.CapabilityUnavailable
                : preparation is { Waiting: true } && !preparation.Work.IsCompleted ? VolumePreparationFailureKind.CapacityExceeded : null;
            if (failure is { } rejected)
                return Task.FromResult(new VolumeDeletionPreparationSnapshot(default, target, VolumePreparationPhase.Failed,
                    Failure: new(rejected)));
            var reads = containerReads!;
            var previous = reads.PreparationWorkflow;
            InvalidatePreparationLocked(VolumePreparationFailureKind.Superseded, cancel: true);
            request = new(reads, new(new(++nextPreparation, 1), target, VolumePreparationPhase.Loading), !previous.IsCompleted);
            preparation = request;
            // Set ownership before registering callbacks, which can execute synchronously.
            request.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, cancellationToken, request.Deadline.Token);
            request.Work = Task.Run(() => RunPreparationAsync(request, previous));
            reads.PreparationWorkflow = request.Work;
            request.Registration = request.Cancellation.Token.Register(() => PreparationCanceled(request, cancellationToken));
            request.Deadline.CancelAfter(options.OperationTimeout);
        }
        NotifyPreparationChanged();
        return request.Work;
    }

    public Task CloseVolumeDeletionPreparationAsync(VolumePreparationIdentity identity)
    {
        Task work;
        lock (gate)
        {
            if (preparation is not { } current || current.Snapshot.Identity.PreparationId != identity.PreparationId) return Task.CompletedTask;
            InvalidatePreparationLocked(VolumePreparationFailureKind.Closed, cancel: true);
            work = current.Work;
        }
        NotifyPreparationChanged();
        return work;
    }

    private void InvalidatePreparationLocked(VolumePreparationFailureKind reason, bool cancel = false, string? name = null)
    {
        if (preparation is not { } current || (name is not null
            && !StringComparer.Ordinal.Equals(name, current.Snapshot.Target.Name))) return;
        if (current.Snapshot.Phase is VolumePreparationPhase.Loading or VolumePreparationPhase.Completed)
            current.Snapshot = current.Snapshot with { Identity = current.Snapshot.Identity with { Revision = current.Snapshot.Identity.Revision + 1 },
                Phase = VolumePreparationPhase.Invalidated, Failure = new(reason) };
        if (cancel && !current.Finished) current.Cancellation?.Cancel();
    }

    private void PreparationCanceled(PreparationRequest request, CancellationToken caller)
    {
        bool lost = false;
        lock (gate)
        {
            if (request.Snapshot.Phase != VolumePreparationPhase.Loading) return;
            var kind = request.Deadline.IsCancellationRequested ? VolumePreparationFailureKind.Deadline
                : caller.IsCancellationRequested ? VolumePreparationFailureKind.Canceled : VolumePreparationFailureKind.ConnectionChanged;
            request.Snapshot = request.Snapshot with { Identity = request.Snapshot.Identity with { Revision = request.Snapshot.Identity.Revision + 1 },
                Phase = kind == VolumePreparationFailureKind.Deadline ? VolumePreparationPhase.Failed : VolumePreparationPhase.Invalidated,
                Failure = new(kind) };
            if (kind == VolumePreparationFailureKind.Deadline && CurrentEventsLocked(request.Reads))
            {
                ResourceTransportLostLocked(request.Reads, new(EngineResourceFailureKind.TransportLoss,
                    "The volume dependency preparation exceeded its deadline."), false);
                lost = true;
            }
        }
        if (lost) NotifyContainerBrowsers();
        NotifyPreparationChanged();
    }

    private async Task<VolumeDeletionPreparationSnapshot> RunPreparationAsync(PreparationRequest request, Task previous)
    {
        bool lost = false;
        try
        {
            // Retain the fence and borrowed connection until even non-cooperative superseded work returns.
            await previous.ConfigureAwait(false);
            lock (gate)
            {
                request.Waiting = false;
                if (!CurrentPreparationLocked(request)) return request.Snapshot;
            }
            var result = await ((IVolumeDependencyReader)request.Reads.Connection)
                .QueryVolumeDependenciesAsync(request.Snapshot.Target.Name, request.Cancellation!.Token).ConfigureAwait(false);
            lock (gate)
            {
                // Transport loss remains shared connection evidence even after an event invalidation.
                if (!request.Cancellation!.IsCancellationRequested && CurrentEventsLocked(request.Reads)
                    && result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
                {
                    FailPreparationLocked(request, result.Failure);
                    ResourceTransportLostLocked(request.Reads, result.Failure, false);
                    lost = true;
                }
                else if (CurrentPreparationLocked(request))
                {
                    if (result.Failure is { } failure) FailPreparationLocked(request, failure);
                    else if (!StringComparer.Ordinal.Equals(result.Value!.VolumeName, request.Snapshot.Target.Name))
                        FailPreparationLocked(request, new(EngineResourceFailureKind.MalformedData,
                            "The dependency query returned a different volume identity."));
                    else request.Snapshot = request.Snapshot with { Identity = request.Snapshot.Identity with { Revision = request.Snapshot.Identity.Revision + 1 },
                        Phase = VolumePreparationPhase.Completed, Dependencies = result.Value };
                }
            }
        }
        catch (OperationCanceledException) when (request.Cancellation!.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate)
                if (CurrentPreparationLocked(request)) FailPreparationLocked(request,
                    new(EngineResourceFailureKind.MalformedData, "The dependency query did not return classified evidence."));
        }
        finally
        {
            request.Registration.Dispose();
            lock (gate) { request.Finished = true; request.Cancellation!.Dispose(); request.Deadline.Dispose(); }
            if (lost) NotifyContainerBrowsers();
            else NotifyPreparationChanged();
        }
        lock (gate) return request.Snapshot;
    }

    private bool CurrentPreparationLocked(PreparationRequest request) => ReferenceEquals(preparation, request)
        && CurrentEventsLocked(request.Reads) && !request.Cancellation!.IsCancellationRequested
        && request.Snapshot.Phase == VolumePreparationPhase.Loading;

    private static void FailPreparationLocked(PreparationRequest request, EngineResourceFailure failure)
        => request.Snapshot = request.Snapshot with { Identity = request.Snapshot.Identity with { Revision = request.Snapshot.Identity.Revision + 1 },
            Phase = VolumePreparationPhase.Failed, Failure = new(VolumePreparationFailureKind.AcquisitionFailed, failure) };

    private void NotifyPreparationChanged()
    {
        lock (gate)
        {
            if (ReferenceEquals(notifiedPreparation, preparation?.Snapshot)) return;
            notifiedPreparation = preparation?.Snapshot;
        }
        NotifyImageObservers(VolumeDeletionPreparationChanged);
    }

    private sealed class PreparationRequest(ContainerReads reads, VolumeDeletionPreparationSnapshot snapshot, bool waiting)
    {
        public ContainerReads Reads { get; } = reads;
        public VolumeDeletionPreparationSnapshot Snapshot = snapshot;
        public bool Waiting = waiting;
        public bool Finished;
        public CancellationTokenSource Deadline { get; } = new();
        public CancellationTokenSource? Cancellation;
        public CancellationTokenRegistration Registration;
        public Task<VolumeDeletionPreparationSnapshot> Work = Task.FromResult(snapshot);
    }
}
