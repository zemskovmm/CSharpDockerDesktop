using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;

internal static partial class ContainerPresentationChecks
{
    public static async Task LifecycleAvailability()
    {
        var clock = new Dispatcher();
        var browser = new Browser();
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach();
        foreach (string? state in new[] { "created", "exited", "running", "paused", "restarting", "dead", "future-state", null })
        {
            browser.Publish(Ready(Row(A, "alpha", state: state))); await clock.Settle(view); view.Select(A);
            Check(view.CanStart == (state is "created" or "exited"), "Start availability: " + state);
            Check(view.CanStop == (state == "running") && view.CanRestart == view.CanStop, "Stop/restart availability: " + state);
            Check(view.CanDelete == (state is "created" or "exited" or "dead"), "Ordinary Delete availability: " + state);
            Check(view.CanForceDelete == (state is "running" or "paused" or "restarting"), "Force Delete availability: " + state);
            Check(view.ActionMessage.Contains(ContainerDisplay.Text(state)), "State must have a textual indication");
            view.Execute(ContainerCommand.Delete); Check(browser.Requests.Count == 0, "Delete cannot bypass confirmation");
        }
        browser.Publish(Ready(Row(A, state: "exited"))); await clock.Settle(view);
        view.Execute(ContainerCommand.Start); view.Execute(ContainerCommand.Start); view.Execute(ContainerCommand.Stop);
        Check(browser.Requests.Count == 1 && browser.Requests[0] == new ContainerOperationRequest(new(1, A), ContainerCommand.Start), "Repeated actions have no queue");
        Check(!view.CanStart && view.OperationMessage.Contains("Command pending"), "Pending command status");
        view.Search("alpha"); view.Refresh(); view.Select(A); view.OpenSelected();
        Check(browser.RefreshCalls == 1 && browser.SelectCalls == 1, "Browsing and details remain responsive while command pending");
        var acknowledged = new ContainerCommandResult(ContainerCommand.Start, ContainerCommandDisposition.Acknowledged, 204,
            Acknowledgement: ContainerCommandAcknowledgement.Accepted);
        browser.PublishOperation(browser.ContainerOperation! with { Phase = ContainerOperationPhase.Reconciling, CommandOutcome = acknowledged });
        clock.Pump(); Check(view.OperationMessage.Contains("Reconciling") && !view.OperationMessage.Contains("Observed requested state"), "Acknowledgement is not success");
        browser.CompleteOperation(ContainerOperationResolution.ObservedRequestedState, acknowledged);
        await Task.Delay(10); await clock.Settle(view);
        Check(view.OperationMessage.Contains("Observed requested state") && view.CanStart, "Authoritative retained completion releases admission");
        browser.Publish(new(1, ResourceReadState.Failed, [Row(A, state: "exited")])); clock.Pump();
        Check(!view.CanStart, "Stale rows from a failed read do not authorize new actions");
    }

