using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>One explicitly active selected-container view; all returned data is immutable.
/// Notifications are coalesced and may run off the UI thread. Always read the latest snapshot.</summary>
public interface IContainerStats
{
    event EventHandler? ContainerStatsChanged;
    ContainerStatsSnapshot ContainerStats { get; }
    bool OpenContainerStats(string containerId, out ContainerStatsFailure? admissionFailure);
    ContainerStatsHistory ReadContainerStats(long afterEntryId = 0, int maximumEntries = 300);
    Task CloseContainerStatsAsync();
}

public enum ContainerStatsPhase { Closed, AwaitingLifecycle, Opening, Streaming, Stalled, Inactive, Recovering, Completed, Failed }
public enum ContainerStatsBoundary { None, Opened, LifecycleInvalidated, ConnectionInterrupted, SampleGap, StreamEnded }

/// <summary>Local observed time controls retention; Measurements.Read preserves daemon timestamp evidence.
/// Null Measurements marks a gap, never a zero or interpolated activity.</summary>
public sealed record ContainerStatsHistoryEntry(long EntryId, long Continuity, DateTimeOffset ObservedAt,
    ContainerStatsMeasurements? Measurements, ContainerStatsBoundary Boundary);
public sealed record ContainerStatsHistory(long SessionId, long Revision, string? ContainerId,
    long ConnectionGeneration, IReadOnlyList<ContainerStatsHistoryEntry> Entries);
public sealed record ContainerStatsSnapshot(long SessionId, long Revision, ContainerStatsPhase Phase,
    string? ContainerId = null, long ConnectionGeneration = 0, EngineEndpoint? Endpoint = null, string? EngineId = null,
    long Continuity = 0, ContainerStatsMeasurements? Current = null, DateTimeOffset? LastObservedAt = null,
    int RetainedEntries = 0, long EvictedEntries = 0, long ProcessedSamples = 0,
    ContainerStatsFailure? Failure = null, ContainerStatsCompletion? Completion = null);

/// <summary>Separate budgets for retained display evidence, read copies and acquisition (owned by the source).
/// TimeProvider supplies monotonic cadence/retention and can be controlled by a deterministic harness.</summary>
public sealed record ContainerStatsOptions
{
    public TimeSpan PresentationInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Retention { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumEntries { get; init; } = 600;
    public int MaximumReadEntries { get; init; } = 300;
    public int IngestionBatchSize { get; init; } = 128;
    public int MaximumRecoveryAttempts { get; init; } = 3;
    internal void Validate()
    {
        if (PresentationInterval <= TimeSpan.Zero || PresentationInterval.TotalMilliseconds > int.MaxValue
            || Retention <= TimeSpan.Zero || Retention.TotalMilliseconds > int.MaxValue
            || StaleAfter < PresentationInterval || StaleAfter.TotalMilliseconds > int.MaxValue
            || MaximumEntries is < 1 or > 100_000 || MaximumReadEntries is < 1 or > 1024
            || IngestionBatchSize < 1 || MaximumRecoveryAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(ContainerStatsOptions));
    }
}
