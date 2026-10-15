using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;
using Windows.ApplicationModel.DataTransfer;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--check-fixture"]) { FixtureChecks.RunAsync().GetAwaiter().GetResult(); return; }
        if (OperatingSystem.IsWindows())
        {
            try { WindowsResearchHost.Prepare(); }
            catch
            {
                Console.Error.WriteLine("Windows research host unavailable or unsafe; no browser was started. See docs/terminal-surface-research.md.");
                Environment.ExitCode = 2;
                return;
            }
        }
        // Uno's macOS backend logs ExecuteScriptAsync arguments at Debug level.
        // Do not initialize any logging provider, telemetry or crash reporter here.
        Uno.UI.FeatureConfiguration.WebView2.EnableDevTools = false;
        UnoPlatformHostBuilder.Create().App(() => new PrototypeApp()).UseWin32().UseMacOS().Build().Run();
    }
}

internal sealed class PrototypeApp : Microsoft.UI.Xaml.Application
{
    private Window? window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var view = new PrototypeView();
        window = new Window { Title = "Isolated terminal surface research — controlled fixture", Content = view };
        if (OperatingSystem.IsWindows())
        {
            bool closing = false, closed = false;
            window.AppWindow.Closing += async (_, e) =>
            {
                if (closed) return;
                e.Cancel = true;
                if (closing) return;
                closing = true;
                view.IsHitTestVisible = false;
                try { await view.ShutdownAsync(); }
                finally
                {
                    if (!await WindowsResearchHost.ShutdownAsync())
                        Console.Error.WriteLine("Research profile retained: browser exit or profile cleanup could not be confirmed.");
                    closed = true;
                    window.Close();
                }
            };
        }
        else window.Closed += async (_, _) => await view.ShutdownAsync();
        window.Activate();
    }
}

internal sealed class PrototypeView : Grid
{
    private readonly ControlledTerminal fixture = new();
    private readonly IContainerTerminal terminal;
    private readonly Microsoft.UI.Xaml.Controls.WebView2 web = new();
    private readonly TextBlock status = new() { Text = "Loading local terminal assets…", TextWrapping = TextWrapping.Wrap };
    private readonly SemaphoreSlim transition = new(1);
    private readonly Channel<(long Id, byte[] Bytes)> input = Channel.CreateBounded<(long, byte[])>(16);
    private CancellationTokenSource pumpLifetime = new();
    private Task pump = Task.CompletedTask;
    private Task producer = Task.CompletedTask;
    private readonly Task inputPump;
    private TaskCompletionSource? ack;
    private long generation, sequence, awaitingSequence;
    private bool bootstrapping = true, ready, holdDelivery, shuttingDown;
    private int blockedNavigations, blockedPopups;
    private bool? fetchBlocked;
    private bool? sandboxPopupBlocked;
    private long copyRequested;

    public PrototypeView()
    {
        terminal = fixture;
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Add("New fixture", async () => await StartAsync(false));
        Add("10,000 lines", async () => await StartAsync(true));
        Add("Close", CloseAsync);
        Add("80 × 24", () => ResizeAsync(80, 24));
        Add("100 × 30", () => ResizeAsync(100, 30));
        Add("Ctrl+C", async () => { await web.ExecuteScriptAsync("surface.controlC()"); await ReportAsync(); });
        Add("Copy selection", async () =>
        {
            copyRequested = generation;
            await web.ExecuteScriptAsync("surface.selection()");
        });
        Add("Paste ≤4 KiB", async () =>
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (Encoding.UTF8.GetByteCount(text) <= 4096)
                await web.ExecuteScriptAsync($"surface.paste({JsonSerializer.Serialize(text)})");
            else status.Text = "Paste exceeds the fixture's 4 KiB input limit.";
        });
        Add("Report bounds", ReportAsync);
        var extra = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pause = new Button { Content = "Hold delivery" };
        pause.Click += (_, _) => { holdDelivery = !holdDelivery; pause.Content = holdDelivery ? "Resume delivery" : "Hold delivery"; };
        extra.Children.Add(pause);
        Extra("Close/replace pending", async () =>
        {
            holdDelivery = true; await StartAsync(true); await Task.Delay(100);
            await StartAsync(false); holdDelivery = false; pause.Content = "Hold delivery";
            await Task.Delay(500); await ReportAsync();
        });
        Extra("Select fixture", async () => await web.ExecuteScriptAsync("surface.selectFixture()"));
        Extra("Probe popup", async () => { await web.ExecuteScriptAsync("surface.probePopup()"); await Task.Delay(150); await ReportAsync(); });
        Extra("Probe navigation", async () => { await web.ExecuteScriptAsync("surface.probeNavigation()"); await Task.Delay(150); await ReportAsync(); });
        Extra("Probe fetch", async () => { await web.ExecuteScriptAsync("surface.probeFetch()"); await Task.Delay(150); await ReportAsync(); });
        Extra("Probe URL handler", async () => { await web.ExecuteScriptAsync("surface.probeExternal()"); await Task.Delay(150); await ReportAsync(); });
        Extra("1 MiB cap", () => StartAsync(true, 100000));
        var top = new StackPanel { Spacing = 6 }; top.Children.Add(buttons); top.Children.Add(extra);
        Children.Add(top); SetRow(status, 1); Children.Add(status); SetRow(web, 2); Children.Add(web);
        Loaded += async (_, _) => await InitializeAsync();
        inputPump = ConsumeInputAsync();

