using System.Reflection;

// The ordinary NuGet host must never silently start a Windows research surface.
internal static class WindowsResearchHost
{
    private static Type? policy;
    internal static Type RequirePatchedHost(Assembly assembly)
    {
        var type = assembly.GetType("Uno.UI.Runtime.Skia.Win32.TerminalResearchWebViewPolicy", throwOnError: true)!;
        if ((string?)type.GetField("Protocol")?.GetRawConstantValue() != "uno-6.7.135-custom-crash-v1")
            throw new InvalidOperationException("Incompatible research host.");
        return type;
    }
    public static void Prepare()
    {
        policy = RequirePatchedHost(Assembly.Load("Uno.UI.Runtime.Skia.Win32"));
        policy.GetMethod("Prepare")!.Invoke(null, null);
    }
    public static Task<bool> ShutdownAsync() => policy is null
        ? Task.FromResult(true)
        : (Task<bool>)policy.GetMethod("ShutdownAsync")!.Invoke(null, null)!;
}
