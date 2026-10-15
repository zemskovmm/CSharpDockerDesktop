using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

/// <summary>Synthetic Application-derived evidence, never a Docker/measurement-formula test.</summary>
internal sealed partial class FixtureBrowser : IContainerStats
{
    private readonly object statsGate = new();
    private readonly Queue<ContainerStatsHistoryEntry> statsEntries = new();
    private ContainerStatsSnapshot statsSnapshot = new(0, 0, ContainerStatsPhase.Closed);
    private CancellationTokenSource? statsLifetime;
    private Task statsWork = Task.CompletedTask;
    private long statsSession, statsEntry;
    private DateTimeOffset statsStart;
    public int StatsScenario, StatsOpens, StatsCloses, StatsReadDelay, StatsReads;
    public event EventHandler? ContainerStatsChanged;
    public ContainerStatsSnapshot ContainerStats { get { lock (statsGate) return statsSnapshot; } }
    public bool OpenContainerStats(string id, out ContainerStatsFailure? failure)
    {
        failure = null; _ = CloseContainerStatsAsync();
        lock (statsGate)
        {
            ++StatsOpens; statsEntries.Clear(); statsEntry = 0; statsStart = DateTimeOffset.UtcNow.AddMinutes(-5);
            long session = ++statsSession; int scenario = StatsScenario;
            statsSnapshot = new(session, 1, ContainerStatsPhase.Opening, id, ContainerSnapshot.ConnectionGeneration);
            var cancellation = statsLifetime = new();
            statsWork = Task.Run(() => ProduceStatsAsync(session, scenario, cancellation));
        }
        return true;
    }
    public ContainerStatsHistory ReadContainerStats(long afterEntryId = 0, int maximumEntries = 300)
    {
        Interlocked.Increment(ref StatsReads);
        ContainerStatsHistory page;
        lock (statsGate) page = new(statsSnapshot.SessionId, statsSnapshot.Revision, statsSnapshot.ContainerId,
            statsSnapshot.ConnectionGeneration, statsEntries.Where(e => e.EntryId > afterEntryId).Take(Math.Min(maximumEntries, 128)).ToArray());
        int delay = Volatile.Read(ref StatsReadDelay);
        if (delay > 0) Thread.Sleep(delay); // Deliberately noncooperative synchronous boundary on the background reader.
        return page;
    }
    public Task CloseContainerStatsAsync()
    {
        lock (statsGate)
        {
            if (statsLifetime is not null) { ++StatsCloses; statsLifetime.Cancel(); statsLifetime = null; }
            statsEntries.Clear(); statsSnapshot = statsSnapshot with { Phase = ContainerStatsPhase.Closed, Current = null,
                RetainedEntries = 0, Revision = statsSnapshot.Revision + 1 };
            return statsWork;
        }
    }
    public void StatsState(ContainerStatsPhase phase)
    {
        lock (statsGate)
        {
            statsSnapshot = statsSnapshot with { Phase = phase, Current = null, Continuity = statsSnapshot.Continuity + 1, Revision = statsSnapshot.Revision + 1 };
            AddStatsEntryLocked(null, ContainerStatsBoundary.LifecycleInvalidated);
        }
        ContainerStatsChanged?.Invoke(this, EventArgs.Empty);
    }
    private void AddStatsEntryLocked(ContainerStatsMeasurements? m, ContainerStatsBoundary boundary = ContainerStatsBoundary.None)
    {
        statsEntries.Enqueue(new(++statsEntry, statsSnapshot.Continuity, statsStart.AddSeconds(statsEntry), m, boundary));
        while (statsEntries.Count > 600 || statsEntries.Count > 0 && statsEntries.Peek().ObservedAt < DateTimeOffset.UtcNow.AddMinutes(-5)) { statsEntries.Dequeue(); statsSnapshot = statsSnapshot with { EvictedEntries = statsSnapshot.EvictedEntries + 1 }; }
        statsSnapshot = statsSnapshot with { RetainedEntries = statsEntries.Count, Revision = statsSnapshot.Revision + 1 };
    }
    private async Task ProduceStatsAsync(long session, int scenario, CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token; await Task.Delay(350, token);
            int tick = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (statsGate)
                {
                    if (statsSnapshot.SessionId != session || token.IsCancellationRequested) return;
                    if (scenario >= 2)
                    {
                        var phase = scenario switch { 2 => ContainerStatsPhase.AwaitingLifecycle, 3 => ContainerStatsPhase.Inactive,
                            4 => ContainerStatsPhase.Stalled, 5 => ContainerStatsPhase.Recovering, 6 => ContainerStatsPhase.Completed, _ => ContainerStatsPhase.Failed };
                        statsSnapshot = statsSnapshot with { Phase = phase, Current = null, Failure = scenario == 7
                            ? new(ContainerStatsFailureKind.DaemonError, "Synthetic failure", 500, "PRIVATE Stats fixture daemon content") : null, Revision = statsSnapshot.Revision + 1 };
                    }
                    else
                    {
                        int count = tick == 0 ? 300 : 1;
                        for (int i = 0; i < count; ++i)
                        {
                            long n = statsEntry + 1; bool gap = n % 50 == 0;
                            if (gap) statsSnapshot = statsSnapshot with { Continuity = statsSnapshot.Continuity + 1 };
                            var m = gap ? null : StatsMeasurement(statsSnapshot.ContainerId!, scenario == 1, n);
                            AddStatsEntryLocked(m, gap ? ContainerStatsBoundary.SampleGap : ContainerStatsBoundary.None);
                            statsSnapshot = statsSnapshot with { Phase = ContainerStatsPhase.Streaming, Current = m, LastObservedAt = DateTimeOffset.UtcNow };
                        }
                    }
                }
                ContainerStatsChanged?.Invoke(this, EventArgs.Empty);
                if (scenario >= 2) return;
                ++tick; await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { lock (statsGate) { if (ReferenceEquals(statsLifetime, cancellation)) statsLifetime = null; cancellation.Dispose(); } }
    }
    private static ContainerStatsMeasurements StatsMeasurement(string id, bool windows, long point)
    {
        var available = ContainerStatsAvailability.Available;
        var source = windows ? ContainerStatsMeasurementSource.WindowsPrivateWorkingSet : point % 3 == 0
            ? ContainerStatsMeasurementSource.LinuxRawAdjustmentRejected : ContainerStatsMeasurementSource.LinuxInactiveFile;
        return new(id, windows ? "windows" : "linux", new("synthetic", point, 0),
            new(point % 7 == 0 ? 0 : 125 + 75 * Math.Sin(point / 12.0), available, windows ? ContainerStatsMeasurementSource.WindowsCpu100Nanoseconds : ContainerStatsMeasurementSource.LinuxCpuOnlineCount),
            ContainerStatsCpuBaseline.PreviousSample, new((ulong)(1024 * 1024 * (64 + point % 32)), available, source),
            windows ? new(null, ContainerStatsAvailability.UnsupportedPlatform, ContainerStatsMeasurementSource.None) : new(25, available, ContainerStatsMeasurementSource.LinuxMemoryLimit),
            windows ? new(128 * 1024 * 1024, available, ContainerStatsMeasurementSource.WindowsCommit) : new(null, ContainerStatsAvailability.UnsupportedPlatform, ContainerStatsMeasurementSource.None),
            new(point % 9 == 0 ? 0 : point * 100, available, ContainerStatsMeasurementSource.NetworkCounters),
            point % 11 == 0 ? new(null, ContainerStatsAvailability.CounterReset, ContainerStatsMeasurementSource.NetworkCounters) : new(point * 50, available, ContainerStatsMeasurementSource.NetworkCounters),
            new(point * 40, available, windows ? ContainerStatsMeasurementSource.WindowsStorage : ContainerStatsMeasurementSource.LinuxBlockIo),
            new(0, available, windows ? ContainerStatsMeasurementSource.WindowsStorage : ContainerStatsMeasurementSource.LinuxBlockIo));
    }
}
