using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed record InstalledRuntime(string Id, string Name, EngineEndpoint Endpoint);
public sealed record RuntimeLaunchResult(bool Accepted, string Message);
public interface IInstalledRuntimeLauncher
{
    IReadOnlyList<InstalledRuntime> Discover();
    Task<RuntimeLaunchResult> StartAsync(InstalledRuntime runtime, CancellationToken token);
}
public enum RuntimeStartupPhase { Idle, Starting, WaitingForEngine, Ready, Failed, TimedOut, Canceled, Superseded }
public sealed record RuntimeStartupSnapshot(RuntimeStartupPhase Phase, string Message);

/// <summary>Owns one deliberate startup and observes the session's actual negotiated readiness.</summary>
public sealed class RuntimeStartup(DesktopSession session, IInstalledRuntimeLauncher launcher,
    TimeSpan? timeout = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TimeSpan deadline = timeout ?? TimeSpan.FromMinutes(3);
    private CancellationTokenSource? cancellation;
    private Task work = Task.CompletedTask;
    private bool disposed;
    private RuntimeStartupSnapshot snapshot = new(RuntimeStartupPhase.Idle, "Choose an installed runtime to start. Canceling startup does not stop a runtime already launched.");
    public IReadOnlyList<InstalledRuntime> Runtimes => launcher.Discover();
    public RuntimeStartupSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public bool IsBusy { get { lock (gate) return cancellation is not null; } }
    public event EventHandler? Changed;

    public Task StartAsync(InstalledRuntime runtime)
    {
        lock (gate)
        {
            if (disposed || cancellation is not null) return work;
            if (!launcher.Discover().Contains(runtime))
            {
                snapshot = new(RuntimeStartupPhase.Failed, "The selected runtime installation or existing profile is unavailable. Reopen the app after installing/configuring it separately.");
                return Task.CompletedTask;
            }
            cancelRequested = false;
            cancellation = new CancellationTokenSource(deadline);
            snapshot = new(RuntimeStartupPhase.Starting, $"Starting {runtime.Name}…");
            // Select synchronously with admission: a subsequent user selection must win even
            // when the background launcher has not started executing yet.
            session.SelectEndpoint(runtime.Endpoint);
            var token = cancellation.Token;
            work = Task.Run(() => RunAsync(runtime, token));
            return work;
        }
    }
    public void Cancel() { lock (gate) { if (cancellation is not null) { cancelRequested = true; cancellation.Cancel(); } } }
    private void Publish(RuntimeStartupPhase phase, string message)
    {
        lock (gate) snapshot = new(phase, message);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private async Task RunAsync(InstalledRuntime runtime, CancellationToken token)
    {
        bool superseded = false;
        using var selection = CancellationTokenSource.CreateLinkedTokenSource(token);
        void ObserveSelection(object? sender, EventArgs args)
        {
            if (session.Snapshot.Endpoint != runtime.Endpoint) { superseded = true; selection.Cancel(); }
        }
        session.Changed += ObserveSelection;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
            ObserveSelection(null, EventArgs.Empty);
            var result = await launcher.StartAsync(runtime, selection.Token).ConfigureAwait(false);
            selection.Token.ThrowIfCancellationRequested();
            if (!result.Accepted) { Publish(RuntimeStartupPhase.Failed, result.Message); return; }
            Publish(RuntimeStartupPhase.WaitingForEngine, $"{runtime.Name} startup requested. Waiting for the Engine at {runtime.Endpoint.Address}…");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveReady(object? sender, EventArgs args)
            {
                var current = session.Snapshot;
                if (current.Endpoint == runtime.Endpoint && current.State == EngineConnectionState.Connected)
                    ready.TrySetResult();
            }
            session.Changed += ObserveReady;
            try
            {
                ObserveReady(null, EventArgs.Empty);
                if (!ready.Task.IsCompleted) session.Retry();
                await ready.Task.WaitAsync(selection.Token).ConfigureAwait(false);
                selection.Token.ThrowIfCancellationRequested();
                Publish(RuntimeStartupPhase.Ready, $"{runtime.Name}: Docker Engine connected.");
            }
            finally { session.Changed -= ObserveReady; }
        }
        catch (OperationCanceledException)
        {
            bool canceled; lock (gate) canceled = cancelRequested || disposed;
            Publish(superseded ? RuntimeStartupPhase.Superseded : canceled ? RuntimeStartupPhase.Canceled : RuntimeStartupPhase.TimedOut,
                superseded ? "Startup monitoring ended because the endpoint changed."
                : canceled ? "Startup canceled. The runtime may already be running; it was not stopped."
                : "Runtime startup timed out before Engine readiness. The runtime may still be starting; use Retry connection.");
        }
        catch (Exception)
        {
            Publish(RuntimeStartupPhase.Failed, "Runtime startup failed. Check the runtime installation and start it manually for further diagnostics.");
        }
        finally
        {
            session.Changed -= ObserveSelection;
            lock (gate) { cancellation?.Dispose(); cancellation = null; cancelRequested = false; }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    private bool cancelRequested;
    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (gate) { disposed = true; cancellation?.Cancel(); pending = work; }
        await pending.ConfigureAwait(false);
    }
}
