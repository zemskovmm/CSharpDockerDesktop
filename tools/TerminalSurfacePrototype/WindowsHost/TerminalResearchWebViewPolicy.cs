// Experimental additions to the Uno 6.7.135 Win32 host. Not an upstream public API.
extern alias mswebview2;
using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DirectN;
using NativeWebView = mswebview2::Microsoft.Web.WebView2.Core;

namespace Uno.UI.Runtime.Skia.Win32;

public static class TerminalResearchWebViewPolicy
{
    public const string Protocol = "uno-6.7.135-custom-crash-v1";
    private static string? profile;
    private static int claimed;
    private static bool closing;
    private static Action? closeController;
    private static TaskCompletionSource? browserExited;
    // Keep callbacks and their environment alive until shutdown completes.
    private static object? environmentReference, exitHandlerReference;

    public static void Prepare()
    {
        if (profile is not null) throw new InvalidOperationException("Research host already prepared.");
        ValidateConfiguration();
        var backend = Environment.GetEnvironmentVariable("UNO_WEBVIEW2_BACKEND")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(backend)) backend = "webview2aot";
        if (backend is not ("webview2aot" or "microsoft.web.webview2"))
            throw new InvalidOperationException("Unsupported research backend.");
        Environment.SetEnvironmentVariable("UNO_WEBVIEW2_BACKEND", backend);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalSurfacePrototype", "WindowsResearch", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(folder)) throw new IOException("Research profile must be fresh.");
        Directory.CreateDirectory(folder);
        profile = folder;
    }

    private static void ValidateConfiguration()
    {
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (((string)variable.Key).StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 environment overrides are forbidden in this experiment.");
        if (FeatureConfiguration.WebView2.AllowSingleSignOnUsingOSPrimaryAccount ||
            !string.IsNullOrEmpty(FeatureConfiguration.WebView2.AdditionalBrowserArguments))
            throw new InvalidOperationException("Research host requires default SSO and no browser arguments.");
    }

    internal static string ClaimUserDataFolder()
    {
        ValidateConfiguration();
        if (profile is null || closing || Interlocked.Exchange(ref claimed, 1) != 0)
            throw new InvalidOperationException("Research host requires one prepared environment; retries are forbidden.");
        return profile;
    }

    internal static void RequireRuntime(string version)
    {
        // Use the pinned binding's own default compatibility floor, never an unsupported older-runtime fallback.
        if (!Version.TryParse(version, out var actual) || actual < Version.Parse(WebView2.Constants.CORE_WEBVIEW_TARGET_PRODUCT_VERSION))
            throw new InvalidOperationException("Research runtime is absent, prerelease, or below the pinned binding's compatibility floor.");
    }

    internal static WebView2.CoreWebView2EnvironmentOptions CreateAotOptions()
    {
        var options = new WebView2.CoreWebView2EnvironmentOptions();
        options.put_IsCustomCrashReportingEnabled(BOOL.TRUE).ThrowOnError();
        options.put_ExclusiveUserDataFolderAccess(BOOL.TRUE).ThrowOnError();
        BOOL custom = default, exclusive = default;
        options.get_IsCustomCrashReportingEnabled(ref custom).ThrowOnError();
        options.get_ExclusiveUserDataFolderAccess(ref exclusive).ThrowOnError();
        if (custom.Value == 0 || exclusive.Value == 0)
            throw new InvalidOperationException("Required WebView2 options were rejected.");
        return options;
    }

    internal static NativeWebView.CoreWebView2EnvironmentOptions CreateManagedOptions()
    {
        var options = new NativeWebView.CoreWebView2EnvironmentOptions
        {
            IsCustomCrashReportingEnabled = true,
            ExclusiveUserDataFolderAccess = true
        };
        if (!options.IsCustomCrashReportingEnabled || !options.ExclusiveUserDataFolderAccess)
            throw new InvalidOperationException("Required WebView2 options were rejected.");
        return options;
    }

    internal static void ValidateFolders(string userDataFolder, string dumpFolder)
    {
        if (profile is null || !Path.GetFullPath(userDataFolder).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Runtime changed the research profile location.");
        var relative = Path.GetRelativePath(profile, dumpFolder);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Crash dumps are outside the owned research profile.");
        if (browserExited is not null) throw new InvalidOperationException("Only one research environment is allowed.");
    }

    internal static void TrackAotEnvironment(WebView2.ICoreWebView2Environment environment)
    {
        // Environment11 dates from the custom-crash-reporting API generation. No interface fallback.
        var env = (WebView2.ICoreWebView2Environment11)environment;
        env.get_UserDataFolder(out var userData).ThrowOnError();
        env.get_FailureReportFolderPath(out var dumps).ThrowOnError();
        ValidateFolders(userData.ToStringAndDispose()!, dumps.ToStringAndDispose()!);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new WebView2.Utilities.CoreWebView2BrowserProcessExitedEventHandler((_, _) => exited.TrySetResult());
        var token = new WebView2.EventRegistrationToken();
        env.add_BrowserProcessExited(handler, ref token).ThrowOnError();
        environmentReference = env;
        exitHandlerReference = handler;
        browserExited = exited;
    }

    internal static void TrackManagedEnvironment(NativeWebView.CoreWebView2Environment environment)
    {
        ValidateFolders(environment.UserDataFolder, environment.FailureReportFolderPath);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        environmentReference = environment;
        browserExited = exited;
    }

    internal static void TrackController(Action close)
    {
        if (closing || closeController is not null) throw new InvalidOperationException("Unexpected research controller.");
        closeController = close;
    }

    public static async Task<bool> ShutdownAsync()
    {
        closing = true;
        var cleaned = await JoinCleanupAsync(closeController, browserExited?.Task, claimed != 0,
            () => { if (profile is not null) Directory.Delete(profile, recursive: true); }, TimeSpan.FromSeconds(10));
        if (cleaned)
        {
            closeController = null;
            environmentReference = exitHandlerReference = null;
        }
        return cleaned;
    }

    internal static async Task<bool> JoinCleanupAsync(Action? close, Task? exited, bool environmentWasClaimed,
        Action delete, TimeSpan timeout)
    {
        try
        {
            close?.Invoke();
            // Failed creation may have started a browser without returning its environment. Retain rather than guess.
            if (environmentWasClaimed && exited is null) return false;
            if (exited is not null) await exited.WaitAsync(timeout);
            await Task.Run(delete);
            return true;
        }
        catch { return false; } // No fallback, payload logging, uploader or deletion while a browser might be active.
    }
}
