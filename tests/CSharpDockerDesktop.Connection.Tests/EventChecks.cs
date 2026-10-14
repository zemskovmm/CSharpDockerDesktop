using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class EventChecks
{
    internal const string Event = """{"Type":"container","Action":"start","Actor":{"ID":"full-id","Attributes":{"name":"private-α","future":"kept"}},"time":1629574695,"timeNano":1629574695515050031}""";
    private static void Check(bool value, string message = "Event assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task<IEngineConnection> Connect(EventTransport transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected, attempt.Failure?.Description ?? "Connection missing"); return attempt.Connection!;
    }
    private static async Task<IEngineEventSubscription> Open(IEngineConnection connection, EngineEventCursor? cursor = null, CancellationToken token = default)
    {
        var attempt = await ((IEngineEventSource)connection).SubscribeEventsAsync(cursor, token);
        Check(attempt.IsSubscribed, attempt.Failure?.Description ?? "Subscription missing"); return attempt.Subscription!;
    }
    private static async Task<List<EngineEvent>> Drain(IEngineEventSubscription subscription)
    {
        var observations = new List<EngineEvent>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var observation in subscription.Events.ReadAllAsync(deadline.Token)) observations.Add(observation);
        return observations;
    }
    private static async Task Canceled(Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { } }

    internal static async Task SchemasAndFragmentation()
    {
        for (int minor = 40; minor <= 56; minor++)
        {
            var raw = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Events", $"v1.{minor}.jsonl"));
            var stream = new EventStream(raw, fragmentSize: minor % 2 == 0 ? 1 : 4096, end: true);
            var transport = new EventTransport(stream, minor);
            await using var connection = await Connect(transport);
            await using var subscription = await Open(connection);
            var observations = await Drain(subscription);
            Check(observations.Select(e => e.Type).SequenceEqual(new[] { "container", "image", "volume", "network" }));
            Check(observations.All(e => e.Timestamp.UnixNanoseconds == 1629574695515050031
                && e.Timestamp.UnixSeconds == 1629574695 && e.ActorAttributes!["future-key"] == "private-example"));
            Check(observations[1].Action == "future-action" && observations[3].ResourceId == "fixture-network");
            Check((await subscription.Completion)?.Kind == EngineEventFailureKind.UnexpectedEnd && stream.Disposed);
            Check(transport.Paths.Any(p => p.StartsWith($"/v1.{minor}/events?")) && transport.EventAccept == "application/json");
        }
    }

    internal static async Task TimestampsDuplicatesAndCursor()
    {
        var raw = Event + "\r\n\n" + Event + "\n" + """{"Type":"volume","Action":"prune","Actor":{"ID":"","Attributes":{}},"timeNano":null}""" + "\n"
            + """{"Type":"network","Action":"future","Future":[1,2]}""" + "\n";
        var transport = new EventTransport(new EventStream(raw, fragmentSize: 7, end: true));
        await using var connection = await Connect(transport);
        await using var subscription = await Open(connection, new(1629574695, 515050031));
        var observations = await Drain(subscription);
        Check(observations.Count == 4);
        Check(observations[0].Timestamp == observations[1].Timestamp && observations[0].ResourceId == observations[1].ResourceId);
        Check(observations[2].ResourceId == "" && observations[2].ActorAttributes!.Count == 0 && observations[2].Timestamp.UnixNanoseconds is null);
        Check(observations[3].ResourceId is null && observations[3].ActorAttributes is null && observations[3].Scope is null
            && observations[3].Timestamp.UnixSeconds is null);
        var url = transport.Paths.Single(p => p.Contains("/events?"));
        var query = Uri.UnescapeDataString(url);
        Check(query.EndsWith("&since=1629574695.515050031") && query.Contains("{\"type\":[\"container\",\"image\",\"volume\",\"network\"]}"));
        foreach (var cursor in new[] { new EngineEventCursor(-1), new EngineEventCursor(1, -1), new EngineEventCursor(1, 1_000_000_000) })
        {
            try { await ((IEngineEventSource)connection).SubscribeEventsAsync(cursor); throw new InvalidOperationException("Expected cursor validation"); }
            catch (ArgumentOutOfRangeException) { }
        }
        Check(transport.Handlers.Count == 2);
    }

    internal static async Task BoundedSlowConsumer()
    {
        var stream = new EventStream(string.Concat(Enumerable.Repeat(Event + "\n", 200)), fragmentSize: Encoding.UTF8.GetByteCount(Event + "\n"));
        var transport = new EventTransport(stream);
        await using var connection = await Connect(transport, new() { EventBufferCapacity = 2 });
        var subscription = await Open(connection);
        await stream.WaitForReads(3);
        await Task.Delay(80);
        Check(stream.Reads == 3, "Producer must block at capacity; no drop or unbounded buffering");
        var denied = await ((IEngineEventSource)connection).SubscribeEventsAsync();
        Check(denied.Failure?.Kind == EngineEventFailureKind.SubscriptionLimit && transport.Handlers.Count == 2);
        Check((await subscription.Events.ReadAsync()).ResourceId == "full-id");
        await stream.WaitForReads(4);
        await Task.Delay(50); Check(stream.Reads == 4);
        await subscription.DisposeAsync();
        Check(await subscription.Completion is null && !subscription.Events.TryRead(out _) && stream.Disposed);
        Check(transport.Handlers[1].Disposed && !transport.Handlers[0].Disposed);
        await using var replacement = await Open(connection);
        Check(transport.Handlers.Count == 3);
    }

    internal static async Task InvalidAndOversized()
    {
        foreach (var (raw, kind) in new[]
        {
            ("{bad\n", EngineEventFailureKind.MalformedEvent), ("[]\n", EngineEventFailureKind.MalformedEvent),
            ("{}\n", EngineEventFailureKind.MalformedEvent), ("{\"Type\":\"container\",\"Action\":3}\n", EngineEventFailureKind.MalformedEvent),
            ("{\"Type\":\"image\",\"Action\":\"tag\",\"timeNano\":1.5}\n", EngineEventFailureKind.MalformedEvent),
            ("{\"Type\":\"image\",\"Action\":\"tag\",\"Actor\":[]}\n", EngineEventFailureKind.MalformedEvent),
            ("{\"Type\":\"image\",\"Action\":\"tag\",\"Actor\":{\"Attributes\":{\"x\":null}}}\n", EngineEventFailureKind.MalformedEvent),
            ("{\"Type\":\"image\",\"Action\":\"tag\",\"timeNano\":9223372036854775808}\n", EngineEventFailureKind.MalformedEvent),
            (new string('x', 257), EngineEventFailureKind.OversizedEvent), ("{\"Type\":\"container\"", EngineEventFailureKind.MalformedEvent)
        })
        {
            var stream = new EventStream(raw, fragmentSize: 1, end: true);
            var transport = new EventTransport(stream);
            await using var connection = await Connect(transport, new() { MaximumEventBytes = 256 });
            await using var subscription = await Open(connection);
            Check((await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(3)))?.Kind == kind);
            Check((await Drain(subscription)).Count == 0 && stream.Disposed && transport.Handlers[1].Disposed);
        }
        // Exactly the limit is legal; delimiter bytes do not consume the per-record allocation.
        var exact = "{\"Type\":\"image\",\"Action\":\"tag\"}".PadRight(64) + "\n";
        var atLimit = new EventTransport(new EventStream(exact, end: true));
        await using var last = await Connect(atLimit, new() { MaximumEventBytes = 64 });
        await using var valid = await Open(last);
        Check((await Drain(valid)).Count == 1);
    }

    internal static async Task HttpAndTransportFailures()
    {
        foreach (int status in new[] { 400, 401, 403, 404, 500, 503 })
        {
            var transport = new EventTransport(new EventStream("")) { EventStatus = status, ErrorBody = "{\"message\":\"private daemon explanation\"}" };
            await using var connection = await Connect(transport);
            var attempt = await ((IEngineEventSource)connection).SubscribeEventsAsync();
            Check(attempt.Failure?.Kind == (status is 401 or 403 ? EngineEventFailureKind.AccessDenied
                : status < 500 ? EngineEventFailureKind.InvalidRequest : EngineEventFailureKind.DaemonHttpError));
            Check(attempt.Failure!.HttpStatusCode == status && attempt.Failure.DaemonMessage == "private daemon explanation"
                && !attempt.Failure.Description.Contains("private"));
            Check(transport.Handlers[1].Disposed && transport.Contents.All(c => c.Disposed));
        }
        var oversized = new EventTransport(new EventStream("")) { EventStatus = 503, ErrorBody = new string('x', 1025) };
        await using var tooLarge = await Connect(oversized, new() { MaximumResponseBytes = 1024 });
        var largeFailure = await ((IEngineEventSource)tooLarge).SubscribeEventsAsync();
        Check(largeFailure.Failure is { Kind: EngineEventFailureKind.DaemonHttpError, HttpStatusCode: 503, DaemonMessage: null });
        foreach (Exception error in new Exception[] { new UnauthorizedAccessException(), new HttpRequestException("private"), new IOException("private") })
        {
            var transport = new EventTransport(new EventStream("")) { EventException = error };
            await using var connection = await Connect(transport);
            var attempt = await ((IEngineEventSource)connection).SubscribeEventsAsync();
            Check(attempt.Failure?.Kind == (error is UnauthorizedAccessException ? EngineEventFailureKind.AccessDenied : EngineEventFailureKind.TransportLoss));
            Check(transport.Handlers[1].Disposed);
        }
        var broken = new EventTransport(new EventStream(Event + "\n") { ThrowAfterData = true });
        await using var lost = await Connect(broken);
        await using var failed = await Open(lost);
        Check((await failed.Completion)?.Kind == EngineEventFailureKind.TransportLoss && !failed.Events.TryRead(out _));
    }

    internal static async Task OpeningAndLongLifetime()
    {
        var transport = new EventTransport(new EventStream("")) { StallOpening = true };
        await using var connection = await Connect(transport, new() { EventOpeningTimeout = TimeSpan.FromMilliseconds(80) });
        var failure = await ((IEngineEventSource)connection).SubscribeEventsAsync();
        Check(failure.Failure?.Kind == EngineEventFailureKind.OpeningDeadline && transport.Handlers[1].Disposed);
        var stream = new EventStream(Event + "\n");
        var live = new EventTransport(stream);
        await using var retained = await Connect(live, new() { RequestTimeout = TimeSpan.FromMilliseconds(50), EventOpeningTimeout = TimeSpan.FromMilliseconds(80) });
        await using var subscription = await Open(retained);
        Check((await subscription.Events.ReadAsync()).ResourceId == "full-id");
        await Task.Delay(200);
        Check(!subscription.Completion.IsCompleted && !stream.Disposed);
        Check(await retained.CheckLivenessAsync() is null && (await ((IContainerReader)retained).ListContainersAsync()).IsSuccess);
        await subscription.DisposeAsync(); Check(await subscription.Completion is null);
    }

    internal static async Task CancellationAndDisposal()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var transport = new EventTransport(new EventStream(""));
        var connection = await Connect(transport);
        await Canceled(async () => await ((IEngineEventSource)connection).SubscribeEventsAsync(cancellationToken: canceled.Token));
        Check(transport.Handlers.Count == 1);
        var stalled = new EventTransport(new EventStream("")) { StallOpening = true };
        await using var openingOwner = await Connect(stalled);
        using var cancel = new CancellationTokenSource();
        var opening = ((IEngineEventSource)openingOwner).SubscribeEventsAsync(cancellationToken: cancel.Token);
        await stalled.OpeningStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        await Canceled(async () => await opening);
        Check(stalled.Handlers[1].Disposed);
        using var lifetime = new CancellationTokenSource();
        var subscription = await Open(connection, token: lifetime.Token);
        lifetime.Cancel();
        Check(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(3)) is null);
        await Task.WhenAll(subscription.DisposeAsync().AsTask(), subscription.DisposeAsync().AsTask());
        await using var next = await Open(connection);
        await Task.WhenAll(connection.DisposeAsync().AsTask(), connection.DisposeAsync().AsTask(), next.DisposeAsync().AsTask());
        Check(await next.Completion is null && !next.Events.TryRead(out _) && transport.Handlers.All(h => h.Disposed));
        try { await ((IEngineEventSource)connection).SubscribeEventsAsync(); throw new InvalidOperationException("Expected disposed connection"); }
        catch (ObjectDisposedException) { }
        var race = new EventTransport(new EventStream("")) { StallOpening = true };
        var owner = await Connect(race);
        var pending = ((IEngineEventSource)owner).SubscribeEventsAsync();
        await race.OpeningStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await owner.DisposeAsync();
        Check(!(await pending).IsSubscribed && race.Handlers.All(h => h.Disposed));
    }

    internal static async Task ContinuityAndMetadata()
    {
        var changed = new EventTransport(new EventStream(Event + "\n")) { DifferentStreamEngine = true };
        await using var connection = await Connect(changed);
        Check((await ((IEngineEventSource)connection).SubscribeEventsAsync()).Failure?.Kind == EngineEventFailureKind.TransportLoss);
        Check(!changed.Paths.Any(p => p.Contains("/events?")) && changed.Handlers[1].Disposed);
        var transport = new EventTransport(new EventStream(Event + "\n"));
        await using var owner = await Connect(transport);
        await using var subscription = await Open(owner);
        Check((await subscription.Events.ReadAsync()).ResourceId == "full-id");
        var pendingRead = subscription.Events.WaitToReadAsync().AsTask();
        transport.Counters[0].Opened(); // Ordinary pool replacement invalidates queued/ongoing stream observations.
        Check(!await pendingRead.WaitAsync(TimeSpan.FromSeconds(3)));
        Check(!await subscription.Events.WaitToReadAsync());
        Check((await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(3)))?.Kind == EngineEventFailureKind.TransportLoss);
        Check((await ((IContainerReader)owner).ListContainersAsync()).Failure?.Kind == EngineResourceFailureKind.TransportLoss);
        var replaced = new EventTransport(new EventStream("")) { ReopenEventTransport = true };
        await using var current = await Connect(replaced);
        Check((await ((IEngineEventSource)current).SubscribeEventsAsync()).Failure?.Kind == EngineEventFailureKind.TransportLoss);
        Check(replaced.Handlers[1].Disposed);
    }

    internal static async Task FormatsAndLateOpening()
    {
        foreach (var format in new[] { "application/json", "application/x-ndjson", "application/jsonl", "application/json-seq" })
        {
            var transport = new EventTransport(new EventStream(Event + "\n", end: true)) { EventMediaType = format };
            await using var connection = await Connect(transport);
            var attempt = await ((IEngineEventSource)connection).SubscribeEventsAsync();
            if (format == "application/json-seq")
                Check(attempt.Failure?.Kind == EngineEventFailureKind.MalformedEvent && transport.Handlers[1].Disposed);
            else
            {
                await using var subscription = attempt.Subscription!;
                Check((await Drain(subscription)).Count == 1 && (await subscription.Completion)?.Kind == EngineEventFailureKind.UnexpectedEnd);
            }
        }
        var lateResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new EventTransport(new EventStream("")) { LateResponse = lateResponse };
        var owner = await Connect(late);
        var opening = ((IEngineEventSource)owner).SubscribeEventsAsync();
        await late.OpeningStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var closing = owner.DisposeAsync().AsTask();
        Check(!closing.IsCompleted, "Owner disposal must join outstanding opening work");
        var content = new TrackedContent(Event + "\n");
        lateResponse.SetResult(new(HttpStatusCode.OK) { Content = content });
        await closing.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!(await opening).IsSubscribed && content.Disposed && late.Handlers.All(h => h.Disposed));

        using var cancellation = new CancellationTokenSource();
        var stream = new EventStream(string.Concat(Enumerable.Repeat(Event + "\n", 10)), Encoding.UTF8.GetByteCount(Event + "\n"));
        var blocked = new EventTransport(stream);
        await using var active = await Connect(blocked, new() { EventBufferCapacity = 1 });
        await using var pending = await Open(active, token: cancellation.Token);
        await stream.WaitForReads(2); cancellation.Cancel();
        Check(await pending.Completion.WaitAsync(TimeSpan.FromSeconds(3)) is null && !pending.Events.TryRead(out _) && stream.Disposed);
    }

    internal static async Task ConcurrentNamedPipe()
    {
        // Unix fixture exercises actual SocketsHttpHandler framing and concurrent ownership only.
        var name = "cdd-events-" + Guid.NewGuid().ToString("N")[..8];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var second = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var paths = new ConcurrentQueue<string>();
        var eventClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Serve(NamedPipeServerStream pipe)
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            while (true)
            {
                var headers = new StringBuilder(); var one = new byte[1];
                while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    try { if (await pipe.ReadAsync(one, deadline.Token) == 0) return; }
                    catch (IOException) when (headers.Length == 0) { return; } // Win32 can signal closure as pipe break.
                    if (headers.Length > 8192) throw new IOException("Test request too large");
                    headers.Append((char)one[0]);
                }
                var path = headers.ToString().Split(' ')[1]; paths.Enqueue(path);
                if (path.Contains("/events?"))
                {
                    Check(headers.ToString().Contains("Accept: application/json"));
                    await pipe.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n"), deadline.Token);
                    var bytes = Encoding.UTF8.GetBytes(Event + "\n" + Event + "\n");
                    foreach (var chunk in bytes.Chunk(13))
                    {
                        await pipe.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"), deadline.Token);
                        await pipe.WriteAsync(chunk, deadline.Token);
                        await pipe.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), deadline.Token);
                        await pipe.FlushAsync(deadline.Token);
                    }
                    try { Check(await pipe.ReadAsync(one, deadline.Token) == 0); } catch (IOException) { }
                    eventClosed.TrySetResult(); return;
                }
                var body = path switch
                {
                    "/_ping" => "OK", "/version" => "{\"Version\":\"fixture\",\"ApiVersion\":\"1.56\",\"MinAPIVersion\":\"1.40\"}",
                    "/v1.56/containers/json?all=true" => "[{\"Id\":\"ordinary\"}]",
                    "/v1.56/containers/ordinary/json" => "{\"Id\":\"ordinary\"}", _ => "{\"ID\":\"same-engine\"}"
                };
                var payload = Encoding.UTF8.GetBytes(body);
                var status = path.EndsWith("/start") ? "204 No Content" : "200 OK";
                if (path.EndsWith("/start")) payload = [];
                await pipe.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {payload.Length}\r\n\r\n"), deadline.Token);
                await pipe.WriteAsync(payload, deadline.Token); await pipe.FlushAsync(deadline.Token);
            }
        }
        var serving = Task.WhenAll(Task.Run(() => Serve(first)), Task.Run(() => Serve(second)));
        var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true), new() { RequestTimeout = TimeSpan.FromMilliseconds(300) })
            .ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token);
        Check(attempt.IsConnected); await using var connection = attempt.Connection!;
        await using var subscription = await Open(connection);
        Check((await subscription.Events.ReadAsync(deadline.Token)).Timestamp.UnixNanoseconds == 1629574695515050031);
        Check((await subscription.Events.ReadAsync(deadline.Token)).ResourceId == "full-id");
        await Task.Delay(400, deadline.Token); // Longer than ordinary request timeout.
        Check(!subscription.Completion.IsCompleted);
        Check(await connection.CheckLivenessAsync(deadline.Token) is null);
        var reader = (IContainerReader)connection;
        Check((await reader.ListContainersAsync(deadline.Token)).Value![0].Id == "ordinary");
        Check((await reader.InspectContainerAsync("ordinary", deadline.Token)).IsSuccess);
        Check((await ((IContainerCommands)connection).StartContainerAsync("ordinary", deadline.Token)).Disposition == ContainerCommandDisposition.Acknowledged);
        Check(!subscription.Completion.IsCompleted && (await reader.ListContainersAsync(deadline.Token)).IsSuccess);
        await subscription.DisposeAsync(); await eventClosed.Task.WaitAsync(deadline.Token);
        Check(await connection.CheckLivenessAsync(deadline.Token) is null);
        await connection.DisposeAsync(); await serving;
        Check(paths.Count(p => p == "/version") == 2 && paths.Count(p => p.Contains("/events?")) == 1);
    }
}

