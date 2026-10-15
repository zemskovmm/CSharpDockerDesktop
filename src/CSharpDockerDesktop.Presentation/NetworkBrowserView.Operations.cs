using System.Collections;
using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>One presentation lifetime; its target never follows selection.</summary>
public sealed class NetworkDeletePresentation(NetworkDeletionTarget target)
{
    public NetworkDeletionTarget Target { get; } = target;
    internal CancellationTokenSource Cancellation { get; } = new();
    internal Task<NetworkDeletionPreparationSnapshot>? Work;
    internal NetworkDeletionPreparationSnapshot? Snapshot;
    internal NetworkDeleteConfirmation? Confirmation;
    internal bool Closed, Invalidated, Starting = true;
    internal NetworkPreparationIdentity? CompletedIdentity;
}

/// <summary>The exact completed revision shown to the user, consumed once by reference identity.</summary>
public sealed record NetworkDeleteConfirmation(NetworkOperationRequest Request);

public sealed partial class NetworkBrowserView
{
    private readonly INetworkDeletionPreparation? preparationService = browser as INetworkDeletionPreparation;
    private readonly INetworkOperations? operations = browser as INetworkOperations;
    private bool submitting;
    private NetworkOperationSnapshot? operation;
    private NetworkAttachmentRows? preparationRows, operationRows, operationPreparationRows;
    private readonly Dictionary<NetworkInspection, Task<NetworkAttachmentRows>> attachmentIndexes = new(ReferenceEqualityComparer.Instance);
    private long operationEvidenceRevision, dependencyCopyRevision;
    private CancellationTokenSource? dependencyCopyWork;
    private CancellationTokenSource? operationEvidenceWork;
    private string operationEvidence = "";
    private NetworkDeletionPreparationSnapshot? revealedPreparation;
    public NetworkDeletePresentation? Deletion { get; private set; }
    public Task PreparationCleanup { get; private set; } = Task.CompletedTask;
    public string AdmissionMessage { get; private set; } = "";
    public NetworkOperationSnapshot? Operation => active ? operations?.NetworkOperation : null;
    public bool ShowingOperationDetails { get; private set; }
    public bool PreparingOperationDetails => operationEvidenceWork is not null;
    public bool CanDelete => preparationService is not null && operations is not null && !submitting
        && Deletion is null && operations.NetworkOperation?.Pending != true && CurrentIdentity(browser.NetworkSnapshot) && !KnownBuiltIn;
    public const string DeletionWarning = "Ordinary network deletion only. Attached containers and endpoints will not be removed. Built-in bridge, host and none networks cannot be deleted.";
    public const string PrecheckExplanation = "Fresh inspected attachment evidence is a precheck only. It does not establish cluster-wide completeness or remove the check/delete race. The daemon makes the final decision. A conflict requires another explicit precheck and confirmation.";
    private bool KnownBuiltIn => browser.NetworkSnapshot.Networks.FirstOrDefault(n => StringComparer.Ordinal.Equals(n.Id, SelectedId))?.Name is "bridge" or "host" or "none"
        || CurrentInspection(browser.NetworkSnapshot) && browser.NetworkSnapshot.Inspection?.Name is "bridge" or "host" or "none";
    private bool TargetCurrent(NetworkDeletePresentation presentation)
    {
        var latest = browser.NetworkSnapshot;
        return active && !presentation.Closed && ReferenceEquals(Deletion, presentation)
            && CurrentIdentity(latest) && latest.ConnectionGeneration == presentation.Target.ConnectionGeneration
            && StringComparer.Ordinal.Equals(SelectedId, presentation.Target.Id) && !KnownBuiltIn;
    }
    private NetworkDeletionPreparationSnapshot? LivePreparation
    {
        get
        {
            if (Deletion is not { } presentation || !TargetCurrent(presentation) || presentation.Starting) return null;
            var latest = preparationService?.NetworkDeletionPreparation;
            return latest is not null && latest.Identity.PreparationId == presentation.Snapshot?.Identity.PreparationId
                && latest.Target == presentation.Target ? latest : presentation.Snapshot?.Identity.PreparationId == 0 ? presentation.Snapshot : null;
        }
    }
    public NetworkDeleteConfirmation? DeleteConfirmation => Deletion?.Confirmation is { } confirmation
        && LivePreparation is { Phase: NetworkPreparationPhase.Completed } latest && latest.Identity == confirmation.Request.Preparation
        && latest.Target == confirmation.Request.Target && latest.IsPrecheckEligible
        && preparationService!.IsNetworkDeletionPreparationCurrent(confirmation.Request.Preparation)
        && Deletion?.Invalidated == false && !submitting && operations?.NetworkOperation?.Pending != true ? confirmation : null;
    public string PreparationMessage => LivePreparation is { } snapshot ? NetworkOperationDisplay.Preparation(snapshot)
        : Deletion is null ? "" : "Preparation unavailable or invalidated. Cancel, then choose Delete for a fresh explicit precheck.";
    public IReadOnlyList<NetworkAttachmentRow> PreparationAttachments => RowsFor(LivePreparation?.Inspection, ref preparationRows);
    public IReadOnlyList<NetworkAttachmentRow> OperationPreparationAttachments => RowsFor(ShowingOperationDetails && ReferenceEquals(operation, operations?.NetworkOperation) ? Operation?.Preparation?.Inspection : null, ref operationPreparationRows);
    public IReadOnlyList<NetworkAttachmentRow> RefreshedAttachments => RowsFor(ShowingOperationDetails && ReferenceEquals(operation, operations?.NetworkOperation) ? Operation?.RefreshedInspection : null, ref operationRows);
    private IReadOnlyList<NetworkAttachmentRow> RowsFor(NetworkInspection? source, ref NetworkAttachmentRows? rows)
    {
        if (source?.Containers is null) return Array.Empty<NetworkAttachmentRow>();
        if (ReferenceEquals(rows?.Source, source)) return rows;
        if (!attachmentIndexes.TryGetValue(source, out var work))
        {
            // Dictionary indexing is prepared off-thread; no per-row linear scans or eager text layouts.
            int lifetime = epoch;
            work = Task.Run(() => new NetworkAttachmentRows(source)); attachmentIndexes[source] = work;
            _ = work.ContinueWith(_ => dispatch(() =>
            {
                if (active && epoch == lifetime && (ReferenceEquals(source, LivePreparation?.Inspection)
                    || ShowingOperationDetails && ReferenceEquals(operation, operations?.NetworkOperation)
                        && (ReferenceEquals(source, operation?.Preparation?.Inspection) || ReferenceEquals(source, operation?.RefreshedInspection))))
                    Changed?.Invoke(this, EventArgs.Empty);
            }), TaskScheduler.Default);
        }
        if (!work.IsCompletedSuccessfully) return Array.Empty<NetworkAttachmentRow>();
        rows = work.Result; return rows;
    }

