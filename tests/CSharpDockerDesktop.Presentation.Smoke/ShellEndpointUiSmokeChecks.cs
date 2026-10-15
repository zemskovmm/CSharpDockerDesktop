using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

/// <summary>Actual shell controls with a controlled Application connection. No Docker transport is used.</summary>
internal static class ShellEndpointUiSmokeChecks
{
    private static DesktopSession? session;
    private static Client? client;
    public static async Task Open(Frame frame)
    {
        if (frame.Content is MainPage previous) previous.Detach();
        if (session is not null) { Release(); await session.DisposeAsync(); }
        client = new(); session = new(client, new() { MaximumRecoveryAttempts = 0 });
        frame.Content = new MainPage(session); session.Start();
    }
    public static void Release() => client?.Pending.TrySetResult(true);
    public static async Task Run(Frame frame, TextBlock evidence)
    {
        var results = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        try
        {
            await Open(frame);
            var shell = (MainPage)frame.Content;
            T Control<T>(string name) where T : FrameworkElement => (T)shell.FindName(name);
            await Until(() => shell.IsLoaded && client!.Calls == 1);
            Control<Expander>("EndpointEditor").IsExpanded = true;
            await Task.Delay(100);
            var draft = Control<TextBox>("EndpointDraft");
            Check(draft.MaxLength == ConnectionEditorView.MaximumAddressCharacters && draft.Focus(FocusState.Keyboard), "Endpoint draft must be bounded and focusable");
            draft.Text = "npipe://./pipe/shell_b";
            await Until(() => Control<TextBlock>("EndpointDraftStatus").Text.Contains("Unapplied"));
            Check(client!.Calls == 1 && session!.Snapshot.Endpoint == EngineEndpoint.LocalWindows && Control<TextBlock>("EndpointDraftStatus").Text.Contains("Unapplied"), "Typing must not connect");
            var apply = Control<Button>("ApplyEndpoint");
            Check(apply.Focus(FocusState.Keyboard), "Apply must accept keyboard focus"); Invoke(apply);
            await Until(() => client.Calls == 2);
            Check(Control<TextBlock>("SelectedEndpoint").Text.Contains("shell_b") && Control<TextBlock>("ConnectionDiagnostic").Text.Contains("shell_b"), "Selected/connecting labels must name the applied endpoint");
            var navigation = Control<NavigationView>("ResourceNavigation");
            foreach (string section in new[] { "Images", "Volumes", "Networks", "Containers" })
            {
                navigation.SelectedItem = navigation.MenuItems.Cast<NavigationViewItem>().Single(i => (string)i.Tag == section);
                Check(Control<Frame>("ResourceFrame").Content.GetType().Name == section + "Page", "Resource navigation blocked by delayed connection");
            }
            Check(session!.Snapshot.State == EngineConnectionState.Connecting, "Fixture connection must remain delayed during navigation");
            results.Add("PASS actual draft/Apply controls, input bound, programmatic keyboard focus and resource navigation during delayed Application connection");
            draft.Text = "unix:///unapplied"; Invoke((Button)FindByAutomationId(shell, "RetryConnection"));
            await Until(() => client.Calls == 3);
            Check(client.Endpoints.Last().Address.EndsWith("shell_b") && draft.Text == "unix:///unapplied", "Retry must use actual selection and retain draft");
            Release(); await Until(() => session.Snapshot.State == EngineConnectionState.Unavailable);
            // Evidence is deliberately hidden until requested; identical fixture messages at another endpoint cannot inherit reveal.
            Invoke(Control<Button>("ShowDaemonMessage")); Check(Control<TextBlock>("DaemonMessage").Visibility == Visibility.Visible, "Deliberate daemon reveal failed");
            Invoke(Control<Button>("UseDefaultEndpoint")); await Until(() => client.Calls == 4);
            Check(draft.Text == EngineEndpoint.LocalWindows.Address && Control<TextBlock>("DaemonMessage").Text == "", "Return to default must reset draft and old evidence");
            Release(); await Until(() => session.Snapshot.State == EngineConnectionState.Unavailable);
            Check(Control<TextBlock>("DaemonMessage").Visibility == Visibility.Collapsed, "Identical messages must remain scoped to their connection");
            results.Add("PASS actual Retry/default controls, retained draft, explicit default selection and endpoint-scoped identical daemon evidence");
            results.Add("Synthetic Uno/macOS control checks. Native keyboard activation and Windows/Docker behavior require separate evidence.");
        }
        catch (Exception error) { results.Add("FAIL " + error); }
        evidence.Text = string.Join("\n", results);
        await File.WriteAllLinesAsync("/private/tmp/csharpdocker-endpoint-ui-smoke.txt", results);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static Microsoft.UI.Xaml.DependencyObject FindByAutomationId(Microsoft.UI.Xaml.DependencyObject root, string id)
    {
        if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(root) == id) return root;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); ++i)
        { try { return FindByAutomationId(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), id); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException(id);
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private sealed class Client : IEngineConnectionClient
    {
        public int Calls;
        public readonly List<EngineEndpoint> Endpoints = [];
        public TaskCompletionSource<bool> Pending { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Endpoints.Add(endpoint!); Interlocked.Increment(ref Calls);
            await Pending.Task.WaitAsync(cancellationToken);
            return EngineConnectionAttempt.Failed(new(EngineConnectionFailureKind.AccessDenied, "Synthetic denied endpoint. No Docker transport used.", DaemonMessage: "same private synthetic daemon message"));
        }
    }
}
