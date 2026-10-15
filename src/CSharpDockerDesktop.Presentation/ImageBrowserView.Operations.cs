using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>An immutable choice consumed by reference identity before submission.</summary>
public sealed record ImageDeleteConfirmation(ImageOperationRequest Request, string Tags)
{
    public string Message => $"Delete image · non-force (force=false)\nFull ID: {Request.Target.Id}\nTags/names: {Tags}\nConnection generation: {Request.Target.ConnectionGeneration}\n\n"
        + "Deletion cannot be undone. Dependencies, including containers using this image, may prevent deletion. "
        + "The client will not remove dependent containers or escalate to force. The daemon makes the final dependency decision.";
}

public sealed partial class ImageBrowserView
{
    private readonly IImageOperations? operations = browser as IImageOperations;
    private bool submitting;
    public ImageDeleteConfirmation? DeleteConfirmation { get; private set; }
    public ImageOperationSnapshot? Operation { get; private set; }
    public string AdmissionMessage { get; private set; } = "";
    public bool ShowingOperationDetails { get; private set; }
    public bool CanDelete => operations is not null && !submitting && DeleteConfirmation is null
        && operations.ImageOperation?.Pending != true && browser.ImageSnapshot.State == ResourceReadState.Ready
        && CurrentIdentity(browser.ImageSnapshot);
    public string OperationMessage => Operation is { } operation ? ImageOperationDisplay.Status(operation, Snapshot.State == ResourceReadState.Unavailable ? -1 : Snapshot.ConnectionGeneration) : "No image deletion in this session.";
    public bool HasOperationDetails => Operation?.CommandOutcome?.DaemonMessage is not null
        || Operation?.CommandOutcome?.RemovalRecords is { Count: > 0 } || Operation?.ObservationFailure?.DaemonMessage is not null;
    public string OperationDetails => ShowingOperationDetails && Operation is { } operation ? ImageOperationDisplay.SensitiveDetails(operation) : "";

