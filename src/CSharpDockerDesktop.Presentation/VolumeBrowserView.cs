using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>Read-only, UI-dispatched projection of Application-owned volume evidence.</summary>
public sealed class VolumeBrowserView(IVolumeBrowser browser, Func<Action, bool> dispatch) : IDisposable
{
    private bool active;
    private int epoch, notificationQueued;
    private long filterRevision, overviewRevision, evidenceRevision;
    private CancellationTokenSource? filtering, overviewWork, evidenceWork;
    private IReadOnlyList<VolumeSummary>? filteredSource, rowsSource;
    private IReadOnlyList<VolumeRow> rows = Array.Empty<VolumeRow>();
    private string filteredQuery = "", overview = "", evidence = "";
    private VolumeInspection? overviewSource;
    public JsonInspectView Inspect { get; } = new(dispatch);
    public event EventHandler? Changed;
    public VolumeBrowsingSnapshot Snapshot { get; private set; } = new(0, ResourceReadState.Unavailable, []);
    public IReadOnlyList<VolumeRow> Rows => CurrentGeneration(browser.VolumeSnapshot) && ReferenceEquals(rowsSource, browser.VolumeSnapshot.Volumes) ? rows : Array.Empty<VolumeRow>();
    public string Query { get; private set; } = "";
    public string? SelectedName { get; private set; }
    public bool DetailOpen { get; private set; }
    public bool ShowingInspect { get; private set; }
    public bool Filtering { get; private set; }
    public bool ShowingEvidence { get; private set; }
    public bool PreparingEvidence => evidenceWork is not null;
    public bool CanRefresh => CurrentGeneration(browser.VolumeSnapshot);
    public bool CanOpen => CurrentIdentity(browser.VolumeSnapshot);
    public bool HasEvidence => Snapshot.LatestListing is not null || Snapshot.Failure is not null || Snapshot.InspectionFailure is not null;
    public string Evidence => ShowingEvidence && CurrentEvidence(browser.VolumeSnapshot) ? evidence : "";
    public string Overview => CurrentInspection(browser.VolumeSnapshot) && ReferenceEquals(overviewSource, Snapshot.Inspection) ? overview : "";
    public string WarningMessage => Snapshot.LatestListing is null ? "No accepted volume listing yet."
        : Snapshot.Warnings is null ? "Daemon warnings: unavailable (null collection)."
        : Snapshot.Warnings.Count == 0 ? "Daemon warnings: empty collection."
        : $"Daemon warnings: {Snapshot.Warnings.Count}. Show original evidence to read them (may be sensitive).";
    public string ListMessage
    {
        get
        {
            var s = Snapshot;
            bool complete = s.IsAuthoritative && s.LatestListing?.Volumes is not null && !s.HasWarnings;
            string retained = s.HasRetainedRows ? " Retained rows remain browsable." : "";
            string message = s.State switch
            {
                ResourceReadState.Loading => (s.Volumes.Count == 0 ? "Loading volumes…" : "Refreshing volumes…") + retained,
                ResourceReadState.NotRequested => "Volumes have not been requested. Choose Refresh volumes.",
                ResourceReadState.Failed => "Volume refresh failed. Choose Refresh volumes to retry." + retained,
                ResourceReadState.Unavailable when !Usable(s) => "Volumes unavailable. Connection status and recovery are shown above.",
                _ when s.Completeness == VolumeListCompleteness.Unavailable => "Volume listing unavailable; results are not complete." + retained,
                _ when !complete => "Incomplete volume listing; absence is not established." + retained,
                _ when s.Volumes.Count == 0 && s.LatestListing?.Volumes is { Count: 0 } => "No volumes on this Engine (complete authoritative listing).",
                _ => "Complete authoritative volume listing."
            };
            if (Filtering) message += " Searching loaded volume metadata…";
            else if (s.Volumes.Count > 0) message += Rows.Count == 0 ? " No loaded volumes match your search." : $" {Rows.Count} of {s.Volumes.Count} loaded volumes.";
            message += FailureSuffix(s.Failure);
            if (!DetailOpen && s.InspectionFailure is not null) message += FailureSuffix(s.InspectionFailure);
            return message;
        }
    }
    public string DetailMessage => Snapshot.InspectionState switch
    {
        ResourceReadState.Loading => $"Loading {(ShowingInspect ? "Inspect" : "Overview")}…",
        ResourceReadState.Failed => "Volume details failed. Choose Refresh details to retry." + FailureSuffix(Snapshot.InspectionFailure),
        ResourceReadState.Ready => ShowingInspect ? "Raw volume Inspect · may contain sensitive values" : overviewWork is not null ? "Preparing volume Overview…" : "Volume Overview",
        ResourceReadState.Unavailable => "Volume details unavailable. Check connection status above.",
        _ => "Choose Refresh details to load details."
    };
    // Unavailable is also the valid result of a null collection on a usable connection.
    // Application invalidation discards both rows and LatestListing, even within the same generation.
    private static bool Usable(VolumeBrowsingSnapshot s) => s.State != ResourceReadState.Unavailable || s.LatestListing is not null || s.HasRetainedRows && s.Volumes.Count > 0;
    private bool CurrentGeneration(VolumeBrowsingSnapshot latest) => active && Usable(latest) && latest.ConnectionGeneration == Snapshot.ConnectionGeneration;
    private bool OwnsInspection(VolumeBrowsingSnapshot latest) => DetailOpen && latest.ConnectionGeneration == Snapshot.ConnectionGeneration
        && SelectedName is not null && StringComparer.Ordinal.Equals(latest.SelectedVolumeName, SelectedName);
    private bool CurrentIdentity(VolumeBrowsingSnapshot latest) => CurrentGeneration(latest) && SelectedName is not null
        && latest.Volumes.Any(row => StringComparer.Ordinal.Equals(row.Name, SelectedName))
        && (!DetailOpen || StringComparer.Ordinal.Equals(latest.SelectedVolumeName, SelectedName));
    private bool CurrentInspection(VolumeBrowsingSnapshot latest) => DetailOpen && CurrentIdentity(latest)
        && latest.InspectionState == ResourceReadState.Ready && StringComparer.Ordinal.Equals(latest.Inspection?.Name, SelectedName)
        && ReferenceEquals(latest.Inspection, Snapshot.Inspection);
    private bool CurrentEvidence(VolumeBrowsingSnapshot latest) => active && latest.ConnectionGeneration == Snapshot.ConnectionGeneration
        && ReferenceEquals(latest.LatestListing, Snapshot.LatestListing) && ReferenceEquals(latest.Failure, Snapshot.Failure)
        && ReferenceEquals(latest.InspectionFailure, Snapshot.InspectionFailure);

