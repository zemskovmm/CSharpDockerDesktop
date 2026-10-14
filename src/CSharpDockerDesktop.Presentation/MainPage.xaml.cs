using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using Microsoft.UI.Xaml.Controls;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class MainPage : Page
{
    public MainPage(DesktopSession session)
    {
        InitializeComponent();
        EngineStatus.Text = session.EngineState switch
        {
            EngineConnectionState.NotConnected => "Not connected",
            _ => throw new InvalidOperationException("Unknown Engine state.")
        };
        ResourceNavigation.SelectedItem = ContainersItem;
    }

    private void OnResourceSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string sectionName }
            && Enum.TryParse<ResourceSection>(sectionName, out var section))
        {
            ResourceFrame.Navigate(typeof(ResourcePage), section);
        }
    }
}
