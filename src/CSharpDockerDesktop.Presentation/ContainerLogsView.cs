using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>UI-dispatcher-owned bounded window. Notifications carry no text and enqueue at most one
/// callback. Scans use one bounded page per dispatcher turn; cursor hints never publish old pages.</summary>
public sealed class ContainerLogsView(IContainerLogs? logs, Func<Action, bool> dispatch) : IDisposable
{
    public const int PageSize = 128;
    private int epoch, queued;
    private bool active, scanning, dirty, subscribed, scanStarted;
    private long session, scanAfter, windowAfter, scanRemaining;
    private int scanMode; // latest, first, next, previous
    private long previousLimit, windowBefore = long.MaxValue;
    private string? identity;
    private ContainerLogFailure? admission;
    public Func<bool>? IsCurrentIdentity { get; set; }
    public event EventHandler? Changed;
    public IReadOnlyList<ContainerLogRow> Rows { get; private set; } = Array.Empty<ContainerLogRow>();
    public ContainerLogsSnapshot Snapshot { get; private set; } = new(0, 0, ContainerLogsPhase.Closed);
    public string Query { get; private set; } = "";
    public ContainerLogStream? Stream { get; private set; }
    public bool Autoscroll { get; private set; } = true;
    public bool Follow { get; private set; } = true;
    public bool Busy => scanning;
    public bool ShowingDaemonContent { get; private set; }
    public string DaemonContent => ShowingDaemonContent ? (admission ?? Snapshot.Failure)?.DaemonMessage ?? "" : "";
    public bool HasDaemonContent => (admission ?? Snapshot.Failure)?.DaemonMessage is not null;
    public string ReadingMessage { get; private set; } = "";
    public Task Cleanup { get; private set; } = Task.CompletedTask;
    public string Message => admission is { } rejected ? "Logs could not open. " + Failure(rejected)
        : Snapshot.Phase switch
        {
            ContainerLogsPhase.Opening => "Opening a limited recent tail…",
            ContainerLogsPhase.Streaming => Follow ? "Streaming live output." : "Reading recent output without live follow.",
            ContainerLogsPhase.Recovering => $"Recovering Logs (attempt {Snapshot.RecoveryAttempt}). Recent output may contain gaps or duplicates. " + (Snapshot.Failure is { } f ? Failure(f) : ""),
            ContainerLogsPhase.Completed => "Log stream completed. Retained output remains available.",
            ContainerLogsPhase.Canceled => "Logs canceled.",
            ContainerLogsPhase.Failed => "Logs failed. " + (Snapshot.Failure is { } f ? Failure(f) : "No further output is available."),
            _ => "Logs closed."
        };
    public string RetentionMessage => $"{Snapshot.RetainedEntries} retained entries; {Snapshot.EvictedEntries} evicted; {Snapshot.DiscardedCharacters} characters discarded. "
        + (Snapshot.IsTty == true ? "TTY output is combined. " : "")
        + (Snapshot.Continuity == ContainerLogsContinuity.Initial ? "Recent tail only; full history is not loaded."
            : Snapshot.Continuity == ContainerLogsContinuity.BoundedOverlap ? "Recovery matched bounded overlap evidence; gap-free history is not guaranteed."
            : "Recovery continuity is uncertain; gaps or duplicates may remain.");
    public string SearchMessage => $"{Rows.Count} matching rows in this window (up to {PageSize} source entries). "
        + (scanning ? "Searching retained output… " : "")
        + "Search covers retained output; Older/Newer browse matching windows. " + ReadingMessage;

