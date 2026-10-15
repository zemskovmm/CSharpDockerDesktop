using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record ConnectionSnapshot(EngineConnectionState State, EngineEndpoint Endpoint,
    EngineMetadata? Metadata = null, EngineConnectionFailure? Failure = null, int RecoveryAttempt = 0,
    bool AutomaticRecoveryPending = false, EngineEventFailure? EventFailure = null,
    bool EventRecoveryPending = false);

/// <summary>Bounded implementation defaults; tune independently of product requirements.</summary>
public sealed record ConnectionRecoveryOptions
{
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RecoveryCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan LivenessInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaximumRecoveryAttempts { get; init; } = 6;
    internal void Validate()
    {
        if (RetryDelay <= TimeSpan.Zero || RetryDelay.TotalMilliseconds > int.MaxValue
            || RecoveryCooldown <= TimeSpan.Zero || RecoveryCooldown.TotalMilliseconds > int.MaxValue
            || LivenessInterval <= TimeSpan.Zero || LivenessInterval.TotalMilliseconds > int.MaxValue
            || OperationTimeout <= TimeSpan.Zero || OperationTimeout.TotalMilliseconds > int.MaxValue
            || MaximumRecoveryAttempts < 0) throw new ArgumentOutOfRangeException(nameof(ConnectionRecoveryOptions));
    }
}

