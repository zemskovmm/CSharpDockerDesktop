using System.Globalization;
using System.Runtime.CompilerServices;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>Dispatcher-owned view. One reader, one pending notification/result and one cleanup join,
/// even when a source ignores cancellation. The Application owns formulas, freshness and retention.</summary>
public sealed class ContainerStatsView(IContainerStats? stats, Func<Action, bool> dispatch) : IDisposable
{
    public const int HistoryLimit = 600, PageSize = 128, MaximumPages = 6;
    private int epoch, notified, readRetries = 1;
    // Shared by pages borrowing the same capability: page recreation cannot create an unbounded
    // number of noncooperative readers, queued dispatcher callbacks or cleanup observers.
    private static readonly ConditionalWeakTable<IContainerStats, WorkSlot> slots = new();
    private static readonly ConditionalWeakTable<Task, CleanupObserver> cleanupObservers = new();
    private readonly WorkSlot slot = stats is null ? new() : slots.GetValue(stats, _ => new());
    private sealed class CleanupObserver(Task task)
    {
        public Task Completion { get; } = Observe(task);
        private static async Task Observe(Task work) { try { await work.ConfigureAwait(false); } catch (Exception) { } }
    }
    private sealed class WorkSlot
    {
        private readonly object gate = new();
        private bool reading, queued;
        private ContainerStatsView? waitingReader, waitingUi, waitingOpener;
        private Task closure = Task.CompletedTask;
        private bool watchingClosure;
        public Task Closure { get { lock (gate) return closure; } }
        public void SetClosure(Task work) { lock (gate) closure = work; }
        public void WaitForCleanup(ContainerStatsView owner)
        {
            Task work;
            lock (gate) { waitingOpener = owner; if (watchingClosure) return; watchingClosure = true; work = closure; }
            _ = AwaitClosureAsync(work);
        }
        private async Task AwaitClosureAsync(Task work)
        {
            await work.ConfigureAwait(false);
            ContainerStatsView? next;
            lock (gate) { watchingClosure = false; next = waitingOpener; waitingOpener = null; }
            next?.Schedule();
        }
        public bool TryRead(ContainerStatsView owner)
        {
            lock (gate) { if (reading) { waitingReader = owner; return false; } reading = true; return true; }
        }
        public void EndRead()
        {
            ContainerStatsView? next;
            lock (gate) { reading = false; next = waitingReader; waitingReader = null; }
            next?.Schedule();
        }
        public void Cancel(ContainerStatsView owner)
        {
            lock (gate)
            {
                if (ReferenceEquals(waitingOpener, owner)) waitingOpener = null;
                if (ReferenceEquals(waitingReader, owner)) waitingReader = null;
                if (ReferenceEquals(waitingUi, owner)) waitingUi = null;
            }
        }
        public void Schedule(ContainerStatsView owner, Func<Action, bool> dispatch)
        {
            lock (gate) { if (!Volatile.Read(ref owner.active)) return; waitingUi = owner; if (queued) return; queued = true; }
            if (!dispatch(() =>
            {
                ContainerStatsView? next;
                lock (gate) { next = waitingUi; waitingUi = null; queued = false; }
                if (next is not null && next.active) next.Tick();
            })) { lock (gate) { queued = false; waitingUi = null; } }
        }
    }
    private bool active, subscribed, reading;
    private long session;
    private string? identity;
    private ContainerStatsFailure? admission;
    private Task readWork = Task.CompletedTask;
    private ReadResult? completedRead;
    private readonly object resultGate = new();
    private sealed record ReadResult(int Epoch, ContainerStatsSnapshot Snapshot,
        IReadOnlyList<ContainerStatsHistoryEntry>? Entries, string Message);
    public Func<bool>? IsCurrentIdentity { get; set; }
    public Func<long, bool>? IsCurrentConnection { get; set; }
    public event EventHandler? Changed;
    public ContainerStatsSnapshot Snapshot { get; private set; } = new(0, 0, ContainerStatsPhase.Closed);
    public IReadOnlyList<ContainerStatsHistoryEntry> History { get; private set; } = Array.Empty<ContainerStatsHistoryEntry>();
    public IReadOnlyList<StatsChart> Charts { get; private set; } = Array.Empty<StatsChart>();
    public Task Cleanup { get; private set; } = Task.CompletedTask;
    public bool Busy => reading || active && session == 0 && !slot.Closure.IsCompleted;
    public string HistoryMessage { get; private set; } = "";
    public bool ShowingDaemonContent { get; private set; }
    public bool HasDaemonContent => (admission ?? Snapshot.Failure)?.DaemonMessage is not null;
    public string DaemonContent => ShowingDaemonContent && active && IsCurrentIdentity?.Invoke() != false && (admission is not null || Current()) ? (admission ?? Snapshot.Failure)?.DaemonMessage ?? "" : "";
    public string Message => admission is { } rejected ? "Stats could not open. " + Failure(rejected)
        : active && session == 0 ? "Opening Stats: waiting for previous session cleanup…"
        : Snapshot.Phase switch
        {
            ContainerStatsPhase.Opening => "Opening Stats… No current measurements yet.",
            ContainerStatsPhase.AwaitingLifecycle => "Awaiting authoritative lifecycle evidence. No current measurements.",
            ContainerStatsPhase.Streaming when Snapshot.Current is not null => "Live Stats · current measurements.",
            ContainerStatsPhase.Streaming => "Stats stream open; awaiting measurements. No current measurements yet.",
            ContainerStatsPhase.Inactive => "Inactive / stopped or paused container. No current measurements.",
            ContainerStatsPhase.Stalled => "Stats stalled: no fresh evidence. Charts are historical; no current measurements.",
            ContainerStatsPhase.Recovering => "Recovering Stats: awaiting fresh connection and lifecycle evidence. No current measurements.",
            ContainerStatsPhase.Completed => "Stats completed. Charts are historical; no current measurements.",
            ContainerStatsPhase.Failed => "Stats failed. Charts are historical; no current measurements. " + (Snapshot.Failure is { } f ? Failure(f) : ""),
            _ => "Stats closed."
        };
    public string Values => StatsDisplay.Values(Snapshot.Phase == ContainerStatsPhase.Streaming ? Snapshot.Current : null);
    public string RetentionMessage => $"{History.Count} displayed / {Snapshot.RetainedEntries} retained observations; {Snapshot.EvictedEntries} evicted. Five-minute in-memory history. "
        + "Charts show historical observations, including gaps; only the values above can be current. " + HistoryMessage;

