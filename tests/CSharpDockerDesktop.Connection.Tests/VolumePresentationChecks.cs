using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class VolumePresentationChecks
{
    private const string A = "Volume-Exact-Full-Name", B = "volume-exact-full-name";
    private static VolumeSummary Row(string name, bool missing = false) => new(name, missing ? null : "local", missing ? null : "local-scope", missing ? null : "/sensitive/mount", missing ? null : new Dictionary<string, string> { ["owner"] = "Example", ["empty"] = "" });
    private static VolumeBrowsingSnapshot Ready(params VolumeSummary[] rows) => new(1, ResourceReadState.Ready, rows, LatestListing: new(rows, []), Completeness: VolumeListCompleteness.Complete);
    private static VolumeInspection Detail(string name, string raw) => new(name, "local", "", null, new Dictionary<string, string> { ["TOKEN"] = "private-exact", ["empty"] = "" }, raw);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task MetadataSearchAndExactIdentity()
    {
        var ui = new Dispatcher(); var browser = new Browser { VolumeSnapshot = Ready(Row(A), Row(B, true), new(" spaced name ", "", "", "", new Dictionary<string, string>())) };
        using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        var missing = view.Rows.Single(r => r.Name == B); var empty = view.Rows.Single(r => r.Name == " spaced name ");
        Check(missing.Driver == "unavailable (null)" && missing.Labels == "unavailable (null)" && empty.Driver == "empty string" && empty.Mountpoint == "empty string" && empty.Labels == "none (empty collection)", "Unavailable metadata differs from valid empty fields and labels");
        Check(view.Rows.Single(r => r.Name == A).Labels.Contains("empty: empty string"), "Empty label value stays available");
        foreach (string query in new[] { "LOCAL", "local-scope", "/sensitive/", "OWNER", "example", "empty" })
        { view.Search(query); await ui.Settle(view); Check(view.Rows.Single().Name == A, "Loaded metadata filtering missed " + query); }
        view.Search(" spaced name "); await ui.Settle(view); Check(view.Rows.Single().Name == " spaced name ", "Search doesn't trim text");
        view.Search(""); await ui.Settle(view); view.Select("Volume-Exact"); Check(view.SelectedName is null, "Prefix is not an identity");
        view.Select(A); view.CopyIdentity(browser.Copy); Check(browser.Copied == A && browser.SelectCalls == 0, "Explicit full-name copying without Engine work");
        view.Select(B); view.OpenSelected(); ui.Pump(); Check(browser.LastSelected == B && browser.SelectCalls == 1 && view.SelectedName == B, "Case-distinct names select exact identity");
        view.Select(" spaced name "); view.CopyIdentity(browser.Copy); Check(browser.Copied == " spaced name ", "Identity is never trimmed or normalized");
        Check(browser.RefreshCalls == 0, "Search and selection don't refresh Engine");
    }
    public static async Task EmptyEvidenceMatrix()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        Check(!view.CanRefresh && view.ListMessage.Contains("unavailable"), "Connection unavailable");
        browser.Publish(new(1, ResourceReadState.NotRequested, [])); await ui.Settle(view); Check(view.ListMessage.Contains("not been requested"), "Not requested");
        browser.Publish(new(1, ResourceReadState.Loading, [])); await ui.Settle(view); Check(view.ListMessage.StartsWith("Loading"), "Initial loading");
        foreach (var warnings in new IReadOnlyList<string>?[] { null, Array.Empty<string>() })
        {
            browser.Publish(Ready() with { LatestListing = new(Array.Empty<VolumeSummary>(), warnings) }); await ui.Settle(view);
            Check(view.ListMessage.StartsWith("No volumes") && view.CanRefresh, "Only complete authoritative emptiness is definitive");
            view.ShowEvidence(true); await ui.Settle(view); Check(view.Evidence.Contains(warnings is null ? "Warnings: null" : "Warnings: empty"), "Null/empty warning collection distinguished");
        }
        browser.Publish(Ready() with { LatestListing = new(Array.Empty<VolumeSummary>(), ["warning"]), Completeness = VolumeListCompleteness.Incomplete }); await ui.Settle(view);
        Check(!view.ListMessage.Contains("No volumes") && view.ListMessage.Contains("Incomplete") && view.WarningMessage.Contains("1"), "Warning-bearing Ready empty is incomplete");
        browser.Publish(Ready() with { State = ResourceReadState.Unavailable, LatestListing = new(null, null), Completeness = VolumeListCompleteness.Unavailable }); await ui.Settle(view);
        Check(!view.ListMessage.Contains("No volumes") && view.ListMessage.Contains("listing unavailable") && view.CanRefresh, "Null listing empty remains refreshable without connection replacement");
        browser.Publish(Ready() with { Completeness = VolumeListCompleteness.Unavailable, LatestListing = null }); await ui.Settle(view);
        Check(!view.ListMessage.Contains("No volumes"), "Ready by itself cannot assert successful emptiness");
        browser.Publish(Ready() with { State = ResourceReadState.Failed, Failure = new(EngineResourceFailureKind.AccessDenied, "Denied", 403, "PRIVATE") }); await ui.Settle(view);
        Check(view.ListMessage.Contains("AccessDenied (HTTP 403)") && !view.ListMessage.Contains("PRIVATE") && !view.ListMessage.Contains("No volumes"), "Safe failure is not successful empty");
    }
    public static async Task RetainedRowsAndDetailBrowsing()
    {
        var ui = new Dispatcher(); var browser = new Browser { VolumeSnapshot = Ready(Row(A), Row(B)) }; using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        view.Select(A); view.OpenSelected(); ui.Pump(); browser.Publish(browser.VolumeSnapshot with { Inspection = Detail(A, "{\"Name\":\"exact\"}"), InspectionState = ResourceReadState.Ready }); await ui.Settle(view);
        foreach (var listing in new VolumeListing[] { new([], ["partial"]), new(null, ["unavailable"]), new(null, null) })
        {
            browser.Publish(browser.VolumeSnapshot with { State = listing.Volumes is null ? ResourceReadState.Unavailable : ResourceReadState.Ready, LatestListing = listing,
                Completeness = listing.Volumes is null ? VolumeListCompleteness.Unavailable : VolumeListCompleteness.Incomplete, HasRetainedRows = true }); await ui.Settle(view);
            Check(view.Rows.Count == 2 && view.SelectedName == A && view.DetailOpen && view.Overview.Contains("Name: " + A) && view.CanOpen && view.CanRefresh && view.ListMessage.Contains("Retained"), "Partial/null collection retains usable selection/details");
            view.CopyIdentity(browser.Copy); Check(browser.Copied == A, "Retained row identity copy");
        }
        foreach (var state in new[] { ResourceReadState.Loading, ResourceReadState.Failed })
        {
            browser.Publish(browser.VolumeSnapshot with { State = state, Failure = state == ResourceReadState.Failed ? new(EngineResourceFailureKind.Conflict, "Refresh failed", 409, "PRIVATE") : null }); await ui.Settle(view);
            Check(view.Rows.Count == 2 && view.DetailOpen && view.CanOpen && view.Overview.Length > 0, "Refreshing/failed listings preserve useful detail");
        }
        view.CloseDetail(); ui.Pump(); Check(browser.ClearCalls == 1 && view.SelectedName == A, "Close clears inspection ownership without clearing table selection");
        view.Select(B); view.OpenSelected(); ui.Pump(); Check(browser.LastSelected == B, "Retained rows remain openable even when latest listing state is Failed");
        view.CloseDetail(); ui.Pump(); view.OpenSelected(); ui.Pump(); Check(browser.DetailRefreshCalls == 1, "Reopening same Application identity explicitly reloads cleared inspection");
    }
    public static async Task WarningsReplacementAndDeliberateReveal()
    {
        const string warning = "  PRIVATE warning\n\ttrailing  ";
        var ui = new Dispatcher(); var browser = new Browser { VolumeSnapshot = Ready(Row(A)) with { LatestListing = new([], [warning, "", warning, null!]), Completeness = VolumeListCompleteness.Incomplete } };
        using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        Check(view.Evidence == "" && !view.ShowingEvidence && view.WarningMessage.Contains("4") && !view.ListMessage.Contains("PRIVATE"), "Original warning content is hidden by default");
        view.ShowEvidence(true); await ui.Settle(view); string evidence = view.Evidence;
        int first = evidence.IndexOf(warning, StringComparison.Ordinal), second = evidence.IndexOf(warning, first + warning.Length, StringComparison.Ordinal);
        Check(first >= 0 && second > first && evidence.Contains("Warning 2 · 0 characters:\n\n") && evidence.Contains("Warning 4: null"), "Exact whitespace, ordering, duplicates and null/empty warning entries");
        view.ShowEvidence(false); Check(view.Evidence == "", "Explicit hide clears exposed evidence");
        view.ShowEvidence(true); await ui.Settle(view);
        browser.Publish(browser.VolumeSnapshot with { LatestListing = new(null, ["REPLACEMENT"]) }); Check(view.Evidence == "", "Live evidence fence rejects replaced warning before queued notification"); await ui.Settle(view);
        Check(!view.ShowingEvidence && view.Snapshot.Warnings!.Single() == "REPLACEMENT", "Replacement is not accumulated and requires new reveal");
        view.ShowEvidence(true); await ui.WaitQueued(); browser.Publish(browser.VolumeSnapshot with { LatestListing = new(null, []) }); await ui.Settle(view);
        Check(view.Evidence == "" && !view.ShowingEvidence, "Late evidence preparation cannot reveal superseded warnings");
        browser.Publish(browser.VolumeSnapshot with { Failure = new(EngineResourceFailureKind.AccessDenied, "Denied", 403, "  SECRET original  "), InspectionFailure = new(EngineResourceFailureKind.NotFound, "Missing", 404, "") }); await ui.Settle(view);
        Check(view.ListMessage.Contains("HTTP 403") && view.ListMessage.Contains("HTTP 404") && !view.ListMessage.Contains("SECRET"), "Readable classified failures and hidden originals");
        view.ShowEvidence(true); await ui.Settle(view); Check(view.Evidence.Contains("  SECRET original  \n") && view.Evidence.Contains("Inspection daemon message · 0 characters"), "Deliberate exact daemon message access including empty string");
        browser.Publish(new(1, ResourceReadState.Unavailable, [], Failure: new(EngineResourceFailureKind.TransportLoss, "Lost", 500, "original loss"))); await ui.Settle(view);
        view.ShowEvidence(true); await ui.Settle(view); Check(view.Evidence.Contains("original loss"), "Current disconnected failure original remains revealable");
    }
    public static async Task OverviewInspectAndExternalInvalidation()
    {
        const string raw = "{ \"Name\":\"volume\", \"unknown\":{\"n\":12345678901234567890.123456789,\"n\":null}, \"empty\":[],\"token\":\"SECRET exact\" }";
        var ui = new Dispatcher(); var browser = new Browser { VolumeSnapshot = Ready(Row(A), Row(B)) }; using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        view.Select(A); view.OpenSelected(); ui.Pump(); browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, raw) }); await ui.Settle(view);
        Check(view.Overview.Contains("Name: " + A) && view.Overview.Contains("Scope: empty string") && view.Overview.Contains("Mountpoint: unavailable (null)") && view.Overview.Contains("TOKEN: private-exact") && view.Overview.Contains("empty: empty string"), "Overview preserves actual selected metadata and null/empty distinctions");
        Check(VolumeDisplay.Overview(Detail(A, "{}") with { Labels = null }).Contains("unavailable (null)") && VolumeDisplay.Overview(Detail(A, "{}") with { Labels = new Dictionary<string, string>() }).Contains("none (empty collection)"), "Overview absent/empty labels");
        view.ShowInspect(true); await ui.Settle(view); view.Inspect.Select(0); view.Inspect.Copy(false, browser.Copy); await ui.Settle(view); Check(browser.Copied == raw, "Root Inspect copy preserves exact source");
        view.Inspect.Search("SECRET"); await ui.Settle(view); view.Inspect.Select(view.Inspect.Rows[^1].NodeId); view.Inspect.Copy(false, browser.Copy); await ui.Settle(view); Check(browser.Copied == "\"SECRET exact\"", "Deliberate exact sensitive value copy");
        view.Inspect.Copy(true, browser.Copy); await ui.Settle(view); Check(browser.Copied == "$[\"token\",0]", "Reusable unambiguous path copy");
        view.ShowInspect(false); view.ShowInspect(true); await ui.Settle(view); Check(view.Inspect.Query == "SECRET" && browser.SelectCalls == 1, "Tabs preserve viewer and don't reread");
        browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null });
        Check(view.Overview == "" && view.Inspect.Rows.Count == 0 && !view.Inspect.CanCopy, "Matching event immediately hides obsolete exposed content before UI dispatch");
        int copies = browser.CopyCalls; view.Inspect.Copy(false, browser.Copy); await ui.Settle(view); Check(browser.CopyCalls == copies && view.DetailOpen, "Invalidation fences copy while keeping selected detail pending");
        browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"fresh\":true}") }); await ui.Settle(view); Check(view.Inspect.Rows.Any(r => r.Text.Contains("fresh")), "Replacement Inspect is current");
        browser.Publish(browser.VolumeSnapshot with { SelectedVolumeName = B, Inspection = Detail(B, "{}") }); Check(!view.CopyIdentity(browser.Copy), "External selection immediately fences old identity"); await ui.Settle(view);
        Check(!view.DetailOpen && view.SelectedName is null && view.Inspect.Rows.Count == 0 && view.Overview == "", "External selected-name replacement clears details");
    }
    public static async Task QueuedParsingFilteringOverviewAndCopyFences()
    {
        var ui = new Dispatcher(); var browser = new Browser { VolumeSnapshot = Ready(Row(A)) }; using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        view.Select(A); view.OpenSelected(); ui.Pump(); browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"old\":true}") }); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
        view.Inspect.Select(0); view.Inspect.Copy(false, browser.Copy); await ui.WaitQueued(); int copies = browser.CopyCalls;
        browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null }); ui.Pump(); await ui.Settle(view);
        Check(browser.CopyCalls == copies && view.Inspect.Rows.Count == 0 && view.Overview == "", "Queued copy ahead of event notification is rejected by live snapshot");
        browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"large\":[" + string.Join(",", Enumerable.Range(0, 50000)) + "]}") }); ui.Pump();
        // Both Overview and Inspect publication are pending; replace the connection before they drain.
        browser.Publish(Ready(Row(B)) with { ConnectionGeneration = 2 }); await ui.Settle(view);
        Check(view.Overview == "" && view.Inspect.Rows.Count == 0 && !view.DetailOpen && view.SelectedName is null && view.Rows.Single().Name == B, "Generation rejects queued parsing/Overview and preserves only new rows");
        view.Search("volume"); await ui.WaitQueued(); browser.Publish(browser.VolumeSnapshot with { Volumes = [Row("new-row")], LatestListing = new([Row("new-row")], []) }); ui.Pump(); await ui.Settle(view);
        Check(view.Rows.Count == 0, "Queued metadata filter cannot publish an obsolete source in the same generation");
        view.Search(""); await ui.Settle(view); view.Select("new-row"); browser.Publish(new(2, ResourceReadState.Unavailable, []));
        Check(!view.CanOpen && !view.CanRefresh && !view.CopyIdentity(browser.Copy) && view.Rows.Count == 0, "Same-generation shared invalidation immediately fences all admissions"); await ui.Settle(view);
        Check(view.SelectedName is null && view.Rows.Count == 0, "Shared invalidation clears stale selection");
    }
    public static async Task CompleteDisappearanceAndNotFound()
    {
        var ui = new Dispatcher(); var browser = new Browser { VolumeSnapshot = Ready(Row(A), Row(B)) }; using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        view.Select(A); view.OpenSelected(); ui.Pump(); browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{}") }); await ui.Settle(view);
        browser.Publish(Ready(Row(B))); Check(!view.CopyIdentity(browser.Copy) && view.Overview == "", "Complete-list disappearance immediately fences current selection"); await ui.Settle(view);
        Check(view.SelectedName is null && !view.DetailOpen && browser.ClearCalls == 0, "Authoritative disappearance closes already-cleared Application detail");
        view.Select(B); view.OpenSelected(); ui.Pump(); browser.Publish(browser.VolumeSnapshot with { Volumes = [], SelectedVolumeName = null, Inspection = null, InspectionState = ResourceReadState.Failed,
            InspectionFailure = new(EngineResourceFailureKind.NotFound, "Volume disappeared", 404, "private missing" ) }); await ui.Settle(view);
        Check(view.SelectedName is null && !view.DetailOpen && view.Overview == "" && view.Inspect.Rows.Count == 0 && view.ListMessage.Contains("NotFound (HTTP 404)"), "Current Inspect NotFound clears details while preserving readable failure");
    }
    public static async Task NotificationsNavigationAndRepeatedAttachment()
    {
        var ui = new Dispatcher(); var browser = new Browser { VolumeSnapshot = Ready(Row(A)) }; using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        await Task.Run(() => { for (int i = 0; i < 200; ++i) browser.Publish(Ready(Row(i == 199 ? B : A))); });
        Check(ui.Count == 1, "Off-thread notification bursts coalesce"); await ui.Settle(view); Check(view.Rows.Single().Name == B, "Coalesced callback reads latest state");
        view.Select(B); view.OpenSelected(); ui.Pump(); browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(B, "{\"old\":true}") }); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
        view.Inspect.Select(0); view.Inspect.Copy(false, browser.Copy); await ui.WaitQueued(); int copies = browser.CopyCalls, changes = 0; view.Changed += (_, _) => ++changes;
        view.Dispose(); int before = changes; ui.Pump(); Check(browser.Subscribers == 0 && browser.ClearCalls == 1 && changes == before && browser.CopyCalls == copies && view.Inspect.Rows.Count == 0 && view.Rows.Count == 0 && view.Snapshot.Inspection is null, "Navigation clears sensitive data, cancels copy and prevents late publication");
        view.Refresh(); view.OpenSelected(); view.Search("inactive"); view.ShowEvidence(true); view.ShowEvidence(false); Check(browser.RefreshCalls == 0 && browser.SelectCalls == 1 && changes == before, "Detached commands never publish or read");
        for (int i = 0; i < 12; ++i) { view.Attach(); browser.Publish(Ready(Row(A))); view.Dispose(); }
        view.Attach(); browser.Publish(Ready(Row(B))); await ui.Settle(view); Check(browser.Subscribers == 1 && view.Rows.Single().Name == B && !view.DetailOpen, "Old notifications never suppress a fresh attachment");
        view.Select(B); view.OpenSelected(); ui.Pump(); view.Dispose(); Check(browser.ClearCalls == 2 && browser.VolumeSnapshot.InspectionState == ResourceReadState.NotRequested, "Leaving during pending inspection clears Application ownership");
        view.Attach(); await ui.Settle(view); view.Select(B); view.OpenSelected(); ui.Pump();
        browser.Publish(Ready(Row(A)) with { ConnectionGeneration = 2, SelectedVolumeName = A, InspectionState = ResourceReadState.Loading });
        view.Dispose(); ui.Pump(); Check(browser.ClearCalls == 2 && browser.VolumeSnapshot.SelectedVolumeName == A && browser.VolumeSnapshot.InspectionState == ResourceReadState.Loading, "Navigation before queued generation notification cannot clear replacement-owned inspection");
        view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        browser.Publish(browser.VolumeSnapshot with { Volumes = [Row(B)], SelectedVolumeName = B, InspectionState = ResourceReadState.Loading }); await ui.Settle(view);
        Check(browser.ClearCalls == 2 && !view.DetailOpen && view.SelectedName is null && browser.VolumeSnapshot.SelectedVolumeName == B, "External selection with disappearance cannot clear another owner's inspection");
    }
    public static async Task LargeCollectionsAndSupersededSearch()
    {
        var ui = new Dispatcher(); var rows = Enumerable.Range(0, 20000).Select(i => new VolumeSummary($"volume-{i:D5}", "local", "local", "/mount/" + i, new Dictionary<string, string> { ["search"] = $"label-{i:D5}" })).ToArray();
        var browser = new Browser { VolumeSnapshot = Ready(rows) }; using var view = new VolumeBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); Check(view.Rows.Count == 20000, "All loaded rows can be browsed");
        for (int i = 0; i < 100; i++) view.Search("label-" + i); view.Search("label-19999"); await ui.Settle(view); Check(view.Rows.Single().Name == rows[^1].Name, "Only latest metadata search publishes");
        view.Select(rows[^1].Name); view.OpenSelected(); ui.Pump();
        string raw = "{\"large\":[" + string.Join(",", Enumerable.Range(0, 50000).Select(i => "{\"value\":\"fixture-" + i + "\"}")) + "]}";
        browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(rows[^1].Name, raw) }); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
        view.Inspect.Search("fixture-49999"); await ui.Settle(view); Check(view.Inspect.Rows.Count == 4 && view.Inspect.Rows[^1].Text.Contains("fixture-49999"), "Inspect searches deep collapsed content");
        view.Inspect.Search("fixture-"); await ui.Settle(view); Check(view.Inspect.Rows.Count == 100002, "Large Inspect projection remains complete with finite virtualized UI viewport");
        view.Inspect.Search(""); await ui.Settle(view); view.Inspect.Select(1); view.Inspect.SetExpanded(true); await ui.Settle(view); Check(view.Inspect.Rows.Count == 50002, "Large JSON expansion");
        view.Inspect.SetExpanded(false); await ui.Settle(view); Check(view.Inspect.Rows.Count == 2, "Large JSON collapse");
        view.Inspect.Search("fixture-"); view.Dispose(); ui.Pump(); Check(view.Inspect.Rows.Count == 0 && browser.ClearCalls == 1 && browser.RefreshCalls == 0, "Navigation stays synchronous and cancels pending large projection without extra reads");
    }
    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new();
        public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void Pump() { while (queue.TryDequeue(out var action)) action(); }
        public async Task WaitQueued() { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (queue.IsEmpty) await Task.Delay(1, timeout.Token); }
        public async Task Settle(VolumeBrowserView view)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do { Pump(); if (!view.Filtering && !view.Inspect.Busy && !view.PreparingEvidence && !view.DetailMessage.StartsWith("Preparing") && queue.IsEmpty) return; await Task.Delay(1, timeout.Token); } while (true);
        }
    }
    private sealed class Browser : IVolumeBrowser
    {
        private EventHandler? changed;
        public int Subscribers, SelectCalls, ClearCalls, RefreshCalls, DetailRefreshCalls, CopyCalls;
        public string? Copied, LastSelected;
        public VolumeBrowsingSnapshot VolumeSnapshot { get; set; } = new(0, ResourceReadState.Unavailable, []);
        public event EventHandler? VolumesChanged { add { changed += value; ++Subscribers; } remove { changed -= value; --Subscribers; } }
        public void Publish(VolumeBrowsingSnapshot snapshot) { VolumeSnapshot = snapshot; changed?.Invoke(this, EventArgs.Empty); }
        public void RefreshVolumes() => ++RefreshCalls;
        public void SelectVolume(string? name)
        { ++SelectCalls; LastSelected = name; if (VolumeSnapshot.SelectedVolumeName == name) return; Publish(VolumeSnapshot with { SelectedVolumeName = name, InspectionState = ResourceReadState.Loading, Inspection = null, InspectionFailure = null }); }
        public void RefreshVolumeInspection() { ++DetailRefreshCalls; Publish(VolumeSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null, InspectionFailure = null }); }
        public void ClearVolumeInspection() { ++ClearCalls; Publish(VolumeSnapshot with { InspectionState = ResourceReadState.NotRequested, Inspection = null, InspectionFailure = null }); }
        public void Copy(string text) { ++CopyCalls; Copied = text; }
    }
}
