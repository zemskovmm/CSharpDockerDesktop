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
            .UseMacOS()
            .Build();
        host.Run();
    }
}
