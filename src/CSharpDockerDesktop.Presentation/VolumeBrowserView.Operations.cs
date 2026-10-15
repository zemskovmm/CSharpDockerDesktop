using System.Collections;
using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>One presentation lifetime; its target never follows selection.</summary>
public sealed class VolumeDeletePresentation(VolumeDeletionTarget target)
{
    public VolumeDeletionTarget Target { get; } = target;
    internal CancellationTokenSource Cancellation { get; } = new();
    internal Task<VolumeDeletionPreparationSnapshot>? Work;
    internal VolumeDeletionPreparationSnapshot? Snapshot;
    internal VolumeDeleteConfirmation? Confirmation;
    internal bool Closed, Invalidated, Starting = true;
    internal VolumePreparationIdentity? CompletedIdentity;
}

/// <summary>The exact completed revision shown to the user, consumed once by reference identity.</summary>
public sealed record VolumeDeleteConfirmation(VolumeOperationRequest Request);

public sealed partial class VolumeBrowserView
{
    private readonly IVolumeDeletionPreparation? preparationService = browser as IVolumeDeletionPreparation;
    private readonly IVolumeOperations? operations = browser as IVolumeOperations;
    private bool submitting;
    private VolumeOperationSnapshot? operation;
    private VolumeDependencyRows? preparationRows, operationRows, operationPreparationRows;
    private long operationEvidenceRevision, dependencyCopyRevision;
    private CancellationTokenSource? dependencyCopyWork;
    private CancellationTokenSource? operationEvidenceWork;
    private string operationEvidence = "";
    private VolumeDeletionPreparationSnapshot? revealedPreparation;
    public VolumeDeletePresentation? Deletion { get; private set; }
    public Task PreparationCleanup { get; private set; } = Task.CompletedTask;
    public string AdmissionMessage { get; private set; } = "";
    public VolumeOperationSnapshot? Operation => active ? operations?.VolumeOperation : null;
    public bool ShowingOperationDetails { get; private set; }
    public bool PreparingOperationDetails => operationEvidenceWork is not null;
    public bool CanDelete => preparationService is not null && operations is not null && !submitting
        && Deletion is null && operations.VolumeOperation?.Pending != true && CurrentIdentity(browser.VolumeSnapshot);
    public const string DataLossWarning = "Warning: deleting this volume can permanently remove its data. Deletion cannot be undone.";
    public const string PrecheckExplanation = "Fresh dependency evidence includes found running and stopped containers. This precheck is not proof of safety: dependencies can change before deletion. The daemon makes the final decision. Non-force deletion only; dependent containers will not be removed.";
    private bool TargetCurrent(VolumeDeletePresentation presentation)
    {
        var latest = browser.VolumeSnapshot;
        return active && !presentation.Closed && ReferenceEquals(Deletion, presentation)
            && CurrentIdentity(latest) && latest.ConnectionGeneration == presentation.Target.ConnectionGeneration
            && StringComparer.Ordinal.Equals(SelectedName, presentation.Target.Name);
    }
    private VolumeDeletionPreparationSnapshot? LivePreparation
    {
        get
        {
            if (Deletion is not { } presentation || !TargetCurrent(presentation) || presentation.Starting) return null;
            var latest = preparationService?.VolumeDeletionPreparation;
            return latest is not null && latest.Identity.PreparationId == presentation.Snapshot?.Identity.PreparationId
                && latest.Target == presentation.Target ? latest : presentation.Snapshot?.Identity.PreparationId == 0 ? presentation.Snapshot : null;
        }
    }
    public VolumeDeleteConfirmation? DeleteConfirmation => Deletion?.Confirmation is { } confirmation
        && LivePreparation is { Phase: VolumePreparationPhase.Completed } latest && latest.Identity == confirmation.Request.Preparation
        && latest.Target == confirmation.Request.Target && latest.HasUsableEvidence
        && preparationService!.IsVolumeDeletionPreparationCurrent(confirmation.Request.Preparation)
        && Deletion?.Invalidated == false && !submitting && operations?.VolumeOperation?.Pending != true ? confirmation : null;
    public string PreparationMessage => LivePreparation is { } snapshot ? VolumeOperationDisplay.Preparation(snapshot)
        : Deletion is null ? "" : "Preparation unavailable or invalidated. Cancel, then choose Delete for a fresh explicit precheck.";
    public IReadOnlyList<VolumeDependencyRow> PreparationDependencies => LivePreparation is { Dependencies: { } dependencies }
        ? RowsFor(dependencies, ref preparationRows) : Array.Empty<VolumeDependencyRow>();
    public IReadOnlyList<VolumeDependencyRow> OperationPreparationDependencies => ShowingOperationDetails && ReferenceEquals(operation, operations?.VolumeOperation) && Operation is { Preparation.Dependencies: { } dependencies }
        ? RowsFor(dependencies, ref operationPreparationRows) : Array.Empty<VolumeDependencyRow>();
    public IReadOnlyList<VolumeDependencyRow> RefreshedDependencies => ShowingOperationDetails && ReferenceEquals(operation, operations?.VolumeOperation) && Operation is { RefreshedDependencies: { } dependencies }
        ? RowsFor(dependencies, ref operationRows) : Array.Empty<VolumeDependencyRow>();
    private static IReadOnlyList<VolumeDependencyRow> RowsFor(VolumeDependencies source, ref VolumeDependencyRows? rows)
    { if (!ReferenceEquals(rows?.Source, source)) rows = new(source); return rows; }

