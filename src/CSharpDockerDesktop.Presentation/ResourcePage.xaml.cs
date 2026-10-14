using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class ResourcePage : Page
{
    public ResourcePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        var section = (ResourceSection)args.Parameter;
        PageTitle.Text = section.ToString();
        PageDescription.Text = section switch
        {
            ResourceSection.Containers => "View containers exposed by your local Docker Engine.",
            ResourceSection.Images => "View images available on your local Docker Engine.",
            ResourceSection.Volumes => "View volumes managed by your local Docker Engine.",
            ResourceSection.Networks => "View networks managed by your local Docker Engine.",
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
        EmptyStateMessage.Text = "Waiting for connection status. Resource loading is pending implementation.";
    }
    public void UpdateConnection(ConnectionSnapshot snapshot)
    {
        EmptyStateTitle.Text = "Resource loading pending";
        EmptyStateMessage.Text = snapshot.State switch
        {
            EngineConnectionState.Connected => "The Engine is connected. This page will show its resources once resource loading is implemented.",
            EngineConnectionState.Connecting => "Connecting to the Engine. You can continue navigating while the request is pending.",
            EngineConnectionState.Lost => "The Engine connection was lost. Connection recovery status remains visible above.",
            EngineConnectionState.Unsupported => "The selected endpoint or Engine API is unsupported. Connection details explain the reason.",
            _ => "The Engine is unavailable. Recovery and manual retry are available above."
        };
    }
}
