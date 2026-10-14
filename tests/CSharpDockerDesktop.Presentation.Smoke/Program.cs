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
        var evidence = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        browse.Click += (_, _) => frame.Navigate(typeof(ContainersPage), browser);
        other.Click += (_, _) => frame.Content = new TextBlock { Text = "Other resource screen — navigation remains usable during reads." };
        invalidate.Click += (_, _) => browser.Disconnect();
        measure.Click += (_, _) =>
        {
            int realized = Count<ListViewItem>(frame);
            evidence.Text = $"Realized rows: {realized}; loaded rows: 20000; inspection cancellations: {browser.Cancellations}";
            if (realized <= 0 || realized >= 20000) throw new InvalidOperationException("Expected virtualized row realization.");
        };
        controls.Children.Add(browse); controls.Children.Add(other); controls.Children.Add(invalidate); controls.Children.Add(measure); controls.Children.Add(evidence);
        grid.Children.Add(controls); grid.Children.Add(frame); Grid.SetRow(frame, 1);
        window = new Window { Title = "Container Browser Checks — synthetic data", Content = grid };
        frame.Navigate(typeof(ContainersPage), browser);
        window.Activate();
    }
    private static int Count<T>(DependencyObject root) where T : DependencyObject
    {
        int result = root is T ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i) result += Count<T>(VisualTreeHelper.GetChild(root, i));
        return result;
    }
}

internal sealed class FixtureBrowser : IContainerBrowser
{
    private CancellationTokenSource? inspection;
    public int Cancellations;
    private readonly IReadOnlyList<ContainerSummary> rows = Enumerable.Range(0, 20000).Select(i => new ContainerSummary(
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
            ContainerSnapshot = ContainerSnapshot with { InspectionState = ResourceReadState.Ready, Inspection = new(id,
                rows.First(row => row.Id == id).Names![0], "example/image:latest", null, DateTimeOffset.UnixEpoch,
                new("running", true, false, false, false, 0, null, DateTimeOffset.UnixEpoch, null, new("healthy", 0)),
                [new(80, "tcp", "127.0.0.1", 8080)], [new("bind", null, "/fixture/source", "/app", null, "rw", true, null)],
                [new("bridge", "fixture-network", null, "172.17.0.2", 16, "172.17.0.1", null, null, null, null, null)], "{}") };
            Notify(); // Deliberately originates off the UI thread.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void RefreshContainerInspection() => SelectContainer(ContainerSnapshot.SelectedContainerId);
    public void ClearContainerInspection()
    { Cancel(); ContainerSnapshot = ContainerSnapshot with { Inspection = null, InspectionState = ResourceReadState.NotRequested }; Notify(); }
    public void Disconnect()
    { Cancel(); ContainerSnapshot = new(2, ResourceReadState.Unavailable, []); Notify(); }
    private void Cancel()
    {
        if (inspection is { } pending)
        { ++Cancellations; pending.Cancel(); pending.Dispose(); inspection = null; }
    }
    private void Notify() => ContainersChanged?.Invoke(this, EventArgs.Empty);
}
