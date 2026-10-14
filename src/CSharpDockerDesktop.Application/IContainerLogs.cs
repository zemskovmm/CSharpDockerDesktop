using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public enum ContainerLogsPhase { Closed, Opening, Streaming, Recovering, Completed, Failed, Canceled }
/// <summary>BoundedOverlap means matching replay evidence, never a guarantee of gap-free daemon history.
/// Uncertain preserves ambiguous replay; finite tails, partial lines and missing evidence can leave duplicates or gaps.</summary>
public enum ContainerLogsContinuity { Initial, BoundedOverlap, Uncertain }

/// <summary>Immutable metadata only. Log text is deliberate caller data, never diagnostic content.</summary>
public sealed record ContainerLogsSnapshot(long SessionId, long ViewRevision, ContainerLogsPhase Phase,
    string? ContainerId = null, long ConnectionGeneration = 0, EngineEndpoint? Endpoint = null,
    string? EngineId = null, bool? IsTty = null, int RetainedEntries = 0, int RetainedCharacters = 0,
    long EvictedEntries = 0, long DiscardedCharacters = 0, ContainerLogsContinuity Continuity = ContainerLogsContinuity.Initial,
    ContainerLogCursor? LastObservedCursor = null, int RecoveryAttempt = 0,
    ContainerLogCompletion? Completion = null, ContainerLogFailure? Failure = null);

/// <summary>Immutable bounded segment of a logical line. Text includes original CR/LF and timestamp prefixes.
/// Segments are ordered by first observation; interleaved streams retain independent line assembly.
/// Continued marks a line split for the entry budget. Partial marks an unfinished line or interruption.</summary>
public sealed record ContainerLogEntry(long EntryId, long LineId, ContainerLogStream Stream, string Text,
    ContainerLogTimestamp? Timestamp, bool Continued, bool Partial);
public sealed record ContainerLogsPage(long SessionId, long ViewRevision, IReadOnlyList<ContainerLogEntry> Entries);

/// <summary>One explicitly requested, connection-scoped session. Changed is coalesced off the UI thread;
/// read current metadata and a bounded immutable page, then marshal to the UI. Reject pages whose session
/// or revision is obsolete. Pages are caller-owned snapshots; do not accumulate them without bound.
/// Opening returns admission immediately; observe Opening/Streaming/terminal state asynchronously.
/// Close joins all opening, consumer, producer and disposal work. Clear is a local publication barrier.</summary>
public interface IContainerLogs
{
    event EventHandler? ContainerLogsChanged;
    ContainerLogsSnapshot ContainerLogs { get; }
    bool OpenContainerLogs(string containerId, bool follow = true, int? tailLines = null);
    bool OpenContainerLogs(string containerId, out ContainerLogFailure? admissionFailure, bool follow = true, int? tailLines = null);
    ContainerLogsPage ReadContainerLogs(long afterEntryId = 0, int maximumEntries = 128);
    void ClearContainerLogs();
    Task CloseContainerLogsAsync();
}

/// <summary>Validated implementation tuning, independent of product performance promises.</summary>
public sealed record ContainerLogsOptions
{
    public int InitialTailLines { get; init; } = 300;
    public int MaximumTailLines { get; init; } = 10_000;
    public int MaximumEntries { get; init; } = 4096;
    public int MaximumCharacters { get; init; } = 2 * 1024 * 1024;
    public int MaximumEntryCharacters { get; init; } = 4096;
    public int MaximumPageEntries { get; init; } = 256;
    public int MaximumReplayEntries { get; init; } = 512;
    public int MaximumReplayCharacters { get; init; } = 256 * 1024;
    public int IngestionBatchSize { get; init; } = 128;
    public TimeSpan NotificationInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ReplayOverlap { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumRecoveryAttempts { get; init; } = 5;
    internal void Validate()
    {
        if (MaximumTailLines is < 1 or > 1_000_000 || InitialTailLines < 0 || InitialTailLines > MaximumTailLines
            || MaximumEntries is < 3 or > 100_000 || MaximumCharacters is < 16 or > 64 * 1024 * 1024
            || MaximumEntryCharacters < 2 || MaximumEntryCharacters > MaximumCharacters / 3
            || MaximumPageEntries is < 1 or > 1024 || MaximumPageEntries > MaximumEntries
            || MaximumReplayEntries is < 1 or > 10_000 || MaximumReplayCharacters < MaximumEntryCharacters
            || MaximumReplayCharacters > 16 * 1024 * 1024 || IngestionBatchSize is < 1 or > 4096
            || MaximumRecoveryAttempts is < 0 or > 100 || !ValidDelay(NotificationInterval)
            || !ValidDelay(RetryDelay) || ReplayOverlap < TimeSpan.Zero || ReplayOverlap > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(ContainerLogsOptions));
    }
    private static bool ValidDelay(TimeSpan value) => value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue;
}
