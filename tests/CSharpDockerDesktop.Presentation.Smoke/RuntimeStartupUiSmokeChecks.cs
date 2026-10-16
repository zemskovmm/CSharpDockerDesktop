using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

internal static class RuntimeStartupUiSmokeChecks
{
    public static async Task Run(Frame frame, TextBlock evidence)
    {
        var results = new List<string>();
        try
        {
            var client = new Client();
            var launcher = new Launcher();
            await using var session = new DesktopSession(client);
            await using var runtime = new RuntimeStartup(session, launcher);
            var page = new MainPage(session, runtime);
            frame.Content = page;
            await Until(() => page.IsLoaded);
            T Control<T>(string name) where T : FrameworkElement => (T)page.FindName(name);
            var start = Control<Button>("StartRuntime");
            var cancel = Control<Button>("CancelRuntime");
            Check(start.IsEnabled && !cancel.IsEnabled && launcher.Calls == 0, "Opening the app must not launch the runtime");
            Check(Control<TextBlock>("RuntimeDestination").Text.Contains("runtime-ui"), "Destination must be visible before starting");
            Invoke(start);
            await Until(() => launcher.Calls == 1 && !start.IsEnabled && cancel.IsEnabled);
            Check(!Control<ComboBox>("RuntimeChoices").IsEnabled, "Runtime selection must be disabled during startup");
            var navigation = Control<NavigationView>("ResourceNavigation");
            navigation.SelectedItem = navigation.MenuItems.Cast<NavigationViewItem>().Single(item => (string)item.Tag == "Images");
            Check(Control<Frame>("ResourceFrame").Content is ImagesPage, "Navigation must remain usable");
            Invoke(cancel);
            await Until(() => runtime.Snapshot.Phase == RuntimeStartupPhase.Canceled && start.IsEnabled);
            Check(Control<TextBlock>("RuntimeStatus").Text.Contains("not stopped"), "Cancellation must explain that the runtime is not stopped");
            results.Add("PASS actual startup/cancel controls, visible destination, duplicate admission and navigation while launching");
            Invoke(start);
            await Until(() => launcher.Calls == 2);
            launcher.Pending.TrySetResult(new(true, "accepted"));
            await Until(() => runtime.Snapshot.Phase == RuntimeStartupPhase.WaitingForEngine);
            Check(!start.IsEnabled && Control<TextBlock>("RuntimeStatus").Text.Contains("Waiting for the Engine"), "Launcher exit is not Engine readiness");
            client.Ready = true;
            session.Retry();
            await Until(() => runtime.Snapshot.Phase == RuntimeStartupPhase.Ready && start.IsEnabled);
            Check(Control<TextBlock>("RuntimeStatus").Text.Contains("Docker Engine connected"), "Ready status must reach the UI");
            Check(Control<TextBlock>("SelectedEndpoint").Text.Contains("runtime-ui"), "Chosen runtime endpoint must reach the UI");
            results.Add("PASS actual startup button selects the runtime endpoint and shows readiness only after a negotiated connection");
            page.Detach();
        }
        catch (Exception ex) { results.Add("FAIL " + ex); }
        evidence.Text = string.Join("\n", results);
        await File.WriteAllLinesAsync("/private/tmp/csharpdocker-runtime-ui-smoke.txt", results);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!predicate()) await Task.Delay(20, timeout.Token); }
    private sealed class Launcher : IInstalledRuntimeLauncher
    {
        private static readonly InstalledRuntime runtime = new("fixture", "Fixture runtime", new("npipe://./pipe/runtime-ui", EngineTransport.WindowsNamedPipe));
        public int Calls;
        public TaskCompletionSource<RuntimeLaunchResult> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<InstalledRuntime> Discover() => [runtime];
        public Task<RuntimeLaunchResult> StartAsync(InstalledRuntime selected, CancellationToken token)
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref Calls);
            return Pending.Task.WaitAsync(token);
        }
    }
    private sealed class Client : IEngineConnectionClient
    {
        public bool Ready;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Ready ? EngineConnectionAttempt.Connected(new Connection(endpoint!))
                : EngineConnectionAttempt.Failed(new(EngineConnectionFailureKind.MissingEndpoint, "Not ready")));
    }
    private sealed class Connection(EngineEndpoint endpoint) : IEngineConnection
    {
        public EngineMetadata Metadata { get; } = new(endpoint, "fixture", new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 56)),
            null, null, null, null, null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