    private void SyncOperation()
    {
        var latest = operations?.ImageOperation;
        if (Operation?.OperationId != latest?.OperationId || Operation?.Request != latest?.Request) ShowingOperationDetails = false;
        Operation = latest;
    }
    public void ShowOperationDetails(bool show)
    {
        if (!active) return;
        ShowingOperationDetails = show && HasOperationDetails;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public ImageDeleteConfirmation? BeginDelete()
    {
        // Guard against a generation/selection change before a queued UI notification drains.
        if (!CanDelete) return null;
        ApplyLatest();
        if (!CanDelete) return null;
        var latest = browser.ImageSnapshot;
        if (!CurrentIdentity(latest) || latest.State != ResourceReadState.Ready) return null;
        var row = latest.Images.First(image => image.Id == SelectedId);
        DeleteConfirmation = new(new(new(latest.ConnectionGeneration, row.Id)), ImageOperationDisplay.Tags(row.RepoTags));
        AdmissionMessage = "";
        Changed?.Invoke(this, EventArgs.Empty);
        return DeleteConfirmation;
    }
    private bool ConfirmationCurrent(ImageDeleteConfirmation confirmation)
    {
        var latest = browser.ImageSnapshot;
        var target = confirmation.Request.Target;
        return active && SelectedId == target.Id && latest.ConnectionGeneration == target.ConnectionGeneration
            && latest.State == ResourceReadState.Ready && latest.Images.Any(row => row.Id == target.Id)
            && (!DetailOpen || latest.SelectedImageId == target.Id);
    }
    private void RejectConfirmation()
    {
        DeleteConfirmation = null;
        AdmissionMessage = "Delete not dispatched: the captured selection or connection is no longer current, or another operation is pending.";
    }
    public void CancelDelete(ImageDeleteConfirmation confirmation)
    {
        if (!active || !ReferenceEquals(DeleteConfirmation, confirmation)) return;
        DeleteConfirmation = null; Changed?.Invoke(this, EventArgs.Empty);
    }
    public void ConfirmDelete(ImageDeleteConfirmation confirmation)
    {
        if (!active || !ReferenceEquals(DeleteConfirmation, confirmation)) return;
        bool current = ConfirmationCurrent(confirmation);
        DeleteConfirmation = null; // Consume before reentrant Application notifications or dialog completion.
        if (current && !submitting && operations is not null && operations.ImageOperation?.Pending != true) Submit(confirmation.Request);
        else { RejectConfirmation(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private void Submit(ImageOperationRequest request)
    {
        submitting = true; AdmissionMessage = "";
        int lifetime = epoch;
        Task<ImageOperationSnapshot> completion;
        // Accepted command and reconciliation belong to the session, never to the page lifetime.
        try { completion = operations!.ExecuteImageOperationAsync(request); }
        catch (Exception) { submitting = false; AdmissionMessage = ImageOperationDisplay.Unresolved; Changed?.Invoke(this, EventArgs.Empty); return; }
        if (active && lifetime == epoch) { SyncOperation(); Changed?.Invoke(this, EventArgs.Empty); }
        _ = CompleteSubmission(completion, lifetime);
    }
    private async Task CompleteSubmission(Task<ImageOperationSnapshot> completion, int lifetime)
    {
        ImageOperationSnapshot? result = null;
        try { result = await completion.ConfigureAwait(false); }
        catch (Exception) { /* Never log sensitive exception content. */ }
        dispatch(() =>
        {
            if (!active || lifetime != epoch) return;
            submitting = false; SyncOperation();
            // Admission rejection does not replace useful retained session evidence.
            if (result is null) AdmissionMessage = ImageOperationDisplay.Unresolved;
            else if (result.OperationId == 0) AdmissionMessage = ImageOperationDisplay.Status(result);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}

public static class ImageOperationDisplay
{
    public const string Unresolved = "Unresolved outcome. The command may have taken effect. Unresolved mutations are never automatically replayed; check authoritative Engine state before another deliberate action.";
    public static string Status(ImageOperationSnapshot operation, long? currentGeneration = null)
    {
        string phase = operation.Phase switch
        {
            ImageOperationPhase.CommandPending => "Command pending. Image absence has not been established.",
            ImageOperationPhase.Reconciling => "Reconciling authoritative Engine state. Command acknowledgement/removal records do not prove absence.",
            _ => operation.Resolution switch
            {
                ImageOperationResolution.ObservedAbsent => "Observed absent. Authoritative observation establishes absence, not which action caused it.",
                ImageOperationResolution.ObservedPresent => "Observed present. The image remains on the Engine, including after a command acknowledgement.",
                ImageOperationResolution.NotDispatched => "Not dispatched. No delete command was sent.",
                _ => Unresolved
            }
        };
        var outcome = operation.CommandOutcome;
        string command = outcome is null ? "" : $"\nCommand: {outcome.Disposition}{(outcome.HttpStatusCode is { } http ? $" (HTTP {http})" : "")}."
            + (outcome.Failure is { } failure ? $" {failure.Kind}: {failure.Description}" : "")
            + (outcome.Failure?.Kind == ImageCommandFailureKind.Conflict ? " Dependencies may prevent non-force deletion. Review the current dependencies; the client will not remove containers or force deletion. Do not blindly retry." : "")
            + (outcome.RemovalRecords is { } records ? $" Removal response records: {records.Count}; these are command evidence, not authoritative absence." : "")
            + (outcome.ResponseBodyUnavailable ? " Original response body unavailable." : "");
        string observation = operation.ObservedExists is { } exists ? $"\nObserved: image {(exists ? "present" : "absent")}." : "";
        if (operation.ObservationFailure is { } error) observation += $"\nObservation failure: {ContainerDisplay.Failure(error)}";
        if (operation.ObservedConnectionGeneration is { } generation) observation += $" Observation connection generation: {generation}.";
        return $"Non-force Delete — full ID {operation.Request.Target.Id}, connection generation {operation.Request.Target.ConnectionGeneration}\n{OperationProvenanceDisplay.Describe(operation.Endpoint, operation.EngineId, operation.Request.Target.ConnectionGeneration, currentGeneration)}\n{phase}{command}{observation}";
    }
    private const int MaximumCharacters = 16384;
    private const int MaximumRecords = 128;
    public static string Tags(IReadOnlyList<string>? tags)
    {
        if (tags is null) return "unknown";
        if (tags.Count == 0) return "none";
        var text = new StringBuilder();
        for (int i = 0; i < Math.Min(tags.Count, MaximumRecords) && text.Length < MaximumCharacters; ++i)
        { if (i != 0) text.Append(", "); AppendBounded(text, tags[i]); }
        if (tags.Count > MaximumRecords || text.Length >= MaximumCharacters) text.Append("\n[Display truncated; full ID above remains the target.]");
        return text.ToString();
    }
    private static void AppendBounded(StringBuilder text, string? value)
    {
        value ??= "none";
        int available = Math.Max(0, MaximumCharacters - text.Length);
        text.Append(value.AsSpan(0, Math.Min(value.Length, available)));
    }
    public static string SensitiveDetails(ImageOperationSnapshot operation)
    {
        var text = new StringBuilder();
        AppendBounded(text, "Original command daemon message:\n"); AppendBounded(text, operation.CommandOutcome?.DaemonMessage);
        AppendBounded(text, "\nOriginal observation daemon message:\n"); AppendBounded(text, operation.ObservationFailure?.DaemonMessage);
        AppendBounded(text, "\nRemoval response evidence (not proof of absence):\n");
        var records = operation.CommandOutcome?.RemovalRecords;
        if (records is null || records.Count == 0) AppendBounded(text, "none");
        else for (int i = 0; i < Math.Min(records.Count, MaximumRecords) && text.Length < MaximumCharacters; ++i)
        {
            AppendBounded(text, "Untagged: "); AppendBounded(text, records[i].Untagged);
            AppendBounded(text, " · Deleted: "); AppendBounded(text, records[i].Deleted); AppendBounded(text, "\n");
        }
        if (text.Length >= MaximumCharacters || records?.Count > MaximumRecords) text.Append("\n[Display truncated; retained Application evidence is unchanged.]");
        return text.ToString();
    }
}
