using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class RuntimeStartupChecks
{
    private static readonly InstalledRuntime Runtime = new("fixture", "Fixture runtime", new("unix:///tmp/runtime.sock", EngineTransport.UnixSocket));
    private static void Check(bool value, string message = "Runtime assertion failed") { if (!value) throw new InvalidOperationException(message); }
    public static async Task ReadinessAndAdmission()
    {
        bool ready = false;
        var launched = new TaskCompletionSource<RuntimeLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new Launcher(async token => await launched.Task.WaitAsync(token));
        await using var session = new DesktopSession(new Client(() => ready), new() { RetryDelay = TimeSpan.FromMilliseconds(10), RecoveryCooldown = TimeSpan.FromMilliseconds(10) });
        await using var startup = new RuntimeStartup(session, launcher, TimeSpan.FromSeconds(3));
        var first = startup.StartAsync(Runtime);
        var duplicate = startup.StartAsync(Runtime);
        Check(ReferenceEquals(first, duplicate), "Repeated start must join one request");
        await Until(() => launcher.Calls == 1);
        Check(startup.Snapshot.Phase == RuntimeStartupPhase.Starting && startup.IsBusy);
        launched.SetResult(new(true, "accepted"));
        await Until(() => startup.Snapshot.Phase == RuntimeStartupPhase.WaitingForEngine);
        Check(session.EngineState != EngineConnectionState.Connected, "Launch acknowledgement is not readiness");
        ready = true;
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Check(startup.Snapshot.Phase == RuntimeStartupPhase.Ready && session.Snapshot.Endpoint == Runtime.Endpoint && launcher.Calls == 1 && !startup.IsBusy);
        // Already-running Engines still require a deliberate launcher action and real connection evidence.
        await startup.StartAsync(Runtime);
        Check(startup.Snapshot.Phase == RuntimeStartupPhase.Ready && launcher.Calls == 2);
    }
    public static async Task FailuresCancellationAndSelection()
    {
        await using (var session = new DesktopSession(new Client(() => false)))
        await using (var startup = new RuntimeStartup(session, new Launcher(_ => Task.FromResult(new RuntimeLaunchResult(true, "accepted")))))
        {
            var original = session.Snapshot.Endpoint;
            await startup.StartAsync(Runtime with { Id = "unknown" });
            Check(startup.Snapshot.Phase == RuntimeStartupPhase.Failed && session.Snapshot.Endpoint == original, "Unknown runtime must not alter the selection");
            var pending = startup.StartAsync(Runtime);
            var other = new EngineEndpoint("npipe://./pipe/immediate", EngineTransport.WindowsNamedPipe);
            session.SelectEndpoint(other);
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(startup.Snapshot.Phase == RuntimeStartupPhase.Superseded && session.Snapshot.Endpoint == other, "Immediate user selection must win over a queued startup");
        }
        foreach (bool reject in new[] { true, false })
        {
            await using var session = new DesktopSession(new Client(() => false));
            await using var startup = new RuntimeStartup(session, new Launcher(_ => Task.FromResult(new RuntimeLaunchResult(!reject, "Launcher rejected"))), TimeSpan.FromMilliseconds(80));
            await startup.StartAsync(Runtime);
            Check(startup.Snapshot.Phase == (reject ? RuntimeStartupPhase.Failed : RuntimeStartupPhase.TimedOut));
        }
        foreach (var mode in new[] { "cancel", "selection", "dispose" })
        {
            var launcher = new Launcher(async token => { await Task.Delay(Timeout.Infinite, token); return new(true, "unreachable"); });
            await using var session = new DesktopSession(new Client(() => false));
            await using var startup = new RuntimeStartup(session, launcher);
            var pending = startup.StartAsync(Runtime);
            await Until(() => launcher.Calls == 1);
            var other = new EngineEndpoint("npipe://./pipe/other", EngineTransport.WindowsNamedPipe);
            if (mode == "cancel") startup.Cancel();
            if (mode == "selection") session.SelectEndpoint(other);
            if (mode == "dispose") await startup.DisposeAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(startup.Snapshot.Phase == (mode == "selection" ? RuntimeStartupPhase.Superseded : RuntimeStartupPhase.Canceled));
            if (mode == "selection") Check(session.Snapshot.Endpoint == other, "Late startup must not overwrite endpoint selection");
            Check(!startup.IsBusy);
        }
    }
    public static async Task FixedLaunchContracts()
    {
        var runner = new Runner();
        var files = new HashSet<string> { "/opt/homebrew/bin/colima", "/fixture/.colima/default/colima.yaml", "/Applications/Docker.app/Contents/MacOS/Docker Desktop" };
        var launcher = new InstalledRuntimeLauncher(runner, files.Contains, "/fixture", "macos");
        var runtimes = launcher.Discover();
        Check(runtimes.Count == 2);
        var colima = runtimes.Single(r => r.Id == "colima");
        Check(colima.Endpoint.Address == "unix:///fixture/.colima/default/docker.sock");
        Check((await launcher.StartAsync(colima, default)).Accepted);
        Check(runner.Last!.Executable == "/opt/homebrew/bin/colima" && runner.Last.WaitForExit);
        Check(runner.Last.Arguments.SequenceEqual(new[] { "start", "--profile", "default", "--activate=false", "--save-config=false" }), "Fixed Colima args and no context/config mutation");
        Check(runner.Last.ColimaHome == "/fixture/.colima", "Colima home must match the displayed socket");
        var calls = runner.Calls;
        Check(!(await launcher.StartAsync(colima with { Endpoint = new("unix:///tmp/injected;command", EngineTransport.UnixSocket) }, default)).Accepted && runner.Calls == calls);
        files.Remove("/opt/homebrew/bin/colima");
        Check(!(await launcher.StartAsync(colima, default)).Accepted && runner.Calls == calls, "Removed installation must be rechecked");
        var desktop = runtimes.Single(r => r.Id == "docker-desktop");
        Check((await launcher.StartAsync(desktop, default)).Accepted);
        Check(runner.Last!.Executable == "/usr/bin/open" && runner.Last.Arguments.SequenceEqual(new[] { "-a", "/Applications/Docker.app" }));
        runner.Success = false;
        Check(!(await launcher.StartAsync(desktop, default)).Accepted);
        var windows = new InstalledRuntimeLauncher(runner, _ => true, "user", "windows", "programfiles");
        await windows.StartAsync(windows.Discover().Single(), default);
        Check(runner.Last!.Executable == Path.Combine("programfiles", "Docker", "Docker", "Docker Desktop.exe") && !runner.Last.WaitForExit && runner.Last.Arguments.Length == 0);
        Check(new InstalledRuntimeLauncher(runner, _ => false, "/fixture", "macos").Discover().Count == 0);
        if (!OperatingSystem.IsWindows())
        {
            var realRunner = new RuntimeProcessRunner();
            Check(await realRunner.RunAsync(new("/usr/bin/true", [], true), default));
            Check(!await realRunner.RunAsync(new("/usr/bin/false", [], true), default));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try { await realRunner.RunAsync(new("/bin/sleep", ["30"], true), cancel.Token); throw new Exception("Launcher not canceled"); }
            catch (OperationCanceledException) { }
        }
    }
    public static async Task Smoke(string id)
    {
        var launcher = new InstalledRuntimeLauncher();
        var runtime = launcher.Discover().Single(r => r.Id == id);
        await using var session = new DesktopSession(new DockerEngineConnectionClient(), defaultEndpoint: LocalDockerTransportDefault.Endpoint);
        await using var startup = new RuntimeStartup(session, launcher);
        await startup.StartAsync(runtime);
        Check(startup.Snapshot.Phase == RuntimeStartupPhase.Ready, startup.Snapshot.Message);
        Check(session.Snapshot.Metadata?.Endpoint == runtime.Endpoint);
        Console.WriteLine($"Runtime startup verified: {runtime.Name}; {session.Snapshot.Metadata!.Endpoint.Transport}; Engine {session.Snapshot.Metadata.EngineVersion}; negotiated API {session.Snapshot.Metadata.Api.Negotiated}.");
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private sealed class Launcher(Func<CancellationToken, Task<RuntimeLaunchResult>> start) : IInstalledRuntimeLauncher
    {
        public int Calls;
        public IReadOnlyList<InstalledRuntime> Discover() => [Runtime];
        public Task<RuntimeLaunchResult> StartAsync(InstalledRuntime runtime, CancellationToken token) { Interlocked.Increment(ref Calls); return start(token); }
    }
    private sealed class Runner : IRuntimeProcessRunner
    {
        public int Calls; public RuntimeCommand? Last; public bool Success = true;
        public Task<bool> RunAsync(RuntimeCommand command, CancellationToken token) { Calls++; Last = command; return Task.FromResult(Success); }
    }
    private sealed class Client(Func<bool> ready) : IEngineConnectionClient
    {
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ready() ? EngineConnectionAttempt.Connected(new Connection(endpoint!))
                : EngineConnectionAttempt.Failed(new(EngineConnectionFailureKind.MissingEndpoint, "Not running")));
    }
    private sealed class Connection(EngineEndpoint endpoint) : IEngineConnection
    {
        public EngineMetadata Metadata { get; } = new(endpoint, "fixture", new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 56)),
            null, null, null, null, null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
