using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Uno.UI.Hosting;

internal static class Program
{
    [STAThread]
    public static void Main() => UnoPlatformHostBuilder.Create().App(() => new SmokeApp()).UseWin32().UseMacOS().Build().Run();
}

internal sealed class SmokeApp : Microsoft.UI.Xaml.Application
{
    private Window? window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var browser = new FixtureBrowser();
        var frame = new Frame { IsNavigationStackEnabled = false };
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var browse = new Button { Content = "Containers fixture" };
        var other = new Button { Content = "Navigate away" };
        var invalidate = new Button { Content = "Disconnect fixture" };
        var measure = new Button { Content = "Check realized rows" };
        var scenario = new ComboBox { ItemsSource = new[] { "Observed requested state", "Observed different state", "Unresolved", "Not dispatched" }, SelectedIndex = 0 };
        scenario.SelectionChanged += (_, _) => browser.Scenario = scenario.SelectedIndex;
        var evidence = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        browse.Click += (_, _) => frame.Navigate(typeof(ContainersPage), browser);
        other.Click += (_, _) => frame.Content = new TextBlock { Text = "Other resource screen — navigation remains usable during reads." };
        invalidate.Click += (_, _) => browser.Disconnect();
        measure.Click += (_, _) =>
        {
            var list = Lists(frame).FirstOrDefault(list => list.Name == "LogsList" && list.Visibility == Visibility.Visible && list.Items.Count > 0)
                ?? Lists(frame).FirstOrDefault(list => list.Name == "InspectList" && list.Visibility == Visibility.Visible && list.Items.Count > 0)
                ?? Lists(frame).FirstOrDefault(list => list.Name is "ContainerList" or "ImageList" or "VolumeList");
            int loaded = list?.Items.Count ?? 0;
            int realized = list is null ? 0 : Count<ListViewItem>(list);
            evidence.Text = $"{list?.Name}: {realized} realized / {loaded} loaded; inspection cancellations: containers {browser.Cancellations}, images {browser.ImageCancellations}, volumes {browser.VolumeCancellations}; commands: containers {browser.Commands}, images {browser.ImageCommands}, volumes {browser.VolumeCommands}; volume prepares/closes: {browser.VolumePrepares}/{browser.VolumePreparationCloses}; volume target: {browser.LastVolumeRequest?.Target.Name}; volume phase: {browser.VolumeOperation?.Phase}/{browser.VolumeOperation?.Resolution}; image target: {browser.LastImageRequest?.Target.Id}; image phase: {browser.ImageOperation?.Phase}/{browser.ImageOperation?.Resolution}; Logs opens/closes: {browser.LogOpens}/{browser.LogCloses}; last container: {browser.LastRequest?.Command}, force={browser.LastRequest?.ForceDelete}, volumes preserved";
            if (loaded >= 128 && (realized <= 0 || realized >= loaded)) throw new InvalidOperationException("Expected bounded virtualized row realization.");
        };
        controls.Children.Add(browse); controls.Children.Add(other); controls.Children.Add(invalidate); controls.Children.Add(measure); controls.Children.Add(scenario);
        var tools = new StackPanel { Spacing = 4 };
        tools.Children.Add(controls);
        var logScenarios = new ComboBox { ItemsSource = new[] { "Sustained stdout/stderr", "TTY combined", "Recovery then streaming", "Logging driver unavailable", "Access denied", "Finite completion", "Canceled stream", "Malformed output" }, SelectedIndex = 0, Header = "Logs scenario on next open" };
        logScenarios.SelectionChanged += (_, _) => browser.LogScenario = logScenarios.SelectedIndex;
        var imageTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var images = new Button { Content = "Images fixture" };
        var imageEvent = new Button { Content = "Invalidate selected image" };
        images.Click += (_, _) => frame.Navigate(typeof(ImagesPage), browser);
        imageEvent.Click += (_, _) => browser.InvalidateSelectedImage();
        imageTools.Children.Add(images); imageTools.Children.Add(imageEvent);
        var imageScenario = new ComboBox { ItemsSource = new[] { "Image: observed absent", "Image: dependency conflict", "Image: unresolved", "Image: acknowledged but present", "Image: not dispatched" }, SelectedIndex = 0 };
        imageScenario.SelectionChanged += (_, _) => browser.ImageScenario = imageScenario.SelectedIndex;
        imageTools.Children.Add(imageScenario);
        tools.Children.Add(imageTools);
        var volumeTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var volumes = new Button { Content = "Volumes fixture" };
        volumes.Click += (_, _) => frame.Navigate(typeof(VolumesPage), browser);
        var volumeEvent = new Button { Content = "Invalidate selected volume" };
        volumeEvent.Click += (_, _) => browser.InvalidateSelectedVolume();
        var volumeScenario = new ComboBox { ItemsSource = new[] { "Volumes: complete", "Volumes: empty", "Volumes: warning-bearing empty", "Volumes: null with warning", "Volumes: null warnings", "Volumes: failed refresh" }, SelectedIndex = 0 };
        volumeScenario.SelectionChanged += (_, _) => { browser.VolumeScenario = volumeScenario.SelectedIndex; browser.RefreshVolumes(); };
        volumeTools.Children.Add(volumes); volumeTools.Children.Add(volumeEvent); volumeTools.Children.Add(volumeScenario);
        var volumeDeletion = new ComboBox { ItemsSource = new[] { "Delete: complete-empty", "Delete: running and stopped", "Delete: incomplete", "Delete: unavailable", "Delete: failed", "Delete: slow preparation", "Delete: 30,000 dependencies", "Delete: event during confirmation", "Delete: generation during confirmation", "Delete: conflict refresh", "Delete: lost response", "Delete: acknowledged but present", "Delete: not dispatched", "Delete: target disappears" }, SelectedIndex = 1 };
        browser.VolumeDeletionScenario = 1;
        volumeDeletion.SelectionChanged += (_, _) => browser.VolumeDeletionScenario = volumeDeletion.SelectedIndex;
        var volumeChecks = new Button { Content = "Run volume deletion UI checks" };
        volumeChecks.Click += async (_, _) => { volumeChecks.IsEnabled = false; await VolumeUiSmokeChecks.Run(frame, browser, evidence); volumeChecks.IsEnabled = true; };
        var volumeActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        volumeActions.Children.Add(volumeDeletion); volumeActions.Children.Add(volumeChecks); tools.Children.Add(volumeActions);
        tools.Children.Add(volumeTools);
        tools.Children.Add(logScenarios); tools.Children.Add(evidence);
        grid.Children.Add(tools); grid.Children.Add(frame); Grid.SetRow(frame, 1);
        window = new Window { Title = "Container Browser Checks — synthetic data", Content = grid };
        frame.Navigate(typeof(ContainersPage), browser);
        window.Activate();
    }
    private static IEnumerable<ListView> Lists(DependencyObject root)
    {
        if (root is ListView list) yield return list;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in Lists(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static int Count<T>(DependencyObject root) where T : DependencyObject
    {
        int result = root is T ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i) result += Count<T>(VisualTreeHelper.GetChild(root, i));
        return result;
    }
}

