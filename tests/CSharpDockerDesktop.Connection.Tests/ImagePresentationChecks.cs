using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class ImagePresentationChecks
{
    private static readonly string A = "sha256:" + new string('a', 64), B = "sha256:" + new string('b', 64);
    private static ImageSummary Row(string id, string? tag = null) => new(id, tag is null ? null : [tag, "second/name:tag"], tag is null ? null : ["repo@sha256:digest"], tag is null ? null : DateTimeOffset.UnixEpoch, tag is null ? null : 0);
    private static ImageBrowsingSnapshot Ready(params ImageSummary[] rows) => new(1, ResourceReadState.Ready, rows);
    private static ImageInspection Detail(string id, string raw) => new(id, ["repo:tag"], [], DateTimeOffset.UnixEpoch, 123, "linux", null, "arm64", "v8",
        new(null, ["TOKEN=deliberate"], ["run", "arg"], [], "/app", new Dictionary<string, string?> { ["owner"] = "example" }, [], null, null, null, null, false, "{}"),
        new("layers", ["layer-first", "layer-second"]), raw);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task MetadataAndSearch()
    {
        var ui = new Dispatcher(); var browser = new Browser { ImageSnapshot = Ready(Row(A, "Repo/First:Tag"), Row(B)) };
        using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        foreach (string query in new[] { "REPO/FIRST", "second/name", "digest", A, A[25..], "1970-01-01", "0" })
        {
            view.Search(query); await ui.Settle(view); Check(view.Rows.Single().Id == A, $"Metadata search missed {query}");
        }
        view.Search(""); await ui.Settle(view);
        var known = view.Rows.Single(row => row.Id == A); var missing = view.Rows.Single(row => row.Id == B);
        Check(known.Tags.Contains("Repo/First:Tag") && known.Tags.Contains("second/name:tag") && known.ShortId == new string('a', 12)
            && known.Size == "0 bytes" && known.Created.Contains("1970"), "All tags, shortened digest, known zero and created metadata");
        Check(missing.Tags == "unknown" && missing.Size == "unknown" && missing.Created == "unknown", "Missing metadata stays unknown");
        Check(new ImageRow(new(B, [], [], null, null)).Tags == "none", "Successful empty tags differ from absent tags");
        Check(browser.SelectCalls == 0 && browser.RefreshCalls == 0 && browser.CopyCalls == 0, "Projection/search never initiates Engine work or copy");
    }
    public static async Task StatesAndCoalescing()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post);
        view.Attach(); await ui.Settle(view); Check(view.ListMessage.Contains("unavailable") && !view.CanRefresh, "Disconnected");
        browser.Publish(new(1, ResourceReadState.NotRequested, [])); await ui.Settle(view); Check(view.ListMessage.Contains("not been requested"), "Not requested");
        browser.Publish(new(1, ResourceReadState.Loading, [])); ui.Pump(); Check(view.ListMessage == "Loading images…", "Initial loading");
        browser.Publish(Ready()); await ui.Settle(view); Check(view.ListMessage == "No images on this Engine.", "Successful empty");
        browser.Publish(Ready(Row(A))); await ui.Settle(view); view.Search("unmatched"); await ui.Settle(view);
        Check(view.ListMessage == "No images match your search.", "Search emptiness");
        view.Search(""); await ui.Settle(view); browser.Publish(browser.ImageSnapshot with { State = ResourceReadState.Loading }); ui.Pump();
        Check(view.ListMessage.StartsWith("Refreshing") && view.Rows.Count == 1, "Refresh retains old list");
        var failure = new EngineResourceFailure(EngineResourceFailureKind.AccessDenied, "Permission denied", 403, "PRIVATE original daemon");
        browser.Publish(browser.ImageSnapshot with { State = ResourceReadState.Failed, Failure = failure }); ui.Pump();
        Check(view.ListMessage.Contains("AccessDenied (HTTP 403)") && !view.ListMessage.Contains("PRIVATE") && view.Rows.Count == 1, "Safe classified failure");
        view.Refresh(); Check(browser.RefreshCalls == 1, "Deliberate refresh");
        await Task.Run(() => { for (int i = 0; i < 200; ++i) browser.Publish(Ready(Row(i == 199 ? B : A))); });
        Check(ui.Count == 1, "Off-thread notification burst must coalesce"); await ui.Settle(view);
        Check(view.Rows.Single().Id == B, "Coalesced callback reads latest snapshot");
        browser.Publish(new(1, ResourceReadState.Failed, [], Failure: failure)); await ui.Settle(view);
        Check(view.ListMessage.StartsWith("Image refresh failed"), "Failed first read is not successful empty");
    }
    public static async Task IdentityOverviewAndAdmission()
    {
        var ui = new Dispatcher(); var browser = new Browser { ImageSnapshot = Ready(Row(A, "alpha"), Row(B, "beta")) };
        using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        view.Select(A); view.CopyIdentity(browser.Copy); Check(browser.Copied == A && browser.SelectCalls == 0, "Full ID copy is explicit and does not read");
        view.OpenSelected(); view.OpenSelected(); ui.Pump(); Check(browser.LastSelected == A && browser.SelectCalls == 1 && view.DetailMessage == "Loading Overview…", "Only deliberate open reads using full ID");
        view.Search("beta"); await ui.Settle(view); Check(view.SelectedId == A && view.DetailOpen && view.Rows.Single().Id == B, "Filtering preserves identity/detail");
        browser.Publish(browser.ImageSnapshot with { Images = [Row(B), Row(A)] }); await ui.Settle(view);
        Check(view.SelectedId == A && browser.SelectCalls == 1, "Reorder doesn't reopen detail");
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"Unknown\":true}") }); await ui.Settle(view);
        Check(view.Overview.Contains("OS: linux") && view.Overview.Contains("OS version: unknown") && view.Overview.Contains("Architecture: arm64")
            && view.Overview.Contains("Args escaped: no") && view.Overview.Contains("Entrypoint: none") && view.Overview.Contains("Volumes: unknown")
            && view.Overview.Contains("TOKEN=deliberate") && view.Overview.Contains("owner: example")
            && !view.Overview.Contains("Tags/names: unknown") && !view.Overview.Contains("Environment: unknown")
            && view.Overview.IndexOf("1. layer-first", StringComparison.Ordinal) < view.Overview.IndexOf("2. layer-second", StringComparison.Ordinal), "Overview keeps actual config, ordered layers and missing/empty distinction");
        var absent = Detail(A, "{}") with { Configuration = null, RootFileSystem = null, OS = null, Architecture = null };
        Check(ImageDisplay.Overview(absent).Contains("Configuration · may contain sensitive values\nunknown") && ImageDisplay.Overview(absent).Contains("OS: unknown"), "No fabricated configuration/platform");
        view.RefreshDetail(); Check(view.Overview == "" && browser.DetailRefreshCalls == 1, "Refresh clears old presentation");
        browser.Publish(browser.ImageSnapshot with { Inspection = null, InspectionState = ResourceReadState.Failed, InspectionFailure = new(EngineResourceFailureKind.Conflict, "Conflict", 409, "PRIVATE") }); ui.Pump();
        Check(view.Overview == "" && view.DetailMessage.Contains("Conflict (HTTP 409)") && !view.DetailMessage.Contains("PRIVATE"), "Safe detail failure");
        view.CloseDetail(); ui.Pump(); Check(browser.ClearCalls == 1 && view.SelectedId == A, "Close cancels detail while preserving table identity");
        browser.Publish(new(2, ResourceReadState.Ready, [Row(A)]));
        int reads = browser.SelectCalls; view.OpenSelected(); Check(browser.SelectCalls == reads && !view.CopyIdentity(browser.Copy), "Queued generation replacement rejects read/copy before dispatcher drains");
    }
    public static async Task InspectContinuityAndExternalInvalidation()
    {
        var ui = new Dispatcher(); var browser = new Browser { ImageSnapshot = Ready(Row(A), Row(B)) };
        using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        const string raw = "{ \"Env\":[\"SECRET=exact\"], \"unknown\":{\"n\":12345678901234567890.123456789012345,\"n\":null}, \"empty\":[] }";
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, raw) }); ui.Pump();
        view.ShowInspect(true); await ui.Settle(view); view.Inspect.Select(0); view.Inspect.Copy(false, browser.Copy); await ui.Settle(view);
        Check(browser.Copied == raw, "Root value is exact daemon JSON with whitespace, duplicates, numbers and sensitive values");
        view.Inspect.Search("SECRET"); await ui.Settle(view); view.Inspect.Select(view.Inspect.Rows[^1].NodeId); view.Inspect.Copy(false, browser.Copy); await ui.Settle(view);
        Check(browser.Copied == "\"SECRET=exact\"", "Deliberate sensitive scalar copy remains exact");
        view.Inspect.Copy(true, browser.Copy); await ui.Settle(view); Check(browser.Copied == "$[\"Env\",0][0]", "Exact unambiguous path");
        view.ShowInspect(false); view.ShowInspect(true); await ui.Settle(view);
        Check(view.Inspect.Query == "SECRET" && browser.SelectCalls == 1, "Tabs preserve viewer without reread");
        int copies = browser.CopyCalls;
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null });
        view.Inspect.Copy(false, browser.Copy); Check(browser.CopyCalls == copies && view.Overview == "", "Event invalidation rejects obsolete copy before dispatch");
        ui.Pump(); Check(view.Inspect.Rows.Count == 0 && !view.Inspect.CanCopy && view.DetailOpen, "Selected image event immediately clears exposed Inspect when applied");
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"fresh\":true}") }); await ui.Settle(view);
        Check(view.Inspect.Rows.Any(row => row.Text.Contains("fresh")), "Authoritative event replacement exposes only current Inspect");
        browser.Publish(browser.ImageSnapshot with { SelectedImageId = B, Inspection = Detail(B, "{\"other\":true}") });
        Check(!view.CopyIdentity(browser.Copy), "External selection replacement fences identity copy");
        await ui.Settle(view); Check(!view.DetailOpen && view.SelectedId is null && view.Inspect.Rows.Count == 0 && view.Overview == "" && !view.CopyIdentity(browser.Copy), "Selection replacement removes previous details and copy admission");
    }
    public static async Task StaleQueuedResultsAndCopy()
    {
        var ui = new Dispatcher(); var browser = new Browser { ImageSnapshot = Ready(Row(A)) };
        using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"old\":true}") }); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
        view.Inspect.Select(0); int copies = browser.CopyCalls; view.Inspect.Copy(false, browser.Copy); await ui.WaitQueued();
        // The copy completion is ahead of the invalidation notification. Live snapshot admission still rejects it.
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null }); ui.Pump(); await ui.Settle(view);
        Check(browser.CopyCalls == copies && view.Inspect.Rows.Count == 0, "Undrained selected-image event fences queued copy");
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"large\":[" + string.Join(",", Enumerable.Range(0, 50000)) + "]}") }); ui.Pump();
        browser.Publish(new(2, ResourceReadState.Unavailable, [])); await ui.Settle(view);
        Check(view.Inspect.Rows.Count == 0 && view.Overview == "" && view.Rows.Count == 0 && view.SelectedId is null, "Disconnect rejects pending large parse and overview");
        browser.Publish(Ready(Row(A))); await ui.Settle(view); view.Search(new string('a', 12)); await ui.WaitQueued();
        browser.Publish(new(3, ResourceReadState.Ready, [Row(B)])); ui.Pump(); await ui.Settle(view);
        Check(view.Rows.Count == 0 && view.Snapshot.ConnectionGeneration == 3, "A queued filter cannot publish into a different generation");
        view.Search(""); await ui.Settle(view); view.Select(B); view.OpenSelected(); ui.Pump();
        browser.Publish(browser.ImageSnapshot with { Images = [], SelectedImageId = null, Inspection = null });
        Check(!view.CopyIdentity(browser.Copy), "Authoritative disappearance immediately rejects full identity copy");
        await ui.Settle(view); Check(!view.DetailOpen && view.SelectedId is null && browser.ClearCalls == 1, "Authoritative disappearance clears owned detail");
    }
    public static async Task NavigationAndReattachment()
    {
        var ui = new Dispatcher(); var browser = new Browser { ImageSnapshot = Ready(Row(A)) };
        using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        browser.Publish(browser.ImageSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"old\":true}") }); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
        view.Inspect.Copy(false, browser.Copy); await ui.WaitQueued(); int copies = browser.CopyCalls;
        int changes = 0; view.Changed += (_, _) => ++changes; view.Dispose(); int previous = changes; ui.Pump();
        Check(browser.Subscribers == 0 && browser.ClearCalls == 1 && changes == previous && browser.CopyCalls == copies && view.Inspect.Rows.Count == 0
            && view.Snapshot.Inspection is null && view.Rows.Count == 0, "Navigation detaches, clears raw content, cancels work and prevents pending copy/render");
        view.OpenSelected(); view.Refresh(); view.Search("inactive"); Check(browser.SelectCalls == 1 && browser.RefreshCalls == 0, "Inactive commands do nothing");
        view.Attach(); browser.Publish(Ready(Row(A))); view.Dispose(); view.Attach(); browser.Publish(Ready(Row(B))); await ui.Settle(view);
        Check(browser.Subscribers == 1 && view.Rows.Single().Id == B && !view.DetailOpen, "Old queued callbacks cannot suppress new lifetime notifications");
    }
    public static async Task LargeCollectionRapidSearch()
    {
        var ui = new Dispatcher(); var rows = Enumerable.Range(0, 20000).Select(i => Row("sha256:" + i.ToString("x64"), $"image-{i}:latest")).ToArray();
        var browser = new Browser { ImageSnapshot = Ready(rows) }; using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        Check(view.Rows.Count == 20000, "Every loaded image can be browsed");
        for (int i = 0; i < 100; ++i) view.Search($"image-{i}"); view.Search("image-19999:"); await ui.Settle(view);
        Check(view.Rows.Single().Id == rows[^1].Id && browser.SelectCalls == 0 && browser.RefreshCalls == 0, "Canceled/superseded searches publish only latest result without Engine reads");
    }
    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new();
        public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void Pump() { while (queue.TryDequeue(out var action)) action(); }
        public async Task WaitQueued()
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (queue.IsEmpty) await Task.Delay(1, timeout.Token); }
        public async Task Settle(ImageBrowserView view)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            do { Pump(); if (!view.Filtering && !view.Inspect.Busy && !view.DetailMessage.StartsWith("Preparing") && queue.IsEmpty) return; await Task.Delay(1, timeout.Token); } while (true);
        }
    }
    private sealed class Browser : IImageBrowser
    {
        private EventHandler? changed;
        public int Subscribers, SelectCalls, ClearCalls, RefreshCalls, DetailRefreshCalls, CopyCalls;
        public string? Copied, LastSelected;
        public ImageBrowsingSnapshot ImageSnapshot { get; set; } = new(0, ResourceReadState.Unavailable, []);
        public event EventHandler? ImagesChanged { add { changed += value; ++Subscribers; } remove { changed -= value; --Subscribers; } }
        public void Publish(ImageBrowsingSnapshot snapshot) { ImageSnapshot = snapshot; changed?.Invoke(this, EventArgs.Empty); }
        public void RefreshImages() => ++RefreshCalls;
        public void SelectImage(string? id)
        { ++SelectCalls; LastSelected = id; Publish(ImageSnapshot with { SelectedImageId = id, InspectionState = ResourceReadState.Loading, Inspection = null, InspectionFailure = null }); }
        public void RefreshImageInspection() => ++DetailRefreshCalls;
        public void ClearImageInspection()
        { ++ClearCalls; Publish(ImageSnapshot with { InspectionState = ResourceReadState.NotRequested, Inspection = null, InspectionFailure = null }); }
        public void Copy(string text) { ++CopyCalls; Copied = text; }
    }
}
