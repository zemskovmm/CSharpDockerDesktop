using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class StatsChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static void Check(bool condition, string message = "Stats assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Stats", file));
    private static async Task<IEngineConnection> Connect(StatsTransport transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected, attempt.Failure?.Description ?? "No connection"); return attempt.Connection!;
    }
    private static async Task<IContainerStatsSubscription> Open(IEngineConnection owner, CancellationToken token = default)
    {
        var attempt = await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical", token);
        Check(attempt.IsSubscribed, attempt.Failure?.Description ?? "No subscription"); return attempt.Subscription!;
    }
    private static async Task<List<ContainerStatsSample>> Drain(IContainerStatsSubscription subscription)
    {
        using var deadline = new CancellationTokenSource(Deadline); var result = new List<ContainerStatsSample>();
        await foreach (var sample in subscription.Samples.ReadAllAsync(deadline.Token)) result.Add(sample);
        return result;
    }
    private static async Task Canceled(Func<Task> work)
    { try { await work(); throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { } }

    internal static async Task OfficialContracts()
    {
        using var evidence = JsonDocument.Parse(Fixture("contract-evidence.json"));
        var versions = evidence.RootElement.GetProperty("versions").EnumerateArray().ToArray(); Check(versions.Length == 17);
        foreach (var version in versions)
        {
            int minor = version.GetProperty("minor").GetInt32();
            Check(version.GetProperty("sha256").GetString()!.Length == 64);
            var parameters = version.GetProperty("parameters").EnumerateArray().ToArray();
            Check(parameters.Single(p => p.GetProperty("name").GetString() == "stream").GetProperty("type").GetString() == "boolean");
            Check(parameters.Any(p => p.GetProperty("name").GetString() == "one-shot") == (minor >= 41));
            Check(version.GetProperty("responses").EnumerateArray().Any(p => p.GetString() == "404"));
            var bytes = Encoding.UTF8.GetBytes(Fixture($"v1.{minor}.linux.json"));
            var transport = new StatsTransport(new([.. bytes, .. bytes], fragment: minor % 2 == 0 ? 1 : 513, randomSeed: minor), minor);
            await using var owner = await Connect(transport); await using var stats = await Open(owner);
            var samples = await Drain(stats); Check(samples.Count == 2);
            var sample = samples[0];
            Check(sample.ContainerId == "canonical" && sample.OSType == "linux");
            Check(sample.Read?.Original == "2015-01-08T22:57:31.547920715Z" && sample.Read.Nanoseconds == 547920715);
            Check(sample.PreRead?.Nanoseconds == 123456789 && sample.Cpu?.TotalUsage == 100215355 && sample.PreviousCpu?.TotalUsage == 100093996);
            Check(sample.Cpu?.OnlineCpus == 4 && sample.Cpu.SystemUsage == 739306590000000);
            Check(sample.Networks!["eth0"]!.ReceivedBytes == ulong.MaxValue && sample.Networks["eth0"]!.SentErrors == 0);
            Check(sample.BlockIo!.Value.Length == 2 && sample.BlockIo.Value[0].Operation == "Read" && sample.BlockIo.Value[1].Value == 100);
            Check(sample.Memory?.Usage == 6537216 && sample.Memory.Limit == 67108864 && sample.Storage is null);
            Check((sample.Cpu?.PerCpuUsage is null) == (minor >= 42));
            Check((sample.Memory?.MaximumUsage is null) == (minor >= 42));
            Check(sample.Memory!.Stats![minor >= 42 ? "inactive_file" : "cache"] == (minor >= 42 ? 2510848UL : 0UL));
            Check((await stats.Completion).Termination == ContainerStatsTermination.CleanEnd);
            Check(transport.Paths.Contains($"/v1.{minor}/containers/canonical/stats?stream=true"));
            Check(transport.Paths.Count(p => p == "/version") == 2 && transport.Handlers[1].Disposed && !transport.Handlers[0].Disposed);
            Check(await owner.CheckLivenessAsync() is null);
        }
        var windowsTransport = new StatsTransport(new(Encoding.UTF8.GetBytes(Fixture("windows.json")), fragment: 1)) { OS = "windows" };
        await using var windowsOwner = await Connect(windowsTransport); await using var windowsStats = await Open(windowsOwner);
        var windows = (await Drain(windowsStats)).Single();
        Check(windows.OSType == "windows" && windows.NumberOfProcessors == 16 && windows.Cpu?.TotalUsage == 29912000);
        Check(windows.Cpu!.SystemUsage is null && windows.Cpu.PerCpuUsage is null && windows.Cpu.OnlineCpus is null);
        Check(windows.Memory?.Usage is null && windows.Memory!.Limit is null && windows.Memory!.Stats is null && windows.BlockIo is null);
        Check(windows.Memory?.PrivateWorkingSet == 0 && windows.Memory.CommitBytes == ulong.MaxValue && windows.Storage?.WriteBytes == 0);
        Check(windows.Networks!["endpoint"]!.ReceivedBytes == 0 && windows.Networks["endpoint"]!.ReceivedErrors is null);
        Check(windows.Storage!.WriteCountNormalized is null && windows.Networks["endpoint"]!.InstanceId == "instance");
    }

    internal static async Task DecoderBoundsAndMalformed()
    {
        async Task<List<ContainerStatsSample>> Decode(string text, DockerConnectionOptions? options = null, int fragment = 1)
        {
            var list = new List<ContainerStatsSample>();
            using var stream = new LogTestStream(Encoding.UTF8.GetBytes(text), fragment);
            await DockerStatsDecoder.DecodeAsync(stream, "canonical", null, options ?? new(), sample => { list.Add(sample); return ValueTask.CompletedTask; }, default);
            return list;
        }
        var data = await Decode("{} {\"memory_stats\":{\"usage\":0,\"stats\":{}},\"networks\":{},\"blkio_stats\":{\"io_service_bytes_recursive\":[]}}\r\n{}");
        Check(data.Count == 3 && data[0].Memory is null && data[0].Read is null && data[0].Networks is null);
        Check(data[1].Memory!.Usage == 0 && data[1].Memory!.Limit is null && data[1].Memory!.Stats!.Count == 0 && data[1].Networks!.Count == 0 && data[1].BlockIo!.Value.Length == 0);
        Check((await Decode("{\"read\":\"0001-01-01T00:00:00Z\",\"preread\":\"\",\"cpu_stats\":null}")).Single().Read!.UnixSeconds < 0);
        Check((await Decode("{\"read\":\"2025-01-16T14:55:22.000000001+01:00\"}")).Single().Read!.Nanoseconds == 1);
        string exactly = "{\"future\":\"" + new string('a', 51) + "\"}"; Check(Encoding.UTF8.GetByteCount(exactly) == 64);
        Check((await Decode(exactly, new() { MaximumStatsSampleBytes = 64 })).Count == 1);
        foreach (string invalid in new[] { "{\"future\":" + new string('[', 64) + "0" + new string(']', 64) + "}", "[]", "null", "{", "{\"future\":\"truncated", "{} {", "{]", "{\"memory_stats\":true}",
            "{\"cpu_stats\":{\"cpu_usage\":[]}}", "{\"cpu_stats\":{\"cpu_usage\":{\"total_usage\":-1}}}",
            "{\"cpu_stats\":{\"cpu_usage\":{\"total_usage\":18446744073709551616}}}", "{\"memory_stats\":{\"usage\":\"0\"}}",
            "{\"memory_stats\":{\"usage\":1.5}}", "{\"networks\":[]}", "{\"networks\":{\"eth0\":1}}", "{\"num_procs\":4294967296}",
            "{\"storage_stats\":{\"read_size_bytes\":false}}", "{\"blkio_stats\":{\"io_service_bytes_recursive\":[null]}}",
            "{\"read\":\"\\ud800\"}", "{\"memory_stats\":{\"stats\":{\"\\ud800\":0}}}", "{\"read\":\"bad-date\"}", "{\"read\":\"2025-01-01T00:00:00.1234567890Z\"}", "{\"id\":\"different\"}",
            "{\"memory_stats\":{\"stats\":{\"cache\":1,\"cache\":2}}}", "{\"networks\":{\"eth0\":{},\"eth0\":{}}}" })
        {
            try { await Decode(invalid); throw new InvalidOperationException("Accepted malformed Stats sample: " + invalid); }
            catch (Exception ex) when (ex is JsonException or StatsProtocolException) { }
        }
        foreach (string bounded in new[] { "{\"future\":\"" + new string('a', 100) + "\"}",
            "{\"cpu_stats\":{\"cpu_usage\":{\"percpu_usage\":[0,1]}}}", "{\"memory_stats\":{\"stats\":{\"cache\":0,\"inactive_file\":0}}}",
            "{\"networks\":{\"a\":{},\"b\":{}}}", "{\"blkio_stats\":{\"io_service_bytes_recursive\":[{},{}]}}",
            "{\"cpu_stats\":{\"cpu_usage\":{\"percpu_usage\":[1]}},\"networks\":{\"a\":{}}}" })
        {
            try { await Decode(bounded, new() { MaximumStatsCollectionEntries = 1, MaximumStatsSampleBytes = 100 }); throw new InvalidOperationException("Accepted unbounded Stats"); }
            catch (StatsProtocolException ex) { Check(ex.Failure.Kind == ContainerStatsFailureKind.MalformedSample); }
        }
        var nullable = (await Decode("{\"memory_stats\":{\"stats\":{\"cache\":null,\"inactive_file\":0}},\"networks\":{\"missing\":null}}" )).Single();
        Check(nullable.Memory!.Stats!["cache"] is null && nullable.Memory.Stats["inactive_file"] == 0 && nullable.Networks!["missing"] is null);
        foreach (var invalidOptions in new DockerConnectionOptions[] { new() { StatsOpeningTimeout = TimeSpan.Zero }, new() { MaximumConcurrentStatsSubscriptions = 0 },
            new() { StatsBufferCapacity = 0 }, new() { MaximumStatsSampleBytes = 63 }, new() { MaximumStatsCollectionEntries = 0 }, new() { MaximumStatsErrorBytes = 63 } })
        {
            try { _ = new DockerEngineConnectionClient(invalidOptions); throw new InvalidOperationException("Accepted invalid Stats budget"); }
            catch (ArgumentOutOfRangeException) { }
        }
        using var utf8 = new LogTestStream([123,34,120,34,58,34,255,34,125]);
        try { await DockerStatsDecoder.DecodeAsync(utf8, "canonical", null, new(), _ => ValueTask.CompletedTask, default); throw new InvalidOperationException("Accepted invalid UTF8"); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { }
    }

    internal static async Task OpeningFailures()
    {
        foreach (var (status, expected) in new[] { (404, ContainerStatsFailureKind.NotFound), (403, ContainerStatsFailureKind.AccessDenied), (401, ContainerStatsFailureKind.AccessDenied),
            (400, ContainerStatsFailureKind.InvalidRequest), (409, ContainerStatsFailureKind.InvalidRequest), (500, ContainerStatsFailureKind.DaemonError) })
        {
            var transport = new StatsTransport(new([])) { Status = status, ErrorBody = "{\"message\":\"private daemon content\"}" };
            await using var owner = await Connect(transport);
            var attempt = await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical");
            Check(attempt.Failure?.Kind == expected && attempt.Failure.HttpStatusCode == status && attempt.Failure.DaemonMessage == "private daemon content");
            Check(!attempt.Failure!.Description.Contains("private") && transport.Handlers[1].Disposed && transport.Errors.All(e => e.Disposed));
        }
        var truncated = new StatsTransport(new([])) { Status = 500, ErrorBody = new string('x', 1000) };
        await using (var owner = await Connect(truncated, new() { MaximumStatsErrorBytes = 64 }))
        {
            var failure = (await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical")).Failure!;
            Check(failure.DaemonMessageTruncated && failure.DaemonMessage!.Length == 64);
        }
        foreach (var (exception, expected) in new (Exception, ContainerStatsFailureKind)[] {
            (new UnauthorizedAccessException("private permission content"), ContainerStatsFailureKind.AccessDenied),
            (new IOException("private transport content"), ContainerStatsFailureKind.TransportLoss) })
        {
            var denied = new StatsTransport(new([])) { OpeningException = exception };
            await using var owner = await Connect(denied);
            var failure = (await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical")).Failure!;
            Check(failure.Kind == expected && !failure.Description.Contains("private") && denied.Handlers[1].Disposed);
        }
        var missing = new StatsTransport(new([])) { FailInspect = true, Status = 404 };
        await using (var owner = await Connect(missing))
        {
            Check((await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical")).Failure?.Kind == ContainerStatsFailureKind.NotFound);
            Check(!missing.Paths.Any(p => p.Contains("/stats?")));
        }
        foreach (string mismatch in new[] { "engine", "missing-engine", "version", "api", "os", "identity", "sample-platform" })
        {
            var transport = new StatsTransport(new(Encoding.UTF8.GetBytes("{\"os_type\":\"windows\"}"))) { Mismatch = mismatch };
            await using var owner = await Connect(transport);
            var result = await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical");
            if (mismatch == "sample-platform")
            {
                Check(result.IsSubscribed); await using var stats = result.Subscription!; Check((await stats.Completion).Failure?.Kind == ContainerStatsFailureKind.MalformedSample); Check((await Drain(stats)).Count == 0);
            }
            else Check(result.Failure?.Kind == (mismatch == "identity" ? ContainerStatsFailureKind.InvalidRequest : mismatch == "api" ? ContainerStatsFailureKind.IncompatibleApi : ContainerStatsFailureKind.TransportLoss));
        }
        foreach (string stage in new[] { "handshake", "inspect", "stats" })
        {
            var transport = new StatsTransport(new([])) { StallStage = stage };
            await using var owner = await Connect(transport, new() { StatsOpeningTimeout = TimeSpan.FromMilliseconds(80) });
            Check((await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical")).Failure?.Kind == ContainerStatsFailureKind.OpeningDeadline);
            Check(transport.Handlers[1].Disposed);
        }
    }

    internal static async Task AdmissionAndBlockedDelivery()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{}", 100)));
        var transport = new StatsTransport(new(bytes, fragment: 2, end: false));
        await using var owner = await Connect(transport, new() { StatsBufferCapacity = 1, MaximumConcurrentStatsSubscriptions = 1 });
        foreach (var invalid in new[] { "", " ", ".", "..", "new\nline", "x\ud800", new string('a', 4097) })
            Check((await ((IContainerStatsSource)owner).SubscribeStatsAsync(invalid)).Failure?.Kind == ContainerStatsFailureKind.InvalidRequest);
        Check(transport.Handlers.Count == 1);
        var stats = await Open(owner); await transport.Stream.WaitForReads(2);
        Check(!stats.Completion.IsCompleted && transport.Stream.Reads == 2, "Slow consumer must backpressure after capacity plus one in-flight sample");
        Check((await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical")).Failure?.Kind == ContainerStatsFailureKind.SubscriptionLimit);
        await stats.DisposeAsync().AsTask().WaitAsync(Deadline);
        Check((await stats.Completion).Termination == ContainerStatsTermination.Canceled && !stats.Samples.TryRead(out _));
        Check(transport.Stream.Disposed && !transport.Handlers[0].Disposed);
        // Natural completion retains its admission until disposed, bounding retained completed channels too.
        var eofTransport = new StatsTransport(new(Encoding.UTF8.GetBytes("{}")));
        await using var eofOwner = await Connect(eofTransport, new() { MaximumConcurrentStatsSubscriptions = 1 });
        var eof = await Open(eofOwner); await eof.Completion;
        Check((await ((IContainerStatsSource)eofOwner).SubscribeStatsAsync("canonical")).Failure?.Kind == ContainerStatsFailureKind.SubscriptionLimit);
        Check(eof.Samples.TryRead(out _)); await eof.DisposeAsync();
        await using var next = await Open(eofOwner); await next.Completion;
    }

    internal static async Task CancellationAndJoinedOwnership()
    {
        foreach (string stage in new[] { "handshake", "inspect", "stats" })
        foreach (bool caller in new[] { true, false })
        {
            var transport = new StatsTransport(new([])) { StallStage = stage };
            var owner = await Connect(transport, new() { MaximumConcurrentStatsSubscriptions = 1 }); using var cancel = new CancellationTokenSource();
            var pending = ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical", cancel.Token);
            await transport.OpeningStarted.Task.WaitAsync(Deadline);
            Check((await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical")).Failure?.Kind == ContainerStatsFailureKind.SubscriptionLimit);
            Check(transport.Handlers.Count == 2, "An opening subscription reserves admission before transport allocation");
            if (caller) { cancel.Cancel(); await Canceled(async () => await pending); }
            else { await owner.DisposeAsync(); Check(!(await pending).IsSubscribed); }
            Check(transport.Handlers[1].Disposed); await owner.DisposeAsync();
        }
        foreach (bool caller in new[] { true, false })
        {
            var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new StatsTransport(new([])) { LateOpening = late };
            var owner = await Connect(transport); using var cancel = new CancellationTokenSource();
            var pending = ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical", cancel.Token);
            await transport.OpeningStarted.Task.WaitAsync(Deadline);
            var disposing = caller ? Task.CompletedTask : owner.DisposeAsync().AsTask();
            if (caller) cancel.Cancel();
            Check(!pending.IsCompleted && (caller || !disposing.IsCompleted), "Cancellation must join late transport opening");
            late.SetResult(new(HttpStatusCode.OK) { Content = new StreamContent(transport.Stream) });
            if (caller) await Canceled(async () => await pending); else Check(!(await pending).IsSubscribed);
            await disposing; await owner.DisposeAsync(); Check(transport.Stream.Disposed && transport.Handlers.All(h => h.Disposed));
        }
        foreach (bool blocked in new[] { true, false })
        foreach (string ending in new[] { "caller", "subscription", "owner", "owner-generation", "stream-generation" })
        {
            var transport = new StatsTransport(new(Encoding.UTF8.GetBytes(blocked ? "{}{}{}" : "{}"), fragment: 2, end: false));
            var owner = await Connect(transport, new() { StatsBufferCapacity = 1 }); using var cancel = new CancellationTokenSource();
            var stats = await Open(owner, cancel.Token);
            if (blocked) await transport.Stream.WaitForReads(2); else await transport.Stream.Stalled.Task.WaitAsync(Deadline);
            switch (ending)
            {
                case "caller": cancel.Cancel(); break;
                case "subscription": await stats.DisposeAsync(); break;
                case "owner": await owner.DisposeAsync(); break;
                case "owner-generation": transport.Counters[0].Opened(); break;
                case "stream-generation": transport.Counters[1].Opened(); break;
            }
            var completion = await stats.Completion.WaitAsync(Deadline);
            Check(completion.Termination == (ending.EndsWith("generation") ? ContainerStatsTermination.Failed : ContainerStatsTermination.Canceled));
            Check(!stats.Samples.TryRead(out _) && !await stats.Samples.WaitToReadAsync());
            Check(transport.Stream.Disposed && transport.Handlers[1].Disposed);
            await stats.DisposeAsync(); await owner.DisposeAsync();
        }
    }

    internal static async Task EstablishedStreamsAndFailures()
    {
        var transport = new StatsTransport(new([], end: false));
        await using var owner = await Connect(transport, new() { RequestTimeout = TimeSpan.FromMilliseconds(50), StatsOpeningTimeout = TimeSpan.FromMilliseconds(50) });
        await using var stats = await Open(owner); await transport.Stream.Stalled.Task.WaitAsync(Deadline);
        await Task.Delay(120); Check(!stats.Completion.IsCompleted && !stats.Samples.TryRead(out _), "An established idle stream must survive ordinary/opening deadlines without invented samples");
        Check(await owner.CheckLivenessAsync() is null && transport.Handlers.Count == 2);
        var unicode = new StatsTransport(new(Encoding.UTF8.GetBytes("{\"id\":\"\\ud800\"}")));
        await using (var selected = await Connect(unicode))
        await using (var stream = await Open(selected))
            Check((await stream.Completion.WaitAsync(Deadline)).Failure?.Kind == ContainerStatsFailureKind.MalformedSample);
        foreach (bool loss in new[] { false, true })
        {
            var broken = new StatsTransport(new(Encoding.UTF8.GetBytes(loss ? "{}" : "{}{\"read\":")));
            if (loss) broken = new StatsTransport(new(Encoding.UTF8.GetBytes("{}")) { ThrowAfterData = true });
            await using var selected = await Connect(broken); await using var stream = await Open(selected);
            var completion = await stream.Completion.WaitAsync(Deadline);
            Check(completion.Failure?.Kind == (loss ? ContainerStatsFailureKind.TransportLoss : ContainerStatsFailureKind.MalformedSample));
            var samples = await Drain(stream); Check(samples.Count == (loss ? 0 : 1));
        }
    }
}

internal sealed class StatsTransport(LogTestStream stream, int minor = 56) : IDockerTransportFactory
{
    public LogTestStream Stream => stream;
    public string OS { get; init; } = "linux";
    public int Status { get; init; } = 200;
    public string ErrorBody { get; init; } = "error";
    public bool FailInspect { get; init; }
    public Exception? OpeningException { get; init; }
    public string? Mismatch { get; init; }
    public string? StallStage { get; init; }
    public TaskCompletionSource<HttpResponseMessage>? LateOpening { get; init; }
    public TaskCompletionSource OpeningStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<TrackingHandler> Handlers { get; } = [];
    public List<PipeConnectionCounter> Counters { get; } = [];
    public ConcurrentQueue<string> Paths { get; } = new();
    public ConcurrentQueue<TrackedContent> Errors { get; } = new();
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
    {
        int pool = Handlers.Count; var counter = new PipeConnectionCounter(); counter.Opened(); Counters.Add(counter);
        var handler = new TrackingHandler(async (request, _, token) =>
        {
            string path = request.RequestUri!.PathAndQuery; Paths.Enqueue(path);
            bool inspect = path.EndsWith("/containers/canonical/json"), stats = path.Contains("/stats?");
            if (pool > 0 && (StallStage == "handshake" && path == "/_ping" || StallStage == "inspect" && inspect || StallStage == "stats" && stats))
            { OpeningStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (stats || inspect && FailInspect)
            {
                OpeningStarted.TrySetResult();
                if (OpeningException is { } exception) throw exception;
                if (LateOpening is { } late) return await late.Task;
                if (Status != 200)
                { var error = new TrackedContent(ErrorBody); Errors.Enqueue(error); return new((HttpStatusCode)Status) { Content = error }; }
                return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            }
            string os = pool > 0 && Mismatch == "os" ? "changed" : OS;
            string body = path switch
            {
                "/_ping" => "OK",
                "/version" => $$"""{"Version":"{{(pool > 0 && Mismatch == "version" ? "changed" : "fixture")}}","ApiVersion":"1.{{(pool > 0 && Mismatch == "api" ? 40 : minor)}}","MinAPIVersion":"1.40","Os":"{{os}}"}""",
                _ when path.EndsWith("/info") => $$"""{"ID":"{{(Mismatch == "missing-engine" ? "" : pool > 0 && Mismatch == "engine" ? "changed" : "same-engine")}}","OSType":"{{os}}"}""",
                _ when inspect => $$"""{"Id":"{{(Mismatch == "identity" ? "other" : "canonical")}}"}""",
                _ => "[]"
            };
            return new(HttpStatusCode.OK) { Content = new TrackedContent(body) };
        });
        Handlers.Add(handler); return new ContinuityHandler(handler, counter);
    }
}
