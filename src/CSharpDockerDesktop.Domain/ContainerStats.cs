using System.Collections.Immutable;

namespace CSharpDockerDesktop.Domain;

/// <summary>Exact RFC3339 nanosecond timestamp. A daemon's zero date is preserved, not a current measurement.</summary>
public sealed record ContainerStatsTimestamp(string Original, long UnixSeconds, int Nanoseconds);

/// <summary>Raw counters only. Null is unavailable; zero and observed-empty collections are distinct.
/// CPU units are nanoseconds on Linux and 100ns on Windows. No values are synthesized or aggregated.</summary>
public sealed record ContainerStatsSample(string ContainerId, string? OSType,
    ContainerStatsTimestamp? Read, ContainerStatsTimestamp? PreRead,
    ContainerCpuCounters? Cpu, ContainerCpuCounters? PreviousCpu, uint? NumberOfProcessors,
    ContainerMemoryCounters? Memory, ImmutableDictionary<string, ContainerNetworkCounters?>? Networks,
    ImmutableArray<ContainerBlockIoCounter>? BlockIo, ContainerStorageCounters? Storage);

public sealed record ContainerCpuCounters(ulong? TotalUsage, ulong? SystemUsage, uint? OnlineCpus,
    ImmutableArray<ulong?>? PerCpuUsage, ulong? UsageInKernelMode, ulong? UsageInUserMode);

/// <summary>Stats retains all bounded memory.stat keys, including cache, total_inactive_file and inactive_file.
/// Linux usage/limit and Windows commit/private working set remain separate evidence.</summary>
public sealed record ContainerMemoryCounters(ulong? Usage, ulong? Limit, ulong? MaximumUsage, ulong? FailCount,
    ImmutableDictionary<string, ulong?>? Stats, ulong? CommitBytes, ulong? CommitPeakBytes, ulong? PrivateWorkingSet);

public sealed record ContainerNetworkCounters(ulong? ReceivedBytes, ulong? SentBytes, ulong? ReceivedPackets,
    ulong? SentPackets, ulong? ReceivedErrors, ulong? SentErrors, ulong? ReceivedDropped, ulong? SentDropped,
    string? EndpointId, string? InstanceId);

/// <summary>Linux io_service_bytes_recursive entries. Operation case and per-device counters are preserved.
/// These are I/O activity, never filesystem disk-space usage.</summary>
public sealed record ContainerBlockIoCounter(ulong? Major, ulong? Minor, string? Operation, ulong? Value);
/// <summary>Windows storage I/O counters, not Linux Block I/O or filesystem disk-space usage.</summary>
public sealed record ContainerStorageCounters(ulong? ReadBytes, ulong? WriteBytes, ulong? ReadCountNormalized, ulong? WriteCountNormalized);

public enum ContainerStatsFailureKind
{
    InvalidRequest, SubscriptionLimit, OpeningDeadline, NotFound, AccessDenied,
    IncompatibleApi, DaemonError, MalformedSample, TransportLoss
}
/// <summary>Original bounded daemon content is explicit caller data; never automatically log it.</summary>
public sealed record ContainerStatsFailure(ContainerStatsFailureKind Kind, string Description,
    int? HttpStatusCode = null, string? DaemonMessage = null, bool DaemonMessageTruncated = false);
public enum ContainerStatsTermination { CleanEnd, Canceled, Failed }
public sealed record ContainerStatsCompletion(ContainerStatsTermination Termination, ContainerStatsFailure? Failure = null);
