using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class MainPage : Page
{
    private readonly DesktopSession session;
    private string? displayedDaemonMessage;
    private bool attached;

    public MainPage(DesktopSession session)
    {
        this.session = session;
        InitializeComponent();
        Loaded += (_, _) => { if (!attached) { session.Changed += OnSessionChanged; attached = true; } UpdateConnection(); };
        Unloaded += (_, _) => Detach();
        UpdateConnection();
        ResourceNavigation.SelectedItem = ContainersItem;
    }

    public void Detach()
    {
        session.Changed -= OnSessionChanged;
        attached = false;
        if (ResourceFrame.Content is ContainersPage containers) containers.Detach();
    }
    private void OnSessionChanged(object? sender, EventArgs args)
        => DispatcherQueue.TryEnqueue(() => { if (attached) UpdateConnection(); });
    private void OnRetry(object sender, RoutedEventArgs args) => session.Retry();
    private void OnShowDaemonMessage(object sender, RoutedEventArgs args)
    {
        DaemonMessage.Text = session.Snapshot.Failure?.DaemonMessage ?? session.Snapshot.EventFailure?.DaemonMessage ?? "No daemon message available.";
        DaemonMessage.Visibility = Visibility.Visible;
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
        var recovery = s.AutomaticRecoveryPending ? " Automatic recovery is scheduled."
            : s.State is EngineConnectionState.Unavailable or EngineConnectionState.Lost ? " Use Retry connection when ready." : "";
        ConnectionDiagnostic.Text = s.Failure is { } failure
            ? $"{FailureLabel(failure.Kind)}: {failure.Description}{recovery}"
            : s.EventFailure is { } eventFailure
                ? $"Automatic container updates interrupted ({eventFailure.Kind}): {eventFailure.Description} "
                    + (s.EventRecoveryPending ? "Observation recovery is scheduled. " : "Use Retry connection to restore observation. ")
                    + "Refresh containers and navigation remain available."
            : s.State == EngineConnectionState.Connecting ? "Opening the default local Windows connection. Navigation remains available."
            : s.State == EngineConnectionState.Connected ? "Engine connected. Containers supports browsing, Overview, Inspect and lifecycle actions; Images, Volumes and Networks are pending." : "";
        var daemonMessage = s.Failure?.DaemonMessage ?? s.EventFailure?.DaemonMessage;
        if (!Equals(displayedDaemonMessage, daemonMessage))
        {
            DaemonMessage.Visibility = Visibility.Collapsed;
            DaemonMessage.Text = "";
            displayedDaemonMessage = daemonMessage;
        }
        ShowDaemonMessage.Visibility = daemonMessage is null ? Visibility.Collapsed : Visibility.Visible;
        ConnectionDetails.Text = FormatDetails(s);
        if (ResourceFrame.Content is ResourcePage page) page.UpdateConnection(s);
    }

    private static string FailureLabel(EngineConnectionFailureKind kind) => kind switch
    {
        EngineConnectionFailureKind.MissingEndpoint => "Pipe unavailable",
        EngineConnectionFailureKind.AccessDenied => "Access denied",
        EngineConnectionFailureKind.IncompatibleApi => "Incompatible Engine API",
        EngineConnectionFailureKind.UnsupportedEndpoint => "Unsupported endpoint or platform",
        EngineConnectionFailureKind.MalformedResponse => "Invalid Engine response",
        EngineConnectionFailureKind.DaemonHttpError => "Daemon error",
        EngineConnectionFailureKind.TransientTransportFailure => "Transport interrupted",
        _ => "Connection error"
    };

    private static string FormatDetails(ConnectionSnapshot s)
    {
        var m = s.Metadata;
        static string Text(string? value) => string.IsNullOrEmpty(value) ? "unknown" : value;
        static string Flag(bool? value) => value switch { true => "available", false => "unavailable", _ => "unknown" };
        var c = m?.Capabilities;
        return $"Selection: Default local Windows pipe (no Docker CLI context selected)\nEndpoint: {s.Endpoint.Address}\nTransport: {s.Endpoint.Transport}\nEngine version: {Text(m?.EngineVersion)}\nDaemon API: {m?.Api.DaemonMinimum.ToString() ?? "unknown"}–{m?.Api.DaemonMaximum.ToString() ?? "unknown"}\nNegotiated API: {m?.Api.Negotiated.ToString() ?? "unknown"}\nOperating system: {Text(m?.OperatingSystem)}\nOS type: {Text(m?.OSType)}\nRuntime OS: {Text(m?.RuntimeOS)}\nHost architecture: {Text(m?.HostArchitecture)}\nRuntime architecture: {Text(m?.RuntimeArchitecture)}\nCapabilities reported by daemon:\nMemory limit: {Flag(c?.MemoryLimit)}; swap limit: {Flag(c?.SwapLimit)}\nCPU period: {Flag(c?.CpuCfsPeriod)}; CPU quota: {Flag(c?.CpuCfsQuota)}; CPU shares: {Flag(c?.CpuShares)}; CPU set: {Flag(c?.CpuSet)}\nPID limit: {Flag(c?.PidsLimit)}; IPv4 forwarding: {Flag(c?.IPv4Forwarding)}; experimental: {Flag(c?.Experimental)}\nSwarm state: {Text(c?.SwarmLocalNodeState)}";
    }

    private void OnResourceSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string sectionName }
            && Enum.TryParse<ResourceSection>(sectionName, out var section))
        {
            if (ResourceFrame.Content is ContainersPage containers) containers.Detach();
            ResourceFrame.Navigate(section == ResourceSection.Containers ? typeof(ContainersPage) : typeof(ResourcePage),
                section == ResourceSection.Containers ? (object)session : section);
            if (ResourceFrame.Content is ResourcePage page) page.UpdateConnection(session.Snapshot);
        }
    }
}
