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
            if (!reads.OperationReadActive) reads.Active?.Cancel();
            containerSnapshot = containerSnapshot with { State = ResourceReadState.Loading, Failure = null };
            reads.Signal.Writer.TryWrite(0);
        }
        NotifyContainerBrowsers();
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
        NotifyContainerBrowsers();
    }

    public void RefreshContainerInspection()
    {
        lock (gate)
        {
            if (disposed || containerReads is not { Reader: not null } reads || reads.Lifetime.IsCancellationRequested
                || containerSnapshot.SelectedContainerId is not { } id) return;
            RequestInspectionLocked(reads, id, true);
        }
        NotifyContainerBrowsers();
    }

    public void ClearContainerInspection()
    {
        lock (gate)
        {
            if (disposed || containerReads is not { Reader: not null } reads || reads.Lifetime.IsCancellationRequested) return;
            RequestInspectionLocked(reads, containerSnapshot.SelectedContainerId, false);
        }
        NotifyContainerBrowsers();
    }

    private void RequestInspectionLocked(ContainerReads reads, string? id, bool inspect)
    {
        if (containerSnapshot.SelectedContainerId != id && logsRequest is not null)
            CloseLogsLocked(ContainerLogsPhase.Closed);
        reads.DetailRevision++;
        reads.Inspect = inspect;
        if (reads.InspectActive && !reads.OperationReadActive) reads.Active?.Cancel();
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
            reads = new(connection, token);
            containerReads = reads;
            containerSnapshot = new(++containerGeneration,
                reads.Reader is null ? ResourceReadState.Unavailable : ResourceReadState.Loading,
                Array.Empty<ContainerSummary>(), InspectionState: reads.Reader is null
                    ? ResourceReadState.Unavailable : ResourceReadState.NotRequested);
            if (reads.Reader is not null)
            {
                reads.Workflow = Task.Run(() => ReadContainersAsync(reads));
            }
            reads.EventsWorkflow = Task.Run(() => ObserveContainerEventsAsync(reads));
            ResumeContainerOperationLocked(reads);
        }
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        return reads;
    }

    // Caller holds gate. Invalidation precedes cancellation/disposal and rejects all late publications.
    private void InvalidateContainersLocked(EngineResourceFailure? failure = null, bool inspectionFailure = false)
    {
        var previous = containerReads;
        InvalidateImagesLocked();
        SuspendLogsLocked();
        containerReads = null;
        containerSnapshot = new(containerSnapshot.ConnectionGeneration, ResourceReadState.Unavailable,
            Array.Empty<ContainerSummary>(), Failure: inspectionFailure ? null : failure,
            InspectionFailure: inspectionFailure ? failure : null);
        InvalidateContainerOperationLocked();
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
        if (changed)
        {
            NotifyContainerBrowsers();
            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        }
        // A reader that ignores cancellation must finish before the connection owner disposes it.
        try { await Task.WhenAll(reads.Workflow, reads.EventsWorkflow, reads.OperationWorkflow, reads.LogsWorkflow).ConfigureAwait(false); }
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
                    OperationObservationRequest? operationRead;
                    lock (gate)
                    {
                        if (!CurrentEventsLocked(reads)) return;
                        operationRead = reads.InitialObservationPending || reads.InitialSnapshotPending ? null : reads.OperationObservation;
                        if (operationRead is not null) reads.OperationObservation = null;
                    }
                    if (operationRead is not null)
                    {
                        await ReadOperationObservationAsync(reads, operationRead).ConfigureAwait(false);
                        continue;
                    }
                    bool list;
                    long revision;
                    string? identity;
                    CancellationTokenSource request;
                    bool batch;
                    lock (gate)
                    {
                        if (!ReferenceEquals(containerReads, reads) || reads.Lifetime.IsCancellationRequested) return;
                        batch = !reads.Refresh && reads.EventRefresh && !(reads.Inspect && reads.LastReadWasList);
                    }
                    if (batch) await delay.DelayAsync(reconciliation.BatchInterval, reads.Lifetime.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!ReferenceEquals(containerReads, reads) || reads.Lifetime.IsCancellationRequested) return;
                        if (reads.InitialObservationPending) break;
                        // A command may queue observation between the scheduling locks.
                        if (!reads.InitialSnapshotPending && reads.OperationObservation is not null) continue;
                        if (!reads.Refresh && !reads.EventRefresh && !reads.Inspect) break;
                        // Give requested details a turn even under a continuous list-invalidating stream.
                        list = reads.Refresh || (reads.EventRefresh && !(reads.Inspect && reads.LastReadWasList));
                        if (list)
                        {
                            reads.Refresh = false;
                            // Observations buffered before the initial snapshot starts still need
                            // reconciliation after it. Only subsequent snapshots consume this flag.
                            if (!reads.InitialSnapshotPending) reads.EventRefresh = false;
                            reads.InitialSnapshotPending = false;
                        }
                        else reads.Inspect = false;
                        reads.LastReadWasList = list;
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
                        if (notify)
                        {
                            NotifyContainerBrowsers();
                            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
                        }
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
                ReconcileLogsLocked(reads);
                if (containerSnapshot.SelectedContainerId is { } selected && !rows.Any(row => row.Id == selected))
                    RequestInspectionLocked(reads, null, false);
            }
        }
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
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
                {
                    if (logsRequest?.ContainerId == identity) CloseLogsLocked(ContainerLogsPhase.Failed,
                        new(ContainerLogFailureKind.NotFound, "The container is no longer available."));
                    reads.Inspect = false;
                    containerSnapshot = containerSnapshot with { SelectedContainerId = null,
                        Containers = Array.AsReadOnly(containerSnapshot.Containers.Where(row => row.Id != identity).ToArray()) };
                }
            }
            // An observation during this read already invalidated its content. Finish the I/O but
            // leave the pending authoritative reread to publish; never resurrect stale Inspect.
            else if (!reads.Inspect) containerSnapshot = containerSnapshot with { InspectionState = ResourceReadState.Ready,
                Inspection = result.Value, InspectionFailure = null };
        }
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResourceTransportLostLocked(ContainerReads reads, EngineResourceFailure failure, bool inspection)
    {
        InvalidateContainersLocked(failure, inspection);
        reads.TransportLoss.TrySetResult(new(EngineConnectionFailureKind.TransientTransportFailure,
            failure.Description, failure.HttpStatusCode, failure.DaemonMessage));
    }

    private sealed class ContainerReads(IEngineConnection connection, CancellationToken token)
    {
        public IEngineConnection Connection { get; } = connection;
        public IContainerReader? Reader { get; } = connection as IContainerReader;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public Channel<byte> Signal { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        public TaskCompletionSource<EngineConnectionFailure> TransportLoss { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Workflow = Task.CompletedTask;
        public Task EventsWorkflow = Task.CompletedTask;
        public Task OperationWorkflow = Task.CompletedTask;
        public Task LogsWorkflow = Task.CompletedTask;
        public OperationObservationRequest? OperationObservation;
        public bool OperationReadActive;
        public long ObservationRevision;
        public CancellationTokenSource? Active;
        public bool Refresh, EventRefresh, Inspect, InspectActive, LastReadWasList;
        public bool InitialSnapshotPending = true;
        public bool InitialObservationPending = true;
        public long ListRevision, DetailRevision;
    }
}