    public void Open(string id, bool follow = true)
    {
        Close();
        active = true; identity = id; Follow = follow; admission = null; ShowingDaemonContent = false;
        Query = ""; Stream = null; Autoscroll = true; windowAfter = 0; windowBefore = long.MaxValue; ReadingMessage = "";
        if (logs is null) { admission = new(ContainerLogFailureKind.InvalidRequest, "This connection does not provide Logs."); Notify(); return; }
        logs.ContainerLogsChanged += OnChanged; subscribed = true;
        if (!logs.OpenContainerLogs(id, out admission, follow)) { Notify(); return; }
        session = logs.ContainerLogs.SessionId;
        Snapshot = logs.ContainerLogs;
        StartScan(0); Schedule(); Notify();
    }
    public void Search(string query)
    {
        if (!active || Query == query) return;
        Query = query; Rows = Array.Empty<ContainerLogRow>(); ReadingMessage = "";
        StartScan(Autoscroll ? 0 : 1); Schedule(); Notify();
    }
    public void Filter(ContainerLogStream? stream)
    {
        if (!active) return;
        Stream = Snapshot.IsTty == true ? null : stream;
        Rows = Array.Empty<ContainerLogRow>(); StartScan(Autoscroll ? 0 : 1); Schedule(); Notify();
    }
    public void SetAutoscroll(bool enabled)
    {
        Autoscroll = enabled; ReadingMessage = "";
        if (enabled) { StartScan(0); Schedule(); }
        else { scanning = false; dirty = false; }
        Notify();
    }
    public void Older()
    {
        Autoscroll = false; previousLimit = Rows.FirstOrDefault()?.Entry.EntryId ?? long.MaxValue;
        StartScan(3); Schedule(); Notify();
    }
    public void Newer()
    {
        Autoscroll = false; StartScan(2); Schedule(); Notify();
    }
    public void ShowDaemonContent(bool show) { ShowingDaemonContent = show; Notify(); }
    public void Clear()
    {
        if (!Current()) return;
        // Invalidate all queued scans before the Application publication barrier.
        ++epoch; scanning = false; dirty = false;
        logs!.ClearContainerLogs(); Rows = Array.Empty<ContainerLogRow>(); windowAfter = 0; ReadingMessage = "Local view cleared.";
        Snapshot = logs.ContainerLogs; StartScan(Autoscroll ? 0 : 1); Schedule(); Notify();
    }
    private bool Current() => active && logs is not null && session != 0 && IsCurrentIdentity?.Invoke() != false
        && logs.ContainerLogs.SessionId == session && logs.ContainerLogs.ContainerId == identity;
    private void OnChanged(object? sender, EventArgs args) => Schedule();
    private void Schedule()
    {
        int current = Volatile.Read(ref epoch) + 1;
        if (Interlocked.CompareExchange(ref queued, current, 0) != 0) return;
        if (!dispatch(() =>
        {
            Interlocked.CompareExchange(ref queued, 0, current);
            if (!active) return;
            if (current != epoch + 1) { Schedule(); return; }
            Tick();
        })) Interlocked.CompareExchange(ref queued, 0, current);
    }
    private void StartScan(int mode)
    {
        if (!Current()) return;
        scanMode = mode; scanning = true; dirty = false; scanStarted = false;
        if (mode != 3) windowBefore = long.MaxValue;
        scanAfter = mode == 2 ? Rows.LastOrDefault()?.Entry.EntryId ?? windowAfter : mode == 4 ? windowAfter : 0;
        // Fixed work budget: producer activity does not cancel/restart a pass or extend it forever.
        scanRemaining = logs!.ContainerLogs.RetainedEntries + PageSize;
    }
    private ContainerLogsPage? ReadCurrent(long after)
    {
        var page = logs!.ReadContainerLogs(after, PageSize);
        var latest = logs.ContainerLogs;
        if (!Current() || page.SessionId != session || page.ViewRevision != latest.ViewRevision) return null;
        Snapshot = latest;
        if (Snapshot.IsTty == true) Stream = null;
        return page;
    }
    private IReadOnlyList<ContainerLogRow> Project(ContainerLogsPage page) => Array.AsReadOnly(page.Entries
        .Where(e => (Stream is null || e.Stream == Stream || Snapshot.IsTty == true)
            && e.Text.Contains(Query, StringComparison.OrdinalIgnoreCase)).Select(e => new ContainerLogRow(e)).ToArray());
    private void Tick()
    {
        if (!Current()) { Rows = Array.Empty<ContainerLogRow>(); scanning = false; Notify(); return; }
        var latest = logs!.ContainerLogs;
        dirty |= latest.ViewRevision != Snapshot.ViewRevision;
        if (latest.Failure != Snapshot.Failure) ShowingDaemonContent = false;
        // Refresh the exact window on every signal, including mutable partial EntryIds and Clear/eviction.
        var window = ReadCurrent(windowAfter);
        if (window is null) { Schedule(); return; }
        if (!Autoscroll && Rows.Count > 0 && (window.Entries.Count == 0 || window.Entries[0].EntryId > Rows[0].Entry.EntryId))
            ReadingMessage = "The reading position was evicted or cleared. Showing the next retained output.";
        SetRows(Array.AsReadOnly(Project(window).Where(r => r.Entry.EntryId < windowBefore).ToArray()));
        if (!scanning && dirty) { dirty = false; if (Autoscroll || Rows.Count == 0) StartScan(Autoscroll ? (Rows.Count == 0 ? 0 : 4) : 1); }
        if (scanning)
        {
            if (!scanStarted) { scanRemaining = Snapshot.RetainedEntries + PageSize; scanStarted = true; }
            // Cursor is just a search hint. Only a freshly read, current-revision page can be shown.
            var page = ReadCurrent(scanAfter);
            if (page is null) { Schedule(); return; }
            var matches = Project(page);
            bool eligible = scanMode != 3 || page.Entries.Count > 0 && page.Entries[0].EntryId < previousLimit;
            if (matches.Count > 0 && eligible)
            {
                if (scanMode == 3) matches = Array.AsReadOnly(matches.Where(r => r.Entry.EntryId < previousLimit).ToArray());
                if (matches.Count > 0) { windowAfter = scanAfter; windowBefore = scanMode == 3 ? previousLimit : long.MaxValue; SetRows(matches); }
                if (scanMode is 1 or 2) scanning = false;
            }
            scanRemaining -= page.Entries.Count;
            if (page.Entries.Count == 0 || scanRemaining <= 0 || scanMode == 3 && page.Entries[^1].EntryId >= previousLimit) scanning = false;
            else scanAfter = page.Entries[^1].EntryId;
        }
        Notify();
        if (scanning) Schedule();
    }
    private void SetRows(IReadOnlyList<ContainerLogRow> rows)
    {
        if (Rows.Count == rows.Count && Rows.Zip(rows).All(pair => pair.First.Entry == pair.Second.Entry)) return;
        Rows = rows;
    }
    public void Close()
    {
        bool owned = logs is not null && session != 0 && logs.ContainerLogs.SessionId == session;
        active = false; ++epoch;
        if (logs is not null && subscribed) logs.ContainerLogsChanged -= OnChanged;
        subscribed = false;
        session = 0; identity = null; scanning = dirty = false; Rows = Array.Empty<ContainerLogRow>();
        ShowingDaemonContent = false; admission = null; Query = ""; Stream = null; ReadingMessage = "";
        Snapshot = new(0, 0, ContainerLogsPhase.Closed);
        // Invoke close now, before returning. Awaiting its captured join never closes a later session.
        if (owned) Cleanup = JoinAsync(logs!.CloseContainerLogsAsync());
    }
    private static async Task JoinAsync(Task cleanup) => await cleanup.ConfigureAwait(false);
    public void Dispose() => Close();
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    private static string Failure(ContainerLogFailure failure) => failure.Kind switch
    {
        ContainerLogFailureKind.LoggingUnavailable => "Logging unavailable: this container's logging driver cannot provide readable logs.",
        ContainerLogFailureKind.AccessDenied => "Access denied while opening Logs.",
        ContainerLogFailureKind.NotFound => "The container is no longer available.",
        ContainerLogFailureKind.SubscriptionLimit => "Logs stream capacity is currently occupied. Try opening Logs again.",
        ContainerLogFailureKind.OpeningDeadline => "Logs opening exceeded its deadline.",
        ContainerLogFailureKind.TransportLoss => "Logs transport was interrupted.",
        ContainerLogFailureKind.MalformedOutput => "Logs output could not be decoded safely.",
        ContainerLogFailureKind.DaemonError => $"Daemon rejected Logs{(failure.HttpStatusCode is { } code ? $" (HTTP {code})" : "")}.",
        _ => "Logs requires a current container snapshot and a Logs-capable connection."
    };
}

public sealed record ContainerLogRow(ContainerLogEntry Entry)
{
    public string Text => Entry.Text;
    public string Metadata => $"{Entry.Stream.ToString().ToLowerInvariant()} · {Entry.Timestamp?.Original ?? "timestamp unavailable"}"
        + (Entry.Continued ? " · continued segment" : "") + (Entry.Partial ? " · partial" : "");
}
