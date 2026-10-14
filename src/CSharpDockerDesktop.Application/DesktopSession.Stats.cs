using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IContainerStats
{
    private readonly ContainerStatsOptions statsOptions;
    private readonly TimeProvider statsTime;
    private readonly CancellationTokenSource statsNotificationLifetime = new();
    private readonly Channel<byte> statsSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private Task statsNotifications = Task.CompletedTask, statsWork = Task.CompletedTask;
    private StatsRequest? statsRequest, runningStatsRequest;
    private ContainerReads? pendingStatsReads;
    private bool statsRunnerActive, statsDirty;
    private long nextStatsSession;
    private ContainerStatsSnapshot statsSnapshot = new(0, 0, ContainerStatsPhase.Closed);

    public event EventHandler? ContainerStatsChanged;
    public ContainerStatsSnapshot ContainerStats
    {
        get
        {
            lock (gate)
            {
                MaintainStatsLocked();
                return statsSnapshot with { RetainedEntries = statsRequest?.History.Count ?? 0,
                    EvictedEntries = statsRequest?.Evicted ?? 0 };
            }
        }
    }

    public bool OpenContainerStats(string containerId) => OpenContainerStats(containerId, out _);
    public bool OpenContainerStats(string containerId, out ContainerStatsFailure? admissionFailure)
    {
        lock (gate)
        {
            admissionFailure = null;
            if (disposed || containerReads is not { } reads || !CurrentEventsLocked(reads)
                || reads.Connection is not IContainerStatsSource || containerSnapshot.State != ResourceReadState.Ready
                || string.IsNullOrEmpty(containerId) || containerSnapshot.SelectedContainerId != containerId)
            {
                admissionFailure = new(ContainerStatsFailureKind.InvalidRequest,
                    "Stats requires the actively selected full container identity and a current capable connection.");
                return false;
            }
            if (!containerSnapshot.Containers.Any(c => c.Id == containerId))
            {
                admissionFailure = new(ContainerStatsFailureKind.NotFound, "The full container identity is unavailable.");
                return false;
            }
            CloseStatsLocked();
            var metadata = reads.Connection.Metadata;
            statsRequest = new(++nextStatsSession, containerId, metadata.Endpoint, metadata.EngineId);
            statsSnapshot = new(statsRequest.Id, 1, ContainerStatsPhase.AwaitingLifecycle, containerId,
                containerSnapshot.ConnectionGeneration, metadata.Endpoint, metadata.EngineId);
            AddStatsBoundaryLocked(statsRequest, ContainerStatsBoundary.Opened);
            if (containerSnapshot.InspectionState == ResourceReadState.NotRequested)
                RequestInspectionLocked(reads, containerId, true);
            ReconcileStatsLocked(reads);
            SignalStatsLocked();
            return true;
        }
    }

    public ContainerStatsHistory ReadContainerStats(long afterEntryId = 0, int maximumEntries = 300)
    {
        if (afterEntryId < 0 || maximumEntries is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        lock (gate)
        {
            MaintainStatsLocked();
            var entries = statsRequest?.History.Where(e => e.Entry.EntryId > afterEntryId)
                .Take(Math.Min(maximumEntries, statsOptions.MaximumReadEntries)).Select(e => e.Entry).ToArray()
                ?? Array.Empty<ContainerStatsHistoryEntry>();
            return new(statsSnapshot.SessionId, statsSnapshot.Revision, statsSnapshot.ContainerId,
                statsSnapshot.ConnectionGeneration, Array.AsReadOnly(entries));
        }
    }

    public Task CloseContainerStatsAsync()
    {
        lock (gate)
        {
            // Join work already owned at this boundary, without waiting for a later new view.
            var completion = runningStatsRequest?.ActiveCompletion?.Task ?? Task.CompletedTask;
            CloseStatsLocked(); return completion;
        }
    }

    private void CloseStatsLocked()
    {
        if (statsRequest is { } request)
        {
            // Fence publication and release retention before cancellation callbacks can reenter.
            statsRequest = null;
            pendingStatsReads = null;
            request.History.Clear(); request.Pending = null;
            statsSnapshot = statsSnapshot with { Phase = ContainerStatsPhase.Closed, Current = null,
                RetainedEntries = 0, LastObservedAt = null, Failure = null, Revision = statsSnapshot.Revision + 1,
                Completion = new(ContainerStatsTermination.Canceled) };
            request.Lifetime.Cancel(); request.Active?.Cancel();
            if (!ReferenceEquals(runningStatsRequest, request)) request.Lifetime.Dispose();
        }
        SignalStatsLocked();
    }

    private void SignalStatsLocked()
    {
        if (disposed) return;
        statsDirty = true;
        if (statsNotifications.IsCompleted) statsNotifications = Task.Run(NotifyStatsAsync);
        statsSignal.Writer.TryWrite(0);
    }

    private async Task NotifyStatsAsync()
    {
        var token = statsNotificationLifetime.Token;
        try
        {
            while (await statsSignal.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                bool active;
                do
                {
                    await Task.Delay(statsOptions.PresentationInterval, statsTime, token).ConfigureAwait(false);
                    bool notify;
                    lock (gate)
                    {
                        if (disposed) return;
                        while (statsSignal.Reader.TryRead(out _)) { }
                        MaintainStatsLocked();
                        if (statsRequest is { Ready: true, Pending: { } pending } request)
                        {
                            request.Pending = null;
                            AddStatsHistoryLocked(request, new(++request.EntryId, request.Continuity,
                                request.LastObservedAt!.Value, pending, ContainerStatsBoundary.None), request.LastRaw);
                            statsSnapshot = statsSnapshot with { Current = pending, Phase = ContainerStatsPhase.Streaming,
                                Revision = statsSnapshot.Revision + 1 };
                            statsDirty = true;
                        }
                        notify = statsDirty; statsDirty = false;
                        active = statsRequest is not null;
                    }
                    if (notify) ContainerStatsChanged?.Invoke(this, EventArgs.Empty);
                } while (active);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void MaintainStatsLocked()
    {
        if (statsRequest is not { } request) return;
        long now = statsTime.GetTimestamp();
        while (request.History.TryPeek(out var first) && statsTime.GetElapsedTime(first.Stamp, now) >= statsOptions.Retention)
        {
            request.History.Dequeue(); ++request.Evicted; statsDirty = true;
            statsSnapshot = statsSnapshot with { Revision = statsSnapshot.Revision + 1 };
        }
        if (request.HasRaw && request.Ready && statsTime.GetElapsedTime(request.LastRaw, now) >= statsOptions.StaleAfter
            && statsSnapshot.Phase is ContainerStatsPhase.Streaming or ContainerStatsPhase.Opening)
        {
            request.Pending = null; request.Calculator.Reset();
            AddStatsBoundaryLocked(request, ContainerStatsBoundary.SampleGap);
            statsSnapshot = statsSnapshot with { Current = null, Phase = ContainerStatsPhase.Stalled,
                Revision = statsSnapshot.Revision + 1 };
            statsDirty = true;
        }
    }

    private void AddStatsHistoryLocked(StatsRequest request, ContainerStatsHistoryEntry entry, long stamp)
    {
        while (request.History.Count >= statsOptions.MaximumEntries) { request.History.Dequeue(); ++request.Evicted; }
        request.History.Enqueue((entry, stamp));
    }

    private void AddStatsBoundaryLocked(StatsRequest request, ContainerStatsBoundary boundary)
    {
        // Adjacent invalidations share one gap. Bursts cannot create per-event history entries.
        if (request.LastBoundary && boundary != ContainerStatsBoundary.Opened) return;
        request.LastBoundary = true;
        ++request.Continuity;
        AddStatsHistoryLocked(request, new(++request.EntryId, request.Continuity, statsTime.GetUtcNow(), null, boundary), statsTime.GetTimestamp());
        statsSnapshot = statsSnapshot with { Continuity = request.Continuity };
    }

    private void InvalidateStatsLifecycleLocked(ContainerReads reads, string? identity = null)
    {
        if (statsRequest is not { Terminal: false } request || !CurrentEventsLocked(reads)
            || (!string.IsNullOrEmpty(identity) && identity != request.ContainerId)) return;
        SuspendStatsLocked(ContainerStatsPhase.AwaitingLifecycle, ContainerStatsBoundary.LifecycleInvalidated);
    }

    private void SuspendStatsLocked(ContainerStatsPhase phase = ContainerStatsPhase.Recovering,
        ContainerStatsBoundary boundary = ContainerStatsBoundary.ConnectionInterrupted)
    {
        if (statsRequest is not { Terminal: false } request) return;
        ++request.Epoch; request.Ready = false; request.Pending = null; request.Calculator.Reset();
        pendingStatsReads = null;
        statsSnapshot = statsSnapshot with { Phase = phase, Current = null, Revision = statsSnapshot.Revision + 1 };
        AddStatsBoundaryLocked(request, boundary);
        request.Active?.Cancel();
        SignalStatsLocked();
    }

    // Called from the existing authoritative resource scheduler only; events merely invalidate.
    private void ReconcileStatsLocked(ContainerReads reads)
    {
        if (statsRequest is not { Terminal: false } request || !CurrentEventsLocked(reads)) return;
        var metadata = reads.Connection.Metadata;
        if (statsSnapshot.ConnectionGeneration != containerSnapshot.ConnectionGeneration)
        {
            if (request.Endpoint != metadata.Endpoint || request.EngineId is null || request.EngineId != metadata.EngineId
                || reads.Connection is not IContainerStatsSource)
            {
                FailStatsLocked(request, new(ContainerStatsFailureKind.TransportLoss,
                    "The Stats Engine identity changed or could not be established.")); return;
            }
            // A new connection is a new observation context: history never mixes Engine generations.
            request.History.Clear(); request.Pending = null; request.Calculator.Reset(); request.HasRaw = false;
            request.LastBoundary = false;
            statsSnapshot = statsSnapshot with { ConnectionGeneration = containerSnapshot.ConnectionGeneration,
                Current = null, LastObservedAt = null, Revision = statsSnapshot.Revision + 1 };
            AddStatsBoundaryLocked(request, ContainerStatsBoundary.ConnectionInterrupted);
            if (containerSnapshot.SelectedContainerId is null) RequestInspectionLocked(reads, request.ContainerId, true);
        }
        var row = containerSnapshot.Containers.FirstOrDefault(c => c.Id == request.ContainerId);
        if (containerSnapshot.State != ResourceReadState.Ready || row is null
            || containerSnapshot.SelectedContainerId != request.ContainerId)
        {
            SuspendStatsLocked(ContainerStatsPhase.Inactive, ContainerStatsBoundary.LifecycleInvalidated); return;
        }
        var inspect = containerSnapshot.Inspection;
        var state = inspect?.State;
        bool valid = string.Equals(row.State, "running", StringComparison.Ordinal)
            && !reads.Refresh && !reads.EventRefresh && !reads.Inspect
            && containerSnapshot.InspectionState == ResourceReadState.Ready && inspect?.Id == request.ContainerId
            && state is { Running: true, Paused: false, Restarting: false, Dead: false, Status: "running" };
        if (!valid)
        {
            SuspendStatsLocked(containerSnapshot.InspectionState == ResourceReadState.Loading
                ? ContainerStatsPhase.AwaitingLifecycle : ContainerStatsPhase.Inactive, ContainerStatsBoundary.LifecycleInvalidated);
            return;
        }
        var lifecycle = (state!.StartedAt, inspect!.RestartCount);
        if (request.Ready && request.Lifecycle == lifecycle) return;
        if (request.Ready) SuspendStatsLocked(ContainerStatsPhase.AwaitingLifecycle, ContainerStatsBoundary.LifecycleInvalidated);
        request.Lifecycle = lifecycle;
        request.Ready = true;
        pendingStatsReads = reads;
        if (!statsRunnerActive)
        {
            statsRunnerActive = true;
            statsWork = Task.Run(RunStatsQueueAsync);
        }
        reads.StatsWorkflow = statsWork;
        statsSnapshot = statsSnapshot with { Phase = ContainerStatsPhase.Opening, Current = null,
            Failure = null, Completion = null, Revision = statsSnapshot.Revision + 1 };
        SignalStatsLocked();
    }

    private async Task RunStatsQueueAsync()
    {
        while (true)
        {
            StatsRequest request; ContainerReads reads; CancellationTokenSource cancellation; long epoch;
            lock (gate)
            {
                if (statsRequest is not { Ready: true, Terminal: false } current || pendingStatsReads is not { } pending
                    || !CurrentEventsLocked(pending))
                { pendingStatsReads = null; statsRunnerActive = false; return; }
                request = current; reads = pending; epoch = request.Epoch; pendingStatsReads = null;
                runningStatsRequest = request;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Lifetime.Token, reads.Lifetime.Token);
                request.Active = cancellation;
                request.ActiveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                request.Calculator.Reset(); request.HasRaw = false;
            }
            try { await RunStatsAsync(reads, request, epoch, cancellation).ConfigureAwait(false); }
            finally
            {
                lock (gate)
                {
                    request.Active = null; runningStatsRequest = null; cancellation.Dispose();
                    request.ActiveCompletion!.TrySetResult();
                    if (request.Lifetime.IsCancellationRequested) request.Lifetime.Dispose();
                }
            }
        }
    }

    private bool CurrentStatsLocked(ContainerReads reads, StatsRequest request, long epoch, CancellationToken token)
        => ReferenceEquals(statsRequest, request) && CurrentEventsLocked(reads) && request.Ready
            && request.Epoch == epoch && !request.Terminal && !token.IsCancellationRequested;

    private async Task RunStatsAsync(ContainerReads reads, StatsRequest request, long epoch, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        IContainerStatsSubscription? subscription = null;
        using var deadline = statsTime.CreateTimer(_ =>
        {
            lock (gate)
            {
                if (!CurrentStatsLocked(reads, request, epoch, token)) return;
                FinishStatsLocked(reads, request, new(ContainerStatsTermination.Failed,
                    new(ContainerStatsFailureKind.OpeningDeadline, "Stats opening exceeded its deadline.")));
                cancellation.Cancel();
            }
        }, null, options.OperationTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            var attempt = await ((IContainerStatsSource)reads.Connection).SubscribeStatsAsync(request.ContainerId, token).ConfigureAwait(false);
            subscription = attempt.Subscription; // Own and dispose even noncooperative late results.
            deadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lock (gate)
            {
                if (!CurrentStatsLocked(reads, request, epoch, token)) return;
                if (subscription is null)
                { FinishStatsLocked(reads, request, new(ContainerStatsTermination.Failed, attempt.Failure)); return; }
                if (subscription.ContainerId != request.ContainerId)
                {
                    FinishStatsLocked(reads, request, new(ContainerStatsTermination.Failed,
                        new(ContainerStatsFailureKind.NotFound, "The requested full container identity is unavailable."))); return;
                }
                // An open stream supplies no lifecycle or freshness evidence.
                request.LastRaw = statsTime.GetTimestamp(); request.HasRaw = true;
            }
            int batch = 0;
            while (await subscription.Samples.WaitToReadAsync(token).ConfigureAwait(false))
            {
                lock (gate)
                {
                    if (!CurrentStatsLocked(reads, request, epoch, token)) return;
                    if (subscription.Samples.TryRead(out var sample))
                    {
                        if (sample.ContainerId != request.ContainerId)
                        {
                            FinishStatsLocked(reads, request, new(ContainerStatsTermination.Failed,
                                new(ContainerStatsFailureKind.MalformedSample, "Stats returned a different container identity."))); return;
                        }
                        MaintainStatsLocked();
                        // Raw continuity must not depend on whether a presentation timer was scheduled.
                        if (request.HasRaw && statsTime.GetElapsedTime(request.LastRaw) >= statsOptions.StaleAfter)
                        {
                            request.Calculator.Reset();
                            AddStatsBoundaryLocked(request, ContainerStatsBoundary.SampleGap);
                        }
                        request.Pending = request.Calculator.Calculate(sample);
                        request.LastRaw = statsTime.GetTimestamp(); request.LastObservedAt = statsTime.GetUtcNow();
                        request.LastBoundary = false;
                        statsSnapshot = statsSnapshot with { LastObservedAt = request.LastObservedAt,
                            ProcessedSamples = statsSnapshot.ProcessedSamples + 1 };
                        SignalStatsLocked();
                    }
                }
                if (++batch == statsOptions.IngestionBatchSize) { batch = 0; await Task.Yield(); }
            }
            var completion = await subscription.Completion.WaitAsync(token).ConfigureAwait(false);
            lock (gate)
                if (CurrentStatsLocked(reads, request, epoch, token)) FinishStatsLocked(reads, request, completion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            lock (gate)
                if (CurrentStatsLocked(reads, request, epoch, token))
                    FinishStatsLocked(reads, request, new(ContainerStatsTermination.Failed,
                        new(ContainerStatsFailureKind.TransportLoss, "The Stats source was interrupted.")));
        }
        finally { if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false); }
    }

    private void FinishStatsLocked(ContainerReads reads, StatsRequest request, ContainerStatsCompletion completion)
    {
        if (request.Pending is { } final)
        {
            AddStatsHistoryLocked(request, new(++request.EntryId, request.Continuity,
                request.LastObservedAt!.Value, final, ContainerStatsBoundary.None), request.LastRaw);
        }
        request.Pending = null; request.Calculator.Reset();
        statsSnapshot = statsSnapshot with { Current = null, Completion = completion, Failure = completion.Failure,
            Phase = completion.Termination == ContainerStatsTermination.Failed ? ContainerStatsPhase.Failed : ContainerStatsPhase.Completed,
            Revision = statsSnapshot.Revision + 1 };
        AddStatsBoundaryLocked(request, ContainerStatsBoundary.StreamEnded);
        if (completion.Failure?.Kind is ContainerStatsFailureKind.TransportLoss or ContainerStatsFailureKind.OpeningDeadline)
        {
            if (request.Recoveries < statsOptions.MaximumRecoveryAttempts) ++request.Recoveries;
            else { request.Terminal = true; request.Ready = false; }
            ResourceTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                "Stats continuity was interrupted."), false);
        }
        else { request.Terminal = true; request.Ready = false; }
        if (completion.Failure?.Kind == ContainerStatsFailureKind.NotFound)
        {
            // Stats is not another resource-state owner. Ask the shared scheduler for fresh evidence.
            reads.EventRefresh = true;
            InvalidateEventDetailLocked(reads, request.ContainerId);
            reads.Signal.Writer.TryWrite(0);
        }
        SignalStatsLocked();
    }

    private void FailStatsLocked(StatsRequest request, ContainerStatsFailure failure)
    {
        request.Terminal = true; request.Ready = false; request.Pending = null; ++request.Epoch;
        statsSnapshot = statsSnapshot with { Current = null, Phase = ContainerStatsPhase.Failed, Failure = failure,
            Completion = new(ContainerStatsTermination.Failed, failure), Revision = statsSnapshot.Revision + 1 };
        request.Active?.Cancel(); SignalStatsLocked();
    }

    private sealed class StatsRequest(long id, string containerId, EngineEndpoint endpoint, string? engineId)
    {
        public long Id { get; } = id;
        public string ContainerId { get; } = containerId;
        public EngineEndpoint Endpoint { get; } = endpoint;
        public string? EngineId { get; } = engineId;
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource? Active;
        public TaskCompletionSource? ActiveCompletion;
        public ContainerStatsCalculator Calculator { get; } = new();
        public Queue<(ContainerStatsHistoryEntry Entry, long Stamp)> History { get; } = new();
        public ContainerStatsMeasurements? Pending;
        public (DateTimeOffset?, long?) Lifecycle;
        public DateTimeOffset? LastObservedAt;
        public long Epoch, Continuity, EntryId, Evicted, LastRaw;
        public bool Ready, Terminal, HasRaw, LastBoundary;
        public int Recoveries;
    }
}