    public void Attach()
    {
        if (active) return;
        active = true; Interlocked.Increment(ref epoch);
        browser.VolumesChanged += OnChanged; Inspect.Changed += OnInspectChanged;
        Inspect.IsCurrentContent = raw => CurrentInspection(browser.VolumeSnapshot) && ReferenceEquals(Snapshot.Inspection?.RawJson, raw);
        ApplyLatest();
    }
    public void Dispose()
    {
        if (!active) return;
        bool clear = OwnsInspection(browser.VolumeSnapshot);
        active = false; Interlocked.Increment(ref epoch); Interlocked.Exchange(ref notificationQueued, 0);
        browser.VolumesChanged -= OnChanged; Inspect.Changed -= OnInspectChanged; Inspect.Dispose();
        CancelFilter(); ClearOverview(); HideEvidence(); filteredSource = rowsSource = null;
        DetailOpen = ShowingInspect = false; SelectedName = null; rows = Array.Empty<VolumeRow>();
        Snapshot = new(0, ResourceReadState.Unavailable, []);
        if (clear) browser.ClearVolumeInspection();
    }
    private void OnInspectChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    private void OnChanged(object? sender, EventArgs args)
    {
        if (!Volatile.Read(ref active)) return;
        int current = Volatile.Read(ref epoch);
        if (Interlocked.CompareExchange(ref notificationQueued, current, 0) != 0) return;
        if (!dispatch(() =>
        {
            Interlocked.CompareExchange(ref notificationQueued, 0, current);
            if (active && current == Volatile.Read(ref epoch)) ApplyLatest();
        })) Interlocked.CompareExchange(ref notificationQueued, 0, current);
    }
    private void ApplyLatest()
    {
        var latest = browser.VolumeSnapshot;
        bool replaced = latest.ConnectionGeneration != Snapshot.ConnectionGeneration || !Usable(latest);
        bool disappeared = SelectedName is not null && !latest.Volumes.Any(row => StringComparer.Ordinal.Equals(row.Name, SelectedName));
        bool clear = !replaced && disappeared && OwnsInspection(latest);
        if (replaced || disappeared) { SelectedName = null; DetailOpen = false; }
        if (replaced) { rows = Array.Empty<VolumeRow>(); rowsSource = filteredSource = null; CancelFilter(); }
        if (DetailOpen && !StringComparer.Ordinal.Equals(latest.SelectedVolumeName, SelectedName)) { DetailOpen = false; SelectedName = null; }
        if (!CurrentEvidence(latest)) HideEvidence();
        Snapshot = latest;
        if (!DetailOpen) ShowingInspect = false;
        SyncDetails(); Filter();
        if (clear) browser.ClearVolumeInspection();
    }
    private void SyncDetails()
    {
        if (!CurrentInspection(browser.VolumeSnapshot)) { Inspect.Load(null); ClearOverview(); return; }
        var inspection = Snapshot.Inspection!;
        if (ShowingInspect) Inspect.Load(inspection.RawJson);
        if (ReferenceEquals(overviewSource, inspection)) return;
        ClearOverview(); overviewSource = inspection;
        var cancel = overviewWork = new(); var token = cancel.Token;
        long revision = overviewRevision; int lifetime = epoch;
        _ = Task.Run(() =>
        {
            try
            {
                string text = VolumeDisplay.Overview(inspection, token);
                dispatch(() =>
                {
                    if (!active || epoch != lifetime || overviewRevision != revision || token.IsCancellationRequested || !CurrentInspection(browser.VolumeSnapshot)) return;
                    overview = text; overviewWork?.Dispose(); overviewWork = null; Changed?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) { }
        });
    }
    private void ClearOverview()
    { ++overviewRevision; overviewWork?.Cancel(); overviewWork?.Dispose(); overviewWork = null; overviewSource = null; overview = ""; }
    public void ShowInspect(bool show)
    {
        if (!active || !DetailOpen) return;
        ApplyLatest(); if (!DetailOpen) return;
        ShowingInspect = show; SyncDetails(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Search(string query) { if (active && Query != query) { Query = query; Filter(); } }
    public void Select(string? name)
    {
        var latest = browser.VolumeSnapshot;
        if (!CurrentGeneration(latest) || name is null || !latest.Volumes.Any(row => StringComparer.Ordinal.Equals(row.Name, name)) || StringComparer.Ordinal.Equals(SelectedName, name)) return;
        bool clear = OwnsInspection(latest);
        DetailOpen = ShowingInspect = false; Inspect.Load(null); ClearOverview(); SelectedName = name;
        if (clear) browser.ClearVolumeInspection();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void OpenSelected()
    {
        if (!CanOpen || DetailOpen) return;
        browser.SelectVolume(SelectedName);
        // ClearInspection retains Application selection; re-opening the same name needs an explicit refresh.
        var latest = browser.VolumeSnapshot;
        if (StringComparer.Ordinal.Equals(latest.SelectedVolumeName, SelectedName) && latest.InspectionState == ResourceReadState.NotRequested) browser.RefreshVolumeInspection();
        DetailOpen = true; ShowingInspect = false; ApplyLatest();
    }
    public void CloseDetail()
    {
        if (!active || !DetailOpen) return;
        bool clear = OwnsInspection(browser.VolumeSnapshot);
        DetailOpen = ShowingInspect = false; Inspect.Load(null); ClearOverview();
        if (clear) browser.ClearVolumeInspection(); ApplyLatest();
    }
    public void Refresh() { if (CanRefresh) browser.RefreshVolumes(); }
    public void RefreshDetail()
    {
        if (!DetailOpen || !CanOpen) return;
        Inspect.Load(null); ClearOverview(); browser.RefreshVolumeInspection();
    }
    public bool CopyIdentity(Action<string> publish)
    {
        if (!CanOpen) return false;
        try { publish(SelectedName!); return true; } catch (Exception) { return false; }
    }
    public void ShowEvidence(bool show)
    {
        if (!active) return;
        HideEvidence();
        if (!show || !active || !HasEvidence) { Changed?.Invoke(this, EventArgs.Empty); return; }
        // Capture the current immutable evidence only through deliberate user reveal.
        ApplyLatest(); if (!HasEvidence || !CurrentEvidence(browser.VolumeSnapshot)) return;
        ShowingEvidence = true;
        var captured = Snapshot; var cancel = evidenceWork = new(); var token = cancel.Token;
        long revision = evidenceRevision; int lifetime = epoch;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() =>
        {
            try
            {
                string text = VolumeDisplay.Evidence(captured, token);
                dispatch(() =>
                {
                    if (!active || epoch != lifetime || revision != evidenceRevision || token.IsCancellationRequested || !CurrentEvidence(browser.VolumeSnapshot)) return;
                    evidence = text; evidenceWork?.Dispose(); evidenceWork = null; Changed?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) { }
        });
    }
    private void HideEvidence()
    { ++evidenceRevision; evidenceWork?.Cancel(); evidenceWork?.Dispose(); evidenceWork = null; ShowingEvidence = false; evidence = ""; }
    private void CancelFilter()
    { ++filterRevision; filtering?.Cancel(); filtering?.Dispose(); filtering = null; Filtering = false; }
    private void Filter()
    {
        if (!Usable(Snapshot)) { CancelFilter(); filteredSource = rowsSource = null; rows = Array.Empty<VolumeRow>(); Changed?.Invoke(this, EventArgs.Empty); return; }
        if (ReferenceEquals(filteredSource, Snapshot.Volumes) && filteredQuery == Query) { Changed?.Invoke(this, EventArgs.Empty); return; }
        CancelFilter(); var cancel = filtering = new(); var token = cancel.Token;
        var source = Snapshot.Volumes; string query = Query;
        long revision = filterRevision; int lifetime = epoch; long generation = Snapshot.ConnectionGeneration;
        filteredSource = source; filteredQuery = query; Filtering = true; Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() =>
        {
            try
            {
                var result = new List<VolumeRow>();
                foreach (var summary in source)
                { token.ThrowIfCancellationRequested(); if (VolumeDisplay.Matches(summary, query, token)) result.Add(new(summary, token)); }
                dispatch(() =>
                {
                    var latest = browser.VolumeSnapshot;
                    if (!active || lifetime != epoch || revision != filterRevision || token.IsCancellationRequested || !CurrentGeneration(latest)
                        || latest.ConnectionGeneration != generation || !ReferenceEquals(latest.Volumes, source)) return;
                    rows = result.AsReadOnly(); rowsSource = source; Filtering = false; Changed?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) { }
        });
    }
    private static string FailureSuffix(EngineResourceFailure? failure) => failure is null ? "" : $" {ContainerDisplay.Failure(failure)}";
}

public sealed class VolumeRow
{
    public string Name { get; }
    public string Driver { get; }
    public string Scope { get; }
    public string Mountpoint { get; }
    public string Labels { get; }
    public string AccessibleSummary { get; }
    public VolumeRow(VolumeSummary summary, CancellationToken token = default)
    {
        Name = summary.Name; Driver = VolumeDisplay.Cell(summary.Driver); Scope = VolumeDisplay.Cell(summary.Scope);
        Mountpoint = VolumeDisplay.Cell(summary.Mountpoint); Labels = VolumeDisplay.Labels(summary.Labels, token, true);
        AccessibleSummary = $"Name: {Name}, driver: {Driver}, scope: {Scope}, mountpoint: {Mountpoint}, labels: {Labels}";
    }
}
public static class VolumeDisplay
{
    public static string Text(string? value) => value is null ? "unavailable (null)" : value.Length == 0 ? "empty string" : value;
    public static string Cell(string? value) => value is { Length: > 240 } ? value[..240] + "… (full value in Overview/Inspect)" : Text(value);
    public static string Labels(IReadOnlyDictionary<string, string>? labels, CancellationToken token = default, bool compact = false)
    {
        if (labels is null) return "unavailable (null)";
        if (labels.Count == 0) return "none (empty collection)";
        var text = new StringBuilder(); int count = 0;
        foreach (var label in labels)
        {
            token.ThrowIfCancellationRequested();
            if (compact && count++ == 4) { text.Append("… (full labels in Overview/Inspect)"); break; }
            if (text.Length > 0) text.Append(compact ? "; " : "\n");
            text.Append(compact ? Cell(label.Key) : Text(label.Key)).Append(": ").Append(compact ? Cell(label.Value) : Text(label.Value));
        }
        return text.ToString();
    }
    public static bool Matches(VolumeSummary summary, string query, CancellationToken token = default)
    {
        bool Has(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        if (query.Length == 0 || Has(summary.Name) || Has(summary.Driver) || Has(summary.Scope) || Has(summary.Mountpoint)) return true;
        if (summary.Labels is not null) foreach (var label in summary.Labels)
        { token.ThrowIfCancellationRequested(); if (Has(label.Key) || Has(label.Value)) return true; }
        return false;
    }
    public static string Overview(VolumeInspection volume, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return $"Name: {volume.Name}\nDriver: {Text(volume.Driver)}\nScope: {Text(volume.Scope)}\nMountpoint: {Text(volume.Mountpoint)}\n\nLabels · may contain sensitive values\n{Labels(volume.Labels, token)}";
    }
    public static string Evidence(VolumeBrowsingSnapshot snapshot, CancellationToken token = default)
    {
        var text = new StringBuilder("Original daemon evidence · may contain sensitive content\nWarning order, duplicates and text are preserved. Character counts distinguish empty content.\n\n");
        if (snapshot.LatestListing is null) text.AppendLine("Listing: no accepted listing.");
        else
        {
            text.AppendLine(snapshot.LatestListing.Volumes is null ? "Latest Volumes: null (unavailable)." : $"Latest Volumes: {snapshot.LatestListing.Volumes.Count} returned rows.");
            if (snapshot.Warnings is null) text.AppendLine("Warnings: null (unavailable collection).");
            else if (snapshot.Warnings.Count == 0) text.AppendLine("Warnings: empty collection.");
            else for (int i = 0; i < snapshot.Warnings.Count; i++)
            { token.ThrowIfCancellationRequested(); Original($"Warning {i + 1}", snapshot.Warnings[i]); }
        }
        if (snapshot.Failure is { } failure) { text.AppendLine("List failure: " + ContainerDisplay.Failure(failure)); Original("List daemon message", failure.DaemonMessage); }
        if (snapshot.InspectionFailure is { } detail) { text.AppendLine("Inspection failure: " + ContainerDisplay.Failure(detail)); Original("Inspection daemon message", detail.DaemonMessage); }
        return text.ToString();
        void Original(string label, string? content)
        {
            token.ThrowIfCancellationRequested(); text.Append(label).Append(content is null ? ": null (unavailable)\n" : $" · {content.Length} characters:\n");
            if (content is not null) text.Append(content).AppendLine().AppendLine();
        }
    }
}
