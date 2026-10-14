using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : INetworkDeletionPreparation
{
    private NetworkPreparationRequest? networkPreparation;
    private long nextNetworkPreparation;
    private NetworkDeletionPreparationSnapshot? notifiedNetworkPreparation;

    public event EventHandler? NetworkDeletionPreparationChanged;
    public NetworkDeletionPreparationSnapshot? NetworkDeletionPreparation { get { lock (gate) return networkPreparation?.Snapshot; } }

    public bool IsNetworkDeletionPreparationCurrent(NetworkPreparationIdentity identity)
    {
        lock (gate) return networkPreparation is { } current && current.Snapshot.Identity == identity
            && current.Snapshot.IsPrecheckEligible && CurrentNetworksLocked(current.Reads);
    }

    public Task<NetworkDeletionPreparationSnapshot> PrepareNetworkDeletionAsync(NetworkDeletionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        NetworkPreparationRequest request;
        lock (gate)
        {
            NetworkPreparationFailureKind? failure = cancellationToken.IsCancellationRequested ? NetworkPreparationFailureKind.Canceled
                : UsableNetworkEventIdentity(target.Id) is null ? NetworkPreparationFailureKind.InvalidRequest
                : disposed || networkReads is null || !CurrentNetworksLocked(networkReads)
                    || target.ConnectionGeneration != networkSnapshot.ConnectionGeneration ? NetworkPreparationFailureKind.StaleConnection
                : networkReads.Reader is null ? NetworkPreparationFailureKind.CapabilityUnavailable
                : !networkSnapshot.Networks.Any(row => StringComparer.Ordinal.Equals(row.Id, target.Id)) ? NetworkPreparationFailureKind.TargetAbsent
                : networkPreparation is { Waiting: true } && !networkPreparation.Work.IsCompleted ? NetworkPreparationFailureKind.CapacityExceeded : null;
            if (failure is { } rejected)
                return Task.FromResult(new NetworkDeletionPreparationSnapshot(default, target, NetworkPreparationPhase.Failed,
                    NetworkDeletionEligibility.Unknown, Array.Empty<NetworkDeletionBlockReason>(), Failure: new(rejected)));
            var reads = networkReads!;
            var previous = reads.NetworkPreparationWorkflow;
            InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.Superseded, cancel: true);
            request = new(reads, new(new(++nextNetworkPreparation, 1), target, NetworkPreparationPhase.Loading,
                NetworkDeletionEligibility.Unknown, Array.Empty<NetworkDeletionBlockReason>()),
                networkSnapshot.Networks.Single(row => StringComparer.Ordinal.Equals(row.Id, target.Id)).Name, !previous.IsCompleted);
            networkPreparation = request;
            // Set ownership before registering callbacks, which can execute synchronously.
            request.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, cancellationToken, request.Deadline.Token);
            request.Work = Task.Run(() => RunNetworkPreparationAsync(request, previous));
            reads.NetworkPreparationWorkflow = request.Work;
            request.Registration = request.Cancellation.Token.Register(() => NetworkPreparationCanceled(request, cancellationToken));
            request.DeadlineRegistration = request.Deadline.Token.Register(() => NetworkPreparationDeadline(request));
            request.Deadline.CancelAfter(options.OperationTimeout);
        }
        NotifyNetworkPreparationChanged();
        return request.Work;
    }

    public Task CloseNetworkDeletionPreparationAsync(NetworkPreparationIdentity identity)
    {
        Task work;
        lock (gate)
        {
            if (networkPreparation is not { } current || current.Snapshot.Identity.PreparationId != identity.PreparationId) return Task.CompletedTask;
            InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.Closed, cancel: true);
            work = current.Work;
        }
        NotifyNetworkPreparationChanged();
        return work;
    }

    public void InvalidateNetworkDeletionPreparation(NetworkPreparationIdentity identity)
    {
        lock (gate)
        {
            if (networkPreparation?.Snapshot.Identity != identity) return;
            InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.ExplicitInvalidation, cancel: true);
        }
        NotifyNetworkPreparationChanged();
    }

    private void InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind reason, bool cancel = false, string? id = null)
    {
        if (networkPreparation is not { } current || (id is not null
            && !StringComparer.Ordinal.Equals(id, current.Snapshot.Target.Id))) return;
        if (current.Snapshot.Phase is NetworkPreparationPhase.Loading or NetworkPreparationPhase.Completed)
            current.Snapshot = current.Snapshot with { Identity = current.Snapshot.Identity with { Revision = current.Snapshot.Identity.Revision + 1 },
                Phase = NetworkPreparationPhase.Invalidated, Eligibility = NetworkDeletionEligibility.Unknown,
                Reasons = Array.AsReadOnly(new[] { NetworkDeletionBlockReason.EvidenceNotCurrent }), Failure = new(reason) };
        if (cancel && !current.Finished) current.Cancellation?.Cancel();
    }

    private void NetworkPreparationCanceled(NetworkPreparationRequest request, CancellationToken caller)
    {
        lock (gate)
        {
            if (request.Snapshot.Phase != NetworkPreparationPhase.Loading) return;
            var kind = request.Deadline.IsCancellationRequested ? NetworkPreparationFailureKind.Deadline
                : caller.IsCancellationRequested ? NetworkPreparationFailureKind.Canceled : NetworkPreparationFailureKind.ConnectionChanged;
            request.Snapshot = request.Snapshot with { Identity = request.Snapshot.Identity with { Revision = request.Snapshot.Identity.Revision + 1 },
                Eligibility = NetworkDeletionEligibility.Unknown, Reasons = Array.AsReadOnly(new[] { NetworkDeletionBlockReason.EvidenceNotCurrent }),
                Phase = kind == NetworkPreparationFailureKind.Deadline ? NetworkPreparationPhase.Failed : NetworkPreparationPhase.Invalidated,
                Failure = new(kind) };
        }
        NotifyNetworkPreparationChanged();
    }

    private void NetworkPreparationDeadline(NetworkPreparationRequest request)
    {
        bool lost = false;
        lock (gate)
        {
            if (request.Finished) return;
            if (request.Snapshot.Phase == NetworkPreparationPhase.Loading)
                request.Snapshot = request.Snapshot with
                {
                    Identity = request.Snapshot.Identity with { Revision = request.Snapshot.Identity.Revision + 1 },
                    Phase = NetworkPreparationPhase.Failed, Eligibility = NetworkDeletionEligibility.Unknown,
                    Reasons = Array.AsReadOnly(new[] { NetworkDeletionBlockReason.EvidenceNotCurrent }),
                    Failure = new(NetworkPreparationFailureKind.Deadline)
                };
            // A superseded/event-fenced read can ignore cancellation. Its independent clock still
            // reports lost continuity, without granting its late result publication rights.
            if (CurrentNetworksLocked(request.Reads))
            {
                NetworkTransportLostLocked(request.Reads, new(EngineResourceFailureKind.TransportLoss,
                    "The network deletion preparation exceeded its deadline."), false);
                lost = true;
            }
        }
        if (lost) NotifyContainerBrowsers();
    }

    private async Task<NetworkDeletionPreparationSnapshot> RunNetworkPreparationAsync(NetworkPreparationRequest request, Task previous)
    {
        bool lost = false;
        try
        {
            // Retain the fence and borrowed connection until even non-cooperative superseded work returns.
            await previous.ConfigureAwait(false);
            lock (gate)
            {
                request.Waiting = false;
                if (!CurrentNetworkPreparationLocked(request)) return request.Snapshot;
            }
            var result = await request.Reads.Reader!
                .InspectNetworkAsync(request.Snapshot.Target.Id, request.Cancellation!.Token).ConfigureAwait(false);
            lock (gate)
            {
                // Transport loss remains shared connection evidence even after an event invalidation.
                if (!request.Cancellation!.IsCancellationRequested && CurrentNetworksLocked(request.Reads)
                    && result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
                {
                    if (CurrentNetworkPreparationLocked(request)) FailNetworkPreparationLocked(request, result.Failure);
                    NetworkTransportLostLocked(request.Reads, result.Failure, false);
                    lost = true;
                }
                else if (CurrentNetworkPreparationLocked(request))
                {
                    if (result.Failure is { } failure)
                    {
                        FailNetworkPreparationLocked(request, failure);
                        if (failure.Kind == EngineResourceFailureKind.NotFound)
                            RemovePreparedNetworkLocked(request.Snapshot.Target.Id);
                    }
                    else CompleteNetworkPreparationLocked(request, result.Value!);
                }
            }
        }
        catch (OperationCanceledException) when (request.Cancellation!.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate)
                if (CurrentNetworkPreparationLocked(request)) FailNetworkPreparationLocked(request,
                    new(EngineResourceFailureKind.MalformedData, "The network inspection did not return classified evidence."));
        }
        finally
        {
            request.Registration.Dispose();
            request.DeadlineRegistration.Dispose();
            lock (gate) { request.Finished = true; request.Cancellation!.Dispose(); request.Deadline.Dispose(); }
            if (lost) NotifyContainerBrowsers();
            else { NotifyNetworkPreparationChanged(); NotifyNetworksChanged(); }
        }
        lock (gate) return request.Snapshot;
    }

    private void RemovePreparedNetworkLocked(string id)
    {
        networkSnapshot = networkSnapshot with { Networks = Array.AsReadOnly(networkSnapshot.Networks
            .Where(row => !StringComparer.Ordinal.Equals(row.Id, id)).ToArray()) };
        if (networkReads is { } reads && StringComparer.Ordinal.Equals(networkSnapshot.SelectedNetworkId, id))
            RequestNetworkInspectionLocked(reads, null, false);
    }

    private static void CompleteNetworkPreparationLocked(NetworkPreparationRequest request, NetworkInspection inspection)
    {
        var evidence = CopyNetworkInspection(inspection);
        var reasons = new List<NetworkDeletionBlockReason>();
        bool matching = StringComparer.Ordinal.Equals(evidence.Id, request.Snapshot.Target.Id);
        if (!matching) reasons.Add(NetworkDeletionBlockReason.IdentityMismatch);
        // Retain built-in evidence from either source, including contradictory sources.
        if (IsBuiltInNetworkName(request.ListedName) || IsBuiltInNetworkName(evidence.Name))
            reasons.Add(NetworkDeletionBlockReason.BuiltInNetwork);
        if (UsableNetworkEventIdentity(evidence.Name) is null) reasons.Add(NetworkDeletionBlockReason.NameUnavailable);
        if (request.ListedName is not null && !StringComparer.Ordinal.Equals(request.ListedName, evidence.Name))
            reasons.Add(NetworkDeletionBlockReason.NameContradiction);
        if (evidence.Containers is null) reasons.Add(NetworkDeletionBlockReason.AttachmentsUnavailable);
        else
        {
            if (evidence.Containers.Count != 0) reasons.Add(NetworkDeletionBlockReason.AttachmentsRemain);
            if (evidence.Containers.Any(pair => UsableNetworkEventIdentity(pair.Key) is null || pair.Value is null))
                reasons.Add(NetworkDeletionBlockReason.AttachmentEvidenceInvalid);
        }
        request.Snapshot = request.Snapshot with
        {
            Identity = request.Snapshot.Identity with { Revision = request.Snapshot.Identity.Revision + 1 },
            Phase = matching ? NetworkPreparationPhase.Completed : NetworkPreparationPhase.Failed,
            Inspection = evidence, Reasons = Array.AsReadOnly(reasons.ToArray()),
            Eligibility = reasons.Count == 0 ? NetworkDeletionEligibility.PrecheckEligible : NetworkDeletionEligibility.Blocked,
            Failure = matching ? null : new(NetworkPreparationFailureKind.AcquisitionFailed,
                new(EngineResourceFailureKind.MalformedData, "The network inspection returned a different identity."))
        };
    }

    private static bool IsBuiltInNetworkName(string? name) => name is "bridge" or "host" or "none";

    private bool CurrentNetworkPreparationLocked(NetworkPreparationRequest request) => ReferenceEquals(networkPreparation, request)
        && CurrentNetworksLocked(request.Reads) && !request.Cancellation!.IsCancellationRequested
        && request.Snapshot.Phase == NetworkPreparationPhase.Loading;

    private static void FailNetworkPreparationLocked(NetworkPreparationRequest request, EngineResourceFailure failure)
        => request.Snapshot = request.Snapshot with { Identity = request.Snapshot.Identity with { Revision = request.Snapshot.Identity.Revision + 1 },
            Phase = NetworkPreparationPhase.Failed, Failure = new(NetworkPreparationFailureKind.AcquisitionFailed, failure) };

    private void NotifyNetworkPreparationChanged()
    {
        lock (gate)
        {
            if (ReferenceEquals(notifiedNetworkPreparation, networkPreparation?.Snapshot)) return;
            notifiedNetworkPreparation = networkPreparation?.Snapshot;
        }
        NotifyImageObservers(NetworkDeletionPreparationChanged);
    }

    private sealed class NetworkPreparationRequest(NetworkReads reads, NetworkDeletionPreparationSnapshot snapshot, string? listedName, bool waiting)
    {
        public NetworkReads Reads { get; } = reads;
        public NetworkDeletionPreparationSnapshot Snapshot = snapshot;
        public string? ListedName { get; } = listedName;
        public bool Waiting = waiting;
        public bool Finished;
        public CancellationTokenSource Deadline { get; } = new();
        public CancellationTokenSource? Cancellation;
        public CancellationTokenRegistration Registration, DeadlineRegistration;
        public Task<NetworkDeletionPreparationSnapshot> Work = Task.FromResult(snapshot);
    }
}
