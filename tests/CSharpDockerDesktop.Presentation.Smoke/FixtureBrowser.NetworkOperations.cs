using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal sealed partial class FixtureBrowser : INetworkDeletionPreparation, INetworkOperations
{
    public int NetworkDeletionScenario, NetworkPrepares, NetworkCommands, NetworkPreparationCloses;
    public NetworkOperationRequest? LastNetworkRequest;
    private NetworkDeletionPreparationSnapshot? networkPreparation;
    private NetworkOperationSnapshot? networkOperation;
    private Task<NetworkDeletionPreparationSnapshot> networkPreparationWork = null!;
    public NetworkDeletionPreparationSnapshot? NetworkDeletionPreparation { get { lock (networkGate) return networkPreparation; } }
    public NetworkOperationSnapshot? NetworkOperation { get { lock (networkGate) return networkOperation; } }
    public event EventHandler? NetworkDeletionPreparationChanged;
    public event EventHandler? NetworkOperationChanged;
    public bool IsNetworkDeletionPreparationCurrent(NetworkPreparationIdentity identity)
    { lock (networkGate) return networkPreparation is { IsPrecheckEligible: true } current && current.Identity == identity && current.Target.ConnectionGeneration == networkSnapshot.ConnectionGeneration; }
    public Task<NetworkDeletionPreparationSnapshot> PrepareNetworkDeletionAsync(NetworkDeletionTarget target, CancellationToken cancellationToken = default)
    {
        Task<NetworkDeletionPreparationSnapshot> work;
        lock (networkGate)
        {
            var snapshot = new NetworkDeletionPreparationSnapshot(new(++NetworkPrepares, 1), target, NetworkPreparationPhase.Loading, NetworkDeletionEligibility.Unknown, []);
            networkPreparation = snapshot;
            work = networkPreparationWork = RunNetworkPreparation(snapshot, NetworkDeletionScenario, cancellationToken);
        }
        NetworkDeletionPreparationChanged?.Invoke(this, EventArgs.Empty); return work;
    }
    private async Task<NetworkDeletionPreparationSnapshot> RunNetworkPreparation(NetworkDeletionPreparationSnapshot initial, int scenario, CancellationToken token)
    {
        using var registration = token.Register(() => InvalidateNetworkPreparation(NetworkPreparationFailureKind.Canceled, initial.Identity.PreparationId));
        // Slow case deliberately joins a non-cooperative acquisition; navigation remains available.
        await Task.Delay(scenario is 5 or 14 ? 8000 : 1000).ConfigureAwait(false);
        NetworkDeletionPreparationSnapshot result;
        lock (networkGate)
        {
            if (networkPreparation?.Identity.PreparationId != initial.Identity.PreparationId) return initial with { Phase = NetworkPreparationPhase.Invalidated, Failure = new(NetworkPreparationFailureKind.Superseded) };
            if (networkPreparation.Phase != NetworkPreparationPhase.Loading) return networkPreparation;
            networkPreparation = networkPreparation with { Identity = initial.Identity with { Revision = 2 }, Phase = scenario == 4 ? NetworkPreparationPhase.Failed : NetworkPreparationPhase.Completed,
                Inspection = scenario == 4 ? null : FixtureNetworkInspection(initial.Target.Id, scenario == 9 ? 0 : scenario),
                Eligibility = scenario == 4 || scenario == 3 ? NetworkDeletionEligibility.Unknown : scenario is 1 or 2 or 6 ? NetworkDeletionEligibility.Blocked : NetworkDeletionEligibility.PrecheckEligible,
                Reasons = scenario is 1 or 2 or 6 ? [NetworkDeletionBlockReason.AttachmentsRemain] : scenario == 3 ? [NetworkDeletionBlockReason.AttachmentsUnavailable] : [],
                Failure = scenario == 4 ? new(NetworkPreparationFailureKind.AcquisitionFailed, new(EngineResourceFailureKind.AccessDenied, "Synthetic access denied", 403, "  PRIVATE original precheck failure\n  ")) : null };
            result = networkPreparation;
        }
        NetworkDeletionPreparationChanged?.Invoke(this, EventArgs.Empty);
        if (scenario is 7 or 8 or 13) _ = ChangeAfterNetworkPreparation(initial.Identity.PreparationId, scenario);
        return result;
    }
    private async Task ChangeAfterNetworkPreparation(long preparationId, int scenario)
    {
        await Task.Delay(2500).ConfigureAwait(false);
        lock (networkGate) if (networkPreparation?.Identity.PreparationId != preparationId) return;
        if (scenario == 8) DisconnectNetworks();
        else if (scenario == 13)
        {
            lock (networkGate) networkSnapshot = networkSnapshot with { Networks = networkSnapshot.Networks.Where(row => row.Id != networkPreparation!.Target.Id).ToArray() };
            InvalidateNetworkPreparation(NetworkPreparationFailureKind.TargetAbsent); NetworksChanged?.Invoke(this, EventArgs.Empty);
        }
        else InvalidateNetworkPreparation(NetworkPreparationFailureKind.ObservedChange);
    }
    public void InvalidateNetworkPreparation(NetworkPreparationFailureKind kind, long? preparationId = null)
    {
        lock (networkGate)
        {
            if (networkPreparation is not { } current || preparationId is { } id && current.Identity.PreparationId != id
                || current.Phase is not (NetworkPreparationPhase.Loading or NetworkPreparationPhase.Completed)) return;
            networkPreparation = current with { Identity = current.Identity with { Revision = current.Identity.Revision + 1 }, Phase = NetworkPreparationPhase.Invalidated, Failure = new(kind) };
        }
        NetworkDeletionPreparationChanged?.Invoke(this, EventArgs.Empty);
    }
    public Task CloseNetworkDeletionPreparationAsync(NetworkPreparationIdentity identity)
    {
        lock (networkGate)
        {
            if (networkPreparation?.Identity.PreparationId != identity.PreparationId) return Task.CompletedTask;
            ++NetworkPreparationCloses;
            InvalidateNetworkPreparation(NetworkPreparationFailureKind.Closed, identity.PreparationId);
            return networkPreparationWork;
        }
    }
    public void InvalidateNetworkDeletionPreparation(NetworkPreparationIdentity identity)
    { if (NetworkDeletionPreparation?.Identity == identity) InvalidateNetworkPreparation(NetworkPreparationFailureKind.ExplicitInvalidation); }
    private static NetworkInspection FixtureNetworkInspection(string id, int scenario) => new(id, "custom-network", "bridge", "local", false, null, null,
        scenario == 3 ? null : scenario is 1 or 2 or 6 or 9
            ? Enumerable.Range(0, scenario == 6 ? 30000 : 2).ToDictionary(i => $"full-container-{i}", i => new NetworkAttachment($"container-{i}", $"full-endpoint-{i}", null, $"10.0.0.{i}", null))
            : new Dictionary<string, NetworkAttachment>(), "{}");
    public async Task<NetworkOperationSnapshot> ExecuteNetworkOperationAsync(NetworkOperationRequest request, CancellationToken cancellationToken = default)
    {
        NetworkOperationSnapshot current;
        int scenario;
        lock (networkGate)
        {
            if (!IsNetworkDeletionPreparationCurrent(request.Preparation) || networkOperation?.Pending == true || networkPreparation!.Target != request.Target)
                return new(0, request, NetworkOperationPhase.Completed, Resolution: NetworkOperationResolution.NotDispatched, Rejection: NetworkOperationRejection.PreparationNotCurrent);
            scenario = NetworkDeletionScenario; LastNetworkRequest = request;
            current = new(++NetworkCommands, request, NetworkOperationPhase.CommandPending, Preparation: networkPreparation);
            networkOperation = current;
            networkPreparation = networkPreparation with { Phase = NetworkPreparationPhase.Consumed, Identity = networkPreparation.Identity with { Revision = networkPreparation.Identity.Revision + 1 } };
        }
        NetworkDeletionPreparationChanged?.Invoke(this, EventArgs.Empty); NetworkOperationChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(1500).ConfigureAwait(false);
        var command = scenario == 9 ? new NetworkCommandResult(NetworkCommandDisposition.DaemonResponse, 409, "  PRIVATE fixture dependency conflict\n  ", "{\"message\":\"PRIVATE conflict\"}", new(NetworkCommandFailureKind.Conflict, "Synthetic dependency conflict"))
            : scenario == 10 ? new NetworkCommandResult(NetworkCommandDisposition.Uncertain, DaemonMessage: "PRIVATE lost response", Failure: new(NetworkCommandFailureKind.TransportFailure, "Synthetic response loss"))
            : scenario == 12 ? new NetworkCommandResult(NetworkCommandDisposition.NotDispatched, Failure: new(NetworkCommandFailureKind.Canceled, "Synthetic cancellation"))
            : new NetworkCommandResult(NetworkCommandDisposition.Acknowledged, 204);
        lock (networkGate) networkOperation = current = current with { Phase = NetworkOperationPhase.Reconciling, CommandOutcome = command };
        NetworkOperationChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(1800).ConfigureAwait(false);
        lock (networkGate)
        {
            bool same = request.Target.ConnectionGeneration == networkSnapshot.ConnectionGeneration;
            var resolution = scenario == 12 ? NetworkOperationResolution.NotDispatched : scenario == 10 || !same ? NetworkOperationResolution.Unresolved
                : scenario is 9 or 11 ? NetworkOperationResolution.ObservedPresent : NetworkOperationResolution.ObservedAbsent;
            networkOperation = current = current with { Phase = NetworkOperationPhase.Completed, Resolution = resolution,
                ObservedExists = resolution == NetworkOperationResolution.ObservedAbsent ? false : resolution == NetworkOperationResolution.ObservedPresent ? true : null,
                ObservedConnectionGeneration = same ? networkSnapshot.ConnectionGeneration : null,
                RefreshedInspection = scenario == 9 ? FixtureNetworkInspection(request.Target.Id, 9) : null,
                AttachmentObservationState = scenario == 9 ? NetworkAttachmentObservationState.Complete : NetworkAttachmentObservationState.NotRequested };
            if (resolution == NetworkOperationResolution.ObservedAbsent) networkSnapshot = networkSnapshot with { Networks = networkSnapshot.Networks.Where(row => row.Id != request.Target.Id).ToArray() };
        }
        NetworkOperationChanged?.Invoke(this, EventArgs.Empty); NetworksChanged?.Invoke(this, EventArgs.Empty); return current;
    }
}
