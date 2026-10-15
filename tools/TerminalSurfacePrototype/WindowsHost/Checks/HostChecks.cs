using System.Reflection;
using Uno.UI.Runtime.Skia.Win32;

var scenario = args[0];
void Require(bool condition) { if (!condition) throw new Exception("Research host check failed (payload omitted)."); }
void Reject(Action action)
{
    bool rejected = false;
    try { action(); } catch { rejected = true; }
    Require(rejected);
}
switch (scenario)
{
    case "options":
        // The actual WebView2Aot 1.5.0 managed options object, not a fake SDK object.
        using (var options = TerminalResearchWebViewPolicy.CreateAotOptions())
        {
            DirectN.BOOL enabled = default;
            options.get_IsCustomCrashReportingEnabled(ref enabled).ThrowOnError();
            Require(enabled.Value != 0);
            options.get_ExclusiveUserDataFolderAccess(ref enabled).ThrowOnError();
            Require(enabled.Value != 0);
        }
        if (OperatingSystem.IsWindows())
        {
            var managed = TerminalResearchWebViewPolicy.CreateManagedOptions();
            Require(managed.IsCustomCrashReportingEnabled && managed.ExclusiveUserDataFolderAccess);
        }
        else Console.WriteLine("Managed WebView2 options: compiled only; execution requires Windows.");
        break;
    case "runtime":
        TerminalResearchWebViewPolicy.RequireRuntime(WebView2.Constants.CORE_WEBVIEW_TARGET_PRODUCT_VERSION);
        Reject(() => TerminalResearchWebViewPolicy.RequireRuntime("100.0.0.0"));
        Reject(() => TerminalResearchWebViewPolicy.RequireRuntime("unknown"));
        Reject(() => TerminalResearchWebViewPolicy.RequireRuntime("999.0.0.0 dev"));
        Console.WriteLine($"Pinned WebView2Aot runtime compatibility floor: {WebView2.Constants.CORE_WEBVIEW_TARGET_PRODUCT_VERSION}.");
        break;
    case "browser-arguments":
        Uno.UI.FeatureConfiguration.WebView2.AdditionalBrowserArguments = "synthetic-override";
        Reject(TerminalResearchWebViewPolicy.Prepare);
        break;
    case "unprepared":
        Reject(() => TerminalResearchWebViewPolicy.ClaimUserDataFolder());
        break;
    case "unknown-backend":
        Environment.SetEnvironmentVariable("UNO_WEBVIEW2_BACKEND", "unsupported");
        Reject(TerminalResearchWebViewPolicy.Prepare);
        break;
    case "override":
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "synthetic-override");
        Reject(TerminalResearchWebViewPolicy.Prepare);
        break;
    case "sso":
        Uno.UI.FeatureConfiguration.WebView2.AllowSingleSignOnUsingOSPrimaryAccount = true;
        Reject(TerminalResearchWebViewPolicy.Prepare);
        break;
    case "profile":
        TerminalResearchWebViewPolicy.Prepare();
        var profile = TerminalResearchWebViewPolicy.ClaimUserDataFolder();
        Require(Directory.Exists(profile) && !Directory.EnumerateFileSystemEntries(profile).Any());
        TerminalResearchWebViewPolicy.ValidateFolders(profile, Path.Combine(profile, "Crashpad"));
        Reject(() => TerminalResearchWebViewPolicy.ValidateFolders(profile + "-different", Path.Combine(profile, "Crashpad")));
        Reject(() => TerminalResearchWebViewPolicy.ValidateFolders(profile, Path.GetDirectoryName(profile)!));
        Reject(() => TerminalResearchWebViewPolicy.ClaimUserDataFolder());
        Require(!await TerminalResearchWebViewPolicy.ShutdownAsync()); // No browser-exit evidence: retain.
        Require(Directory.Exists(profile));
        Directory.Delete(profile); // This test never created a browser.
        break;
    case "unused-profile":
        TerminalResearchWebViewPolicy.Prepare();
        Require(await TerminalResearchWebViewPolicy.ShutdownAsync());
        Reject(() => TerminalResearchWebViewPolicy.ClaimUserDataFolder());
        break;
    case "patched-host":
        Require(WindowsResearchHost.RequirePatchedHost(Assembly.LoadFrom(args[1])).GetMethod("ShutdownAsync") is not null);
        break;
    case "stock-host":
        Reject(() => WindowsResearchHost.RequirePatchedHost(Assembly.LoadFrom(args[1])));
        break;
    case "cleanup":
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closed = false, deleted = false;
        var cleanup = TerminalResearchWebViewPolicy.JoinCleanupAsync(() => closed = true, exited.Task, true,
            () => { Require(closed && exited.Task.IsCompletedSuccessfully); deleted = true; }, TimeSpan.FromSeconds(2));
        Require(closed && !deleted && !cleanup.IsCompleted);
        exited.SetResult();
        Require(await cleanup && deleted);
        foreach (var failedExit in new Task?[] { null, Task.FromException(new Exception()), new TaskCompletionSource().Task })
        {
            deleted = false;
            Require(!await TerminalResearchWebViewPolicy.JoinCleanupAsync(() => { }, failedExit, true,
                () => deleted = true, TimeSpan.FromMilliseconds(30)) && !deleted);
        }
        deleted = false;
        Require(!await TerminalResearchWebViewPolicy.JoinCleanupAsync(() => throw new Exception(), Task.CompletedTask, true,
            () => deleted = true, TimeSpan.FromSeconds(1)) && !deleted);
        Require(!await TerminalResearchWebViewPolicy.JoinCleanupAsync(() => { }, Task.CompletedTask, true,
            () => throw new IOException(), TimeSpan.FromSeconds(1)));
        break;
    default: throw new Exception("Unknown check.");
}
Console.WriteLine($"Research host check passed: {scenario}.");
