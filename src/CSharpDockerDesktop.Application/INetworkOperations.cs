using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record NetworkOperationRequest(NetworkDeletionTarget Target, NetworkPreparationIdentity Preparation);
public enum NetworkOperationPhase { CommandPending, Reconciling, Completed }
public enum NetworkOperationResolution { NotDispatched, ObservedAbsent, ObservedPresent, Unresolved }
public enum NetworkOperationRejection { Canceled, StaleConnection, InvalidRequest, CapabilityUnavailable, CapacityExceeded, PreparationNotCurrent }
public enum NetworkOperationInterruption { CallerCanceled, CommandDeadline, ConnectionChanged }
public enum NetworkAttachmentObservationState { NotRequested, Complete, Unavailable, Malformed, Failed, Invalidated }

/// <summary>Immutable retained private evidence. Observations describe current presence, never command causation.
/// Raw Inspect, attachments, metadata and daemon errors must not be automatically logged or transmitted.</summary>
public sealed record NetworkOperationSnapshot(long OperationId, NetworkOperationRequest Request,
    NetworkOperationPhase Phase, NetworkDeletionPreparationSnapshot? Preparation = null,
    NetworkCommandResult? CommandOutcome = null, NetworkOperationResolution? Resolution = null,
    NetworkOperationRejection? Rejection = null, NetworkOperationInterruption? Interruption = null,
    long? ObservedConnectionGeneration = null, bool? ObservedExists = null,
    IReadOnlyList<NetworkSummary>? ObservedListing = null, EngineResourceFailure? ObservationListFailure = null,
    EngineResourceFailure? ObservationFailure = null, int Observations = 0, bool ObservationInvalidated = false,
    NetworkInspection? RefreshedInspection = null,
    NetworkAttachmentObservationState AttachmentObservationState = NetworkAttachmentObservationState.NotRequested,
    EngineResourceFailure? AttachmentFailure = null, int AttachmentRefreshAttempts = 0)
{
    public bool Pending => Phase != NetworkOperationPhase.Completed;
}

public interface INetworkOperations
{
    NetworkOperationSnapshot? NetworkOperation { get; }
    /// <summary>May run off-thread. Read the latest immutable snapshot; observer exceptions are isolated.</summary>
    event EventHandler? NetworkOperationChanged;
    /// <summary>Caller owns deliberate confirmation and dependency display. Admission atomically consumes
    /// the exact current PrecheckEligible preparation. One pending operation, no queue; replay of the
    /// retained admitted request shares its completion. Rejected admission leaves retained evidence intact.
    /// Deletes the complete original ordinal ID once. Possible dispatch reconciles under session ownership
    /// even after caller cancellation or navigation. Recovery observes only the same known Engine/endpoint.</summary>
    Task<NetworkOperationSnapshot> ExecuteNetworkOperationAsync(NetworkOperationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record NetworkOperationOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan FollowupInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaximumObservations { get; init; } = 4;
    internal void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout.TotalMilliseconds > int.MaxValue
            || FollowupInterval <= TimeSpan.Zero || FollowupInterval.TotalMilliseconds > int.MaxValue
            || MaximumObservations < 1 || MaximumObservations > 100)
            throw new ArgumentOutOfRangeException(nameof(NetworkOperationOptions));
    }
}
