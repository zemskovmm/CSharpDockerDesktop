using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>Presentation state only. All methods except the notification handler run on the UI dispatcher.</summary>
public sealed partial class ContainerBrowserView(IContainerBrowser browser, Func<Action, bool> dispatch) : IDisposable
{
    private int epoch;
    private int notificationQueued;
    private bool active;
    private long filterRevision;
    private CancellationTokenSource? filtering;
    private IReadOnlyList<ContainerSummary>? filteredSource;
    private string filteredQuery = "";
    public JsonInspectView Inspect { get; } = new(dispatch);
    public bool ShowingInspect { get; private set; }
    public bool ShowingLogs { get; private set; }
    public ContainerLogsView Logs { get; } = new(browser as IContainerLogs, dispatch);
    private void OnInspectChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public event EventHandler? Changed;
    public ContainerBrowsingSnapshot Snapshot { get; private set; } = new(0, ResourceReadState.Unavailable, []);
    public IReadOnlyList<ContainerRow> Rows { get; private set; } = Array.Empty<ContainerRow>();
    public string Query { get; private set; } = "";
    public string? SelectedId { get; private set; }
    public bool DetailOpen { get; private set; }
    public bool Filtering { get; private set; }
    public bool CanRefresh => Snapshot.State != ResourceReadState.Unavailable;
    public bool CanOpen => SelectedId is not null && CanRefresh;
    public string ListMessage => Snapshot.State switch
    {
        ResourceReadState.Unavailable => "Containers unavailable. Connection status and recovery are shown above." + FailureSuffix(Snapshot.Failure ?? Snapshot.InspectionFailure),
        ResourceReadState.NotRequested => "Containers have not been requested. Choose Refresh containers.",
        ResourceReadState.Loading when Snapshot.Containers.Count == 0 => "Loading containers…",
        ResourceReadState.Loading => "Refreshing containers… Previous results remain visible.",
        ResourceReadState.Failed => "Container refresh failed. Choose Refresh containers to retry." + FailureSuffix(Snapshot.Failure),
        _ when Filtering => "Searching loaded containers…",
        _ when Snapshot.Containers.Count == 0 => "No containers on this Engine.",
        _ when Rows.Count == 0 => "No containers match your search.",
        _ => $"{Rows.Count} of {Snapshot.Containers.Count} containers"
    };
    public string DetailMessage => ShowingLogs ? "Container Logs · sensitive original output" : Snapshot.InspectionState switch
    {
        ResourceReadState.Loading => $"Loading {(ShowingInspect ? "Inspect" : "Overview")}…",
        ResourceReadState.Failed => $"{(ShowingInspect ? "Inspect" : "Overview")} failed. Choose Refresh details to retry." + FailureSuffix(Snapshot.InspectionFailure),
        ResourceReadState.Ready => ShowingInspect ? "Raw Inspect" : "Container Overview",
        ResourceReadState.Unavailable => $"{(ShowingInspect ? "Inspect" : "Overview")} unavailable. Check connection status above.",
        _ => "Choose Refresh details to load details."
    };
    public string Overview => DetailOpen && Snapshot.InspectionState == ResourceReadState.Ready
        && Snapshot.Inspection is { } inspection && inspection.Id == SelectedId
        ? ContainerDisplay.Overview(inspection) : "";

