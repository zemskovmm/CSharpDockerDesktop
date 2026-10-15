using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class MainPage : Page
{
    private readonly DesktopSession session;
    private readonly ConnectionEditorView connection;
    private bool attached;
    private int connectionUpdateQueued;

    public MainPage(DesktopSession session)
    {
        this.session = session;
        connection = new(session);
        InitializeComponent();
        EndpointDraft.Text = connection.Draft;
        Loaded += (_, _) => { if (!attached) { session.Changed += OnSessionChanged; attached = true; } UpdateConnection(); };
        Unloaded += (_, _) => Detach();
        UpdateConnection();
        ResourceNavigation.SelectedItem = ContainersItem;
    }

    public void Detach()
    {
        session.Changed -= OnSessionChanged;
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
