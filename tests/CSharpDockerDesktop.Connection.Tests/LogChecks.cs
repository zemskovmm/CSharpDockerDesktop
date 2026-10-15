using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class LogChecks
{
    internal const string Stamp = "2021-08-21T19:38:15.515050031Z ";
    private static void Check(bool value, string message = "Logs assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    internal static byte[] Frame(byte kind, byte[] bytes, uint? announced = null)
    {
        var frame = new byte[8 + bytes.Length]; frame[0] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), announced ?? (uint)bytes.Length);
        bytes.CopyTo(frame, 8); return frame;
    }
    internal static byte[] Frame(byte kind, string text, uint? announced = null) => Frame(kind, Encoding.UTF8.GetBytes(text), announced);
    private static async Task<IEngineConnection> Connect(LogTransport transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected, attempt.Failure?.Description ?? "No connection"); return attempt.Connection!;
    }
    private static async Task<IContainerLogSubscription> Open(IEngineConnection connection, ContainerLogRequest? request = null, CancellationToken token = default)
    {
        var attempt = await ((IContainerLogSource)connection).OpenLogsAsync(request ?? new("canonical", 20), token);
        Check(attempt.IsSubscribed, attempt.Failure?.Description ?? "No logs subscription"); return attempt.Subscription!;
    }
    private static async Task<List<ContainerLogOutput>> Drain(IContainerLogSubscription subscription)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = new List<ContainerLogOutput>();
        await foreach (var fragment in subscription.Output.ReadAllAsync(deadline.Token)) output.Add(fragment);
        return output;
    }
    private static async Task Canceled(Func<Task> work)
    { try { await work(); throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { } }

    internal static async Task ContractsAndFragmentation()
    {
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Logs", "contract-evidence.json")));
        var contracts = fixtures.RootElement.GetProperty("versions").EnumerateArray().ToArray();
        Check(contracts.Length == 17);
        foreach (var fixture in contracts)
        {
            int minor = fixture.GetProperty("minor").GetInt32();
            Check(fixture.GetProperty("sha256").GetString()!.Length == 64);
            var parameters = fixture.GetProperty("parameters").EnumerateArray().ToArray();
            Check(parameters.Single(p => p.GetProperty("name").GetString() == "tail").GetProperty("type").GetString() == "string");
            Check(parameters.Single(p => p.GetProperty("name").GetString() == "follow").GetProperty("type").GetString() == "boolean");
            Check(fixture.GetProperty("responses").TryGetProperty("200", out _) && fixture.GetProperty("responses").TryGetProperty("404", out _));
            string text = Stamp + "α🙂 first\r\n";
            var stream = new LogTestStream([.. Frame(1, text), .. Frame(2, Stamp + "stderr\n"), .. Frame(1, text)], fragment: minor % 2 == 0 ? 1 : 1024, randomSeed: minor);
            var transport = new LogTransport(stream, minor);
            await using var owner = await Connect(transport);
            await using var subscription = await Open(owner, new("name with /?α", 23, true, new(1629574695, 515050031)));
            var outputs = await Drain(subscription);
            string stdout = string.Concat(outputs.Where(o => o.Stream == ContainerLogStream.Stdout).Select(o => o.Text));
            string stderr = string.Concat(outputs.Where(o => o.Stream == ContainerLogStream.Stderr).Select(o => o.Text));
            Check(stdout == text + text, $"API 1.{minor} stdout differs (expectedCharacters={text.Length * 2}, actualCharacters={stdout.Length}, fragments={outputs.Count})");
            Check(stderr == Stamp + "stderr\n", $"API 1.{minor} stderr differs (expectedCharacters={Stamp.Length + 7}, actualCharacters={stderr.Length}, fragments={outputs.Count})");
            Check(outputs.Count(o => o.StartsBoundary) == 3 && outputs.Count(o => o.EndsBoundary) == 3);
            Check(outputs.Select(o => o.Sequence).SequenceEqual(Enumerable.Range(1, outputs.Count).Select(i => (long)i)));
            Check(outputs.All(o => o.Timestamp?.Nanoseconds == 515050031 && o.Timestamp.UnixSeconds == 1629574695));
            Check(outputs.Where(o => o.StartsBoundary).All(o => o.TimestampPrefixCharacters == Stamp.Length));
            Check(outputs.All(o => o.Boundary == ContainerLogBoundary.MultiplexedFrame));
            Check((await subscription.Completion).Termination == ContainerLogTermination.CleanEnd);
            Check(transport.Paths.Contains($"/v1.{minor}/containers/name%20with%20%2F%3F%CE%B1/json"));
            var path = transport.Paths.Single(p => p.Contains("/logs?"));
            Check(path == fixture.GetProperty("expectedCursorPath").GetString());
            Check(subscription.ContainerId == "canonical" && !subscription.IsTty);
            Check(transport.Handlers.Count == 2 && transport.Handlers[1].Disposed);
        }
        // UTF-8 may span separate frames of one stream, even with another stream interleaved.
        var emoji = Encoding.UTF8.GetBytes("🙂");
        var cross = new LogTestStream([.. Frame(1, emoji[..2]), .. Frame(2, "error\r\n"), .. Frame(1, emoji[2..]), .. Frame(1, "\r"), .. Frame(1, "\n")], fragment: 1);
        var crossed = new LogTransport(cross);
        await using var connection = await Connect(crossed);
        await using var logs = await Open(connection);
        var fragments = await Drain(logs);
        Check(string.Concat(fragments.Where(o => o.Stream == ContainerLogStream.Stdout).Select(o => o.Text)) == "🙂\r\n");
        Check(fragments.Count(o => o.EndsBoundary) == 5 && (await logs.Completion).Termination == ContainerLogTermination.CleanEnd);
    }

    internal static async Task TtyAndPrecision()
    {
        var raw = Stamp + "tty\r\n" + "2021-08-21T21:38:15.000000001+02:00 last🙂";
        var transport = new LogTransport(new(Encoding.UTF8.GetBytes(raw), fragment: 1)) { Tty = true };
        await using var connection = await Connect(transport);
        await using var logs = await Open(connection, new("canonical", 0, true));
        var output = await Drain(logs);
        Check(logs.IsTty && output.All(o => o.Stream == ContainerLogStream.Combined && o.Boundary == ContainerLogBoundary.CombinedLine));
        Check(string.Concat(output.Select(o => o.Text)) == raw);
        Check(output.Count(o => o.StartsBoundary) == 2 && output.Count(o => o.EndsBoundary) == 2);
        var last = output.Last(o => o.Timestamp is not null).Timestamp!;
        Check(last.Nanoseconds == 1 && last.UnixSeconds == 1629574695 && last.Cursor!.Value.ToString() == "1629574695.000000001");
        Check(transport.Paths.Single(p => p.Contains("/logs?")).Contains("tail=0&follow=true"));
        Check(!transport.Paths.Single(p => p.Contains("/logs?")).Contains("since="));
        // Missing timestamp is unknown; content is retained. Multiplex-looking TTY bytes must never be sniffed.
        var ttyText = "\u0001\0\0\0\0\0\0\u0003abc\n";
        var missing = new LogTransport(new(Encoding.UTF8.GetBytes(ttyText))) { Tty = true };
        await using var second = await Connect(missing);
        await using var unknown = await Open(second);
        var unknownOutput = await Drain(unknown);
        Check(string.Concat(unknownOutput.Select(o => o.Text)) == ttyText && unknownOutput.All(o => o.Timestamp is null));
    }

    internal static async Task LongOutputAndMalformed()
    {
        foreach (bool tty in new[] { false, true })
        {
            var text = Stamp + new string('x', 200_000) + "🙂\r\n";
            var source = new LogTestStream(tty ? Encoding.UTF8.GetBytes(text) : Frame(1, text), randomSeed: 73);
            var transport = new LogTransport(source) { Tty = tty };
            await using var connection = await Connect(transport, new() { MaximumLogFragmentCharacters = 64, LogBufferCapacity = 2 });
            await using var logs = await Open(connection);
            var output = await Drain(logs);
            Check(output.All(o => o.Text.Length <= 64) && string.Concat(output.Select(o => o.Text)) == text);
            Check(output.Count(o => o.StartsBoundary) == 1 && output.Count(o => o.EndsBoundary) == 1);
        }
        byte[] reserved = Frame(1, "x"); reserved[2] = 1;
        foreach (var bytes in new[] { reserved, Frame(9, ""), Frame(0, ""), new byte[] { 1, 0, 0 }, Frame(1, "partial", 99),
            Frame(1, new byte[] { 0xff }), Frame(1, new byte[] { 0xf0, 0x9f }), Frame(3, "bad", 90) })
        {
            var transport = new LogTransport(new(bytes, fragment: 1));
            await using var connection = await Connect(transport);
            await using var logs = await Open(connection);
            await Drain(logs);
            Check((await logs.Completion).Failure?.Kind == ContainerLogFailureKind.MalformedOutput);
        }
        var huge = new LogTransport(new(Frame(1, new string('v', 100_000), uint.MaxValue)));
        await using var owner = await Connect(huge, new() { MaximumLogFragmentCharacters = 64 });
        await using var partial = await Open(owner);
        var fragments = await Drain(partial);
        Check(string.Concat(fragments.Select(o => o.Text)) == new string('v', 100_000));
        Check(fragments.All(o => o.Text.Length <= 64) && !fragments.Any(o => o.EndsBoundary));
        Check((await partial.Completion).Failure?.Kind == ContainerLogFailureKind.MalformedOutput);
        var empty = new LogTransport(new([.. Frame(1, ""), .. Frame(2, "")]));
        await using var emptyOwner = await Connect(empty);
        await using var zero = await Open(emptyOwner);
        Check((await Drain(zero)).Count == 2 && (await zero.Completion).Termination == ContainerLogTermination.CleanEnd);
    }

    internal static async Task ErrorsAndValidation()
    {
        var unused = new LogTransport(new([]));
        await using var owner = await Connect(unused, new() { MaximumLogTailLines = 30 });
        foreach (var request in new ContainerLogRequest[] { new("", 1), new("..", 1), new(new string('x', 4097), 1),
            new("canonical", -1), new("canonical", int.MaxValue), new("canonical", 31),
            new("canonical", 1, Since: new(-1)), new("canonical", 1, Since: new(1, 1_000_000_000)), null! })
            Check((await ((IContainerLogSource)owner).OpenLogsAsync(request)).Failure?.Kind == ContainerLogFailureKind.InvalidRequest);
        Check(unused.Handlers.Count == 1 && unused.Paths.Count == 3);
        foreach (bool inspect in new[] { false, true })
        foreach (var (status, body, kind) in new[] {
            (404, "{\"message\":\"No such container: private\"}", ContainerLogFailureKind.NotFound),
            (403, "{\"message\":\"private access\"}", ContainerLogFailureKind.AccessDenied),
            (400, "{\"message\":\"private bad parameter\"}", ContainerLogFailureKind.InvalidRequest),
            (500, "{\"message\":\"configured logging driver does not support reading\"}", ContainerLogFailureKind.LoggingUnavailable),
            (500, "{\"message\":\"private daemon error\"}", ContainerLogFailureKind.DaemonError) })
        {
            var transport = new LogTransport(new([])) { Status = status, ErrorBody = body, FailInspect = inspect };
            await using var connection = await Connect(transport);
            var failed = await ((IContainerLogSource)connection).OpenLogsAsync(new("canonical", 20));
            Check(failed.Failure?.Kind == kind && failed.Failure.HttpStatusCode == status);
            Check(failed.Failure!.DaemonMessage is not null && !failed.Failure.Description.Contains("private"));
            Check(transport.Handlers[1].Disposed && transport.ErrorContents.All(c => c.Disposed));
        }
        var truncated = new LogTransport(new([])) { Status = 500, ErrorBody = new string('s', 1000) };
        await using var limited = await Connect(truncated, new() { MaximumLogErrorBytes = 64 });
        var error = await ((IContainerLogSource)limited).OpenLogsAsync(new("canonical", 0));
        Check(error.Failure?.DaemonMessage == new string('s', 64) && error.Failure.DaemonMessageTruncated);
        foreach (var inspect in new[] { "{\"Id\":\"canonical\"}", "{\"Id\":\"canonical\",\"Config\":{\"Tty\":\"false\"}}", "[]", "{broken" })
        {
            var bad = new LogTransport(new([])) { InspectBody = inspect };
            await using var connection = await Connect(bad);
            Check((await ((IContainerLogSource)connection).OpenLogsAsync(new("canonical", 1))).Failure?.Kind == ContainerLogFailureKind.MalformedOutput);
            Check(!bad.Paths.Any(p => p.Contains("/logs?")));
        }
        var disabled = new LogTransport(new([])) { InspectBody = "{\"Id\":\"canonical\",\"Config\":{\"Tty\":false},\"HostConfig\":{\"LogConfig\":{\"Type\":\"none\"}}}" };
        await using var disabledOwner = await Connect(disabled);
        Check((await ((IContainerLogSource)disabledOwner).OpenLogsAsync(new("canonical", 1))).Failure?.Kind == ContainerLogFailureKind.LoggingUnavailable);
        Check(!disabled.Paths.Any(p => p.Contains("/logs?")));
        var system = new LogTransport(new([.. Frame(1, "valid\n"), .. Frame(3, new string('e', 1000))]));
        await using var sysOwner = await Connect(system, new() { MaximumLogErrorBytes = 64 });
        await using var sysLogs = await Open(sysOwner);
        Check(string.Concat((await Drain(sysLogs)).Select(o => o.Text)) == "valid\n");
        var sysResult = await sysLogs.Completion;
        Check(sysResult.Failure?.Kind == ContainerLogFailureKind.DaemonError && sysResult.Failure.DaemonMessage == new string('e', 64)
            && sysResult.Failure.DaemonMessageTruncated);
        var unavailable = new LogTransport(new(Frame(3, "Error grabbing logs: configured logging driver does not support reading\n")));
        await using var unavailableOwner = await Connect(unavailable);
        await using var unavailableLogs = await Open(unavailableOwner);
        await Drain(unavailableLogs);
        Check((await unavailableLogs.Completion).Failure?.Kind == ContainerLogFailureKind.LoggingUnavailable);

    }

    internal static async Task PressureCancellationAndAdmission()
    {
        var stream = new LogTestStream(Frame(1, Stamp + new string('x', 500_000)), end: false);
        var transport = new LogTransport(stream);
        await using var owner = await Connect(transport, new() { MaximumConcurrentLogSubscriptions = 1, LogBufferCapacity = 2, MaximumLogFragmentCharacters = 64 });
        using var cancel = new CancellationTokenSource();
        await using var logs = await Open(owner, token: cancel.Token);
        await stream.WaitForReads(2);
        int reads = stream.Reads; await Task.Delay(50); Check(stream.Reads == reads && reads <= 2, "Backpressure must stop producer reads");
        Check((await ((IContainerLogSource)owner).OpenLogsAsync(new("other", 2))).Failure?.Kind == ContainerLogFailureKind.SubscriptionLimit);
        Check(transport.Handlers.Count == 2);
        cancel.Cancel();
        Check((await logs.Completion.WaitAsync(TimeSpan.FromSeconds(3))).Termination == ContainerLogTermination.Canceled);
        Check(!logs.Output.TryRead(out _) && !await logs.Output.WaitToReadAsync());
        Check(stream.Disposed && transport.Handlers[1].Disposed);
        await logs.DisposeAsync();
        await using var reopened = await Open(owner);
        await reopened.DisposeAsync();
        Check(transport.Handlers.Count == 3);
        using var pre = new CancellationTokenSource(); pre.Cancel();
        await Canceled(async () => await ((IContainerLogSource)owner).OpenLogsAsync(new("canonical", 0), pre.Token));
        Check(transport.Handlers.Count == 3);
        // Natural completion remains caller-owned and keeps capacity until disposed.
        var finite = new LogTransport(new(Frame(1, "done\n")));
        await using var connection = await Connect(finite, new() { MaximumConcurrentLogSubscriptions = 1 });
        await using var history = await Open(connection);
        await Drain(history); await history.Completion;
        Check((await ((IContainerLogSource)connection).OpenLogsAsync(new("canonical", 2))).Failure?.Kind == ContainerLogFailureKind.SubscriptionLimit);
        await history.DisposeAsync();
        await using var next = await Open(connection);
        // Owner disposal must join pressure-blocked producers and invalidate all buffered output.
        var blocked = new LogTransport(new(Frame(1, new string('x', 100_000)), end: false));
        var disposingOwner = await Connect(blocked, new() { LogBufferCapacity = 1, MaximumLogFragmentCharacters = 64 });
        var blockedLogs = await Open(disposingOwner);
        await blocked.Stream.WaitForReads(2);
        await disposingOwner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check((await blockedLogs.Completion).Termination == ContainerLogTermination.Canceled && !blockedLogs.Output.TryRead(out _));
        Check(blocked.Handlers.All(h => h.Disposed) && blocked.Stream.Disposed);
        await blockedLogs.DisposeAsync();
    }

    internal static async Task LifetimesAndContinuity()
    {
        var slow = new LogTransport(new([])) { StallStage = "logs" };
        await using var owner = await Connect(slow, new() { LogOpeningTimeout = TimeSpan.FromMilliseconds(80) });
        var timed = await ((IContainerLogSource)owner).OpenLogsAsync(new("canonical", 2));
        Check(timed.Failure?.Kind == ContainerLogFailureKind.OpeningDeadline && slow.Handlers[1].Disposed);
        var live = new LogTransport(new(Frame(1, "live\n"), end: false));
        await using var liveOwner = await Connect(live, new() { RequestTimeout = TimeSpan.FromMilliseconds(100), LogOpeningTimeout = TimeSpan.FromMilliseconds(120) });
        await using var logs = await Open(liveOwner, new("canonical", 0, true));
        await live.Stream.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(200); Check(!logs.Completion.IsCompleted);
        await logs.DisposeAsync(); Check((await logs.Completion).Termination == ContainerLogTermination.Canceled);
        foreach (bool streamPool in new[] { false, true })
        {
            var transport = new LogTransport(new(Frame(1, "buffered\n"), end: false));
            await using var connection = await Connect(transport);
            await using var subscription = await Open(connection);
            await transport.Stream.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            transport.Counters[streamPool ? 1 : 0].Opened();
            Check((await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(3))).Failure?.Kind == ContainerLogFailureKind.TransportLoss);
            Check(!subscription.Output.TryRead(out _) && transport.Handlers[1].Disposed);
            Check(transport.Paths.Count(p => p.Contains("/logs?")) == 1);
        }
        var mismatch = new LogTransport(new([])) { DifferentEngine = true };
        await using var selected = await Connect(mismatch);
        Check((await ((IContainerLogSource)selected).OpenLogsAsync(new("canonical", 1))).Failure?.Kind == ContainerLogFailureKind.TransportLoss);
        Check(!mismatch.Paths.Any(p => p.Contains("/logs?")));
        var lost = new LogTransport(new(Frame(1, "sensitive\n")) { ThrowAfterData = true });
        await using var lostOwner = await Connect(lost);
        await using var lostLogs = await Open(lostOwner);
        Check((await lostLogs.Completion).Failure?.Kind == ContainerLogFailureKind.TransportLoss && !lostLogs.Output.TryRead(out _));
        Check(lost.Paths.Count(p => p.Contains("/logs?")) == 1);
        foreach (var exception in new Exception[] { new UnauthorizedAccessException("private"), new IOException("private") })
        {
            var failing = new LogTransport(new([])) { OpeningException = exception };
            await using var failedOwner = await Connect(failing);
            var failed = await ((IContainerLogSource)failedOwner).OpenLogsAsync(new("canonical", 1));
            Check(failed.Failure?.Kind == (exception is UnauthorizedAccessException ? ContainerLogFailureKind.AccessDenied : ContainerLogFailureKind.TransportLoss));
            Check(!failed.Failure!.Description.Contains("private"));
        }
        foreach (var stage in new[] { "handshake", "inspect", "logs" })
        {
            var pendingTransport = new LogTransport(new([])) { StallStage = stage };
            var pendingOwner = await Connect(pendingTransport);
            using var token = new CancellationTokenSource();
            var opening = ((IContainerLogSource)pendingOwner).OpenLogsAsync(new("canonical", 1), token.Token);
            await pendingTransport.OpeningStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); token.Cancel();
            await Canceled(async () => await opening);
            await pendingOwner.DisposeAsync();
            Check(pendingTransport.Handlers.All(h => h.Disposed));
        }
    }

    internal static async Task OpeningDisposalRaces()
    {
        foreach (bool caller in new[] { false, true })
        {
            var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new LogTransport(new([])) { LateOpening = late };
            var owner = await Connect(transport, new() { MaximumConcurrentLogSubscriptions = 1 });
            using var cancel = new CancellationTokenSource();
            var pending = ((IContainerLogSource)owner).OpenLogsAsync(new("canonical", 1), cancel.Token);
            await transport.OpeningStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task disposing = caller ? Task.CompletedTask : owner.DisposeAsync().AsTask();
            if (caller) cancel.Cancel();
            await Task.Delay(30);
            Check(!pending.IsCompleted && (caller || !disposing.IsCompleted), "Disposal must join a late transport response");
            late.SetResult(new(HttpStatusCode.OK) { Content = new StreamContent(transport.Stream) });
            if (caller) await Canceled(async () => await pending);
            else Check(!(await pending).IsSubscribed);
            await disposing; await owner.DisposeAsync();
            Check(transport.Stream.Disposed && transport.Handlers.All(h => h.Disposed));
        }
        var admission = new LogTransport(new([])) { StallStage = "logs" };
        var selected = await Connect(admission, new() { MaximumConcurrentLogSubscriptions = 1 });
        var first = ((IContainerLogSource)selected).OpenLogsAsync(new("canonical", 2));
        await admission.OpeningStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check((await ((IContainerLogSource)selected).OpenLogsAsync(new("canonical", 2))).Failure?.Kind == ContainerLogFailureKind.SubscriptionLimit);
        await selected.DisposeAsync(); Check(!(await first).IsSubscribed && admission.Handlers.All(h => h.Disposed));
    }

    internal static async Task SimultaneousNamedPipe()
    {
        // Actual SocketsHttpHandler over four controlled pipe connections, not a real Docker daemon.
        var name = "cdd-logs-" + Guid.NewGuid().ToString("N")[..8];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipes = Enumerable.Range(0, 4).Select(_ => new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)).ToArray();
        var paths = new ConcurrentQueue<string>();
        async Task Serve(NamedPipeServerStream pipe)
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            while (true)
            {
                var header = new StringBuilder(); var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    try { if (await pipe.ReadAsync(one, deadline.Token) == 0) return; }
                    catch (IOException) when (header.Length == 0) { return; }
                    Check(header.Length < 8192); header.Append((char)one[0]);
                }
                var path = header.ToString().Split(' ')[1]; paths.Enqueue(path);
                var contentLengthLine = header.ToString().Split("\r\n").SingleOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                int contentLength = contentLengthLine is null ? 0 : int.Parse(contentLengthLine.Split(':')[1]);
                Check(contentLength <= 4096);
                var requestBody = new byte[contentLength]; await pipe.ReadExactlyAsync(requestBody, deadline.Token);
                if (path.EndsWith("/exec"))
                {
                    using var createBody = System.Text.Json.JsonDocument.Parse(requestBody);
                    Check(createBody.RootElement.GetProperty("Cmd").EnumerateArray().Single().GetString() == "explicit-command"
                        && createBody.RootElement.GetProperty("AttachStdin").GetBoolean() && createBody.RootElement.GetProperty("Tty").GetBoolean());
                }
                if (path.Contains("/logs?") || path.Contains("/events?") || path.Contains("/stats?"))
                {
                    var media = path.Contains("/logs?") ? "application/vnd.docker.multiplexed-stream" : "application/json";
                    await pipe.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: {media}\r\n\r\n"), deadline.Token);
                    var bytes = path.Contains("/logs?") ? Frame(1, Stamp + "pipe🙂\r\n") : Encoding.UTF8.GetBytes(path.Contains("/stats?") ? "{\"id\":\"canonical\",\"memory_stats\":{\"usage\":123}}" : EventChecks.Event + "\n");
                    foreach (var chunk in bytes.Chunk(3))
                    {
                        await pipe.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"), deadline.Token);
                        await pipe.WriteAsync(chunk, deadline.Token);
                        await pipe.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), deadline.Token); await pipe.FlushAsync(deadline.Token);
                    }
                    try { Check(await pipe.ReadAsync(one, deadline.Token) == 0); } catch (IOException) { }
                    return;
                }
                string body = path switch
                {
                    "/_ping" => "OK", "/version" => "{\"Version\":\"fixture\",\"ApiVersion\":\"1.56\",\"MinAPIVersion\":\"1.40\"}",
                    "/v1.56/containers/json?all=true" => "[{\"Id\":\"canonical\"}]",
                    "/v1.56/containers/canonical/json" => "{\"Id\":\"canonical\",\"Config\":{\"Tty\":false},\"State\":{\"Status\":\"exited\",\"Running\":false},\"Mounts\":[{\"Type\":\"volume\",\"Name\":\"plugin/full?name\"}]}",
                    var candidatePath when candidatePath.StartsWith("/v1.56/containers/json?all=true&filters=") => "[{\"Id\":\"canonical\",\"Names\":[\"/private-original-name\"]}]",
                    "/v1.56/images/json?all=true&digests=true" => "[{\"Id\":\"sha256:authoritative-image\",\"RepoTags\":[\"repo/name:tag\"],\"Size\":0}]",
                    "/v1.56/images/repo%2Fname%3Atag/json" => "{\"Id\":\"sha256:authoritative-image\",\"Config\":{\"Env\":[\"SYNTHETIC_SECRET=preserve\"]}}",
                    "/v1.56/images/repo%2Fname%3Atag?force=false&noprune=false" => "[{\"Untagged\":\"repo/name:tag\"}]",
                    "/v1.56/networks" => "[{\"Id\":\"complete-network-id\",\"Name\":\"private network\",\"Internal\":false}]",
                    "/v1.56/networks/complete-network-id" => "{\"Id\":\"complete-network-id\",\"IPAM\":{\"Driver\":\"custom\"},\"Containers\":{\"canonical\":{\"EndpointID\":\"complete-endpoint-id\",\"IPv4Address\":\"private-address\"}}}",
                    "/v1.56/volumes" => "{\"Volumes\":[{\"Name\":\"plugin/full?name\",\"Driver\":\"custom\"}],\"Warnings\":[\"private synthetic warning\"]}",
                    "/v1.56/volumes/plugin%2Ffull%3Fname" => "{\"Name\":\"plugin/full?name\",\"Status\":{\"token\":\"private synthetic secret\"}}",
                    var execPath when execPath == $"/v1.56/containers/{ExecChecks.ContainerId}/exec" => $$"""{"Id":"{{ExecChecks.ExecId}}"}""",
                    var execPath when execPath == $"/v1.56/exec/{ExecChecks.ExecId}/json" => $$"""{"ID":"{{ExecChecks.ExecId}}","ContainerID":"{{ExecChecks.ContainerId}}","Running":false,"ExitCode":0}""",
                    _ => "{\"ID\":\"same-engine\"}"
                };
                var payload = Encoding.UTF8.GetBytes(body);
                bool command = (header.ToString().StartsWith("DELETE ") && path == "/v1.56/networks/complete-network-id") || path.EndsWith("/start") || path == "/v1.56/volumes/plugin%2Ffull%3Fname?force=false"; if (command) payload = [];
                await pipe.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(command ? "204 No Content" : path.EndsWith("/exec") ? "201 Created" : "200 OK")}\r\nContent-Length: {payload.Length}\r\n\r\n"), deadline.Token);
                await pipe.WriteAsync(payload, deadline.Token); await pipe.FlushAsync(deadline.Token);
            }
        }
        var serving = Task.WhenAll(pipes.Select(pipe => Task.Run(() => Serve(pipe))));
        try
        {
            var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true), new() { RequestTimeout = TimeSpan.FromMilliseconds(500) })
                .ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token);
            Check(attempt.IsConnected); await using var owner = attempt.Connection!;
            await using var logs = await Open(owner, new("canonical", 10, true));
            var eventsAttempt = await ((IEngineEventSource)owner).SubscribeEventsAsync(cancellationToken: deadline.Token);
            Check(eventsAttempt.IsSubscribed); await using var events = eventsAttempt.Subscription!;
            var statsAttempt = await ((IContainerStatsSource)owner).SubscribeStatsAsync("canonical", deadline.Token);
            Check(statsAttempt.IsSubscribed); await using var stats = statsAttempt.Subscription!;
            Check((await stats.Samples.ReadAsync(deadline.Token)).Memory!.Usage == 123);
            string text = "";
            while (true)
            {
                var item = await logs.Output.ReadAsync(deadline.Token); text += item.Text;
                if (item.EndsBoundary) break;
            }
            Check(text == Stamp + "pipe🙂\r\n");
            Check((await events.Events.ReadAsync(deadline.Token)).Type == "container");
            await Task.Delay(600, deadline.Token);
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted);
            Check((await ((IContainerReader)owner).ListContainersAsync(deadline.Token)).IsSuccess);
            Check((await ((IContainerReader)owner).InspectContainerAsync("canonical", deadline.Token)).IsSuccess);
            Check((await ((IImageReader)owner).ListImagesAsync(deadline.Token)).Value![0].Id == "sha256:authoritative-image");
            Check((await ((IImageReader)owner).InspectImageAsync("repo/name:tag", deadline.Token)).Value!.Configuration!.Environment!.Single() == "SYNTHETIC_SECRET=preserve");
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Borrowed image reads must leave live streams running");
            var networkList = ((INetworkReader)owner).ListNetworksAsync(deadline.Token);
            var networkDetail = ((INetworkReader)owner).InspectNetworkAsync("complete-network-id", deadline.Token);
            await Task.WhenAll(networkList, networkDetail, ((IContainerReader)owner).ListContainersAsync(deadline.Token),
                ((IImageReader)owner).ListImagesAsync(deadline.Token), ((IVolumeReader)owner).ListVolumesAsync(deadline.Token),
                owner.CheckLivenessAsync(deadline.Token));
            Check(networkList.Result.Value!.Single().Id == "complete-network-id"
                && networkDetail.Result.Value!.Containers!["canonical"].EndpointId == "complete-endpoint-id");
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Borrowed network reads leave live streams running");
            var volumes = (await ((IVolumeReader)owner).ListVolumesAsync(deadline.Token)).Value!;
            Check(volumes.Volumes![0].Name == "plugin/full?name" && volumes.HasWarnings);
            Check((await ((IVolumeReader)owner).InspectVolumeAsync(volumes.Volumes[0].Name, deadline.Token)).Value!.RawJson.Contains("private synthetic secret"));
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Borrowed volume reads must leave live streams running");
            var dependencies = (await ((IVolumeDependencyReader)owner).QueryVolumeDependenciesAsync("plugin/full?name", deadline.Token)).Value!;
            Check(dependencies.Completeness == VolumeDependencyCompleteness.Complete && dependencies.Containers!.Single().Id == "canonical"
                && dependencies.Containers![0].State == "exited" && dependencies.Containers![0].Names!.Single() == "/private-original-name");
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Borrowed dependency reads leave live streams running");
            Check((await ((IContainerCommands)owner).StartContainerAsync("canonical", deadline.Token)).Disposition == ContainerCommandDisposition.Acknowledged);
            var removal = await ((IImageCommands)owner).DeleteImageAsync("repo/name:tag", deadline.Token);
            Check(removal.Disposition == ImageCommandDisposition.Acknowledged && removal.RemovalRecords!.Single().Untagged == "repo/name:tag" && removal.RequiresReconciliation);
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Image mutation leaves retained live streams running");
            var volumeRemoval = await ((IVolumeCommands)owner).DeleteVolumeAsync("plugin/full?name", deadline.Token);
            Check(volumeRemoval.Disposition == VolumeCommandDisposition.Acknowledged && volumeRemoval.RequiresReconciliation);
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Volume deletion leaves unrelated live streams running");
            var networkRemoval = await ((INetworkCommands)owner).DeleteNetworkAsync("complete-network-id", deadline.Token);
            Check(networkRemoval.Disposition == NetworkCommandDisposition.Acknowledged && networkRemoval.RequiresReconciliation);
            var createdExec = await ((IContainerExec)owner).CreateTtyExecAsync(new(ExecChecks.ContainerId, ["explicit-command"]), deadline.Token);
            Check(createdExec.IsUsableAcknowledgement && createdExec.AcknowledgedInstance!.ExecId == ExecChecks.ExecId);
            var inspectedExec = await ((IContainerExec)owner).InspectExecAsync(createdExec.AcknowledgedInstance!, deadline.Token);
            Check(inspectedExec.IsSuccess && inspectedExec.Value!.Running == false && inspectedExec.Value.ExitCode == 0);
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Exec protocol requests leave all live streams running");
            Check(!logs.Completion.IsCompleted && !events.Completion.IsCompleted && !stats.Completion.IsCompleted, "Network deletion leaves unrelated live streams running");
            Check((await ((INetworkReader)owner).ListNetworksAsync(deadline.Token)).IsSuccess);
            Check((await ((IVolumeReader)owner).ListVolumesAsync(deadline.Token)).IsSuccess);
            Check((await ((IImageReader)owner).ListImagesAsync(deadline.Token)).IsSuccess);
            Check((await ((IContainerCommands)owner).StartContainerAsync("canonical", deadline.Token)).Disposition == ContainerCommandDisposition.Acknowledged);
            Check(await owner.CheckLivenessAsync(deadline.Token) is null);
            await logs.DisposeAsync(); await events.DisposeAsync(); await stats.DisposeAsync();
            Check((await ((INetworkReader)owner).ListNetworksAsync(deadline.Token)).IsSuccess);
            Check((await ((INetworkReader)owner).InspectNetworkAsync("complete-network-id", deadline.Token)).IsSuccess);
            Check((await ((IVolumeReader)owner).ListVolumesAsync(deadline.Token)).Value!.HasWarnings);
            Check((await ((IImageReader)owner).ListImagesAsync(deadline.Token)).IsSuccess);
            Check((await ((IContainerReader)owner).ListContainersAsync(deadline.Token)).IsSuccess);
            Check(await owner.CheckLivenessAsync(deadline.Token) is null);
            await owner.DisposeAsync(); await serving;
            Check(paths.Count(p => p == "/version") == 4 && paths.Count(p => p.Contains("/logs?")) == 1 && paths.Count(p => p.Contains("/events?")) == 1 && paths.Count(p => p.Contains("/stats?")) == 1);
            Check(paths.Count(p => p.EndsWith("/exec")) == 1 && paths.Count(p => p == $"/v1.56/exec/{ExecChecks.ExecId}/json") == 1);
        }
        finally { foreach (var pipe in pipes) pipe.Dispose(); }
    }
}

