using System.Numerics;
using CSharpDockerDesktop.Domain;
using A = CSharpDockerDesktop.Domain.ContainerStatsAvailability;
using S = CSharpDockerDesktop.Domain.ContainerStatsMeasurementSource;

namespace CSharpDockerDesktop.Application;

/// <summary>Single-consumer calculator retaining at most one immutable sample, with no history or transport ownership.
/// Call Reset at every stream/connection continuity boundary, even for the same container. Each call replaces the
/// baseline, including invalid samples, so missing evidence is never bridged. Not thread safe.</summary>
public sealed class ContainerStatsCalculator
{
    private ContainerStatsSample? previous;
    private bool allowEmbedded = true;

    /// <summary>Also suppresses embedded previous CPU counters on the next sample: they may cross the boundary.</summary>
    public void Reset() { previous = null; allowEmbedded = false; }

    public ContainerStatsMeasurements Calculate(ContainerStatsSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        A pair = previous is null ? A.BaselineRequired :
            string.IsNullOrEmpty(sample.ContainerId) || previous.ContainerId != sample.ContainerId ||
            previous.OSType != sample.OSType ? A.IncompatibleBaseline : A.Available;
        BigInteger elapsed = 0;
        if (pair == A.Available && !Elapsed(previous!.Read, sample.Read, out elapsed)) pair = A.InvalidTimestamp;

        var (cpu, origin) = Cpu(sample, pair, elapsed);
        var (memory, percent, commit) = Memory(sample);
        var rx = Network(sample, pair, elapsed, true);
        var tx = Network(sample, pair, elapsed, false);
        var read = Io(sample, pair, elapsed, true);
        var write = Io(sample, pair, elapsed, false);
        previous = sample;
        allowEmbedded = false;
        return new(sample.ContainerId, sample.OSType, sample.Read, cpu, origin, memory, percent, commit, rx, tx, read, write);
    }

    private (ContainerStatsMeasurement<double>, ContainerStatsCpuBaseline) Cpu(ContainerStatsSample sample, A pair, BigInteger elapsed)
    {
        if (sample.OSType is not ("linux" or "windows")) return (Missing<double>(A.UnsupportedPlatform), ContainerStatsCpuBaseline.None);
        var old = previous?.Cpu;
        var origin = ContainerStatsCpuBaseline.PreviousSample;
        uint? oldCount = sample.OSType == "windows" ? previous?.NumberOfProcessors : CpuCount(old).Count;
        if (previous is null && allowEmbedded && !string.IsNullOrEmpty(sample.ContainerId))
        {
            old = sample.PreviousCpu;
            if (old is null || sample.PreRead is null) return (Missing<double>(A.BaselineRequired), ContainerStatsCpuBaseline.None);
            oldCount = sample.OSType == "windows" ? sample.NumberOfProcessors : CpuCount(old).Count;
            pair = Elapsed(sample.PreRead, sample.Read, out elapsed) ? A.Available : A.InvalidTimestamp;
            origin = ContainerStatsCpuBaseline.EmbeddedPreviousCpu;
        }
        if (pair != A.Available) return (Missing<double>(pair), ContainerStatsCpuBaseline.None);
        var (count, source) = sample.OSType == "windows"
            ? (sample.NumberOfProcessors is > 0 ? sample.NumberOfProcessors : null, S.WindowsCpu100Nanoseconds) : CpuCount(sample.Cpu);
        if (count is null || oldCount is null || sample.Cpu?.TotalUsage is null || old?.TotalUsage is null)
            return (Missing<double>(A.MissingEvidence, source), origin);
        if (count != oldCount) return (Missing<double>(A.CpuCountChanged, source), origin);
        if (!Delta(old.TotalUsage, sample.Cpu.TotalUsage, out var used)) return (Missing<double>(A.CounterReset, source), origin);
        if (sample.OSType == "windows")
        {
            // Docker CLI floors elapsed nanoseconds to 100ns before multiplying by NumProcs.
            BigInteger possible = elapsed / 100 * count.Value;
            return (possible > 0 ? Available((double)used / (double)possible * 100, source) : Missing<double>(A.InvalidDenominator, source), origin);
        }
        if (old.SystemUsage is null || sample.Cpu.SystemUsage is null) return (Missing<double>(A.MissingEvidence, source), origin);
        if (!Delta(old.SystemUsage, sample.Cpu.SystemUsage, out var system)) return (Missing<double>(A.CounterReset, source), origin);
        return (system > 0 ? Available((double)used / (double)system * count.Value * 100, source) : Missing<double>(A.InvalidDenominator, source), origin);
    }