public interface IConnectionDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Owns one connection and one serialized, cancelable connection/recovery workflow.</summary>
public sealed partial class DesktopSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IEngineConnectionClient client;
    private readonly ConnectionRecoveryOptions options;
    private readonly IConnectionDelay delay;
    private readonly ContainerReconciliationOptions reconciliation;
    private readonly ContainerOperationOptions operationOptions;
    private CancellationTokenSource? workflowCancellation;
    private Task workflow = Task.CompletedTask;
    private ConnectionWorkflow? pendingWorkflow;
    private bool workflowRunnerActive;
    private Task connectionCleanup = Task.CompletedTask;
    private long generation;
    private bool started, disposed;
    private ConnectionSnapshot snapshot;

    public DesktopSession(IEngineConnectionClient client, ConnectionRecoveryOptions? options = null,
        IConnectionDelay? delay = null, EngineEndpoint? endpoint = null,
        ContainerReconciliationOptions? reconciliation = null, ContainerOperationOptions? operationOptions = null,
        ContainerLogsOptions? logOptions = null, ImageOperationOptions? imageOperationOptions = null, VolumeOperationOptions? volumeOperationOptions = null, NetworkOperationOptions? networkOperationOptions = null,
        ContainerStatsOptions? statsOptions = null, TimeProvider? statsTimeProvider = null,
        ContainerTerminalOptions? terminalOptions = null, TimeProvider? terminalTimeProvider = null)
    {
        this.client = client;
        this.options = options ?? new();
        this.options.Validate();
        this.delay = delay ?? new SystemConnectionDelay();
        this.reconciliation = reconciliation ?? new();
        this.reconciliation.Validate();
        this.operationOptions = operationOptions ?? new();
        this.operationOptions.Validate();
        this.imageOperationOptions = imageOperationOptions ?? new();
        this.imageOperationOptions.Validate();
        this.volumeOperationOptions = volumeOperationOptions ?? new();
        this.volumeOperationOptions.Validate();
        this.networkOperationOptions = networkOperationOptions ?? new();
        this.networkOperationOptions.Validate();
        this.logOptions = logOptions ?? new();
        this.logOptions.Validate();
        this.statsOptions = statsOptions ?? new();
        this.statsOptions.Validate();
        statsTime = statsTimeProvider ?? TimeProvider.System;
        this.terminalOptions = terminalOptions ?? new();
        this.terminalOptions.Validate();
        terminalTime = terminalTimeProvider ?? TimeProvider.System;
        snapshot = new(EngineConnectionState.NotConnected, endpoint ?? EngineEndpoint.LocalWindows);
    }

    // Observers read the latest immutable snapshot. Notification may originate off the UI thread.
    public event EventHandler? Changed;
    public ConnectionSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public EngineConnectionState EngineState => Snapshot.State;
    public void Start() => Restart(onlyIfNotStarted: true);
    public void Retry() => Restart(onlyIfNotStarted: false);

    /// <summary>
    /// Select an explicit endpoint and connect asynchronously. Revokes the old connection before returning.
    /// One active workflow drains before the latest pending selection starts; intermediate selections are discarded.
    /// Selecting the current endpoint is a no-op; use Retry to negotiate it again.
    /// Infrastructure validates the endpoint and reports unsupported selections without fallback.
    /// </summary>
    public void SelectEndpoint(EngineEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Restart(onlyIfNotStarted: false, endpoint);
    }

    /// <summary>Cancel and join owned work while keeping the session available for a later Start/Retry.</summary>
    public Task DisconnectAsync()
    {
        Task completion;
        lock (gate)
        {
            if (disposed) return Task.WhenAll(workflow, logsWork, logNotifications, statsWork, statsNotifications, terminalWork, terminalNotifications);
            started = false;
            pendingWorkflow = null;
            snapshot = new(EngineConnectionState.NotConnected, snapshot.Endpoint);
            ++generation;
            InvalidateContainersLocked();
            CloseStatsLocked();
            CloseLogsLocked(ContainerLogsPhase.Closed);
            workflowCancellation?.Cancel();
            completion = Task.WhenAll(connectionCleanup, logsWork, statsWork, terminalWork, JoinTerminalNotificationsLocked());
        }
        Changed?.Invoke(this, EventArgs.Empty);
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        return completion;
    }

    private void Restart(bool onlyIfNotStarted, EngineEndpoint? endpoint = null)
    {
        lock (gate)
        {
            if (disposed || (onlyIfNotStarted && started) || endpoint == snapshot.Endpoint) return;
            started = true;
            var selected = endpoint ?? snapshot.Endpoint;
            ++generation;
            snapshot = new(EngineConnectionState.Connecting, selected);
            pendingWorkflow = new(generation, selected);
            InvalidateContainersLocked();
            // Explicit selection ends live-view ownership even if the user returns to the same Engine later.
            if (endpoint is not null)
            {
                CloseLogsLocked(ContainerLogsPhase.Closed);
                CloseStatsLocked();
                eventCursor = null;
                cursorEndpoint = null;
                cursorEngineId = null;
            }
            workflowCancellation?.Cancel();
            if (!workflowRunnerActive)
            {
                workflowRunnerActive = true;
                workflow = Task.Run(RunConnectionQueueAsync);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        NotifyContainerBrowsers();
        ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunConnectionQueueAsync()
    {
        while (true)
        {
            ConnectionWorkflow request;
            CancellationTokenSource cancellation;
            TaskCompletionSource cleanup;
            lock (gate)
            {
                if (disposed || pendingWorkflow is not { } pending)
                {
                    pendingWorkflow = null;
                    workflowRunnerActive = false;
                    return;
                }
                request = pending;
                pendingWorkflow = null;
                workflowCancellation = cancellation = new();
                cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                connectionCleanup = cleanup.Task;
            }
            try { await RunAsync(request.Generation, request.Endpoint, cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                lock (gate)
                {
                    workflowCancellation = null;
                    cancellation.Dispose();
                    cleanup.TrySetResult();
                }
            }
        }
    }

    private sealed record ConnectionWorkflow(long Generation, EngineEndpoint Endpoint);

    private bool Publish(long currentGeneration, ConnectionSnapshot value)
    {
        lock (gate)
        {
            if (disposed || currentGeneration != generation) return false;
            snapshot = value;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task RunAsync(long currentGeneration, EngineEndpoint endpoint, CancellationToken token)
    {
        IEngineConnection? connection = null;
        ContainerReads? reads = null;
        ImageReads? images = null;
        VolumeReads? volumes = null;
        NetworkReads? networks = null;
        var lost = false;
        int retries = 0;
        EngineConnectionFailure? lastFailure = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!Publish(currentGeneration, new(EngineConnectionState.Connecting, endpoint,
                    Failure: lastFailure, RecoveryAttempt: retries))) return;
                EngineConnectionAttempt attempt;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(options.OperationTimeout);
                    try
                    {
                        attempt = await client.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
                        if (deadline.IsCancellationRequested)
                        {
                            if (attempt.Connection is { } late) await late.DisposeAsync().ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            attempt = EngineConnectionAttempt.Failed(new(EngineConnectionFailureKind.TransientTransportFailure, "The connection attempt exceeded its deadline."));
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { attempt = EngineConnectionAttempt.Failed(new(EngineConnectionFailureKind.TransientTransportFailure, "The connection attempt exceeded its deadline.")); }
                }
                // Always take ownership before checking cancellation or generation.
                connection = attempt.Connection;
                token.ThrowIfCancellationRequested();
                if (connection is not null)
                {
                    if (!Publish(currentGeneration, new(EngineConnectionState.Connected, endpoint, connection.Metadata))) return;
                    reads = BeginContainerReads(currentGeneration, connection, token);
                    if (reads is null) return;
                    images = BeginImageReads(reads);
                    volumes = BeginVolumeReads(reads);
                    networks = BeginNetworkReads(reads);
                    retries = 0;
                    lastFailure = await WatchConnectionAsync(connection, reads, token).ConfigureAwait(false);
                    lost = true;
                    var finishedReads = reads;
                    var finishedImages = images;
                    var finishedVolumes = volumes;
                    var finishedNetworks = networks;
                    reads = null;
                    images = null;
                    volumes = null;
                    networks = null;
                    await Task.WhenAll(EndContainerReadsAsync(finishedReads),
                        finishedImages is null ? Task.CompletedTask : EndImageReadsAsync(finishedImages),
                        finishedVolumes is null ? Task.CompletedTask : EndVolumeReadsAsync(finishedVolumes),
                        finishedNetworks is null ? Task.CompletedTask : EndNetworkReadsAsync(finishedNetworks)).ConfigureAwait(false);
                    await connection.DisposeAsync().ConfigureAwait(false);
                    connection = null;
                }
                else lastFailure = attempt.Failure!;
                bool recoverable = lastFailure.Kind is EngineConnectionFailureKind.MissingEndpoint
                    or EngineConnectionFailureKind.TransientTransportFailure or EngineConnectionFailureKind.DaemonHttpError;
                bool retry = recoverable && options.MaximumRecoveryAttempts > 0;
                bool cooldown = retries >= options.MaximumRecoveryAttempts;
                var state = lastFailure.Kind is EngineConnectionFailureKind.UnsupportedEndpoint or EngineConnectionFailureKind.IncompatibleApi
                    ? EngineConnectionState.Unsupported : lost ? EngineConnectionState.Lost : EngineConnectionState.Unavailable;
                if (!Publish(currentGeneration, new(state, endpoint, Failure: lastFailure,
                    RecoveryAttempt: retries, AutomaticRecoveryPending: retry)) || !retry) return;
                await delay.DelayAsync(cooldown ? options.RecoveryCooldown : options.RetryDelay, token).ConfigureAwait(false);
                retries = cooldown ? 0 : retries + 1;
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(reads is null ? Task.CompletedTask : EndContainerReadsAsync(reads),
                    images is null ? Task.CompletedTask : EndImageReadsAsync(images),
                    volumes is null ? Task.CompletedTask : EndVolumeReadsAsync(volumes),
                    networks is null ? Task.CompletedTask : EndNetworkReadsAsync(networks)).ConfigureAwait(false);
            }
            finally { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async Task<EngineConnectionFailure> WatchConnectionAsync(IEngineConnection connection, ContainerReads reads,
        CancellationToken token)
    {
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(token);
        var liveness = CheckConnectionAsync(connection, monitoring.Token);
        var completed = await Task.WhenAny(liveness, reads.TransportLoss.Task).ConfigureAwait(false);
        if (completed == reads.TransportLoss.Task)
        {
            monitoring.Cancel();
            try { await liveness.ConfigureAwait(false); }
            catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();
            return await reads.TransportLoss.Task.ConfigureAwait(false);
        }
        return await liveness.ConfigureAwait(false);
    }

    private async Task<EngineConnectionFailure> CheckConnectionAsync(IEngineConnection connection, CancellationToken token)
    {
        while (true)
        {
            await delay.DelayAsync(options.LivenessInterval, token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(options.OperationTimeout);
            EngineConnectionFailure? failure;
            try
            {
                failure = await connection.CheckLivenessAsync(deadline.Token).ConfigureAwait(false);
                if (deadline.IsCancellationRequested)
                    failure = new(EngineConnectionFailureKind.TransientTransportFailure, "The Engine readiness check exceeded its deadline.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { failure = new(EngineConnectionFailureKind.TransientTransportFailure, "The Engine readiness check exceeded its deadline."); }
            token.ThrowIfCancellationRequested();
            if (failure is not null) return failure;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task completion;
        bool changed = false;
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                pendingWorkflow = null;
                CloseLogsLocked(ContainerLogsPhase.Closed);
                CloseStatsLocked();
                logNotificationLifetime.Cancel();
                statsNotificationLifetime.Cancel();
                terminalNotificationLifetime.Cancel();
                ++generation;
                InvalidateContainersLocked();
                workflowCancellation?.Cancel();
                changed = true;
            }
            completion = Task.WhenAll(workflow, logsWork, logNotifications, statsWork, statsNotifications, terminalWork, terminalNotifications);
        }
        if (changed)
        {
            NotifyContainerBrowsers();
            ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        }
        return new ValueTask(FinishDesktopDisposalAsync(completion));
    }

    private async Task FinishDesktopDisposalAsync(Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        finally { logNotificationLifetime.Dispose(); statsNotificationLifetime.Dispose(); terminalNotificationLifetime.Dispose(); }
    }

    private sealed class SystemConnectionDelay : IConnectionDelay
    {
        public Task DelayAsync(TimeSpan value, CancellationToken token) => Task.Delay(value, token);
    }
}
