using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class ImageOperationPresentationChecks
{
    private static readonly string A = "sha256:" + new string('a', 64), B = "sha256:" + new string('b', 64);
    private static ImageSummary Row(string id) => new(id, ["private/repo:latest", "second:tag"], [], null, 123);
    private static ImageBrowsingSnapshot Ready(long generation = 1) => new(generation, ResourceReadState.Ready, [Row(A), Row(B)]);
    private static ImageOperationSnapshot Completed(ImageOperationRequest request, ImageOperationResolution resolution,
        ImageCommandResult? result = null, long id = 1) => new(id, request, ImageOperationPhase.Completed, result, resolution,
            resolution is ImageOperationResolution.ObservedAbsent or ImageOperationResolution.ObservedPresent ? request.Target.ConnectionGeneration : null,
            resolution == ImageOperationResolution.ObservedAbsent ? false : resolution == ImageOperationResolution.ObservedPresent ? true : null);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task CancellationAndSingleUse()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post);
        view.Attach(); await ui.Settle(view); view.Select(A);
        var canceled = view.BeginDelete()!;
        Check(canceled.Request.Target == new ImageIdentity(1, A) && canceled.Message.Contains(A) && canceled.Message.Contains("second:tag")
            && canceled.Message.Contains("force=false") && canceled.Message.Contains("will not remove dependent containers or escalate to force"), "Confirmation captures full identity, tags and non-force dependency consequence");
        view.CancelDelete(canceled); view.ConfirmDelete(canceled); Check(browser.Calls == 0 && view.CanDelete, "Cancellation never mutates");
        var confirmation = view.BeginDelete()!;
        browser.OnExecute = () => view.ConfirmDelete(confirmation);
        view.ConfirmDelete(confirmation); view.ConfirmDelete(confirmation);
        Check(browser.Calls == 1 && ReferenceEquals(browser.LastRequest, confirmation.Request) && browser.LastToken == default
            && browser.LastRequest.Target.Id == A && !view.CanDelete && view.DeleteConfirmation is null, "Single-use exact-ID request, consumed before reentrancy; session lifetime");
        browser.Complete(Completed(confirmation.Request, ImageOperationResolution.ObservedAbsent)); await ui.SettleCompletion();
        view.ConfirmDelete(confirmation); Check(browser.Calls == 1, "Completed confirmation remains consumed; never replayed");
        Check(view.OperationMessage.Contains("Observed absent") && view.OperationMessage.Contains("not which action caused"), "Absence does not infer causation");
    }
    public static async Task QueuedStateAndGenerationRaces()
    {
        foreach (int scenario in Enumerable.Range(0, 5))
        {
            var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post);
            view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
            var confirmation = view.BeginDelete()!;
            browser.Publish(scenario switch
            {
                0 => browser.ImageSnapshot with { SelectedImageId = B },
                1 => Ready(2),
                2 => new(1, ResourceReadState.Unavailable, []),
                3 => browser.ImageSnapshot with { Images = [Row(B)] },
                _ => browser.ImageSnapshot with { State = ResourceReadState.Loading }
            });
            // Do not drain notification: admission must re-read live Application state.
            view.ConfirmDelete(confirmation);
            Check(browser.Calls == 0 && view.AdmissionMessage.Contains("not dispatched"), $"Queued state race {scenario} must reject");
            ui.Pump();
        }
        {
            var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post);
            view.Attach(); await ui.Settle(view); view.Select(A); browser.Publish(Ready(2));
            Check(view.BeginDelete() is null && browser.Calls == 0, "Capture rejects undrained generation replacement");
        }
    }
    public static async Task ObsoleteConfirmationAndPendingAdmission()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post);
        view.Attach(); await ui.Settle(view); view.Select(A); var old = view.BeginDelete()!;
        view.Select(B); view.Select(A); var current = view.BeginDelete()!; view.ConfirmDelete(old);
        Check(browser.Calls == 0 && ReferenceEquals(view.DeleteConfirmation, current), "Obsolete completion cannot accept a newer dialog");
        var external = new ImageOperationSnapshot(8, new(new(1, B)), ImageOperationPhase.CommandPending);
        browser.PublishOperation(external); view.ConfirmDelete(current);
        Check(browser.Calls == 0 && view.AdmissionMessage.Contains("pending"), "Live pending work rejects admission before notification");
        ui.Pump(); Check(ReferenceEquals(view.Operation, external) && view.BeginDelete() is null, "Retained pending operation is shown and never queued");
        browser.PublishOperation(Completed(external.Request, ImageOperationResolution.ObservedPresent, id: 8)); ui.Pump();
        browser.RejectAdmission = true;
        var rejected = view.BeginDelete()!; view.ConfirmDelete(rejected); await ui.SettleCompletion();
        Check(view.Operation?.OperationId == 8 && view.OperationMessage.Contains("Observed present") && view.AdmissionMessage.Contains("Not dispatched"), "Rejected admission preserves prior useful evidence");
    }
    public static async Task DisappearanceAndIndependentEvidence()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post);
        view.Attach(); await ui.Settle(view); view.Select(A); view.OpenSelected(); ui.Pump();
        var confirmation = view.BeginDelete()!; view.ConfirmDelete(confirmation);
        browser.Publish(browser.ImageSnapshot with { Images = [Row(B)], SelectedImageId = null, Inspection = null }); await ui.Settle(view);
        Check(!view.DetailOpen && view.SelectedId is null && view.Operation?.Pending == true, "Disappearance closes details but keeps pending evidence");
        view.Select(B); view.OpenSelected(); ui.Pump(); view.CloseDetail(); ui.Pump();
        Check(view.Operation?.Request.Target.Id == A && !view.CanDelete, "Selection and closing details preserve target evidence and block pending action");
        browser.Complete(Completed(confirmation.Request, ImageOperationResolution.ObservedAbsent,
            new(ImageCommandDisposition.Acknowledged, 200, RemovalRecords: [new(Deleted: A)]))); await ui.SettleCompletion();
        Check(view.OperationMessage.Contains(A) && view.OperationMessage.Contains("Removal response records: 1") && view.OperationMessage.Contains("Observed absent"), "Command removal records remain distinct from observation");
    }
    public static async Task ClassifiedOutcomeDisplay()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        var request = new ImageOperationRequest(new(1, A));
        browser.PublishOperation(new(1, request, ImageOperationPhase.CommandPending)); ui.Pump(); Check(view.OperationMessage.Contains("Command pending"), "Pending text cue");
        browser.PublishOperation(new(1, request, ImageOperationPhase.Reconciling, new(ImageCommandDisposition.Acknowledged, 200))); ui.Pump();
        Check(view.OperationMessage.Contains("Reconciling authoritative") && view.OperationMessage.Contains("do not prove absence"), "Reconciliation is not command success");
        browser.PublishOperation(Completed(request, ImageOperationResolution.ObservedPresent, new(ImageCommandDisposition.DaemonResponse, 409, "PRIVATE conflict",
            Failure: new(ImageCommandFailureKind.Conflict, "Image is in use.")))); ui.Pump();
        Check(view.OperationMessage.Contains("Conflict") && view.OperationMessage.Contains("HTTP 409") && view.OperationMessage.Contains("Dependencies")
            && view.OperationMessage.Contains("Do not blindly retry") && !view.OperationMessage.Contains("PRIVATE"), "Conflict classification and safe dependency explanation");
        browser.PublishOperation(Completed(request, ImageOperationResolution.ObservedPresent, new(ImageCommandDisposition.Acknowledged, 200))); ui.Pump();
        Check(view.OperationMessage.Contains("Observed present") && view.OperationMessage.Contains("Acknowledged"), "Acknowledged but still present is not hidden");
        browser.PublishOperation(Completed(request, ImageOperationResolution.NotDispatched, new(ImageCommandDisposition.NotDispatched,
            Failure: new(ImageCommandFailureKind.StaleConnection, "Connection changed.")))); ui.Pump();
        Check(view.OperationMessage.Contains("Not dispatched") && view.OperationMessage.Contains("No delete command was sent"), "Not-dispatched cue");
        browser.PublishOperation(Completed(request, ImageOperationResolution.Unresolved, new(ImageCommandDisposition.Uncertain,
            Failure: new(ImageCommandFailureKind.TransportFailure, "Lost response."))) with
            { ObservationFailure = new(EngineResourceFailureKind.AccessDenied, "Read denied", 403, "PRIVATE observation") }); ui.Pump();
        Check(view.OperationMessage.Contains("Unresolved") && view.OperationMessage.Contains("never automatically replayed")
            && view.OperationMessage.Contains("AccessDenied (HTTP 403)") && !view.OperationMessage.Contains("PRIVATE"), "Uncertainty and safe classified observation failure");
        Check(browser.Calls == 0, "Rendering never dispatches or retries");
    }
    public static async Task OffThreadCoalescingAndSensitiveReveal()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view);
        var request = new ImageOperationRequest(new(1, A));
        await Task.Run(() => { for (int i = 1; i <= 200; ++i) browser.PublishOperation(Completed(request, ImageOperationResolution.ObservedAbsent,
            new(ImageCommandDisposition.Acknowledged, 200, "PRIVATE command", [new("PRIVATE tag", "PRIVATE removed")]), i) with
            { ObservationFailure = new(EngineResourceFailureKind.NotFound, "Missing", 404, "PRIVATE observation") }); });
        Check(ui.Count == 1 && view.Operation is null, "Off-thread operation notifications coalesce on UI dispatcher"); ui.Pump();
        Check(view.Operation?.OperationId == 200 && view.HasOperationDetails && view.OperationDetails == "" && !view.OperationMessage.Contains("PRIVATE"), "Latest snapshot with sensitive content hidden");
        view.ShowOperationDetails(true);
        Check(view.OperationDetails.Contains("PRIVATE command") && view.OperationDetails.Contains("PRIVATE observation")
            && view.OperationDetails.Contains("PRIVATE tag") && view.OperationDetails.Contains("PRIVATE removed"), "Deliberate reveal preserves original messages and removal evidence");
        view.ShowOperationDetails(false); Check(view.OperationDetails == "", "Hide clears exposed evidence");
        browser.PublishOperation(Completed(request, ImageOperationResolution.ObservedAbsent, new(ImageCommandDisposition.Acknowledged, 200,
            new string('s', 50000), Enumerable.Repeat(new ImageRemovalRecord("tag", "deleted"), 5000).ToArray()), 201)); ui.Pump();
        view.ShowOperationDetails(true); Check(view.OperationDetails.Length < 16500 && view.OperationDetails.Contains("truncated"), "Display work is bounded for huge evidence");
        browser.PublishOperation(Completed(request, ImageOperationResolution.ObservedPresent, new(ImageCommandDisposition.DaemonResponse, 409, "NEW PRIVATE"), 202)); ui.Pump();
        Check(!view.ShowingOperationDetails && view.OperationDetails == "", "New operation requires a fresh deliberate reveal");
        Check(ImageOperationDisplay.Tags(Enumerable.Repeat(new string('t', 1000), 5000).ToArray()).Length < 16500, "Confirmation tag display is bounded");
    }
    public static async Task NavigationRetentionAndLateCompletion()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var old = new ImageBrowserView(browser, ui.Post); old.Attach(); await ui.Settle(old);
        old.Select(A); var confirmation = old.BeginDelete()!; old.ConfirmDelete(confirmation);
        int changes = 0; old.Changed += (_, _) => ++changes;
        browser.PublishOperation(browser.ImageOperation! with { Phase = ImageOperationPhase.Reconciling });
        old.Dispose(); int baseline = changes;
        Check(browser.ImageSubscribers == 0 && browser.OperationSubscribers == 0 && browser.LastToken == default, "Teardown detaches both subscriptions without command cancellation");
        using var current = new ImageBrowserView(browser, ui.Post); current.Attach(); await ui.Settle(current);
        Check(current.OperationMessage.Contains("Reconciling") && !current.DetailOpen, "Navigation reentry shows retained session operation");
        await Task.Run(() => browser.Complete(Completed(confirmation.Request, ImageOperationResolution.Unresolved,
            new(ImageCommandDisposition.Uncertain, DaemonMessage: "PRIVATE late")))); await ui.SettleCompletion();
        Check(changes == baseline && old.Operation is null && current.OperationMessage.Contains("Unresolved") && current.OperationDetails == "", "Late completion cannot publish into detached view; current page gets retained evidence");
        old.ConfirmDelete(confirmation); old.ShowOperationDetails(true); Check(browser.Calls == 1 && old.OperationDetails == "", "Detached dialog and reveal do nothing");
    }
    public static async Task TeardownAndReattachmentDialogFences()
    {
        var ui = new Dispatcher(); var browser = new Browser(); using var view = new ImageBrowserView(browser, ui.Post); view.Attach(); await ui.Settle(view); view.Select(A);
        var obsolete = view.BeginDelete()!; view.Dispose(); view.Attach(); await ui.Settle(view); view.Select(A); var current = view.BeginDelete()!;
        view.ConfirmDelete(obsolete); Check(browser.Calls == 0 && ReferenceEquals(current, view.DeleteConfirmation), "A former lifetime's dialog cannot accept current confirmation");
        view.CancelDelete(current); view.Select(B); Check(view.CanDelete, "Cancellation restores action admission");
        browser.Publish(new(1, ResourceReadState.Failed, [Row(B)])); Check(view.BeginDelete() is null, "Unavailable authoritative ready state cannot capture stale retained rows");
        Check(browser.Calls == 0, "No teardown/cancellation mutation");
    }
    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new();
        public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void Pump() { while (queue.TryDequeue(out var action)) action(); }
        public async Task SettleCompletion() { for (int i = 0; i < 20; ++i) { Pump(); await Task.Delay(1); } Pump(); }
        public async Task Settle(ImageBrowserView view)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            do { Pump(); if (!view.Filtering && queue.IsEmpty) return; await Task.Delay(1, timeout.Token); } while (true);
        }
    }
    private sealed class Browser : IImageBrowser, IImageOperations
    {
        private EventHandler? imagesChanged, operationChanged;
        private TaskCompletionSource<ImageOperationSnapshot>? completion;
        public int Calls, ImageSubscribers, OperationSubscribers;
        public bool RejectAdmission;
        public Action? OnExecute;
        public ImageOperationRequest? LastRequest;
        public CancellationToken LastToken;
        public ImageBrowsingSnapshot ImageSnapshot { get; private set; } = Ready();
        public ImageOperationSnapshot? ImageOperation { get; private set; }
        public event EventHandler? ImagesChanged { add { imagesChanged += value; ++ImageSubscribers; } remove { imagesChanged -= value; --ImageSubscribers; } }
        public event EventHandler? ImageOperationChanged { add { operationChanged += value; ++OperationSubscribers; } remove { operationChanged -= value; --OperationSubscribers; } }
        public void Publish(ImageBrowsingSnapshot snapshot) { ImageSnapshot = snapshot; imagesChanged?.Invoke(this, EventArgs.Empty); }
        public void PublishOperation(ImageOperationSnapshot operation) { ImageOperation = operation; operationChanged?.Invoke(this, EventArgs.Empty); }
        public void RefreshImages() { }
        public void SelectImage(string? id) => Publish(ImageSnapshot with { SelectedImageId = id });
        public void RefreshImageInspection() { }
        public void ClearImageInspection() => Publish(ImageSnapshot with { InspectionState = ResourceReadState.NotRequested, Inspection = null });
        public Task<ImageOperationSnapshot> ExecuteImageOperationAsync(ImageOperationRequest request, CancellationToken cancellationToken = default)
        {
            ++Calls; LastRequest = request; LastToken = cancellationToken;
            if (RejectAdmission) return Task.FromResult(Completed(request, ImageOperationResolution.NotDispatched,
                new(ImageCommandDisposition.NotDispatched, Failure: new(ImageCommandFailureKind.CapacityExceeded, "Admission rejected")), 0));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PublishOperation(new(Calls, request, ImageOperationPhase.CommandPending)); OnExecute?.Invoke(); return completion.Task;
        }
        public void Complete(ImageOperationSnapshot result) { PublishOperation(result); completion?.TrySetResult(result); }
    }
}
