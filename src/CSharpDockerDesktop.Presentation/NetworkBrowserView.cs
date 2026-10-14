using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>UI-dispatched projection of Application-owned network browsing and deletion evidence.</summary>
public sealed partial class NetworkBrowserView(INetworkBrowser browser, Func<Action, bool> dispatch) : IDisposable
{
    private bool active;
    private int epoch, notificationQueued;
    private long filterRevision, overviewRevision, evidenceRevision;
    private CancellationTokenSource? filtering, overviewWork, evidenceWork;
    private IReadOnlyList<NetworkSummary>? filteredSource, rowsSource;
    private IReadOnlyList<NetworkRow> rows = Array.Empty<NetworkRow>();
    private string filteredQuery = "", overview = "", evidence = "";
    private NetworkInspection? overviewSource;
    public JsonInspectView Inspect { get; } = new(dispatch);
    public event EventHandler? Changed;
    public NetworkBrowsingSnapshot Snapshot { get; private set; } = new(0, ResourceReadState.Unavailable, []);
    public IReadOnlyList<NetworkRow> Rows => CurrentGeneration(browser.NetworkSnapshot) && ReferenceEquals(rowsSource, browser.NetworkSnapshot.Networks) ? rows : Array.Empty<NetworkRow>();
    public string Query { get; private set; } = "";
    public string? SelectedId { get; private set; }
    public bool DetailOpen { get; private set; }
    public bool ShowingInspect { get; private set; }
    public bool Filtering { get; private set; }
    public bool ShowingEvidence { get; private set; }
    public bool PreparingEvidence => evidenceWork is not null;
    public bool CanRefresh => CurrentGeneration(browser.NetworkSnapshot);
    public bool CanOpen => CurrentIdentity(browser.NetworkSnapshot);
    public bool HasEvidence => Snapshot.Failure is not null || Snapshot.InspectionFailure is not null;
    public string Evidence => ShowingEvidence && CurrentEvidence(browser.NetworkSnapshot) ? evidence : "";
    public string Overview => CurrentInspection(browser.NetworkSnapshot) && ReferenceEquals(overviewSource, Snapshot.Inspection) ? overview : "";
    public string ListMessage
    {
        get
        {
            var s = Snapshot;
            string message = s.State switch
            {
                ResourceReadState.Loading => s.Networks.Count == 0 ? "Loading networks…" : "Refreshing networks… Retained rows remain browsable.",
                ResourceReadState.NotRequested => "Networks have not been requested. Choose Refresh networks.",
                ResourceReadState.Failed => "Network refresh failed. Choose Refresh networks to retry." + (s.HasRetainedRows ? " Retained rows remain browsable." : ""),
                ResourceReadState.Unavailable => "Networks unavailable. Connection status and recovery are shown above.",
                _ when s.IsAuthoritative && s.Networks.Count == 0 => "No networks on this Engine (authoritative listing).",
                _ => "Authoritative network listing."
            };
            if (Filtering) message += " Searching loaded network metadata…";
            else if (s.Networks.Count > 0) message += Rows.Count == 0 ? " No loaded networks match your search." : $" {Rows.Count} of {s.Networks.Count} loaded networks.";
            message += FailureSuffix(s.Failure);
            if (!DetailOpen) message += FailureSuffix(s.InspectionFailure);
            return message;
        }
    }
    public string DetailMessage => Snapshot.InspectionState switch
    {
        ResourceReadState.Loading => $"Loading {(ShowingInspect ? "Inspect" : "Overview")}…",
        ResourceReadState.Failed => "Network details failed. Choose Refresh details to retry." + FailureSuffix(Snapshot.InspectionFailure),
        ResourceReadState.Ready => ShowingInspect ? "Raw network Inspect · may contain sensitive values" : overviewWork is not null ? "Preparing network Overview…" : "Network Overview",
        ResourceReadState.Unavailable => "Network details unavailable. Check connection status above.",
        _ => "Choose Refresh details to load details."
    };
    private static bool Usable(NetworkBrowsingSnapshot s) => s.State != ResourceReadState.Unavailable;
    private bool CurrentGeneration(NetworkBrowsingSnapshot latest) => active && Usable(latest) && latest.ConnectionGeneration == Snapshot.ConnectionGeneration;
    private bool OwnsInspection(NetworkBrowsingSnapshot latest) => DetailOpen && latest.ConnectionGeneration == Snapshot.ConnectionGeneration
        && SelectedId is not null && StringComparer.Ordinal.Equals(latest.SelectedNetworkId, SelectedId);
    private bool CurrentIdentity(NetworkBrowsingSnapshot latest) => CurrentGeneration(latest) && SelectedId is not null
        && latest.Networks.Any(row => StringComparer.Ordinal.Equals(row.Id, SelectedId))
        && (!DetailOpen || StringComparer.Ordinal.Equals(latest.SelectedNetworkId, SelectedId));
    private bool CurrentInspection(NetworkBrowsingSnapshot latest) => DetailOpen && CurrentIdentity(latest)
        && latest.InspectionState == ResourceReadState.Ready && StringComparer.Ordinal.Equals(latest.Inspection?.Id, SelectedId)
        && ReferenceEquals(latest.Inspection, Snapshot.Inspection);
    private bool CurrentEvidence(NetworkBrowsingSnapshot latest) => active && latest.ConnectionGeneration == Snapshot.ConnectionGeneration
        && ReferenceEquals(latest.Failure, Snapshot.Failure)
        && ReferenceEquals(latest.InspectionFailure, Snapshot.InspectionFailure);

