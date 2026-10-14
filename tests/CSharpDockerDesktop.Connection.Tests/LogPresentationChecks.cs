using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class LogPresentationChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task WindowsAndLoad()
    {
        var source = new Source(); var ui = new Dispatcher(); using var view = new ContainerLogsView(source, ui.Post);
        view.Open("a"); source.Add(1000); await Task.Run(() => source.Signal(1000));
        Check(ui.Count == 1, "Background signals enqueue only one callback"); ui.Drain();
        Check(view.Rows.Count <= ContainerLogsView.PageSize && view.Rows[^1].Entry.EntryId == 1000, "Latest bounded window");
        Check(view.Rows[0].Text.EndsWith("\r\n") && view.Rows[0].Metadata.Contains("stdout"), "Original CRLF and stream remain present");
        view.SetAutoscroll(false); view.Older(); ui.Drain(); long first = view.Rows[0].Entry.EntryId;
        source.Add(50); source.Signal(); ui.Drain();
        Check(view.Rows[0].Entry.EntryId == first, "Autoscroll off keeps the retained reading window while ingestion proceeds");
        view.Search("row-17\r\n"); ui.Drain();
        Check(view.Rows.Single().Entry.EntryId == 17, "Search reaches retained output outside the current page");
        for (int i = 0; i < 100; ++i) { view.Search("row-" + i); view.Filter(i % 2 == 0 ? ContainerLogStream.Stdout : null); source.Add(1); source.Signal(); }
        view.Filter(null); view.Search("row-999\r\n");
        for (int i = 0; i < 30 && (ui.Count > 0 || view.Busy); ++i) { source.Add(1); ui.One(); }
        Check(view.Rows.Any(r => r.Entry.EntryId == 999) && !view.Busy, "Latest query progresses under producer load without revision restart starvation");
        Check(ui.Count <= 1 && source.LargestRead <= ContainerLogsView.PageSize, "Bounded queue and page reads");
        source.EvictBefore(1020); source.Signal(); ui.Drain();
        Check(view.Rows.All(r => r.Entry.EntryId >= 1020) && view.ReadingMessage.Contains("evicted"), "Evicted reading content never reappears and position loss is explained");
    }
    public static Task RacesAndClear()
    {
        var source = new Source(); var ui = new Dispatcher(); using var view = new ContainerLogsView(source, ui.Post);
        view.Open("a"); source.Add(300); ui.Drain(); view.SetAutoscroll(false);
        long id = view.Rows[0].Entry.EntryId;
        source.Replace(id, "partial\r\n終", true); source.Signal(); ui.Drain();
        Check(view.Rows[0].Text == "partial\r\n終" && view.Rows[0].Entry.Partial, "Mutable EntryId refreshes text and flags");
        source.OnRead = () => source.Replace(id, "new revision", false); source.Signal(); ui.One();
        Check(view.Rows[0].Text != "new revision", "Page raced by revision is rejected"); ui.Drain();
        Check(view.Rows[0].Text == "new revision", "Retry publishes current text");
        source.Signal(); for (int i = 0; i < 100; ++i) view.Clear(); Check(ui.Count == 1, "Repeated Clear retains one pending callback"); ui.Drain(); Check(view.Rows.Count == 0, "Clear invalidates pending publication");
        source.Add(3); source.Signal(); ui.Drain(); Check(view.Rows.All(r => r.Entry.EntryId > 300), "Clear never restores old text");
        view.Search("new revision"); source.OnRead = () => source.ClearContainerLogs(); ui.Drain();
        Check(view.Rows.Count == 0, "Clear during page read rejects old page");
        source.Add(10, ContainerLogStream.Combined); source.SetTty(); view.Search(""); view.Filter(ContainerLogStream.Stderr); ui.Drain();
        Check(view.Stream is null && view.Rows.All(r => r.Entry.Stream == ContainerLogStream.Combined) && view.Rows.Count > 0, "TTY stays combined despite stream filter");
        source.Fail(new(ContainerLogFailureKind.LoggingUnavailable, "private description", 500, "sensitive original")); ui.Drain();
        Check(view.Message.Contains("logging driver") && !view.Message.Contains("sensitive") && view.DaemonContent == "", "Driver failure is useful and daemon content deliberate");
        view.ShowDaemonContent(true); Check(view.DaemonContent == "sensitive original", "Explicit reveal");
        source.Recover(); ui.Drain(); Check(view.Message.Contains("Recovering") && view.RetentionMessage.Contains("gaps or duplicates") && view.DaemonContent == "", "Recovery and continuity limitations are visible");
        return Task.CompletedTask;
    }
    public static async Task OwnershipAndNavigation()
    {
        var source = new Source(); var ui = new Dispatcher(); var old = new ContainerLogsView(source, ui.Post);
        old.Open("a"); source.Add(200); source.Signal();
        source.CloseJoin = new(TaskCreationOptions.RunContinuationsAsynchronously); old.Close();
        Check(!old.Cleanup.IsCompleted && old.Rows.Count == 0 && source.Closes == 1, "Close invalidates synchronously and captures asynchronous join");
        using var replacement = new ContainerLogsView(source, ui.Post); replacement.Open("b"); source.Add(10); ui.Drain();
        old.Dispose(); source.CloseJoin.SetResult(); await old.Cleanup;
        Check(source.Closes == 1 && replacement.Rows.Count == 10 && source.ContainerLogs.ContainerId == "b", "Late cleanup never closes new session");
        bool identity = true; replacement.IsCurrentIdentity = () => identity; source.Signal(); identity = false; ui.Drain();
        Check(replacement.Rows.Count == 0, "Identity guard rejects work before navigation notification");
        replacement.Dispose(); source.Signal(); ui.Drain(); Check(source.Subscribers == 0, "Departure detaches notifications");
        source.Admit = false; replacement.Open("c");
        Check(replacement.Message.Contains("could not open") && source.Opens == 3, "Admission failure visible");
        replacement.Dispose();
    }
    public static async Task BrowserLifetime()
    {
        var source = new Source(); var ui = new Dispatcher(); using var view = new ContainerBrowserView(source, ui.Post);
        view.Attach(); await ui.Settle(view); view.Select("a"); view.OpenSelected(); ui.Drain();
        Check(source.Opens == 0, "Overview never opens logs"); view.ShowLogs(); source.Add(10); ui.Drain();
        Check(view.ShowingLogs && view.Logs.Rows.Count == 10, "Selected detail opens Logs through Application");
        view.ShowInspect(true); Check(source.Closes == 1 && !view.ShowingLogs, "Leaving Logs closes immediately");
        view.ShowLogs(); view.CloseDetail(); Check(source.Closes == 2, "Closing details closes Logs");
        view.OpenSelected(); view.ShowLogs(); view.Select("b"); Check(source.Closes == 3, "Selection replacement closes Logs");
        view.OpenSelected(); view.ShowLogs(); source.ContainerSnapshot = new(2, ResourceReadState.Unavailable, []); source.NotifyBrowser(); ui.Drain();
        Check(!view.ShowingLogs && source.Closes == 4, "Connection/navigation invalidates Logs");
        view.Dispose(); ui.Drain(); Check(source.Subscribers == 0, "Lifetime detaches logs");
    }
    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new();
        public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void One() { if (queue.TryDequeue(out var action)) action(); }
        public void Drain() { int budget = 10000; while (queue.Count > 0 && --budget > 0) One(); Check(budget > 0, "Dispatcher did not converge"); }
        public async Task Settle(ContainerBrowserView view) { for (int i = 0; i < 5000; ++i) { Drain(); if (!view.Filtering) return; await Task.Delay(1); } throw new InvalidOperationException("Filtering timeout"); }
    }
    private sealed class Source : IContainerLogs, IContainerBrowser
    {
        private readonly List<ContainerLogEntry> entries = [];
        private long next, nextSession;
        private EventHandler? changed;
        public Action? OnRead;
        public int Subscribers, Opens, Closes, LargestRead;
        public bool Admit = true;
        public TaskCompletionSource? CloseJoin;
        public ContainerLogsSnapshot ContainerLogs { get; private set; } = new(0, 0, ContainerLogsPhase.Closed);
        public event EventHandler? ContainerLogsChanged { add { changed += value; ++Subscribers; } remove { changed -= value; --Subscribers; } }
        public void Signal(int count = 1) { for (int i = 0; i < count; ++i) changed?.Invoke(this, EventArgs.Empty); }
        public bool OpenContainerLogs(string id, bool follow = true, int? tailLines = null) => OpenContainerLogs(id, out _, follow, tailLines);
        public bool OpenContainerLogs(string id, out ContainerLogFailure? failure, bool follow = true, int? tailLines = null)
        { ++Opens; failure = Admit ? null : new(ContainerLogFailureKind.InvalidRequest, "Fixture admission"); if (!Admit) return false; entries.Clear(); next = 0; ContainerLogs = new(++nextSession, 0, ContainerLogsPhase.Streaming, id); return true; }
        public ContainerLogsPage ReadContainerLogs(long afterEntryId = 0, int maximumEntries = 128)
        { LargestRead = Math.Max(LargestRead, maximumEntries); var page = new ContainerLogsPage(ContainerLogs.SessionId, ContainerLogs.ViewRevision, entries.Where(e => e.EntryId > afterEntryId).Take(maximumEntries).ToArray()); var hook = OnRead; OnRead = null; hook?.Invoke(); return page; }
        public void ClearContainerLogs() { entries.Clear(); Revision(); Signal(); }
        public Task CloseContainerLogsAsync() { ++Closes; entries.Clear(); ContainerLogs = ContainerLogs with { Phase = ContainerLogsPhase.Closed, RetainedEntries = 0, ViewRevision = ContainerLogs.ViewRevision + 1 }; return CloseJoin?.Task ?? Task.CompletedTask; }
        public void Add(int count, ContainerLogStream stream = ContainerLogStream.Stdout)
        { for (int i = 0; i < count; ++i) { long id = ++next; entries.Add(new(id, id, stream, $"row-{id}\r\n", null, false, false)); } Revision(); }
        public void Replace(long id, string text, bool partial) { int index = entries.FindIndex(e => e.EntryId == id); entries[index] = entries[index] with { Text = text, Partial = partial }; Revision(); }
        public void EvictBefore(long id) { int count = entries.RemoveAll(e => e.EntryId < id); ContainerLogs = ContainerLogs with { EvictedEntries = ContainerLogs.EvictedEntries + count }; Revision(); }
        private void Revision() => ContainerLogs = ContainerLogs with { ViewRevision = ContainerLogs.ViewRevision + 1, RetainedEntries = entries.Count };
        public void SetTty() { ContainerLogs = ContainerLogs with { IsTty = true }; Signal(); }
        public void Fail(ContainerLogFailure failure) { ContainerLogs = ContainerLogs with { Phase = ContainerLogsPhase.Failed, Failure = failure }; Signal(); }
        public void Recover() { ContainerLogs = ContainerLogs with { Phase = ContainerLogsPhase.Recovering, Failure = null, Continuity = ContainerLogsContinuity.Uncertain }; Signal(); }
        private static ContainerSummary Row(string id) => new(id, [id], null, null, "running", null, null, null, null, null, null);
        public ContainerBrowsingSnapshot ContainerSnapshot { get; set; } = new(1, ResourceReadState.Ready, [Row("a"), Row("b")]);
        public event EventHandler? ContainersChanged;
        public void NotifyBrowser() => ContainersChanged?.Invoke(this, EventArgs.Empty);
        public void RefreshContainers() { }
        public void SelectContainer(string? id) { ContainerSnapshot = ContainerSnapshot with { SelectedContainerId = id }; NotifyBrowser(); }
        public void ClearContainerInspection() { }
        public void RefreshContainerInspection() { }
    }
}