    public void Open(string id)
    {
        Close(); Cleanup = slot.Closure; active = true; identity = id;
        if (stats is null) { admission = new(ContainerStatsFailureKind.InvalidRequest, "Stats unavailable."); Notify(); return; }
        stats.ContainerStatsChanged += OnChanged; subscribed = true;
        TryOpen(); Notify();
    }
    private void TryOpen()
    {
        if (!active || session != 0 || stats is null || admission is not null || IsCurrentIdentity?.Invoke() == false) return;
        if (!slot.Closure.IsCompleted)
        {
            slot.WaitForCleanup(this);
            return;
        }
        if (!stats.OpenContainerStats(identity!, out admission)) return;
        Snapshot = stats.ContainerStats; session = Snapshot.SessionId;
        Schedule();
    }
    private bool Owned(ContainerStatsSnapshot snapshot) => active && session != 0 && snapshot.SessionId == session
        && snapshot.ContainerId == identity && IsCurrentIdentity?.Invoke() != false
        && IsCurrentConnection?.Invoke(snapshot.ConnectionGeneration) != false;
    private bool Current() => stats is not null && Owned(stats.ContainerStats);
    public void RefreshPresentation()
    {
        if (!active) return;
        Tick();
    }
    private void OnChanged(object? sender, EventArgs args) { Interlocked.Exchange(ref notified, 1); Schedule(); }
    private void Schedule()
    {
        if (Volatile.Read(ref active)) slot.Schedule(this, dispatch);
    }
    private void Tick()
    {
        ReadResult? result;
        lock (resultGate) { result = completedRead; completedRead = null; }
        if (result is not null) reading = false;
        if (!active) return;
        if (Interlocked.Exchange(ref notified, 0) != 0) readRetries = 1;
        if (IsCurrentIdentity?.Invoke() == false) { Close(); Notify(); return; }
        TryOpen();
        var latest = stats?.ContainerStats;
        if (latest is null || !Owned(latest))
        { if (session != 0) Close(); else ClearPresentation(); Notify(); return; }
        if (latest.ConnectionGeneration != Snapshot.ConnectionGeneration || latest.Continuity != Snapshot.Continuity
            || latest.Failure != Snapshot.Failure) ShowingDaemonContent = false;
        // Never mix histories across a recovered connection, even within one active request.
        if (latest.ConnectionGeneration != Snapshot.ConnectionGeneration || latest.EvictedEntries != Snapshot.EvictedEntries
            || latest.RetainedEntries == 0) { History = []; Charts = []; }
        Snapshot = latest;
        if (result is not null && result.Epoch == epoch && SameRevision(result.Snapshot, latest))
        {
            HistoryMessage = result.Message;
            if (result.Entries is { } entries) { History = entries; Charts = StatsDisplay.Charts(entries); }
        }
        bool obsoleteRead = result is not null && result.Epoch == epoch && !SameRevision(result.Snapshot, latest);
        if (obsoleteRead) HistoryMessage = "History changed during reading; awaiting a coherent update.";
        // One immediate repair per notification handles a finite pagination race. A continuously
        // changing source then waits for another signal instead of creating a read/dispatch spin.
        if (!reading && (result is null || result.Epoch != epoch)) StartRead(latest);
        else if (!reading && obsoleteRead && readRetries > 0) { --readRetries; StartRead(latest); }
        Notify();
    }
    private static bool SameRevision(ContainerStatsSnapshot a, ContainerStatsSnapshot b) => a.SessionId == b.SessionId
        && a.ContainerId == b.ContainerId && a.ConnectionGeneration == b.ConnectionGeneration && a.Revision == b.Revision;
    private void StartRead(ContainerStatsSnapshot snapshot)
    {
        if (!slot.TryRead(this))
        { HistoryMessage = "A previous history read is unfinished; current state remains available."; return; }
        reading = true; int lifetime = epoch;
        readWork = Task.Run(() =>
        {
            var entries = new List<ContainerStatsHistoryEntry>(HistoryLimit);
            string message = ""; bool valid = true; long after = 0;
            try
            {
                for (int pageNumber = 0; pageNumber < MaximumPages && entries.Count < HistoryLimit; ++pageNumber)
                {
                    if (Volatile.Read(ref epoch) != lifetime) { valid = false; break; }
                    var page = stats!.ReadContainerStats(after, Math.Min(PageSize, HistoryLimit - entries.Count));
                    if (page.SessionId != snapshot.SessionId || page.Revision != snapshot.Revision || page.ContainerId != snapshot.ContainerId
                        || page.ConnectionGeneration != snapshot.ConnectionGeneration || !SameRevision(snapshot, stats.ContainerStats))
                    { valid = false; message = "History changed during reading; awaiting the next update."; break; }
                    // The source may clamp page size. Advance only by observed unique ordered IDs.
                    if (page.Entries.Count == 0) break;
                    foreach (var entry in page.Entries.Take(PageSize))
                    {
                        if (entry.EntryId <= after) { valid = false; break; }
                        entries.Add(entry); after = entry.EntryId;
                        if (entries.Count == HistoryLimit) break;
                    }
                    if (!valid || entries.Count >= snapshot.RetainedEntries) break;
                }
                if (valid && entries.Count < snapshot.RetainedEntries) message = "Display read budget reached; a bounded history prefix is shown.";
            }
            catch (Exception) { valid = false; message = "History could not be read. Current state remains available."; }
            lock (resultGate) completedRead = new(lifetime, snapshot, valid ? entries.AsReadOnly() : null, message);
            Schedule();
            slot.EndRead();
        });
    }
    // Called immediately before visuals are updated, including callbacks delayed behind navigation.
    public void ValidateForRender()
    {
        if (!active) { ClearPresentation(); return; }
        if (IsCurrentIdentity?.Invoke() == false) { Close(); return; }
        if (session == 0) return;
        var latest = stats!.ContainerStats;
        if (!Owned(latest)) { Close(); return; }
        if (latest.ConnectionGeneration != Snapshot.ConnectionGeneration || latest.EvictedEntries != Snapshot.EvictedEntries
            || latest.RetainedEntries == 0) { History = []; Charts = []; }
        if (latest.Failure != Snapshot.Failure || latest.Continuity != Snapshot.Continuity) ShowingDaemonContent = false;
        Snapshot = latest;
    }
    public void ShowDaemonContent(bool show) { ValidateForRender(); ShowingDaemonContent = show && active && HasDaemonContent; Notify(); }
    private void ClearPresentation()
    { Snapshot = new(0, 0, ContainerStatsPhase.Closed); History = []; Charts = []; ShowingDaemonContent = false; HistoryMessage = ""; }
    public void Close()
    {
        bool owned = stats is not null && session != 0 && stats.ContainerStats.SessionId == session;
        active = false; Interlocked.Increment(ref epoch); readRetries = 1; Interlocked.Exchange(ref notified, 0);
        if (subscribed) stats!.ContainerStatsChanged -= OnChanged;
        slot.Cancel(this);
        subscribed = false; session = 0; identity = null; admission = null; ClearPresentation();
        // No new opener is admitted until this join finishes. The latest desired identity replaces
        // earlier ones without queuing tasks, even if cleanup or a history read never cooperates.
        if (owned)
        {
            var close = stats!.CloseContainerStatsAsync();
            var observed = close.IsCompletedSuccessfully ? Task.CompletedTask : cleanupObservers.GetValue(close, task => new(task)).Completion;
            Cleanup = readWork.IsCompleted ? observed : JoinAsync(observed, readWork);
            slot.SetClosure(Cleanup);
        }
    }
    private static async Task JoinAsync(Task close, Task read)
    { try { await Task.WhenAll(close, read).ConfigureAwait(false); } catch (Exception) { /* No daemon/exception content in diagnostics. */ } }
    public void Dispose() => Close();
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    private static string Failure(ContainerStatsFailure failure) => failure.Kind switch
    {
        ContainerStatsFailureKind.AccessDenied => "Access denied.",
        ContainerStatsFailureKind.NotFound => "The container is no longer available.",
        ContainerStatsFailureKind.SubscriptionLimit => "Stats capacity is occupied. Reopen Stats to retry.",
        ContainerStatsFailureKind.OpeningDeadline => "Opening exceeded its deadline.",
        ContainerStatsFailureKind.TransportLoss => "Connection interrupted.",
        ContainerStatsFailureKind.MalformedSample => "Invalid Stats evidence received.",
        ContainerStatsFailureKind.DaemonError => $"Daemon rejected Stats (HTTP {failure.HttpStatusCode?.ToString() ?? "unknown"}).",
        _ => "Stats requires a current selected container and a Stats-capable connection."
    };
}

