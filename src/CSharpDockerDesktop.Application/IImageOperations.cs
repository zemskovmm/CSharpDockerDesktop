using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record ImageIdentity(long ConnectionGeneration, string Id);
public sealed record ImageOperationRequest(ImageIdentity Target);
public enum ImageOperationPhase { CommandPending, Reconciling, Completed }
public enum ImageOperationResolution { NotDispatched, ObservedAbsent, ObservedPresent, Unresolved }

/// <summary>Retained evidence for one non-force delete. Observations establish current state, never causation.
/// Daemon messages and removal records are sensitive and intended only for deliberate display.</summary>
public sealed record ImageOperationSnapshot(long OperationId, ImageOperationRequest Request,
    ImageOperationPhase Phase, ImageCommandResult? CommandOutcome = null,
    ImageOperationResolution? Resolution = null, long? ObservedConnectionGeneration = null,
    bool? ObservedExists = null, EngineResourceFailure? ObservationFailure = null, int Observations = 0)
{
    /// <summary>Captured at admission; retained evidence always belongs to this endpoint and Engine.</summary>
    public EngineEndpoint? Endpoint { get; init; }
    public string? EngineId { get; init; }
    public bool Pending => Phase != ImageOperationPhase.Completed;
}

public interface IImageOperations
{
    ImageOperationSnapshot? ImageOperation { get; }
    /// <summary>Notifications may originate off-thread; read the latest immutable snapshot.</summary>
    event EventHandler? ImageOperationChanged;
    /// <summary>One pending delete, no queue. Identical requests share completion. Caller cancellation
    /// controls command dispatch; possible dispatch is reconciled independently under session ownership.
    /// The caller owns confirmation; the session always uses the captured complete ID with non-force deletion.</summary>
    Task<ImageOperationSnapshot> ExecuteImageOperationAsync(ImageOperationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ImageOperationOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan FollowupInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaximumObservations { get; init; } = 4;
    internal void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout.TotalMilliseconds > int.MaxValue
            || FollowupInterval <= TimeSpan.Zero || FollowupInterval.TotalMilliseconds > int.MaxValue
            || MaximumObservations < 1 || MaximumObservations > 100)
            throw new ArgumentOutOfRangeException(nameof(ImageOperationOptions));
    }
}
