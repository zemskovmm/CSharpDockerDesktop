using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>UI-dispatched image presentation; Engine work remains owned by IImageBrowser.</summary>
public sealed class ImageBrowserView(IImageBrowser browser, Func<Action, bool> dispatch) : IDisposable
{
    private bool active;
    private int epoch, notificationQueued;
    private long filterRevision, overviewRevision;
    private CancellationTokenSource? filtering, overviewWork;
    private IReadOnlyList<ImageSummary>? filteredSource;
    private string filteredQuery = "";
    private ImageInspection? overviewSource;
    private string overview = "";
    public JsonInspectView Inspect { get; } = new(dispatch);
    public event EventHandler? Changed;
    public ImageBrowsingSnapshot Snapshot { get; private set; } = new(0, ResourceReadState.Unavailable, []);
    public IReadOnlyList<ImageRow> Rows { get; private set; } = Array.Empty<ImageRow>();
    public string Query { get; private set; } = "";
    public string? SelectedId { get; private set; }
    public bool DetailOpen { get; private set; }
    public bool ShowingInspect { get; private set; }
    public bool Filtering { get; private set; }
    public bool CanRefresh => CurrentGeneration(browser.ImageSnapshot);
    public bool CanOpen => CurrentIdentity(browser.ImageSnapshot);
    public string Overview => CurrentInspection(browser.ImageSnapshot) && ReferenceEquals(overviewSource, Snapshot.Inspection) ? overview : "";
    public string ListMessage => Snapshot.State switch
    {
        ResourceReadState.Unavailable => "Images unavailable. Connection status and recovery are shown above." + FailureSuffix(Snapshot.Failure ?? Snapshot.InspectionFailure),
        ResourceReadState.NotRequested => "Images have not been requested. Choose Refresh images.",
        ResourceReadState.Loading when Snapshot.Images.Count == 0 => "Loading images…",
        ResourceReadState.Loading => "Refreshing images… Previous results remain visible.",
        ResourceReadState.Failed => "Image refresh failed. Choose Refresh images to retry." + FailureSuffix(Snapshot.Failure),
        _ when Filtering => "Searching loaded images…",
        _ when Snapshot.Images.Count == 0 => "No images on this Engine.",
        _ when Rows.Count == 0 => "No images match your search.",
        _ => $"{Rows.Count} of {Snapshot.Images.Count} images"
    };
    public string DetailMessage => Snapshot.InspectionState switch
    {
        ResourceReadState.Loading => $"Loading {(ShowingInspect ? "Inspect" : "Overview")}…",
        ResourceReadState.Failed => "Image details failed. Choose Refresh details to retry." + FailureSuffix(Snapshot.InspectionFailure),
        ResourceReadState.Ready => ShowingInspect ? "Raw image Inspect · may contain sensitive values" : overviewWork is not null ? "Preparing image Overview…" : "Image Overview",
        ResourceReadState.Unavailable => "Image details unavailable. Check connection status above.",
        _ => "Choose Refresh details to load details."
    };
    private bool CurrentGeneration(ImageBrowsingSnapshot latest) => active && latest.State != ResourceReadState.Unavailable
        && latest.ConnectionGeneration == Snapshot.ConnectionGeneration;
    private bool CurrentIdentity(ImageBrowsingSnapshot latest) => CurrentGeneration(latest) && SelectedId is not null
        && latest.Images.Any(row => row.Id == SelectedId) && (!DetailOpen || latest.SelectedImageId == SelectedId);
    private bool CurrentInspection(ImageBrowsingSnapshot latest) => DetailOpen && CurrentIdentity(latest)
        && latest.InspectionState == ResourceReadState.Ready && latest.Inspection?.Id == SelectedId
        && ReferenceEquals(latest.Inspection, Snapshot.Inspection);

