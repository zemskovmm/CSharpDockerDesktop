using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static partial class ContainerPresentationChecks
{
    private static readonly string A = new('a', 64), B = new('b', 64);
    private static ContainerSummary Row(string id, string? name = null, string? image = null, string? state = null,
        string? status = null) => new(id, name is null ? null : [name], image, null, state, status, null, null, null, null, null);
    private static ContainerBrowsingSnapshot Ready(params ContainerSummary[] rows) => new(1, ResourceReadState.Ready, rows);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task SearchAndUnknownMetadata()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A, "/NameAlpha", "Repo:Tag", "restarting", "Restarting (2)"), Row(B)) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view);
        foreach (string query in new[] { "NAMEALPHA", A[..12], A, A[12..], "repo:tag", "RESTARTING", "(2)" })
        {
            view.Search(query); await clock.Settle(view);
            Check(view.Rows.Count == 1 && view.Rows[0].Id == A, $"Search field missed {query}");
        }
        view.Search(" "); await clock.Settle(view);
        var unknown = view.Rows.Single(row => row.Id == B);
        Check(unknown.Name == "unknown" && unknown.Image == "unknown" && unknown.State == "unknown"
            && unknown.Status == "unknown" && unknown.Health == "unknown" && unknown.Ports == "unknown"
            && unknown.Created == "unknown", "Omitted metadata must be unknown");
        Check(ContainerDisplay.Ports([]) == "none", "An empty collection differs from omitted information");
        Check(ContainerDisplay.Flag(false) == "no" && ContainerDisplay.Flag(null) == "unknown", "Unknown flags must not become false");
        Check(browser.SelectCalls == 0 && browser.RefreshCalls == 0, "Rendering and search may not read the Engine");
    }
    public static async Task DistinctStatesAndLatestNotifications()
    {
        var clock = new Dispatcher();
        var browser = new Browser();
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view);
        Check(view.ListMessage.Contains("unavailable") && !view.CanRefresh, "Disconnected state");
        browser.Publish(new(1, ResourceReadState.Loading, [])); clock.Pump();
        Check(view.ListMessage == "Loading containers…", "Initial loading");
        browser.Publish(Ready()); await clock.Settle(view);
        Check(view.ListMessage == "No containers on this Engine.", "Successful empty state");
        browser.Publish(Ready(Row(A))); await clock.Settle(view);
        view.Search("missing"); await clock.Settle(view);
        Check(view.ListMessage == "No containers match your search.", "No-match state");
        view.Search(""); await clock.Settle(view);
        browser.Publish(browser.ContainerSnapshot with { State = ResourceReadState.Loading }); clock.Pump();
        Check(view.ListMessage.StartsWith("Refreshing") && view.Rows.Count == 1, "Pending refresh retains old rows");
        var failure = new EngineResourceFailure(EngineResourceFailureKind.AccessDenied, "Permission denied", 403, "PRIVATE daemon body");
        browser.Publish(browser.ContainerSnapshot with { State = ResourceReadState.Failed, Failure = failure }); clock.Pump();
        Check(view.ListMessage.Contains("AccessDenied (HTTP 403)") && view.Rows.Count == 1
            && !view.ListMessage.Contains("PRIVATE"), "Classified resource error retains rows without automatic daemon-content display");
        view.Refresh(); Check(browser.RefreshCalls == 1, "Explicit retry reaches the Application contract");
        // Many off-thread notifications coalesce into one dispatcher callback, reading the newest snapshot.
        await Task.Run(() => { for (int i = 0; i < 200; ++i) browser.Publish(Ready(Row(i == 199 ? B : A))); });
        Check(clock.Count == 1, "Off-thread updates should be coalesced");
        await clock.Settle(view);
        Check(view.Rows.Single().Id == B, "Dispatcher must obtain the latest snapshot");
        browser.Publish(new(1, ResourceReadState.Failed, [], Failure: failure)); await clock.Settle(view);
        Check(view.ListMessage.StartsWith("Container refresh failed"), "Failed initial read is never successful emptiness");
    }
    public static async Task SelectionDetailsAndCancellation()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A, "alpha"), Row(B, "beta")) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view);
        view.Select(A); view.OpenSelected(); view.OpenSelected(); clock.Pump();
        Check(browser.SelectCalls == 1 && view.DetailOpen && view.DetailMessage == "Loading Overview…", "Only deliberate open starts inspection");
        view.Search("beta"); await clock.Settle(view);
        Check(view.SelectedId == A && view.DetailOpen && view.Rows.Single().Id == B, "Filtering preserves full identity and details");
        browser.Publish(browser.ContainerSnapshot with { State = ResourceReadState.Loading }); clock.Pump();
        browser.Publish(browser.ContainerSnapshot with { State = ResourceReadState.Ready, Containers = [Row(B), Row(A)] }); await clock.Settle(view);
        Check(view.SelectedId == A && browser.SelectCalls == 1, "Refresh and reordering must not duplicate inspection");
        var detail = new ContainerInspection(A, "/alpha", "repo:tag", "image-id", DateTimeOffset.UnixEpoch,
            new("paused", true, true, false, false, 0, null, DateTimeOffset.UnixEpoch, null, new("healthy", 0)),
            [new(80, "tcp", "127.0.0.1", 8080)], [new("bind", null, "/private/source", "/app", null, "rw", true, null)],
            [new("bridge", "network-id", "endpoint-id", "172.17.0.2", 16, "172.17.0.1", null, null, null, null, ["alpha"])],
            "{\"Env\":[\"SECRET=hidden\"]}");
        browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = detail }); clock.Pump();
        Check(view.Overview.Contains(A) && view.Overview.Contains("Started:") && view.Overview.Contains("Finished: unknown")
            && view.Overview.Contains("paused: yes") && view.Overview.Contains("healthy") && view.Overview.Contains("8080")
            && view.Overview.Contains("/private/source") && view.Overview.Contains("network-id") && !view.Overview.Contains("SECRET"), "Actual Overview projection");
        view.RefreshDetail(); Check(browser.InspectionRefreshCalls == 1, "Explicit detail refresh");
        var failure = new EngineResourceFailure(EngineResourceFailureKind.Conflict, "State conflict", 409);
        browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Failed, Inspection = null, InspectionFailure = failure }); clock.Pump();
        Check(view.Overview == "" && view.DetailMessage.Contains("Conflict (HTTP 409)"), "Failed inspection clears obsolete detail");
        view.CloseDetail(); clock.Pump();
        Check(!view.DetailOpen && browser.ClearCalls == 1 && view.SelectedId == A, "Closing details cancels inspection and retains row identity");
        view.OpenSelected(); browser.Publish(browser.ContainerSnapshot with { Containers = [Row(B)], SelectedContainerId = null, Inspection = null }); await clock.Settle(view);
        Check(view.SelectedId is null && !view.DetailOpen && view.Overview == "" && browser.ClearCalls == 2, "Disappearance closes and cancels details");
    }
    public static async Task DiagnosticsLifetime()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A), Row(B)) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view); view.Select(A); view.OpenSelected(); clock.Pump();
        ContainerInspection Detail(bool? oom, long? restarts) => new(A, null, null, null, null,
            new("exited", false, false, false, false, 137, "runtime failure", null, null, new("unhealthy", 1), oom),
            null, null, null, "{\"Config\":{\"Env\":[\"SECRET=hidden\"]}}", restarts);
        void Publish(ContainerInspection detail)
        { browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = detail }); clock.Pump(); }
        Publish(Detail(true, 7));
        Check(view.Overview.Contains("OOMKilled: yes") && view.Overview.Contains("Restart count: 7")
            && view.Overview.Contains("Exit code: 137") && view.Overview.Contains("Health: unhealthy")
            && view.Overview.Contains("Runtime error: runtime failure") && !view.Overview.Contains("SECRET"),
            "Overview must label actual diagnostics alongside existing evidence without exposing raw environment");
        view.RefreshDetail();
        browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Loading, Inspection = null }); clock.Pump();
        Check(view.Overview == "" && browser.InspectionRefreshCalls == 1, "Pending authoritative refresh clears previous diagnostics");
        Publish(Detail(false, 0));
        Check(view.Overview.Contains("OOMKilled: no") && view.Overview.Contains("Restart count: 0")
            && !view.Overview.Contains("Restart count: 7"), "Refresh replaces OOM and restart evidence, including valid false/zero");
        Publish(Detail(null, null));
        Check(view.Overview.Contains("OOMKilled: unknown") && view.Overview.Contains("Restart count: unknown"),
            "Unavailable diagnostics must not become false/zero");
        Publish(Detail(true, 7)); view.Select(B); clock.Pump();
        Check(view.Overview == "" && !view.DetailOpen, "Selection change clears diagnostics immediately");
        view.Select(A); view.OpenSelected(); Publish(Detail(true, 7));
        browser.Publish(browser.ContainerSnapshot with { Containers = [Row(B)] }); await clock.Settle(view);
        Check(view.Overview == "" && !view.DetailOpen, "External absence clears obsolete diagnostics even with retained detail");
        browser.Publish(Ready(Row(A))); await clock.Settle(view); view.Select(A); view.OpenSelected(); Publish(Detail(true, 7));
        browser.Publish(browser.ContainerSnapshot with { ConnectionGeneration = 2 }); await clock.Settle(view);
        Check(view.Overview == "" && view.SelectedId is null, "Replacement connection rejects previous diagnostics for the same ID");
        browser.Publish(Ready(Row(A))); await clock.Settle(view); view.Select(A); view.OpenSelected(); Publish(Detail(true, 7));
        browser.Publish(new(1, ResourceReadState.Unavailable, [])); await clock.Settle(view);
        Check(view.Overview == "" && !view.DetailOpen, "Disconnect clears diagnostics");
    }

    public static async Task InvalidationAndNavigation()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A)) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view); view.Select(A); view.OpenSelected(); clock.Pump();
        browser.Publish(new(1, ResourceReadState.Unavailable, [])); await clock.Settle(view);
        Check(view.Rows.Count == 0 && view.SelectedId is null && !view.DetailOpen && view.Overview == "", "Disconnect immediately invalidates identity and details");
        browser.Publish(Ready(Row(A))); await clock.Settle(view); view.Select(A); view.OpenSelected(); clock.Pump();
        browser.Publish(new(2, ResourceReadState.Loading, [])); await clock.Settle(view);
        Check(view.SelectedId is null && !view.DetailOpen, "Replacement generation invalidates the old selection");
        browser.Publish(new(2, ResourceReadState.Ready, [Row(B)])); await clock.Settle(view);
        view.Select(B); view.OpenSelected(); clock.Pump();
        int changed = 0; view.Changed += (_, _) => ++changed;
        browser.Publish(new(3, ResourceReadState.Ready, [Row(A)])); // queued, not yet dispatched
        view.Dispose(); int before = changed; clock.Pump();
        view.Search("inactive");
        Check(changed == before && browser.Subscribers == 0 && browser.ClearCalls == 1, "Navigation detaches and cancels; queued work cannot render");
        browser.Publish(Ready()); clock.Pump();
        Check(changed == before, "Notifications after exit do nothing");
        // Reattach before an old callback drains; it must not suppress the new lifetime's notification.
        view.Attach();
        browser.Publish(Ready(Row(A))); view.Dispose(); view.Attach(); browser.Publish(Ready(Row(B)));
        await clock.Settle(view);
        Check(view.Rows.Single().Id == B && !view.Filtering, "Reattachment rejects canceled work without losing current notifications");
    }
    public static async Task LargeCollectionAndRapidSearch()
    {
        var clock = new Dispatcher();
        var rows = Enumerable.Range(0, 20000).Select(i => Row(i.ToString("x64"), $"container-{i}", "image", i % 2 == 0 ? "running" : "exited")).ToArray();
        var browser = new Browser { ContainerSnapshot = Ready(rows) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view);
        Check(view.Rows.Count == 20000, "Every loaded state is browsable");
        for (int i = 0; i < 100; ++i) view.Search($"container-{i}");
        view.Search("container-19999"); await clock.Settle(view);
        Check(view.Rows.Single().Id == rows[^1].Id && browser.SelectCalls == 0, "Rapid search must publish only the latest result, without reads");
    }
    public static async Task InspectLifetime()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A), Row(B)) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view); view.Select(A); view.OpenSelected(); clock.Pump();
        ContainerInspection Detail(string id, string raw) => new(id, null, null, null, null, null, null, null, null, raw);
        browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"Env\":[\"SECRET=visible\"],\"nested\":{\"flag\":true}}") });
        clock.Pump(); view.ShowInspect(true); await clock.Settle(view);
        Check(view.Inspect.Rows.Count == 3 && browser.SelectCalls == 1, "Inspect reuses selected response");
        view.Inspect.Search("SECRET"); await clock.Settle(view);
        Check(view.Inspect.Rows.Count == 3, "Search includes collapsed array data");
        string? copied = null; view.Inspect.Select(view.Inspect.Rows[^1].NodeId); view.Inspect.Copy(false, text => copied = text);
        await clock.Settle(view); Check(copied == "\"SECRET=visible\"", "Sensitive copy requires explicit action and preserves JSON");
        view.ShowInspect(false); view.ShowInspect(true); await clock.Settle(view);
        Check(browser.SelectCalls == 1 && view.Inspect.Query == "SECRET", "Tabs reuse current inspection and viewer");
        view.RefreshDetail(); Check(view.Inspect.Rows.Count == 0 && browser.InspectionRefreshCalls == 1, "Refresh clears retained viewer immediately");
        browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"fresh\":null}") });
        await clock.Settle(view); Check(view.Inspect.Rows.Any(row => row.Text.Contains("fresh")), "Refresh replaces old Inspect");
        // Complete processing before notification invalidation drains: even then current session guards reject publication.
        view.Inspect.Search("fresh");
        browser.Publish(browser.ContainerSnapshot with { Containers = [Row(B)], SelectedContainerId = null, Inspection = null });
        await clock.Settle(view); Check(!view.DetailOpen && view.Inspect.Rows.Count == 0 && !view.Inspect.CanCopy, "Removal cancels parsing/search/copy");
        view.Select(B); view.OpenSelected(); clock.Pump(); view.ShowInspect(true);
        browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(B, "{\"large\":[" + string.Join(",", Enumerable.Range(0, 50000)) + "]}") });
        clock.Pump(); browser.Publish(new(2, ResourceReadState.Unavailable, []));
        await clock.Settle(view); Check(view.Inspect.Rows.Count == 0 && view.SelectedId is null, "Disconnect rejects pending large parse");
        browser.Publish(Ready(Row(A))); await clock.Settle(view); view.Select(A); view.OpenSelected(); clock.Pump(); view.ShowInspect(true);
        browser.Publish(browser.ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = Detail(A, "{\"old\":true}") });
        clock.Pump(); view.Inspect.Search("old"); int changes = 0; view.Changed += (_, _) => ++changes;
        view.Dispose(); int prior = changes; await Task.Delay(30); clock.Pump();
        Check(changes == prior && view.Inspect.Rows.Count == 0 && view.Snapshot.Inspection is null && browser.Subscribers == 0, "Departure clears raw content and rejects queued work");
    }
    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new();
        public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void Pump() { while (queue.TryDequeue(out var action)) action(); }
        public async Task Settle(ContainerBrowserView view)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do { Pump(); if (!view.Filtering && !view.Inspect.Busy && queue.IsEmpty) return; await Task.Delay(1, timeout.Token); } while (true);
        }
    }
    private sealed class Browser : IContainerBrowser, IContainerOperations
    {
        private EventHandler? changed;
        private EventHandler? operationChanged;
        public int OperationSubscribers;
        public List<ContainerOperationRequest> Requests { get; } = [];
        public TaskCompletionSource<ContainerOperationSnapshot>? Completion;
        public bool RejectNext;
        public Exception? ExecuteFailure;
        public ContainerOperationSnapshot? ContainerOperation { get; private set; }
        public event EventHandler? ContainerOperationChanged { add { operationChanged += value; ++OperationSubscribers; } remove { operationChanged -= value; --OperationSubscribers; } }
        public void PublishOperation(ContainerOperationSnapshot? operation) { ContainerOperation = operation; operationChanged?.Invoke(this, EventArgs.Empty); }
        public Task<ContainerOperationSnapshot> ExecuteContainerOperationAsync(ContainerOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (ExecuteFailure is { } failure) throw failure;
            if (RejectNext)
            {
                RejectNext = false;
                return Task.FromResult(new ContainerOperationSnapshot(0, request, ContainerOperationPhase.Completed,
                    new(request.Command, ContainerCommandDisposition.NotDispatched, Failure: new(ContainerCommandFailureKind.CapacityExceeded, "Another operation is pending.")),
                    ContainerOperationResolution.NotDispatched));
            }
            Requests.Add(request);
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PublishOperation(new(Requests.Count, request, ContainerOperationPhase.CommandPending));
            return Completion.Task;
        }
        public void CompleteOperation(ContainerOperationResolution resolution, ContainerCommandResult? command = null, EngineResourceFailure? failure = null)
        {
            var completed = ContainerOperation! with { Phase = ContainerOperationPhase.Completed, Resolution = resolution, CommandOutcome = command, ObservationFailure = failure };
            PublishOperation(completed); Completion!.TrySetResult(completed);
        }
        public int Subscribers, SelectCalls, ClearCalls, RefreshCalls, InspectionRefreshCalls;
        public ContainerBrowsingSnapshot ContainerSnapshot { get; set; } = new(0, ResourceReadState.Unavailable, []);
        public event EventHandler? ContainersChanged { add { changed += value; ++Subscribers; } remove { changed -= value; --Subscribers; } }
        public void Publish(ContainerBrowsingSnapshot snapshot) { ContainerSnapshot = snapshot; changed?.Invoke(this, EventArgs.Empty); }
        public void RefreshContainers() => ++RefreshCalls;
        public void SelectContainer(string? id)
        { ++SelectCalls; Publish(ContainerSnapshot with { SelectedContainerId = id, InspectionState = ResourceReadState.Loading, Inspection = null, InspectionFailure = null }); }
        public void RefreshContainerInspection() => ++InspectionRefreshCalls;
        public void ClearContainerInspection()
        { ++ClearCalls; Publish(ContainerSnapshot with { InspectionState = ResourceReadState.NotRequested, Inspection = null, InspectionFailure = null }); }
    }
}
