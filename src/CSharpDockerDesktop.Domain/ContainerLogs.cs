using System.Globalization;

namespace CSharpDockerDesktop.Domain;

/// <summary>Exact Unix cursor; no conversion through sub-nanosecond-losing DateTime values.</summary>
public readonly record struct ContainerLogCursor(long UnixSeconds, int Nanoseconds = 0)
{
    public bool IsValid => UnixSeconds >= 0 && Nanoseconds is >= 0 and <= 999_999_999;
    public override string ToString()
    {
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(ContainerLogCursor));
        return string.Create(CultureInfo.InvariantCulture, $"{UnixSeconds}.{Nanoseconds:D9}");
    }
}

/// <summary>Finite historical tail; zero requests only live output when Follow is true.</summary>
public sealed record ContainerLogRequest(string ContainerId, int TailLines, bool Follow = false,
    ContainerLogCursor? Since = null);
public enum ContainerLogStream { Stdout, Stderr, Combined }
public enum ContainerLogBoundary { MultiplexedFrame, CombinedLine }

/// <summary>Original timestamp prefix and exact Unix value; absent means unavailable, never guessed.</summary>
public sealed record ContainerLogTimestamp(string Original, long UnixSeconds, int Nanoseconds)
{
    public ContainerLogCursor? Cursor => UnixSeconds >= 0 ? new(UnixSeconds, Nanoseconds) : null;
}

/// <summary>Exact timestamped wire text, excluding multiplex headers. Empty boundary markers are legitimate.</summary>
/// <remarks>Sequence and BoundaryId are subscription-local, not daemon IDs. TTY boundaries are LF-delimited;
/// non-TTY boundaries are frames. A character split between frames is emitted when complete on its stream.
/// TimestampPrefixCharacters is nonzero only on the first fragment with a recognized prefix. No deduplication.</remarks>
public sealed record ContainerLogOutput(long Sequence, ContainerLogStream Stream, string Text,
    long BoundaryId, ContainerLogBoundary Boundary, bool StartsBoundary, bool EndsBoundary,
    ContainerLogTimestamp? Timestamp, int TimestampPrefixCharacters = 0);

public enum ContainerLogFailureKind
{
    InvalidRequest, SubscriptionLimit, OpeningDeadline, NotFound, AccessDenied,
    LoggingUnavailable, DaemonError, MalformedOutput, TransportLoss
}

/// <summary>Original bounded DaemonMessage is deliberate caller data; never automatically log it or output.</summary>
public sealed record ContainerLogFailure(ContainerLogFailureKind Kind, string Description,
    int? HttpStatusCode = null, string? DaemonMessage = null, bool DaemonMessageTruncated = false);
public enum ContainerLogTermination { CleanEnd, Canceled, Failed }
public sealed record ContainerLogCompletion(ContainerLogTermination Termination, ContainerLogFailure? Failure = null);
