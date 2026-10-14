using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Infrastructure.Docker;
using Microsoft.UI.Xaml;

namespace CSharpDockerDesktop.Presentation;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    private DesktopSession? _session;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Composition root: views see Application contracts, never Docker protocol operations.
        _session = new DesktopSession(new DockerEngineConnectionClient());
        _window = new Window
        {
            Title = "CSharpDockerDesktop",
            Content = new MainPage(_session)
        };
        _window.Closed += OnClosed;
        _window.Activate();
        _session.Start();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (_window?.Content is MainPage page) page.Detach();
        if (_session is { } session) await session.DisposeAsync();
    }
}
