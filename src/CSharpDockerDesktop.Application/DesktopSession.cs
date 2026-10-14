using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record ConnectionSnapshot(EngineConnectionState State, EngineEndpoint Endpoint,
    EngineMetadata? Metadata = null, EngineConnectionFailure? Failure = null, int RecoveryAttempt = 0,
    bool AutomaticRecoveryPending = false);

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
public sealed class DesktopSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IEngineConnectionClient client;
    private readonly ConnectionRecoveryOptions options;
    private readonly IConnectionDelay delay;
    private CancellationTokenSource? workflowCancellation;
    private Task workflow = Task.CompletedTask;
    private long generation;
    private bool started, disposed;
    private ConnectionSnapshot snapshot;

    public DesktopSession(IEngineConnectionClient client, ConnectionRecoveryOptions? options = null,
        IConnectionDelay? delay = null, EngineEndpoint? endpoint = null)
    {
        this.client = client;
        this.options = options ?? new();
        this.options.Validate();
        this.delay = delay ?? new SystemConnectionDelay();
        snapshot = new(EngineConnectionState.NotConnected, endpoint ?? EngineEndpoint.LocalWindows);
    }

    // Observers read the latest immutable snapshot. Notification may originate off the UI thread.
    public event EventHandler? Changed;
    public ConnectionSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public EngineConnectionState EngineState => Snapshot.State;
    public void Start() => Restart(onlyIfNotStarted: true);
    public void Retry() => Restart(onlyIfNotStarted: false);

    private void Restart(bool onlyIfNotStarted)
    {
        lock (gate)
        {
            if (disposed || (onlyIfNotStarted && started)) return;
            started = true;
            var previous = workflow;
            workflowCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            workflowCancellation = cancellation;
            var currentGeneration = ++generation;
            // Serial chaining also handles clients that complete successfully after cancellation.
            workflow = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try { await RunAsync(currentGeneration, cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally
                {
                    lock (gate) { if (ReferenceEquals(workflowCancellation, cancellation)) workflowCancellation = null; }
                    cancellation.Dispose();
                }
            });
        }
    }

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

    private async Task RunAsync(long currentGeneration, CancellationToken token)
    {
        IEngineConnection? connection = null;
        var endpoint = Snapshot.Endpoint;
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
                    retries = 0;
                    while (true)
                    {
                        await delay.DelayAsync(options.LivenessInterval, token).ConfigureAwait(false);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(options.OperationTimeout);
                        try
                        {
                            lastFailure = await connection.CheckLivenessAsync(deadline.Token).ConfigureAwait(false);
                            if (deadline.IsCancellationRequested)
                                lastFailure = new(EngineConnectionFailureKind.TransientTransportFailure, "The Engine readiness check exceeded its deadline.");
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        { lastFailure = new(EngineConnectionFailureKind.TransientTransportFailure, "The Engine readiness check exceeded its deadline."); }
                        token.ThrowIfCancellationRequested();
                        if (lastFailure is not null) break;
                    }
                    lost = true;
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
        finally { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                ++generation;
                workflowCancellation?.Cancel();
            }
            return new ValueTask(workflow);
        }
    }

    private sealed class SystemConnectionDelay : IConnectionDelay
    {
        public Task DelayAsync(TimeSpan value, CancellationToken token) => Task.Delay(value, token);
    }
}
