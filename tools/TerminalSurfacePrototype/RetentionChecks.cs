using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

// Synthetic-only native Uno/WKWebView research. No arbitrary data/file/URL input.
internal static class RetentionChecks
{
    internal static string? Workload;
    internal static readonly string[] Workloads = ["aggregate-sustained", "aggregate-empty-cycles", "aggregate-idle-cycles", "aggregate-cycles", "combined", "normal", "sustained", "combining", "width-cache", "width-cache-long", "stock-links", "links", "stock-alternate", "alternate", "attributes", "osc", "dcs", "csi", "a11y", "a11y-boundaries", "lifecycle", "cutoff"];
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
                case "aggregate-sustained": case "aggregate-cycle":
                    // 1,200 half-second ticks: >=10 min, one generation, ~2 MiB.
                    // CR overwrites leave stale combined/extended metadata; LF churns history.
                    int ticks = workload == "aggregate-sustained" ? 1200 : 24;
                    for (int tick = 0; tick < ticks; tick++)
                    {
                        var text = new StringBuilder();
                        if (tick == 0) text.Append("\x1b]2;fixed-retention-title\a\x1b[22;0t");
                        for (int line = 0; line < 16; line++)
                            text.Append($"café λ 世界 🐳 {tick:D4} \x1b[4:3;58:2::4:3:2m")
                                .Append(line % 2 == 0 ? "a\u0301b\u0308" : "ordinary")
                                .Append("\x1b[0m\rplain\r\n");
                        // A rendered row of distinct short combined keys, all four variants.
                        text.Append($"\x1b[0;{new[] { "0", "1", "3", "1;3" }[tick % 4]}m");
                        for (int cell = 0; cell < 64; cell++)
                        {
                            int serial = tick * 64 + cell;
                            text.Append('a').Append((char)(0x300 + serial % 112))
                                .Append((char)(0x300 + serial / 112 % 112));
                        }
                        text.Append("\x1b[0m");
                        await Add(text.ToString());
                        if (used != 0) await Send();
                        await Task.Delay(workload == "aggregate-sustained" ? 500 : 60, token);
                    }
                    break;
                case "normal": case "sustained":
                    for (int i = 0; i < (workload == "sustained" ? 1200000 : 300000); i++) await Add($"line {i:D6} café λ 世界 🐳 \x1b[31mRED\x1b[0m\r\n");
                    break;
                case "combining":
                    await Add("a");
                    for (int i = 0; i < 131072; i++) await Add("\u0301");
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
            await writer.WriteAsync(chunk.AsSpan(0, count).ToArray(), token);
            ResearchBytes += count;
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
            pendingChunks = fixture.PendingChunks, generation,
            producerJoined = producer.IsCompleted, pumpJoined = pump.IsCompleted,
            nativeAckPending = ack is not null && !ack.Task.IsCompleted,
            inputQueueEntries = input.Reader.Count, stats = result }));
        return result;
    }
    private async Task RunRetentionChecksAsync()
    {
        int exit = 0;
        researchClock.Start();
        IsHitTestVisible = false;
        try
        {
            bool aggregate = RetentionChecks.Workload!.StartsWith("aggregate-", StringComparison.Ordinal);
            // All matched baselines have never constructed xterm. Compatibility runs afterward.
            if (!aggregate)
            {
                await StartAsync(false);
                await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
                Require((await ReadResearchStatsAsync()).GetProperty("utf8").GetBoolean());
                await RecordResearchStatsAsync("baseline");
            }
            if (aggregate) await CheckAggregateAsync();
            else if (RetentionChecks.Workload == "combined") await CheckCombinedCellsAsync();
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
            else if (RetentionChecks.Workload is "width-cache" or "width-cache-long") await CheckWidthCacheAsync();
            else if (RetentionChecks.Workload == "a11y-boundaries") await CheckAccessibilityAsync();
            else if (RetentionChecks.Workload is "a11y" or "attributes")
            {
                await OpenResearchAsync(RetentionChecks.Workload!);
                long failedId = generation; int before = accessibilityFailures;
                producer = fixture.ProduceResearchAsync(RetentionChecks.Workload!);
                await AwaitAccessibilityFailureAsync(failedId, before);
                Require(!fixture.ProducerCompleted && fixture.ResearchBytes >= 4 * 4093);
                await StartAsync(false);
                await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
                await RecordResearchStatsAsync("accessibility-producer-reopened");
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
                    case "stock-links": Require(result.GetProperty("links").GetInt32() == 8000); break;
                    case "links": case "alternate": case "stock-alternate": Require(result.GetProperty("links").GetInt32() == 0); break;
                    case "osc": case "dcs": Require(result.GetProperty("parserLimited").GetBoolean()); break;
                    case "csi": Require(result.GetProperty("params").GetInt32() <= 32); break;
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
    private async Task CheckAggregateAsync()
    {
        bool sustained = RetentionChecks.Workload == "aggregate-sustained";
        bool empty = RetentionChecks.Workload == "aggregate-empty-cycles";
        bool idle = RetentionChecks.Workload == "aggregate-idle-cycles";
        bool control = empty || idle;
        await CloseAsync();
        var baseline = await RecordResearchStatsAsync("aggregate-empty-baseline");
        Require(baseline.GetProperty("constructed").GetInt32() == 0
            && !baseline.GetProperty("terminalPresent").GetBoolean());
        await Task.Delay(8000); // Keep this state stable for native VM/footprint snapshots.
        int cycles = sustained ? 1 : 12;
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            await OpenResearchAsync(RetentionChecks.Workload!);
            await RecordResearchStatsAsync($"cycle-{cycle:D2}-baseline");
            long id = generation;
            var elapsed = Stopwatch.StartNew();
            if (control) await Task.Delay(24 * 60); // Matched paced output window, zero output.
            else producer = fixture.ProduceResearchAsync(sustained ? "aggregate-sustained" : "aggregate-cycle");
            if (sustained)
                while (!producer.IsCompleted)
                {
                    await Task.WhenAny(producer, Task.Delay(30000));
                    var sample = await RecordResearchStatsAsync("aggregate-paced");
                    CheckAggregateBounds(sample);
                    Require(generation == id && elapsed.Elapsed < TimeSpan.FromMinutes(12));
                }
            await producer.WaitAsync(TimeSpan.FromSeconds(30));
            if (!control) await WaitForAsync(() => deliveredBytes == fixture.ResearchBytes);
            Require(generation == id && (control ? deliveredBytes == 0 : fixture.ProducerCompleted && fixture.ResearchBytes < 67108864));
            if (sustained) Require(elapsed.Elapsed >= TimeSpan.FromMinutes(10) && deliveredBytes > 1048576);
            // Populate alt while normal stays retained, also exercise extended attributes.
            if (!control) await SendCombinedAsync("\x1b[?1049h" + string.Concat(Enumerable.Repeat("\x1b[4:3;58:2::4:3:2mALT a\u0301 café λ 世界 🐳\x1b[0m\r\n", 30)));
            await Task.Delay(1200);
            var populated = await RecordResearchStatsAsync($"cycle-{cycle:D2}-populated");
            Require(populated.GetProperty("frameCount").GetInt32() == 1
                && populated.GetProperty("opaqueSandbox").GetBoolean()
                && populated.GetProperty("frameWidth").GetDouble() == baseline.GetProperty("frameWidth").GetDouble()
                && populated.GetProperty("frameHeight").GetDouble() == baseline.GetProperty("frameHeight").GetDouble()
                && populated.GetProperty("starts").GetInt32() == cycle);
            if (empty)
                Require(!populated.GetProperty("terminalPresent").GetBoolean()
                    && populated.GetProperty("constructed").GetInt32() == 0
                    && populated.GetProperty("opened").GetInt32() == 0
                    && populated.GetProperty("renders").GetInt32() == 0
                    && populated.GetProperty("attachedTerminalNodes").GetInt32() == 0);
            else
            {
                CheckAggregateBounds(populated);
                Require(populated.GetProperty("constructed").GetInt32() == cycle
                    && populated.GetProperty("opened").GetInt32() == cycle
                    && populated.GetProperty("renders").GetInt32() >= cycle);
                Require(populated.GetProperty("normalLines").GetInt32() == (idle ? 24 : 224)
                    && populated.GetProperty("alternateLines").GetInt32() == (idle ? 0 : 24));
                if (!idle) Require(populated.GetProperty("widthCacheEvictions").GetInt64() > 0);
            }
            if (control) Require(populated.GetProperty("received").GetInt64() == 0
                && populated.GetProperty("acceptedWrites").GetInt32() == 0);
            if (sustained || cycle is 1 or 12) await Task.Delay(8000);
            // Block a fresh producer in the same populated session; close must join both tasks.
            holdDelivery = true;
            producer = fixture.ProduceResearchAsync("normal");
            await WaitForAsync(() => fixture.PendingChunks == 4);
            Require(!producer.IsCompleted);
            await CloseAsync(); holdDelivery = false;
            Require(producer.IsCompleted && pump.IsCompleted && generation < 0);
            await web.ExecuteScriptAsync($"surface.write({id},0,'c3RhbGU=')");
            await web.ExecuteScriptAsync($"surface.write({generation},0,'c3RhbGU=')");
            var closed = await RecordResearchStatsAsync($"cycle-{cycle:D2}-post-close");
            Require(!closed.GetProperty("terminalPresent").GetBoolean()
                && closed.GetProperty("attachedTerminalNodes").GetInt32() == 0
                && closed.GetProperty("received").GetInt64() == populated.GetProperty("received").GetInt64()
                && closed.GetProperty("disposed").GetInt32() == (empty ? 0 : cycle)
                && closed.GetProperty("staleWrites").GetInt32() == cycle
                && closed.GetProperty("inactiveWrites").GetInt32() == cycle);
            if (cycle is 1 or 12) await Task.Delay(8000); // Closed capture dwell, identical across variants.
            await Task.Delay(sustained ? 30000 : 8000);
            await RecordResearchStatsAsync($"cycle-{cycle:D2}-post-idle");
            if (sustained || cycle is 1 or 12) await Task.Delay(8000);
        }
        // Measurement ends above. Verify an empty replacement cannot retain a prior terminal.
        await OpenResearchAsync("aggregate-idle-cycles");
        long staleControl = generation;
        await OpenResearchAsync("aggregate-empty-cycles");
        await web.ExecuteScriptAsync($"surface.write({staleControl},0,'c3RhbGU=')");
        await web.ExecuteScriptAsync($"surface.write({generation},0,'c3RhbGU=')");
        var cleared = await RecordResearchStatsAsync("control-replaced-terminal");
        Require(!cleared.GetProperty("terminalPresent").GetBoolean()
            && cleared.GetProperty("attachedTerminalNodes").GetInt32() == 0
            && cleared.GetProperty("received").GetInt64() == 0);
        // Reopen and prove delayed old writes/failures cannot affect a replacement.
        await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await FailSurfaceAsync(staleControl, "accessibility-limit");
        Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);
        await CheckResearchLifecycleAsync();
        await web.ExecuteScriptAsync("surface.probePopup()");
        await web.ExecuteScriptAsync("surface.probeFetch()");
        await web.ExecuteScriptAsync("surface.probeNavigation()");
        await web.ExecuteScriptAsync("surface.probeExternal()");
        await WaitForAsync(() => fetchBlocked == true && sandboxPopupBlocked == true);
        var secured = await RecordResearchStatsAsync("aggregate-reopened-compatible");
        Require(secured.GetProperty("utf8").GetBoolean()
            && secured.GetProperty("externalPopupBlocked").GetBoolean()
            && secured.GetProperty("topNavigationBlocked").GetBoolean());
    }
    private static void CheckAggregateBounds(JsonElement value)
    {
        Require(value.GetProperty("normalLines").GetInt32() <= 224
            && value.GetProperty("alternateLines").GetInt32() <= 24
            && value.GetProperty("maxCombinedUnits").GetInt32() <= 256
            && value.GetProperty("a11yAggregateUnits").GetInt32() <= 16384
            && value.GetProperty("links").GetInt32() == 0
            && value.GetProperty("markers").GetInt32() == 0);
        CheckCacheBounds(value);
    }
    private async Task OpenResearchAsync(string mode = "combined")
    {
        await transition.WaitAsync();
        try
        {
            await StopCoreAsync();
            generation = terminal.OpenContainerTerminal("controlled", [], 80, 24).SessionId;
            deliveredBytes = 0; awaitingSequence = 0;
            await web.ExecuteScriptAsync($"surface.start({generation}," + JsonSerializer.Serialize(mode) + ")");
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
        else
            for (int offset = 0; offset < bytes.Length; offset += 4093)
                await fixture.WriteContainerTerminalAsync(generation, bytes.AsMemory(offset, Math.Min(4093, bytes.Length - offset)));
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
    private async Task CheckWidthCacheAsync()
    {
        bool longCells = RetentionChecks.Workload == "width-cache-long";
        await OpenResearchAsync(RetentionChecks.Workload!);
        int serial = 0; string firstText = ""; double firstWidth = 0;
        foreach (string variant in new[] { "0", "1", "3", "1;3" })
        {
            for (int batch = 0; batch < (longCells ? 4 : 12); batch++)
            {
                // Deliberate blur clears prior announcements, then refocus keeps accessibility
                // enabled. A long batch contains exactly 16,384 announced UTF-16 units.
                await web.ExecuteScriptAsync("surface.probeAccessibility('blur')");
                await web.ExecuteScriptAsync("surface.probeAccessibility('focus')");
                long before = (await ReadResearchStatsAsync()).GetProperty("widthCacheMeasurements").GetInt64();
                var text = new StringBuilder($"\r\x1b[2K\x1b[0;{variant}m");
                for (int cell = 0; cell < 64; cell++, serial++)
                {
                    text.Append('a');
                    if (longCells) text.Append('\u0301', 253);
                    text.Append((char)(0x300 + serial % 112));
                    text.Append((char)(0x300 + serial / 112));
                }
                if (serial == 64) firstText = text.ToString();
                await SendCombinedAsync(text.ToString());
                // Parsed writes may coalesce. Require the actual visible distinct cells in
                // the cache plus measurements, without assuming one render per write.
                var current = await WaitForWidthRenderedAsync(before + 64);
                if (serial == 64) firstWidth = current.GetProperty("widthRenderedWidth").GetDouble();
                CheckCacheBounds(current);
            }
            var sample = await RecordResearchStatsAsync("width-variant-" + variant.Replace(';', '-'));
            Require(sample.GetProperty("a11yRows").GetInt32() == 24);
        }
        var result = await RecordResearchStatsAsync("width-capacity");
        Require(result.GetProperty("widthCacheEvictions").GetInt64() > 0);
        if (longCells) Require(result.GetProperty("widthCachePeakKeyUnits").GetInt32() >= 32000);
        else Require(result.GetProperty("widthCachePeakEntries").GetInt32() == 512);
        long hits = result.GetProperty("widthCacheHits").GetInt64();
        await web.ExecuteScriptAsync("surface.probeWidthCache('refresh')");
        await Task.Delay(150);
        var hit = await RecordResearchStatsAsync("width-repeat-hits");
        Require(hit.GetProperty("widthCacheHits").GetInt64() >= hits + 64
            && hit.GetProperty("widthCacheMeasurements").GetInt64() == result.GetProperty("widthCacheMeasurements").GetInt64());
        // An already evicted early row must measure again and render correctly.
        await web.ExecuteScriptAsync("surface.probeAccessibility('blur')");
        long measured = result.GetProperty("widthCacheMeasurements").GetInt64();
        await SendCombinedAsync(firstText);
        await WaitForWidthRenderedAsync(measured + 64);
        var again = await RecordResearchStatsAsync("width-remeasurement");
        Require(again.GetProperty("widthCacheMeasurements").GetInt64() > measured);
        Require(again.GetProperty("widthRenderedCells").GetInt32() == 64
            && again.GetProperty("widthRenderedWidth").GetDouble() == firstWidth && firstWidth > 0); CheckCacheBounds(again);
        await web.ExecuteScriptAsync("surface.probeWidthCache('font')");
        await WaitForWidthRenderedAsync(again.GetProperty("widthCacheMeasurements").GetInt64() + 64);
        var font = await RecordResearchStatsAsync("width-font-changed");
        Require(font.GetProperty("widthCacheMeasurements").GetInt64() >= again.GetProperty("widthCacheMeasurements").GetInt64() + 64
            && font.GetProperty("widthRenderedCells").GetInt32() == 64); CheckCacheBounds(font);
        await web.ExecuteScriptAsync("surface.probeWidthCache('font')");
        await ResizeAsync(40, 10); await ResizeAsync(100, 30);
        await web.ExecuteScriptAsync("surface.probeAccessibility('blur')");
        await web.ExecuteScriptAsync("surface.probeAccessibility('focus')");
        await SendCombinedAsync("\x1b[0m\r\nRECOVERED\r\n");
        await Task.Delay(100);
        var recovered = await RecordResearchStatsAsync("width-resize-recovered");
        Require(recovered.GetProperty("recovered").GetBoolean()); CheckCacheBounds(recovered);
        await CloseAsync(); await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await CheckResearchLifecycleAsync();
    }
    private async Task<JsonElement> WaitForWidthRenderedAsync(long minimumMeasurements)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var current = await ReadResearchStatsAsync();
            if (current.GetProperty("widthRenderedCells").GetInt32() == 64
                && current.GetProperty("widthCacheMeasurements").GetInt64() >= minimumMeasurements) return current;
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            // Font/character-size invalidations can clear after the initial render.
            // Request an ordinary redraw; never clear the cache or reset the terminal.
            await web.ExecuteScriptAsync("surface.probeWidthCache('refresh')");
            await Task.Delay(20);
        } while (true);
    }
    private static void CheckCacheBounds(JsonElement result)
    {
        int units = result.GetProperty("widthCacheKeyUnits").GetInt32();
        Require(result.GetProperty("widthCacheEntries").GetInt32() <= 512 && units <= 32768
            && units == result.GetProperty("widthCacheAccountedUnits").GetInt32()
            && result.GetProperty("widthCachePeakEntries").GetInt32() <= 512
            && result.GetProperty("widthCachePeakKeyUnits").GetInt32() <= 32768
            && result.GetProperty("widthMeasureUnits").GetInt32() == 0
            && result.GetProperty("maxCombinedUnits").GetInt32() <= 256);
    }
    private async Task AwaitAccessibilityFailureAsync(long stale, int before)
    {
        await WaitForAsync(() => accessibilityFailures == before + 1);
        await failureCleanup.WaitAsync(TimeSpan.FromSeconds(10));
        Require(generation < 0 && pump.IsCompleted && producer.IsCompleted && lastAccessibilityFailure == stale);
        Require(status.Text == "Prototype failed: accessibility retention limit exceeded; session closed.");
    }
    private async Task CheckAccessibilityAsync()
    {
        // Real fragmented bytes through Application, native bridge and xterm parser.
        await OpenResearchAsync("a11y-boundaries");
        await SendCombinedAsync(string.Concat(Enumerable.Repeat("🐳\r", 4096)));
        await Task.Delay(1200); // First half becomes live; second half remains pending.
        await SendCombinedAsync(string.Concat(Enumerable.Repeat("🐳\r", 4095)));
        await SendCombinedAsync("🐳\r", true);
        var exact = await RecordResearchStatsAsync("accessibility-exact");
        Require(exact.GetProperty("a11yAggregateUnits").GetInt32() == 16384
            && exact.GetProperty("a11yUnits").GetInt32() == 8192 && exact.GetProperty("a11yPendingUnits").GetInt32() == 8192);
        Require(exact.GetProperty("a11yEchoEntries").GetInt32() == 0);
        await Task.Delay(1200);
        exact = await RecordResearchStatsAsync("accessibility-transferred");
        Require(exact.GetProperty("a11yUnits").GetInt32() == 16384 && exact.GetProperty("a11yPendingUnits").GetInt32() == 0);
        long stale = generation; int before = accessibilityFailures;
        await fixture.WriteContainerTerminalAsync(stale, Encoding.UTF8.GetBytes("X\rSUFFIX"));
        await AwaitAccessibilityFailureAsync(stale, before);
        Require(deliveredBytes == 40960); // The failed write was never acknowledged.
        await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await FailSurfaceAsync(stale, "accessibility-limit");
        Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);

        // Tab's expanded spaces share the pending/live budget.
        await OpenResearchAsync("a11y-boundaries");
        await SendCombinedAsync(new string('X', 16380));
        stale = generation; before = accessibilityFailures;
        await fixture.WriteContainerTerminalAsync(stale, Encoding.UTF8.GetBytes("\r\t"));
        await AwaitAccessibilityFailureAsync(stale, before);

        // No-echo keyboard queue: actual DOM keydown handlers, fixed synthetic keys.
        await OpenResearchAsync("a11y-no-echo");
        await web.ExecuteScriptAsync("surface.probeAccessibility('keys-exact')");
        exact = await RecordResearchStatsAsync("accessibility-keys-exact");
        Require(exact.GetProperty("a11yEchoEntries").GetInt32() == 256 && exact.GetProperty("a11yEchoUnits").GetInt32() == 256);
        // A direct fixture echo consumes exactly one ordinary key entry.
        await SendCombinedAsync("x");
        exact = await ReadResearchStatsAsync();
        Require(exact.GetProperty("a11yEchoEntries").GetInt32() == 255 && exact.GetProperty("a11yAggregateUnits").GetInt32() == 0);
        await web.ExecuteScriptAsync("surface.probeAccessibility('blur')");
        exact = await RecordResearchStatsAsync("accessibility-blur");
        Require(exact.GetProperty("a11yEchoEntries").GetInt32() == 0 && exact.GetProperty("a11yEchoUnits").GetInt32() == 0);
        await web.ExecuteScriptAsync("surface.probeAccessibility('focus')");
        await web.ExecuteScriptAsync("surface.probeAccessibility('keys-exact')");
        await web.ExecuteScriptAsync("surface.probeAccessibility('hold-write')");
        await fixture.WriteContainerTerminalAsync(generation, Encoding.UTF8.GetBytes("\x1b[z"));
        await Task.Delay(100);
        Require((await ReadResearchStatsAsync()).GetProperty("a11yWriteHeld").GetBoolean() && ack is not null && !ack.Task.IsCompleted);
        stale = generation; before = accessibilityFailures;
        await web.ExecuteScriptAsync("surface.probeAccessibility('keys-over')");
        await AwaitAccessibilityFailureAsync(stale, before);
        await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await Task.Delay(1600); // The old parser continuation must not acknowledge or touch the new terminal.
        Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);

        // Inject prospective growth at the exact boundary through the real delayed
        // accessibility-render callback. This is fault injection, not organic rendering.
        await OpenResearchAsync("a11y-boundaries");
        await SendCombinedAsync(new string('X', 16384));
        stale = generation; before = accessibilityFailures;
        await web.ExecuteScriptAsync("surface.probeAccessibility('delayed-failure')");
        await AwaitAccessibilityFailureAsync(stale, before);
        await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);

        // Dispose a pending debouncer and ensure it cannot announce into replacement.
        await OpenResearchAsync("a11y-boundaries");
        await SendCombinedAsync("café λ 世界 🐳\t\x1b[31mRED\x1b[0m\r\n", true);
        var ordinary = await RecordResearchStatsAsync("accessibility-ordinary");
        Require(ordinary.GetProperty("utf8").GetBoolean() && ordinary.GetProperty("a11yRows").GetInt32() == 24 && ordinary.GetProperty("a11yAggregateUnits").GetInt32() < 16384);
        await web.ExecuteScriptAsync("surface.probeAccessibility('blur')");
        Require((await RecordResearchStatsAsync("accessibility-pending-cleared")).GetProperty("a11yAggregateUnits").GetInt32() == 0);
        await web.ExecuteScriptAsync("surface.probeAccessibility('focus')");
        await SendCombinedAsync(string.Concat(Enumerable.Repeat("row\r\n", 40)));
        await Task.Delay(1200);
        await web.ExecuteScriptAsync("surface.probeAccessibility('navigation')");
        Require((await RecordResearchStatsAsync("accessibility-navigation")).GetProperty("a11yNavigated").GetBoolean());
        await web.ExecuteScriptAsync("surface.probeAccessibility('queue-refresh')");
        Require((await ReadResearchStatsAsync()).GetProperty("a11yRefreshPending").GetBoolean());
        await Task.WhenAll(CloseAsync(), StartAsync(false));
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await Task.Delay(1200);
        Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);
        // Replacement queued while the failing write is in flight.
        await OpenResearchAsync("a11y-boundaries");
        await SendCombinedAsync(new string('X', 16384));
        stale = generation;
        await fixture.WriteContainerTerminalAsync(stale, Encoding.UTF8.GetBytes("X"));
        await StartAsync(false);
        await WaitForAsync(() => fixture.ProducerCompleted && deliveredBytes == 189);
        await FailSurfaceAsync(stale, "accessibility-limit");
        await Task.Delay(1200);
        Require((await ReadResearchStatsAsync()).GetProperty("received").GetInt64() == 189);
        await CheckResearchLifecycleAsync();
        await RecordResearchStatsAsync("accessibility-complete");
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
