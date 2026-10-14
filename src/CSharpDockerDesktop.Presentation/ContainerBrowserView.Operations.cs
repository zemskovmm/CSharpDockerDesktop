using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>A single-use, immutable choice. Reference identity prevents obsolete dialog completion.</summary>
public sealed record ContainerDeleteConfirmation(ContainerOperationRequest Request, string Name)
{
    public string Action => Request.ForceDelete ? "Force Delete" : "Delete";
    public string Message => $"{Action} container {Name}\nFull ID: {Request.Target.Id}\nConnection generation: {Request.Target.ConnectionGeneration}\n\n"
        + (Request.ForceDelete ? "Dangerous: force removal can terminate a running container. " : "Ordinary deletion will not stop a running container. ")
        + "Associated volumes are preserved (v=false). "
        + $"Force is {(Request.ForceDelete ? "true" : "false")}. This action cannot be undone.";
}

public sealed partial class ContainerBrowserView
{
    private readonly IContainerOperations? operations = browser as IContainerOperations;
    private bool submitting;
    public ContainerDeleteConfirmation? DeleteConfirmation { get; private set; }
    public ContainerOperationSnapshot? Operation { get; private set; }
    public string AdmissionMessage { get; private set; } = "";
    public bool ShowingOperationDetails { get; private set; }
    private ContainerSummary? Selected => Snapshot.Containers.FirstOrDefault(row => row.Id == SelectedId);
    private bool CanAct => active && operations is not null && !submitting && operations.ContainerOperation?.Pending != true
        && Snapshot.State == ResourceReadState.Ready && Selected is not null && DeleteConfirmation is null;
    public bool CanStart => CanAct && Selected?.State is "created" or "exited";
    public bool CanStop => CanAct && Selected?.State == "running";
    public bool CanRestart => CanStop;
    public bool CanDelete => CanAct && Selected?.State is "created" or "exited" or "dead";
    public bool CanForceDelete => CanAct && Selected?.State is "running" or "paused" or "restarting";
    public string ActionMessage => Selected is not { } row ? "Select a container for lifecycle actions."
        : $"Selected: {ContainerDisplay.Collection(row.Names, name => name.TrimStart('/'))} ({ContainerDisplay.ShortId(row.Id)}), state {ContainerDisplay.Text(row.State)}. "
            + (row.State == "running" ? "Ordinary deletion requires a separate Stop first, or choose dangerous Force Delete. "
                : row.State is "paused" or "restarting" ? "Ordinary Delete is unavailable in this state; dangerous Force Delete requires explicit confirmation. " : "")
            + (row.State == "paused" ? "Pause/Unpause and lifecycle transitions for paused containers are unavailable. "
                : row.State == "restarting" ? "Start/Stop/Restart are unavailable while restarting. "
                : row.State is not ("running" or "created" or "exited" or "dead") ? "Lifecycle actions are unavailable for this unknown state. " : "")
            + (operations?.ContainerOperation?.Pending == true || submitting ? "Another operation is pending; no actions are queued." : "The Engine decides whether an action is allowed.");
    public string OperationMessage => Operation is { } operation ? ContainerOperationDisplay.Status(operation) : "No container operation in this session.";
    public bool HasOperationDetails => Operation?.CommandOutcome?.DaemonMessage is not null || Operation?.ObservationFailure?.DaemonMessage is not null;
    public string OperationDetails => ShowingOperationDetails && Operation is { } operation
        ? $"Original command daemon content:\n{operation.CommandOutcome?.DaemonMessage ?? "none"}\nOriginal observation daemon content:\n{operation.ObservationFailure?.DaemonMessage ?? "none"}" : "";