    public static async Task DeleteSafety()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A, "alpha", state: "exited"), Row(B, "beta", state: "running")) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view); view.Select(A);
        var canceled = view.BeginDelete(false)!;
        Check(canceled.Message.Contains(A) && canceled.Message.Contains("alpha") && canceled.Message.Contains("Force is false")
            && canceled.Message.Contains("v=false"), "Confirmation identifies full captured target and parameters");
        view.CancelDelete(canceled); view.ConfirmDelete(canceled);
        Check(browser.Requests.Count == 0, "Cancellation/dismissal dispatches nothing");
        var selection = view.BeginDelete(false)!; view.Select(B); view.ConfirmDelete(selection);
        Check(browser.Requests.Count == 0 && view.DeleteConfirmation is null, "Selection change cannot redirect confirmed target");
        Check(view.BeginDelete(false) is null, "Running container cannot be deleted ordinarily");
        var force = view.BeginDelete(true)!;
        Check(force.Message.Contains(B) && force.Message.Contains("Force Delete") && force.Message.Contains("Force is true"), "Force is a separately explicit dangerous choice");
        view.ConfirmDelete(force); view.ConfirmDelete(force);
        Check(browser.Requests.Count == 1 && browser.Requests[0] == new ContainerOperationRequest(new(1, B), ContainerCommand.Delete, true), "Force choice consumed once; no hidden Stop");
        browser.CompleteOperation(ContainerOperationResolution.ObservedRequestedState); await Task.Delay(10); await clock.Settle(view);
        view.Select(A); var ordinary = view.BeginDelete(false)!; view.ConfirmDelete(ordinary);
        Check(browser.Requests.Count == 2 && browser.Requests[1] == new ContainerOperationRequest(new(1, A), ContainerCommand.Delete), "Ordinary choice preserves defaults and full identity");
        browser.CompleteOperation(ContainerOperationResolution.ObservedDifferentState); await Task.Delay(10); await clock.Settle(view);

        foreach (var replacement in new[]
        {
            new ContainerBrowsingSnapshot(2, ResourceReadState.Ready, [Row(A, state: "exited")]),
            new ContainerBrowsingSnapshot(1, ResourceReadState.Ready, [Row(B, state: "exited")]),
            new ContainerBrowsingSnapshot(1, ResourceReadState.Unavailable, []),
            new ContainerBrowsingSnapshot(1, ResourceReadState.Ready, [Row(A, state: "running")])
        })
        {
            browser.Publish(Ready(Row(A, state: "exited"))); await clock.Settle(view); view.Select(A);
            var old = view.BeginDelete(false)!;
            browser.Publish(replacement); // Deliberately do not drain the invalidation callback.
            view.ConfirmDelete(old);
            Check(browser.Requests.Count == 2, "Current session guard rejects undrained generation/disappearance/disconnect/state invalidation");
            await clock.Settle(view);
        }
        browser.Publish(Ready(Row(A, state: "exited"))); await clock.Settle(view); view.Select(A);
        var first = view.BeginDelete(false)!; view.CancelDelete(first); var second = view.BeginDelete(false)!;
        view.ConfirmDelete(first); Check(ReferenceEquals(second, view.DeleteConfirmation), "Obsolete callback does not consume replacement confirmation");
        view.Dispose(); view.ConfirmDelete(second); view.Attach(); view.ConfirmDelete(second);
        Check(browser.Requests.Count == 2 && view.DeleteConfirmation is null && !view.DetailOpen, "Navigation and reentry never reopen or dispatch old confirmation");
    }

    public static async Task OperationAdmissionDiagnostics()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A, state: "running"), Row(B, state: "exited")) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view); view.Select(A); view.Execute(ContainerCommand.Restart);
        Check(browser.Requests.Single() == new ContainerOperationRequest(new(1, A), ContainerCommand.Restart), "Restart uses full identity and daemon grace-period default");
        browser.CompleteOperation(ContainerOperationResolution.ObservedRequestedState);
        await Task.Delay(10); await clock.Settle(view);
        view.Select(B); browser.RejectNext = true; view.Execute(ContainerCommand.Start);
        await Task.Delay(10); clock.Pump();
        Check(view.OperationMessage.Contains(A) && view.OperationMessage.Contains("Restart")
            && view.AdmissionMessage.Contains(B) && view.AdmissionMessage.Contains("Start")
            && view.AdmissionMessage.Contains("Not dispatched") && view.AdmissionMessage.Contains("CapacityExceeded")
            && browser.Requests.Count == 1, "Coordinator rejection is visible without overwriting the retained outcome or queueing");
        browser.ExecuteFailure = new InvalidOperationException("PRIVATE exceptional content");
        view.Execute(ContainerCommand.Start);
        Check(view.AdmissionMessage.Contains("Unresolved") && !view.AdmissionMessage.Contains("PRIVATE")
            && browser.Requests.Count == 1, "Unexpected capability failure is unresolved, never logged or retried");
    }

    public static async Task OperationLifetime()
    {
        var clock = new Dispatcher();
        var browser = new Browser { ContainerSnapshot = Ready(Row(A, state: "running"), Row(B, state: "exited")) };
        using var view = new ContainerBrowserView(browser, clock.Post);
        view.Attach(); await clock.Settle(view); view.Select(A); view.Execute(ContainerCommand.Stop); view.Select(B);
        Check(view.OperationMessage.Contains(A) && view.OperationMessage.Contains("Stop"), "Operation identity is independent of selection");
        var rejection = new ContainerCommandResult(ContainerCommand.Stop, ContainerCommandDisposition.DaemonResponse, 409,
            "PRIVATE original command", Failure: new(ContainerCommandFailureKind.Conflict, "Daemon rejected the command."));
        var observation = new EngineResourceFailure(EngineResourceFailureKind.AccessDenied, "Observation denied", 403, "PRIVATE original observation");
        int changes = 0; view.Changed += (_, _) => ++changes;
        await Task.Run(() => browser.PublishOperation(browser.ContainerOperation! with
        {
            Phase = ContainerOperationPhase.Reconciling, CommandOutcome = rejection, ObservationFailure = observation
        }));
        int before = changes;
        Check(!view.OperationMessage.Contains("Conflict") && clock.Count == 1, "Off-thread notification cannot directly mutate Presentation state");
        clock.Pump();
        Check(changes > before && view.OperationMessage.Contains("HTTP 409") && view.OperationMessage.Contains("Conflict")
            && view.OperationMessage.Contains("AccessDenied (HTTP 403)") && !view.OperationMessage.Contains("PRIVATE"), "Classified command and observation failures without raw daemon display");
        Check(view.HasOperationDetails && view.OperationDetails == "", "Raw sensitive daemon content remains hidden by default");
        view.ShowOperationDetails(true); Check(view.OperationDetails.Contains("PRIVATE original command") && view.OperationDetails.Contains("PRIVATE original observation"), "Deliberate daemon display preserves originals");
        view.OpenSelected(); clock.Pump();
        view.Dispose(); int disposed = changes;
        Check(browser.Subscribers == 0 && browser.OperationSubscribers == 0 && !browser.Completion!.Task.IsCompleted,
            "Departure detaches notifications/details but does not cancel accepted session operation");
        await Task.Run(() => browser.CompleteOperation(ContainerOperationResolution.Unresolved, rejection, observation));
        await Task.Delay(10); clock.Pump();
        Check(changes == disposed && view.Operation is null, "Obsolete completion cannot update disposed page");
        view.Attach(); await clock.Settle(view);
        Check(view.OperationMessage.Contains("Unresolved outcome") && view.OperationMessage.Contains(A)
            && view.OperationMessage.Contains("no automatic mutation retry") && view.OperationDetails == ""
            && view.SelectedId is null && !view.DetailOpen, "Reentry restores retained unresolved operation, without details or confirmation");
        Check(browser.Requests.Count == 1, "Uncertainty never causes mutation retry");
        browser.PublishOperation(browser.ContainerOperation! with { Resolution = ContainerOperationResolution.ObservedDifferentState }); clock.Pump();
        Check(view.OperationMessage.Contains("Observed different state"), "Different observed state distinct from unresolved");
        browser.PublishOperation(browser.ContainerOperation! with { Resolution = ContainerOperationResolution.NotDispatched,
            CommandOutcome = new(ContainerCommand.Stop, ContainerCommandDisposition.NotDispatched, Failure: new(ContainerCommandFailureKind.StaleConnection, "Stale connection")) });
        clock.Pump(); Check(view.OperationMessage.Contains("No command was sent") && view.OperationMessage.Contains("StaleConnection"), "No dispatch is distinct and classified");
        // A queued update from a prior page lifetime must not overwrite a reattached page.
        browser.PublishOperation(browser.ContainerOperation! with { Resolution = ContainerOperationResolution.Unresolved });
        view.Dispose(); view.Attach(); browser.PublishOperation(browser.ContainerOperation! with { Resolution = ContainerOperationResolution.ObservedRequestedState });
        await clock.Settle(view); Check(view.OperationMessage.Contains("Observed requested state"), "Latest notification wins across reentry");
    }
}