    private static (uint? Count, S Source) CpuCount(ContainerCpuCounters? cpu)
    {
        if (cpu?.OnlineCpus is > 0) return (cpu.OnlineCpus, S.LinuxCpuOnlineCount);
        // Only the array length is CPU-count evidence; individual per-core totals are not used by the formula.
        if (cpu?.PerCpuUsage is { IsDefault: false, Length: > 0 } perCpu) return ((uint)perCpu.Length, S.LinuxCpuPerCpuCount);
        return (null, S.LinuxCpuOnlineCount);
    }

    private static (ContainerStatsMeasurement<ulong>, ContainerStatsMeasurement<double>, ContainerStatsMeasurement<ulong>) Memory(ContainerStatsSample sample)
    {
        var mem = sample.Memory;
        if (sample.OSType == "windows") return (FromCounter(mem?.PrivateWorkingSet, S.WindowsPrivateWorkingSet),
            Missing<double>(A.UnsupportedPlatform), FromCounter(mem?.CommitBytes, S.WindowsCommit));
        var noCommit = Missing<ulong>(A.UnsupportedPlatform);
        if (sample.OSType != "linux") return (Missing<ulong>(A.UnsupportedPlatform), Missing<double>(A.UnsupportedPlatform), noCommit);
        if (mem?.Usage is not ulong usage || mem.Stats is null) return (Missing<ulong>(A.MissingEvidence), Missing<double>(A.MissingEvidence), noCommit);
        ContainerStatsMeasurement<ulong>? result = null;
        bool modern = false;
        foreach (var (key, source) in new[] { ("total_inactive_file", S.LinuxTotalInactiveFile), ("inactive_file", S.LinuxInactiveFile) })
        {
            if (!mem.Stats.TryGetValue(key, out var cache)) continue;
            modern = true;
            if (cache is null) { result = Missing<ulong>(A.MissingEvidence, source); break; }
            if (cache < usage) { result = Available(usage - cache.Value, source); break; }
        }
        if (result is null && modern) result = Available(usage, S.LinuxRawAdjustmentRejected);
        if (result is null && mem.Stats.TryGetValue("cache", out var historical))
            result = historical is null ? Missing<ulong>(A.MissingEvidence, S.LinuxHistoricalCache) :
                historical > usage ? Missing<ulong>(A.InvalidAdjustment, S.LinuxHistoricalCache) : Available(usage - historical.Value, S.LinuxHistoricalCache);
        result ??= Missing<ulong>(A.MissingEvidence);
        var percent = result.Value is not ulong used ? Missing<double>(result.Availability, result.Source) :
            mem.Limit is null ? Missing<double>(A.MissingEvidence, S.LinuxMemoryLimit) :
            mem.Limit == 0 ? Missing<double>(A.InvalidDenominator, S.LinuxMemoryLimit) :
            Available((double)used / mem.Limit.Value * 100, result.Source);
        return (result, percent, noCommit);
    }

    private ContainerStatsMeasurement<double> Network(ContainerStatsSample current, A pair, BigInteger elapsed, bool receive)
    {
        const S source = S.NetworkCounters;
        if (pair != A.Available) return Missing<double>(pair, source);
        var old = previous!.Networks; var now = current.Networks;
        if (old is null || now is null) return Missing<double>(A.MissingEvidence, source);
        if (!new HashSet<string>(old.Keys, StringComparer.Ordinal).SetEquals(now.Keys)) return Missing<double>(A.TopologyChanged, source);
        BigInteger sum = 0;
        foreach (var (key, value) in now)
        {
            var prior = old[key];
            if (value is null || prior is null) return Missing<double>(A.MissingEvidence, source);
            if (value.EndpointId != prior.EndpointId || value.InstanceId != prior.InstanceId) return Missing<double>(A.TopologyChanged, source);
            var before = receive ? prior.ReceivedBytes : prior.SentBytes; var after = receive ? value.ReceivedBytes : value.SentBytes;
            if (before is null || after is null) return Missing<double>(A.MissingEvidence, source);
            if (!Delta(before, after, out var delta)) return Missing<double>(A.CounterReset, source);
            sum += delta;
        }
        return Rate(sum, elapsed, source);
    }

