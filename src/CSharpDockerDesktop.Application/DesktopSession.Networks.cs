using System.Collections.ObjectModel;
using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : INetworkBrowser
{
    private NetworkBrowsingSnapshot networkSnapshot = new(0, ResourceReadState.Unavailable, Array.Empty<NetworkSummary>());
    private NetworkReads? networkReads;
    private NetworkBrowsingSnapshot? notifiedNetworkSnapshot;

    public event EventHandler? NetworksChanged;
    public NetworkBrowsingSnapshot NetworkSnapshot { get { lock (gate) return networkSnapshot; } }

    public void RefreshNetworks()
    {
        lock (gate)
        {
            if (networkReads is not { Reader: not null } reads || !CurrentNetworksLocked(reads)) return;
            ++reads.ListRevision;
            ++reads.DetailRevision;
            reads.Refresh = true;
            if (reads.InspectActive)
                reads.Inspect = networkSnapshot.SelectedNetworkId is not null
                    && networkSnapshot.InspectionState == ResourceReadState.Loading;
            networkSnapshot = networkSnapshot with { State = ResourceReadState.Loading, Failure = null };
            // Fence and expose the new state before a cancellation callback can reenter.
            if (!reads.OperationReadActive) reads.Active?.Cancel();
            reads.Signal.Writer.TryWrite(0);
        }
        NotifyNetworksChanged();
    }

    public void SelectNetwork(string? id)
    {
        lock (gate)
        {
            if (networkReads is not { Reader: not null } reads || !CurrentNetworksLocked(reads)) return;
            if (id is not null && !networkSnapshot.Networks.Any(row => StringComparer.Ordinal.Equals(row.Id, id))) id = null;
            if (StringComparer.Ordinal.Equals(networkSnapshot.SelectedNetworkId, id)) return;
            RequestNetworkInspectionLocked(reads, id, id is not null);
        }
        NotifyNetworksChanged();
    }

    public void RefreshNetworkInspection()
    {
        lock (gate)
        {
            if (networkReads is not { Reader: not null } reads || !CurrentNetworksLocked(reads)
                || networkSnapshot.SelectedNetworkId is not { } id) return;
            RequestNetworkInspectionLocked(reads, id, true);
        }
        NotifyNetworksChanged();
    }

    public void ClearNetworkInspection()
    {
        lock (gate)
        {
            if (networkReads is not { Reader: not null } reads || !CurrentNetworksLocked(reads)) return;
            RequestNetworkInspectionLocked(reads, networkSnapshot.SelectedNetworkId, false);
        }
        NotifyNetworksChanged();
    }

    private void RequestNetworkInspectionLocked(NetworkReads reads, string? id, bool inspect)
    {
        ++reads.DetailRevision;
        reads.Inspect = inspect;
        networkSnapshot = networkSnapshot with { SelectedNetworkId = id, Inspection = null,
            InspectionState = inspect ? ResourceReadState.Loading : ResourceReadState.NotRequested, InspectionFailure = null };
        if (reads.InspectActive && !reads.OperationReadActive) reads.Active?.Cancel();
        reads.Signal.Writer.TryWrite(0);
    }

    private NetworkReads? BeginNetworkReads(ContainerReads containers)
    {
        NetworkReads reads;
        lock (gate)
        {
            if (!CurrentEventsLocked(containers)) return null;
            reads = new(containers) { EventRefresh = containers.NetworkRefreshPending };
            networkReads = reads;
            networkSnapshot = new(containerSnapshot.ConnectionGeneration,
                reads.Reader is null ? ResourceReadState.Unavailable : ResourceReadState.Loading,
                Array.Empty<NetworkSummary>(), InspectionState: reads.Reader is null
                    ? ResourceReadState.Unavailable : ResourceReadState.NotRequested);
            if (reads.Reader is not null)
            {
                reads.Signal.Writer.TryWrite(0);
                reads.Workflow = Task.Run(() => ReadNetworksAsync(reads));
                ResumeNetworkOperationLocked(reads);
            }
        }
        NotifyNetworksChanged();
        return reads;
    }

    private void RequestNetworkEventSnapshotLocked(ContainerReads containers, bool initial)
    {
        InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.ObservedChange);
        if (!initial) containers.NetworkRefreshPending = true;
        if (networkReads is not { Reader: not null } reads || reads.Containers != containers) return;
        if (!initial) reads.EventRefresh = true;
        ++reads.ObservationRevision;
        // Retained operation evidence requires event-obsolete rows to remain fenced as well.
        if (networkOperation is not null) ++reads.ListRevision;
        InvalidateNetworkEventDetailLocked(reads, null);
        reads.Signal.Writer.TryWrite(0);
    }

    private bool ObserveNetworkEventLocked(ContainerReads containers, string? identity)
    {
        // Only bounded invalidation state survives the startup race; never retain event attributes.
        InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.ObservedChange, id: UsableNetworkEventIdentity(identity));
        containers.NetworkRefreshPending = true;
        if (networkReads is not { Reader: not null } reads || reads.Containers != containers) return false;
        reads.EventRefresh = true;
        ++reads.ObservationRevision;
        // Retained operation evidence requires event-obsolete rows to remain fenced as well.
        if (networkOperation is not null) ++reads.ListRevision;
        bool changed = InvalidateNetworkEventDetailLocked(reads, UsableNetworkEventIdentity(identity));
        reads.Signal.Writer.TryWrite(0);
        return changed;
    }

    private bool InvalidateNetworkEventDetailLocked(NetworkReads reads, string? identity)
    {
        if (networkSnapshot.SelectedNetworkId is not { } selected
            || networkSnapshot.InspectionState == ResourceReadState.NotRequested
            || (identity is not null && !StringComparer.Ordinal.Equals(identity, selected))) return false;
        // Finish owned I/O with its original deadline, but fence every obsolete result.
        ++reads.DetailRevision;
        reads.Inspect = true;
        bool changed = networkSnapshot.Inspection is not null || networkSnapshot.InspectionState != ResourceReadState.Loading;
        if (changed) networkSnapshot = networkSnapshot with { Inspection = null,
            InspectionState = ResourceReadState.Loading, InspectionFailure = null };
        return changed;
    }

    private static string? UsableNetworkEventIdentity(string? identity)
    {
        // Docker network Actor.ID is the complete opaque identity, not Attributes["name"].
        // Match the borrowed read capability's usability bounds without narrowing opaque network IDs.
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 4096 || identity is "." or "..") return null;
        for (int index = 0; index < identity.Length; index++)
        {
            if (char.IsControl(identity[index]) || char.IsLowSurrogate(identity[index])) return null;
            if (char.IsHighSurrogate(identity[index]) && (++index >= identity.Length || !char.IsLowSurrogate(identity[index]))) return null;
        }
        return identity;
    }

    private bool CurrentNetworksLocked(NetworkReads reads)
        => !disposed && ReferenceEquals(networkReads, reads) && !reads.Lifetime.IsCancellationRequested
            && CurrentEventsLocked(reads.Containers);

    private void InvalidateNetworksLocked(EngineResourceFailure? failure = null, bool inspection = false)
    {
        InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.ConnectionChanged, cancel: true);
        var previous = networkReads;
        networkReads = null;
        InvalidateNetworkOperationLocked();
        networkSnapshot = new(networkSnapshot.ConnectionGeneration, ResourceReadState.Unavailable,
            Array.Empty<NetworkSummary>(), Failure: inspection ? null : failure, InspectionFailure: inspection ? failure : null);
        previous?.Lifetime.Cancel();
    }

    private async Task EndNetworkReadsAsync(NetworkReads reads)
    {
        lock (gate)
        {
            if (ReferenceEquals(networkReads, reads)) InvalidateNetworksLocked();
            else reads.Lifetime.Cancel();
        }
        // Keep ownership even when a reader ignores cancellation; never dispose its borrowed transport early.
        try { await Task.WhenAll(reads.Workflow, reads.NetworkPreparationWorkflow, reads.OperationWorkflow).ConfigureAwait(false); }
        finally { reads.Lifetime.Dispose(); }
    }

    private async Task ReadNetworksAsync(NetworkReads reads)
    {
        try
        {
            while (await reads.Signal.Reader.WaitToReadAsync(reads.Lifetime.Token).ConfigureAwait(false))
            {
                reads.Signal.Reader.TryRead(out _);
                while (true)
                {
                    NetworkObservationRequest? operationRead;
                    lock (gate)
                    {
                        if (!CurrentNetworksLocked(reads)) return;
                        operationRead = reads.Containers.InitialObservationPending || reads.InitialSnapshotPending
                            || reads.LastReadWasOperation && (reads.Refresh || reads.EventRefresh || reads.Inspect)
                            ? null : reads.OperationObservation;
                        if (operationRead is not null) reads.OperationObservation = null;
                    }
                    if (operationRead is not null)
                    {
                        await ReadNetworkObservationAsync(reads, operationRead).ConfigureAwait(false);
                        continue;
                    }
                    bool list;
                    long revision;
                    string? id;
                    CancellationTokenSource request;
                    // An independent clock still expires if superseding cancellation is ignored.
                    using var deadline = new CancellationTokenSource();
                    bool batch;
                    lock (gate)
                    {
                        if (!CurrentNetworksLocked(reads)) return;
                        batch = !reads.Refresh && reads.EventRefresh && !(reads.Inspect && reads.LastReadWasList);
                    }
                    if (batch) await delay.DelayAsync(reconciliation.BatchInterval, reads.Lifetime.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!CurrentNetworksLocked(reads)) return;
                        if (reads.Containers.InitialObservationPending) break;
                        if (!reads.InitialSnapshotPending && reads.OperationObservation is not null
                            && (!reads.LastReadWasOperation || !(reads.Refresh || reads.EventRefresh || reads.Inspect))) continue;
                        if (!reads.Refresh && !reads.EventRefresh && !reads.Inspect) break;
                        // One pending bit per family; alternate turns under sustained refresh/selection bursts.
                        list = (reads.Refresh || reads.EventRefresh) && !(reads.Inspect && reads.LastReadWasList);
                        if (list)
                        {
                            reads.Refresh = false;
                            // Preserve pre-snapshot observations for one authoritative follow-up.
                            // Later reads consume only observations pending at admission.
                            if (!reads.InitialSnapshotPending) reads.EventRefresh = false;
                            reads.InitialSnapshotPending = false;
                        }
                        else reads.Inspect = false;
                        reads.LastReadWasList = list;
                        reads.LastReadWasOperation = false;
                        revision = list ? reads.ListRevision : reads.DetailRevision;
                        id = networkSnapshot.SelectedNetworkId;
                        reads.InspectActive = !list;
                        request = CancellationTokenSource.CreateLinkedTokenSource(reads.Lifetime.Token, deadline.Token);
                        reads.Active = request;
                        reads.ActiveDeadline = deadline;
                        deadline.CancelAfter(options.OperationTimeout);
                    }
                    using var cancellation = deadline.Token.Register(() => NetworkReadDeadline(reads, request, list));
                    try
                    {
                        if (list)
                            CompleteNetworkList(reads, request, revision,
                                await reads.Reader!.ListNetworksAsync(request.Token).ConfigureAwait(false));
                        else if (id is not null)
                            CompleteNetworkInspection(reads, request, revision, id,
                                await reads.Reader!.InspectNetworkAsync(id, request.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                    finally
                    {
                        if (deadline.IsCancellationRequested) NetworkReadDeadline(reads, request, list);
                        lock (gate) { reads.Active = null; reads.ActiveDeadline = null; reads.InspectActive = false; }
                        request.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (reads.Lifetime.IsCancellationRequested) { }
    }

    private bool CurrentNetworkRequestLocked(NetworkReads reads, CancellationTokenSource request, long revision, bool list)
        => CurrentNetworksLocked(reads) && !request.IsCancellationRequested
            && reads.ActiveDeadline?.IsCancellationRequested == false
            && revision == (list ? reads.ListRevision : reads.DetailRevision);

    private void NetworkReadDeadline(NetworkReads reads, CancellationTokenSource request, bool list)
    {
        lock (gate)
        {
            if (!CurrentNetworksLocked(reads) || !ReferenceEquals(reads.Active, request)) return;
            var failure = new EngineResourceFailure(EngineResourceFailureKind.TransportLoss,
                "The network request exceeded its deadline.");
            if (reads.OperationReadActive && networkOperation is { Pending: true } current)
                networkOperation = current with { ObservationFailure = failure, ObservationInvalidated = true,
                    AttachmentObservationState = current.CommandOutcome?.Failure?.Kind == NetworkCommandFailureKind.Conflict
                        ? NetworkAttachmentObservationState.Failed : current.AttachmentObservationState };
            NetworkTransportLostLocked(reads, failure, !list);
        }
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteNetworkList(NetworkReads reads, CancellationTokenSource request, long revision,
        EngineResourceResult<IReadOnlyList<NetworkSummary>> result)
    {
        bool lost = false;
        lock (gate)
        {
            if (!CurrentNetworkRequestLocked(reads, request, revision, true)) return;
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
            { NetworkTransportLostLocked(reads, result.Failure, false); lost = true; }
            else if (result.Failure is { } failure)
                networkSnapshot = networkSnapshot with { State = ResourceReadState.Failed, Failure = failure };
            else
            {
                ApplyNetworkListingLocked(reads, result.Value!);
            }
        }
        NotifyNetworkRead(lost);
    }

    private static IReadOnlyList<NetworkSummary> CopyNetworkListing(IReadOnlyList<NetworkSummary> value)
        => Array.AsReadOnly(value.DistinctBy(row => row.Id, StringComparer.Ordinal)
            .Select(row => row with { Labels = CopyNetworkMap(row.Labels) }).ToArray());

    private void ApplyNetworkListingLocked(NetworkReads reads, IReadOnlyList<NetworkSummary> value)
    {
        var rows = CopyNetworkListing(value);
        networkSnapshot = networkSnapshot with { State = ResourceReadState.Ready, Networks = rows, Failure = null };
        if (networkPreparation is { } preparation && ReferenceEquals(preparation.Reads, reads))
        {
            var row = rows.FirstOrDefault(row => StringComparer.Ordinal.Equals(row.Id, preparation.Snapshot.Target.Id));
            if (row is null) InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.TargetAbsent);
            else if (row.Name is not null &&
                ((preparation.ListedName is not null && !StringComparer.Ordinal.Equals(row.Name, preparation.ListedName))
                || (preparation.Snapshot.Inspection is { } evidence && !StringComparer.Ordinal.Equals(row.Name, evidence.Name))))
                InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.ObservedChange);
        }
        if (networkSnapshot.SelectedNetworkId is { } selected
            && !rows.Any(row => StringComparer.Ordinal.Equals(row.Id, selected)))
            RequestNetworkInspectionLocked(reads, null, false);
    }

    private void CompleteNetworkInspection(NetworkReads reads, CancellationTokenSource request, long revision, string id,
        EngineResourceResult<NetworkInspection> result)
    {
        bool lost = false;
        lock (gate)
        {
            if (!CurrentNetworksLocked(reads) || request.IsCancellationRequested
                || reads.ActiveDeadline?.IsCancellationRequested != false) return;
            // Content supersession cannot erase actual current-connection continuity evidence.
            if (result.Failure?.Kind == EngineResourceFailureKind.TransportLoss)
            { NetworkTransportLostLocked(reads, result.Failure, true); lost = true; }
            else if (!CurrentNetworkRequestLocked(reads, request, revision, false)
                || !StringComparer.Ordinal.Equals(networkSnapshot.SelectedNetworkId, id)) return;
            else
            {
                var failure = result.Failure;
                if (failure is null && !StringComparer.Ordinal.Equals(result.Value!.Id, id))
                    failure = new(EngineResourceFailureKind.MalformedData, "The network inspection returned a different identity.");
                if (failure is not null)
                {
                    networkSnapshot = networkSnapshot with { InspectionState = ResourceReadState.Failed,
                        Inspection = null, InspectionFailure = failure };
                    if (failure.Kind == EngineResourceFailureKind.NotFound)
                    {
                        InvalidateNetworkPreparationLocked(NetworkPreparationFailureKind.TargetAbsent, id: id);
                        networkSnapshot = networkSnapshot with { SelectedNetworkId = null,
                            Networks = Array.AsReadOnly(networkSnapshot.Networks.Where(row => !StringComparer.Ordinal.Equals(row.Id, id)).ToArray()) };
                    }
                }
                else networkSnapshot = networkSnapshot with { InspectionState = ResourceReadState.Ready,
                    Inspection = CopyNetworkInspection(result.Value!), InspectionFailure = null };
            }
        }
        NotifyNetworkRead(lost);
    }

    private static IReadOnlyDictionary<string, T>? CopyNetworkMap<T>(IReadOnlyDictionary<string, T>? values)
        => values is null ? null : new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(values, StringComparer.Ordinal));

    private static NetworkInspection CopyNetworkInspection(NetworkInspection value) => value with
    {
        Labels = CopyNetworkMap(value.Labels), Containers = CopyNetworkMap(value.Containers),
        Ipam = value.Ipam is not { } ipam ? null : ipam with
        {
            Options = CopyNetworkMap(ipam.Options),
            Config = ipam.Config is null ? null : Array.AsReadOnly(ipam.Config.Select(config => config with
                { AuxiliaryAddresses = CopyNetworkMap(config.AuxiliaryAddresses) }).ToArray())
        }
    };

    private void NetworkTransportLostLocked(NetworkReads reads, EngineResourceFailure failure, bool inspection)
    {
        InvalidateContainersLocked();
        InvalidateNetworksLocked(failure, inspection);
        reads.Containers.TransportLoss.TrySetResult(new(EngineConnectionFailureKind.TransientTransportFailure,
            failure.Description, failure.HttpStatusCode, failure.DaemonMessage));
    }

    private void NotifyNetworkRead(bool lost)
    {
        if (lost) { NotifyContainerBrowsers(); ContainerOperationChanged?.Invoke(this, EventArgs.Empty); }
        else NotifyNetworksChanged();
    }

    private void NotifyNetworksChanged()
    {
        NotifyNetworkPreparationChanged();
        NotifyNetworkOperationChanged();
        lock (gate)
        {
            if (ReferenceEquals(notifiedNetworkSnapshot, networkSnapshot)) return;
            notifiedNetworkSnapshot = networkSnapshot;
        }
        NotifyImageObservers(NetworksChanged);
    }

    private sealed class NetworkReads(ContainerReads containers)
    {
        public ContainerReads Containers { get; } = containers;
        public INetworkReader? Reader { get; } = containers.Connection as INetworkReader;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(containers.Lifetime.Token);
        public Channel<byte> Signal { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        public Task Workflow = Task.CompletedTask;
        public Task OperationWorkflow = Task.CompletedTask;
        public NetworkObservationRequest? OperationObservation;
        public bool OperationReadActive, LastReadWasOperation;
        public long ObservationRevision;
        public Task NetworkPreparationWorkflow = Task.CompletedTask;
        public CancellationTokenSource? Active, ActiveDeadline;
        public bool Refresh = true, EventRefresh, Inspect, InspectActive, LastReadWasList;
        public bool InitialSnapshotPending = true;
        public long ListRevision, DetailRevision;
    }
}