    public NetworkDeletePresentation? BeginDelete()
    {
        if (!CanDelete) return null;
        ApplyLatest(); if (!CanDelete) return null;
        var presentation = new NetworkDeletePresentation(new(Snapshot.ConnectionGeneration, SelectedId!));
        Deletion = presentation; AdmissionMessage = "";
        var previous = preparationService!.NetworkDeletionPreparation;
        try
        {
            presentation.Work = preparationService.PrepareNetworkDeletionAsync(presentation.Target, presentation.Cancellation.Token);
            var latest = preparationService.NetworkDeletionPreparation;
            if (!ReferenceEquals(previous, latest) && latest?.Target == presentation.Target) presentation.Snapshot = latest;
        }
        catch (Exception)
        {
            // Exception text can contain private daemon data.
            presentation.Snapshot = new(default, presentation.Target, NetworkPreparationPhase.Failed, NetworkDeletionEligibility.Unknown, [],
                Failure: new(NetworkPreparationFailureKind.AcquisitionFailed));
        }
        presentation.Starting = false;
        SyncDeletion(); Changed?.Invoke(this, EventArgs.Empty);
        if (presentation.Work is { } work) _ = CompletePreparation(presentation, work, epoch);
        return presentation;
    }
    private async Task CompletePreparation(NetworkDeletePresentation presentation, Task<NetworkDeletionPreparationSnapshot> work, int lifetime)
    {
        NetworkDeletionPreparationSnapshot? result = null;
        try { result = await work.ConfigureAwait(false); } catch (Exception) { }
        dispatch(() =>
        {
            if (!active || epoch != lifetime || !ReferenceEquals(Deletion, presentation) || presentation.Closed) return;
            if (result?.Target == presentation.Target) presentation.Snapshot = result;
            else presentation.Snapshot = new(default, presentation.Target, NetworkPreparationPhase.Failed, NetworkDeletionEligibility.Unknown, [],
                Failure: new(NetworkPreparationFailureKind.AcquisitionFailed));
            SyncDeletion(); Changed?.Invoke(this, EventArgs.Empty);
        });
    }
    private void SyncDeletion()
    {
        if (Deletion is not { } presentation || presentation.Starting) return;
        if (!TargetCurrent(presentation)) { CloseDeletion(); return; }
        var latest = LivePreparation;
        if (latest is null) { presentation.Invalidated = true; presentation.Confirmation = null; return; }
        presentation.Snapshot = latest;
        if (!ReferenceEquals(revealedPreparation, latest)) revealedPreparation = null;
        // The first completed revision is the only revision this presentation may confirm,
        // including when its evidence is blocked or unavailable.
        if (latest.Phase == NetworkPreparationPhase.Completed && presentation.CompletedIdentity is null)
        {
            presentation.CompletedIdentity = latest.Identity;
            if (!presentation.Invalidated && latest.IsPrecheckEligible && preparationService!.IsNetworkDeletionPreparationCurrent(latest.Identity))
                presentation.Confirmation = new(new(presentation.Target, latest.Identity));
        }
        if (latest.Phase is NetworkPreparationPhase.Invalidated or NetworkPreparationPhase.Consumed or NetworkPreparationPhase.Failed
            || presentation.CompletedIdentity is { } completed && latest.Identity != completed)
            presentation.Invalidated = true;
        if (presentation.Invalidated || !latest.IsPrecheckEligible || !preparationService!.IsNetworkDeletionPreparationCurrent(latest.Identity))
            presentation.Confirmation = null;
    }
    public bool CopyDeletionTarget(NetworkDeletePresentation presentation, Action<string> publish)
    {
        if (!TargetCurrent(presentation)) return false;
        try { publish(presentation.Target.Id); return true; } catch (Exception) { return false; }
    }
    public bool HasPreparationDetails => LivePreparation?.Failure?.ResourceFailure is not null;
    public bool ShowingPreparationDetails => revealedPreparation is not null && ReferenceEquals(revealedPreparation, LivePreparation);
    public string PreparationDetails => ShowingPreparationDetails && revealedPreparation?.Failure?.ResourceFailure is { } failure
        ? $"Original preparation failure description:\n{failure.Description}\nOriginal daemon message ({failure.DaemonMessage?.Length.ToString() ?? "null"} characters):\n{failure.DaemonMessage}" : "";
    public void ShowPreparationDetails(bool show)
    { if (!active) return; revealedPreparation = show && HasPreparationDetails ? LivePreparation : null; Changed?.Invoke(this, EventArgs.Empty); }
    public void CancelDelete(NetworkDeletePresentation presentation)
    {
        if (!ReferenceEquals(Deletion, presentation)) return;
        CloseDeletion(); if (active) Changed?.Invoke(this, EventArgs.Empty);
    }
    private void CloseDeletion()
    {
        if (Deletion is not { } presentation) return;
        CancelDependencyCopy();
        Deletion = null; preparationRows = null; PruneAttachmentIndexes(); revealedPreparation = null; presentation.Closed = true; presentation.Confirmation = null;
        presentation.Cancellation.Cancel();
        // Close fences the matching ID synchronously. Its async join never blocks navigation.
        var cleanup = CleanupPreparation(presentation);
        if (!cleanup.IsCompleted) PreparationCleanup = PreparationCleanup.IsCompleted ? cleanup : Task.WhenAll(PreparationCleanup, cleanup);
    }
    private async Task CleanupPreparation(NetworkDeletePresentation presentation)
    {
        Task close = Task.CompletedTask;
        long closedId = 0;
        try
        {
            if (presentation.Snapshot is { Identity.PreparationId: > 0 } snapshot)
            { close = preparationService!.CloseNetworkDeletionPreparationAsync(snapshot.Identity); closedId = snapshot.Identity.PreparationId; }
        }
        catch (Exception) { }
        try
        {
            if (presentation.Work is { } work)
            {
                var result = await work.ConfigureAwait(false);
                if (closedId == 0 && result.Target == presentation.Target && result.Identity.PreparationId > 0)
                    await preparationService!.CloseNetworkDeletionPreparationAsync(result.Identity).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* Never log private daemon evidence. */ }
        try { await close.ConfigureAwait(false); } catch (Exception) { }
        finally { presentation.Cancellation.Dispose(); }
    }

    public void ConfirmDelete(NetworkDeletePresentation presentation, NetworkDeleteConfirmation confirmation)
    {
        if (!active || !ReferenceEquals(Deletion, presentation) || !ReferenceEquals(presentation.Confirmation, confirmation)) return;
        bool current = ReferenceEquals(DeleteConfirmation, confirmation) && TargetCurrent(presentation);
        presentation.Confirmation = null; // Consume before reentrant Application notifications.
        if (!current)
        {
            AdmissionMessage = "Delete not dispatched: preparation, target or connection is no longer current, or another operation is pending. Start a fresh explicit precheck.";
            CloseDeletion(); Changed?.Invoke(this, EventArgs.Empty); return;
        }
        submitting = true; AdmissionMessage = "";
        Task<NetworkOperationSnapshot>? completion = null;
        int lifetime = epoch;
        // Admission atomically consumes preparation; only then close its presentation.
        // Accepted mutation and reconciliation use session ownership, never page cancellation.
        try { completion = operations!.ExecuteNetworkOperationAsync(confirmation.Request); }
        catch (Exception) { submitting = false; AdmissionMessage = NetworkOperationDisplay.Unresolved; }
        CloseDeletion(); SyncOperation(); Changed?.Invoke(this, EventArgs.Empty);
        if (completion is not null) _ = CompleteSubmission(completion, lifetime);
    }
    private async Task CompleteSubmission(Task<NetworkOperationSnapshot> completion, int lifetime)
    {
        NetworkOperationSnapshot? result = null;
        try { result = await completion.ConfigureAwait(false); } catch (Exception) { }
        dispatch(() =>
        {
            if (!active || epoch != lifetime) return;
            submitting = false; SyncOperation();
            if (result is null) AdmissionMessage = NetworkOperationDisplay.Unresolved;
            else if (result.OperationId == 0) AdmissionMessage = NetworkOperationDisplay.Status(result);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
    public string OperationMessage => Operation is { } latest ? NetworkOperationDisplay.Status(latest, Snapshot.State == ResourceReadState.Unavailable ? -1 : Snapshot.ConnectionGeneration) : "No network deletion in this session.";
    public bool HasOperationDetails => Operation is { } latest && (latest.CommandOutcome is not null || latest.Preparation is not null
        || latest.ObservationFailure is not null || latest.ObservationListFailure is not null || latest.AttachmentFailure is not null || latest.ObservedListing is not null);
    public string OperationDetails => ShowingOperationDetails && active && ReferenceEquals(operation, operations?.NetworkOperation) ? operationEvidence : "";
    private void SyncOperation()
    {
        var latest = operations?.NetworkOperation;
        if (!ReferenceEquals(operation, latest)) { HideOperationDetails(); operationRows = operationPreparationRows = null; PruneAttachmentIndexes(); }
        operation = latest;
    }
    public void ShowOperationDetails(bool show)
    {
        if (!active) return;
        HideOperationDetails(); SyncOperation();
        if (!show || !HasOperationDetails || operation is not { } captured) { Changed?.Invoke(this, EventArgs.Empty); return; }
        ShowingOperationDetails = true;
        var cancel = operationEvidenceWork = new(); var token = cancel.Token;
        long revision = operationEvidenceRevision; int lifetime = epoch;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() =>
        {
            try
            {
                var text = NetworkOperationDisplay.SensitiveDetails(captured, token);
                dispatch(() =>
                {
                    if (!active || epoch != lifetime || revision != operationEvidenceRevision || token.IsCancellationRequested
                        || !ReferenceEquals(captured, operations?.NetworkOperation)) return;
                    operationEvidence = text; operationEvidenceWork?.Dispose(); operationEvidenceWork = null; Changed?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) { }
        });
    }
    private void HideOperationDetails()
    {
        CancelDependencyCopy();
        ++operationEvidenceRevision; operationEvidenceWork?.Cancel(); operationEvidenceWork?.Dispose(); operationEvidenceWork = null;
        ShowingOperationDetails = false; operationEvidence = "";
    }
    private void PruneAttachmentIndexes()
    {
        var current = operations?.NetworkOperation;
        foreach (var source in attachmentIndexes.Keys.ToArray())
            if (!ReferenceEquals(source, LivePreparation?.Inspection) && !ReferenceEquals(source, current?.Preparation?.Inspection)
                && !ReferenceEquals(source, current?.RefreshedInspection)) attachmentIndexes.Remove(source);
    }
    private void CancelDependencyCopy()
    { ++dependencyCopyRevision; dependencyCopyWork?.Cancel(); dependencyCopyWork?.Dispose(); dependencyCopyWork = null; }
    public async Task<bool> CopyAttachmentAsync(NetworkAttachmentRow row, bool operationEvidence, Action<string> publish)
    {
        if (!active) return false;
        CancelDependencyCopy();
        var cancel = dependencyCopyWork = new(); var token = cancel.Token; long copyRevision = dependencyCopyRevision;
        var presentation = Deletion; var capturedPreparation = LivePreparation; var capturedOperation = Operation; int lifetime = epoch; long revealRevision = operationEvidenceRevision;
        bool Current() => active && lifetime == epoch && copyRevision == dependencyCopyRevision && !token.IsCancellationRequested && (operationEvidence
            ? ShowingOperationDetails && revealRevision == operationEvidenceRevision && ReferenceEquals(capturedOperation, operations?.NetworkOperation)
                && (ReferenceEquals(row.Source, capturedOperation?.RefreshedInspection) || ReferenceEquals(row.Source, capturedOperation?.Preparation?.Inspection))
            : ReferenceEquals(presentation, Deletion) && presentation is not null && TargetCurrent(presentation)
                && ReferenceEquals(capturedPreparation, LivePreparation) && ReferenceEquals(row.Source, LivePreparation?.Inspection));
        if (!Current()) { CancelDependencyCopy(); return false; }
        string text;
        try { text = await Task.Run(() => row.FullText(token), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => done.TrySetResult(false));
        if (!dispatch(() =>
        {
            if (!Current()) { done.TrySetResult(false); return; }
            try { publish(text); done.TrySetResult(true); } catch (Exception) { done.TrySetResult(false); }
            if (ReferenceEquals(dependencyCopyWork, cancel)) { dependencyCopyWork = null; cancel.Dispose(); }
        })) done.TrySetResult(false);
        return await done.Task.ConfigureAwait(false);
    }

}

/// <summary>Off-thread identity index; row text and visual containers are created only on demand.</summary>
public sealed class NetworkAttachmentRows : IReadOnlyList<NetworkAttachmentRow>
{
    public NetworkInspection Source { get; }
    private readonly KeyValuePair<string, NetworkAttachment>[] entries;
    public NetworkAttachmentRows(NetworkInspection source) { Source = source; entries = source.Containers?.ToArray() ?? []; }
    public int Count => entries.Length;
    public NetworkAttachmentRow this[int index] => new(Source, entries[index].Key, entries[index].Value);
    public IEnumerator<NetworkAttachmentRow> GetEnumerator() { for (int i = 0; i < Count; ++i) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
public sealed record NetworkAttachmentRow(NetworkInspection Source, string Id, NetworkAttachment? Attachment)
{
    public string Text => $"Attachment/container ID: {NetworkOperationDisplay.Preview(Id)}\nName: {NetworkOperationDisplay.Preview(Attachment?.Name)} · endpoint ID: {NetworkOperationDisplay.Preview(Attachment?.EndpointId)}\nMAC: {NetworkOperationDisplay.Preview(Attachment?.MacAddress)} · IPv4: {NetworkOperationDisplay.Preview(Attachment?.IPv4Address)} · IPv6: {NetworkOperationDisplay.Preview(Attachment?.IPv6Address)}\nCopy selected attachment for complete original identities and metadata.";
    public string FullText(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return $"Full attachment/container ID: {Id}\nName: {NetworkDisplay.Text(Attachment?.Name)}\nFull endpoint ID: {NetworkDisplay.Text(Attachment?.EndpointId)}\nMAC: {NetworkDisplay.Text(Attachment?.MacAddress)}\nIPv4: {NetworkDisplay.Text(Attachment?.IPv4Address)}\nIPv6: {NetworkDisplay.Text(Attachment?.IPv6Address)}";
    }
}
public static class NetworkOperationDisplay
{
    public static string Preview(string? value) => value is { Length: > 240 } ? value[..240] + "… (copy for full original value)" : NetworkDisplay.Text(value);
    private static string SafeFailure(EngineResourceFailure failure) => $"{failure.Kind}{(failure.HttpStatusCode is { } http ? $" (HTTP {http})" : "")}. Reveal original evidence for details.";
    public const string Unresolved = "Unresolved outcome. The command may have taken effect. Uncertain mutations are never automatically replayed. Check authoritative Engine state before a new deliberate action.";
    public static string Attachments(NetworkInspection? inspection) => inspection?.Containers switch
    {
        null => "Attachment evidence unavailable (null collection); absence of dependencies is not established.",
        { Count: 0 } => "Authoritative inspected empty attachment collection. This observation does not establish cluster-wide completeness or close the check/delete race.",
        { } attachments => $"Inspected attachments: {attachments.Count}. Attached containers/endpoints block deletion. This is an inspected observation, not cluster-wide completeness. Every returned attachment is accessible below."
    };
    public static string Preparation(NetworkDeletionPreparationSnapshot preparation)
    {
        string phase = preparation.Phase switch
        {
            NetworkPreparationPhase.Loading => "Loading fresh network Inspect and attachment evidence… Deletion is disabled while acquisition is pending.",
            NetworkPreparationPhase.Completed => $"Preparation completed · eligibility: {preparation.Eligibility}. " + Attachments(preparation.Inspection),
            NetworkPreparationPhase.Invalidated => $"Invalidated evidence ({preparation.Failure?.Kind}). Deletion is disabled. Cancel and choose Delete for a fresh explicit precheck. Previously inspected attachments are retained below.",
            NetworkPreparationPhase.Consumed => "Preparation consumed. It cannot authorize another deletion.",
            _ => $"Preparation failed ({preparation.Failure?.Kind}). Deletion is disabled. Cancel and choose Delete for a fresh explicit precheck."
        };
        return phase + $"\nPreparation identity/revision: {preparation.Identity.PreparationId}/{preparation.Identity.Revision}."
            + (preparation.Reasons.Count > 0 ? " Block reasons: " + string.Join(", ", preparation.Reasons) + "." : "")
            + (preparation.Failure?.ResourceFailure is { } failure ? " " + SafeFailure(failure) : "");
    }
    public static string Status(NetworkOperationSnapshot operation, long? currentGeneration = null)
    {
        string phase = operation.Phase switch
        {
            NetworkOperationPhase.CommandPending => "Command pending. Network absence has not been established.",
            NetworkOperationPhase.Reconciling => "Reconciling authoritative Engine state. Command acknowledgement does not establish deletion.",
            _ => operation.Resolution switch
            {
                NetworkOperationResolution.NotDispatched => "Not dispatched. No delete command was sent.",
                NetworkOperationResolution.ObservedAbsent => "Observed absent in authoritative Engine state; this does not establish command causation.",
                NetworkOperationResolution.ObservedPresent => "Observed present in authoritative Engine state. Deletion has not been established.",
                _ => Unresolved
            }
        };
        var text = new StringBuilder($"Ordinary network Delete · ID: {Preview(operation.Request.Target.Id)} · connection generation: {operation.Request.Target.ConnectionGeneration}\n{OperationProvenanceDisplay.Describe(operation.Endpoint, operation.EngineId, operation.Request.Target.ConnectionGeneration, currentGeneration)}\n{phase}");
        if (operation.Rejection is { } rejection) text.Append($"\nAdmission rejection: {rejection}.");
        if (operation.Interruption is { } interruption) text.Append($"\nInterruption: {interruption}. Possible dispatch still requires observation.");
        if (operation.CommandOutcome is { } command)
        {
            text.Append($"\nOriginal command disposition: {command.Disposition}.");
            if (command.HttpStatusCode is { } http) text.Append($" HTTP {http}.");
            if (command.Failure is { } failure) text.Append($" Failure: {failure.Kind}.");
            if (command.ErrorBodyUnavailable) text.Append(" Original error body unavailable.");
            if (command.Failure?.Kind == NetworkCommandFailureKind.Conflict)
                text.Append(" Attachment conflict: review refreshed evidence. Release dependencies deliberately outside this action, then choose Delete for a fresh explicit precheck and confirmation. Refreshed evidence never renews confirmation. No force, dependency removal, or blind retry.");
        }
        if (operation.ObservedExists is { } exists) text.Append($"\nSeparate authoritative observation: ID {(exists ? "present" : "absent")}.");
        if (operation.ObservedConnectionGeneration is { } generation) text.Append($" Observation generation: {generation}.");
        if (operation.ObservationInvalidated) text.Append(" Observation invalidated; current presence remains unestablished.");
        if (operation.ObservationFailure is { } error) text.Append("\nObservation failure: " + SafeFailure(error));
        if (operation.ObservationListFailure is { } listError) text.Append("\nObservation listing failure: " + SafeFailure(listError));
        if (operation.AttachmentObservationState != NetworkAttachmentObservationState.NotRequested)
            text.Append($"\nConflict attachment refresh: {operation.AttachmentObservationState}. ").Append(Attachments(operation.RefreshedInspection));
        if (operation.AttachmentFailure is { } attachmentError) text.Append("\nAttachment refresh failure: " + SafeFailure(attachmentError));
        text.Append("\nAcknowledgement alone is not deletion. Observations describe presence independently of command disposition. Conflict evidence is historical when invalidated and never authorizes another attempt.");
        return text.ToString();
    }
    public static string SensitiveDetails(NetworkOperationSnapshot operation, CancellationToken token = default)
    {
        var text = new StringBuilder("Original private operation evidence · may contain sensitive content\n");
        Original("Exact ordinal network ID", operation.Request.Target.Id);
        text.AppendLine($"Preparation ID/revision: {operation.Request.Preparation.PreparationId}/{operation.Request.Preparation.Revision}");
        if (operation.Preparation is { } preparation) text.AppendLine("Original precheck (historical): " + Preparation(preparation));
        Original("Command daemon message", operation.CommandOutcome?.DaemonMessage);
        Original("Command error body", operation.CommandOutcome?.DaemonErrorBody);
        Original("Command failure description", operation.CommandOutcome?.Failure?.Description);
        Original("Preparation daemon message", operation.Preparation?.Failure?.ResourceFailure?.DaemonMessage);
        Original("Preparation failure description", operation.Preparation?.Failure?.ResourceFailure?.Description);
        Original("Observation daemon message", operation.ObservationFailure?.DaemonMessage);
        Original("Observation failure description", operation.ObservationFailure?.Description);
        Original("Observation listing daemon message", operation.ObservationListFailure?.DaemonMessage);
        Original("Observation listing failure description", operation.ObservationListFailure?.Description);
        Original("Attachment refresh daemon message", operation.AttachmentFailure?.DaemonMessage);
        Original("Attachment refresh failure description", operation.AttachmentFailure?.Description);
        return text.ToString();
        void Original(string label, string? value)
        {
            token.ThrowIfCancellationRequested();
            text.Append(label).Append(value is null ? ": null (unavailable)\n" : $" · {value.Length} characters:\n{value}\n\n");
        }
    }
}