    public VolumeDeletePresentation? BeginDelete()
    {
        if (!CanDelete) return null;
        ApplyLatest(); if (!CanDelete) return null;
        var presentation = new VolumeDeletePresentation(new(Snapshot.ConnectionGeneration, SelectedName!));
        Deletion = presentation; AdmissionMessage = "";
        var previous = preparationService!.VolumeDeletionPreparation;
        try
        {
            presentation.Work = preparationService.PrepareVolumeDeletionAsync(presentation.Target, presentation.Cancellation.Token);
            var latest = preparationService.VolumeDeletionPreparation;
            if (!ReferenceEquals(previous, latest) && latest?.Target == presentation.Target) presentation.Snapshot = latest;
        }
        catch (Exception)
        {
            // Exception text can contain private daemon data.
            presentation.Snapshot = new(default, presentation.Target, VolumePreparationPhase.Failed,
                Failure: new(VolumePreparationFailureKind.AcquisitionFailed));
        }
        presentation.Starting = false;
        SyncDeletion(); Changed?.Invoke(this, EventArgs.Empty);
        if (presentation.Work is { } work) _ = CompletePreparation(presentation, work, epoch);
        return presentation;
    }
    private async Task CompletePreparation(VolumeDeletePresentation presentation, Task<VolumeDeletionPreparationSnapshot> work, int lifetime)
    {
        VolumeDeletionPreparationSnapshot? result = null;
        try { result = await work.ConfigureAwait(false); } catch (Exception) { }
        dispatch(() =>
        {
            if (!active || epoch != lifetime || !ReferenceEquals(Deletion, presentation) || presentation.Closed) return;
            if (result?.Target == presentation.Target) presentation.Snapshot = result;
            else presentation.Snapshot = new(default, presentation.Target, VolumePreparationPhase.Failed,
                Failure: new(VolumePreparationFailureKind.AcquisitionFailed));
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
        if (!presentation.Invalidated && latest.HasUsableEvidence && preparationService!.IsVolumeDeletionPreparationCurrent(latest.Identity))
        {
            // A revision change never silently renews an already shown confirmation.
            if (presentation.CompletedIdentity is null && latest.Phase == VolumePreparationPhase.Completed)
            { presentation.CompletedIdentity = latest.Identity; presentation.Confirmation = new(new(presentation.Target, latest.Identity)); }
            else if (presentation.Confirmation?.Request.Preparation != latest.Identity) presentation.Confirmation = null;
        }
        else
        {
            if (latest.Phase is VolumePreparationPhase.Invalidated or VolumePreparationPhase.Consumed or VolumePreparationPhase.Failed) presentation.Invalidated = true;
            presentation.Confirmation = null;
        }
    }
    public bool HasPreparationDetails => LivePreparation?.Failure?.ResourceFailure is not null;
    public bool ShowingPreparationDetails => revealedPreparation is not null && ReferenceEquals(revealedPreparation, LivePreparation);
    public string PreparationDetails => ShowingPreparationDetails && revealedPreparation?.Failure?.ResourceFailure is { } failure
        ? $"Original preparation failure description:\n{failure.Description}\nOriginal daemon message ({failure.DaemonMessage?.Length.ToString() ?? "null"} characters):\n{failure.DaemonMessage}" : "";
    public void ShowPreparationDetails(bool show)
    { if (!active) return; revealedPreparation = show && HasPreparationDetails ? LivePreparation : null; Changed?.Invoke(this, EventArgs.Empty); }
    public void CancelDelete(VolumeDeletePresentation presentation)
    {
        if (!ReferenceEquals(Deletion, presentation)) return;
        CloseDeletion(); if (active) Changed?.Invoke(this, EventArgs.Empty);
    }
    private void CloseDeletion()
    {
        if (Deletion is not { } presentation) return;
        CancelDependencyCopy();
        Deletion = null; preparationRows = null; revealedPreparation = null; presentation.Closed = true; presentation.Confirmation = null;
        presentation.Cancellation.Cancel();
        // Close fences the matching ID synchronously. Its async join never blocks navigation.
        var cleanup = CleanupPreparation(presentation);
        if (!cleanup.IsCompleted) PreparationCleanup = PreparationCleanup.IsCompleted ? cleanup : Task.WhenAll(PreparationCleanup, cleanup);
    }
    private async Task CleanupPreparation(VolumeDeletePresentation presentation)
    {
        Task close = Task.CompletedTask;
        long closedId = 0;
        try
        {
            if (presentation.Snapshot is { Identity.PreparationId: > 0 } snapshot)
            { close = preparationService!.CloseVolumeDeletionPreparationAsync(snapshot.Identity); closedId = snapshot.Identity.PreparationId; }
        }
        catch (Exception) { }
        try
        {
            if (presentation.Work is { } work)
            {
                var result = await work.ConfigureAwait(false);
                if (closedId == 0 && result.Target == presentation.Target && result.Identity.PreparationId > 0)
                    await preparationService!.CloseVolumeDeletionPreparationAsync(result.Identity).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* Never log private daemon evidence. */ }
        try { await close.ConfigureAwait(false); } catch (Exception) { }
        finally { presentation.Cancellation.Dispose(); }
    }

    public void ConfirmDelete(VolumeDeletePresentation presentation, VolumeDeleteConfirmation confirmation)
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
        Task<VolumeOperationSnapshot>? completion = null;
        int lifetime = epoch;
        // Admission atomically consumes preparation; only then close its presentation.
        // Accepted mutation and reconciliation use session ownership, never page cancellation.
        try { completion = operations!.ExecuteVolumeOperationAsync(confirmation.Request); }
        catch (Exception) { submitting = false; AdmissionMessage = VolumeOperationDisplay.Unresolved; }
        CloseDeletion(); SyncOperation(); Changed?.Invoke(this, EventArgs.Empty);
        if (completion is not null) _ = CompleteSubmission(completion, lifetime);
    }
    private async Task CompleteSubmission(Task<VolumeOperationSnapshot> completion, int lifetime)
    {
        VolumeOperationSnapshot? result = null;
        try { result = await completion.ConfigureAwait(false); } catch (Exception) { }
        dispatch(() =>
        {
            if (!active || epoch != lifetime) return;
            submitting = false; SyncOperation();
            if (result is null) AdmissionMessage = VolumeOperationDisplay.Unresolved;
            else if (result.OperationId == 0) AdmissionMessage = VolumeOperationDisplay.Status(result);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
    public string OperationMessage => Operation is { } latest ? VolumeOperationDisplay.Status(latest, Snapshot.State == ResourceReadState.Unavailable ? -1 : Snapshot.ConnectionGeneration) : "No volume deletion in this session.";
    public bool HasOperationDetails => Operation is { } latest && (latest.CommandOutcome is not null || latest.Preparation is not null
        || latest.ObservationFailure is not null || latest.ObservationListFailure is not null || latest.DependencyFailure is not null || latest.ObservedListing is not null);
    public string OperationDetails => ShowingOperationDetails && active && ReferenceEquals(operation, operations?.VolumeOperation) ? operationEvidence : "";
    private void SyncOperation()
    {
        var latest = operations?.VolumeOperation;
        if (!ReferenceEquals(operation, latest)) { HideOperationDetails(); operationRows = operationPreparationRows = null; }
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
                var text = VolumeOperationDisplay.SensitiveDetails(captured, token);
                dispatch(() =>
                {
                    if (!active || epoch != lifetime || revision != operationEvidenceRevision || token.IsCancellationRequested
                        || !ReferenceEquals(captured, operations?.VolumeOperation)) return;
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
    private void CancelDependencyCopy()
    { ++dependencyCopyRevision; dependencyCopyWork?.Cancel(); dependencyCopyWork?.Dispose(); dependencyCopyWork = null; }
    public async Task<bool> CopyDependencyAsync(VolumeDependencyRow row, bool operationEvidence, Action<string> publish)
    {
        if (!active) return false;
        CancelDependencyCopy();
        var cancel = dependencyCopyWork = new(); var token = cancel.Token; long copyRevision = dependencyCopyRevision;
        var presentation = Deletion; var capturedOperation = Operation; int lifetime = epoch; long revealRevision = operationEvidenceRevision;
        bool Current() => active && lifetime == epoch && copyRevision == dependencyCopyRevision && !token.IsCancellationRequested && (operationEvidence
            ? ShowingOperationDetails && revealRevision == operationEvidenceRevision && ReferenceEquals(capturedOperation, operations?.VolumeOperation)
                && (ReferenceEquals(row.Source, capturedOperation?.RefreshedDependencies) || ReferenceEquals(row.Source, capturedOperation?.Preparation?.Dependencies))
            : ReferenceEquals(presentation, Deletion) && DeleteConfirmation is not null && ReferenceEquals(row.Source, LivePreparation?.Dependencies));
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

/// <summary>Lazy projection: every relationship remains reachable without allocating a visual/text per dependency.</summary>
public sealed class VolumeDependencyRows(VolumeDependencies source) : IReadOnlyList<VolumeDependencyRow>
{
    public VolumeDependencies Source { get; } = source;
    public int Count => (Source.Containers?.Count ?? 0) + Source.UnverifiedContainerIds.Count;
    public VolumeDependencyRow this[int index] => index >= 0 && index < Count ? new(Source, index) : throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<VolumeDependencyRow> GetEnumerator() { for (int i = 0; i < Count; ++i) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record VolumeDependencyRow(VolumeDependencies Source, int Index)
{
    private VolumeContainerDependency? Container => Index < (Source.Containers?.Count ?? 0) ? Source.Containers![Index] : null;
    public string Id => Container?.Id ?? Source.UnverifiedContainerIds[Index - (Source.Containers?.Count ?? 0)];
    public string Text => Container is { } c
        ? $"Found related container · ID: {VolumeOperationDisplay.Preview(c.Id)}\nName: {VolumeOperationDisplay.Preview(c.Name ?? c.Names?.FirstOrDefault())} · names: {(c.Names is null ? "unavailable" : c.Names.Count)}\nState: {VolumeOperationDisplay.Preview(c.State)} · running: {(c.Running is { } running ? running ? "yes" : "no (includes stopped containers)" : "unavailable")}. Select and copy for full original identities/state."
        : $"Unverified candidate · ID: {VolumeOperationDisplay.Preview(Id)}\nMount evidence unavailable; a relationship cannot be confirmed or excluded. Select and copy for the full identity.";
    public string FullText(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Container is not { } c) return "Unverified candidate · full ID: " + Id + "\nMount relationship unverified.";
        var text = new StringBuilder($"Full ID: {c.Id}\nName: {VolumeDisplay.Text(c.Name)}\nNames: ");
        if (c.Names is null) text.Append("unavailable (null)");
        else if (c.Names.Count == 0) text.Append("empty collection");
        else foreach (var name in c.Names) { token.ThrowIfCancellationRequested(); text.Append(name).AppendLine(); }
        token.ThrowIfCancellationRequested();
        return text.Append($"\nState: {VolumeDisplay.Text(c.State)}\nRunning: {c.Running?.ToString() ?? "unavailable (null)"}").ToString();
    }

}

public static class VolumeOperationDisplay
{
    public static string Preview(string? value) => value is { Length: > 240 } ? value[..240] + "… (reveal/copy for full original value)" : VolumeDisplay.Text(value);
    private static string SafeFailure(EngineResourceFailure failure) => $"{failure.Kind}{(failure.HttpStatusCode is { } http ? $" (HTTP {http})" : "")}. Reveal original evidence for details.";
    public const string Unresolved = "Unresolved outcome. The command may have taken effect. Uncertain mutations are never automatically replayed. Check authoritative Engine state before a new deliberate action.";
    public static string Dependencies(VolumeDependencies? dependencies) => dependencies?.Completeness switch
    {
        VolumeDependencyCompleteness.Complete when dependencies.Containers!.Count == 0 => "Complete-empty dependency evidence: no related containers found by this query. This is not proof of safety.",
        VolumeDependencyCompleteness.Complete => $"Complete dependency evidence: {dependencies.Containers!.Count} found related containers, including stopped containers.",
        VolumeDependencyCompleteness.Incomplete => $"Incomplete dependency evidence: {dependencies.Containers!.Count} found relationships and {dependencies.UnverifiedContainerIds.Count} unverified candidates. Missing mount evidence means more relationships may exist; absence cannot be established. Both collections are listed below.",
        _ => "Unavailable dependency evidence. No usable candidate collection was returned. Deletion is disabled; cancel and run a fresh explicit precheck."
    };
    public static string Preparation(VolumeDeletionPreparationSnapshot preparation) => preparation.Phase switch
    {
        VolumePreparationPhase.Loading => "Loading fresh running/stopped container dependencies… Deletion is disabled while acquisition is pending.",
        VolumePreparationPhase.Completed => Dependencies(preparation.Dependencies),
        VolumePreparationPhase.Invalidated => $"Invalidated dependency evidence ({preparation.Failure?.Kind}). Deletion is disabled. Cancel and choose Delete for a fresh explicit precheck. Previously found relationships are retained below.",
        VolumePreparationPhase.Consumed => "Preparation consumed by an operation. It cannot authorize another deletion.",
        _ => $"Dependency preparation failed ({preparation.Failure?.Kind}). Deletion is disabled. Cancel and choose Delete for a fresh precheck."
            + (preparation.Failure?.ResourceFailure is { } failure ? " " + SafeFailure(failure) : "")
    };
    public static string Status(VolumeOperationSnapshot operation, long? currentGeneration = null)
    {
        string phase = operation.Phase switch
        {
            VolumeOperationPhase.CommandPending => "Command pending. Volume absence has not been established.",
            VolumeOperationPhase.Reconciling => "Reconciling authoritative Engine state. Command acknowledgement does not establish deletion.",
            _ => operation.Resolution switch
            {
                VolumeOperationResolution.NotDispatched => "Not dispatched. No delete command was sent.",
                VolumeOperationResolution.ObservedAbsent => "Observed absent in authoritative Engine state; this does not establish command causation.",
                VolumeOperationResolution.ObservedPresent => "Observed present in authoritative Engine state. The name may refer to a recreated volume; identity continuity and command causation are not established.",
                _ => Unresolved
            }
        };
        var text = new StringBuilder($"Non-force volume Delete · name: {Preview(operation.Request.Target.Name)} · connection generation: {operation.Request.Target.ConnectionGeneration}\n{OperationProvenanceDisplay.Describe(operation.Endpoint, operation.EngineId, operation.Request.Target.ConnectionGeneration, currentGeneration)}\n{phase}");
        if (operation.Rejection is { } rejection) text.Append($"\nAdmission rejection: {rejection}.");
        if (operation.Interruption is { } interruption) text.Append($"\nInterruption: {interruption}. Possible dispatch still requires observation.");
        if (operation.CommandOutcome is { } command)
        {
            text.Append($"\nOriginal command disposition: {command.Disposition}.");
            if (command.HttpStatusCode is { } http) text.Append($" HTTP {http}.");
            if (command.Failure is { } failure) text.Append($" Failure: {failure.Kind}.");
            if (command.ErrorBodyUnavailable) text.Append(" Original error body unavailable.");
            if (command.Failure?.Kind == VolumeCommandFailureKind.Conflict) text.Append(" Dependency conflict: review refreshed evidence below. Release dependencies deliberately outside this action, then choose Delete for a fresh explicit precheck and confirmation. Refreshed evidence does not authorize another attempt; no force, container removal, or blind retry.");
        }
        if (operation.ObservedExists is { } exists) text.Append($"\nSeparate authoritative observation: name {(exists ? "present" : "absent")}.");
        if (operation.ObservedConnectionGeneration is { } generation) text.Append($" Observation generation: {generation}.");
        if (operation.ObservationInvalidated) text.Append(" Observation invalidated; current presence remains unestablished.");
        if (operation.ObservationFailure is { } error) text.Append("\nObservation failure: " + SafeFailure(error));
        if (operation.ObservationListFailure is { } listError) text.Append("\nObservation listing failure: " + SafeFailure(listError));
        if (operation.ObservedListing is { } listing && (listing.Volumes is null || listing.HasWarnings)) text.Append("\nObservation listing is incomplete/unavailable; warnings cannot establish absence.");
        if (operation.DependencyObservationState != VolumeDependencyObservationState.NotRequested)
            text.Append($"\nConflict dependency refresh: {operation.DependencyObservationState}. ").Append(Dependencies(operation.RefreshedDependencies));
        if (operation.DependencyFailure is { } dependencyError) text.Append("\nDependency refresh failure: " + SafeFailure(dependencyError));
        text.Append("\nVolume names can be recreated. Observations describe current presence by name, not which action caused it. Acknowledgement alone is not deletion.");
        return text.ToString();
    }
    public static string SensitiveDetails(VolumeOperationSnapshot operation, CancellationToken token = default)
    {
        var text = new StringBuilder("Original private operation evidence · may contain sensitive content\n");
        Original("Exact ordinal volume name", operation.Request.Target.Name);
        if (operation.Preparation is { } preparation) text.AppendLine("Original precheck (historical): " + Preparation(preparation));
        text.AppendLine($"Preparation ID/revision: {operation.Request.Preparation.PreparationId}/{operation.Request.Preparation.Revision}");
        Original("Command daemon message", operation.CommandOutcome?.DaemonMessage);
        Original("Command error body", operation.CommandOutcome?.DaemonErrorBody);
        Original("Command failure description", operation.CommandOutcome?.Failure?.Description);
        Original("Preparation daemon message", operation.Preparation?.Failure?.ResourceFailure?.DaemonMessage);
        Original("Preparation failure description", operation.Preparation?.Failure?.ResourceFailure?.Description);
        Original("Observation daemon message", operation.ObservationFailure?.DaemonMessage);
        Original("Observation failure description", operation.ObservationFailure?.Description);
        Original("Observation listing daemon message", operation.ObservationListFailure?.DaemonMessage);
        Original("Observation listing failure description", operation.ObservationListFailure?.Description);
        Original("Dependency refresh daemon message", operation.DependencyFailure?.DaemonMessage);
        Original("Dependency refresh failure description", operation.DependencyFailure?.Description);
        if (operation.ObservedListing is { } listing)
        {
            if (listing.Warnings is null) text.AppendLine("Observation warnings: null (unavailable collection).");
            else if (listing.Warnings.Count == 0) text.AppendLine("Observation warnings: empty collection.");
            else for (int i = 0; i < listing.Warnings.Count; ++i) Original($"Observation warning {i + 1}", listing.Warnings[i]);
        }
        return text.ToString();
        void Original(string label, string? value)
        {
            token.ThrowIfCancellationRequested();
            text.Append(label).Append(value is null ? ": null (unavailable)\n" : $" · {value.Length} characters:\n{value}\n\n");
        }
    }
}