    private ContainerStatsMeasurement<double> Io(ContainerStatsSample current, A pair, BigInteger elapsed, bool read)
    {
        var source = current.OSType == "windows" ? S.WindowsStorage : S.LinuxBlockIo;
        if (current.OSType is not ("linux" or "windows")) return Missing<double>(A.UnsupportedPlatform);
        if (pair != A.Available) return Missing<double>(pair, source);
        if (current.OSType == "windows")
        {
            var before = read ? previous!.Storage?.ReadBytes : previous!.Storage?.WriteBytes;
            var after = read ? current.Storage?.ReadBytes : current.Storage?.WriteBytes;
            if (before is null || after is null) return Missing<double>(A.MissingEvidence, source);
            return Delta(before, after, out var delta) ? Rate(delta, elapsed, source) : Missing<double>(A.CounterReset, source);
        }
        var old = IoCounters(previous!, read); var now = IoCounters(current, read);
        if (old.Error != A.Available || now.Error != A.Available) return Missing<double>(now.Error != A.Available ? now.Error : old.Error, source);
        if (!old.Counters.Keys.ToHashSet().SetEquals(now.Counters.Keys)) return Missing<double>(A.TopologyChanged, source);
        BigInteger sum = 0;
        foreach (var (device, value) in now.Counters)
        {
            if (!Delta(old.Counters[device], value, out var delta)) return Missing<double>(A.CounterReset, source);
            sum += delta;
        }
        return Rate(sum, elapsed, source);
    }

    private static (Dictionary<(ulong, ulong), ulong> Counters, A Error) IoCounters(ContainerStatsSample sample, bool read)
    {
        var result = new Dictionary<(ulong, ulong), ulong>();
        if (sample.BlockIo is not { IsDefault: false } entries) return (result, A.MissingEvidence);
        if (entries.Length == 0) return (result, A.Available);
        foreach (var entry in entries)
        {
            if (entry is null) return (result, A.AmbiguousCounters);
            var op = entry.Operation?.ToLowerInvariant();
            if (op is "total" or "sync" or "async") continue; // summaries overlap Read/Write; never add twice
            if (op is not ("read" or "write")) return (result, A.AmbiguousCounters);
            if ((op == "read") != read) continue;
            if (entry.Major is null || entry.Minor is null || entry.Value is null) return (result, A.MissingEvidence);
            if (!result.TryAdd((entry.Major.Value, entry.Minor.Value), entry.Value.Value)) return (result, A.AmbiguousCounters);
        }
        return (result, result.Count == 0 ? A.MissingEvidence : A.Available);
    }

    private static bool ValidTime(ContainerStatsTimestamp? t) => t is { UnixSeconds: > -62135596800 and <= 253402300799, Nanoseconds: >= 0 and < 1_000_000_000 };
    private static bool Elapsed(ContainerStatsTimestamp? before, ContainerStatsTimestamp? after, out BigInteger nanoseconds)
    {
        nanoseconds = 0;
        if (!ValidTime(before) || !ValidTime(after)) return false;
        nanoseconds = ((BigInteger)after!.UnixSeconds - before!.UnixSeconds) * 1_000_000_000 + after.Nanoseconds - before.Nanoseconds;
        return nanoseconds > 0;
    }
    private static bool Delta(ulong? before, ulong? after, out BigInteger delta)
    {
        delta = 0;
        if (before is null || after is null || after < before) return false;
        delta = (BigInteger)after.Value - before.Value; return true;
    }
    private static ContainerStatsMeasurement<double> Rate(BigInteger delta, BigInteger elapsed, S source)
        => Available((double)delta / (double)elapsed * 1_000_000_000, source);
    private static ContainerStatsMeasurement<ulong> FromCounter(ulong? value, S source)
        => value is ulong n ? Available(n, source) : Missing<ulong>(A.MissingEvidence, source);
    private static ContainerStatsMeasurement<double> Available(double value, S source)
        => double.IsFinite(value) && value >= 0 ? new(value, A.Available, source) : Missing<double>(A.InvalidDenominator, source);
    private static ContainerStatsMeasurement<T> Available<T>(T value, S source) where T : struct => new(value, A.Available, source);
    private static ContainerStatsMeasurement<T> Missing<T>(A reason, S source = S.None) where T : struct => new(null, reason, source);
}
