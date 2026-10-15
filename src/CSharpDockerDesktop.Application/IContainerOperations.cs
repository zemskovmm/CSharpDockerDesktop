using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Full Engine identity, scoped to the browsing snapshot's connection generation.</summary>
public sealed record ContainerIdentity(long ConnectionGeneration, string Id);
public sealed record ContainerOperationRequest(ContainerIdentity Target, ContainerCommand Command,
    bool ForceDelete = false, int? GracePeriodSeconds = null);
public enum ContainerOperationPhase { CommandPending, Reconciling, Completed }
public enum ContainerOperationResolution { NotDispatched, ObservedRequestedState, ObservedDifferentState, Unresolved }

/// <summary>One retained operation. Observations describe the Engine, not causation or HTTP success.
/// Original daemon content in CommandOutcome and ObservationFailure is for deliberate display only.</summary>
public sealed record ContainerOperationSnapshot(long OperationId, ContainerOperationRequest Request,
    ContainerOperationPhase Phase, ContainerCommandResult? CommandOutcome = null,
    ContainerOperationResolution? Resolution = null, long? ObservedConnectionGeneration = null,
    bool? ObservedExists = null, ContainerRuntimeState? ObservedState = null,
    EngineResourceFailure? ObservationFailure = null, int Observations = 0)
{
    /// <summary>Captured at admission; retained evidence always belongs to this endpoint and Engine.</summary>
    public EngineEndpoint? Endpoint { get; init; }
    public string? EngineId { get; init; }
    public bool Pending => Phase != ContainerOperationPhase.Completed;
}

public interface IContainerOperations
{
    ContainerOperationSnapshot? ContainerOperation { get; }
    /// <summary>Read the immutable state; notifications may originate off the UI thread.</summary>
    event EventHandler? ContainerOperationChanged;
    /// <summary>One operation at a time, no queue. Identical pending requests share completion.
    /// Caller cancellation controls dispatch/command; possible dispatch is still reconciled under connection ownership.
    /// Delete is an explicit caller decision; default and forced deletion both preserve volumes.</summary>
    Task<ContainerOperationSnapshot> ExecuteContainerOperationAsync(ContainerOperationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounded coordination defaults, separate from protocol deadlines and product requirements.</summary>
public sealed record ContainerOperationOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan FollowupInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaximumObservations { get; init; } = 4;
    internal void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout.TotalMilliseconds > int.MaxValue
            || FollowupInterval <= TimeSpan.Zero || FollowupInterval.TotalMilliseconds > int.MaxValue
            || MaximumObservations < 1 || MaximumObservations > 100)
            throw new ArgumentOutOfRangeException(nameof(ContainerOperationOptions));
    }
}
