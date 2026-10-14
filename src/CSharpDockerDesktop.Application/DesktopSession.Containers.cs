using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IContainerBrowser
{
    private ContainerBrowsingSnapshot containerSnapshot = new(0, ResourceReadState.Unavailable, Array.Empty<ContainerSummary>());
    private ContainerReads? containerReads;
    private long containerGeneration;

    public event EventHandler? ContainersChanged;
    public ContainerBrowsingSnapshot ContainerSnapshot { get { lock (gate) return containerSnapshot; } }

    public void RefreshContainers()
    {
        lock (gate)
        {
            if (containerReads is not { Reader: not null } reads || disposed || reads.Lifetime.IsCancellationRequested) return;
            reads.ListRevision++;
            reads.Refresh = true;
            // Refresh takes priority, but reload a canceled current inspection after the new list.
            if (reads.InspectActive)
            {
                reads.DetailRevision++;
                reads.Inspect = containerSnapshot.SelectedContainerId is not null
                    && containerSnapshot.InspectionState == ResourceReadState.Loading;
            }
            reads.Active?.Cancel();
            containerSnapshot = containerSnapshot with { State = ResourceReadState.Loading, Failure = null };
            reads.Signal.Writer.TryWrite(0);
        }
        ContainersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectContainer(string? containerId)
    {
        lock (gate)
        {
            if (disposed || containerReads is not { Reader: not null } reads || reads.Lifetime.IsCancellationRequested) return;
            if (containerId is not null && !containerSnapshot.Containers.Any(row => row.Id == containerId))
                containerId = null;
            RequestInspectionLocked(reads, containerId, containerId is not null);
        }
        ContainersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshContainerInspection()
    {
        lock (gate)
        {
            if (disposed || containerReads is not { Reader: not null } reads || reads.Lifetime.IsCancellationRequested
                || containerSnapshot.SelectedContainerId is not { } id) return;
            RequestInspectionLocked(reads, id, true);
        }
        ContainersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearContainerInspection()
    {
        lock (gate)
        {
            if (disposed || containerReads is not { Reader: not null } reads || reads.Lifetime.IsCancellationRequested) return;
            RequestInspectionLocked(reads, containerSnapshot.SelectedContainerId, false);
        }
        ContainersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RequestInspectionLocked(ContainerReads reads, string? id, bool inspect)
    {
        reads.DetailRevision++;
        reads.Inspect = inspect;
        if (reads.InspectActive) reads.Active?.Cancel();
        containerSnapshot = containerSnapshot with { SelectedContainerId = id, Inspection = null,
            InspectionState = inspect ? ResourceReadState.Loading : ResourceReadState.NotRequested, InspectionFailure = null };
        reads.Signal.Writer.TryWrite(0);
    }

    private ContainerReads? BeginContainerReads(long currentGeneration, IEngineConnection connection, CancellationToken token)
    {
        ContainerReads reads;
        lock (gate)
        {
            if (disposed || generation != currentGeneration || token.IsCancellationRequested) return null;
            reads = new(connection as IContainerReader, token);
            containerReads = reads;
            containerSnapshot = new(++containerGeneration,
                reads.Reader is null ? ResourceReadState.Unavailable : ResourceReadState.Loading,
                Array.Empty<ContainerSummary>(), InspectionState: reads.Reader is null
                    ? ResourceReadState.Unavailable : ResourceReadState.NotRequested);
            if (reads.Reader is not null)
            {
                reads.Refresh = true;
                reads.Signal.Writer.TryWrite(0);
                reads.Workflow = Task.Run(() => ReadContainersAsync(reads));
            }
        }
        ContainersChanged?.Invoke(this, EventArgs.Empty);
        return reads;
    }

    // Caller holds gate. Invalidation precedes cancellation/disposal and rejects all late publications.
    private void InvalidateContainersLocked(EngineResourceFailure? failure = null, bool inspectionFailure = false)
    {
        var previous = containerReads;
        containerReads = null;
        containerSnapshot = new(containerSnapshot.ConnectionGeneration, ResourceReadState.Unavailable,
            Array.Empty<ContainerSummary>(), Failure: inspectionFailure ? null : failure,
            InspectionFailure: inspectionFailure ? failure : null);
        previous?.Lifetime.Cancel();
    }

    private async Task EndContainerReadsAsync(ContainerReads reads)
    {
        bool changed;
        lock (gate)
        {
            changed = ReferenceEquals(containerReads, reads);
            if (changed) InvalidateContainersLocked();
            else reads.Lifetime.Cancel();
        }
        if (changed) ContainersChanged?.Invoke(this, EventArgs.Empty);
        // A reader that ignores cancellation must finish before the connection owner disposes it.
        try { await reads.Workflow.ConfigureAwait(false); }
        finally { reads.Lifetime.Dispose(); }
    }

    private async Task ReadContainersAsync(ContainerReads reads)
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
                    string? identity;
                    CancellationTokenSource request;
                    lock (gate)
                    {
                        if (!ReferenceEquals(containerReads, reads) || reads.Lifetime.IsCancellationRequested) return;
                        if (!reads.Refresh && !reads.Inspect) break;
                        list = reads.Refresh;
                        if (list) reads.Refresh = false; else reads.Inspect = false;
                        revision = list ? reads.ListRevision : reads.DetailRevision;
                        identity = containerSnapshot.SelectedContainerId;
                        reads.InspectActive = !list;
                        request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token);
                        request.CancelAfter(options.OperationTimeout);
                        reads.Active = request;
                    }
                    try
                    {
                        if (list)
                        {
                            var result = await reads.Reader!.ListContainersAsync(request.Token).ConfigureAwait(false);
                            CompleteContainerRead(reads, request, revision, result);
                        }
                        else if (identity is not null)
                        {
                            var result = await reads.Reader!.InspectContainerAsync(identity, request.Token).ConfigureAwait(false);
                            CompleteInspectionRead(reads, request, revision, identity, result);
                        }
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                    finally
                    {
                        // Only a still-current read canceled by its deadline becomes a continuity failure.
                        bool notify = false;
                        lock (gate)
                        {
                            if (ReferenceEquals(containerReads, reads) && !reads.Lifetime.IsCancellationRequested
                                && revision == (list ? reads.ListRevision : reads.DetailRevision) && request.IsCancellationRequested)
                            {
                                ResourceTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                                    "The container request exceeded its deadline."), !list);
                                notify = true;
                            }
                            reads.Active = null;
                            reads.InspectActive = false;
                            request.Dispose();
                        }
                        if (notify) ContainersChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
    }

    private bool IsCurrentRead(ContainerReads reads, CancellationTokenSource request, long revision, bool list)
        => ReferenceEquals(containerReads, reads) && !request.IsCancellationRequested && !disposed
            && revision == (list ? reads.ListRevision : reads.DetailRevision);

    private void CompleteContainerRead(ContainerReads reads, CancellationTokenSource request, long revision,
        EngineResourceResult<IReadOnlyList<ContainerSummary>> result)
    {
        lock (gate)
        {
            if (!IsCurrentRead(reads, request, revision, true)) return;
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
                ResourceTransportLostLocked(reads, result.Failure, false);
            else if (result.Failure is { } failure)
                containerSnapshot = containerSnapshot with { State = ResourceReadState.Failed, Failure = failure };
            else
            {
                var rows = Array.AsReadOnly(result.Value!.DistinctBy(row => row.Id, StringComparer.Ordinal).ToArray());
                containerSnapshot = containerSnapshot with { State = ResourceReadState.Ready, Containers = rows, Failure = null };
                if (containerSnapshot.SelectedContainerId is { } selected && !rows.Any(row => row.Id == selected))
                    RequestInspectionLocked(reads, null, false);
            }
        }
        ContainersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteInspectionRead(ContainerReads reads, CancellationTokenSource request, long revision, string identity,
        EngineResourceResult<ContainerInspection> result)
    {
        lock (gate)
        {
            if (!IsCurrentRead(reads, request, revision, false)) return;
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
                ResourceTransportLostLocked(reads, result.Failure, true);
            else if (result.Failure is { } failure)
            {
                containerSnapshot = containerSnapshot with { InspectionState = ResourceReadState.Failed,
                    Inspection = null, InspectionFailure = failure };
                if (failure.Kind == EngineResourceFailureKind.NotFound)
                    containerSnapshot = containerSnapshot with { SelectedContainerId = null,
                        Containers = Array.AsReadOnly(containerSnapshot.Containers.Where(row => row.Id != identity).ToArray()) };
            }
            else containerSnapshot = containerSnapshot with { InspectionState = ResourceReadState.Ready,
                Inspection = result.Value, InspectionFailure = null };
        }
        ContainersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResourceTransportLostLocked(ContainerReads reads, EngineResourceFailure failure, bool inspection)
    {
        InvalidateContainersLocked(failure, inspection);
        reads.TransportLoss.TrySetResult(new(EngineConnectionFailureKind.TransientTransportFailure,
            failure.Description, failure.HttpStatusCode, failure.DaemonMessage));
    }

    private sealed class ContainerReads(IContainerReader? reader, CancellationToken token)
    {
        public IContainerReader? Reader { get; } = reader;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public Channel<byte> Signal { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        public TaskCompletionSource<EngineConnectionFailure> TransportLoss { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Workflow = Task.CompletedTask;
        public CancellationTokenSource? Active;
        public bool Refresh, Inspect, InspectActive;
        public long ListRevision, DetailRevision;
    }
}