    public void Attach()
    {
        if (active) return;
        active = true; Interlocked.Increment(ref epoch);
        browser.ImagesChanged += OnChanged; Inspect.Changed += OnInspectChanged;
        Inspect.IsCurrentContent = raw => CurrentInspection(browser.ImageSnapshot) && ReferenceEquals(Snapshot.Inspection?.RawJson, raw);
        ApplyLatest();
    }
    public void Dispose()
    {
        if (!active) return;
        active = false; Interlocked.Increment(ref epoch); Interlocked.Exchange(ref notificationQueued, 0);
        browser.ImagesChanged -= OnChanged; Inspect.Changed -= OnInspectChanged; Inspect.Dispose();
        CancelFilter(); ClearOverview(); filteredSource = null;
        bool clear = DetailOpen;
        DetailOpen = ShowingInspect = false; SelectedId = null; Rows = Array.Empty<ImageRow>();
        Snapshot = new(0, ResourceReadState.Unavailable, []);
        if (clear) browser.ClearImageInspection();
    }
    private void OnInspectChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
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
        var latest = browser.ImageSnapshot;
        bool replaced = latest.ConnectionGeneration != Snapshot.ConnectionGeneration || latest.State == ResourceReadState.Unavailable;
        bool disappeared = SelectedId is not null && !latest.Images.Any(row => row.Id == SelectedId);
        bool clear = DetailOpen && !replaced && disappeared;
        if (replaced || disappeared) { SelectedId = null; DetailOpen = false; }
        if (replaced) { Rows = Array.Empty<ImageRow>(); filteredSource = null; CancelFilter(); }
        if (DetailOpen && latest.SelectedImageId != SelectedId) { DetailOpen = false; SelectedId = null; }
        Snapshot = latest;
        if (!DetailOpen) ShowingInspect = false;
        SyncDetails(); Filter();
        if (clear) browser.ClearImageInspection();
    }
    private void SyncDetails()
    {
        if (!CurrentInspection(browser.ImageSnapshot)) { Inspect.Load(null); ClearOverview(); return; }
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
                string text = ImageDisplay.Overview(inspection, token);
                dispatch(() =>
                {
                    if (!active || epoch != lifetime || overviewRevision != revision || token.IsCancellationRequested
                        || !CurrentInspection(browser.ImageSnapshot)) return;
                    overview = text; overviewWork?.Dispose(); overviewWork = null; Changed?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) { }
        });
    }
    private void ClearOverview()
    {
        ++overviewRevision; overviewWork?.Cancel(); overviewWork?.Dispose(); overviewWork = null; overviewSource = null; overview = "";
    }
    public void ShowInspect(bool show)
    {
        if (!active || !DetailOpen) return;
        ApplyLatest(); if (!DetailOpen) return;
        ShowingInspect = show; SyncDetails(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Search(string query) { if (active) { Query = query.Trim(); Filter(); } }
    public void Select(string? id)
    {
        var latest = browser.ImageSnapshot;
        if (!CurrentGeneration(latest) || id is null || !latest.Images.Any(row => row.Id == id) || SelectedId == id) return;
        bool clear = DetailOpen;
        DetailOpen = ShowingInspect = false; Inspect.Load(null); ClearOverview(); SelectedId = id;
        if (clear) browser.ClearImageInspection();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void OpenSelected()
    {
        if (!CanOpen || DetailOpen) return;
        browser.SelectImage(SelectedId); DetailOpen = true; ShowingInspect = false; ApplyLatest();
    }
    public void CloseDetail()
    {
        if (!active || !DetailOpen) return;
        DetailOpen = ShowingInspect = false; Inspect.Load(null); ClearOverview();
        browser.ClearImageInspection(); ApplyLatest();
    }
    public void Refresh() { if (CanRefresh) browser.RefreshImages(); }
    public void RefreshDetail()
    {
        if (!DetailOpen || !CanOpen) return;
        Inspect.Load(null); ClearOverview(); browser.RefreshImageInspection();
    }
    public bool CopyIdentity(Action<string> publish)
    {
        if (!CanOpen) return false;
        try { publish(SelectedId!); return true; } catch (Exception) { return false; }
    }
    private void CancelFilter()
    {
        ++filterRevision; filtering?.Cancel(); filtering?.Dispose(); filtering = null; Filtering = false;
    }
    private void Filter()
    {
        if (Snapshot.State == ResourceReadState.Unavailable)
        {
            CancelFilter(); filteredSource = null; Rows = Array.Empty<ImageRow>(); Changed?.Invoke(this, EventArgs.Empty); return;
        }
        if (ReferenceEquals(filteredSource, Snapshot.Images) && filteredQuery == Query) { Changed?.Invoke(this, EventArgs.Empty); return; }
        CancelFilter(); var cancel = filtering = new(); var token = cancel.Token;
        var source = Snapshot.Images; string query = Query;
        long revision = filterRevision; int lifetime = epoch; long generation = Snapshot.ConnectionGeneration;
        filteredSource = source; filteredQuery = query; Filtering = true; Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() =>
        {
            var rows = new List<ImageRow>();
            foreach (var summary in source)
            {
                if (token.IsCancellationRequested) return;
                if (ImageDisplay.Matches(summary, query)) rows.Add(new(summary));
            }
            dispatch(() =>
            {
                var latest = browser.ImageSnapshot;
                if (!active || lifetime != epoch || revision != filterRevision || token.IsCancellationRequested
                    || latest.State == ResourceReadState.Unavailable || latest.ConnectionGeneration != generation || !ReferenceEquals(latest.Images, source)) return;
                Rows = rows.AsReadOnly(); Filtering = false; Changed?.Invoke(this, EventArgs.Empty);
            });
        });
    }
    private static string FailureSuffix(EngineResourceFailure? failure) => failure is null ? "" : $" {ContainerDisplay.Failure(failure)}";
}

public sealed class ImageRow(ImageSummary summary)
{
    public string Id => summary.Id;
    public string ShortId => ImageDisplay.ShortId(Id);
    public string Tags => ContainerDisplay.Collection(summary.RepoTags, ContainerDisplay.Text);
    public string Created => ContainerDisplay.Time(summary.Created);
    public string Size => ImageDisplay.Size(summary.Size);
    public string AccessibleSummary => $"Tags/names: {Tags}, ID: {ShortId}, created: {Created}, size: {Size}";
}

public static class ImageDisplay
{
    public static string ShortId(string id) => ContainerDisplay.ShortId(id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id);
    public static string Size(long? value) => value is null ? "unknown" : $"{value.Value:N0} bytes";
    public static bool Matches(ImageSummary summary, string query)
    {
        bool Has(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        return query.Length == 0 || Has(summary.Id) || summary.RepoTags?.Any(Has) == true || summary.RepoDigests?.Any(Has) == true
            || Has(summary.Created?.ToString("u")) || Has(summary.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public static string Overview(ImageInspection image, CancellationToken token = default)
    {
        var text = new System.Text.StringBuilder();
        void Field(string name, string? value) { token.ThrowIfCancellationRequested(); text.Append(name).Append(": ").AppendLine(ContainerDisplay.Text(value)); }
        void Collection(string name, IReadOnlyList<string>? values)
        {
            if (values is null || values.Count == 0) Field(name, values is null ? "unknown" : "none");
            else text.Append(name).AppendLine(":");
            if (values is not null) foreach (string value in values) { token.ThrowIfCancellationRequested(); text.AppendLine(value); }
        }
        Field("Identity", image.Id); Collection("Tags/names", image.RepoTags); Collection("Repository digests", image.RepoDigests);
        Field("Created", ContainerDisplay.Time(image.Created)); Field("Size", Size(image.Size));
        Field("OS", image.OS); Field("OS version", image.OSVersion); Field("Architecture", image.Architecture); Field("Variant", image.Variant);
        text.AppendLine().AppendLine("Configuration · may contain sensitive values");
        if (image.Configuration is not { } config) text.AppendLine("unknown");
        else
        {
            Field("User", config.User); Field("Working directory", config.WorkingDirectory); Field("Stop signal", config.StopSignal);
            Field("Args escaped", ContainerDisplay.Flag(config.ArgsEscaped)); Collection("Environment", config.Environment);
            Collection("Command", config.Command); Collection("Entrypoint", config.Entrypoint); Collection("Shell", config.Shell);
            Collection("Exposed ports", config.ExposedPorts); Collection("Volumes", config.Volumes); Collection("On build", config.OnBuild);
            if (config.Labels is null || config.Labels.Count == 0) Field("Labels", config.Labels is null ? "unknown" : "none");
            else text.AppendLine("Labels:");
            if (config.Labels is not null) foreach (var label in config.Labels) Field(label.Key, label.Value);
        }
        text.AppendLine().AppendLine("Root filesystem"); Field("Type", image.RootFileSystem?.Type);
        text.AppendLine("Layers · ordered uncompressed content digests");
        if (image.RootFileSystem?.Layers is not { } layers) text.AppendLine("unknown");
        else if (layers.Count == 0) text.AppendLine("none");
        else for (int i = 0; i < layers.Count; ++i) { token.ThrowIfCancellationRequested(); text.Append(i + 1).Append(". ").AppendLine(layers[i]); }
        return text.ToString();
    }
}
