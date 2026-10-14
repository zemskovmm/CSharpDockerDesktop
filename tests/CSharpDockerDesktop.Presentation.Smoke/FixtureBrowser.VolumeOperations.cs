using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal sealed partial class FixtureBrowser : IVolumeDeletionPreparation, IVolumeOperations
{
    public int VolumeDeletionScenario, VolumePrepares, VolumeCommands, VolumePreparationCloses;
    public VolumeOperationRequest? LastVolumeRequest;
    private VolumeDeletionPreparationSnapshot? volumePreparation;
    private VolumeOperationSnapshot? volumeOperation;
    private Task<VolumeDeletionPreparationSnapshot> volumePreparationWork = null!;
    public VolumeDeletionPreparationSnapshot? VolumeDeletionPreparation { get { lock (volumeGate) return volumePreparation; } }
    public VolumeOperationSnapshot? VolumeOperation { get { lock (volumeGate) return volumeOperation; } }
    public event EventHandler? VolumeDeletionPreparationChanged;
    public event EventHandler? VolumeOperationChanged;
    public bool IsVolumeDeletionPreparationCurrent(VolumePreparationIdentity identity)
    { lock (volumeGate) return volumePreparation is { HasUsableEvidence: true } current && current.Identity == identity && current.Target.ConnectionGeneration == volumeSnapshot.ConnectionGeneration; }
    public Task<VolumeDeletionPreparationSnapshot> PrepareVolumeDeletionAsync(VolumeDeletionTarget target, CancellationToken cancellationToken = default)
    {
        Task<VolumeDeletionPreparationSnapshot> work;
        lock (volumeGate)
        {
            var snapshot = new VolumeDeletionPreparationSnapshot(new(++VolumePrepares, 1), target, VolumePreparationPhase.Loading);
            volumePreparation = snapshot;
            work = volumePreparationWork = RunVolumePreparation(snapshot, VolumeDeletionScenario, cancellationToken);
        }
        VolumeDeletionPreparationChanged?.Invoke(this, EventArgs.Empty); return work;
    }
    private async Task<VolumeDeletionPreparationSnapshot> RunVolumePreparation(VolumeDeletionPreparationSnapshot initial, int scenario, CancellationToken token)
    {
        using var registration = token.Register(() => InvalidateVolumePreparation(VolumePreparationFailureKind.Canceled, initial.Identity.PreparationId));
        // Slow case deliberately joins a non-cooperative acquisition; navigation remains available.
        await Task.Delay(scenario is 5 or 14 ? 8000 : 1000).ConfigureAwait(false);
        VolumeDeletionPreparationSnapshot result;
        lock (volumeGate)
        {
            if (volumePreparation?.Identity.PreparationId != initial.Identity.PreparationId) return initial with { Phase = VolumePreparationPhase.Invalidated, Failure = new(VolumePreparationFailureKind.Superseded) };
            if (volumePreparation.Phase != VolumePreparationPhase.Loading) return volumePreparation;
            volumePreparation = volumePreparation with { Identity = initial.Identity with { Revision = 2 }, Phase = scenario == 4 ? VolumePreparationPhase.Failed : VolumePreparationPhase.Completed,
                Dependencies = scenario == 4 ? null : FixtureVolumeDependencies(initial.Target.Name, scenario),
                Failure = scenario == 4 ? new(VolumePreparationFailureKind.AcquisitionFailed, new(EngineResourceFailureKind.AccessDenied, "Synthetic access denied", 403, "  PRIVATE original precheck failure\n  ")) : null };
            result = volumePreparation;
        }
        VolumeDeletionPreparationChanged?.Invoke(this, EventArgs.Empty);
        if (scenario is 7 or 8 or 13) _ = ChangeAfterVolumePreparation(initial.Identity.PreparationId, scenario);
        return result;
    }
    private async Task ChangeAfterVolumePreparation(long preparationId, int scenario)
    {
        await Task.Delay(2500).ConfigureAwait(false);
        lock (volumeGate) if (volumePreparation?.Identity.PreparationId != preparationId) return;
        if (scenario == 8) DisconnectVolumes();
        else if (scenario == 13)
        {
            lock (volumeGate) volumeSnapshot = volumeSnapshot with { Volumes = volumeSnapshot.Volumes.Where(row => row.Name != volumePreparation!.Target.Name).ToArray() };
            InvalidateVolumePreparation(VolumePreparationFailureKind.TargetAbsent); VolumesChanged?.Invoke(this, EventArgs.Empty);
        }
        else InvalidateVolumePreparation(VolumePreparationFailureKind.ObservedChange);
    }
    public void InvalidateVolumePreparation(VolumePreparationFailureKind kind, long? preparationId = null)
    {
        lock (volumeGate)
        {
            if (volumePreparation is not { } current || preparationId is { } id && current.Identity.PreparationId != id
                || current.Phase is not (VolumePreparationPhase.Loading or VolumePreparationPhase.Completed)) return;
            volumePreparation = current with { Identity = current.Identity with { Revision = current.Identity.Revision + 1 }, Phase = VolumePreparationPhase.Invalidated, Failure = new(kind) };
        }
        VolumeDeletionPreparationChanged?.Invoke(this, EventArgs.Empty);
    }
    public Task CloseVolumeDeletionPreparationAsync(VolumePreparationIdentity identity)
    {
        lock (volumeGate)
        {
            if (volumePreparation?.Identity.PreparationId != identity.PreparationId) return Task.CompletedTask;
            ++VolumePreparationCloses;
            InvalidateVolumePreparation(VolumePreparationFailureKind.Closed, identity.PreparationId);
            return volumePreparationWork;
        }
    }
    private static VolumeDependencies FixtureVolumeDependencies(string name, int scenario) => scenario switch
    {
        0 => new(name, [], []),
        3 => new(name, null, ["unverified-candidate-full-id"]),
        6 => new(name, Enumerable.Range(0, 20000).Select(i => new VolumeContainerDependency(i.ToString("x64"), ["/container-" + i, "/alias-" + i], "container-" + i, i % 2 == 0 ? "running" : "exited", i % 2 == 0)), Enumerable.Range(0, 10000).Select(i => "unverified-" + i)),
        _ => new(name, [new(new string('a', 64), ["/running-container", "/running-alias"], "running-container", "running", true),
            new(new string('b', 64), ["/stopped-container"], "stopped-container", "exited", false)], scenario is 2 or 9 ? [new string('c', 64)] : [])
    };
    public async Task<VolumeOperationSnapshot> ExecuteVolumeOperationAsync(VolumeOperationRequest request, CancellationToken cancellationToken = default)
    {
        VolumeOperationSnapshot current;
        int scenario;
        lock (volumeGate)
        {
            if (!IsVolumeDeletionPreparationCurrent(request.Preparation) || volumeOperation?.Pending == true || volumePreparation!.Target != request.Target)
                return new(0, request, VolumeOperationPhase.Completed, Resolution: VolumeOperationResolution.NotDispatched, Rejection: VolumeOperationRejection.PreparationNotCurrent);
            scenario = VolumeDeletionScenario; LastVolumeRequest = request;
            current = new(++VolumeCommands, request, VolumeOperationPhase.CommandPending, Preparation: volumePreparation);
            volumeOperation = current;
            volumePreparation = volumePreparation with { Phase = VolumePreparationPhase.Consumed, Identity = volumePreparation.Identity with { Revision = volumePreparation.Identity.Revision + 1 } };
        }
        VolumeDeletionPreparationChanged?.Invoke(this, EventArgs.Empty); VolumeOperationChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(1500).ConfigureAwait(false);
        var command = scenario == 9 ? new VolumeCommandResult(VolumeCommandDisposition.DaemonResponse, 409, "  PRIVATE fixture dependency conflict\n  ", "{\"message\":\"PRIVATE conflict\"}", new(VolumeCommandFailureKind.Conflict, "Synthetic dependency conflict"))
            : scenario == 10 ? new VolumeCommandResult(VolumeCommandDisposition.Uncertain, DaemonMessage: "PRIVATE lost response", Failure: new(VolumeCommandFailureKind.TransportFailure, "Synthetic response loss"))
            : scenario == 12 ? new VolumeCommandResult(VolumeCommandDisposition.NotDispatched, Failure: new(VolumeCommandFailureKind.Canceled, "Synthetic cancellation"))
            : new VolumeCommandResult(VolumeCommandDisposition.Acknowledged, 204);
        lock (volumeGate) volumeOperation = current = current with { Phase = VolumeOperationPhase.Reconciling, CommandOutcome = command };
        VolumeOperationChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(1800).ConfigureAwait(false);
        lock (volumeGate)
        {
            bool same = request.Target.ConnectionGeneration == volumeSnapshot.ConnectionGeneration;
            var resolution = scenario == 12 ? VolumeOperationResolution.NotDispatched : scenario == 10 || !same ? VolumeOperationResolution.Unresolved
                : scenario is 9 or 11 ? VolumeOperationResolution.ObservedPresent : VolumeOperationResolution.ObservedAbsent;
            volumeOperation = current = current with { Phase = VolumeOperationPhase.Completed, Resolution = resolution,
                ObservedExists = resolution == VolumeOperationResolution.ObservedAbsent ? false : resolution == VolumeOperationResolution.ObservedPresent ? true : null,
                ObservedConnectionGeneration = same ? volumeSnapshot.ConnectionGeneration : null,
                RefreshedDependencies = scenario == 9 ? FixtureVolumeDependencies(request.Target.Name, 9) : null,
                DependencyObservationState = scenario == 9 ? VolumeDependencyObservationState.Incomplete : VolumeDependencyObservationState.NotRequested };
            if (resolution == VolumeOperationResolution.ObservedAbsent) volumeSnapshot = volumeSnapshot with { Volumes = volumeSnapshot.Volumes.Where(row => row.Name != request.Target.Name).ToArray() };
        }
        VolumeOperationChanged?.Invoke(this, EventArgs.Empty); VolumesChanged?.Invoke(this, EventArgs.Empty); return current;
    }
}
