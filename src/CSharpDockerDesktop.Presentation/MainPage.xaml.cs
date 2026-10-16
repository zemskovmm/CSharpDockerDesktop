using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class MainPage : Page
{
    private readonly DesktopSession session;
    private readonly ConnectionEditorView connection;
    private readonly RuntimeStartup? runtime;
    private bool attached;
    private int connectionUpdateQueued;

    public MainPage(DesktopSession session, RuntimeStartup? runtime = null)
    {
        this.session = session;
        this.runtime = runtime;
        connection = new(session);
        InitializeComponent();
        RuntimeChoices.ItemsSource = runtime?.Runtimes;
        if (runtime?.Runtimes.Count > 0) RuntimeChoices.SelectedIndex = 0;
        EndpointDraft.Text = connection.Draft;
        Loaded += (_, _) => { if (!attached) { session.Changed += OnSessionChanged; if (runtime is not null) runtime.Changed += OnSessionChanged; attached = true; } UpdateConnection(); };
        Unloaded += (_, _) => Detach();
        UpdateConnection();
        ResourceNavigation.SelectedItem = ContainersItem;
    }

    public void Detach()
    {
        session.Changed -= OnSessionChanged;
        if (runtime is not null) runtime.Changed -= OnSessionChanged;
        attached = false;
        connection.HideDaemonMessage();
        DaemonMessage.Text = "";
        DaemonMessage.Visibility = Visibility.Collapsed;
        if (ResourceFrame.Content is ContainersPage containers) containers.Detach();
        if (ResourceFrame.Content is ImagesPage images) images.Detach();
        if (ResourceFrame.Content is VolumesPage volumes) volumes.Detach();
        if (ResourceFrame.Content is NetworksPage networks) networks.Detach();
    }
    private void OnSessionChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref connectionUpdateQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref connectionUpdateQueued, 0);
            if (attached) UpdateConnection();
        })) Interlocked.Exchange(ref connectionUpdateQueued, 0);
    }
    private void OnRuntimeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (RuntimeDestination is not null) UpdateRuntime();
    }
    private async void OnStartRuntime(object sender, RoutedEventArgs args)
    {
        if (runtime is null || RuntimeChoices.SelectedItem is not InstalledRuntime selected) return;
        var pending = runtime.StartAsync(selected);
        // The runtime request deliberately selects its own endpoint; keep the visible draft in sync.
        connection.Draft = selected.Endpoint.Address;
        EndpointDraft.Text = connection.Draft;
        UpdateRuntime();
        await pending;
        if (attached) UpdateConnection();
    }
    private void OnCancelRuntime(object sender, RoutedEventArgs args) => runtime?.Cancel();
    private void UpdateRuntime()
    {
        var choice = RuntimeChoices.SelectedItem as InstalledRuntime;
        var busy = runtime?.IsBusy == true;
        StartRuntime.IsEnabled = runtime is not null && choice is not null && !busy;
        CancelRuntime.IsEnabled = busy;
        RuntimeChoices.IsEnabled = !busy;
        RuntimeDestination.Text = choice is null ? "No supported installed runtime found. Install Docker Desktop, or configure a default Colima Docker profile, then reopen the app."
            : $"Start {choice.Name} and connect to {choice.Endpoint.Address}";
        RuntimeStatus.Text = runtime?.Snapshot.Message ?? "Runtime startup is unavailable in this host.";
    }
    private void OnRetry(object sender, RoutedEventArgs args) { connection.Retry(); UpdateConnection(); }
    private void OnApplyEndpoint(object sender, RoutedEventArgs args)
    {
        connection.Draft = EndpointDraft.Text;
        connection.Apply(); UpdateConnection();
    }
    private void OnUseDefaultEndpoint(object sender, RoutedEventArgs args)
    {
        connection.UseDefault(); EndpointDraft.Text = connection.Draft; UpdateConnection();
    }
    private void OnEndpointDraftChanged(object sender, TextChangedEventArgs args)
    {
        if (EndpointDraft is null || ApplyEndpoint is null || EndpointDraftStatus is null) return;
        connection.Draft = EndpointDraft.Text;
        ApplyEndpoint.IsEnabled = connection.CanApply;
        EndpointDraftStatus.Text = connection.DraftStatus;
    }
    private void OnShowDaemonMessage(object sender, RoutedEventArgs args)
    {
        connection.RevealDaemonMessage();
        DaemonMessage.Text = connection.DaemonMessage;
        DaemonMessage.Visibility = connection.ShowingDaemonMessage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateConnection()
    {
        var s = session.Snapshot;
        UpdateRuntime();
        EngineStatus.Text = s.State switch
        {
            EngineConnectionState.Connecting => "Docker Engine: Connecting…",
            EngineConnectionState.Connected => "Docker Engine: Connected",
            EngineConnectionState.Lost => "Docker Engine: Connection lost",
            EngineConnectionState.Unsupported => "Docker Engine: Unsupported",
            EngineConnectionState.Unavailable => "Docker Engine: Unavailable",
            _ => "Docker Engine: Not connected"
        };
        connection.Refresh();
        SelectedEndpoint.Text = $"Selected: {ConnectionEditorView.Preview(s.Endpoint.Address)} · {s.Endpoint.Transport}";
        EndpointDraftStatus.Text = connection.DraftStatus;
        ApplyEndpoint.IsEnabled = connection.CanApply;
        ConnectionDiagnostic.Text = connection.Diagnostic;
        DaemonMessage.Text = connection.DaemonMessage;
        DaemonMessage.Visibility = connection.ShowingDaemonMessage ? Visibility.Visible : Visibility.Collapsed;
        ShowDaemonMessage.Visibility = connection.HasDaemonMessage ? Visibility.Visible : Visibility.Collapsed;
        ConnectionDetails.Text = connection.Details;
        if (ResourceFrame.Content is ResourcePage page) page.UpdateConnection(s);
    }

    private void OnResourceSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string sectionName }
            && Enum.TryParse<ResourceSection>(sectionName, out var section))
        {
            if (ResourceFrame.Content is ContainersPage containers) containers.Detach();
            if (ResourceFrame.Content is ImagesPage images) images.Detach();
            if (ResourceFrame.Content is VolumesPage volumes) volumes.Detach();
            if (ResourceFrame.Content is NetworksPage networks) networks.Detach();
            var pageType = section switch { ResourceSection.Containers => typeof(ContainersPage), ResourceSection.Images => typeof(ImagesPage), ResourceSection.Volumes => typeof(VolumesPage), ResourceSection.Networks => typeof(NetworksPage), _ => typeof(ResourcePage) };
            ResourceFrame.Navigate(pageType, section is ResourceSection.Containers or ResourceSection.Images or ResourceSection.Volumes or ResourceSection.Networks ? (object)session : section);
            if (ResourceFrame.Content is ResourcePage page) page.UpdateConnection(session.Snapshot);
        }
    }
}
