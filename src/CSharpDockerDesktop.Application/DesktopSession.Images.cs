using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IImageBrowser
{
    private ImageBrowsingSnapshot imageSnapshot = new(0, ResourceReadState.Unavailable, Array.Empty<ImageSummary>());
    private ImageReads? imageReads;
    private ImageBrowsingSnapshot? notifiedImageSnapshot;

    public event EventHandler? ImagesChanged;
    public ImageBrowsingSnapshot ImageSnapshot { get { lock (gate) return imageSnapshot; } }

    public void RefreshImages()
    {
        lock (gate)
        {
            if (imageReads is not { Reader: not null } reads || !CurrentImagesLocked(reads)) return;
            ++reads.ListRevision;
            ++reads.ObservationRevision;
            reads.Refresh = true;
            // Fence detail results predating this refresh, including non-cooperative readers.
            ++reads.DetailRevision;
            if (reads.InspectActive)
                reads.Inspect = imageSnapshot.SelectedImageId is not null
                    && imageSnapshot.InspectionState == ResourceReadState.Loading;
            if (!reads.OperationReadActive)
            { reads.ActiveCanceledByUser = true; reads.Active?.Cancel(); }
            imageSnapshot = imageSnapshot with { State = ResourceReadState.Loading, Failure = null };
            reads.Signal.Writer.TryWrite(0);
        }
        NotifyImagesChanged();
    }

    public void SelectImage(string? imageId)
    {
        lock (gate)
        {
            if (imageReads is not { Reader: not null } reads || !CurrentImagesLocked(reads)) return;
            if (imageId is not null && !imageSnapshot.Images.Any(row => row.Id == imageId)) imageId = null;
            RequestImageInspectionLocked(reads, imageId, imageId is not null);
        }
        NotifyImagesChanged();
    }

    public void RefreshImageInspection()
    {
        lock (gate)
        {
            if (imageReads is not { Reader: not null } reads || !CurrentImagesLocked(reads)
                || imageSnapshot.SelectedImageId is not { } id) return;
            RequestImageInspectionLocked(reads, id, true);
        }
        NotifyImagesChanged();
    }

    public void ClearImageInspection()
    {
        lock (gate)
        {
            if (imageReads is not { Reader: not null } reads || !CurrentImagesLocked(reads)) return;
            RequestImageInspectionLocked(reads, imageSnapshot.SelectedImageId, false);
        }
        NotifyImagesChanged();
    }

    private void RequestImageInspectionLocked(ImageReads reads, string? id, bool inspect)
    {
        ++reads.DetailRevision;
        reads.Inspect = inspect;
        if (reads.InspectActive && !reads.OperationReadActive)
        {
            reads.ActiveCanceledByUser = true;
            reads.Active?.Cancel();
        }
        imageSnapshot = imageSnapshot with { SelectedImageId = id, Inspection = null,
            InspectionState = inspect ? ResourceReadState.Loading : ResourceReadState.NotRequested, InspectionFailure = null };
        reads.Signal.Writer.TryWrite(0);
    }

    private ImageReads? BeginImageReads(ContainerReads containers)
    {
        ImageReads reads;
        lock (gate)
        {
            if (!CurrentEventsLocked(containers)) return null;
            reads = new(containers) { EventRefresh = containers.ImageRefreshPending };
            imageReads = reads;
            imageSnapshot = new(containerSnapshot.ConnectionGeneration,
                reads.Reader is null ? ResourceReadState.Unavailable : ResourceReadState.Loading,
                Array.Empty<ImageSummary>(), InspectionState: reads.Reader is null
                    ? ResourceReadState.Unavailable : ResourceReadState.NotRequested);
            if (reads.Reader is not null)
            {
                reads.Signal.Writer.TryWrite(0);
                reads.Workflow = Task.Run(() => ReadImagesAsync(reads));
                ResumeImageOperationLocked(reads);
            }
        }
        NotifyImagesChanged();
        return reads;
    }

    private void RequestImageEventSnapshotLocked(ContainerReads containers, bool initial)
    {
        if (!initial) containers.ImageRefreshPending = true;
        if (imageReads is not { Reader: not null } reads || reads.Containers != containers) return;
        if (!initial) reads.EventRefresh = true;
        ++reads.ObservationRevision;
        InvalidateImageEventDetailLocked(reads, null);
        reads.Signal.Writer.TryWrite(0);
    }

    private bool ObserveImageEventLocked(ContainerReads containers, string? identity)
    {
        // Retain no event/actor metadata, including during the coordinator startup race.
        containers.ImageRefreshPending = true;
        if (imageReads is not { Reader: not null } reads || reads.Containers != containers) return false;
        reads.EventRefresh = true;
        ++reads.ObservationRevision;
        bool changed = InvalidateImageEventDetailLocked(reads, identity);
        reads.Signal.Writer.TryWrite(0);
        return changed;
    }

    private bool InvalidateImageEventDetailLocked(ImageReads reads, string? identity)
    {
        if (imageSnapshot.SelectedImageId is not { } selected
            || imageSnapshot.InspectionState == ResourceReadState.NotRequested
            || (!string.IsNullOrEmpty(identity) && identity != selected)) return false;
        // Fence successes and failures from non-cooperative older inspections. Do not repeatedly
        // cancel I/O: finish that bounded request and give the authoritative replacement a turn.
        ++reads.DetailRevision;
        reads.Inspect = true;
        bool changed = imageSnapshot.Inspection is not null || imageSnapshot.InspectionState != ResourceReadState.Loading;
        if (changed) imageSnapshot = imageSnapshot with { Inspection = null,
            InspectionState = ResourceReadState.Loading, InspectionFailure = null };
        return changed;
    }

    private bool CurrentImagesLocked(ImageReads reads)
        => !disposed && ReferenceEquals(imageReads, reads) && !reads.Lifetime.IsCancellationRequested
            && CurrentEventsLocked(reads.Containers);

    // Shared invalidation always precedes cancellation and disposal; no stale family may keep publishing.
    private void InvalidateImagesLocked(EngineResourceFailure? failure = null, bool inspection = false)
    {
        var previous = imageReads;
        imageReads = null;
        imageSnapshot = new(imageSnapshot.ConnectionGeneration, ResourceReadState.Unavailable,
            Array.Empty<ImageSummary>(), Failure: inspection ? null : failure, InspectionFailure: inspection ? failure : null);
        InvalidateImageOperationLocked();
        previous?.Lifetime.Cancel();
    }

    private async Task EndImageReadsAsync(ImageReads reads)
    {
        lock (gate)
        {
            if (ReferenceEquals(imageReads, reads)) InvalidateImagesLocked();
            else reads.Lifetime.Cancel();
        }
        // Never release the borrowed connection while even a non-cooperative read is outstanding.
        try { await Task.WhenAll(reads.Workflow, reads.OperationWorkflow).ConfigureAwait(false); }
        finally { reads.Lifetime.Dispose(); }
    }

    private async Task ReadImagesAsync(ImageReads reads)
    {
        try
        {
            while (await reads.Signal.Reader.WaitToReadAsync(reads.Lifetime.Token).ConfigureAwait(false))
            {
                reads.Signal.Reader.TryRead(out _);
                while (true)
                {
                    ImageObservationRequest? operationRead;
                    lock (gate)
                    {
                        if (!CurrentImagesLocked(reads)) return;
                        bool browsingPending = reads.Refresh || reads.EventRefresh || reads.Inspect;
                        operationRead = reads.Containers.InitialObservationPending || reads.InitialSnapshotPending
                            || reads.LastReadWasOperation && browsingPending ? null : reads.OperationObservation;
                        if (operationRead is not null)
                        { reads.OperationObservation = null; reads.LastReadWasOperation = true; }
                    }
                    if (operationRead is not null)
                    {
                        await ReadImageObservationAsync(reads, operationRead).ConfigureAwait(false);
                        continue;
                    }
                    bool list;
                    long revision;
                    string? id;
                    CancellationTokenSource request;
                    bool batch;
                    lock (gate)
                    {
                        if (!CurrentImagesLocked(reads)) return;
                        batch = !reads.Refresh && reads.EventRefresh && !(reads.Inspect && reads.LastReadWasList);
                    }
                    if (batch) await delay.DelayAsync(reconciliation.BatchInterval, reads.Lifetime.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!CurrentImagesLocked(reads)) return;
                        if (reads.Containers.InitialObservationPending) break;
                        if (!reads.InitialSnapshotPending && reads.OperationObservation is not null && !reads.LastReadWasOperation) continue;
                        if (!reads.Refresh && !reads.EventRefresh && !reads.Inspect) break;
                        // Alternate when both are pending: repeated refreshes cannot monopolize the scheduler.
                        list = (reads.Refresh || reads.EventRefresh) && !(reads.Inspect && reads.LastReadWasList);
                        if (list)
                        {
                            reads.Refresh = false;
                            // Pre-snapshot observations survive the initial read; concurrent observations
                            // set this flag again after subsequent reads consume it.
                            if (!reads.InitialSnapshotPending) reads.EventRefresh = false;
                            reads.InitialSnapshotPending = false;
                        }
                        else reads.Inspect = false;
                        reads.LastReadWasList = list;
                        reads.LastReadWasOperation = false;
                        revision = list ? reads.ListRevision : reads.DetailRevision;
                        id = imageSnapshot.SelectedImageId;
                        reads.InspectActive = !list;
                        request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token);
                        request.CancelAfter(options.OperationTimeout);
                        reads.ActiveCanceledByUser = false;
                        reads.Active = request;
                    }
                    // A deadline invalidates both families immediately, even if the reader ignores cancellation.
                    using var cancellation = request.Token.Register(() => ImageReadCanceled(reads, request, list));
                    try
                    {
                        if (list)
                            CompleteImageList(reads, request, revision,
                                await reads.Reader!.ListImagesAsync(request.Token).ConfigureAwait(false));
                        else if (id is not null)
                            CompleteImageInspection(reads, request, revision, id,
                                await reads.Reader!.InspectImageAsync(id, request.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                    finally
                    {
                        // A cooperative reader can finish before the deadline callback gets its turn.
                        if (request.IsCancellationRequested) ImageReadCanceled(reads, request, list);
                        lock (gate) { reads.Active = null; reads.InspectActive = false; }
                        request.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
    }

    private bool CurrentImageRequestLocked(ImageReads reads, long revision, bool list)
        => CurrentImagesLocked(reads) && revision == (list ? reads.ListRevision : reads.DetailRevision);

    private void ImageReadCanceled(ImageReads reads, CancellationTokenSource request, bool list)
    {
        lock (gate)
        {
            if (!request.IsCancellationRequested || !CurrentImagesLocked(reads)
                || !ReferenceEquals(reads.Active, request) || reads.ActiveCanceledByUser) return;
            ImageTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                "The image request exceeded its deadline."), !list);
        }
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteImageList(ImageReads reads, CancellationTokenSource request, long revision,
        EngineResourceResult<IReadOnlyList<ImageSummary>> result)
    {
        bool lost = false;
        lock (gate)
        {
            if (request.IsCancellationRequested || !CurrentImageRequestLocked(reads, revision, true)) return;
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
            { ImageTransportLostLocked(reads, result.Failure, false); lost = true; }
            else if (result.Failure is { } failure)
                imageSnapshot = imageSnapshot with { State = ResourceReadState.Failed, Failure = failure };
            else
            {
                var rows = Array.AsReadOnly(result.Value!.DistinctBy(row => row.Id, StringComparer.Ordinal).ToArray());
                imageSnapshot = imageSnapshot with { State = ResourceReadState.Ready, Images = rows, Failure = null };
                if (imageSnapshot.SelectedImageId is { } selected && !rows.Any(row => row.Id == selected))
                    RequestImageInspectionLocked(reads, null, false);
            }
        }
        NotifyImageRead(lost);
    }

    private void CompleteImageInspection(ImageReads reads, CancellationTokenSource request, long revision, string id,
        EngineResourceResult<ImageInspection> result)
    {
        bool lost = false;
        lock (gate)
        {
            if (request.IsCancellationRequested || !CurrentImagesLocked(reads)) return;
            // Obsolete content cannot publish, but current connection continuity evidence remains valid.
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
            { ImageTransportLostLocked(reads, result.Failure, true); lost = true; }
            else if (!CurrentImageRequestLocked(reads, revision, false) || imageSnapshot.SelectedImageId != id) return;
            else if (result.Failure is { } failure)
            {
                imageSnapshot = imageSnapshot with { InspectionState = ResourceReadState.Failed,
                    Inspection = null, InspectionFailure = failure };
                if (failure.Kind == EngineResourceFailureKind.NotFound)
                    imageSnapshot = imageSnapshot with { SelectedImageId = null,
                        Images = Array.AsReadOnly(imageSnapshot.Images.Where(row => row.Id != id).ToArray()) };
            }
            else imageSnapshot = imageSnapshot with { InspectionState = ResourceReadState.Ready,
                Inspection = result.Value, InspectionFailure = null };
        }
        NotifyImageRead(lost);
    }

    private void ImageTransportLostLocked(ImageReads reads, EngineResourceFailure failure, bool inspection)
    {
        // Invalidation cancels events, lifecycle observations and relevant Logs along with container reads.
        InvalidateContainersLocked();
        InvalidateImagesLocked(failure, inspection);
        reads.Containers.TransportLoss.TrySetResult(new(EngineConnectionFailureKind.TransientTransportFailure,
            failure.Description, failure.HttpStatusCode, failure.DaemonMessage));
    }

    private void NotifyImageRead(bool lost)
    {
        if (lost) { NotifyContainerBrowsers(); ContainerOperationChanged?.Invoke(this, EventArgs.Empty); }
        else NotifyImagesChanged();
    }

    private void NotifyImagesChanged()
    {
        lock (gate)
        {
            if (ReferenceEquals(notifiedImageSnapshot, imageSnapshot)) return;
            notifiedImageSnapshot = imageSnapshot;
        }
        NotifyImageObservers(ImagesChanged);
        NotifyImageOperationChanged();
    }

    private void NotifyContainerBrowsers()
    {
        ContainersChanged?.Invoke(this, EventArgs.Empty);
        NotifyImagesChanged();
    }

    private sealed class ImageReads(ContainerReads containers)
    {
        public ContainerReads Containers { get; } = containers;
        public IImageReader? Reader { get; } = containers.Connection as IImageReader;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(containers.Lifetime.Token);
        public Channel<byte> Signal { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        public Task Workflow = Task.CompletedTask;
        public Task OperationWorkflow = Task.CompletedTask;
        public ImageObservationRequest? OperationObservation;
        public bool OperationReadActive, LastReadWasOperation;
        public long ObservationRevision;
        public CancellationTokenSource? Active;
        public bool ActiveCanceledByUser;
        public bool Refresh = true, EventRefresh, Inspect, InspectActive, LastReadWasList;
        public bool InitialSnapshotPending = true;
        public long ListRevision, DetailRevision;
    }
}
