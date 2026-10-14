using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record NetworkDeletionTarget(long ConnectionGeneration, string Id);
public readonly record struct NetworkPreparationIdentity(long PreparationId, long Revision);
public enum NetworkPreparationPhase { Loading, Completed, Invalidated, Failed, Consumed }
public enum NetworkPreparationFailureKind
{
    InvalidRequest, CapabilityUnavailable, StaleConnection, CapacityExceeded, Canceled, Deadline,
    AcquisitionFailed, Superseded, Closed, TargetAbsent, ObservedChange, ConnectionChanged, ExplicitInvalidation
}
public enum NetworkDeletionEligibility { Unknown, Blocked, PrecheckEligible }
public enum NetworkDeletionBlockReason
{
    BuiltInNetwork, AttachmentsRemain, AttachmentsUnavailable, IdentityMismatch, NameUnavailable,
    NameContradiction, AttachmentEvidenceInvalid, EvidenceNotCurrent
}

/// <summary>Private daemon evidence; never automatically log or transmit it.</summary>
public sealed record NetworkPreparationFailure(NetworkPreparationFailureKind Kind, EngineResourceFailure? ResourceFailure = null);

/// <summary>
/// Immutable fresh Inspect evidence. PrecheckEligible never proves cluster-wide completeness or closes the
/// check/delete race. Inspection, names, attachment keys and endpoint metadata are private evidence.
/// </summary>
public sealed record NetworkDeletionPreparationSnapshot(NetworkPreparationIdentity Identity, NetworkDeletionTarget Target,
    NetworkPreparationPhase Phase, NetworkDeletionEligibility Eligibility,
    IReadOnlyList<NetworkDeletionBlockReason> Reasons, NetworkInspection? Inspection = null,
    NetworkPreparationFailure? Failure = null)
{
    public bool HasUsableEvidence => Phase == NetworkPreparationPhase.Completed && Inspection is not null;
    public bool IsPrecheckEligible => HasUsableEvidence && Eligibility == NetworkDeletionEligibility.PrecheckEligible;
}

public interface INetworkDeletionPreparation
{
    NetworkDeletionPreparationSnapshot? NetworkDeletionPreparation { get; }
    /// <summary>May run off-thread; read the latest snapshot. Observer exceptions are isolated.</summary>
    event EventHandler? NetworkDeletionPreparationChanged;
    /// <summary>
    /// Fresh read-only Inspect independent of selection. One active and one waiting replacement maximum.
    /// Admission rejection leaves retained state unchanged. Deadline includes superseded-work waiting.
    /// Completion joins even non-cooperative borrowed reads. No network command is dispatched.
    /// </summary>
    Task<NetworkDeletionPreparationSnapshot> PrepareNetworkDeletionAsync(NetworkDeletionTarget target,
        CancellationToken cancellationToken = default);
    /// <summary>Fence this preparation ID (any revision), cancel and join its owned work.</summary>
    Task CloseNetworkDeletionPreparationAsync(NetworkPreparationIdentity identity);
    /// <summary>Explicitly fence this exact identity/revision without requesting a replacement.</summary>
    void InvalidateNetworkDeletionPreparation(NetworkPreparationIdentity identity);
    /// <summary>Checks exact identity/revision and eligible current evidence; not deletion authorization.</summary>
    bool IsNetworkDeletionPreparationCurrent(NetworkPreparationIdentity identity);
}