internal sealed partial class FixtureBrowser : IContainerBrowser, IContainerOperations
{
    private CancellationTokenSource? inspection;
    public int Cancellations;
    public int Commands, Scenario;
    public ContainerOperationRequest? LastRequest;
    public ContainerOperationSnapshot? ContainerOperation { get; private set; }
    public event EventHandler? ContainerOperationChanged;
    public async Task<ContainerOperationSnapshot> ExecuteContainerOperationAsync(ContainerOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (ContainerOperation?.Pending == true || request.Target.ConnectionGeneration != ContainerSnapshot.ConnectionGeneration)
            return new(0, request, ContainerOperationPhase.Completed, new(request.Command, ContainerCommandDisposition.NotDispatched,
                Failure: new(ContainerCommandFailureKind.StaleConnection, "Fixture admission rejected.")), ContainerOperationResolution.NotDispatched);
        int scenario = Scenario;
        LastRequest = request;
        var current = new ContainerOperationSnapshot(++Commands, request, ContainerOperationPhase.CommandPending);
        ContainerOperation = current; ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(1500).ConfigureAwait(false);
        var outcome = scenario == 3 ? new ContainerCommandResult(request.Command, ContainerCommandDisposition.NotDispatched,
            Failure: new(ContainerCommandFailureKind.Canceled, "Synthetic no-dispatch evidence."))
            : scenario == 1 ? new ContainerCommandResult(request.Command, ContainerCommandDisposition.DaemonResponse, 409, "fixture-sensitive rejection content",
                Failure: new(ContainerCommandFailureKind.Conflict, "Synthetic daemon conflict."))
            : scenario == 2 ? new ContainerCommandResult(request.Command, ContainerCommandDisposition.Uncertain,
                Failure: new(ContainerCommandFailureKind.TransportFailure, "Synthetic lost response."))
            : new ContainerCommandResult(request.Command, ContainerCommandDisposition.Acknowledged, 204,
                Acknowledgement: ContainerCommandAcknowledgement.Accepted);
        current = current with { Phase = ContainerOperationPhase.Reconciling, CommandOutcome = outcome };
        ContainerOperation = current; ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(2000).ConfigureAwait(false);
        bool currentGeneration = ContainerSnapshot.ConnectionGeneration == request.Target.ConnectionGeneration;
        var resolution = scenario == 3 ? ContainerOperationResolution.NotDispatched
            : !currentGeneration || scenario == 2 ? ContainerOperationResolution.Unresolved
            : scenario == 1 ? ContainerOperationResolution.ObservedDifferentState : ContainerOperationResolution.ObservedRequestedState;
        if (resolution == ContainerOperationResolution.ObservedRequestedState)
        {
            rows = rows.Where(row => request.Command != ContainerCommand.Delete || row.Id != request.Target.Id)
                .Select(row => row.Id == request.Target.Id ? row with { State = request.Command == ContainerCommand.Stop ? "exited" : "running" } : row).ToArray();
            ContainerSnapshot = ContainerSnapshot with { Containers = rows, Inspection = null, InspectionState = ResourceReadState.NotRequested };
            Notify();
        }
        current = current with { Phase = ContainerOperationPhase.Completed, Resolution = resolution,
            ObservedConnectionGeneration = currentGeneration ? ContainerSnapshot.ConnectionGeneration : null,
            ObservedExists = resolution == ContainerOperationResolution.ObservedRequestedState ? request.Command != ContainerCommand.Delete : null,
            ObservationFailure = scenario == 2 ? new(EngineResourceFailureKind.TransportLoss, "Synthetic observation unavailable", 500, "fixture-sensitive observation content") : null };
        ContainerOperation = current; ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
        return current;
    }
    private IReadOnlyList<ContainerSummary> rows = Enumerable.Range(0, 20000).Select(i => new ContainerSummary(
        i.ToString("x64"), [$"/container-{i}"], i % 3 == 0 ? null : "example/image:latest", null,
        new[] { "running", "exited", "paused", "restarting", "dead", "created" }[i % 6], "Fixture status", DateTimeOffset.UnixEpoch.AddHours(i),
        i % 2 == 0 ? [new(80, "tcp", "127.0.0.1", 8080)] : null, null, null,
        i % 4 == 0 ? new("healthy", 0) : null)).ToArray();
    public FixtureBrowser() => ContainerSnapshot = new(1, ResourceReadState.Ready, rows);
    public ContainerBrowsingSnapshot ContainerSnapshot { get; private set; }
    public event EventHandler? ContainersChanged;
    public void RefreshContainers()
    { ContainerSnapshot = ContainerSnapshot with { State = ResourceReadState.Ready, Containers = rows }; Notify(); }
    public void SelectContainer(string? id)
    {
        Cancel();
        ContainerSnapshot = ContainerSnapshot with { SelectedContainerId = id, Inspection = null, InspectionState = ResourceReadState.Loading };
        Notify();
        if (id is null) return;
        inspection = new();
        _ = Complete(id, inspection.Token);
    }
    private async Task Complete(string id, CancellationToken token)
    {
        try
        {
            await Task.Delay(2500, token).ConfigureAwait(false);
            string raw = InspectFixture(id);
            token.ThrowIfCancellationRequested();
            var row = rows.FirstOrDefault(row => row.Id == id);
            if (row is null) return;
            string? state = row.State;
            ContainerSnapshot = ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = new(id,
                row.Names![0], "example/image:latest", null, DateTimeOffset.UnixEpoch,
                new(state, state is "running" or "paused" or "restarting", state == "paused", state == "restarting", state == "dead", 0, null, DateTimeOffset.UnixEpoch, null, new("healthy", 0)),
                [new(80, "tcp", "127.0.0.1", 8080)], [new("bind", null, "/fixture/source", "/app", null, "rw", true, null)],
                [new("bridge", "fixture-network", null, "172.17.0.2", 16, "172.17.0.1", null, null, null, null, null)], raw) };
            Notify(); // Deliberately originates off the UI thread.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static string InspectFixture(string id) => "{\"Id\":\"" + id + "\",\"Config\":{\"Env\":[\"TOKEN=fixture-sensitive\",null],\"Escaped\":\"line\\nquote\\\"\"},\"UnknownFutureField\":{\"EmptyObject\":{},\"EmptyArray\":[],\"PreciseNumber\":12345678901234567890.1234567890123456789012345},\"LargeArray\":[" + string.Join(",", Enumerable.Range(0, 50000).Select(i => "{\"index\":" + i + ",\"value\":\"fixture-" + i + "\"}")) + "]}";
    public void RefreshContainerInspection() => SelectContainer(ContainerSnapshot.SelectedContainerId);
    public void ClearContainerInspection()
    { Cancel(); ContainerSnapshot = ContainerSnapshot with { Inspection = null, InspectionState = ResourceReadState.NotRequested }; Notify(); }
    public void Disconnect()
    { Cancel(); DisconnectImages(); DisconnectVolumes(); _ = CloseContainerLogsAsync(); ContainerSnapshot = new(2, ResourceReadState.Unavailable, []); Notify(); }
    private void Cancel()
    {
        if (inspection is { } pending)
        { ++Cancellations; pending.Cancel(); pending.Dispose(); inspection = null; }
    }
    private void Notify() => ContainersChanged?.Invoke(this, EventArgs.Empty);
}
