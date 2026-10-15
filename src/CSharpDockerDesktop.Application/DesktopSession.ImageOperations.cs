using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IImageOperations
{
    private readonly ImageOperationOptions imageOperationOptions;
    private ImageOperationSnapshot? imageOperation, notifiedImageOperation;
    private Task<ImageOperationSnapshot>? imageOperationCompletion;
    private long nextImageOperation;
    private EngineEndpoint? imageOperationEndpoint;
    private string? imageOperationEngineId;

    public event EventHandler? ImageOperationChanged;
    public ImageOperationSnapshot? ImageOperation { get { lock (gate) return imageOperation; } }

    public Task<ImageOperationSnapshot> ExecuteImageOperationAsync(ImageOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        Task<ImageOperationSnapshot> completion;
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested)
                return RejectImageOperation(request, ImageCommandFailureKind.Canceled, "The operation was canceled before dispatch.");
            if (disposed || imageReads is not { Reader: not null } reads || !CurrentImagesLocked(reads)
                || request.Target.ConnectionGeneration != imageSnapshot.ConnectionGeneration)
                return RejectImageOperation(request, ImageCommandFailureKind.StaleConnection, "Select an image from the current connection.");
            if (reads.Containers.Connection is not IImageCommands commands)
                return RejectImageOperation(request, ImageCommandFailureKind.InvalidRequest, "The connection does not offer image deletion.");
            if (string.IsNullOrEmpty(request.Target.Id) || !imageSnapshot.Images.Any(row => row.Id == request.Target.Id))
                return RejectImageOperation(request, ImageCommandFailureKind.NotFound, "Supply a complete image identity present in the current snapshot.");
            if (imageOperation is { } pending && (pending.Pending || imageOperationCompletion?.IsCompleted == false))
                return pending.Request == request ? imageOperationCompletion! : RejectImageOperation(request,
                    ImageCommandFailureKind.CapacityExceeded, "Another image operation is pending.");
            long id = ++nextImageOperation;
            imageOperation = new(id, request, ImageOperationPhase.CommandPending)
            { Endpoint = reads.Containers.Connection.Metadata.Endpoint, EngineId = reads.Containers.Connection.Metadata.EngineId };
            imageOperationEndpoint = reads.Containers.Connection.Metadata.Endpoint;
            imageOperationEngineId = reads.Containers.Connection.Metadata.EngineId;
            FenceImageOperationReadsLocked(reads);
            completion = Task.Run(() => RunImageOperationAsync(reads, commands, id, request, cancellationToken));
            imageOperationCompletion = completion;
            reads.OperationWorkflow = completion;
        }
        NotifyImagesChanged();
        NotifyImageOperationChanged();
        return completion;
    }

    private static Task<ImageOperationSnapshot> RejectImageOperation(ImageOperationRequest request,
        ImageCommandFailureKind kind, string description) => Task.FromResult(new ImageOperationSnapshot(0, request,
            ImageOperationPhase.Completed, new(ImageCommandDisposition.NotDispatched, Failure: new(kind, description)),
            ImageOperationResolution.NotDispatched));

    private static ImageCommandResult FreezeImageOutcome(ImageCommandResult outcome)
        => outcome.RemovalRecords is { } records ? outcome with { RemovalRecords = Array.AsReadOnly(records.ToArray()) } : outcome;

    private bool CurrentImageOperationLocked(ImageReads reads, long id)
        => CurrentImagesLocked(reads) && imageOperation?.OperationId == id;

    private void FenceImageOperationReadsLocked(ImageReads reads)
    {
        ++reads.ObservationRevision;
        ++reads.ListRevision;
        ++reads.DetailRevision;
        if (reads.Active is not null && !reads.OperationReadActive)
        { reads.ActiveCanceledByUser = true; reads.Active.Cancel(); }
        // Parent pruning can affect any selected image, not only the deletion target.
        InvalidateImageEventDetailLocked(reads, null);
        reads.Signal.Writer.TryWrite(0);
    }

    private async Task<ImageOperationSnapshot> RunImageOperationAsync(ImageReads reads, IImageCommands commands,
        long id, ImageOperationRequest request, CancellationToken caller)
    {
        try
        {
            using var commandDeadline = new CancellationTokenSource();
            using var command = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, caller, commandDeadline.Token);
            commandDeadline.CancelAfter(imageOperationOptions.CommandTimeout);
            ImageCommandResult outcome;
            lock (gate)
            {
                if (!CurrentImageOperationLocked(reads, id)) return imageOperation!;
                if (command.IsCancellationRequested)
                    outcome = new(ImageCommandDisposition.NotDispatched, Failure: new(commandDeadline.IsCancellationRequested
                        ? ImageCommandFailureKind.Deadline : ImageCommandFailureKind.Canceled,
                        "The operation ended before dispatch."));
                else
                {
                    FenceImageOperationReadsLocked(reads);
                    outcome = new(ImageCommandDisposition.Uncertain);
                    imageOperation = imageOperation! with { CommandOutcome = outcome };
                }
            }
            if (outcome.RequiresReconciliation)
            {
                // Deadline invalidates continuity immediately even if a borrowed command ignores cancellation.
                using var deadline = commandDeadline.Token.Register(() =>
                {
                    lock (gate)
                    {
                        if (reads.Lifetime.IsCancellationRequested || !CurrentImageOperationLocked(reads, id)) return;
                        imageOperation = imageOperation! with { CommandOutcome = new(ImageCommandDisposition.Uncertain,
                            Failure: new(ImageCommandFailureKind.Deadline, "The image command exceeded its deadline.")) };
                        ImageTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                            "The image command exceeded its deadline."), false);
                    }
                    NotifyContainerBrowsers();
                    NotifyImageOperationChanged();
                });
                try { outcome = await commands.DeleteImageAsync(request.Target.Id, command.Token).ConfigureAwait(false); }
                catch (ImageCommandCanceledException canceled) { outcome = canceled.Outcome; }
                catch (OperationCanceledException)
                { outcome = new(ImageCommandDisposition.Uncertain, Failure: new(caller.IsCancellationRequested
                    ? ImageCommandFailureKind.Canceled : ImageCommandFailureKind.Deadline,
                    "The command was interrupted; authoritative observation is required.")); }
                catch (Exception)
                { outcome = new(ImageCommandDisposition.Uncertain, Failure: new(ImageCommandFailureKind.UnexpectedResponse,
                    "The command did not return classified evidence; authoritative observation is required.")); }
            }
            bool lost;
            Task<ImageObservation>? first = null;
            lock (gate)
            {
                if (!CurrentImageOperationLocked(reads, id))
                {
                    // Late protocol evidence retains its original provenance without restoring resource ownership.
                    if (imageOperation?.OperationId == id)
                        imageOperation = imageOperation with { CommandOutcome = FreezeImageOutcome(outcome) };
                    return imageOperation!;
                }
                // Freeze a capability's mutable collection at the Application boundary.
                outcome = FreezeImageOutcome(outcome);
                imageOperation = imageOperation! with { CommandOutcome = outcome,
                    Phase = outcome.RequiresReconciliation ? ImageOperationPhase.Reconciling : ImageOperationPhase.Completed,
                    Resolution = outcome.RequiresReconciliation ? null : ImageOperationResolution.NotDispatched };
                if (!outcome.RequiresReconciliation)
                { reads.Refresh = true; reads.Signal.Writer.TryWrite(0); }
                lost = outcome.Failure?.Kind is ImageCommandFailureKind.TransportFailure
                    or ImageCommandFailureKind.StaleConnection or ImageCommandFailureKind.ConnectionDisposed
                    or ImageCommandFailureKind.Deadline;
                if (outcome.RequiresReconciliation)
                {
                    if (!lost) first = QueueImageObservationAsync(reads, request.Target.Id, false);
                    FenceImageOperationReadsLocked(reads);
                }
                if (lost) ImageTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                    outcome.Failure!.Description, outcome.HttpStatusCode, outcome.DaemonMessage), false);
            }
            if (lost)
            {
                NotifyContainerBrowsers();
                ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
            }
            else NotifyImagesChanged();
            NotifyImageOperationChanged();
            if (!lost && outcome.RequiresReconciliation) await ObserveImageOperationAsync(reads, id, first).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
        finally
        {
            lock (gate)
                if (imageOperation?.OperationId == id && imageOperation.Pending)
                    imageOperation = imageOperation with { Phase = ImageOperationPhase.Completed, Resolution = ImageOperationResolution.Unresolved };
            NotifyImageOperationChanged();
        }
        lock (gate) return imageOperation!;
    }

    private async Task ObserveImageOperationAsync(ImageReads reads, long id, Task<ImageObservation>? first = null)
    {
        for (int attempt = 0; attempt < imageOperationOptions.MaximumObservations; ++attempt)
        {
            string identity;
            lock (gate)
            {
                if (!CurrentImageOperationLocked(reads, id)) return;
                identity = imageOperation!.Request.Target.Id;
            }
            var observation = await (attempt == 0 && first is not null ? first
                : QueueImageObservationAsync(reads, identity)).ConfigureAwait(false);
            bool finished;
            lock (gate)
            {
                if (!CurrentImageOperationLocked(reads, id)) return;
                var current = imageOperation!;
                bool stale = observation.Invalidated || observation.Revision != reads.ObservationRevision;
                bool authoritative = !stale && observation.Exists is not null && (observation.Failure is null
                    || observation.Exists == false && observation.Failure.Kind == EngineResourceFailureKind.NotFound);
                finished = authoritative || attempt + 1 == imageOperationOptions.MaximumObservations;
                imageOperation = current with { Phase = finished ? ImageOperationPhase.Completed : ImageOperationPhase.Reconciling,
                    Resolution = !finished ? null : authoritative ? observation.Exists == false
                        ? ImageOperationResolution.ObservedAbsent : ImageOperationResolution.ObservedPresent : ImageOperationResolution.Unresolved,
                    ObservedConnectionGeneration = stale ? current.ObservedConnectionGeneration : imageSnapshot.ConnectionGeneration,
                    ObservedExists = authoritative ? observation.Exists : current.ObservedExists,
                    ObservationFailure = stale ? current.ObservationFailure : observation.Failure, Observations = current.Observations + 1 };
            }
            NotifyImageOperationChanged();
            if (finished) return;
            await delay.DelayAsync(imageOperationOptions.FollowupInterval, reads.Lifetime.Token).ConfigureAwait(false);
        }
    }

    private Task<ImageObservation> QueueImageObservationAsync(ImageReads reads, string identity, bool signal = true)
    {
        lock (gate)
        {
            reads.Lifetime.Token.ThrowIfCancellationRequested();
            var observation = new ImageObservationRequest(identity);
            reads.OperationObservation = observation;
            if (signal) reads.Signal.Writer.TryWrite(0);
            return observation.Completion.Task.WaitAsync(reads.Lifetime.Token);
        }
    }

    private void ResumeImageOperationLocked(ImageReads reads)
    {
        if (imageOperation is not { Resolution: ImageOperationResolution.Unresolved, CommandOutcome.RequiresReconciliation: true } current
            || string.IsNullOrEmpty(imageOperationEngineId) || imageOperationEndpoint != reads.Containers.Connection.Metadata.Endpoint
            || imageOperationEngineId != reads.Containers.Connection.Metadata.EngineId) return;
        imageOperation = current with { Phase = ImageOperationPhase.Reconciling, Resolution = null };
        long id = current.OperationId;
        var task = Task.Run(async () =>
        {
            try { await ObserveImageOperationAsync(reads, id).ConfigureAwait(false); }
            catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
            finally
            {
                lock (gate)
                    if (imageOperation?.OperationId == id && imageOperation.Pending)
                        imageOperation = imageOperation with { Phase = ImageOperationPhase.Completed, Resolution = ImageOperationResolution.Unresolved };
                NotifyImageOperationChanged();
            }
            lock (gate) return imageOperation!;
        });
        reads.OperationWorkflow = task;
        imageOperationCompletion = task;
    }

    private void InvalidateImageOperationLocked()
    {
        if (imageOperation is { Pending: true } current)
            imageOperation = current.CommandOutcome is null
                ? current with { Phase = ImageOperationPhase.Completed, Resolution = ImageOperationResolution.NotDispatched,
                    CommandOutcome = new(ImageCommandDisposition.NotDispatched, Failure: new(ImageCommandFailureKind.StaleConnection,
                        "The connection changed before dispatch.")) }
                : current with { Phase = ImageOperationPhase.Completed, Resolution = ImageOperationResolution.Unresolved };
    }

    private void NotifyImageOperationChanged()
    {
        lock (gate)
        {
            if (ReferenceEquals(notifiedImageOperation, imageOperation)) return;
            notifiedImageOperation = imageOperation;
        }
        NotifyImageObservers(ImageOperationChanged);
    }

    private void NotifyImageObservers(EventHandler? handlers)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            // Observer failures must not orphan owned Docker work. Never log content or exceptions.
            try { handler(this, EventArgs.Empty); } catch (Exception) { }
        }
    }

    private sealed record ImageObservation(bool? Exists, EngineResourceFailure? Failure, bool Invalidated, long Revision);
    private sealed class ImageObservationRequest(string identity)
    {
        public string Identity { get; } = identity;
        public TaskCompletionSource<ImageObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The existing image scheduler owns these reads; neither selection nor view lifetime controls observation.
    private async Task ReadImageObservationAsync(ImageReads reads, ImageObservationRequest observation)
    {
        long epoch, listRevision, detailRevision;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token);
        lock (gate)
        {
            epoch = reads.ObservationRevision; listRevision = reads.ListRevision; detailRevision = reads.DetailRevision;
            reads.Refresh = false; reads.EventRefresh = false;
            reads.OperationReadActive = true; reads.ActiveCanceledByUser = false; reads.Active = request;
        }
        using var deadline = request.Token.Register(() => ImageReadCanceled(reads, request, true));
        request.CancelAfter(options.OperationTimeout);
        try
        {
            var list = await reads.Reader!.ListImagesAsync(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            bool? exists = null;
            var failure = list.Failure;
            ImageInspection? inspection = null;
            if (list.IsSuccess)
            {
                exists = list.Value!.Any(row => row.Id == observation.Identity);
                if (exists == true)
                {
                    var detail = await reads.Reader.InspectImageAsync(observation.Identity, request.Token).ConfigureAwait(false);
                    request.Token.ThrowIfCancellationRequested();
                    failure = detail.Failure;
                    inspection = detail.Value;
                    if (failure?.Kind == EngineResourceFailureKind.NotFound) exists = false;
                    if (inspection is not null && inspection.Id != observation.Identity)
                    { exists = null; failure = new(EngineResourceFailureKind.MalformedData, "The image observation returned a different identity."); }
                }
            }
            lock (gate)
            {
                if (!CurrentImagesLocked(reads) || request.IsCancellationRequested) return;
                if (failure?.Kind == EngineResourceFailureKind.TransportLoss)
                { ImageTransportLostLocked(reads, failure, false); return; }
                bool stale = epoch != reads.ObservationRevision;
                if (!stale && listRevision == reads.ListRevision)
                {
                    if (list.IsSuccess)
                    {
                        var rows = Array.AsReadOnly(list.Value!.DistinctBy(row => row.Id, StringComparer.Ordinal)
                            .Where(row => exists != false || row.Id != observation.Identity).ToArray());
                        imageSnapshot = imageSnapshot with { State = ResourceReadState.Ready, Images = rows, Failure = null };
                        if (imageSnapshot.SelectedImageId is { } selected && !rows.Any(row => row.Id == selected))
                            RequestImageInspectionLocked(reads, null, false);
                    }
                    else imageSnapshot = imageSnapshot with { State = ResourceReadState.Failed, Failure = list.Failure };
                }
                if (!stale && detailRevision == reads.DetailRevision && imageSnapshot.SelectedImageId == observation.Identity
                    && imageSnapshot.InspectionState != ResourceReadState.NotRequested)
                {
                    reads.Inspect = false;
                    imageSnapshot = imageSnapshot with { Inspection = inspection, InspectionFailure = failure,
                        InspectionState = failure is null ? ResourceReadState.Ready : ResourceReadState.Failed };
                }
                observation.Completion.TrySetResult(new(exists, failure, stale, epoch));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate)
                if (CurrentImagesLocked(reads)) observation.Completion.TrySetResult(new(null,
                    new(EngineResourceFailureKind.MalformedData, "The image observation did not return classified evidence."),
                    epoch != reads.ObservationRevision, epoch));
        }
        finally
        {
            if (request.IsCancellationRequested) ImageReadCanceled(reads, request, true);
            lock (gate) { reads.OperationReadActive = false; reads.Active = null; }
            NotifyContainerBrowsers();
            NotifyImageOperationChanged();
        }
    }
}
