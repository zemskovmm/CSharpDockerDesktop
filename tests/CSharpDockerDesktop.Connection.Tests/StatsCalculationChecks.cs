using System.Collections.Immutable;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using A = CSharpDockerDesktop.Domain.ContainerStatsAvailability;
using S = CSharpDockerDesktop.Domain.ContainerStatsMeasurementSource;

internal static class StatsCalculationChecks
{
    private static void Check(bool condition, string message = "Calculation assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Value(ContainerStatsMeasurement<double> value, double expected)
        => Check(value.Availability == A.Available && value.Value is double n && double.IsFinite(n) && n >= 0 &&
            Math.Abs(n - expected) <= Math.Max(1e-20, Math.Abs(expected) * 1e-12), $"Expected {expected}, got {value}");
    private static void Missing<T>(ContainerStatsMeasurement<T> value, A reason) where T : struct
        => Check(value.Value is null && value.Availability == reason, $"Expected {reason}, got {value}");
    private static ContainerStatsTimestamp Time(long seconds, int ns = 0) => new("synthetic exact timestamp", seconds, ns);
    private static ContainerCpuCounters Cpu(ulong? total = 100, ulong? system = 1000, uint? count = 4)
        => new(total, system, count, null, null, null);
    private static ContainerMemoryCounters Memory(ulong? usage = 1000, ulong? limit = 2000, params (string, ulong?)[] keys)
        => new(usage, limit, null, null, keys.ToImmutableDictionary(k => k.Item1, k => k.Item2), null, null, null);
    private static ContainerNetworkCounters Net(ulong? rx = 100, ulong? tx = 200, string? endpoint = null, string? instance = null)
        => new(rx, tx, null, null, null, null, null, null, endpoint, instance);
    private static ImmutableDictionary<string, ContainerNetworkCounters?> Networks(params (string, ContainerNetworkCounters?)[] entries)
        => entries.ToImmutableDictionary(x => x.Item1, x => x.Item2);
    private static ContainerBlockIoCounter Io(string? op, ulong? value, ulong? device = 1, ulong? minor = 0) => new(device, minor, op, value);
    private static ContainerStatsSample Sample(long second = 10, int ns = 0) => new("canonical", "linux", Time(second, ns), Time(second - 1, ns),
        Cpu(), Cpu(80, 800), null, Memory(1000, 2000, ("total_inactive_file", 200)), Networks(("eth0", Net())),
        [Io("Read", 100), Io("Write", 200)], null);
    private static ContainerStatsMeasurements Pair(ContainerStatsSample old, ContainerStatsSample now)
    { var calculator = new ContainerStatsCalculator(); calculator.Calculate(old); return calculator.Calculate(now); }

    internal static void AcquiredLinux(ContainerStatsSample first, ContainerStatsSample second, int minor)
    {
        var calculator = new ContainerStatsCalculator(); var result = calculator.Calculate(first);
        // Decimal arithmetic calculated independently: 121359 / 729814450000000 * 400.
        Value(result.CpuPercent, 0.000000066514988844082218432370035972842);
        Check(result.MemoryUsageBytes.Value == (minor < 42 ? 6_537_216UL : 4_026_368UL));
        Value(result.MemoryPercent, minor < 42 ? 9.7412109375 : 5.999755859375);
        Missing(result.NetworkReceivedBytesPerSecond, A.BaselineRequired);
        var next = calculator.Calculate(second);
        Value(next.NetworkReceivedBytesPerSecond, 0); Value(next.NetworkSentBytesPerSecond, 0);
        Value(next.BlockIoReadBytesPerSecond, 0); Value(next.BlockIoWriteBytesPerSecond, 0);
    }
    internal static void AcquiredWindows(ContainerStatsSample sample)
    {
        var result = new ContainerStatsCalculator().Calculate(sample);
        // 7,918,000 / (floor(1,004,791,042 / 100) * 16) * 100.
        Value(result.CpuPercent, 4.925153589154361454272580068889948);
        Check(result.MemoryUsageBytes.Value == 0 && result.MemoryCommitBytes.Value == ulong.MaxValue);
        Missing(result.MemoryPercent, A.UnsupportedPlatform);
    }

    internal static Task CpuAndExactTime()
    {
        var first = Sample();
        var initial = new ContainerStatsCalculator().Calculate(first);
        Value(initial.CpuPercent, 40); Check(initial.CpuBaseline == ContainerStatsCpuBaseline.EmbeddedPreviousCpu);
        var next = first with { Read = Time(10, 250_000_000), Cpu = Cpu(125, 1200), PreviousCpu = Cpu(0, 0) };
        // 25 / 200 * 4 * 100 = 50%; 100 bytes over 1/4 second = 400 bytes/s.
        var result = Pair(first, next with { Networks = Networks(("eth0", Net(200, 200))), BlockIo = [Io("Read", 110), Io("Write", 200)] });
        Value(result.CpuPercent, 50); Value(result.NetworkReceivedBytesPerSecond, 400); Value(result.NetworkSentBytesPerSecond, 0);
        Value(result.BlockIoReadBytesPerSecond, 40); Value(result.BlockIoWriteBytesPerSecond, 0);
        Check(result.CpuBaseline == ContainerStatsCpuBaseline.PreviousSample);
        Value(Pair(first, next with { Cpu = Cpu(100, 1200) }).CpuPercent, 0);
        Missing(Pair(first, next with { Cpu = Cpu(99, 1200) }).CpuPercent, A.CounterReset);
        Missing(Pair(first, next with { Cpu = Cpu(125, 999) }).CpuPercent, A.CounterReset);
        Missing(Pair(first, next with { Cpu = Cpu(125, 1000) }).CpuPercent, A.InvalidDenominator);
        Missing(Pair(first, next with { Cpu = Cpu(null, 1200) }).CpuPercent, A.MissingEvidence);
        Missing(Pair(first, next with { Cpu = Cpu(125, 1200, 8) }).CpuPercent, A.CpuCountChanged);
        var perCpu = Cpu() with { OnlineCpus = 0, PerCpuUsage = ImmutableArray.Create<ulong?>(null, 1) };
        Value(Pair(first with { Cpu = perCpu }, next with { Cpu = perCpu with { TotalUsage = 125, SystemUsage = 1200 } }).CpuPercent, 25);
        Missing(Pair(first, next with { Cpu = Cpu(125, 1200, null) }).CpuPercent, A.MissingEvidence);
        // Cross-second interval: 999,999,950ns to next-second 50ns = exactly 100ns.
        Value(Pair(first with { Read = Time(10, 999_999_950) }, next with { Read = Time(11, 50), Networks = Networks(("eth0", Net(101, 200))) }).NetworkReceivedBytesPerSecond, 10_000_000);
        foreach (var time in new ContainerStatsTimestamp?[] { null, Time(-62135596800), Time(10), Time(9), Time(10, -1), Time(10, 1_000_000_000) })
        {
            var invalid = Pair(first, next with { Read = time });
            Missing(invalid.CpuPercent, A.InvalidTimestamp); Missing(invalid.NetworkReceivedBytesPerSecond, A.InvalidTimestamp);
            Check(invalid.MemoryUsageBytes.Value == 800); // timestamp invalidity stays local to interval metrics
        }
        return Task.CompletedTask;
    }

    internal static Task MemoryAndWindows()
    {
        ContainerStatsMeasurements Calculate(ContainerMemoryCounters memory) => new ContainerStatsCalculator().Calculate(Sample() with { Memory = memory });
        var v1 = Calculate(Memory(1000, 2000, ("total_inactive_file", 200), ("inactive_file", 400), ("cache", 600)));
        Check(v1.MemoryUsageBytes.Value == 800 && v1.MemoryUsageBytes.Source == S.LinuxTotalInactiveFile); Value(v1.MemoryPercent, 40);
        var v2 = Calculate(Memory(1000, 2000, ("inactive_file", 400), ("cache", 600)));
        Check(v2.MemoryUsageBytes.Value == 600 && v2.MemoryUsageBytes.Source == S.LinuxInactiveFile); Value(v2.MemoryPercent, 30);
        var legacy = Calculate(Memory(1000, 2000, ("cache", 600)));
        Check(legacy.MemoryUsageBytes.Value == 400 && legacy.MemoryUsageBytes.Source == S.LinuxHistoricalCache); Value(legacy.MemoryPercent, 20);
        Check(Calculate(Memory(1000, 2000, ("cache", 1000))).MemoryUsageBytes.Value == 0);
        Missing(Calculate(Memory(1000, 2000, ("cache", 1001))).MemoryUsageBytes, A.InvalidAdjustment);
        var fallback = Calculate(Memory(1000, 2000, ("total_inactive_file", 1000), ("inactive_file", 100)));
        Check(fallback.MemoryUsageBytes.Value == 900 && fallback.MemoryUsageBytes.Source == S.LinuxInactiveFile);
        var rejected = Calculate(Memory(1000, 2000, ("total_inactive_file", 1001), ("cache", 500)));
        Check(rejected.MemoryUsageBytes.Value == 1000 && rejected.MemoryUsageBytes.Source == S.LinuxRawAdjustmentRejected);
        foreach (var memory in new[] { Memory(), Memory(1000, 2000, ("unrelated", 0)), Memory(1000, 2000, ("total_inactive_file", null), ("cache", 0)), Memory(null, 2000, ("cache", 0)), Memory() with { Stats = null } })
            Missing(Calculate(memory).MemoryUsageBytes, A.MissingEvidence);
        Missing(Calculate(Memory(1000, 0, ("inactive_file", 0))).MemoryPercent, A.InvalidDenominator);
        Missing(Calculate(Memory(1000, null, ("inactive_file", 0))).MemoryPercent, A.MissingEvidence);
        var zero = Calculate(Memory(0, 100, ("inactive_file", 0))); Check(zero.MemoryUsageBytes.Value == 0); Value(zero.MemoryPercent, 0);
        var windows = Sample() with { OSType = "windows", Read = Time(10, 250_000_099), PreRead = Time(10),
            NumberOfProcessors = 4, Cpu = Cpu(5_000_000, null, null), PreviousCpu = Cpu(0, null, null),
            Memory = Memory() with { PrivateWorkingSet = 0, CommitBytes = ulong.MaxValue }, Storage = new(10, 20, null, null) };
        // floor(250000099 / 100) * 4 = 10000000 possible intervals; 5000000 => 50%.
        var win = new ContainerStatsCalculator().Calculate(windows);
        Value(win.CpuPercent, 50); Check(win.CpuPercent.Source == S.WindowsCpu100Nanoseconds);
        Check(win.MemoryUsageBytes.Value == 0 && win.MemoryCommitBytes.Value == ulong.MaxValue); Missing(win.MemoryPercent, A.UnsupportedPlatform);
        var next = windows with { Read = Time(10, 500_000_099), Cpu = Cpu(7_500_000, null, null), Storage = new(35, 20, null, null) };
        var winNext = Pair(windows, next); Value(winNext.CpuPercent, 25); Value(winNext.BlockIoReadBytesPerSecond, 100); Value(winNext.BlockIoWriteBytesPerSecond, 0);
        Missing(Pair(windows, next with { NumberOfProcessors = 8 }).CpuPercent, A.CpuCountChanged);
        Missing(new ContainerStatsCalculator().Calculate(windows with { NumberOfProcessors = 0 }).CpuPercent, A.MissingEvidence);
        Missing(new ContainerStatsCalculator().Calculate(windows with { Read = Time(10, 99) }).CpuPercent, A.InvalidDenominator);
        Missing(new ContainerStatsCalculator().Calculate(windows with { Memory = Memory() with { CommitBytes = 5 } }).MemoryUsageBytes, A.MissingEvidence);
        return Task.CompletedTask;
    }

    internal static Task BaselinesAndBoundedState()
    {
        var first = Sample(); var calculator = new ContainerStatsCalculator();
        var noPrevious = calculator.Calculate(first with { PreviousCpu = null, PreRead = null });
        Missing(noPrevious.CpuPercent, A.BaselineRequired); Missing(noPrevious.NetworkReceivedBytesPerSecond, A.BaselineRequired);
        calculator.Reset(); var reset = calculator.Calculate(first);
        Missing(reset.CpuPercent, A.BaselineRequired); Missing(reset.BlockIoReadBytesPerSecond, A.BaselineRequired);
        Value(calculator.Calculate(first with { Read = Time(11), Cpu = Cpu(120, 1200) }).CpuPercent, 40);
        var changed = calculator.Calculate(first with { ContainerId = "different", Read = Time(12) });
        Missing(changed.CpuPercent, A.IncompatibleBaseline); Missing(changed.NetworkReceivedBytesPerSecond, A.IncompatibleBaseline);
        var unknown = new ContainerStatsCalculator().Calculate(first with { OSType = null });
        Missing(unknown.CpuPercent, A.UnsupportedPlatform); Missing(unknown.MemoryUsageBytes, A.UnsupportedPlatform);
        // Byte counters have common units even when neither sample reports a platform.
        Value(Pair(first with { OSType = null }, first with { OSType = null, Read = Time(11) }).NetworkSentBytesPerSecond, 0);
        Missing(Pair(first, first with { OSType = "windows", NumberOfProcessors = 4, Read = Time(11) }).CpuPercent, A.IncompatibleBaseline);
        var missing = first with { Read = Time(11), Networks = null, Cpu = null };
        calculator.Reset(); calculator.Calculate(first); calculator.Calculate(missing);
        var afterGap = calculator.Calculate(first with { Read = Time(12) });
        Missing(afterGap.NetworkReceivedBytesPerSecond, A.MissingEvidence); Missing(afterGap.CpuPercent, A.MissingEvidence);
        // Repeated calls replace one baseline; no historical list, clocks, or accumulating totals.
        calculator.Reset(); calculator.Calculate(first);
        for (int i = 1; i <= 30_000; i++)
        {
            var result = calculator.Calculate(first with { Read = Time(10 + i), Cpu = Cpu((ulong)(100 + i), (ulong)(1000 + i * 10)) });
            Value(result.CpuPercent, 40); Value(result.NetworkSentBytesPerSecond, 0);
        }
        return Task.CompletedTask;
    }

    internal static Task TopologyAndIntegerBoundaries()
    {
        var old = Sample(); var now = old with { Read = Time(11), Cpu = Cpu(100, 1200) };
        foreach (var map in new[] { Networks(), Networks(("other", Net())), Networks(("eth0", Net()), ("extra", Net())),
            Networks(("eth0", Net(endpoint: "new"))), Networks(("eth0", Net(instance: "new"))) })
            Missing(Pair(old, now with { Networks = map }).NetworkReceivedBytesPerSecond, A.TopologyChanged);
        var partial = Pair(old, now with { Networks = Networks(("eth0", Net(null, 250))) });
        Missing(partial.NetworkReceivedBytesPerSecond, A.MissingEvidence); Value(partial.NetworkSentBytesPerSecond, 50); Value(partial.CpuPercent, 0);
        Missing(Pair(old, now with { Networks = Networks(("eth0", null)) }).NetworkSentBytesPerSecond, A.MissingEvidence);
        Missing(Pair(old, now with { Networks = Networks(("eth0", Net(99, 200))) }).NetworkReceivedBytesPerSecond, A.CounterReset);
        var empty = Pair(old with { Networks = Networks(), BlockIo = [] }, now with { Networks = Networks(), BlockIo = [] });
        Value(empty.NetworkReceivedBytesPerSecond, 0); Value(empty.BlockIoWriteBytesPerSecond, 0);
        Missing(Pair(old, now with { BlockIo = null }).BlockIoReadBytesPerSecond, A.MissingEvidence);
        Missing(Pair(old, now with { BlockIo = [Io("Read", 100, 2), Io("Write", 200)] }).BlockIoReadBytesPerSecond, A.TopologyChanged);
        Missing(Pair(old, now with { BlockIo = [] }).BlockIoReadBytesPerSecond, A.TopologyChanged);
        var ioPartial = Pair(old, now with { BlockIo = [Io("Read", null), Io("Write", 250)] });
        Missing(ioPartial.BlockIoReadBytesPerSecond, A.MissingEvidence); Value(ioPartial.BlockIoWriteBytesPerSecond, 50);
        Missing(Pair(old, now with { BlockIo = [Io("Read", 100, null), Io("Write", 250)] }).BlockIoReadBytesPerSecond, A.MissingEvidence);
        Missing(Pair(old, now with { BlockIo = [Io("Read", 99), Io("Write", 250)] }).BlockIoReadBytesPerSecond, A.CounterReset);
        var duplicate = Pair(old, now with { BlockIo = [Io("Read", 100), Io("READ", 100), Io("Write", 250)] });
        Missing(duplicate.BlockIoReadBytesPerSecond, A.AmbiguousCounters); Value(duplicate.BlockIoWriteBytesPerSecond, 50);
        foreach (var op in new string?[] { "Reads", "Future", null, "" })
            Missing(Pair(old, now with { BlockIo = [Io(op, 100), Io("Write", 250)] }).BlockIoWriteBytesPerSecond, A.AmbiguousCounters);
        Value(Pair(old, now with { BlockIo = [Io("rEaD", 110), Io("write", 250), Io("Total", ulong.MaxValue), Io("Sync", 999), Io("Async", 999)] }).BlockIoReadBytesPerSecond, 10);
        // Subtract beyond double's exact-integer boundary before conversion: one byte must survive.
        ulong high = ulong.MaxValue - 1;
        var hugeOld = old with { Cpu = Cpu(high, high), Networks = Networks(("a", Net(high, 0)), ("b", Net(high, 0))),
            BlockIo = [Io("Read", high, 1), Io("Read", high, 2), Io("Write", 0)] };
        var hugeNow = now with { Cpu = Cpu(ulong.MaxValue, ulong.MaxValue), Networks = Networks(("a", Net(ulong.MaxValue, 0)), ("b", Net(ulong.MaxValue, 0))),
            BlockIo = [Io("Read", ulong.MaxValue, 1), Io("Read", ulong.MaxValue, 2), Io("Write", 0)] };
        var exact = Pair(hugeOld, hugeNow); Value(exact.CpuPercent, 400); Value(exact.NetworkReceivedBytesPerSecond, 2); Value(exact.BlockIoReadBytesPerSecond, 2);
        var fromZero = hugeOld with { Networks = Networks(("a", Net(0, 0)), ("b", Net(0, 0))), BlockIo = [Io("Read", 0, 1), Io("Read", 0, 2), Io("Write", 0)] };
        var sum = Pair(fromZero, hugeNow); Value(sum.NetworkReceivedBytesPerSecond, 36_893_488_147_419_103_230d); Value(sum.BlockIoReadBytesPerSecond, 36_893_488_147_419_103_230d);
        return Task.CompletedTask;
    }
}
