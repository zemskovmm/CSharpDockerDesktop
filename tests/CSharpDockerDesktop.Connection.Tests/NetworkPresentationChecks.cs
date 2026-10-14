using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class NetworkPresentationChecks
{
    private static readonly string A = "Opaque-ID-é-with whitespace ", B = "opaque-ID-é-with whitespace ";
    private static NetworkSummary Row(string id, string? tag = null) => new(id, tag, tag is null ? null : "bridge", tag is null ? null : "", tag is null ? null : false,
        tag is null ? null : new Dictionary<string, string> { ["label"] = "search-label", ["empty"] = "" });
    private static NetworkBrowsingSnapshot Ready(params NetworkSummary[] rows) => new(1, ResourceReadState.Ready, rows);
    private static NetworkInspection Detail(string id, string raw) => new(id, "", "bridge", null, false, new Dictionary<string, string>(),
        new("default", [new("10.1.0.0/16", "", "10.1.0.1", new Dictionary<string, string> { ["dns"] = "10.1.0.53" })], null),
        new Dictionary<string, NetworkAttachment> { ["full-CONTAINER-é "] = new("", "full-ENDPOINT-é ", null, "10.1.0.2/16", "2001:db8::2/64") }, raw);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task MetadataAndSearch()
    {
        var ui = new Dispatcher(); var browser = new Browser { NetworkSnapshot = Ready(Row(A, "network-first"), Row(B)) };
        using var view = new NetworkBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        foreach (string query in new[] { "NETWORK-FIRST", "bridge", "search-label", "false" })
        { view.Search(query); await ui.Settle(view); Check(view.Rows.Single().Id == A, "Search loaded metadata"); }
        view.Search(""); await ui.Settle(view);
        Check(view.Rows[0].DriverScope.Contains("empty string") && view.Rows[0].Internal == "false" && view.Rows[1].Internal.Contains("unavailable"), "Empty and false differ from unavailable");
        view.Select(A[..12]); Check(view.SelectedId is null, "Prefix cannot select opaque ID");
        view.Select(A); view.CopyIdentity(browser.Copy); Check(browser.Copied == A && browser.SelectCalls == 0, "Deliberate complete ordinal identity copy");
        view.OpenSelected(); ui.Pump(); browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{}") }); await ui.Settle(view);
        foreach (string value in new[] { "Full identity: " + A, "Name: empty string", "Scope: unavailable", "Internal: false", "10.1.0.0/16", "IP range: empty string", "10.1.0.53", "full-CONTAINER-é ", "full-ENDPOINT-é ", "10.1.0.2/16", "2001:db8::2/64", "cluster-wide completeness or deletion eligibility" })
            Check(view.Overview.Contains(value), "Overview missing " + value);
        var missing = Detail(A, "{}") with { Ipam = null, Containers = null };
        Check(NetworkDisplay.Overview(missing).Contains("IPAM: unavailable") && NetworkDisplay.Overview(missing).Contains("Attachments: unavailable"), "Unavailable nested evidence");
        Check(NetworkDisplay.Overview(missing with { Ipam = new("", [], new Dictionary<string, string>()), Containers = new Dictionary<string, NetworkAttachment>() }).Contains("Attachments: none (empty collection)"), "Observed empty attachments");
        view.Search("unmatched"); await ui.Settle(view); Check(view.SelectedId == A && view.DetailOpen && view.Rows.Count == 0, "Filter preserves exact selected identity");
        view.CloseDetail(); await ui.Settle(view); view.OpenSelected(); Check(browser.DetailRefreshCalls == 1, "Reopening explicitly cleared same identity refreshes inspection");
    }
    public static async Task StatesAndCoalescing()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new NetworkBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        Check(view.ListMessage.Contains("unavailable") && !view.CanRefresh, "Unavailable is not empty");
        browser.Publish(new(1, ResourceReadState.NotRequested, [])); await ui.Settle(view); Check(view.ListMessage.Contains("not been requested"), "Not requested");
        browser.Publish(new(1, ResourceReadState.Loading, [])); await ui.Settle(view); Check(view.ListMessage.Contains("Loading"), "Initial loading");
        browser.Publish(Ready()); await ui.Settle(view); Check(view.ListMessage.Contains("No networks") && view.ListMessage.Contains("authoritative"), "Authoritative empty");
        browser.Publish(Ready(Row(A))); await ui.Settle(view);
        browser.Publish(browser.NetworkSnapshot with { State = ResourceReadState.Loading }); await ui.Settle(view); Check(view.Rows.Count == 1 && view.ListMessage.Contains("Retained"), "Loading retained rows");
        var listFailure = new EngineResourceFailure(EngineResourceFailureKind.AccessDenied, "Safe denied", 403, "  PRIVATE list\n  ");
        var detailFailure = new EngineResourceFailure(EngineResourceFailureKind.Conflict, "Safe conflict", 409, "PRIVATE details");
        browser.Publish(browser.NetworkSnapshot with { State = ResourceReadState.Failed, Failure = listFailure, InspectionFailure = detailFailure }); await ui.Settle(view);
        Check(view.Rows.Count == 1 && view.ListMessage.Contains("HTTP 403") && !view.ListMessage.Contains("PRIVATE") && view.Evidence == "", "Safe failed refresh and independent detail evidence");
        view.ShowEvidence(true); await ui.Settle(view); Check(view.Evidence.Contains("  PRIVATE list\n  ") && view.Evidence.Contains("PRIVATE details"), "Original messages deliberately revealed exactly");
        browser.Publish(browser.NetworkSnapshot with { Failure = listFailure with { DaemonMessage = "replacement" } }); Check(view.Evidence == "", "Live replacement fences old originals"); await ui.Settle(view); Check(!view.ShowingEvidence, "Replacement hides reveal");
        view.Refresh(); Check(browser.RefreshCalls == 1, "Explicit refresh");
        await Task.Run(() => { for (int i = 0; i < 200; ++i) browser.Publish(Ready(Row(i == 199 ? B : A))); });
        Check(ui.Count == 1, "Notifications coalesce"); await ui.Settle(view); Check(view.Rows.Single().Id == B, "Reads latest snapshot");
    }
    public static async Task IdentityOverviewAndAdmission()
    {
        var ui = new Dispatcher(); var browser = new Browser { NetworkSnapshot = Ready(Row(A), Row(B)) }; using var view = new NetworkBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        view.Select(A); view.OpenSelected(); ui.Pump(); Check(view.DetailMessage.StartsWith("Loading"), "Slow read keeps detail pending");
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Failed, InspectionFailure = new(EngineResourceFailureKind.AccessDenied, "Denied", 403, "PRIVATE"), State = ResourceReadState.Ready }); await ui.Settle(view);
        Check(view.DetailMessage.Contains("HTTP 403") && !view.DetailMessage.Contains("PRIVATE") && !view.ListMessage.Contains("Denied"), "Independent safe list/detail diagnostics");
        view.RefreshDetail(); Check(browser.DetailRefreshCalls == 1, "Explicit detail refresh");
        var attachments = Enumerable.Range(0, 20000).ToDictionary(i => "complete-container-" + i, i => new NetworkAttachment("name", "complete-endpoint-" + i, null, "10.0.0.2", null));
        var detail = Detail(A, "{}") with { Containers = attachments };
        string text = NetworkDisplay.Overview(detail);
        Check(text.Length < NetworkDisplay.OverviewLimit + 200 && text.Contains("Attachments · 20000") && text.Contains("Overview preview is bounded") && !text.Contains("complete-container-101"), "Large nested projection is bounded with full evidence in Inspect");
        string giant = new('x', 1000000); Check(NetworkDisplay.Overview(detail with { Name = giant }).Length < NetworkDisplay.OverviewLimit + 200, "Giant scalar text layout is bounded");
        browser.Publish(new(2, ResourceReadState.Ready, [Row(A)])); Check(!view.CopyIdentity(browser.Copy) && view.Overview == "", "Connection replacement fences reads and copying before dispatcher"); await ui.Settle(view); Check(!view.DetailOpen && view.SelectedId is null, "Replacement clears detail");
    }
    public static async Task InspectContinuityAndExternalInvalidation()
    {
        var ui = new Dispatcher(); var browser = new Browser { NetworkSnapshot = Ready(Row(A), Row(B)) };
        using var view = new NetworkBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        const string raw = "{ \"Env\":[\"SECRET=exact\"], \"unknown\":{\"n\":12345678901234567890.123456789012345,\"n\":null}, \"empty\":[] }";
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, raw) }); ui.Pump();
        view.ShowInspect(true); await ui.Settle(view); view.Inspect.Select(0); view.Inspect.Copy(false, browser.Copy); await ui.Settle(view);
        Check(browser.Copied == raw, "Root value is exact daemon JSON with whitespace, duplicates, numbers and sensitive values");
        view.Inspect.Search("SECRET"); await ui.Settle(view); view.Inspect.Select(view.Inspect.Rows[^1].NodeId); view.Inspect.Copy(false, browser.Copy); await ui.Settle(view);
        Check(browser.Copied == "\"SECRET=exact\"", "Deliberate sensitive scalar copy remains exact");
        view.Inspect.Copy(true, browser.Copy); await ui.Settle(view); Check(browser.Copied == "$[\"Env\",0][0]", "Exact unambiguous path");
        view.ShowInspect(false); view.ShowInspect(true); await ui.Settle(view);
        Check(view.Inspect.Query == "SECRET" && browser.SelectCalls == 1, "Tabs preserve viewer without reread");
        int copies = browser.CopyCalls;
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null });
        view.Inspect.Copy(false, browser.Copy); Check(browser.CopyCalls == copies && view.Overview == "", "Event invalidation rejects obsolete copy before dispatch");
        ui.Pump(); Check(view.Inspect.Rows.Count == 0 && !view.Inspect.CanCopy && view.DetailOpen, "Selected network event immediately clears exposed Inspect when applied");
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"fresh\":true}") }); await ui.Settle(view);
        Check(view.Inspect.Rows.Any(row => row.Text.Contains("fresh")), "Authoritative event replacement exposes only current Inspect");
        browser.Publish(browser.NetworkSnapshot with { SelectedNetworkId = B, Inspection = Detail(B, "{\"other\":true}") });
        Check(!view.CopyIdentity(browser.Copy), "External selection replacement fences identity copy");
        await ui.Settle(view); Check(!view.DetailOpen && view.SelectedId is null && view.Inspect.Rows.Count == 0 && view.Overview == "" && !view.CopyIdentity(browser.Copy), "Selection replacement removes previous details and copy admission");
    }
    public static async Task StaleQueuedResultsAndCopy()
    {
        var ui = new Dispatcher(); var browser = new Browser { NetworkSnapshot = Ready(Row(A)) };
        using var view = new NetworkBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"old\":true}") }); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
        view.Inspect.Select(0); int copies = browser.CopyCalls; view.Inspect.Copy(false, browser.Copy); await ui.WaitQueued();
        // The copy completion is ahead of the invalidation notification. Live snapshot admission still rejects it.
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null }); ui.Pump(); await ui.Settle(view);
        Check(browser.CopyCalls == copies && view.Inspect.Rows.Count == 0, "Undrained selected-network event fences queued copy");
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"large\":[" + string.Join(",", Enumerable.Range(0, 50000)) + "]}") }); ui.Pump();
        browser.Publish(new(2, ResourceReadState.Unavailable, [])); await ui.Settle(view);
        Check(view.Inspect.Rows.Count == 0 && view.Overview == "" && view.Rows.Count == 0 && view.SelectedId is null, "Disconnect rejects pending large parse and overview");
        browser.Publish(Ready(Row(A))); await ui.Settle(view); view.Search(new string('a', 12)); await ui.WaitQueued();
        browser.Publish(new(3, ResourceReadState.Ready, [Row(B)])); ui.Pump(); await ui.Settle(view);
        Check(view.Rows.Count == 0 && view.Snapshot.ConnectionGeneration == 3, "A queued filter cannot publish into a different generation");
        view.Search(""); await ui.Settle(view); view.Select(B); view.OpenSelected(); ui.Pump();
        browser.Publish(browser.NetworkSnapshot with { Networks = [], SelectedNetworkId = null, Inspection = null });
        Check(!view.CopyIdentity(browser.Copy), "Authoritative disappearance immediately rejects full identity copy");
        await ui.Settle(view); Check(!view.DetailOpen && view.SelectedId is null, "Authoritative disappearance clears owned detail");
    }
    public static async Task NavigationAndReattachment()
    {
        var ui = new Dispatcher(); var browser = new Browser { NetworkSnapshot = Ready(Row(A)) };
        using var view = new NetworkBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        browser.Publish(browser.NetworkSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"old\":true}") }); ui.Pump(); view.ShowInspect(true); await ui.Settle(view);
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
        var ui = new Dispatcher(); var rows = Enumerable.Range(0, 20000).Select(i => Row("sha256:" + i.ToString("x64"), $"network-{i}:latest")).ToArray();
        var browser = new Browser { NetworkSnapshot = Ready(rows) }; using var view = new NetworkBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        Check(view.Rows.Count == 20000, "Every loaded network can be browsed");
        for (int i = 0; i < 100; ++i) view.Search($"network-{i}"); view.Search("network-19999:"); await ui.Settle(view);
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
        public async Task Settle(NetworkBrowserView view)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            do { Pump(); if (!view.Filtering && !view.PreparingEvidence && !view.Inspect.Busy && !view.DetailMessage.StartsWith("Preparing") && queue.IsEmpty) return; await Task.Delay(1, timeout.Token); } while (true);
        }
    }
    private sealed class Browser : INetworkBrowser
    {
        private EventHandler? changed;
        public int Subscribers, SelectCalls, ClearCalls, RefreshCalls, DetailRefreshCalls, CopyCalls;
        public string? Copied, LastSelected;
        public NetworkBrowsingSnapshot NetworkSnapshot { get; set; } = new(0, ResourceReadState.Unavailable, []);
        public event EventHandler? NetworksChanged { add { changed += value; ++Subscribers; } remove { changed -= value; --Subscribers; } }
        public void Publish(NetworkBrowsingSnapshot snapshot) { NetworkSnapshot = snapshot; changed?.Invoke(this, EventArgs.Empty); }
        public void RefreshNetworks() => ++RefreshCalls;
        public void SelectNetwork(string? id)
        { ++SelectCalls; LastSelected = id; if (StringComparer.Ordinal.Equals(NetworkSnapshot.SelectedNetworkId, id)) return; Publish(NetworkSnapshot with { SelectedNetworkId = id, InspectionState = ResourceReadState.Loading, Inspection = null, InspectionFailure = null }); }
        public void RefreshNetworkInspection() => ++DetailRefreshCalls;
        public void ClearNetworkInspection()
        { ++ClearCalls; Publish(NetworkSnapshot with { InspectionState = ResourceReadState.NotRequested, Inspection = null, InspectionFailure = null }); }
        public void Copy(string text) { ++CopyCalls; Copied = text; }
    }
}