        void Add(string label, Func<Task> action) => Button(buttons, label, action);
        void Extra(string label, Func<Task> action) => Button(extra, label, action);
        void Button(StackPanel parent, string label, Func<Task> action)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) =>
            {
                if (!ready) return;
                try { await action(); }
                catch { status.Text = "Prototype operation failed; no payload was logged."; }
            };
            parent.Children.Add(button);
        }
    }

    private async Task InitializeAsync()
    {
        if (ready || shuttingDown) return;
        try
        {
            await web.EnsureCoreWebView2Async();
            if (shuttingDown) return;
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                // Only our initial in-memory document may load. Never grant data: generally.
                if (bootstrapping && (e.Uri == "about:blank" || e.Uri == bootstrapUri)) return;
                e.Cancel = true; blockedNavigations++;
            };
            web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; blockedPopups++; };
            web.WebMessageReceived += (_, e) => Receive(e.WebMessageAsJson);
            var html = BuildHtml();
            bootstrapUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            web.NavigateToString(html);
        }
        catch { status.Text = "WebView unavailable on this host; no terminal conformance established."; }
    }
    private string? bootstrapUri;
    private static string BuildHtml()
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        string Asset(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TerminalSurfacePrototype.Web.{name}")!;
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
        var inner = "<!doctype html><html><head><meta charset='utf-8'>"
            + $"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; connect-src 'none'; img-src 'none'; font-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'\">"
            + "<style>html,body{margin:0;background:#111;height:100%;overflow:hidden}#terminal{height:100%}" + Asset("xterm.css") + "</style></head><body><div id='terminal'></div>"
            + $"<script nonce='{nonce}'>" + Asset("xterm.js") + "</script>"
            + $"<script nonce='{nonce}'>" + Asset("surface.js") + "</script></body></html>";
        // frame-src 'none' also blocks subsequent frame navigation to remote URLs.
        // srcdoc is local inline content. No allow-same-origin/popups/top-navigation/downloads/forms.
        return "<!doctype html><html><head><meta charset='utf-8'>"
            + $"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; frame-src 'none'; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'\">"
            + "<style>html,body{margin:0;height:100%;background:#111}iframe{border:0;width:100%;height:100%}</style>"
            + "</head><body><iframe id='terminal-frame' sandbox='allow-scripts' srcdoc=\""
            + System.Net.WebUtility.HtmlEncode(inner) + "\"></iframe>"
            + $"<script nonce='{nonce}'>" + Asset("bridge.js") + "</script></body></html>";
    }
    private void Receive(string json)
    {
        if (shuttingDown || json.Length > 12000) return;
        try
        {
            using var outer = JsonDocument.Parse(json);
            // Uno's Windows/macOS string bridge wraps our JSON string in JSON.
            using var message = JsonDocument.Parse(outer.RootElement.ValueKind == JsonValueKind.String
                ? outer.RootElement.GetString()! : json);
            var root = message.RootElement;
            var kind = root.GetProperty("kind").GetString();
            if (kind == "ready") { bootstrapping = false; ready = true; status.Text = "Ready. Controlled echo fixture only; no Docker connection."; return; }
            var id = root.GetProperty("generation").GetInt64();
            if (id != generation) return;
            if (kind == "ack" && root.GetProperty("sequence").GetInt64() == awaitingSequence) ack?.TrySetResult();
            if (kind == "fetch") fetchBlocked = root.GetProperty("blocked").GetBoolean();
            if (kind == "sandboxPopup") sandboxPopupBlocked = root.GetProperty("blocked").GetBoolean();
            if (kind == "copy" && copyRequested == id)
            {
                copyRequested = 0;
                var text = root.GetProperty("data").GetString() ?? "";
                if (Encoding.UTF8.GetByteCount(text) <= 4096) { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
                else status.Text = "Selection exceeds the fixture's 4 KiB copy limit.";
            }
            if (kind == "input")
            {
                var bytes = Convert.FromBase64String(root.GetProperty("data").GetString()!);
                if (bytes.Length > 4096 || !input.Writer.TryWrite((id, bytes))) status.Text = "Input capacity reached; input was rejected.";
            }
            if (kind == "capacity") status.Text = "Input exceeds 4 KiB; input was rejected.";
        }
        catch { status.Text = "Invalid prototype bridge message rejected."; }
    }
    private async Task StartAsync(bool sustained, int lineCount = 10000)
    {
        await transition.WaitAsync();
        try
        {
            if (shuttingDown) return;
            await StopCoreAsync();
            generation = terminal.OpenContainerTerminal("controlled", [], 80, 24).SessionId;
            await web.ExecuteScriptAsync($"surface.start({generation})");
            pump = PumpAsync(generation, pumpLifetime.Token);
            producer = fixture.ProduceAsync(sustained, lineCount);
            status.Text = "Controlled fixture active. Type, select/copy/paste, resize, hold, close or replace.";
        }
        finally { transition.Release(); }
    }
    public async Task CloseAsync()
    {
        await transition.WaitAsync();
        try { await StopCoreAsync(); status.Text = "Closed; pending output revoked."; }
        finally { transition.Release(); }
    }
    public async Task ShutdownAsync()
    {
        shuttingDown = true;
        ready = false;
        input.Writer.TryComplete();
        await CloseAsync();
        await inputPump;
    }
    private async Task StopCoreAsync()
    {
        // Negative revoked identity cannot collide with a new positive Application session.
        generation = -Math.Abs(generation) - 1;
        copyRequested = 0;
        var previousLifetime = pumpLifetime;
        pumpLifetime = new();
        previousLifetime.Cancel(); ack?.TrySetCanceled();
        await terminal.CloseContainerTerminalAsync();
        try { await pump; } catch (OperationCanceledException) { }
        await producer;
        if (ready) await web.ExecuteScriptAsync($"surface.close({generation})");
        previousLifetime.Dispose();
    }
    private async Task PumpAsync(long id, CancellationToken token)
    {
        int sessionBytes = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var output = await terminal.ReadContainerTerminalOutputAsync(id, token);
                if (output.Revoked || output.Completed) break;
                while (holdDelivery) await Task.Delay(20, token);
                // This continuation is on the UI dispatcher; check immediately before dispatch.
                if (id != generation || !terminal.IsContainerTerminalCurrent(id, "controlled", 1)) break;
                // A row cap alone cannot bound strings of combining characters in one cell.
                // Research-only hard lifetime budget: end this fixture rather than retain unlimited parser state.
                sessionBytes += output.Bytes.Length;
                if (sessionBytes > 1048576 || output.Bytes.Length > 4096)
                {
                    generation = -Math.Abs(generation) - 1;
                    await terminal.CloseContainerTerminalAsync();
                    await web.ExecuteScriptAsync($"surface.close({generation})");
                    status.Text = "Fixture ended at its 1 MiB output lifetime limit; open a new fixture to continue.";
                    break;
                }
                ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
                awaitingSequence = ++sequence;
                await web.ExecuteScriptAsync($"surface.write({id},{sequence},{JsonSerializer.Serialize(Convert.ToBase64String(output.Bytes.AsSpan()))})");
                // One <=4096-byte write outstanding until xterm has parsed it. No unbounded JS queue.
                await ack.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
        }
        catch (OperationCanceledException) { }
        catch { status.Text = "Surface delivery stopped (timeout or host failure); no payload was logged."; }
    }
    private async Task ConsumeInputAsync()
    {
        await foreach (var item in input.Reader.ReadAllAsync())
            if (item.Id == generation && terminal.IsContainerTerminalCurrent(item.Id, "controlled", 1))
                await terminal.WriteContainerTerminalAsync(item.Id, item.Bytes);
    }
    private async Task ResizeAsync(int columns, int rows)
    {
        var id = generation;
        if (!terminal.IsContainerTerminalCurrent(id, "controlled", 1)) return;
        await web.ExecuteScriptAsync($"surface.resize({columns},{rows})");
        if (id == generation) await terminal.ResizeContainerTerminalAsync(id, columns, rows);
        await ReportAsync();
    }
    private async Task ReportAsync()
    {
        if (generation <= 0) return;
        await web.ExecuteScriptAsync("surface.requestStats()");
        await Task.Delay(100);
        var report = await web.ExecuteScriptAsync("surface.stats()");
        status.Text = $"Surface metadata: {report}; fixture dimensions {fixture.LastColumns}×{fixture.LastRows}; input bytes {fixture.InputBytes}; Ctrl+C bytes {fixture.ControlCCount}; pending chunks {fixture.PendingChunks}/4; producer complete {fixture.ProducerCompleted}; blocked navigation {blockedNavigations}; blocked popups {blockedPopups}; fetch blocked {fetchBlocked}; sandbox blocked popup {sandboxPopupBlocked}.";
    }
}