internal sealed class EventTransport(EventStream stream, int minor = 56) : IDockerTransportFactory
{
    public List<TrackingHandler> Handlers { get; } = [];
    public List<PipeConnectionCounter> Counters { get; } = [];
    public ConcurrentQueue<string> Paths { get; } = new();
    public ConcurrentQueue<TrackedContent> Contents { get; } = new();
    public string? EventAccept { get; private set; }
    public int EventStatus { get; init; } = 200;
    public string? EventMediaType { get; init; }
    public TaskCompletionSource<HttpResponseMessage>? LateResponse { get; init; }
    public string ErrorBody { get; init; } = "error";
    public Exception? EventException { get; init; }
    public bool StallOpening { get; init; }
    public bool DifferentStreamEngine { get; init; }
    public bool ReopenEventTransport { get; init; }
    public TaskCompletionSource OpeningStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
    {
        var pool = Handlers.Count;
        var counter = new PipeConnectionCounter(); counter.Opened(); Counters.Add(counter);
        var handler = new TrackingHandler(async (request, index, token) =>
        {
            var path = request.RequestUri!.PathAndQuery; Paths.Enqueue(path);
            if (path.Contains("/events?"))
            {
                OpeningStarted.TrySetResult(); EventAccept = request.Headers.Accept.ToString();
                if (LateResponse is { } late) return await late.Task;
                if (ReopenEventTransport) counter.Opened();
                if (StallOpening) await Task.Delay(Timeout.Infinite, token);
                if (EventException is { } exception) throw exception;
                if (EventStatus == 200)
                {
                    var streamed = new StreamContent(stream);
                    if (EventMediaType is { } media) streamed.Headers.ContentType = new(media);
                    return new(HttpStatusCode.OK) { Content = streamed };
                }
                var error = new TrackedContent(ErrorBody); Contents.Enqueue(error);
                return new((HttpStatusCode)EventStatus) { Content = error };
            }
            var body = path switch
            {
                "/_ping" => "OK", "/version" => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""",
                _ when path.EndsWith("/info") => "{\"ID\":\"" + (pool > 0 && DifferentStreamEngine ? "changed-engine" : "same-engine") + "\"}",
                _ => "[]"
            };
            var content = new TrackedContent(body); Contents.Enqueue(content);
            return new(HttpStatusCode.OK) { Content = content };
        });
        Handlers.Add(handler); return new ContinuityHandler(handler, counter);
    }
}

internal sealed class EventStream(string data, int fragmentSize = 4096, bool end = false) : Stream
{
    private readonly byte[] bytes = Encoding.UTF8.GetBytes(data);
    private int position, reads;
    public bool Disposed { get; private set; }
    public bool ThrowAfterData { get; init; }
    public int Reads => Volatile.Read(ref reads);
    public async Task WaitForReads(int count)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (Reads < count) await Task.Delay(5, stop.Token);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position == bytes.Length)
        {
            if (ThrowAfterData) throw new IOException("private failure text");
            if (end) return 0;
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        int count = Math.Min(Math.Min(fragmentSize, buffer.Length), bytes.Length - position);
        bytes.AsMemory(position, count).CopyTo(buffer); position += count; Interlocked.Increment(ref reads); return count;
    }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
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
}