    public void Attach()
    {
        if (active) return;
        active = true;
        Interlocked.Increment(ref epoch);
        browser.ContainersChanged += OnChanged;
        if (operations is not null) operations.ContainerOperationChanged += OnChanged;
        Inspect.Changed += OnInspectChanged;
        Logs.Changed += OnInspectChanged;
        Logs.IsCurrentIdentity = () => active && DetailOpen && ShowingLogs && browser.ContainerSnapshot.SelectedContainerId == SelectedId
            && browser.ContainerSnapshot.State != ResourceReadState.Unavailable
            && browser.ContainerSnapshot.ConnectionGeneration == Snapshot.ConnectionGeneration
            && browser.ContainerSnapshot.Containers.Any(row => row.Id == SelectedId);
        Inspect.IsCurrentContent = raw =>
        {
            var latest = browser.ContainerSnapshot;
            return active && DetailOpen && latest.State != ResourceReadState.Unavailable
                && latest.ConnectionGeneration == Snapshot.ConnectionGeneration
                && latest.SelectedContainerId == SelectedId && latest.InspectionState == ResourceReadState.Ready
                && ReferenceEquals(latest.Inspection?.RawJson, raw)
                && latest.Containers.Any(row => row.Id == SelectedId);
        };
        ApplyLatest();
    }
    public void Dispose()
    {
        if (!active) return;
        active = false;
        Interlocked.Increment(ref epoch);
        Interlocked.Exchange(ref notificationQueued, 0);
        browser.ContainersChanged -= OnChanged;
        if (operations is not null) operations.ContainerOperationChanged -= OnChanged;
        DeleteConfirmation = null;
        submitting = false;
        Operation = null;
        AdmissionMessage = "";
        ShowingOperationDetails = false;
        SelectedId = null;
        Logs.Changed -= OnInspectChanged; Logs.Close(); ShowingLogs = false;
        Inspect.Changed -= OnInspectChanged;
        Inspect.Dispose();
        Snapshot = new(0, ResourceReadState.Unavailable, []);
        Rows = Array.Empty<ContainerRow>();
        ShowingInspect = false;
        filtering?.Cancel();
        filtering?.Dispose();
        filtering = null;
        filteredSource = null;
        ++filterRevision;
        if (DetailOpen) browser.ClearContainerInspection();
        DetailOpen = false;
    }
    private void OnChanged(object? sender, EventArgs args)
    {
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
        var latest = browser.ContainerSnapshot; // Never capture an obsolete snapshot in an off-thread notification.
        if (latest.ConnectionGeneration != Snapshot.ConnectionGeneration || latest.State == ResourceReadState.Unavailable)
        {
            SelectedId = null;
            DetailOpen = false;
            Rows = Array.Empty<ContainerRow>();
            filteredSource = null;
        }
        Snapshot = latest;
        SyncOperation();
        if (SelectedId is not null && !latest.Containers.Any(row => row.Id == SelectedId))
        {
            SelectedId = null;
            if (DetailOpen) browser.ClearContainerInspection();
            DetailOpen = false;
        }
        // A detail belongs to exactly one session selection. Never display another identity's result.
        if (DetailOpen && latest.SelectedContainerId != SelectedId) DetailOpen = false;
        if (DeleteConfirmation is { } confirmation && !ConfirmationCurrent(confirmation)) DeleteConfirmation = null;
        if (!DetailOpen && ShowingLogs) { ShowingLogs = false; Logs.Close(); }
        SyncInspect();
        Filter();
    }
    private void SyncInspect()
    {
        if (!DetailOpen || Snapshot.InspectionState != ResourceReadState.Ready || Snapshot.Inspection?.Id != SelectedId) Inspect.Load(null);
        else if (ShowingInspect && Snapshot.Inspection is { } inspection) Inspect.Load(inspection.RawJson);
    }
    public void ShowInspect(bool show)
    {
        if (!active || !DetailOpen) return;
        ShowingLogs = false; Logs.Close(); ShowingInspect = show; SyncInspect(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void ShowLogs(bool follow = true)
    {
        if (!active || !DetailOpen || SelectedId is not { } id) return;
        ShowingInspect = false; Inspect.Load(null); ShowingLogs = true; Logs.Open(id, follow);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Search(string query)
    {
        if (!active) return;
        Query = query.Trim();
        Filter();
    }
    public void Select(string? id)
    {
        if (!active || id is null || !Snapshot.Containers.Any(row => row.Id == id)) return;
        if (SelectedId == id) return;
        if (DetailOpen) browser.ClearContainerInspection();
        DetailOpen = false;
        ShowingLogs = false; Logs.Close();
        ShowingInspect = false; Inspect.Load(null);
        DeleteConfirmation = null;
        SelectedId = id;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void OpenSelected()
    {
        if (!active || !CanOpen || DetailOpen) return;
        DetailOpen = true;
        ShowingInspect = false;
        browser.SelectContainer(SelectedId);
        ApplyLatest();
    }
    public void CloseDetail()
    {
        if (!DetailOpen) return;
        ShowingLogs = false; Logs.Close();
        DetailOpen = false;
        browser.ClearContainerInspection();
        ApplyLatest();
    }
    public void Refresh() { if (active && CanRefresh) browser.RefreshContainers(); }
    public void RefreshDetail()
    {
        if (active && DetailOpen && CanOpen) { Inspect.Load(null); browser.RefreshContainerInspection(); }
    }
    private void Filter()
    {
        if (ReferenceEquals(filteredSource, Snapshot.Containers) && filteredQuery == Query)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        filtering?.Cancel();
        filtering?.Dispose();
        var cancellation = new CancellationTokenSource();
        filtering = cancellation;
        var token = cancellation.Token;
        var source = Snapshot.Containers;
        string query = Query;
        long revision = ++filterRevision;
        int current = epoch;
        filteredSource = source;
        filteredQuery = query;
        Filtering = true;
        Changed?.Invoke(this, EventArgs.Empty);
        // Projection and filtering run away from the UI thread, with latest-search cancellation.
        _ = Task.Run(() =>
        {
            var rows = new List<ContainerRow>();
            foreach (var summary in source)
            {
                if (token.IsCancellationRequested) return;
                if (ContainerDisplay.Matches(summary, query)) rows.Add(new(summary));
            }
            dispatch(() =>
            {
                if (!active || epoch != current || revision != filterRevision || token.IsCancellationRequested) return;
                Rows = rows.AsReadOnly();
                Filtering = false;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        });
    }
    private static string FailureSuffix(EngineResourceFailure? failure) => failure is null ? ""
        : $" {ContainerDisplay.Failure(failure)}";
}

public sealed class ContainerRow(ContainerSummary summary)
{
    public string Id => summary.Id;
    public string ShortId => ContainerDisplay.ShortId(Id);
    public string Name => ContainerDisplay.Collection(summary.Names, name => ContainerDisplay.Text(name.TrimStart('/')));
    public string Image => ContainerDisplay.Text(summary.Image);
    public string State => ContainerDisplay.Text(summary.State);
    public string Status => ContainerDisplay.Text(summary.Status);
    public string Health => ContainerDisplay.Health(summary.Health);
    public string Ports => ContainerDisplay.Ports(summary.Ports);
    public string Created => ContainerDisplay.Time(summary.Created);
    public string AccessibleSummary => $"{Name}, ID {ShortId}, image {Image}, state {State}, status {Status}, health {Health}, ports {Ports}, created {Created}";
}

public static class ContainerDisplay
{
    public static string Text(string? value) => string.IsNullOrEmpty(value) ? "unknown" : value;
    public static string ShortId(string id) => id[..Math.Min(id.Length, 12)];
    public static string Time(DateTimeOffset? value) => value?.ToString("u") ?? "unknown";
    public static string Flag(bool? value) => value switch { true => "yes", false => "no", _ => "unknown" };
    public static string Collection<T>(IReadOnlyList<T>? values, Func<T, string> format)
        => values is null ? "unknown" : values.Count == 0 ? "none" : string.Join("; ", values.Select(format));
    public static string Health(ContainerHealth? health) => health is null ? "unknown"
        : $"{Text(health.Status)} (failing streak: {health.FailingStreak?.ToString() ?? "unknown"})";
    public static string Ports(IReadOnlyList<ContainerPort>? ports) => Collection(ports, port =>
        $"{Text(port.HostIP)}:{port.PublicPort?.ToString() ?? "unknown"} → {port.PrivatePort?.ToString() ?? "unknown"}/{Text(port.Protocol)}");
    public static bool Matches(ContainerSummary summary, string query)
    {
        bool Has(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        return query.Length == 0 || Has(summary.Id) || Has(summary.Image) || Has(summary.ImageId)
            || Has(summary.State) || Has(summary.Status) || Has(summary.Health?.Status)
            || summary.Names?.Any(Has) == true;
    }
    public static string Failure(EngineResourceFailure failure)
        => $"{failure.Kind}{(failure.HttpStatusCode is { } code ? $" (HTTP {code})" : "")}: {failure.Description}";
    public static string Overview(ContainerInspection inspection)
    {
        var state = inspection.State;
        return $"Identity: {inspection.Id}\nName: {Text(inspection.Name)}\nImage: {Text(inspection.Image)}\nImage identity: {Text(inspection.ImageId)}\nCreated: {Time(inspection.Created)}\nStarted: {Time(state?.StartedAt)}\nFinished: {Time(state?.FinishedAt)}\n\nRuntime state: {Text(state?.Status)}\nRunning: {Flag(state?.Running)}; paused: {Flag(state?.Paused)}; restarting: {Flag(state?.Restarting)}; dead: {Flag(state?.Dead)}\nExit code: {state?.ExitCode?.ToString() ?? "unknown"}\nRuntime error: {Text(state?.Error)}\nHealth: {Health(state?.Health)}\n\nPorts\n{Ports(inspection.Ports)}\n\nMounts\n{Collection(inspection.Mounts, mount => $"Type: {Text(mount.Type)}; name: {Text(mount.Name)}; source: {Text(mount.Source)}; destination: {Text(mount.Destination)}; driver: {Text(mount.Driver)}; mode: {Text(mount.Mode)}; read/write: {Flag(mount.ReadWrite)}; propagation: {Text(mount.Propagation)}")}\n\nNetworks\n{Collection(inspection.Networks, network => $"Name: {Text(network.Name)}; network ID: {Text(network.NetworkId)}; endpoint ID: {Text(network.EndpointId)}; IP: {Text(network.IPAddress)}/{network.IPPrefixLength?.ToString() ?? "unknown"}; gateway: {Text(network.Gateway)}; IPv6: {Text(network.GlobalIPv6Address)}/{network.GlobalIPv6PrefixLength?.ToString() ?? "unknown"}; IPv6 gateway: {Text(network.IPv6Gateway)}; MAC: {Text(network.MacAddress)}; aliases: {Collection(network.Aliases, Text)}")}";
    }
}