internal sealed class LogTransport(LogTestStream stream, int minor = 56) : IDockerTransportFactory
{
    public LogTestStream Stream => stream;
    public List<TrackingHandler> Handlers { get; } = [];
    public List<PipeConnectionCounter> Counters { get; } = [];
    public ConcurrentQueue<string> Paths { get; } = new();
    public ConcurrentQueue<TrackedContent> ErrorContents { get; } = new();
    public bool Tty { get; init; }
    public int Status { get; init; } = 200;
    public bool FailInspect { get; init; }
    public string ErrorBody { get; init; } = "error";
    public string? InspectBody { get; init; }
    public bool DifferentEngine { get; init; }
    public string? StallStage { get; init; }
    public Exception? OpeningException { get; init; }
    public TaskCompletionSource<HttpResponseMessage>? LateOpening { get; init; }
    public TaskCompletionSource OpeningStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
    {
        var pool = Handlers.Count;
        var counter = new PipeConnectionCounter(); counter.Opened(); Counters.Add(counter);
        var handler = new TrackingHandler(async (request, _, token) =>
        {
            var path = request.RequestUri!.PathAndQuery; Paths.Enqueue(path);
            bool inspect = path.EndsWith("/json") && path.Contains("/containers/");
            bool logs = path.Contains("/logs?");
            if (pool > 0 && (StallStage == "handshake" && path == "/_ping" || StallStage == "inspect" && inspect || StallStage == "logs" && logs))
            {
                OpeningStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
            }
            if (logs || inspect && FailInspect)
            {
                OpeningStarted.TrySetResult();
                if (OpeningException is { } exception) throw exception;
                if (logs && LateOpening is { } late) return await late.Task;
                if (Status != 200)
                {
                    var error = new TrackedContent(ErrorBody); ErrorContents.Enqueue(error);
                    return new((HttpStatusCode)Status) { Content = error };
                }
                if (logs) return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            }
            string body = path switch
            {
                "/_ping" => "OK", "/version" => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""",
                _ when path.EndsWith("/info") => "{\"ID\":\"" + (DifferentEngine && pool > 0 ? "changed" : "same-engine") + "\"}",
                _ when inspect => InspectBody ?? "{\"Id\":\"canonical\",\"Config\":{\"Tty\":" + (Tty ? "true" : "false") + "},\"Future\":1}",
                _ => "[]"
            };
            return new(HttpStatusCode.OK) { Content = new TrackedContent(body) };
        });
        Handlers.Add(handler); return new ContinuityHandler(handler, counter);
    }
}

internal sealed class LogTestStream(byte[] bytes, int fragment = 4096, bool end = true, int? randomSeed = null) : Stream
{
    private int position, reads;
    private readonly Random? random = randomSeed is { } seed ? new(seed) : null;
    public bool Disposed { get; private set; }
    public bool ThrowAfterData { get; init; }
    public int Reads => Volatile.Read(ref reads);
    public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task WaitForReads(int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (Reads < count) await Task.Delay(5, deadline.Token);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position == bytes.Length)
        {
            if (ThrowAfterData) throw new IOException("private transport detail");
            if (end) return 0;
            Stalled.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        int count = Math.Min(Math.Min(random?.Next(1, fragment + 1) ?? fragment, buffer.Length), bytes.Length - position);
        bytes.AsMemory(position, count).CopyTo(buffer); position += count; Interlocked.Increment(ref reads); return count;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}
