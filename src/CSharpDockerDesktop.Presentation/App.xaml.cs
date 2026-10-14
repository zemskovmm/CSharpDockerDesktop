using CSharpDockerDesktop.Application;
using Microsoft.UI.Xaml;

namespace CSharpDockerDesktop.Presentation;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window
        {
            Title = "CSharpDockerDesktop",
            Content = new MainPage(new DesktopSession())
        };
        _window.Activate();
    }
}
