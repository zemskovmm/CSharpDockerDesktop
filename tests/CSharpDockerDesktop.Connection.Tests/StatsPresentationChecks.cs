using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class StatsPresentationChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task OwnershipAndTabs()
    {
        var source = new Source(); var ui = new Dispatcher(); using var browser = new ContainerBrowserView(source, ui.Post);
        browser.Attach(); await ui.Until(() => !browser.Filtering);
        browser.Select(Source.A); browser.OpenSelected(); Check(source.Opens == 0, "Browsing/Overview must not start Stats");
        browser.ShowStats(); source.Add(Zero(Source.A)); await ui.Settle(browser.Stats);
        Check(source.OpenedId == Source.A && browser.ShowingStats && browser.Stats.Values.Contains("0 %"), "Stats owns the exact selected full identity");
        browser.ShowInspect(true); Check(source.Closes == 1 && browser.Stats.History.Count == 0, "Inspect closes and clears Stats synchronously");
        browser.ShowStats(); browser.ShowLogs(); Check(source.Closes == 2 && !browser.ShowingStats, "Logs closes Stats without breaking navigation");
        browser.ShowStats(); browser.CloseDetail(); Check(source.Closes == 3 && browser.Stats.Charts.Count == 0, "Back clears and closes");
        browser.OpenSelected(); browser.ShowStats(); browser.Select(Source.B); Check(source.Closes == 4, "Selection change closes old full identity");
        browser.OpenSelected(); browser.ShowStats(); Check(source.OpenedId == Source.B, "Reopening targets new exact identity");
        browser.Dispose(); ui.Drain(); Check(source.Closes == 5 && source.Subscribers == 0, "Page departure detaches Stats and joins cleanup");
        await browser.Stats.Cleanup;
    }
    public static async Task SlowCleanupAndReplacement()
    {
        var source = new Source(); var ui = new Dispatcher(); using var view = new ContainerStatsView(source, ui.Post);
        view.Open(Source.A); await ui.Settle(view);
        source.CloseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Close(); Check(!view.Cleanup.IsCompleted && source.Closes == 1, "Close captures an asynchronous join without blocking");
        for (int i = 0; i < 1000; ++i) { view.Open(i == 999 ? Source.B : Source.A); ui.Drain(); }
        Check(source.Opens == 1 && ui.Count <= 1 && view.Message.Contains("cleanup"), "Noncooperative cleanup bounds replacement openers and dispatcher work");
        source.CloseGate.SetResult(); await view.Cleanup; await ui.Until(() => source.Opens == 2); await ui.Settle(view);
        Check(source.OpenedId == Source.B, "Only the latest requested identity opens after joined cleanup");
        view.Close(); await view.Cleanup; Check(source.Closes == 2, "Old cleanup never closes a later session");
    }
    public static async Task PageRecreationBudgets()
    {
        var source = new Source(); var ui = new Dispatcher(); var original = new ContainerStatsView(source, ui.Post);
        original.Open(Source.A); await ui.Settle(original);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        source.OnRead = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "Page read release timeout"); };
        source.Add(Zero(Source.A)); source.Signal(); ui.One(); Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Page read did not start");
        source.CloseGate = new(TaskCreationOptions.RunContinuationsAsynchronously); original.Dispose();
        ContainerStatsView? latest = null;
        for (int i = 0; i < 1000; ++i)
        {
            latest?.Dispose(); latest = new(source, ui.Post); latest.Open(i == 999 ? Source.B : Source.A);
            Check(ui.Count <= 1 && source.Opens == 1 && source.Reads == 2, "Recreated pages share one cleanup/read/dispatcher budget");
        }
        release.Set(); source.CloseGate.SetResult(); await original.Cleanup;
        await ui.Until(() => source.Opens == 2); source.Add(Zero(Source.B)); await ui.Settle(latest!);
        Check(latest!.History.Single().Measurements!.ContainerId == Source.B, "Only latest recreated page receives new history");
        latest.Dispose(); await latest.Cleanup;
    }
    public static async Task ObsoleteReadsAndCallbacks()
    {
        var source = new Source(); var ui = new Dispatcher(); using var view = new ContainerStatsView(source, ui.Post);
        view.Open(Source.A); await ui.Settle(view); source.Add(Zero(Source.A));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        source.OnRead = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "Read release timeout"); };
        source.Signal(); ui.One(); Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Background reader did not start");
        source.Add(Zero(Source.A)); source.Signal(10000); Check(ui.Count <= 1, "Burst has one queued notification");
        int opens = source.Opens; view.Open(Source.B); ui.Drain();
        Check(source.Opens == opens && view.History.Count == 0, "Navigation clears immediately while slow history cleanup joins");
        release.Set(); await view.Cleanup; await ui.Until(() => source.Opens == opens + 1);
        source.Add(Zero(Source.B)); await ui.Settle(view);
        Check(view.History.All(e => e.Measurements?.ContainerId == Source.B), "Obsolete background history cannot populate replacement");
        source.Signal(); view.Close(); ui.Drain(); Check(view.History.Count == 0 && view.Charts.Count == 0, "Queued notifications after departure cannot revive visuals");
        view.Open(Source.A); source.Add(Zero(Source.A)); bool current = true; view.IsCurrentIdentity = () => current;
        await ui.Settle(view); source.Signal(); current = false; view.ValidateForRender(); ui.Drain();
        Check(view.Snapshot.Current is null && view.Charts.Count == 0, "Render rechecks identity before browser notification catches up");
        current = true; view.Open(Source.A); source.Add(Zero(Source.A)); await ui.Settle(view);
        view.IsCurrentConnection = generation => generation == 1;
        source.RecoverGeneration(); source.Add(Zero(Source.A)); source.Signal(); view.ValidateForRender(); ui.Drain();
        Check(view.Snapshot.Current is null && view.Charts.Count == 0, "Render rejects an Application generation that the active browser no longer owns");
    }
    public static async Task RevisionsEvictionAndBounds()
    {
        var source = new Source(); var ui = new Dispatcher(); using var view = new ContainerStatsView(source, ui.Post);
        view.Open(Source.A); await ui.Settle(view); source.Add(Zero(Source.A), 600); source.Signal(); await ui.Settle(view);
        Check(view.History.Count == 600 && view.Charts.Count == 6 && view.Charts.All(c => c.Points.Count <= 600), "Fixed chart/history budgets");
        Check(source.LargestRead <= ContainerStatsView.PageSize && source.Reads <= 7, "Bounded pages per pass");
        source.OnRead = () => source.Evict(300); source.Signal(); await ui.Settle(view);
        Check(view.History.All(e => e.EntryId > 300), "Eviction raced with pagination must not leave evicted data");
        source.Clear(); source.Add(Zero(Source.A), 600); source.ReadClamp = 16; int before = source.Reads; source.Signal(); await ui.Settle(view);
        Check(source.Reads - before <= ContainerStatsView.MaximumPages && view.History.Count <= 96 && view.HistoryMessage.Contains("budget"), "Source-clamped reads have finite pass budget");
        source.ReadClamp = 128;
        void RacingRead() { source.Evict(0); source.OnRead = RacingRead; }
        source.OnRead = RacingRead; before = source.Reads; source.Signal(); await ui.Settle(view);
        Check(source.Reads - before <= 2 && ui.Count == 0 && view.HistoryMessage.Contains("changed"), "Continuously changing revisions must not spin background/dispatcher retries");
        source.OnRead = null; source.Signal(); await ui.Settle(view);
        for (int i = 0; i < 100; ++i)
        {
            source.Add(Zero(Source.A), 10); source.Evict(Math.Max(0, source.LastId - 600)); source.Signal(1000); await ui.Settle(view);
            Check(view.History.Count <= 600 && ui.Count <= 1, "Sustained updates retain bounded state/queue");
        }
        Check(view.History.Last().EntryId == source.LastId && !view.Busy, "Sustained producer makes progress");
    }
    public static async Task LifecycleRecoveryAndPrivacy()
    {
        var source = new Source(); var ui = new Dispatcher(); using var view = new ContainerStatsView(source, ui.Post);
        view.Open(Source.A); source.Add(Zero(Source.A)); await ui.Settle(view);
        foreach (var phase in new[] { ContainerStatsPhase.AwaitingLifecycle, ContainerStatsPhase.Inactive, ContainerStatsPhase.Stalled,
            ContainerStatsPhase.Recovering, ContainerStatsPhase.Completed, ContainerStatsPhase.Opening })
        {
            source.Phase(phase); source.Signal(); view.ValidateForRender();
            Check(!view.Values.Contains("0 %") && view.Snapshot.Current is null, "Lifecycle invalidates current values before delayed notification");
            await ui.Settle(view); Check(view.Message.Contains(phase switch { ContainerStatsPhase.AwaitingLifecycle => "lifecycle", ContainerStatsPhase.Inactive => "Inactive", _ => phase.ToString() }, StringComparison.OrdinalIgnoreCase), "Explicit textual session state " + phase);
        }
        source.Fail(); await ui.Settle(view);
        Check(view.Message.Contains("failed") && !view.Message.Contains("PRIVATE") && view.DaemonContent == "", "Classified safe failure, original hidden");
        view.ShowDaemonContent(true); Check(view.DaemonContent == "PRIVATE daemon content", "Original is deliberate exact reveal");
        source.RecoverGeneration(); source.Signal(); view.ValidateForRender();
        Check(view.Charts.Count == 0 && view.DaemonContent == "", "Generation replacement clears obsolete history and reveal");
        source.Add(Zero(Source.A)); await ui.Settle(view); Check(view.Values.Contains("0 %"), "Recovery adopts new valid evidence");
        var browserSource = new Source(); using var browser = new ContainerBrowserView(browserSource, ui.Post);
        browser.Attach(); await ui.Until(() => !browser.Filtering); browser.Select(Source.A); browser.OpenSelected(); browser.ShowStats();
        browserSource.Disconnect(); ui.Drain(); Check(!browser.DetailOpen && !browser.ShowingStats && browserSource.Closes == 1, "Disconnect closes tab ownership");
        browserSource.Reconnect(); ui.Drain(); Check(browserSource.Opens == 1, "Connection recovery cannot silently reopen a departed view");
    }
    public static Task ValuesAndProvenance()
    {
        var zero = Zero(Source.A); string text = StatsDisplay.Values(zero);
        Check(text.Contains("CPU: 0 %") && text.Contains("Network receive: 0 B/s") && text.Contains("Block I/O read activity: 0 B/s"), "Valid zeros remain zero");
        Check(text.Contains("Memory usage: 0 B") && text.Contains("cache-adjusted") && text.Contains("cgroup v2"), "Memory zero retains cache provenance");
        var unavailable = new ContainerStatsMeasurement<double>(null, ContainerStatsAvailability.BaselineRequired, ContainerStatsMeasurementSource.NetworkCounters);
        text = StatsDisplay.Values(zero with { CpuPercent = new(240.5, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxCpuOnlineCount), NetworkReceivedBytesPerSecond = unavailable });
        Check(text.Contains("240.5 %") && text.Contains("Unavailable · baseline required"), "CPU above 100 never clamped and baseline remains unavailable");
        text = StatsDisplay.Values(zero with { OSType = "windows", MemoryUsageBytes = new(2048, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.WindowsPrivateWorkingSet),
            MemoryPercent = new(null, ContainerStatsAvailability.UnsupportedPlatform, ContainerStatsMeasurementSource.None), MemoryCommitBytes = new(4096, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.WindowsCommit) });
        Check(text.Contains("2 KiB") && text.Contains("Windows private working set") && text.Contains("unsupported platform") && text.Contains("4 KiB") && text.Contains("separate from working set"), "Windows working set/commit/unsupported percent are distinct");
        text = StatsDisplay.Values(zero with { MemoryUsageBytes = new(1024, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxRawAdjustmentRejected) });
        Check(text.Contains("raw usage fallback"), "Raw fallback cannot be labelled cache-adjusted");
        foreach (var availability in Enum.GetValues<ContainerStatsAvailability>().Where(a => a != ContainerStatsAvailability.Available))
        {
            text = StatsDisplay.Values(zero with { NetworkReceivedBytesPerSecond = unavailable with { Availability = availability } });
            Check(text.Contains(StatsDisplay.Availability(availability)) && !text.Contains("Network receive: 0"), "Unavailable evidence must not become zero " + availability);
        }
        return Task.CompletedTask;
    }
    public static Task ChartContinuity()
    {
        var zero = Zero(Source.A); var missing = zero with { CpuPercent = new(null, ContainerStatsAvailability.CounterReset, ContainerStatsMeasurementSource.LinuxCpuOnlineCount) };
        var time = DateTimeOffset.UnixEpoch;
        ContainerStatsHistoryEntry Entry(int id, long continuity, ContainerStatsMeasurements? m, ContainerStatsBoundary boundary = ContainerStatsBoundary.None) => new(id, continuity, time.AddSeconds(id), m, boundary);
        var history = new[] { Entry(1, 1, zero), Entry(2, 1, zero), Entry(3, 1, null, ContainerStatsBoundary.SampleGap), Entry(4, 1, zero),
            Entry(5, 1, missing), Entry(6, 1, zero), Entry(7, 2, zero), Entry(8, 2, zero with { OSType = "windows" }),
            Entry(9, 2, zero with { ContainerId = Source.B, OSType = "windows" }) };
        var charts = StatsDisplay.Charts(history); var cpu = charts[0];
        Check(cpu.Points.Count == 7 && !cpu.Points[1].StartsSegment && cpu.Points.Skip(2).All(p => p.StartsSegment), "Gaps, null field, continuity, platform and identity all break lines");
        Check(charts[2].Points.Count == 8 && !charts[2].Points.Single(p => p.Time == time.AddSeconds(6)).StartsSegment, "Unavailable CPU does not erase available network evidence");
        Check(cpu.Points.All(p => p.Value == 0) && cpu.RangeLabel.Contains("historical", StringComparison.OrdinalIgnoreCase), "Zero is plotted but explicitly historical");
        var provenance = StatsDisplay.Charts([Entry(1, 1, zero), Entry(2, 1, zero with { MemoryUsageBytes = new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxRawAdjustmentRejected) })]);
        Check(provenance[1].Points.All(p => p.StartsSegment), "Changed memory meaning never silently interpolated");
        return Task.CompletedTask;
    }
    internal static ContainerStatsMeasurements Zero(string id) => new(id, "linux", new("fixture", 1, 0),
        new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxCpuOnlineCount), ContainerStatsCpuBaseline.PreviousSample,
        new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxInactiveFile),
        new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxMemoryLimit),
        new(null, ContainerStatsAvailability.UnsupportedPlatform, ContainerStatsMeasurementSource.None),
        new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.NetworkCounters), new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.NetworkCounters),
        new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxBlockIo), new(0, ContainerStatsAvailability.Available, ContainerStatsMeasurementSource.LinuxBlockIo));
    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new(); public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void One() { if (queue.TryDequeue(out var work)) work(); }
        public void Drain() { int budget = 1000; while (queue.Count > 0 && --budget > 0) One(); Check(budget > 0, "UI queue did not converge"); }
        public async Task Until(Func<bool> condition) { for (int i = 0; i < 3000; ++i) { Drain(); if (condition()) return; await Task.Delay(2); } throw new InvalidOperationException("Presentation settle timeout"); }
        public Task Settle(ContainerStatsView view) => Until(() => !view.Busy && Count == 0);
    }
    private sealed class Source : IContainerStats, IContainerBrowser
    {
        public const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly object gate = new(); private readonly List<ContainerStatsHistoryEntry> entries = [];
        private ContainerStatsSnapshot snapshot = new(0, 0, ContainerStatsPhase.Closed); private long sequence, session;
        private EventHandler? changed;
        public int Subscribers, Opens, Closes, Reads, LargestRead, ReadClamp = 128;
        public long LastId { get { lock (gate) return sequence; } }
        public Action? OnRead; public string? OpenedId; public TaskCompletionSource? CloseGate;
        public event EventHandler? ContainerStatsChanged { add { changed += value; ++Subscribers; } remove { changed -= value; --Subscribers; } }
        public ContainerStatsSnapshot ContainerStats { get { lock (gate) return snapshot; } }
        public bool OpenContainerStats(string id, out ContainerStatsFailure? failure)
        { lock (gate) { ++Opens; failure = null; OpenedId = id; entries.Clear(); snapshot = new(++session, 1, ContainerStatsPhase.Opening, id, ContainerSnapshot.ConnectionGeneration); return true; } }
        public ContainerStatsHistory ReadContainerStats(long afterEntryId = 0, int maximumEntries = 300)
        {
            ContainerStatsHistory result; Action? hook;
            lock (gate) { ++Reads; LargestRead = Math.Max(LargestRead, maximumEntries); result = new(snapshot.SessionId, snapshot.Revision, snapshot.ContainerId, snapshot.ConnectionGeneration, entries.Where(e => e.EntryId > afterEntryId).Take(Math.Min(ReadClamp, maximumEntries)).ToArray()); hook = OnRead; OnRead = null; }
            hook?.Invoke(); return result;
        }
        public Task CloseContainerStatsAsync() { lock (gate) { ++Closes; entries.Clear(); snapshot = snapshot with { Phase = ContainerStatsPhase.Closed, Current = null, Revision = snapshot.Revision + 1, RetainedEntries = 0 }; return CloseGate?.Task ?? Task.CompletedTask; } }
        public void Signal(int count = 1) { for (int i = 0; i < count; ++i) changed?.Invoke(this, EventArgs.Empty); }
        public void Add(ContainerStatsMeasurements m, int count = 1)
        { lock (gate) { for (int i = 0; i < count; ++i) entries.Add(new(++sequence, snapshot.Continuity, DateTimeOffset.UnixEpoch.AddSeconds(sequence), m, ContainerStatsBoundary.None)); snapshot = snapshot with { Phase = ContainerStatsPhase.Streaming, Current = m, RetainedEntries = entries.Count, Revision = snapshot.Revision + 1 }; } }
        public void Evict(long before) { lock (gate) { int removed = entries.RemoveAll(e => e.EntryId <= before); snapshot = snapshot with { RetainedEntries = entries.Count, EvictedEntries = snapshot.EvictedEntries + removed, Revision = snapshot.Revision + 1 }; } }
        public void Clear() { lock (gate) { entries.Clear(); snapshot = snapshot with { RetainedEntries = 0, Revision = snapshot.Revision + 1 }; } }
        public void Phase(ContainerStatsPhase phase) { lock (gate) snapshot = snapshot with { Phase = phase, Current = null, Continuity = snapshot.Continuity + 1, Revision = snapshot.Revision + 1 }; }
        public void Fail() { lock (gate) snapshot = snapshot with { Phase = ContainerStatsPhase.Failed, Current = null, Failure = new(ContainerStatsFailureKind.DaemonError, "PRIVATE description", 500, "PRIVATE daemon content"), Revision = snapshot.Revision + 1 }; Signal(); }
        public void RecoverGeneration() { lock (gate) { entries.Clear(); snapshot = snapshot with { ConnectionGeneration = snapshot.ConnectionGeneration + 1, Phase = ContainerStatsPhase.Recovering, Current = null, Failure = null, RetainedEntries = 0, Revision = snapshot.Revision + 1 }; } }
        private static ContainerSummary Row(string id) => new(id, [id], null, null, "running", null, null, null, null, null, null);
        public ContainerBrowsingSnapshot ContainerSnapshot { get; private set; } = new(1, ResourceReadState.Ready, [Row(A), Row(B)]);
        public event EventHandler? ContainersChanged;
        public void SelectContainer(string? id) { ContainerSnapshot = ContainerSnapshot with { SelectedContainerId = id }; ContainersChanged?.Invoke(this, EventArgs.Empty); }
        public void ClearContainerInspection() { }
        public void RefreshContainerInspection() { }
        public void RefreshContainers() { }
        public void Disconnect() { ContainerSnapshot = new(2, ResourceReadState.Unavailable, []); ContainersChanged?.Invoke(this, EventArgs.Empty); }
        public void Reconnect() { ContainerSnapshot = new(3, ResourceReadState.Ready, [Row(A), Row(B)]); ContainersChanged?.Invoke(this, EventArgs.Empty); }
    }
}
