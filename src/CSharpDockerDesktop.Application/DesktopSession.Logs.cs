using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IContainerLogs
{
    private readonly ContainerLogsOptions logOptions;
    private readonly CancellationTokenSource logNotificationLifetime = new();
    private readonly Channel<byte> logSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private Task logNotifications = Task.CompletedTask;
    private Task logsWork = Task.CompletedTask;
    private LogsRequest? logsRequest, runningLogsRequest;
    private ContainerReads? pendingLogReads;
    private bool logsRunnerActive;
    private long nextLogsSession;
    private ContainerLogsSnapshot logsSnapshot = new(0, 0, ContainerLogsPhase.Closed);

    public event EventHandler? ContainerLogsChanged;
    public ContainerLogsSnapshot ContainerLogs
    {
        get { lock (gate) return ReadLogsSnapshotLocked(); }
    }

    public bool OpenContainerLogs(string containerId, bool follow = true, int? tailLines = null)
        => OpenContainerLogs(containerId, out _, follow, tailLines);

    public bool OpenContainerLogs(string containerId, out ContainerLogFailure? admissionFailure,
        bool follow = true, int? tailLines = null)
    {
        lock (gate)
        {
            admissionFailure = null;
            int tail = tailLines ?? logOptions.InitialTailLines;
            if (tail < 0 || tail > logOptions.MaximumTailLines || string.IsNullOrEmpty(containerId))
            {
                admissionFailure = new(ContainerLogFailureKind.InvalidRequest, "Supply a full container identity and a finite valid tail.");
                return false;
            }
            if (disposed || containerReads is not { } reads || !CurrentEventsLocked(reads)
                || reads.Connection is not IContainerLogSource || containerSnapshot.State != ResourceReadState.Ready)
            {
                admissionFailure = new(ContainerLogFailureKind.InvalidRequest, "Logs requires a current authoritative container snapshot and a Logs-capable connection.");
                return false;
            }
            if (!containerSnapshot.Containers.Any(c => c.Id == containerId))
            {
                admissionFailure = new(ContainerLogFailureKind.NotFound, "The full container identity is absent from the current snapshot.");
                return false;
            }
            CloseLogsLocked(ContainerLogsPhase.Canceled);
            var metadata = reads.Connection.Metadata;
            logsRequest = new(++nextLogsSession, containerId, follow, tail, metadata.Endpoint, metadata.EngineId, logOptions);
            logsSnapshot = new(logsRequest.Id, 0, ContainerLogsPhase.Opening, containerId,
                containerSnapshot.ConnectionGeneration, metadata.Endpoint, metadata.EngineId);
            StartLogsLocked(reads, logsRequest);
            SignalLogsLocked();
            return true;
        }
    }

    public ContainerLogsPage ReadContainerLogs(long afterEntryId = 0, int maximumEntries = 128)
    {
        if (maximumEntries is < 1 or > 1024 || afterEntryId < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        lock (gate) return new(logsSnapshot.SessionId, logsSnapshot.ViewRevision,
            logsRequest?.Buffer.Read(afterEntryId, Math.Min(maximumEntries, logOptions.MaximumPageEntries)) ?? Array.Empty<ContainerLogEntry>());
    }

    public void ClearContainerLogs()
    {
        lock (gate)
        {
            if (disposed || logsRequest is not { } request) return;
            request.Buffer.Clear();
            logsSnapshot = logsSnapshot with { ViewRevision = logsSnapshot.ViewRevision + 1 };
            SignalLogsLocked();
        }
    }

    public Task CloseContainerLogsAsync()
    {
        lock (gate) { CloseLogsLocked(ContainerLogsPhase.Closed); return logsWork; }
    }

    private void CloseLogsLocked(ContainerLogsPhase phase, ContainerLogFailure? failure = null)
    {
        if (logsRequest is { } request)
        {
            // Capture immutable final metadata before removing ownership and canceling.
            logsSnapshot = ReadLogsSnapshotLocked() with { Phase = phase, Failure = failure,
                Completion = new(failure is null ? ContainerLogTermination.Canceled : ContainerLogTermination.Failed, failure),
                RetainedEntries = 0, RetainedCharacters = 0, ViewRevision = logsSnapshot.ViewRevision + 1 };
            logsRequest = null;
            request.Lifetime.Cancel();
            request.Active?.Cancel();
            pendingLogReads = null;
            if (!ReferenceEquals(runningLogsRequest, request)) request.Lifetime.Dispose();
        }
        else if (phase == ContainerLogsPhase.Closed) logsSnapshot = logsSnapshot with { Phase = phase };
        SignalLogsLocked();
    }

    private ContainerLogsSnapshot ReadLogsSnapshotLocked()
    {
        if (logsRequest is not { } request) return logsSnapshot;
        var buffer = request.Buffer;
        return logsSnapshot with { RetainedEntries = buffer.Count, RetainedCharacters = buffer.Characters,
            EvictedEntries = buffer.Evictions, DiscardedCharacters = buffer.Discarded,
            Continuity = buffer.Continuity, LastObservedCursor = buffer.Cursor, RecoveryAttempt = request.Attempts };
    }

    private void SignalLogsLocked()
    {
        if (disposed) return;
        if (logNotifications.IsCompleted)
            logNotifications = Task.Run(NotifyLogsAsync);
        logSignal.Writer.TryWrite(0);
    }

    private async Task NotifyLogsAsync()
    {
        var token = logNotificationLifetime.Token;
        try
        {
            while (await logSignal.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                await Task.Delay(logOptions.NotificationInterval, token).ConfigureAwait(false);
                while (logSignal.Reader.TryRead(out _)) { }
                lock (gate)
                {
                    if (disposed) return;
                    if (logsRequest?.Buffer.PublishUnfinished() == true)
                        logsSnapshot = logsSnapshot with { ViewRevision = logsSnapshot.ViewRevision + 1 };
                }
                // Only a signal is queued. Observers always read the current immutable state/page.
                ContainerLogsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void StartLogsLocked(ContainerReads reads, LogsRequest request)
    {
        if (request.Active is not null || request.Lifetime.IsCancellationRequested) return;
        // One runner and one latest pending request: rapid replacements never create a task backlog.
        pendingLogReads = reads;
        if (!logsRunnerActive)
        {
            logsRunnerActive = true;
            logsWork = Task.Run(RunLogsQueueAsync);
        }
        reads.LogsWorkflow = logsWork;
    }

    private async Task RunLogsQueueAsync()
    {
        while (true)
        {
            ContainerReads reads;
            LogsRequest request;
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (logsRequest is not { } current || pendingLogReads is not { } pending
                    || !CurrentEventsLocked(pending) || current.Lifetime.IsCancellationRequested)
                {
                    pendingLogReads = null;
                    logsRunnerActive = false;
                    return;
                }
                request = current; reads = pending; pendingLogReads = null;
                runningLogsRequest = request;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Lifetime.Token, reads.Lifetime.Token);
                request.Active = cancellation;
            }
            try { await RunLogsAsync(reads, request, cancellation).ConfigureAwait(false); }
            finally
            {
                lock (gate)
                {
                    request.Active = null;
                    runningLogsRequest = null;
                    cancellation.Dispose();
                    if (request.Lifetime.IsCancellationRequested) request.Lifetime.Dispose();
                }
            }
        }
    }

    private bool CurrentLogsLocked(ContainerReads reads, LogsRequest request, CancellationToken token)
        => ReferenceEquals(logsRequest, request) && CurrentEventsLocked(reads) && !token.IsCancellationRequested;

    // Called only after a successfully published authoritative list, including operation reconciliation.
    private void ReconcileLogsLocked(ContainerReads reads)
    {
        if (logsRequest is not { } request || !CurrentEventsLocked(reads)) return;
        var metadata = reads.Connection.Metadata;
        if (logsSnapshot.ConnectionGeneration != containerSnapshot.ConnectionGeneration
            && (request.Endpoint != metadata.Endpoint || request.EngineId is null || request.EngineId != metadata.EngineId))
        {
            CloseLogsLocked(ContainerLogsPhase.Failed, new(ContainerLogFailureKind.TransportLoss,
                "The Logs connection identity changed or could not be established."));
            return;
        }
        if (!containerSnapshot.Containers.Any(c => c.Id == request.ContainerId))
        {
            CloseLogsLocked(ContainerLogsPhase.Failed, new(ContainerLogFailureKind.NotFound, "The container is no longer available."));
            return;
        }
        if (logsSnapshot.Phase == ContainerLogsPhase.Recovering && request.Active is null)
        {
            if (request.Attempts >= logOptions.MaximumRecoveryAttempts)
            {
                logsSnapshot = logsSnapshot with { Phase = ContainerLogsPhase.Failed,
                    Failure = logsSnapshot.Failure ?? new(ContainerLogFailureKind.TransportLoss, "Logs recovery attempts were exhausted."),
                    Completion = new(ContainerLogTermination.Failed, logsSnapshot.Failure ??
                        new(ContainerLogFailureKind.TransportLoss, "Logs recovery attempts were exhausted.")) };
                SignalLogsLocked(); return;
            }
            logsSnapshot = logsSnapshot with { ConnectionGeneration = containerSnapshot.ConnectionGeneration };
            StartLogsLocked(reads, request);
        }
    }

    private void SuspendLogsLocked()
    {
        if (logsRequest is not { } request) return;
        request.Active?.Cancel();
        pendingLogReads = null;
        request.Buffer.Interrupt();
        if (request.Follow && logsSnapshot.Phase is ContainerLogsPhase.Opening or ContainerLogsPhase.Streaming or ContainerLogsPhase.Recovering)
            logsSnapshot = logsSnapshot with { Phase = ContainerLogsPhase.Recovering };
        else if (logsSnapshot.Phase is ContainerLogsPhase.Opening or ContainerLogsPhase.Streaming)
            logsSnapshot = logsSnapshot with { Phase = ContainerLogsPhase.Canceled,
                Completion = new(ContainerLogTermination.Canceled) };
        SignalLogsLocked();
    }

    private async Task RunLogsAsync(ContainerReads reads, LogsRequest request, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        IContainerLogSubscription? subscription = null;
        try
        {
            ContainerLogCursor? since = null;
            lock (gate)
            {
                if (!CurrentLogsLocked(reads, request, token)) return;
                if (request.Opened)
                {
                    ++request.Attempts;
                    if (request.Buffer.Cursor is { } cursor)
                    {
                        long seconds = cursor.UnixSeconds - logOptions.ReplayOverlap.Ticks / TimeSpan.TicksPerSecond;
                        long nanos = cursor.Nanoseconds - logOptions.ReplayOverlap.Ticks % TimeSpan.TicksPerSecond * 100;
                        if (nanos < 0) { --seconds; nanos += 1_000_000_000; }
                        since = seconds < 0 ? new(0) : new(seconds, (int)nanos);
                        request.Buffer.BeginReplay(since.Value);
                    }
                    else request.Buffer.Uncertain();
                }
            }
            if (request.Opened) await delay.DelayAsync(logOptions.RetryDelay, token).ConfigureAwait(false);
            cancellation.CancelAfter(options.OperationTimeout);
            ContainerLogSubscriptionAttempt attempt;
            try
            {
                attempt = await ((IContainerLogSource)reads.Connection).OpenLogsAsync(
                    new(request.ContainerId, request.Opened ? Math.Min(logOptions.MaximumTailLines,
                        logOptions.MaximumReplayEntries) : request.Tail, request.Follow, since), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!reads.Lifetime.IsCancellationRequested && !request.Lifetime.IsCancellationRequested)
            {
                FinishLogs(reads, request, new(ContainerLogTermination.Failed,
                    new(ContainerLogFailureKind.OpeningDeadline, "Logs opening exceeded its deadline."))); return;
            }
            subscription = attempt.Subscription; // Own late successful results before any cancellation/staleness check.
            if (token.IsCancellationRequested)
            {
                if (!request.Lifetime.IsCancellationRequested && !reads.Lifetime.IsCancellationRequested)
                    FinishLogs(reads, request, new(ContainerLogTermination.Failed,
                        new(ContainerLogFailureKind.OpeningDeadline, "Logs opening exceeded its deadline.")));
                return;
            }
            cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            if (subscription is null) { FinishLogs(reads, request, new(ContainerLogTermination.Failed, attempt.Failure)); return; }
            lock (gate)
            {
                if (!CurrentLogsLocked(reads, request, token)) return;
                if (subscription.ContainerId != request.ContainerId)
                {
                    FinishLogs(reads, request, new(ContainerLogTermination.Failed,
                        new(ContainerLogFailureKind.NotFound, "The requested full container identity is unavailable."))); return;
                }
                request.Opened = true;
                logsSnapshot = logsSnapshot with { Phase = ContainerLogsPhase.Streaming, IsTty = subscription.IsTty,
                    Failure = null, Completion = null };
                SignalLogsLocked();
            }
            int batch = 0;
            while (await subscription.Output.WaitToReadAsync(token).ConfigureAwait(false))
            {
                lock (gate)
                {
                    if (!CurrentLogsLocked(reads, request, token)) return;
                    if (subscription.Output.TryRead(out var output))
                    {
                        request.Buffer.Add(output);
                        logsSnapshot = logsSnapshot with { ViewRevision = logsSnapshot.ViewRevision + 1 };
                        SignalLogsLocked();
                    }
                }
                if (++batch == logOptions.IngestionBatchSize) { batch = 0; await Task.Yield(); }
            }
            var completion = await subscription.Completion.ConfigureAwait(false);
            lock (gate)
            {
                if (CurrentLogsLocked(reads, request, token)) request.Buffer.Complete();
            }
            FinishLogs(reads, request, completion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false); }
    }

    private void FinishLogs(ContainerReads reads, LogsRequest request, ContainerLogCompletion completion)
    {
        lock (gate)
        {
            if (!ReferenceEquals(logsRequest, request) || !CurrentEventsLocked(reads)) return;
            var failure = completion.Failure;
            logsSnapshot = logsSnapshot with { Completion = completion, Failure = failure,
                Phase = completion.Termination switch { ContainerLogTermination.CleanEnd => ContainerLogsPhase.Completed,
                    ContainerLogTermination.Canceled => ContainerLogsPhase.Canceled, _ => ContainerLogsPhase.Failed } };
            if (failure?.Kind == ContainerLogFailureKind.NotFound)
            {
                containerSnapshot = containerSnapshot with { Containers = Array.AsReadOnly(
                    containerSnapshot.Containers.Where(c => c.Id != request.ContainerId).ToArray()) };
                CloseLogsLocked(ContainerLogsPhase.Failed, failure);
                if (containerSnapshot.SelectedContainerId == request.ContainerId) RequestInspectionLocked(reads, null, false);
            }
            else if (failure?.Kind is ContainerLogFailureKind.TransportLoss or ContainerLogFailureKind.OpeningDeadline
                && request.Follow && request.Attempts < logOptions.MaximumRecoveryAttempts)
            {
                // A failed initial opening counts too. Recovery always borrows the connection owner's replacement.
                if (!request.Opened) { request.Opened = true; request.Buffer.Uncertain(); }
                logsSnapshot = logsSnapshot with { Phase = ContainerLogsPhase.Recovering };
                ResourceTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                    "Logs continuity was interrupted."), false);
            }
            SignalLogsLocked();
        }
        if (completion.Failure?.Kind is ContainerLogFailureKind.NotFound or ContainerLogFailureKind.TransportLoss or ContainerLogFailureKind.OpeningDeadline)
        {
            ContainersChanged?.Invoke(this, EventArgs.Empty);
            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class LogsRequest(long id, string containerId, bool follow, int tail,
        EngineEndpoint endpoint, string? engineId, ContainerLogsOptions options)
    {
        public long Id { get; } = id;
        public string ContainerId { get; } = containerId;
        public bool Follow { get; } = follow;
        public int Tail { get; } = tail;
        public EngineEndpoint Endpoint { get; } = endpoint;
        public string? EngineId { get; } = engineId;
        public ContainerLogBuffer Buffer { get; } = new(options);
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource? Active;
        public int Attempts;
        public bool Opened;
    }
}
