using System.Collections.ObjectModel;
using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IVolumeBrowser
{
    private VolumeBrowsingSnapshot volumeSnapshot = new(0, ResourceReadState.Unavailable, Array.Empty<VolumeSummary>());
    private VolumeReads? volumeReads;
    private VolumeBrowsingSnapshot? notifiedVolumeSnapshot;

    public event EventHandler? VolumesChanged;
    public VolumeBrowsingSnapshot VolumeSnapshot { get { lock (gate) return volumeSnapshot; } }

    public void RefreshVolumes()
    {
        lock (gate)
        {
            if (volumeReads is not { Reader: not null } reads || !CurrentVolumesLocked(reads)) return;
            ++reads.ListRevision;
            ++reads.DetailRevision;
            reads.Refresh = true;
            if (reads.InspectActive)
                reads.Inspect = volumeSnapshot.SelectedVolumeName is not null
                    && volumeSnapshot.InspectionState == ResourceReadState.Loading;
            reads.Active?.Cancel();
            volumeSnapshot = volumeSnapshot with { State = ResourceReadState.Loading, Failure = null,
                Completeness = VolumeListCompleteness.Unavailable, HasRetainedRows = volumeSnapshot.Volumes.Count > 0 };
            reads.Signal.Writer.TryWrite(0);
        }
        NotifyVolumesChanged();
    }

    public void SelectVolume(string? name)
    {
        lock (gate)
        {
            if (volumeReads is not { Reader: not null } reads || !CurrentVolumesLocked(reads)) return;
            if (name is not null && !volumeSnapshot.Volumes.Any(row => StringComparer.Ordinal.Equals(row.Name, name))) name = null;
            // Reselecting the same identity preserves requested or explicitly cleared details.
            if (StringComparer.Ordinal.Equals(volumeSnapshot.SelectedVolumeName, name)) return;
            RequestVolumeInspectionLocked(reads, name, name is not null);
        }
        NotifyVolumesChanged();
    }

    public void RefreshVolumeInspection()
    {
        lock (gate)
        {
            if (volumeReads is not { Reader: not null } reads || !CurrentVolumesLocked(reads)
                || volumeSnapshot.SelectedVolumeName is not { } name) return;
            RequestVolumeInspectionLocked(reads, name, true);
        }
        NotifyVolumesChanged();
    }

    public void ClearVolumeInspection()
    {
        lock (gate)
        {
            if (volumeReads is not { Reader: not null } reads || !CurrentVolumesLocked(reads)) return;
            RequestVolumeInspectionLocked(reads, volumeSnapshot.SelectedVolumeName, false);
        }
        NotifyVolumesChanged();
    }

    private void RequestVolumeInspectionLocked(VolumeReads reads, string? name, bool inspect)
    {
        ++reads.DetailRevision;
        reads.Inspect = inspect;
        if (reads.InspectActive) reads.Active?.Cancel();
        volumeSnapshot = volumeSnapshot with { SelectedVolumeName = name, Inspection = null,
            InspectionState = inspect ? ResourceReadState.Loading : ResourceReadState.NotRequested, InspectionFailure = null };
        reads.Signal.Writer.TryWrite(0);
    }

    private VolumeReads? BeginVolumeReads(ContainerReads containers)
    {
        VolumeReads reads;
        lock (gate)
        {
            if (!CurrentEventsLocked(containers)) return null;
            reads = new(containers) { EventRefresh = containers.VolumeRefreshPending };
            volumeReads = reads;
            volumeSnapshot = new(containerSnapshot.ConnectionGeneration,
                reads.Reader is null ? ResourceReadState.Unavailable : ResourceReadState.Loading,
                Array.Empty<VolumeSummary>(), InspectionState: reads.Reader is null
                    ? ResourceReadState.Unavailable : ResourceReadState.NotRequested);
            if (reads.Reader is not null)
            {
                reads.Signal.Writer.TryWrite(0);
                reads.Workflow = Task.Run(() => ReadVolumesAsync(reads));
            }
        }
        NotifyVolumesChanged();
        return reads;
    }

    private void RequestVolumeEventSnapshotLocked(ContainerReads containers, bool initial)
    {
        if (!initial) containers.VolumeRefreshPending = true;
        if (volumeReads is not { Reader: not null } reads || reads.Containers != containers) return;
        if (!initial) reads.EventRefresh = true;
        InvalidateVolumeEventDetailLocked(reads, null);
        reads.Signal.Writer.TryWrite(0);
    }

    private bool ObserveVolumeEventLocked(ContainerReads containers, string? identity)
    {
        // Only bounded invalidation state survives the startup race; never retain event attributes.
        containers.VolumeRefreshPending = true;
        if (volumeReads is not { Reader: not null } reads || reads.Containers != containers) return false;
        reads.EventRefresh = true;
        bool changed = InvalidateVolumeEventDetailLocked(reads, UsableVolumeEventIdentity(identity));
        reads.Signal.Writer.TryWrite(0);
        return changed;
    }

    private bool InvalidateVolumeEventDetailLocked(VolumeReads reads, string? identity)
    {
        if (volumeSnapshot.SelectedVolumeName is not { } selected
            || volumeSnapshot.InspectionState == ResourceReadState.NotRequested
            || (identity is not null && !StringComparer.Ordinal.Equals(identity, selected))) return false;
        // Finish owned I/O with its original deadline, but fence every obsolete result.
        ++reads.DetailRevision;
        reads.Inspect = true;
        bool changed = volumeSnapshot.Inspection is not null || volumeSnapshot.InspectionState != ResourceReadState.Loading;
        if (changed) volumeSnapshot = volumeSnapshot with { Inspection = null,
            InspectionState = ResourceReadState.Loading, InspectionFailure = null };
        return changed;
    }

    private static string? UsableVolumeEventIdentity(string? identity)
    {
        // Docker volume Actor.ID is the full volume name, not Attributes["name"].
        // Match the borrowed read capability's usability bounds without changing accepted names.
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 4096 || identity is "." or "..") return null;
        for (int index = 0; index < identity.Length; index++)
        {
            if (char.IsControl(identity[index]) || char.IsLowSurrogate(identity[index])) return null;
            if (char.IsHighSurrogate(identity[index]) && (++index >= identity.Length || !char.IsLowSurrogate(identity[index]))) return null;
        }
        return identity;
    }

    private bool CurrentVolumesLocked(VolumeReads reads)
        => !disposed && ReferenceEquals(volumeReads, reads) && !reads.Lifetime.IsCancellationRequested
            && CurrentEventsLocked(reads.Containers);

    // Fence publication before canceling borrowed work, including non-cooperative readers.
    private void InvalidateVolumesLocked(EngineResourceFailure? failure = null, bool inspection = false)
    {
        var previous = volumeReads;
        volumeReads = null;
        volumeSnapshot = new(volumeSnapshot.ConnectionGeneration, ResourceReadState.Unavailable,
            Array.Empty<VolumeSummary>(), Failure: inspection ? null : failure, InspectionFailure: inspection ? failure : null);
        previous?.Lifetime.Cancel();
    }

    private async Task EndVolumeReadsAsync(VolumeReads reads)
    {
        lock (gate)
        {
            if (ReferenceEquals(volumeReads, reads)) InvalidateVolumesLocked();
            else reads.Lifetime.Cancel();
        }
        try { await reads.Workflow.ConfigureAwait(false); }
        finally { reads.Lifetime.Dispose(); }
    }

    private async Task ReadVolumesAsync(VolumeReads reads)
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
                    string? name;
                    CancellationTokenSource request;
                    // A separate clock still expires when user cancellation is ignored by the reader.
                    using var deadline = new CancellationTokenSource();
                    bool batch;
                    lock (gate)
                    {
                        if (!CurrentVolumesLocked(reads)) return;
                        batch = !reads.Refresh && reads.EventRefresh && !(reads.Inspect && reads.LastReadWasList);
                    }
                    if (batch) await delay.DelayAsync(reconciliation.BatchInterval, reads.Lifetime.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!CurrentVolumesLocked(reads)) return;
                        if (reads.Containers.InitialObservationPending) break;
                        if (!reads.Refresh && !reads.EventRefresh && !reads.Inspect) break;
                        list = (reads.Refresh || reads.EventRefresh) && !(reads.Inspect && reads.LastReadWasList);
                        if (list)
                        {
                            reads.Refresh = false;
                            // Initial/pre-snapshot observations still require a subsequent reread.
                            // Later reads consume only observations already pending at their start.
                            if (!reads.InitialSnapshotPending) reads.EventRefresh = false;
                            reads.InitialSnapshotPending = false;
                        }
                        else reads.Inspect = false;
                        reads.LastReadWasList = list;
                        revision = list ? reads.ListRevision : reads.DetailRevision;
                        name = volumeSnapshot.SelectedVolumeName;
                        reads.InspectActive = !list;
                        request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, deadline.Token);
                        reads.Active = request;
                        reads.ActiveDeadline = deadline;
                        deadline.CancelAfter(options.OperationTimeout);
                    }
                    using var cancellation = deadline.Token.Register(() => VolumeReadDeadline(reads, request, list));
                    try
                    {
                        if (list)
                            CompleteVolumeList(reads, request, revision,
                                await reads.Reader!.ListVolumesAsync(request.Token).ConfigureAwait(false));
                        else if (name is not null)
                            CompleteVolumeInspection(reads, request, revision, name,
                                await reads.Reader!.InspectVolumeAsync(name, request.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                    finally
                    {
                        if (deadline.IsCancellationRequested) VolumeReadDeadline(reads, request, list);
                        lock (gate) { reads.Active = null; reads.ActiveDeadline = null; reads.InspectActive = false; }
                        request.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
    }

    private bool CurrentVolumeRequestLocked(VolumeReads reads, CancellationTokenSource request, long revision, bool list)
        => CurrentVolumesLocked(reads) && !request.IsCancellationRequested
            && reads.ActiveDeadline?.IsCancellationRequested == false
            && revision == (list ? reads.ListRevision : reads.DetailRevision);

    private void VolumeReadDeadline(VolumeReads reads, CancellationTokenSource request, bool list)
    {
        lock (gate)
        {
            if (!CurrentVolumesLocked(reads) || !ReferenceEquals(reads.Active, request)) return;
            VolumeTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                "The volume request exceeded its deadline."), !list);
        }
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteVolumeList(VolumeReads reads, CancellationTokenSource request, long revision,
        EngineResourceResult<VolumeListing> result)
    {
        bool lost = false;
        lock (gate)
        {
            if (!CurrentVolumeRequestLocked(reads, request, revision, true)) return;
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
            { VolumeTransportLostLocked(reads, result.Failure, false); lost = true; }
            else if (result.Failure is { } failure)
                volumeSnapshot = volumeSnapshot with { State = ResourceReadState.Failed, Failure = failure,
                    Completeness = VolumeListCompleteness.Unavailable, HasRetainedRows = volumeSnapshot.Volumes.Count > 0 };
            else
            {
                var listing = CopyVolumeListing(result.Value!);
                bool complete = listing.Volumes is not null && !listing.HasWarnings;
                var current = listing.Volumes?.DistinctBy(row => row.Name, StringComparer.Ordinal).ToArray();
                var names = current?.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
                var retained = complete ? Array.Empty<VolumeSummary>() : volumeSnapshot.Volumes
                    .Where(old => names is null || !names.Contains(old.Name)).ToArray();
                var rows = Array.AsReadOnly((current ?? Array.Empty<VolumeSummary>()).Concat(retained).ToArray());
                volumeSnapshot = volumeSnapshot with { State = listing.Volumes is null ? ResourceReadState.Unavailable : ResourceReadState.Ready,
                    Volumes = rows, LatestListing = listing, Failure = null, HasRetainedRows = retained.Length > 0,
                    Completeness = listing.Volumes is null ? VolumeListCompleteness.Unavailable
                        : complete ? VolumeListCompleteness.Complete : VolumeListCompleteness.Incomplete };
                if (complete && volumeSnapshot.SelectedVolumeName is { } selected
                    && !rows.Any(row => StringComparer.Ordinal.Equals(row.Name, selected)))
                    RequestVolumeInspectionLocked(reads, null, false);
            }
        }
        NotifyVolumeRead(lost);
    }

    private void CompleteVolumeInspection(VolumeReads reads, CancellationTokenSource request, long revision, string name,
        EngineResourceResult<VolumeInspection> result)
    {
        bool lost = false;
        lock (gate)
        {
            if (!CurrentVolumesLocked(reads) || request.IsCancellationRequested
                || reads.ActiveDeadline?.IsCancellationRequested != false) return;
            // Obsolete content remains fenced, but a current transport-loss result still proves
            // that the shared connection cannot be used safely.
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
            { VolumeTransportLostLocked(reads, result.Failure, true); lost = true; }
            else if (!CurrentVolumeRequestLocked(reads, request, revision, false)
                || !StringComparer.Ordinal.Equals(volumeSnapshot.SelectedVolumeName, name)) return;
            else
            {
                var failure = result.Failure;
                if (failure is null && !StringComparer.Ordinal.Equals(result.Value!.Name, name))
                    failure = new(EngineResourceFailureKind.MalformedData, "The volume inspection returned a different identity.");
                if (failure is not null)
                {
                    volumeSnapshot = volumeSnapshot with { InspectionState = ResourceReadState.Failed,
                        Inspection = null, InspectionFailure = failure };
                    if (failure.Kind == EngineResourceFailureKind.NotFound)
                        volumeSnapshot = volumeSnapshot with { SelectedVolumeName = null,
                            Volumes = Array.AsReadOnly(volumeSnapshot.Volumes.Where(row => !StringComparer.Ordinal.Equals(row.Name, name)).ToArray()) };
                }
                else volumeSnapshot = volumeSnapshot with { InspectionState = ResourceReadState.Ready,
                    Inspection = result.Value! with { Labels = CopyVolumeLabels(result.Value.Labels) }, InspectionFailure = null };
            }
        }
        NotifyVolumeRead(lost);
    }

    private static VolumeListing CopyVolumeListing(VolumeListing listing) => new(
        listing.Volumes is null ? null : Array.AsReadOnly(listing.Volumes.Select(row => row with { Labels = CopyVolumeLabels(row.Labels) }).ToArray()),
        listing.Warnings is null ? null : Array.AsReadOnly(listing.Warnings.ToArray()));

    private static IReadOnlyDictionary<string, string>? CopyVolumeLabels(IReadOnlyDictionary<string, string>? labels)
        => labels is null ? null : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(labels, StringComparer.Ordinal));

    private void VolumeTransportLostLocked(VolumeReads reads, EngineResourceFailure failure, bool inspection)
    {
        InvalidateContainersLocked();
        InvalidateVolumesLocked(failure, inspection);
        reads.Containers.TransportLoss.TrySetResult(new(EngineConnectionFailureKind.TransientTransportFailure,
            failure.Description, failure.HttpStatusCode, failure.DaemonMessage));
    }

    private void NotifyVolumeRead(bool lost)
    {
        if (lost) { NotifyContainerBrowsers(); ContainerOperationChanged?.Invoke(this, EventArgs.Empty); }
        else NotifyVolumesChanged();
    }

    private void NotifyVolumesChanged()
    {
        lock (gate)
        {
            if (ReferenceEquals(notifiedVolumeSnapshot, volumeSnapshot)) return;
            notifiedVolumeSnapshot = volumeSnapshot;
        }
        NotifyImageObservers(VolumesChanged);
    }

    private sealed class VolumeReads(ContainerReads containers)
    {
        public ContainerReads Containers { get; } = containers;
        public IVolumeReader? Reader { get; } = containers.Connection as IVolumeReader;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(containers.Lifetime.Token);
        public Channel<byte> Signal { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        public Task Workflow = Task.CompletedTask;
        public CancellationTokenSource? Active;
        public CancellationTokenSource? ActiveDeadline;
        public bool Refresh = true, EventRefresh, Inspect, InspectActive, LastReadWasList;
        public bool InitialSnapshotPending = true;
        public long ListRevision, DetailRevision;
    }
}
