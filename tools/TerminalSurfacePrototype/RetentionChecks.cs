using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

// Synthetic-only native Uno/WKWebView research. No arbitrary data/file/URL input.
internal static class RetentionChecks
{
    internal static string? Workload;
    internal static readonly string[] Workloads = ["combined", "normal", "sustained", "combining", "width-cache", "stock-links", "links", "stock-alternate", "alternate", "attributes", "osc", "dcs", "csi", "a11y", "lifecycle", "cutoff"];
    internal static int OutputLimit => Workload is null or "cutoff" ? 1048576 : 67108864;
}

internal sealed partial class ControlledTerminal
{
    internal long ResearchBytes { get; private set; }
    internal async Task ProduceResearchAsync(string workload)
    {
        var writer = output.Writer; var token = lifetime.Token;
        ResearchBytes = 0;
        // Prime-sized fragments cut UTF-8 and escape sequences independently of writes.
        var chunk = new byte[4093]; int used = 0;
        try
        {
            switch (workload)
            {
                case "normal": case "sustained":
                    for (int i = 0; i < (workload == "sustained" ? 1200000 : 300000); i++) await Add($"line {i:D6} café λ 世界 🐳 \x1b[31mRED\x1b[0m\r\n");
                    break;
                case "combining":
                    await Add("a");
                    for (int i = 0; i < 131072; i++) await Add("\u0301");
                    break;
                case "width-cache":
                    for (int i = 0; i < 1200; i++)
                    {
                        await Add($"\ra{(char)(0x300 + i % 100)}{(char)(0x300 + i / 100)}");
                        await Send();
                        await Task.Delay(20, token); // Ensure many distinct cells actually render.
                    }
                    break;
                case "stock-links": case "links":
                    for (int i = 0; i < 8000; i++)
                        await Add($"\x1b]8;id={i};https://example.invalid/{i}\x1b\\X\x1b]8;;\x1b\\\r");
                    break;
                case "stock-alternate": case "alternate":
                    for (int i = 0; i < 8000; i++)
                        await Add($"\x1b[?1049h\x1b]8;;https://example.invalid/{i}\aX\u0301\x1b[?1049l");
                    break;
                case "attributes":
                    for (int i = 0; i < 50000; i++) await Add($"\x1b[4:3;58:2::{i % 256}:3:4mX\x1b[0m\r");
                    break;
                case "osc": case "dcs": case "csi":
                    await Add(workload == "osc" ? "\x1b]2;" : workload == "dcs" ? "\x1bP$q" : "\x1b[");
                    for (int i = 0; i < 3072; i++) await Add(new string(workload == "csi" ? '1' : 'x', 4096));
                    break;
                case "a11y":
                    for (int i = 0; i < 262144; i++) await Add("X\r");
                    break;
                default: throw new InvalidOperationException("Unknown fixed research workload.");
            }
            if (used != 0) await Send();
            ProducerCompleted = true;
        }
        catch (OperationCanceledException) { }
        catch (System.Threading.Channels.ChannelClosedException) { }

        async Task Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = Math.Min(chunk.Length - used, bytes.Length - offset);
                bytes.AsSpan(offset, count).CopyTo(chunk.AsSpan(used)); used += count; offset += count;
                if (used == chunk.Length) await Send();
            }
        }
        async Task Send()
        {
            int count = used;
            int first = workload == "width-cache" ? count - 1 : count;
            await writer.WriteAsync(chunk.AsSpan(0, first).ToArray(), token);
            ResearchBytes += first;
            if (first != count)
            {
                await writer.WriteAsync([chunk[first]], token); // Split the last UTF-8 combining scalar.
                ResearchBytes++;
            }
            used = 0;
            if (workload == "sustained") await Task.Delay(10, token);
        }
    }
}

internal sealed partial class PrototypeView
{
    private TaskCompletionSource<JsonElement>? researchStats;
    private readonly SemaphoreSlim researchStatsGate = new(1);
    private long researchStatsSequence;
    private long deliveredBytes, nextResearchSample = 262144;
    private readonly Stopwatch researchClock = new();

