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
        EmptyStateMessage.Text = $"Connect to a Docker Engine to load {section.ToString().ToLowerInvariant()}. No resource data has been loaded.";
    }
}