    private void SyncOperation()
    {
        var latest = operations?.ContainerOperation;
        if (Operation?.OperationId != latest?.OperationId || Operation?.Request != latest?.Request) ShowingOperationDetails = false;
        Operation = latest;
    }
    public void ShowOperationDetails(bool show)
    {
        if (!active) return;
        ShowingOperationDetails = show && HasOperationDetails;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public ContainerDeleteConfirmation? BeginDelete(bool force)
    {
        // Read the current Application snapshot before capturing, even if a notification is queued.
        if (!active) return null;
        ApplyLatest();
        if (!(force ? CanForceDelete : CanDelete) || Selected is not { } row) return null;
        DeleteConfirmation = new(new(new(Snapshot.ConnectionGeneration, row.Id), ContainerCommand.Delete, force),
            ContainerDisplay.Collection(row.Names, name => name.TrimStart('/')));
        Changed?.Invoke(this, EventArgs.Empty);
        return DeleteConfirmation;
    }
    private bool ConfirmationCurrent(ContainerDeleteConfirmation confirmation)
    {
        var latest = browser.ContainerSnapshot;
        var target = confirmation.Request.Target;
        var row = latest.Containers.FirstOrDefault(row => row.Id == target.Id);
        return active && SelectedId == target.Id && latest.ConnectionGeneration == target.ConnectionGeneration
            && latest.State == ResourceReadState.Ready && row is not null
            && (confirmation.Request.ForceDelete ? row.State is "running" or "paused" or "restarting" : row.State is "created" or "exited" or "dead");
    }
    public void CancelDelete(ContainerDeleteConfirmation confirmation)
    {
        if (!ReferenceEquals(DeleteConfirmation, confirmation)) return;
        DeleteConfirmation = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void ConfirmDelete(ContainerDeleteConfirmation confirmation)
    {
        if (!ReferenceEquals(DeleteConfirmation, confirmation)) return;
        bool current = ConfirmationCurrent(confirmation);
        DeleteConfirmation = null; // Consume before any reentrant coordinator notification.
        if (current && !submitting && operations?.ContainerOperation?.Pending != true) Submit(confirmation.Request);
        else { AdmissionMessage = "Delete not dispatched: the captured selection or connection is no longer current, or another operation is pending."; Changed?.Invoke(this, EventArgs.Empty); }
    }
    public void Execute(ContainerCommand command)
    {
        if (!active) return;
        ApplyLatest();
        if (!(command switch { ContainerCommand.Start => CanStart, ContainerCommand.Stop => CanStop, ContainerCommand.Restart => CanRestart, _ => false })) return;
        Submit(new(new(Snapshot.ConnectionGeneration, SelectedId!), command));
    }
    private void Submit(ContainerOperationRequest request)
    {
        submitting = true;
        AdmissionMessage = "";
        int lifetime = epoch;
        // No page cancellation token: accepted work belongs to the session and survives navigation.
        Task<ContainerOperationSnapshot> completion;
        try { completion = operations!.ExecuteContainerOperationAsync(request); }
        catch (Exception) { submitting = false; AdmissionMessage = "Unresolved operation outcome. Check authoritative Engine state before another deliberate action. No automatic mutation retry will occur."; Changed?.Invoke(this, EventArgs.Empty); return; }
        SyncOperation();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = CompleteSubmission(completion, lifetime);
    }
    private async Task CompleteSubmission(Task<ContainerOperationSnapshot> completion, int lifetime)
    {
        ContainerOperationSnapshot? result = null;
        try { result = await completion.ConfigureAwait(false); }
        catch (Exception) { /* Fixed display below; never log sensitive exception content. */ }
        dispatch(() =>
        {
            if (!active || lifetime != epoch) return;
            submitting = false;
            SyncOperation();
            if (result is null) AdmissionMessage = "Unresolved operation outcome. Check authoritative Engine state before another deliberate action. No automatic mutation retry will occur.";
            else if (result.OperationId == 0) AdmissionMessage = ContainerOperationDisplay.Status(result);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}

public static class ContainerOperationDisplay
{
    public static string Status(ContainerOperationSnapshot operation)
    {
        string action = operation.Request.ForceDelete ? "Force Delete" : operation.Request.Command.ToString();
        string phase = operation.Phase switch
        {
            ContainerOperationPhase.CommandPending => "Command pending. The final state is not established.",
            ContainerOperationPhase.Reconciling => "Reconciling authoritative Engine state. An acknowledgement alone does not establish success.",
            _ => operation.Resolution switch
            {
                ContainerOperationResolution.ObservedRequestedState => "Observed requested state. This describes the Engine observation, not command causation.",
                ContainerOperationResolution.ObservedDifferentState => "Observed different state. The requested state was not observed.",
                ContainerOperationResolution.NotDispatched => "Not dispatched. No command was sent.",
                _ => "Unresolved outcome. The command may have taken effect; no automatic mutation retry will occur."
            }
        };
        var outcome = operation.CommandOutcome;
        string command = outcome is null ? "" : $"\nCommand: {outcome.Disposition}{(outcome.HttpStatusCode is { } http ? $" (HTTP {http})" : "")}."
            + (outcome.Failure is { } failure ? $" {failure.Kind}: {failure.Description}" : "")
            + (outcome.ErrorBodyUnavailable ? " Original daemon error body unavailable." : "");
        string observation = operation.ObservedExists == false ? "\nObserved: container absent."
            : operation.ObservedState is { } state ? $"\nObserved state: {ContainerDisplay.Text(state.Status)}; running: {ContainerDisplay.Flag(state.Running)}; paused: {ContainerDisplay.Flag(state.Paused)}; restarting: {ContainerDisplay.Flag(state.Restarting)}; dead: {ContainerDisplay.Flag(state.Dead)}." : "";
        if (operation.ObservationFailure is { } error) observation += $"\nObservation failure: {ContainerDisplay.Failure(error)}";
        if (operation.ObservedConnectionGeneration is { } generation) observation += $" Observation connection generation: {generation}.";
        return $"{action} — full ID {operation.Request.Target.Id}, connection generation {operation.Request.Target.ConnectionGeneration}\n{phase}{command}{observation}";
    }
}