    private async Task<JsonElement> ReadResearchStatsAsync()
    {
        await researchStatsGate.WaitAsync();
        try
        {
            researchStats = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await web.ExecuteScriptAsync($"surface.requestStats({++researchStatsSequence})");
            return await researchStats.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { researchStatsGate.Release(); }
    }
    private async Task<JsonElement> RecordResearchStatsAsync(string stage)
    {
        var result = await ReadResearchStatsAsync();
        // Fixed metadata only. This mode must never print fixture/input/selection content.
        Console.WriteLine("RETENTION " + JsonSerializer.Serialize(new { workload = RetentionChecks.Workload,
            stage, elapsedSeconds = Math.Round(researchClock.Elapsed.TotalSeconds, 2), deliveredBytes,
            managedBytes = GC.GetTotalMemory(false), processRssBytes = Process.GetCurrentProcess().WorkingSet64,
            pendingChunks = fixture.PendingChunks, stats = result }));
        return result;
    }
    private async Task RunRetentionChecksAsync()
    {
        int exit = 0;
        researchClock.Start();
        IsHitTestVisible = false;
        try
        {
            await StartAsync(false);
            await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
            Require((await ReadResearchStatsAsync()).GetProperty("utf8").GetBoolean());
            await RecordResearchStatsAsync("baseline");
            if (RetentionChecks.Workload == "combined") await CheckCombinedCellsAsync();
            else if (RetentionChecks.Workload == "combining")
            {
                await OpenResearchAsync();
                long failedId = generation;
                producer = fixture.ProduceResearchAsync("combining");
                await WaitForAsync(() => combinedFailures == 1);
                await failureCleanup.WaitAsync(TimeSpan.FromSeconds(10));
                Require(pump.IsCompleted && producer.IsCompleted && !fixture.ProducerCompleted
                    && fixture.ResearchBytes >= 4 * 4093 && lastCombinedFailure == failedId && generation < 0);
                await StartAsync(false);
                await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
                await RecordResearchStatsAsync("combined-failure-reopened");
            }
            else if (RetentionChecks.Workload == "lifecycle") await CheckResearchLifecycleAsync();
            else if (RetentionChecks.Workload == "cutoff")
            {
                await StartAsync(true, 100000);
                await WaitForAsync(() => generation < 0);
                await pump.WaitAsync(TimeSpan.FromSeconds(10));
                await producer.WaitAsync(TimeSpan.FromSeconds(10));
                Require(deliveredBytes <= 1048576 && !terminal.IsContainerTerminalCurrent(generation, "controlled", 1));
                await StartAsync(false);
                await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
                await RecordResearchStatsAsync("cutoff-reopened");
            }
            else
            {
                await StopCoreAsync();
                generation = terminal.OpenContainerTerminal("controlled", [], 80, 24).SessionId;
                deliveredBytes = 0;
                await web.ExecuteScriptAsync($"surface.start({generation},{JsonSerializer.Serialize(RetentionChecks.Workload)})");
                pump = PumpAsync(generation, pumpLifetime.Token);
                producer = fixture.ProduceResearchAsync(RetentionChecks.Workload!);
                await producer.WaitAsync(TimeSpan.FromMinutes(10));
                await WaitForAsync(() => deliveredBytes == fixture.ResearchBytes);
                await Task.Delay(1200); // Allow renderer/accessibility scheduling, not a forced GC.
                var result = await RecordResearchStatsAsync("incomplete-or-final");
                Require(result.GetProperty("received").GetInt64() == fixture.ResearchBytes);
                Require(result.GetProperty("normalLines").GetInt32() <= 224);
                Require(result.GetProperty("alternateLines").GetInt32() <= 24);
                switch (RetentionChecks.Workload)
                {
                    case "normal": case "sustained": Require(deliveredBytes > 1048576); break;
                    case "width-cache": Require(result.GetProperty("maxCombinedUnits").GetInt32() == 3
                        && result.GetProperty("widthCacheEntries").GetInt32() > 8); break;
                    case "stock-links": Require(result.GetProperty("links").GetInt32() == 8000); break;
                    case "links": case "alternate": case "stock-alternate": Require(result.GetProperty("links").GetInt32() == 0); break;
                    case "osc": case "dcs": Require(result.GetProperty("parserLimited").GetBoolean()); break;
                    case "csi": Require(result.GetProperty("params").GetInt32() <= 32); break;
                    case "a11y": Require(result.GetProperty("a11yUnits").GetInt32() >= 262144); break;
                }
                // Terminate incomplete sequences using cancellation characters, then prove recovery.
                await fixture.WriteContainerTerminalAsync(generation, Encoding.UTF8.GetBytes("\x18\x1b[0m\r\nRECOVERED\r\n"));
                await WaitForAsync(() => deliveredBytes == fixture.ResearchBytes + 18);
                var recovered = await ReadResearchStatsAsync();
                Require(recovered.GetProperty("parserState").GetInt32() == 0 && recovered.GetProperty("recovered").GetBoolean());
                await RecordResearchStatsAsync("recovered");
            }
            await Task.Delay(3000);
            await RecordResearchStatsAsync("idle");
            Console.WriteLine("RETENTION_CHECK_PASS " + RetentionChecks.Workload);
        }
        catch (Exception exception)
        {
            // No exception message: native failures can include script/payload text.
            Console.WriteLine("RETENTION_CHECK_FAIL " + exception.GetType().Name); exit = 1;
        }
        finally
        {
            await ShutdownAsync();
            // This runner is macOS development evidence only. Windows requires its
            // separate native browser-exit/profile-cleanup procedure and is refused at entry.
            Environment.Exit(exit);
        }
    }
    private async Task OpenResearchAsync()
    {
        await transition.WaitAsync();
        try
        {
            await StopCoreAsync();
            generation = terminal.OpenContainerTerminal("controlled", [], 80, 24).SessionId;
            deliveredBytes = 0;
            await web.ExecuteScriptAsync($"surface.start({generation}," + JsonSerializer.Serialize("combined") + ")");
            pump = PumpAsync(generation, pumpLifetime.Token);
        }
        finally { transition.Release(); }
    }
    private async Task SendCombinedAsync(string text, bool fragmented = false)
    {
        long target = deliveredBytes + Encoding.UTF8.GetByteCount(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (fragmented)
            foreach (byte value in bytes) await fixture.WriteContainerTerminalAsync(generation, new byte[] { value });
        else await fixture.WriteContainerTerminalAsync(generation, bytes);
        await WaitForAsync(() => deliveredBytes == target);
    }
    private async Task CheckCombinedCellsAsync()
    {
        foreach (bool alternate in new[] { false, true })
        {
            await OpenResearchAsync();
            if (alternate) await SendCombinedAsync("\x1b[?1049h", true);
            await SendCombinedAsync("a" + new string('\u0301', 255), true);
            await RecordResearchStatsAsync(alternate ? "alternate-exact" : "normal-exact");
            Require((await ReadResearchStatsAsync()).GetProperty("maxCombinedUnits").GetInt32() == 256
                && (await ReadResearchStatsAsync()).GetProperty("combinedExact").GetBoolean());
            await SendCombinedAsync("\x1b[1G\x1b[2@\x1b[1G\x1b[P", true);
            await RecordResearchStatsAsync(alternate ? "alternate-edits" : "normal-edits");
            Require((await ReadResearchStatsAsync()).GetProperty("maxCombinedUnits").GetInt32() == 256
                && (await ReadResearchStatsAsync()).GetProperty("combinedExact").GetBoolean());
            await ResizeAsync(40, 10); await ResizeAsync(100, 30);
            Require((await ReadResearchStatsAsync()).GetProperty("maxCombinedUnits").GetInt32() == 256
                && (await ReadResearchStatsAsync()).GetProperty("combinedExact").GetBoolean());
            await SendCombinedAsync("\r\x1b[2Kb\u0301", true);
            await RecordResearchStatsAsync(alternate ? "alternate-overwrite" : "normal-overwrite");
            Require((await ReadResearchStatsAsync()).GetProperty("maxCombinedUnits").GetInt32() <= 256
                && (await ReadResearchStatsAsync()).GetProperty("combinedShort").GetBoolean());
            // Force wrapping and a real normal-screen reflow with the combined cell in the wrapped row.
            await SendCombinedAsync("\r\x1b[2K" + new string('X', alternate ? 10 : 99) + "a\u0301Y\r\n");
            await ResizeAsync(40, 10); await ResizeAsync(100, 30);
            await RecordResearchStatsAsync(alternate ? "alternate-resize" : "normal-reflow");
            Require((await ReadResearchStatsAsync()).GetProperty("maxCombinedUnits").GetInt32() <= 256
                && (await ReadResearchStatsAsync()).GetProperty("combinedReflow").GetBoolean());
            if (alternate)
            {
                await SendCombinedAsync("\x1b[?1049l", true);
                Require((await ReadResearchStatsAsync()).GetProperty("alternateLines").GetInt32() == 0);
            }
            // Fresh exact-limit cell, then incomplete and completing UTF-8 fragments.
            await OpenResearchAsync();
            if (alternate) await SendCombinedAsync("\x1b[?1049h", true);
            await SendCombinedAsync("a" + new string('\u0301', 255), true);
            long stale = generation; int before = combinedFailures;
            await fixture.WriteContainerTerminalAsync(stale, new byte[] { 0xcc });
            await WaitForAsync(() => deliveredBytes == (alternate ? 520 : 512));
            await fixture.WriteContainerTerminalAsync(stale, new byte[] { 0x81, 0x58 });
            await WaitForAsync(() => combinedFailures == before + 1);
            await failureCleanup.WaitAsync(TimeSpan.FromSeconds(10));
            Require(generation < 0 && pump.IsCompleted && producer.IsCompleted && lastCombinedFailure == stale);
            Require(status.Text == "Prototype failed: combined-cell allocation limit exceeded; session closed.");
            await Task.WhenAll(CloseAsync(), StartAsync(false));
            await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
            await web.ExecuteScriptAsync($"surface.write({stale},0,'zIFY')");
            await FailCombinedSurfaceAsync(stale); // Delayed native failure must not close replacement.
            await Task.Delay(100);
            Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);
        }
        // Replacement queued while the failing browser fragment is in flight.
        await OpenResearchAsync();
        await SendCombinedAsync("a" + new string('\u0301', 255));
        long pendingId = generation;
        await fixture.WriteContainerTerminalAsync(pendingId, Encoding.UTF8.GetBytes("\u0301X"));
        await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await Task.Delay(100);
        Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);
        await CheckResearchLifecycleAsync();
        await RecordResearchStatsAsync("combined-complete");
    }
    private async Task CheckResearchLifecycleAsync()
    {
        var initial = await ReadResearchStatsAsync();
        Require(initial.GetProperty("cursorEdit").GetBoolean());
        await web.ExecuteScriptAsync("surface.selectFixture()");
        copyRequested = generation;
        await web.ExecuteScriptAsync("surface.selection()");
        await WaitForAsync(() => copyRequested == 0);
        Require(await Clipboard.GetContent().GetTextAsync() == "UTF-8: ");
        await web.ExecuteScriptAsync("surface.paste('UTF-8: ')");
        await web.ExecuteScriptAsync("surface.controlC()");
        await WaitForAsync(() => fixture.InputBytes == 8 && fixture.ControlCCount == 1 && deliveredBytes == 197);
        await web.ExecuteScriptAsync("surface.paste('X'.repeat(4097))");
        await Task.Delay(100);
        Require(fixture.InputBytes == 8);
        await ResizeAsync(100, 30);
        var resized = await ReadResearchStatsAsync();
        Require(resized.GetProperty("columns").GetInt32() == fixture.LastColumns && fixture.LastRows == 30);
        holdDelivery = true;
        await StartAsync(true);
        await WaitForAsync(() => fixture.PendingChunks == 4);
        Require(!producer.IsCompleted);
        long stale = generation;
        await StartAsync(false); // Joins the held producer and revokes its pending browser writes.
        holdDelivery = false;
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await web.ExecuteScriptAsync($"surface.write({stale},0,'c3RhbGU=')");
        await Task.Delay(100);
        Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);
        await CloseAsync(); await CloseAsync(); await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        Require((await ReadResearchStatsAsync()).GetProperty("utf8").GetBoolean());
        await RecordResearchStatsAsync("lifecycle-complete");
    }
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("Synthetic retention assertion failed.");
    }
}
