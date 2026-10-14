using Uno.UI.Hosting;

namespace CSharpDockerDesktop.Presentation;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWin32()
            // Allows local UI development checks; Windows x64 is the delivery baseline.
            .UseMacOS()
            .Build();
        host.Run();
    }
}
