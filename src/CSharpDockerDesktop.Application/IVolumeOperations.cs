using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record VolumeOperationRequest(VolumeDeletionTarget Target, VolumePreparationIdentity Preparation);
public enum VolumeOperationPhase { CommandPending, Reconciling, Completed }
public enum VolumeOperationResolution { NotDispatched, ObservedAbsent, ObservedPresent, Unresolved }
public enum VolumeOperationRejection { Canceled, StaleConnection, InvalidRequest, CapabilityUnavailable, CapacityExceeded, PreparationNotCurrent }
public enum VolumeOperationInterruption { CallerCanceled, CommandDeadline, ConnectionChanged }
public enum VolumeDependencyObservationState { NotRequested, Complete, Incomplete, Unavailable, Failed, Invalidated }

/// <summary>Retained immutable private evidence. Name-based observations describe current presence, never
/// command causation or identity continuity across same-name recreation. No content is automatically logged.</summary>
public sealed record VolumeOperationSnapshot(long OperationId, VolumeOperationRequest Request,
    VolumeOperationPhase Phase, VolumeDeletionPreparationSnapshot? Preparation = null,
    VolumeCommandResult? CommandOutcome = null, VolumeOperationResolution? Resolution = null,
    VolumeOperationRejection? Rejection = null, VolumeOperationInterruption? Interruption = null,
    long? ObservedConnectionGeneration = null, bool? ObservedExists = null,
    VolumeListing? ObservedListing = null, EngineResourceFailure? ObservationListFailure = null,
    EngineResourceFailure? ObservationFailure = null, int Observations = 0, bool ObservationInvalidated = false,
    VolumeDependencies? RefreshedDependencies = null,
    VolumeDependencyObservationState DependencyObservationState = VolumeDependencyObservationState.NotRequested,
    EngineResourceFailure? DependencyFailure = null, int DependencyRefreshAttempts = 0)
{
    /// <summary>Captured at admission; retained evidence always belongs to this endpoint and Engine.</summary>
    public EngineEndpoint? Endpoint { get; init; }
    public string? EngineId { get; init; }
    public bool Pending => Phase != VolumeOperationPhase.Completed;
}

public interface IVolumeOperations
{
    VolumeOperationSnapshot? VolumeOperation { get; }
    /// <summary>May run off-thread. Read the latest immutable snapshot; observer exceptions are isolated.</summary>
    event EventHandler? VolumeOperationChanged;
    /// <summary>Caller owns deliberate confirmation, dependency display (including incomplete evidence), and
    /// the permanent-data-loss warning. Admission atomically consumes the exact completed preparation.
    /// One pending operation, no queue; replay of a retained admitted request shares completion, including
    /// after completion. Superseded/consumed preparations cannot admit another command. Uses the original
    /// ordinal name and non-force deletion only. Caller cancellation controls the command, while possible
    /// dispatch is reconciled under session ownership. Recovery observes the same known Engine only.</summary>
    Task<VolumeOperationSnapshot> ExecuteVolumeOperationAsync(VolumeOperationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record VolumeOperationOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan FollowupInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaximumObservations { get; init; } = 4;
    internal void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout.TotalMilliseconds > int.MaxValue
            || FollowupInterval <= TimeSpan.Zero || FollowupInterval.TotalMilliseconds > int.MaxValue
            || MaximumObservations < 1 || MaximumObservations > 100)
            throw new ArgumentOutOfRangeException(nameof(VolumeOperationOptions));
    }
}
