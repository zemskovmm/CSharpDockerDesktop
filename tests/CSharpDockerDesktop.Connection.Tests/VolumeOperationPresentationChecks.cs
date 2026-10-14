using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class VolumeOperationPresentationChecks
{
    private const string A = " Volume-Exact-é ", B = " volume-exact-é ";
    private static VolumeSummary Row(string name) => new(name, "local", "local", null, null);
    private static VolumeBrowsingSnapshot Ready(long generation = 1) => new(generation, ResourceReadState.Ready, [Row(A), Row(B)], LatestListing: new([Row(A), Row(B)], []), Completeness: VolumeListCompleteness.Complete);
    private static VolumeDependencies Related(string name = A, bool incomplete = false) => new(name,
        [new("full-running-id", ["/running", "/alias"], "running name", "running", true), new("full-stopped-id", ["/stopped"], "stopped name", "exited", false)], incomplete ? ["full-unverified-id"] : []);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<(Ui Ui, Browser Browser, VolumeBrowserView View)> Open()
    {
        var ui = new Ui(); var browser = new Browser(); var view = new VolumeBrowserView(browser, ui.Post);
        view.Attach(); await ui.Until(() => !view.Filtering); view.Select(A); return (ui, browser, view);
    }
    private static async Task<(VolumeDeletePresentation Presentation, VolumeDeleteConfirmation Confirmation)> Prepared(VolumeBrowserView view, Browser browser, Ui ui, VolumeDependencies? dependencies = null)
    {
        var presentation = view.BeginDelete()!; browser.CompletePreparation(dependencies ?? Related());
        await ui.Until(() => view.DeleteConfirmation is not null);
        return (presentation, view.DeleteConfirmation!);
    }
    public static async Task LoadingCancellationAndExactCapture()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var presentation = view.BeginDelete()!;
            Check(presentation.Target == new VolumeDeletionTarget(1, A) && browser.LastTarget == presentation.Target && browser.Prepares == 1, "Capture complete ordinal name and generation at preparation start");
            Check(view.DeleteConfirmation is null && view.PreparationMessage.Contains("Loading fresh") && !view.CanDelete, "Loading cannot confirm or start another precheck");
            Check(VolumeBrowserView.DataLossWarning.Contains("permanently remove its data") && VolumeBrowserView.PrecheckExplanation.Contains("not proof of safety"), "Explicit loss and race explanation");
            view.CancelDelete(presentation); Check(browser.LastPreparationToken.IsCancellationRequested && view.Deletion is null && !view.PreparationCleanup.IsCompleted, "Cancel fences immediately and joins non-cooperative work asynchronously");
            browser.CompletePreparation(Related()); await view.PreparationCleanup; await ui.Idle();
            Check(view.DeleteConfirmation is null && browser.Calls == 0 && browser.ClosedIds.All(id => id == 1) && view.CanDelete, "Late completed preparation never resurrects canceled confirmation or mutates");
        }
    }
    public static async Task CompleteRunningStoppedAndFullCopy()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (presentation, confirmation) = await Prepared(view, browser, ui);
            Check(confirmation.Request.Target.Name == A && confirmation.Request.Preparation == browser.VolumeDeletionPreparation!.Identity, "Confirmation captures exact completed ID/revision and target");
            Check(view.PreparationDependencies.Count == 2 && view.PreparationDependencies[0].Text.Contains("running: yes") && view.PreparationDependencies[1].Text.Contains("exited") && view.PreparationDependencies[1].Text.Contains("no (includes stopped"), "Every running/stopped relationship and state is presented");
            string copied = ""; var copy = view.CopyDependencyAsync(view.PreparationDependencies[0], false, text => copied = text); await ui.Until(() => copy.IsCompleted);
            Check(await copy && copied.Contains("full-running-id") && copied.Contains("/alias") && copied.Contains("running name"), "Deliberate dependency copy preserves full identities/names/state");
            int publications = 0;
            var copies = Enumerable.Range(0, 50).Select(_ => view.CopyDependencyAsync(view.PreparationDependencies[0], false, _ => ++publications)).ToArray();
            await ui.Until(() => copies.All(task => task.IsCompleted));
            Check(publications == 1 && (await Task.WhenAll(copies)).Count(success => success) == 1, "Superseded copy work cancels; only the latest deliberate copy can publish");
            view.CancelDelete(presentation); await view.PreparationCleanup;
        }
    }
    public static async Task IncompleteEmptyUnavailableAndFailed()
    {
        foreach (int scenario in Enumerable.Range(0, 4))
        {
            var (ui, browser, view) = await Open(); using (view)
            {
                var presentation = view.BeginDelete()!;
                if (scenario == 3) browser.FailPreparation(new(EngineResourceFailureKind.AccessDenied, "PRIVATE description", 403, "  PRIVATE daemon\n  "));
                else browser.CompletePreparation(scenario == 0 ? new(A, [], []) : scenario == 1 ? Related(incomplete: true) : new(A, null, ["unverified-only"]));
                await ui.Until(() => !view.PreparationMessage.Contains("Loading"));
                if (scenario == 0) Check(view.DeleteConfirmation is not null && view.PreparationMessage.Contains("Complete-empty") && view.PreparationDependencies.Count == 0, "Only complete empty query says no containers found, never safety");
                if (scenario == 1) Check(view.DeleteConfirmation is not null && view.PreparationDependencies.Count == 3 && view.PreparationDependencies[2].Text.Contains("Unverified candidate") && view.PreparationMessage.Contains("more relationships may exist"), "Incomplete evidence retains all found and unverified candidates with uncertainty");
                if (scenario == 2) Check(view.DeleteConfirmation is null && view.PreparationMessage.Contains("Unavailable") && view.PreparationDependencies.Count == 1, "Unavailable evidence cannot delete, preserves available unverified candidate");
                if (scenario == 3)
                {
                    Check(view.DeleteConfirmation is null && view.PreparationMessage.Contains("failed") && view.PreparationMessage.Contains("AccessDenied (HTTP 403)") && !view.PreparationMessage.Contains("PRIVATE"), "Safe classified failed precheck");
                    Check(view.PreparationDetails == "", "Private precheck evidence hidden by default"); view.ShowPreparationDetails(true);
                    Check(view.PreparationDetails.Contains("  PRIVATE daemon\n  ") && view.PreparationDetails.Contains("PRIVATE description"), "Explicit reveal preserves original private evidence");
                    view.ShowPreparationDetails(false); Check(view.PreparationDetails == "", "Hide original precheck evidence");
                }
                view.CancelDelete(presentation); await view.PreparationCleanup; Check(browser.Calls == 0, "No read evidence alone dispatches a mutation");
            }
        }
    }
    public static async Task SingleSubmissionAndSessionOwnership()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (presentation, confirmation) = await Prepared(view, browser, ui);
            browser.OnExecute = () => view.ConfirmDelete(presentation, confirmation);
            view.ConfirmDelete(presentation, confirmation); view.ConfirmDelete(presentation, confirmation);
            Check(browser.Calls == 1 && ReferenceEquals(browser.LastRequest, confirmation.Request) && browser.LastCommandToken == default && view.Deletion is null, "Consume exact captured request before reentrancy; submit only once under session ownership");
            Check(view.OperationMessage.Contains("Command pending") && !view.CanDelete, "Retain pending status and block another operation");
            browser.CompleteOperation(VolumeOperationResolution.ObservedAbsent, new(VolumeCommandDisposition.Acknowledged, 204)); await ui.Idle();
            Check(view.OperationMessage.Contains("Observed absent") && view.OperationMessage.Contains("Original command disposition: Acknowledged") && view.OperationMessage.Contains("not establish command causation"), "Authoritative absence remains separate from acknowledgement and causation");
            view.ConfirmDelete(presentation, confirmation); Check(browser.Calls == 1, "Completed confirmation never replays");
        }
    }
    public static async Task SelectionChangesNeverRetarget()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (old, confirmation) = await Prepared(view, browser, ui);
            view.Select(B); Check(view.Deletion is null, "Changing case-sensitive selection closes original preparation");
            view.ConfirmDelete(old, confirmation); Check(browser.Calls == 0, "Obsolete confirmation cannot retarget new selection");
            view.Select(A); var (current, next) = await Prepared(view, browser, ui);
            Check(next.Request.Target.Name == A && next.Request.Preparation.PreparationId != confirmation.Request.Preparation.PreparationId, "Returning to selection needs fresh acquisition");
            view.CancelDelete(old); view.ConfirmDelete(old, confirmation); Check(ReferenceEquals(view.Deletion, current), "Old cleanup cannot cancel new presentation");
            view.ConfirmDelete(current, next); Check(browser.LastRequest!.Target.Name == A && browser.Calls == 1, "Exact original captured name reaches application");
        }
    }
    public static async Task LiveInvalidationBeforeQueuedNotification()
    {
        foreach (var reason in new[] { VolumePreparationFailureKind.ObservedChange, VolumePreparationFailureKind.TargetAbsent, VolumePreparationFailureKind.ConnectionChanged, VolumePreparationFailureKind.Superseded })
        {
            var (ui, browser, view) = await Open(); using (view)
            {
                var (presentation, confirmation) = await Prepared(view, browser, ui); var row = view.PreparationDependencies[0];
                browser.Invalidate(reason);
                Check(view.DeleteConfirmation is null && view.PreparationMessage.Contains("Invalidated") && view.PreparationDependencies.Count == 2, "Live invalidation disables before UI queue drains and retains found relationships");
                Check(!await view.CopyDependencyAsync(row, false, _ => throw new InvalidOperationException()), "Stale precheck copy is rejected before notification");
                view.ConfirmDelete(presentation, confirmation); Check(browser.Calls == 0 && view.AdmissionMessage.Contains("not dispatched"), "Stale captured revision never dispatches");
                await ui.Idle(); Check(view.Deletion is null, "Rejected stale presentation closes and requires fresh user action");
            }
        }
    }
    public static async Task ResourceGenerationAndDetailFences()
    {
        foreach (int scenario in Enumerable.Range(0, 4))
        {
            var (ui, browser, view) = await Open(); using (view)
            {
                view.OpenSelected(); ui.Pump(); var (presentation, confirmation) = await Prepared(view, browser, ui);
                browser.Publish(scenario switch { 0 => Ready(2), 1 => new(1, ResourceReadState.Unavailable, []), 2 => browser.VolumeSnapshot with { Volumes = [Row(B)] }, _ => browser.VolumeSnapshot with { SelectedVolumeName = B } });
                Check(view.DeleteConfirmation is null, "Live target/generation change disables queued confirmation");
                view.ConfirmDelete(presentation, confirmation); await ui.Idle();
                Check(browser.Calls == 0 && view.Deletion is null, "Changed generation, disappearance and detail ownership never dispatch");
            }
        }
    }
    public static async Task NavigationDuringSlowPreparationAndCleanupJoin()
    {
        var (ui, browser, view) = await Open(); var presentation = view.BeginDelete()!;
        int changes = 0; view.Changed += (_, _) => ++changes;
        view.Dispose(); int baseline = changes;
        Check(!view.PreparationCleanup.IsCompleted && browser.LastPreparationToken.IsCancellationRequested && browser.Subscribers == 0, "Navigation detaches all services, cancels acquisition and leaves nonblocking async join");
        using var reopened = new VolumeBrowserView(browser, ui.Post); reopened.Attach(); await ui.Until(() => !reopened.Filtering); reopened.Select(B);
        var replacement = reopened.BeginDelete()!; var replacementIdentity = browser.VolumeDeletionPreparation!.Identity;
        browser.CompleteOldPreparation(1, Related()); await view.PreparationCleanup; await ui.Idle();
        Check(changes == baseline && view.Operation is null && view.Deletion is null && browser.VolumeDeletionPreparation!.Identity == replacementIdentity && ReferenceEquals(reopened.Deletion, replacement), "Late cleanup/callbacks never expose old evidence or close a replacement ID");
        browser.CompletePreparation(Related(B)); await ui.Until(() => reopened.DeleteConfirmation is not null);
        reopened.CancelDelete(replacement); await reopened.PreparationCleanup; Check(browser.Calls == 0, "Navigation causes no mutation");
    }
    public static async Task CloseDetailReattachAndQueuedWorkFences()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            view.OpenSelected(); ui.Pump(); var (presentation, confirmation) = await Prepared(view, browser, ui);
            var copy = view.CopyDependencyAsync(view.PreparationDependencies[0], false, _ => throw new InvalidOperationException("Obsolete copy exposed"));
            view.CloseDetail(); await ui.Until(() => copy.IsCompleted); Check(!await copy && view.Deletion is null, "Leaving presentation fences already queued copy");
            view.Dispose(); view.Attach(); await ui.Until(() => !view.Filtering); view.Select(A);
            var fresh = view.BeginDelete()!; view.ConfirmDelete(presentation, confirmation); view.CancelDelete(presentation);
            Check(ReferenceEquals(view.Deletion, fresh) && browser.Calls == 0, "Prior lifetime cannot complete or cancel new confirmation");
            view.CancelDelete(fresh); browser.CompletePreparation(Related()); await view.PreparationCleanup;
        }
    }
    public static async Task RetainedOperationAfterReopening()
    {
        var (ui, browser, view) = await Open(); var (presentation, confirmation) = await Prepared(view, browser, ui);
        view.ConfirmDelete(presentation, confirmation); browser.PublishOperation(browser.VolumeOperation! with { Phase = VolumeOperationPhase.Reconciling, CommandOutcome = new(VolumeCommandDisposition.Uncertain) });
        view.Dispose(); using var current = new VolumeBrowserView(browser, ui.Post); current.Attach(); await ui.Until(() => !current.Filtering);
        Check(current.OperationMessage.Contains("Reconciling") && current.Operation!.Request.Target.Name == A && !current.CanDelete, "Reopened page discovers retained operation independently of selection");
        browser.CompleteOperation(VolumeOperationResolution.Unresolved, new(VolumeCommandDisposition.Uncertain, DaemonMessage: "PRIVATE original")); await ui.Idle();
        Check(current.OperationMessage.Contains("Unresolved") && view.Operation is null && browser.Calls == 1 && current.OperationDetails == "", "Session completes after navigation without replay or old page publication");
        current.ShowOperationDetails(true); await ui.Until(() => !current.PreparingOperationDetails);
        Check(current.OperationDetails.Contains("PRIVATE original") && current.OperationDetails.Contains(A) && current.OperationPreparationDependencies.Count == 2, "Retained exact private evidence and original precheck relationships remain deliberately discoverable");
    }
    public static async Task ConflictRefreshNeverAuthorizesRetry()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (presentation, confirmation) = await Prepared(view, browser, ui); view.ConfirmDelete(presentation, confirmation);
            browser.CompleteOperation(VolumeOperationResolution.ObservedPresent, new(VolumeCommandDisposition.DaemonResponse, 409, "PRIVATE conflict", Failure: new(VolumeCommandFailureKind.Conflict, "PRIVATE failure")), Related(incomplete: true)); await ui.Idle();
            Check(view.OperationMessage.Contains("Dependency conflict") && view.OperationMessage.Contains("fresh explicit precheck and confirmation") && view.OperationMessage.Contains("Incomplete") && !view.OperationMessage.Contains("PRIVATE"), "Classified conflict exposes uncertainty/recovery without raw daemon content");
            Check(view.RefreshedDependencies.Count == 0, "Original related evidence is deliberately revealed"); view.ShowOperationDetails(true); await ui.Until(() => !view.PreparingOperationDetails);
            Check(view.RefreshedDependencies.Count == 3 && view.RefreshedDependencies[1].Text.Contains("exited") && view.OperationDetails.Contains("PRIVATE conflict"), "Conflict refresh retains every found/stopped/unverified candidate and original daemon evidence");
            view.ConfirmDelete(presentation, confirmation); Check(browser.Calls == 1 && view.DeleteConfirmation is null, "Conflict refresh cannot renew consumed authorization");
            var next = view.BeginDelete()!; Check(browser.Prepares == 2 && view.DeleteConfirmation is null, "Next deliberate attempt requires a new acquisition");
            view.CancelDelete(next); browser.CompletePreparation(Related()); await view.PreparationCleanup;
        }
    }
    public static async Task OriginalEvidenceHideReplacementAndStaleCopy()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var request = new VolumeOperationRequest(new(1, A), new(42, 2));
            var operation = new VolumeOperationSnapshot(1, request, VolumeOperationPhase.Completed, CommandOutcome: new(VolumeCommandDisposition.Uncertain, DaemonMessage: "  PRIVATE command\n  ", DaemonErrorBody: "PRIVATE body"), Resolution: VolumeOperationResolution.Unresolved,
                ObservationListFailure: new(EngineResourceFailureKind.AccessDenied, "PRIVATE description", 403, "PRIVATE listing"), RefreshedDependencies: Related(), DependencyObservationState: VolumeDependencyObservationState.Invalidated,
                ObservedListing: new(null, ["  PRIVATE warning  ", "", "  PRIVATE warning  "]));
            browser.PublishOperation(operation); ui.Pump(); Check(!view.OperationMessage.Contains("PRIVATE") && view.OperationDetails == "", "Ordinary operation status exposes safe classifications only");
            view.ShowOperationDetails(true); view.ShowOperationDetails(false); await ui.Idle(); Check(view.OperationDetails == "", "Hide fences pending reveal");
            view.ShowOperationDetails(true); await ui.Until(() => !view.PreparingOperationDetails);
            Check(view.OperationDetails.Contains("  PRIVATE command\n  ") && view.OperationDetails.Contains("PRIVATE body") && view.OperationDetails.Contains("Observation warning 2 · 0 characters"), "Original strings, whitespace, empty and duplicate warnings survive deliberate reveal");
            var hiddenCopy = view.CopyDependencyAsync(view.RefreshedDependencies[0], true, _ => throw new InvalidOperationException("hidden copy"));
            view.ShowOperationDetails(false); view.ShowOperationDetails(true);
            await ui.Until(() => hiddenCopy.IsCompleted); Check(!await hiddenCopy, "Hide/reveal fences previously queued private copy even with the same operation snapshot");
            await ui.Until(() => !view.PreparingOperationDetails);
            var copy = view.CopyDependencyAsync(view.RefreshedDependencies[0], true, _ => throw new InvalidOperationException("obsolete copy"));
            browser.PublishOperation(operation with { OperationId = 2, RefreshedDependencies = Related(incomplete: true) });
            Check(view.OperationDetails == "" && view.RefreshedDependencies.Count == 0, "Live replacement hides private evidence before dispatcher drains");
            await ui.Until(() => copy.IsCompleted); Check(!await copy && !view.ShowingOperationDetails, "Queued private copy is fenced by immutable retained evidence identity");
            view.ShowOperationDetails(true); view.Dispose(); await ui.Idle(); Check(view.OperationDetails == "" && view.Operation is null, "Disposed view cannot reveal evidence or publish queued output");
        }
    }
    public static Task ClassifiedRejectionInterruptionAndNameRecreation()
    {
        var request = new VolumeOperationRequest(new(1, A), new(1, 2));
        foreach (var rejection in Enum.GetValues<VolumeOperationRejection>())
        {
            var status = VolumeOperationDisplay.Status(new(0, request, VolumeOperationPhase.Completed, Resolution: VolumeOperationResolution.NotDispatched, Rejection: rejection));
            Check(status.Contains($"Admission rejection: {rejection}") && status.Contains("No delete command was sent"), "Every admission rejection is classified");
        }
        foreach (var interruption in Enum.GetValues<VolumeOperationInterruption>())
        {
            var status = VolumeOperationDisplay.Status(new(1, request, VolumeOperationPhase.Completed, CommandOutcome: new(VolumeCommandDisposition.Uncertain), Resolution: VolumeOperationResolution.Unresolved, Interruption: interruption));
            Check(status.Contains($"Interruption: {interruption}") && status.Contains("never automatically replayed"), "Every interruption retains uncertainty and original disposition");
        }
        var present = VolumeOperationDisplay.Status(new(1, request, VolumeOperationPhase.Completed, CommandOutcome: new(VolumeCommandDisposition.Acknowledged, 204), Resolution: VolumeOperationResolution.ObservedPresent, ObservedConnectionGeneration: 1, ObservedExists: true));
        Check(present.Contains("recreated volume") && present.Contains("Acknowledgement alone is not deletion") && present.Contains("Separate authoritative observation: name present"), "Name recreation, present observation and acknowledgement never imply identity continuity or causation");
        return Task.CompletedTask;
    }
    public static async Task LargeDependenciesAndInspectCoexistence()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            view.OpenSelected(); browser.Publish(browser.VolumeSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = new(A, "local", "local", null, null, "{\"exact\":12345678901234567890,\"private\":\"TOKEN\"}") }); ui.Pump(); view.ShowInspect(true); await ui.Until(() => !view.Inspect.Busy);
            var dependencies = new VolumeDependencies(A, Enumerable.Range(0, 20000).Select(i => new VolumeContainerDependency(i.ToString("x64"), ["/name-" + i], null, i % 2 == 0 ? "running" : "exited", i % 2 == 0)), Enumerable.Range(0, 10000).Select(i => "unverified-" + i));
            var (presentation, _) = await Prepared(view, browser, ui, dependencies);
            long before = GC.GetAllocatedBytesForCurrentThread(); var rows = view.PreparationDependencies; long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(rows is VolumeDependencyRows && rows.Count == 30000 && rows[19999].Id == 19999.ToString("x64") && rows[29999].Id == "unverified-9999" && allocated < 100000, "Lazy bounded projection preserves every found/unverified dependency without materializing display rows");
            Check(view.ShowingInspect && view.Inspect.Rows.Count > 0 && view.DetailOpen, "Precheck coexists with actual Inspect without replacing it");
            view.CancelDelete(presentation); await view.PreparationCleanup;
            Check(view.ShowingInspect && view.Inspect.Rows.Count > 0 && view.CanOpen, "Cancel retains browsing/Inspect");
        }
    }
    public static async Task OffThreadCoalescingAndExternalPendingAdmission()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (presentation, confirmation) = await Prepared(view, browser, ui);
            await ui.Idle();
            var external = new VolumeOperationSnapshot(88, new(new(1, B), new(55, 2)), VolumeOperationPhase.CommandPending);
            await Task.Run(() => { for (int i = 0; i < 200; ++i) browser.PublishOperation(external); });
            Check(ui.Count == 1 && view.DeleteConfirmation is null, "Off-thread notifications coalesce and live pending admission disables confirmation");
            view.ConfirmDelete(presentation, confirmation); ui.Pump(); Check(browser.Calls == 0 && view.Operation!.OperationId == 88, "External pending work rejects captured request and keeps retained evidence");
            browser.PublishOperation(external with { Phase = VolumeOperationPhase.Completed, Resolution = VolumeOperationResolution.Unresolved }); ui.Pump();
            var fresh = view.BeginDelete()!; browser.CompletePreparation(Related()); await ui.Until(() => view.DeleteConfirmation is not null);
            browser.RejectAdmission = true; view.ConfirmDelete(fresh, view.DeleteConfirmation!); await ui.Idle();
            Check(view.Operation!.OperationId == 88 && view.AdmissionMessage.Contains("PreparationNotCurrent"), "Classified admission rejection does not replace prior retained operation");
        }
    }
    private sealed class Ui
    {
        private readonly ConcurrentQueue<Action> queue = new();
        public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void Pump() { while (queue.TryDequeue(out var action)) action(); }
        public async Task Until(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do { Pump(); if (condition()) return; await Task.Delay(1, timeout.Token); } while (true);
        }
        public async Task Idle() { for (int i = 0; i < 20; ++i) { Pump(); await Task.Delay(1); } Pump(); }
    }
    private sealed class Browser : IVolumeBrowser, IVolumeDeletionPreparation, IVolumeOperations
    {
        private EventHandler? volumes, preparations, operations;
        private readonly Dictionary<long, TaskCompletionSource<VolumeDeletionPreparationSnapshot>> work = new();
        private TaskCompletionSource<VolumeOperationSnapshot>? operationCompletion;
        public int Prepares, Calls, Subscribers;
        public bool RejectAdmission;
        public Action? OnExecute;
        public List<long> ClosedIds { get; } = [];
        public VolumeDeletionTarget? LastTarget;
        public VolumeOperationRequest? LastRequest;
        public CancellationToken LastPreparationToken, LastCommandToken;
        public VolumeBrowsingSnapshot VolumeSnapshot { get; private set; } = Ready();
        public VolumeDeletionPreparationSnapshot? VolumeDeletionPreparation { get; private set; }
        public VolumeOperationSnapshot? VolumeOperation { get; private set; }
        public event EventHandler? VolumesChanged { add { volumes += value; ++Subscribers; } remove { volumes -= value; --Subscribers; } }
        public event EventHandler? VolumeDeletionPreparationChanged { add { preparations += value; ++Subscribers; } remove { preparations -= value; --Subscribers; } }
        public event EventHandler? VolumeOperationChanged { add { operations += value; ++Subscribers; } remove { operations -= value; --Subscribers; } }
        public void Publish(VolumeBrowsingSnapshot snapshot) { VolumeSnapshot = snapshot; volumes?.Invoke(this, EventArgs.Empty); }
        public void PublishOperation(VolumeOperationSnapshot snapshot) { VolumeOperation = snapshot; operations?.Invoke(this, EventArgs.Empty); }
        private void PublishPreparation(VolumeDeletionPreparationSnapshot snapshot) { VolumeDeletionPreparation = snapshot; preparations?.Invoke(this, EventArgs.Empty); }
        public void RefreshVolumes() { }
        public void SelectVolume(string? name) => Publish(VolumeSnapshot with { SelectedVolumeName = name });
        public void RefreshVolumeInspection() { }
        public void ClearVolumeInspection() => Publish(VolumeSnapshot with { Inspection = null, InspectionState = ResourceReadState.NotRequested });
        public Task<VolumeDeletionPreparationSnapshot> PrepareVolumeDeletionAsync(VolumeDeletionTarget target, CancellationToken cancellationToken = default)
        {
            LastTarget = target; LastPreparationToken = cancellationToken;
            var snapshot = new VolumeDeletionPreparationSnapshot(new(++Prepares, 1), target, VolumePreparationPhase.Loading);
            var completion = new TaskCompletionSource<VolumeDeletionPreparationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); work[Prepares] = completion;
            PublishPreparation(snapshot);
            // Deliberately non-cooperative task completion, but cancellation still immediately fences publication.
            cancellationToken.Register(() => { if (VolumeDeletionPreparation?.Identity.PreparationId == snapshot.Identity.PreparationId) Invalidate(VolumePreparationFailureKind.Canceled); });
            return completion.Task;
        }
        public bool IsVolumeDeletionPreparationCurrent(VolumePreparationIdentity identity) => VolumeDeletionPreparation is { } current && current.Identity == identity && current.HasUsableEvidence;
        public void Invalidate(VolumePreparationFailureKind kind) => PublishPreparation(VolumeDeletionPreparation! with
        { Identity = VolumeDeletionPreparation.Identity with { Revision = VolumeDeletionPreparation.Identity.Revision + 1 }, Phase = VolumePreparationPhase.Invalidated, Failure = new(kind) });
        public Task CloseVolumeDeletionPreparationAsync(VolumePreparationIdentity identity)
        {
            ClosedIds.Add(identity.PreparationId);
            if (VolumeDeletionPreparation?.Identity.PreparationId != identity.PreparationId) return Task.CompletedTask;
            Invalidate(VolumePreparationFailureKind.Closed); return work[identity.PreparationId].Task;
        }
        public void CompleteOldPreparation(long id, VolumeDependencies dependencies) => work[id].TrySetResult(new(new(id, 2), new(1, dependencies.VolumeName), VolumePreparationPhase.Completed, dependencies));
        public void CompletePreparation(VolumeDependencies dependencies)
        {
            var current = VolumeDeletionPreparation!;
            if (current.Phase == VolumePreparationPhase.Loading) PublishPreparation(current with { Identity = current.Identity with { Revision = current.Identity.Revision + 1 }, Phase = VolumePreparationPhase.Completed, Dependencies = dependencies });
            work[current.Identity.PreparationId].TrySetResult(VolumeDeletionPreparation!);
        }
        public void FailPreparation(EngineResourceFailure failure)
        {
            var current = VolumeDeletionPreparation!;
            PublishPreparation(current with { Identity = current.Identity with { Revision = current.Identity.Revision + 1 }, Phase = VolumePreparationPhase.Failed, Failure = new(VolumePreparationFailureKind.AcquisitionFailed, failure) });
            work[current.Identity.PreparationId].TrySetResult(VolumeDeletionPreparation!);
        }
        public Task<VolumeOperationSnapshot> ExecuteVolumeOperationAsync(VolumeOperationRequest request, CancellationToken cancellationToken = default)
        {
            ++Calls; LastRequest = request; LastCommandToken = cancellationToken;
            if (RejectAdmission || !IsVolumeDeletionPreparationCurrent(request.Preparation)) return Task.FromResult(new VolumeOperationSnapshot(0, request, VolumeOperationPhase.Completed, Resolution: VolumeOperationResolution.NotDispatched, Rejection: VolumeOperationRejection.PreparationNotCurrent));
            var preparation = VolumeDeletionPreparation!; PublishPreparation(preparation with { Phase = VolumePreparationPhase.Consumed, Identity = preparation.Identity with { Revision = preparation.Identity.Revision + 1 } });
            operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PublishOperation(new(Calls, request, VolumeOperationPhase.CommandPending, Preparation: preparation)); OnExecute?.Invoke(); return operationCompletion.Task;
        }
        public void CompleteOperation(VolumeOperationResolution resolution, VolumeCommandResult result, VolumeDependencies? dependencies = null)
        {
            var current = VolumeOperation!;
            PublishOperation(current with { Phase = VolumeOperationPhase.Completed, Resolution = resolution, CommandOutcome = result,
                ObservedConnectionGeneration = current.Request.Target.ConnectionGeneration, ObservedExists = resolution == VolumeOperationResolution.ObservedPresent ? true : resolution == VolumeOperationResolution.ObservedAbsent ? false : null,
                RefreshedDependencies = dependencies, DependencyObservationState = dependencies is null ? VolumeDependencyObservationState.NotRequested : VolumeDependencyObservationState.Incomplete });
            operationCompletion?.TrySetResult(VolumeOperation!);
        }
    }
}
