using System.Diagnostics;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal sealed record RuntimeCommand(string Executable, string[] Arguments, bool WaitForExit, string? ColimaHome = null);
internal interface IRuntimeProcessRunner
{
    Task<bool> RunAsync(RuntimeCommand command, CancellationToken token);
}
internal sealed class RuntimeProcessRunner : IRuntimeProcessRunner
{
    public async Task<bool> RunAsync(RuntimeCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = command.WaitForExit,
            RedirectStandardError = command.WaitForExit,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        // Finder-launched apps often have a minimal PATH. Resolve runtime dependencies without a shell.
        if (OperatingSystem.IsMacOS())
            info.Environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        if (command.ColimaHome is not null) info.Environment["COLIMA_HOME"] = command.ColimaHome;
        using var process = Process.Start(info);
        if (process is null) return false;
        if (!command.WaitForExit) return true;
        // Drain without retaining or logging runtime output, which may contain user paths/configuration.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            // Stop only the owned launcher, never kill the runtime VM or other descendants.
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
            throw;
        }
    }
}

/// <summary>Only fixed known installations and arguments; endpoint text never becomes executable input.</summary>
public sealed class InstalledRuntimeLauncher : IInstalledRuntimeLauncher
{
    private readonly IRuntimeProcessRunner runner;
    private readonly Func<string, bool> exists;
    private readonly string home;
    private readonly string platform;
    private readonly string programFiles;
    public InstalledRuntimeLauncher() : this(new RuntimeProcessRunner(), File.Exists,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "unsupported",
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) { }
    internal InstalledRuntimeLauncher(IRuntimeProcessRunner runner, Func<string, bool> exists,
        string home, string platform, string programFiles = "")
    { this.runner = runner; this.exists = exists; this.home = home; this.platform = platform; this.programFiles = programFiles; }
    private IEnumerable<(InstalledRuntime Runtime, RuntimeCommand Command)> Candidates()
    {
        if (platform == "macos")
        {
            foreach (var executable in new[] { "/opt/homebrew/bin/colima", "/usr/local/bin/colima" })
                if (exists(executable) && exists(Path.Combine(home, ".colima/default/colima.yaml")))
                {
                    yield return (new("colima", "Colima (default Docker profile)", new("unix://" + Path.Combine(home, ".colima/default/docker.sock"), EngineTransport.UnixSocket)),
                        new(executable, ["start", "--profile", "default", "--activate=false", "--save-config=false"], true, Path.Combine(home, ".colima")));
                    break;
                }
            foreach (var app in new[] { "/Applications/Docker.app", Path.Combine(home, "Applications/Docker.app") })
                if (exists(Path.Combine(app, "Contents/MacOS/Docker Desktop")))
                {
                    yield return (new("docker-desktop", "Docker Desktop", new("unix://" + Path.Combine(home, ".docker/run/docker.sock"), EngineTransport.UnixSocket)),
                        new("/usr/bin/open", ["-a", app], true));
                    break;
                }
        }
        if (platform == "windows")
        {
            var exe = Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe");
            if (exists(exe)) yield return (new("docker-desktop", "Docker Desktop", EngineEndpoint.LocalWindows), new(exe, [], false));
        }
    }
    public IReadOnlyList<InstalledRuntime> Discover() => Candidates().Select(c => c.Runtime).ToArray();
    public async Task<RuntimeLaunchResult> StartAsync(InstalledRuntime runtime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidate = Candidates().FirstOrDefault(c => c.Runtime == runtime);
        if (candidate.Runtime is null)
            return new(false, "The selected runtime installation or existing profile is unavailable. Install/configure it separately, then reopen the app.");
        try
        {
            var success = await runner.RunAsync(candidate.Command, token).ConfigureAwait(false);
            return new(success, success ? "Runtime launch requested; Engine readiness must still be verified."
                : "The runtime launcher failed. Start the runtime manually to inspect its diagnostics.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return new(false, "The runtime could not be launched. Check installation and access permissions; no elevation was attempted."); }
    }
}