public sealed record StatsChartPoint(DateTimeOffset Time, double Value, bool StartsSegment);
public sealed record StatsChart(string Title, string Unit, IReadOnlyList<StatsChartPoint> Points, string RangeLabel, double Maximum, DateTimeOffset? Start, DateTimeOffset? End);

public static class StatsDisplay
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static string Number(double value) => value.ToString("0.##", Invariant);
    public static string Bytes(double value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        int unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; ++unit; }
        return Number(value) + " " + units[unit];
    }
    public static string Availability(ContainerStatsAvailability availability) => availability switch
    {
        ContainerStatsAvailability.Available => "available",
        ContainerStatsAvailability.MissingEvidence => "missing evidence",
        ContainerStatsAvailability.UnsupportedPlatform => "unsupported platform",
        ContainerStatsAvailability.BaselineRequired => "baseline required",
        ContainerStatsAvailability.IncompatibleBaseline => "incompatible baseline",
        ContainerStatsAvailability.InvalidTimestamp => "invalid timestamp",
        ContainerStatsAvailability.CounterReset => "counter reset",
        ContainerStatsAvailability.CpuCountChanged => "CPU count changed",
        ContainerStatsAvailability.TopologyChanged => "topology changed",
        ContainerStatsAvailability.AmbiguousCounters => "ambiguous counters",
        ContainerStatsAvailability.InvalidAdjustment => "invalid cache adjustment",
        _ => "invalid denominator"
    };
    public static string Provenance(ContainerStatsMeasurementSource source) => source switch
    {
        ContainerStatsMeasurementSource.LinuxTotalInactiveFile => "cache-adjusted · total_inactive_file (cgroup v1)",
        ContainerStatsMeasurementSource.LinuxInactiveFile => "cache-adjusted · inactive_file (cgroup v2)",
        ContainerStatsMeasurementSource.LinuxHistoricalCache => "cache-adjusted · historical cache",
        ContainerStatsMeasurementSource.LinuxRawAdjustmentRejected => "raw usage fallback · cache adjustment rejected",
        ContainerStatsMeasurementSource.WindowsPrivateWorkingSet => "Windows private working set",
        ContainerStatsMeasurementSource.WindowsCommit => "Windows commit · separate from working set",
        ContainerStatsMeasurementSource.LinuxMemoryLimit => "Linux memory limit",
        ContainerStatsMeasurementSource.LinuxCpuOnlineCount => "Linux online CPU count",
        ContainerStatsMeasurementSource.LinuxCpuPerCpuCount => "Linux per-CPU count",
        ContainerStatsMeasurementSource.WindowsCpu100Nanoseconds => "Windows CPU runtime (100 ns)",
        ContainerStatsMeasurementSource.NetworkCounters => "network counter deltas",
        ContainerStatsMeasurementSource.LinuxBlockIo => "Linux Block I/O counter deltas",
        ContainerStatsMeasurementSource.WindowsStorage => "Windows storage counter deltas",
        _ => "no source evidence"
    };
    private static string Format<T>(ContainerStatsMeasurement<T> value, Func<T, string> format) where T : struct
        => (value.Availability == ContainerStatsAvailability.Available && value.Value is { } available ? format(available)
            : "Unavailable · " + Availability(value.Availability)) + " (" + Provenance(value.Source) + ")";
    public static string Values(ContainerStatsMeasurements? m) => m is null
        ? "CPU: unavailable · no current measurement\nMemory: unavailable · no current measurement\nNetwork receive / send: unavailable · no current measurement\nBlock I/O read / write activity: unavailable · no current measurement"
        : $"CPU: {Format(m.CpuPercent, v => Number(v) + " %")} · baseline: {m.CpuBaseline}\n"
            + $"Memory usage: {Format(m.MemoryUsageBytes, v => Bytes(v))}\nMemory percentage: {Format(m.MemoryPercent, v => Number(v) + " %")}\n"
            + (m.OSType == "windows" ? $"Memory commit: {Format(m.MemoryCommitBytes, v => Bytes(v))}\n" : "")
            + $"Network receive: {Format(m.NetworkReceivedBytesPerSecond, v => Bytes(v) + "/s")}\nNetwork send: {Format(m.NetworkSentBytesPerSecond, v => Bytes(v) + "/s")}\n"
            + $"Block I/O read activity: {Format(m.BlockIoReadBytesPerSecond, v => Bytes(v) + "/s")}\nBlock I/O write activity: {Format(m.BlockIoWriteBytesPerSecond, v => Bytes(v) + "/s")}";
    public static IReadOnlyList<StatsChart> Charts(IReadOnlyList<ContainerStatsHistoryEntry> entries)
    {
        StatsChart Make(string title, string unit, Func<ContainerStatsMeasurements, (double? Value, ContainerStatsMeasurementSource Source)> metric)
        {
            var points = new List<StatsChartPoint>(Math.Min(entries.Count, ContainerStatsView.HistoryLimit));
            long? continuity = null; ContainerStatsMeasurementSource? source = null; string? platform = null, identity = null; bool gap = true; DateTimeOffset? previous = null;
            foreach (var entry in entries.Take(ContainerStatsView.HistoryLimit))
            {
                if (entry.Measurements is not { } measurements) { gap = true; continue; }
                var value = metric(measurements);
                if (value.Value is not { } number || !double.IsFinite(number) || number < 0) { gap = true; continue; }
                bool starts = gap || entry.Boundary != ContainerStatsBoundary.None || continuity != entry.Continuity
                    || source != value.Source || platform != measurements.OSType || identity != measurements.ContainerId || previous >= entry.ObservedAt;
                points.Add(new(entry.ObservedAt, number, starts)); gap = false; continuity = entry.Continuity;
                source = value.Source; platform = measurements.OSType; identity = measurements.ContainerId; previous = entry.ObservedAt;
            }
            double max = points.Count == 0 ? 1 : Math.Max(1, points.Max(p => p.Value));
            string label = points.Count == 0 ? "No available historical values. Gaps are not zero."
                : $"Historical · {points[0].Time.UtcDateTime:HH:mm:ss}–{points[^1].Time.UtcDateTime:HH:mm:ss} UTC · {points.Count} available points · "
                    + $"{points.Count(p => p.StartsSegment)} separate segments · scale 0–{(unit == "%" ? Number(max) + " %" : Bytes(max) + unit)}";
            return new(title, unit, points.AsReadOnly(), label, max, entries.FirstOrDefault()?.ObservedAt, entries.LastOrDefault()?.ObservedAt);
        }
        static double? Value<T>(ContainerStatsMeasurement<T> m) where T : struct => m.Availability == ContainerStatsAvailability.Available && m.Value is { } v ? Convert.ToDouble(v, Invariant) : null;
        return Array.AsReadOnly(new[] {
            Make("CPU", "%", m => (Value(m.CpuPercent), m.CpuPercent.Source)),
            Make("Memory usage", "", m => (Value(m.MemoryUsageBytes), m.MemoryUsageBytes.Source)),
            Make("Network receive", "/s", m => (Value(m.NetworkReceivedBytesPerSecond), m.NetworkReceivedBytesPerSecond.Source)),
            Make("Network send", "/s", m => (Value(m.NetworkSentBytesPerSecond), m.NetworkSentBytesPerSecond.Source)),
            Make("Block I/O read activity", "/s", m => (Value(m.BlockIoReadBytesPerSecond), m.BlockIoReadBytesPerSecond.Source)),
            Make("Block I/O write activity", "/s", m => (Value(m.BlockIoWriteBytesPerSecond), m.BlockIoWriteBytesPerSecond.Source)) });
    }
}
