using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record VolumeDeletionTarget(long ConnectionGeneration, string Name);
public readonly record struct VolumePreparationIdentity(long PreparationId, long Revision);
public enum VolumePreparationPhase { Loading, Completed, Invalidated, Failed }
public enum VolumePreparationFailureKind
{
    InvalidRequest, CapabilityUnavailable, StaleConnection, CapacityExceeded, Canceled, Deadline,
    AcquisitionFailed, Superseded, Closed, TargetAbsent, ObservedChange, ConnectionChanged
}

/// <summary>ResourceFailure deliberately retains private daemon diagnostics; never automatically log it.</summary>
public sealed record VolumePreparationFailure(VolumePreparationFailureKind Kind, EngineResourceFailure? ResourceFailure = null);

/// <summary>Immutable read evidence, never volume-existence proof or deletion authorization.</summary>
public sealed record VolumeDeletionPreparationSnapshot(VolumePreparationIdentity Identity, VolumeDeletionTarget Target,
    VolumePreparationPhase Phase, VolumeDependencies? Dependencies = null, VolumePreparationFailure? Failure = null)
{
    public bool HasUsableEvidence => Phase == VolumePreparationPhase.Completed
        && Dependencies is { Completeness: not VolumeDependencyCompleteness.Unavailable };
}

public interface IVolumeDeletionPreparation
{
    VolumeDeletionPreparationSnapshot? VolumeDeletionPreparation { get; }
    /// <summary>May run off-thread. Read the latest immutable snapshot; observer errors are isolated.</summary>
    event EventHandler? VolumeDeletionPreparationChanged;
    /// <summary>
    /// Explicit fresh acquisition, independent of selection. One active and one waiting replacement maximum.
    /// Rejection is returned without replacing retained state. Completion joins borrowed work even after invalidation.
    /// The whole-request deadline includes waiting for the superseded request. Cancellation is classified state.
    /// </summary>
    Task<VolumeDeletionPreparationSnapshot> PrepareVolumeDeletionAsync(VolumeDeletionTarget target,
        CancellationToken cancellationToken = default);
    /// <summary>Fence the captured preparation ID and join its work. Revision changes within that request do not prevent closing; a different ID cannot close it.</summary>
    Task CloseVolumeDeletionPreparationAsync(VolumePreparationIdentity identity);
    /// <summary>Checks identity/revision and current usability only. It cannot close the daemon precheck/delete race.</summary>
    bool IsVolumeDeletionPreparationCurrent(VolumePreparationIdentity identity);
}
