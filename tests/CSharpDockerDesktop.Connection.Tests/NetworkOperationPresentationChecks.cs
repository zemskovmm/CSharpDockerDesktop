using System.Collections.Concurrent;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static class NetworkOperationPresentationChecks
{
    private const string A = " Network-Exact-é ", B = " network-Exact-é ";
    private static NetworkSummary Row(string id, string? name = "custom") => new(id, name, "bridge", "local", false, null);
    private static NetworkBrowsingSnapshot Ready(long generation = 1) => new(generation, ResourceReadState.Ready, [Row(A), Row(B)]);
    private static NetworkInspection Inspection(int count = 0, string id = A, bool unavailable = false) => new(id, "custom", "bridge", "local", false, null, null,
        unavailable ? null : Enumerable.Range(0, count).ToDictionary(i => $" Attachment-é-{i} ", i => new NetworkAttachment(" name ", $" Endpoint-{i} ", "00:11", "10.1.0.2/24", null)), "{}");
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task<(Ui Ui, Browser Browser, NetworkBrowserView View)> Open()
    {
        var ui = new Ui(); var browser = new Browser(); var view = new NetworkBrowserView(browser, ui.Post);
        view.Attach(); await ui.Until(() => !view.Filtering); view.Select(A); return (ui, browser, view);
    }
    private static async Task<(NetworkDeletePresentation Presentation, NetworkDeleteConfirmation Confirmation)> Prepared(NetworkBrowserView view, Browser browser, Ui ui)
    {
        var presentation = view.BeginDelete()!; browser.CompletePreparation(Inspection());
        await ui.Until(() => view.DeleteConfirmation is not null); return (presentation, view.DeleteConfirmation!);
    }
    public static async Task BuiltInsAndClassifiedBlockedEvidence()
    {
        foreach (string name in new[] { "bridge", "host", "none" })
        {
            var (ui, browser, view) = await Open(); using (view)
            {
                browser.Publish(Ready() with { Networks = [Row(A, name)] }); ui.Pump();
                Check(!view.CanDelete && view.BeginDelete() is null && browser.Prepares == 0, "Known built-in network must never start Delete");
            }
        }
        foreach (var reason in Enum.GetValues<NetworkDeletionBlockReason>())
        {
            var (ui, browser, view) = await Open(); using (view)
            {
                var presentation = view.BeginDelete()!;
                browser.CompletePreparation(Inspection(reason == NetworkDeletionBlockReason.AttachmentsRemain ? 2 : 0, unavailable: reason == NetworkDeletionBlockReason.AttachmentsUnavailable), NetworkDeletionEligibility.Blocked, [reason]);
                await ui.Until(() => view.PreparationMessage.Contains("completed"));
                Check(view.DeleteConfirmation is null && view.PreparationMessage.Contains(reason.ToString()) && browser.Calls == 0, "Every built-in, dependency, malformed/contradictory/unavailable classification blocks confirmation");
                view.CancelDelete(presentation); await view.PreparationCleanup;
            }
        }
    }
    public static async Task LoadingCancelFailureAndExactCapture()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var presentation = view.BeginDelete()!;
            Check(presentation.Target == new NetworkDeletionTarget(1, A) && browser.LastTarget == presentation.Target && view.DeleteConfirmation is null
                && view.PreparationMessage.Contains("Loading") && !view.CanDelete, "Capture complete ordinal identity and generation; loading does not authorize mutation");
            view.CancelDelete(presentation);
            Check(view.Deletion is null && browser.LastPreparationToken.IsCancellationRequested && !view.PreparationCleanup.IsCompleted, "Cancel immediately fences and asynchronously joins non-cooperative preparation");
            browser.CompletePreparation(Inspection()); await view.PreparationCleanup; await ui.Idle();
            Check(view.DeleteConfirmation is null && browser.Calls == 0 && view.CanDelete, "Delayed completion never revives canceled confirmation");
            presentation = view.BeginDelete()!; browser.FailPreparation(); await ui.Until(() => view.PreparationMessage.Contains("failed"));
            Check(view.DeleteConfirmation is null && !view.PreparationMessage.Contains("PRIVATE") && view.PreparationDetails == "", "Failed evidence remains classified");
            view.ShowPreparationDetails(true); Check(view.PreparationDetails.Contains("  PRIVATE daemon\n  "), "Deliberate reveal preserves original whitespace");
            view.CancelDelete(presentation); await view.PreparationCleanup;
            Check(view.PreparationDetails == "" && browser.Calls == 0, "Cancellation clears original evidence and never dispatches");
        }
    }
    public static async Task AttachmentsUnavailableAndBoundedFullCopy()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var presentation = view.BeginDelete()!; browser.CompletePreparation(Inspection(30000));
            await ui.Until(() => view.PreparationAttachments.Count == 30000);
            long before = GC.GetAllocatedBytesForCurrentThread(); var rows = view.PreparationAttachments; var last = rows[29999]; long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(rows is NetworkAttachmentRows && last.Id == " Attachment-é-29999 " && bytes < 10000 && last.Text.Contains("Endpoint-29999") && view.DeleteConfirmation is null, "All attachments reachable with bounded lazy row text and no dependency authorization");
            string copied = ""; var copy = view.CopyAttachmentAsync(last, false, text => copied = text); await ui.Until(() => copy.IsCompleted);
            Check(await copy && copied.Contains(" Attachment-é-29999 ") && copied.Contains(" Endpoint-29999 ") && copied.Contains("10.1.0.2/24") && copied.Contains("unavailable (null)"), "Deliberate copy preserves complete opaque identities and nullable endpoint metadata even for blocked evidence");
            copy = view.CopyAttachmentAsync(last, false, _ => throw new Exception("Invalidated copy published"));
            browser.InvalidateNetworkDeletionPreparation(browser.NetworkDeletionPreparation!.Identity);
            await ui.Until(() => copy.IsCompleted); Check(!await copy, "A revised preparation fences queued copy even when its inspected attachment object is retained");
            copy = view.CopyAttachmentAsync(last, false, _ => throw new Exception("Stale copy published")); view.CancelDelete(presentation);
            await ui.Until(() => copy.IsCompleted); Check(!await copy, "Cancellation fences queued attachment copy"); await view.PreparationCleanup;
            presentation = view.BeginDelete()!; browser.CompletePreparation(Inspection(unavailable: true)); await ui.Idle();
            Check(view.DeleteConfirmation is null && view.PreparationMessage.Contains("unavailable (null collection)"), "Missing evidence distinguishable from observed empty");
            view.CancelDelete(presentation); await view.PreparationCleanup;
            var (eligible, _) = await Prepared(view, browser, ui);
            Check(view.PreparationMessage.Contains("inspected empty") && NetworkBrowserView.PrecheckExplanation.Contains("cluster-wide") && NetworkBrowserView.PrecheckExplanation.Contains("race"), "Eligible inspected empty evidence never promises global completeness or race elimination");
            string target = ""; Check(view.CopyDeletionTarget(eligible, text => target = text) && target == A, "Captured network copy preserves exact opaque ID");
            view.CancelDelete(eligible); await view.PreparationCleanup;
            var malformed = view.BeginDelete()!;
            browser.CompletePreparation(Inspection() with { Containers = new Dictionary<string, NetworkAttachment> { ["full-malformed-endpoint"] = null! } }, NetworkDeletionEligibility.Blocked, [NetworkDeletionBlockReason.AttachmentEvidenceInvalid]);
            await ui.Until(() => view.PreparationAttachments.Count == 1);
            Check(view.DeleteConfirmation is null && view.PreparationAttachments[0].Text.Contains("unavailable (null)"), "Malformed null endpoint metadata remains accessible without enabling confirmation or throwing");
            view.CancelDelete(malformed); await view.PreparationCleanup;
            Check(!view.CopyDeletionTarget(eligible, _ => throw new Exception("obsolete target")), "Obsolete dialog cannot copy target");
        }
    }
    public static async Task ExactSingleUseAndSessionOwnership()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (presentation, confirmation) = await Prepared(view, browser, ui);
            browser.OnExecute = () => view.ConfirmDelete(presentation, confirmation);
            view.ConfirmDelete(presentation, confirmation); view.ConfirmDelete(presentation, confirmation);
            Check(browser.Calls == 1 && ReferenceEquals(browser.LastRequest, confirmation.Request) && browser.LastRequest!.Target.Id == A
                && browser.LastCommandToken == default && view.Deletion is null, "Consume exact request before reentrancy, single ordinary deletion with Application-owned lifetime");
            Check(view.OperationMessage.Contains("Command pending") && !view.CanDelete, "Pending original target remains visible");
            view.Dispose(); browser.CompleteOperation(NetworkOperationResolution.ObservedAbsent, new(NetworkCommandDisposition.Acknowledged, 204));
            view.Attach(); await ui.Until(() => !view.Filtering);
            Check(view.OperationMessage.Contains("Observed absent") && view.Operation!.Request.Target.Id == A && browser.Calls == 1, "Navigation does not abandon reconciliation or replay commands");
        }
    }
    public static async Task RevisionsReplacementAndLiveInvalidation()
    {
        foreach (int scenario in Enumerable.Range(0, 4))
        {
            var (ui, browser, view) = await Open(); using (view)
            {
                var (presentation, confirmation) = await Prepared(view, browser, ui); await ui.Idle();
                var old = browser.NetworkDeletionPreparation!;
                var changed = scenario == 0 ? old with { Identity = old.Identity with { Revision = old.Identity.Revision + 1 } }
                    : scenario == 1 ? old with { Identity = new(88, 2) }
                    : scenario == 2 ? old with { Phase = NetworkPreparationPhase.Invalidated }
                    : old with { Phase = NetworkPreparationPhase.Consumed };
                browser.PublishPreparation(changed);
                Check(view.DeleteConfirmation is null, "Live snapshot fences before dispatcher processes invalidation");
                view.ConfirmDelete(presentation, confirmation); ui.Pump();
                Check(browser.Calls == 0 && view.Deletion is null, "Revised, replaced, invalidated and consumed preparation never silently renew confirmation");
                browser.CompletePreparation(Inspection()); await view.PreparationCleanup;
            }
        }
    }
    public static async Task BlockedRevisionNeverRenewsConfirmation()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var presentation = view.BeginDelete()!; browser.CompletePreparation(Inspection(2)); await ui.Idle();
            var blocked = browser.NetworkDeletionPreparation!;
            browser.PublishPreparation(blocked with { Identity = blocked.Identity with { Revision = blocked.Identity.Revision + 1 },
                Inspection = Inspection(), Eligibility = NetworkDeletionEligibility.PrecheckEligible, Reasons = [] }); ui.Pump();
            Check(view.DeleteConfirmation is null && browser.Calls == 0, "A revised blocked snapshot cannot silently become a fresh confirmation");
            view.CancelDelete(presentation); await view.PreparationCleanup;
        }
    }
    public static async Task SelectionNavigationDisconnectAndDelayedCallbacks()
    {
        foreach (int scenario in Enumerable.Range(0, 6))
        {
            var (ui, browser, view) = await Open(); using (view)
            {
                var (presentation, confirmation) = await Prepared(view, browser, ui);
                if (scenario == 0) view.Select(B);
                if (scenario == 1) view.Dispose();
                if (scenario == 2) browser.Publish(Ready(2));
                if (scenario == 3) browser.Publish(new(1, ResourceReadState.Unavailable, []));
                if (scenario == 4) browser.Publish(Ready() with { Networks = [Row(B)] });
                if (scenario == 5) { view.OpenSelected(); view.CloseDetail(); }
                Check(view.DeleteConfirmation is null, "Selection, navigation, connection, disappearance and detail closure fence synchronously");
                view.ConfirmDelete(presentation, confirmation); ui.Pump(); Check(browser.Calls == 0, "Obsolete exact preparation never retargets");
                view.Dispose(); view.Attach(); await ui.Until(() => !view.Filtering); browser.Publish(Ready()); ui.Pump(); view.Select(A);
                var next = view.BeginDelete()!; view.CancelDelete(presentation); view.ConfirmDelete(presentation, confirmation);
                Check(ReferenceEquals(view.Deletion, next), "Old lifetime callbacks cannot close or submit fresh presentation");
                view.CancelDelete(next); browser.CompletePreparation(Inspection()); await view.PreparationCleanup;
            }
        }
        var (slowUi, slowBrowser, slowView) = await Open(); using (slowView)
        {
            var presentation = slowView.BeginDelete()!; slowView.Dispose();
            Check(!slowView.PreparationCleanup.IsCompleted && slowBrowser.Subscribers == 0, "Navigation joins preparation asynchronously and releases all subscriptions");
            slowView.Attach(); slowBrowser.CompletePreparation(Inspection()); await slowView.PreparationCleanup; await slowUi.Idle();
            Check(slowView.DeleteConfirmation is null && slowBrowser.Calls == 0, "Slow prior completion never revives after reattachment");
        }
    }
    public static async Task ConflictRetainedEvidenceAndFreshExplicitAttempt()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (presentation, confirmation) = await Prepared(view, browser, ui); view.ConfirmDelete(presentation, confirmation);
            browser.CompleteOperation(NetworkOperationResolution.ObservedPresent, new(NetworkCommandDisposition.DaemonResponse, 409, "  PRIVATE conflict\n  ", "PRIVATE body", new(NetworkCommandFailureKind.Conflict, "PRIVATE failure")), Inspection(2)); await ui.Idle();
            Check(view.OperationMessage.Contains("Attachment conflict") && view.OperationMessage.Contains("fresh explicit precheck and confirmation") && view.OperationMessage.Contains("Complete")
                && !view.OperationMessage.Contains("PRIVATE") && view.RefreshedAttachments.Count == 0, "Classified conflict refresh currentness and recovery; raw original metadata hidden");
            view.ShowOperationDetails(true); await ui.Until(() => !view.PreparingOperationDetails && view.RefreshedAttachments.Count == 2);
            Check(view.OperationDetails.Contains("  PRIVATE conflict\n  ") && view.OperationDetails.Contains(A) && view.RefreshedAttachments[1].Text.Contains("Endpoint-1"), "Retained conflict includes exact original target/daemon evidence and every refreshed endpoint");
            view.ConfirmDelete(presentation, confirmation); Check(browser.Calls == 1 && view.DeleteConfirmation is null, "Conflict evidence never renews consumed authorization");
            view.Dispose(); view.Attach(); await ui.Until(() => !view.Filtering);
            Check(view.Operation!.Request.Target.Id == A && view.OperationMessage.Contains("Observed present") && view.OperationDetails == "", "Reopening retains outcome but clears deliberate reveal");
            view.Select(A); var next = view.BeginDelete()!; Check(browser.Prepares == 2 && view.DeleteConfirmation is null, "Retry needs explicit new preparation");
            view.CancelDelete(next); browser.CompletePreparation(Inspection()); await view.PreparationCleanup;
        }
    }
    public static async Task OffThreadCoalescingPrivateRevealAndCopyFences()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var operation = new NetworkOperationSnapshot(3, new(new(1, A), new(1, 2)), NetworkOperationPhase.Completed,
                CommandOutcome: new(NetworkCommandDisposition.Uncertain, DaemonMessage: "  PRIVATE original\n "), Resolution: NetworkOperationResolution.Unresolved,
                RefreshedInspection: Inspection(2), AttachmentObservationState: NetworkAttachmentObservationState.Invalidated);
            await ui.Idle(); await Task.Run(() => { for (int i = 0; i < 200; ++i) browser.PublishOperation(operation); });
            Check(ui.Count == 1 && view.OperationDetails == "", "Off-thread notifications coalesce and raw text remains hidden"); ui.Pump();
            view.ShowOperationDetails(true); view.ShowOperationDetails(false); await ui.Idle(); Check(view.OperationDetails == "", "Hide fences queued original reveal");
            view.ShowOperationDetails(true); await ui.Until(() => !view.PreparingOperationDetails && view.RefreshedAttachments.Count == 2);
            Check(view.OperationDetails.Contains("  PRIVATE original\n "), "Explicit reveal preserves original whitespace");
            var copy = view.CopyAttachmentAsync(view.RefreshedAttachments[0], true, _ => throw new Exception("obsolete private copy"));
            browser.PublishOperation(operation with { OperationId = 4 });
            Check(view.OperationDetails == "" && view.RefreshedAttachments.Count == 0, "Immutable replacement immediately hides private evidence");
            await ui.Until(() => copy.IsCompleted); Check(!await copy && !view.ShowingOperationDetails, "Delayed private copy is fenced by immutable operation identity");
            view.ShowOperationDetails(true); view.Dispose(); await ui.Idle(); Check(view.OperationDetails == "" && view.Operation is null, "Detached callbacks cannot publish private evidence");
        }
    }
    public static Task CommandOutcomeDistinctions()
    {
        var request = new NetworkOperationRequest(new(1, A), new(1, 2));
        foreach (var rejection in Enum.GetValues<NetworkOperationRejection>())
        {
            string status = NetworkOperationDisplay.Status(new(0, request, NetworkOperationPhase.Completed, Resolution: NetworkOperationResolution.NotDispatched, Rejection: rejection));
            Check(status.Contains($"Admission rejection: {rejection}") && status.Contains("No delete command was sent"), "Each rejected admission is distinct from a dispatched outcome");
        }
        foreach (var interruption in Enum.GetValues<NetworkOperationInterruption>())
        {
            string status = NetworkOperationDisplay.Status(new(1, request, NetworkOperationPhase.Completed, Resolution: NetworkOperationResolution.Unresolved, Interruption: interruption));
            Check(status.Contains($"Interruption: {interruption}") && status.Contains("never automatically replayed"), "Interruption retains uncertainty and no automatic replay");
        }
        foreach (var disposition in Enum.GetValues<NetworkCommandDisposition>()) foreach (bool exists in new[] { true, false })
        {
            string status = NetworkOperationDisplay.Status(new(1, request, NetworkOperationPhase.Completed, CommandOutcome: new(disposition),
                Resolution: exists ? NetworkOperationResolution.ObservedPresent : NetworkOperationResolution.ObservedAbsent, ObservedExists: exists, ObservedConnectionGeneration: 7));
            Check(status.Contains($"disposition: {disposition}") && status.Contains($"ID {(exists ? "present" : "absent")}") && status.Contains("generation: 7") && status.Contains("Acknowledgement alone is not deletion"), "Command disposition, independent observation and observation generation remain separate");
        }
        return Task.CompletedTask;
    }
    public static async Task ExternalPendingAndRejectedAdmission()
    {
        var (ui, browser, view) = await Open(); using (view)
        {
            var (presentation, confirmation) = await Prepared(view, browser, ui);
            var external = new NetworkOperationSnapshot(88, new(new(1, B), new(55, 2)), NetworkOperationPhase.CommandPending);
            browser.PublishOperation(external); Check(view.DeleteConfirmation is null, "Another pending Application operation fences current confirmation");
            view.ConfirmDelete(presentation, confirmation); ui.Pump(); Check(browser.Calls == 0 && view.Operation!.OperationId == 88, "Pending work retains original target without duplicate dispatch");
            browser.PublishOperation(external with { Phase = NetworkOperationPhase.Completed, Resolution = NetworkOperationResolution.Unresolved }); ui.Pump();
            var (next, nextConfirmation) = await Prepared(view, browser, ui); browser.RejectAdmission = true;
            view.ConfirmDelete(next, nextConfirmation); await ui.Idle();
            Check(view.Operation!.OperationId == 88 && view.AdmissionMessage.Contains("PreparationNotCurrent"), "Atomic rejected admission reports safe reason and preserves prior retained operation");
        }
    }
    private sealed class Ui
    {
        private readonly ConcurrentQueue<Action> queue = new(); public int Count => queue.Count;
        public bool Post(Action action) { queue.Enqueue(action); return true; }
        public void Pump() { while (queue.TryDequeue(out var action)) action(); }
        public async Task Until(Func<bool> condition)
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); do { Pump(); if (condition()) return; await Task.Delay(1, timeout.Token); } while (true); }
        public async Task Idle() { for (int i = 0; i < 20; ++i) { Pump(); await Task.Delay(1); } Pump(); }
    }
    private sealed class Browser : INetworkBrowser, INetworkDeletionPreparation, INetworkOperations
    {
        private EventHandler? networks, preparations, operations;
        private readonly Dictionary<long, TaskCompletionSource<NetworkDeletionPreparationSnapshot>> work = new();
        private TaskCompletionSource<NetworkOperationSnapshot>? operationCompletion;
        public int Prepares, Calls, Subscribers;
        public bool RejectAdmission; public Action? OnExecute;
        public NetworkDeletionTarget? LastTarget; public NetworkOperationRequest? LastRequest;
        public CancellationToken LastPreparationToken, LastCommandToken;
        public NetworkBrowsingSnapshot NetworkSnapshot { get; private set; } = Ready();
        public NetworkDeletionPreparationSnapshot? NetworkDeletionPreparation { get; private set; }
        public NetworkOperationSnapshot? NetworkOperation { get; private set; }
        public event EventHandler? NetworksChanged { add { networks += value; ++Subscribers; } remove { networks -= value; --Subscribers; } }
        public event EventHandler? NetworkDeletionPreparationChanged { add { preparations += value; ++Subscribers; } remove { preparations -= value; --Subscribers; } }
        public event EventHandler? NetworkOperationChanged { add { operations += value; ++Subscribers; } remove { operations -= value; --Subscribers; } }
        public void Publish(NetworkBrowsingSnapshot snapshot) { NetworkSnapshot = snapshot; networks?.Invoke(this, EventArgs.Empty); }
        public void PublishOperation(NetworkOperationSnapshot snapshot) { NetworkOperation = snapshot; operations?.Invoke(this, EventArgs.Empty); }
        public void PublishPreparation(NetworkDeletionPreparationSnapshot snapshot) { NetworkDeletionPreparation = snapshot; preparations?.Invoke(this, EventArgs.Empty); }
        public void RefreshNetworks() { }
        public void SelectNetwork(string? id) => Publish(NetworkSnapshot with { SelectedNetworkId = id });
        public void RefreshNetworkInspection() { }
        public void ClearNetworkInspection() => Publish(NetworkSnapshot with { Inspection = null, InspectionState = ResourceReadState.NotRequested });
        public Task<NetworkDeletionPreparationSnapshot> PrepareNetworkDeletionAsync(NetworkDeletionTarget target, CancellationToken cancellationToken = default)
        {
            LastTarget = target; LastPreparationToken = cancellationToken;
            var snapshot = new NetworkDeletionPreparationSnapshot(new(++Prepares, 1), target, NetworkPreparationPhase.Loading, NetworkDeletionEligibility.Unknown, []);
            var completion = new TaskCompletionSource<NetworkDeletionPreparationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); work[Prepares] = completion;
            PublishPreparation(snapshot);
            cancellationToken.Register(() => { if (NetworkDeletionPreparation?.Identity.PreparationId == snapshot.Identity.PreparationId) InvalidateNetworkDeletionPreparation(NetworkDeletionPreparation.Identity); });
            return completion.Task;
        }
        public bool IsNetworkDeletionPreparationCurrent(NetworkPreparationIdentity identity) => NetworkDeletionPreparation is { } current && current.Identity == identity && current.IsPrecheckEligible && current.Target.ConnectionGeneration == NetworkSnapshot.ConnectionGeneration;
        public void InvalidateNetworkDeletionPreparation(NetworkPreparationIdentity identity)
        { if (NetworkDeletionPreparation?.Identity == identity) PublishPreparation(NetworkDeletionPreparation with { Identity = identity with { Revision = identity.Revision + 1 }, Phase = NetworkPreparationPhase.Invalidated }); }
        public Task CloseNetworkDeletionPreparationAsync(NetworkPreparationIdentity identity)
        {
            if (NetworkDeletionPreparation?.Identity.PreparationId != identity.PreparationId) return Task.CompletedTask;
            InvalidateNetworkDeletionPreparation(NetworkDeletionPreparation.Identity); return work.TryGetValue(identity.PreparationId, out var completion) ? completion.Task : Task.CompletedTask;
        }
        public void CompletePreparation(NetworkInspection inspection, NetworkDeletionEligibility? eligibility = null, IReadOnlyList<NetworkDeletionBlockReason>? reasons = null)
        {
            var current = NetworkDeletionPreparation!;
            if (current.Phase == NetworkPreparationPhase.Loading) PublishPreparation(current with
            {
                Identity = current.Identity with { Revision = current.Identity.Revision + 1 }, Phase = NetworkPreparationPhase.Completed, Inspection = inspection,
                Eligibility = eligibility ?? (inspection.Containers is null ? NetworkDeletionEligibility.Unknown : inspection.Containers.Count == 0 ? NetworkDeletionEligibility.PrecheckEligible : NetworkDeletionEligibility.Blocked),
                Reasons = reasons ?? (inspection.Containers is null ? [NetworkDeletionBlockReason.AttachmentsUnavailable] : inspection.Containers.Count == 0 ? [] : [NetworkDeletionBlockReason.AttachmentsRemain])
            });
            if (work.TryGetValue(current.Identity.PreparationId, out var completion)) completion.TrySetResult(NetworkDeletionPreparation!);
            else foreach (var pair in work) pair.Value.TrySetResult(NetworkDeletionPreparation! with { Identity = new(pair.Key, 9), Phase = NetworkPreparationPhase.Invalidated });
        }
        public void FailPreparation()
        {
            var current = NetworkDeletionPreparation!; PublishPreparation(current with { Phase = NetworkPreparationPhase.Failed,
                Failure = new(NetworkPreparationFailureKind.AcquisitionFailed, new(EngineResourceFailureKind.AccessDenied, "PRIVATE description", 403, "  PRIVATE daemon\n  ")) });
            work[current.Identity.PreparationId].TrySetResult(NetworkDeletionPreparation!);
        }
        public Task<NetworkOperationSnapshot> ExecuteNetworkOperationAsync(NetworkOperationRequest request, CancellationToken cancellationToken = default)
        {
            ++Calls; LastRequest = request; LastCommandToken = cancellationToken;
            if (RejectAdmission || !IsNetworkDeletionPreparationCurrent(request.Preparation)) return Task.FromResult(new NetworkOperationSnapshot(0, request, NetworkOperationPhase.Completed, Resolution: NetworkOperationResolution.NotDispatched, Rejection: NetworkOperationRejection.PreparationNotCurrent));
            var preparation = NetworkDeletionPreparation!; PublishPreparation(preparation with { Phase = NetworkPreparationPhase.Consumed, Identity = preparation.Identity with { Revision = preparation.Identity.Revision + 1 } });
            operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PublishOperation(new(Calls, request, NetworkOperationPhase.CommandPending, Preparation: preparation)); OnExecute?.Invoke(); return operationCompletion.Task;
        }
        public void CompleteOperation(NetworkOperationResolution resolution, NetworkCommandResult result, NetworkInspection? inspection = null)
        {
            var current = NetworkOperation!; PublishOperation(current with { Phase = NetworkOperationPhase.Completed, Resolution = resolution, CommandOutcome = result,
                ObservedConnectionGeneration = current.Request.Target.ConnectionGeneration, ObservedExists = resolution == NetworkOperationResolution.ObservedPresent ? true : resolution == NetworkOperationResolution.ObservedAbsent ? false : null,
                RefreshedInspection = inspection, AttachmentObservationState = inspection is null ? NetworkAttachmentObservationState.NotRequested : NetworkAttachmentObservationState.Complete });
            operationCompletion?.TrySetResult(NetworkOperation!);
        }
    }
}
