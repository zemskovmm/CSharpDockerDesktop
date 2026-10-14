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
            reads.Refresh = true;
            // Fence detail results predating this refresh, including non-cooperative readers.
            ++reads.DetailRevision;
            if (reads.InspectActive)
                reads.Inspect = imageSnapshot.SelectedImageId is not null
                    && imageSnapshot.InspectionState == ResourceReadState.Loading;
            reads.Active?.Cancel();
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
        if (reads.InspectActive) reads.Active?.Cancel();
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
            reads = new(containers);
            imageReads = reads;
            imageSnapshot = new(containerSnapshot.ConnectionGeneration,
                reads.Reader is null ? ResourceReadState.Unavailable : ResourceReadState.Loading,
                Array.Empty<ImageSummary>(), InspectionState: reads.Reader is null
                    ? ResourceReadState.Unavailable : ResourceReadState.NotRequested);
            if (reads.Reader is not null)
            {
                reads.Signal.Writer.TryWrite(0);
                reads.Workflow = Task.Run(() => ReadImagesAsync(reads));
            }
        }
        NotifyImagesChanged();
        return reads;
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
        try { await reads.Workflow.ConfigureAwait(false); }
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
                    bool list;
                    long revision;
                    string? id;
                    CancellationTokenSource request;
                    lock (gate)
                    {
                        if (!CurrentImagesLocked(reads)) return;
                        if (!reads.Refresh && !reads.Inspect) break;
                        // Alternate when both are pending: repeated refreshes cannot monopolize the scheduler.
                        list = reads.Refresh && !(reads.Inspect && reads.LastReadWasList);
                        if (list) reads.Refresh = false; else reads.Inspect = false;
                        reads.LastReadWasList = list;
                        revision = list ? reads.ListRevision : reads.DetailRevision;
                        id = imageSnapshot.SelectedImageId;
                        reads.InspectActive = !list;
                        request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token);
                        request.CancelAfter(options.OperationTimeout);
                        reads.Active = request;
                    }
                    // A deadline invalidates both families immediately, even if the reader ignores cancellation.
                    using var cancellation = request.Token.Register(() => ImageReadCanceled(reads, request, revision, list));
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
                        if (request.IsCancellationRequested) ImageReadCanceled(reads, request, revision, list);
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

    private void ImageReadCanceled(ImageReads reads, CancellationTokenSource request, long revision, bool list)
    {
        lock (gate)
        {
            if (!request.IsCancellationRequested || !CurrentImageRequestLocked(reads, revision, list)) return;
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
            if (request.IsCancellationRequested || !CurrentImageRequestLocked(reads, revision, false)
                || imageSnapshot.SelectedImageId != id) return;
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
            { ImageTransportLostLocked(reads, result.Failure, true); lost = true; }
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
        ImagesChanged?.Invoke(this, EventArgs.Empty);
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
        public CancellationTokenSource? Active;
        public bool Refresh = true, Inspect, InspectActive, LastReadWasList;
        public long ListRevision, DetailRevision;
    }
}