    public void Attach()
    {
        if (active) return;
        active = true; Interlocked.Increment(ref epoch);
        browser.NetworksChanged += OnChanged; Inspect.Changed += OnInspectChanged;
        if (preparationService is not null) preparationService.NetworkDeletionPreparationChanged += OnChanged;
        if (operations is not null) operations.NetworkOperationChanged += OnChanged;
        Inspect.IsCurrentContent = raw => CurrentInspection(browser.NetworkSnapshot) && ReferenceEquals(Snapshot.Inspection?.RawJson, raw);
        ApplyLatest();
    }
    public void Dispose()
    {
        if (!active) return;
        bool clear = OwnsInspection(browser.NetworkSnapshot);
        active = false; Interlocked.Increment(ref epoch); Interlocked.Exchange(ref notificationQueued, 0);
        browser.NetworksChanged -= OnChanged; Inspect.Changed -= OnInspectChanged; Inspect.Dispose();
        if (preparationService is not null) preparationService.NetworkDeletionPreparationChanged -= OnChanged;
        if (operations is not null) operations.NetworkOperationChanged -= OnChanged;
        CloseDeletion(); HideOperationDetails(); operation = null; attachmentIndexes.Clear(); preparationRows = operationRows = operationPreparationRows = null;
        submitting = false; AdmissionMessage = "";
        CancelFilter(); ClearOverview(); HideEvidence(); filteredSource = rowsSource = null;
        DetailOpen = ShowingInspect = false; SelectedId = null; rows = Array.Empty<NetworkRow>();
        Snapshot = new(0, ResourceReadState.Unavailable, []);
        if (clear) browser.ClearNetworkInspection();
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
        var latest = browser.NetworkSnapshot;
        bool replaced = latest.ConnectionGeneration != Snapshot.ConnectionGeneration || !Usable(latest);
        bool disappeared = SelectedId is not null && !latest.Networks.Any(row => StringComparer.Ordinal.Equals(row.Id, SelectedId));
        bool clear = !replaced && disappeared && OwnsInspection(latest);
        if (replaced || disappeared) { SelectedId = null; DetailOpen = false; }
        if (replaced) { rows = Array.Empty<NetworkRow>(); rowsSource = filteredSource = null; CancelFilter(); }
        if (DetailOpen && !StringComparer.Ordinal.Equals(latest.SelectedNetworkId, SelectedId)) { DetailOpen = false; SelectedId = null; }
        if (!CurrentEvidence(latest)) HideEvidence();
        Snapshot = latest;
        if (!DetailOpen) ShowingInspect = false;
        SyncDeletion(); SyncOperation(); SyncDetails(); Filter();
        if (clear) browser.ClearNetworkInspection();
    }
    private void SyncDetails()
    {
        if (!CurrentInspection(browser.NetworkSnapshot)) { Inspect.Load(null); ClearOverview(); return; }
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
                string text = NetworkDisplay.Overview(inspection, token);
                dispatch(() =>
                {
                    if (!active || epoch != lifetime || overviewRevision != revision || token.IsCancellationRequested || !CurrentInspection(browser.NetworkSnapshot)) return;
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
        var latest = browser.NetworkSnapshot;
        if (!CurrentGeneration(latest) || name is null || !latest.Networks.Any(row => StringComparer.Ordinal.Equals(row.Id, name)) || StringComparer.Ordinal.Equals(SelectedId, name)) return;
        bool clear = OwnsInspection(latest);
        CloseDeletion();
        DetailOpen = ShowingInspect = false; Inspect.Load(null); ClearOverview(); SelectedId = name;
        if (clear) browser.ClearNetworkInspection();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void OpenSelected()
    {
        if (!CanOpen || DetailOpen) return;
        browser.SelectNetwork(SelectedId);
        // ClearInspection retains Application selection; re-opening the same ID needs an explicit refresh.
        var latest = browser.NetworkSnapshot;
        if (StringComparer.Ordinal.Equals(latest.SelectedNetworkId, SelectedId) && latest.InspectionState == ResourceReadState.NotRequested) browser.RefreshNetworkInspection();
        DetailOpen = true; ShowingInspect = false; ApplyLatest();
    }
    public void CloseDetail()
    {
        if (!active || !DetailOpen) return;
        bool clear = OwnsInspection(browser.NetworkSnapshot);
        CloseDeletion();
        DetailOpen = ShowingInspect = false; Inspect.Load(null); ClearOverview();
        if (clear) browser.ClearNetworkInspection(); ApplyLatest();
    }
    public void Refresh() { if (CanRefresh) browser.RefreshNetworks(); }
    public void RefreshDetail()
    {
        if (!DetailOpen || !CanOpen) return;
        Inspect.Load(null); ClearOverview(); browser.RefreshNetworkInspection();
    }
    public bool CopyIdentity(Action<string> publish)
    {
        if (!CanOpen) return false;
        try { publish(SelectedId!); return true; } catch (Exception) { return false; }
    }
    public void ShowEvidence(bool show)
    {
        if (!active) return;
        HideEvidence();
        if (!show || !active || !HasEvidence) { Changed?.Invoke(this, EventArgs.Empty); return; }
        // Capture the current immutable evidence only through deliberate user reveal.
        ApplyLatest(); if (!HasEvidence || !CurrentEvidence(browser.NetworkSnapshot)) return;
        ShowingEvidence = true;
        var captured = Snapshot; var cancel = evidenceWork = new(); var token = cancel.Token;
        long revision = evidenceRevision; int lifetime = epoch;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() =>
        {
            try
            {
                string text = NetworkDisplay.Evidence(captured, token);
                dispatch(() =>
                {
                    if (!active || epoch != lifetime || revision != evidenceRevision || token.IsCancellationRequested || !CurrentEvidence(browser.NetworkSnapshot)) return;
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
        if (!Usable(Snapshot)) { CancelFilter(); filteredSource = rowsSource = null; rows = Array.Empty<NetworkRow>(); Changed?.Invoke(this, EventArgs.Empty); return; }
        if (ReferenceEquals(filteredSource, Snapshot.Networks) && filteredQuery == Query) { Changed?.Invoke(this, EventArgs.Empty); return; }
        CancelFilter(); var cancel = filtering = new(); var token = cancel.Token;
        var source = Snapshot.Networks; string query = Query;
        long revision = filterRevision; int lifetime = epoch; long generation = Snapshot.ConnectionGeneration;
        filteredSource = source; filteredQuery = query; Filtering = true; Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() =>
        {
            try
            {
                var result = new List<NetworkRow>();
                foreach (var summary in source)
                { token.ThrowIfCancellationRequested(); if (NetworkDisplay.Matches(summary, query, token)) result.Add(new(summary, token)); }
                dispatch(() =>
                {
                    var latest = browser.NetworkSnapshot;
                    if (!active || lifetime != epoch || revision != filterRevision || token.IsCancellationRequested || !CurrentGeneration(latest)
                        || latest.ConnectionGeneration != generation || !ReferenceEquals(latest.Networks, source)) return;
                    rows = result.AsReadOnly(); rowsSource = source; Filtering = false; Changed?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) { }
        });
    }
    private static string FailureSuffix(EngineResourceFailure? failure) => failure is null ? "" : $" {ContainerDisplay.Failure(failure)}";
}

public sealed class NetworkRow
{
    public string Id { get; }
    public string ShortId { get; }
    public string Name { get; }
    public string DriverScope { get; }
    public string Internal { get; }
    public string AccessibleSummary { get; }
    public NetworkRow(NetworkSummary summary, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); Id = summary.Id;
        ShortId = NetworkDisplay.Cell(Id.Length > 12 ? Id[..12] + "…" : Id);
        Name = NetworkDisplay.Cell(summary.Name);
        DriverScope = NetworkDisplay.Cell(summary.Driver) + " / " + NetworkDisplay.Cell(summary.Scope);
        Internal = NetworkDisplay.Flag(summary.Internal);
        AccessibleSummary = $"Name: {Name}, ID: {ShortId}, driver / scope: {DriverScope}, internal: {Internal}. Copy full ID is available.";
    }
}
public static class NetworkDisplay
{
    public const int OverviewLimit = 65536;
    private const int CollectionLimit = 100;
    public static string Text(string? value) => value is null ? "unavailable (null)" : value.Length == 0 ? "empty string" : value;
    public static string Cell(string? value) => value is { Length: > 160 } ? value[..160] + "… (full value in Inspect)" : Text(value);
    public static string Flag(bool? value) => value switch { true => "true", false => "false", _ => "unavailable (null)" };
    public static bool Matches(NetworkSummary summary, string query, CancellationToken token = default)
    {
        bool Has(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        if (query.Length == 0 || Has(summary.Id) || Has(summary.Name) || Has(summary.Driver) || Has(summary.Scope) || Has(Flag(summary.Internal))) return true;
        if (summary.Labels is not null) foreach (var label in summary.Labels)
        { token.ThrowIfCancellationRequested(); if (Has(label.Key) || Has(label.Value)) return true; }
        return false;
    }
    public static string Overview(NetworkInspection network, CancellationToken token = default)
    {
        // Bounded text avoids constructing an unbounded native text layout. Full evidence remains in virtualized Inspect.
        var text = new StringBuilder(); bool truncated = false;
        void Add(string value)
        {
            token.ThrowIfCancellationRequested();
            int available = OverviewLimit - text.Length;
            if (value.Length + 1 > available) { if (available > 0) text.Append(value.AsSpan(0, Math.Min(value.Length, available))); truncated = true; }
            else text.AppendLine(value);
        }
        void Field(string name, string? value)
        {
            token.ThrowIfCancellationRequested();
            // Bound before concatenation, including unusually large daemon-provided values.
            if (value is { Length: > OverviewLimit }) { value = value[..OverviewLimit]; truncated = true; }
            Add(name + ": " + Text(value));
        }
        void Map(string name, IReadOnlyDictionary<string, string>? values)
        {
            if (values is null || values.Count == 0) { Field(name, values is null ? null : "none (empty collection)"); return; }
            Add(name + $" · {values.Count} entries:"); int count = 0;
            foreach (var pair in values)
            {
                token.ThrowIfCancellationRequested();
                if (count++ >= CollectionLimit || text.Length >= OverviewLimit) { truncated = true; break; }
                Field(Cell(pair.Key), pair.Value);
            }
        }
        Add("Inspected attachment observation only. This does not establish cluster-wide completeness or deletion eligibility.");
        Field("Full identity", network.Id); Field("Name", network.Name); Field("Driver", network.Driver); Field("Scope", network.Scope); Field("Internal", Flag(network.Internal));
        Map("Labels · may contain sensitive values", network.Labels);
        Add("\nIPAM");
        if (network.Ipam is not { } ipam) Field("IPAM", null);
        else
        {
            Field("Driver", ipam.Driver); Map("Options", ipam.Options);
            if (ipam.Config is null || ipam.Config.Count == 0) Field("Configurations", ipam.Config is null ? null : "none (empty collection)");
            else
            {
                Add($"Configurations · {ipam.Config.Count} entries:"); int count = 0;
                foreach (var config in ipam.Config)
                {
                    token.ThrowIfCancellationRequested();
                    if (count++ >= CollectionLimit || text.Length >= OverviewLimit) { truncated = true; break; }
                    Add($"Configuration {count}"); Field("Subnet", config.Subnet); Field("IP range", config.IPRange); Field("Gateway", config.Gateway); Map("Auxiliary addresses", config.AuxiliaryAddresses);
                }
            }
        }
        Add("\nContainers / endpoints · inspected observation");
        if (network.Containers is null || network.Containers.Count == 0) Field("Attachments", network.Containers is null ? null : "none (empty collection)");
        else
        {
            Add($"Attachments · {network.Containers.Count} entries:"); int count = 0;
            foreach (var attachment in network.Containers)
            {
                token.ThrowIfCancellationRequested();
                if (count++ >= CollectionLimit || text.Length >= OverviewLimit) { truncated = true; break; }
                Field("Full attachment / container identity", attachment.Key);
                Field("Name", attachment.Value.Name); Field("Full endpoint identity", attachment.Value.EndpointId);
                Field("MAC address", attachment.Value.MacAddress); Field("IPv4 address", attachment.Value.IPv4Address); Field("IPv6 address", attachment.Value.IPv6Address);
            }
        }
        if (truncated) text.AppendLine("\nOverview preview is bounded to 100 entries per collection and 65,536 characters. Use Inspect to search and copy full returned evidence.");
        return text.ToString();
    }
    public static string Evidence(NetworkBrowsingSnapshot snapshot, CancellationToken token = default)
    {
        var text = new StringBuilder("Original daemon messages · may contain sensitive content\n");
        Original("List", snapshot.Failure); Original("Inspection", snapshot.InspectionFailure); return text.ToString();
        void Original(string label, EngineResourceFailure? failure)
        {
            token.ThrowIfCancellationRequested(); if (failure is null) return;
            text.AppendLine(label + ": " + ContainerDisplay.Failure(failure));
            text.AppendLine(failure.DaemonMessage is null ? "Original message: unavailable (null)" : $"Original message · {failure.DaemonMessage.Length} characters:");
            if (failure.DaemonMessage is not null) text.Append(failure.DaemonMessage).AppendLine();
        }
    }
}
