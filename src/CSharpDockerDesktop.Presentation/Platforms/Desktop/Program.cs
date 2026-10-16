using Uno.UI.Hosting;

namespace CSharpDockerDesktop.Presentation;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            // Vulkan preference retains Uno's automatic OpenGL/software fallback.
            .UseWin32(builder => builder.RenderingBackend(Win32RenderingBackend.Vulkan))
            // AppKit selects Metal when available and otherwise uses software.
            .UseMacOS()
            .Build();
        host.Run();
    }
}
