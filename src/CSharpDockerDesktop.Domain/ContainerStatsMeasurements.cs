namespace CSharpDockerDesktop.Domain;

public enum ContainerStatsAvailability
{
    Available, MissingEvidence, UnsupportedPlatform, BaselineRequired, IncompatibleBaseline,
    InvalidTimestamp, CounterReset, CpuCountChanged, TopologyChanged, AmbiguousCounters,
    InvalidAdjustment, InvalidDenominator
}

public enum ContainerStatsMeasurementSource
{
    None, LinuxCpuOnlineCount, LinuxCpuPerCpuCount, WindowsCpu100Nanoseconds,
    LinuxTotalInactiveFile, LinuxInactiveFile, LinuxHistoricalCache, LinuxRawAdjustmentRejected,
    WindowsPrivateWorkingSet, WindowsCommit, LinuxMemoryLimit, NetworkCounters, LinuxBlockIo, WindowsStorage
}

public enum ContainerStatsCpuBaseline { None, PreviousSample, EmbeddedPreviousCpu }

/// <summary>Unavailable values are null, never a manufactured zero. Source identifies the formula/evidence.</summary>
public sealed record ContainerStatsMeasurement<T>(T? Value, ContainerStatsAvailability Availability,
    ContainerStatsMeasurementSource Source) where T : struct;

/// <summary>Immutable mathematical evidence only; does not establish lifecycle state or freshness.
/// Memory bytes retain integer precision. Block I/O is activity, never filesystem space.</summary>
public sealed record ContainerStatsMeasurements(string ContainerId, string? OSType, ContainerStatsTimestamp? Read,
    ContainerStatsMeasurement<double> CpuPercent, ContainerStatsCpuBaseline CpuBaseline,
    ContainerStatsMeasurement<ulong> MemoryUsageBytes, ContainerStatsMeasurement<double> MemoryPercent,
    ContainerStatsMeasurement<ulong> MemoryCommitBytes,
    ContainerStatsMeasurement<double> NetworkReceivedBytesPerSecond, ContainerStatsMeasurement<double> NetworkSentBytesPerSecond,
    ContainerStatsMeasurement<double> BlockIoReadBytesPerSecond, ContainerStatsMeasurement<double> BlockIoWriteBytesPerSecond);
