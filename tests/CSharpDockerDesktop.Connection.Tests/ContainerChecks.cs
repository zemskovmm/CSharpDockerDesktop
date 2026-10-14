using System.Net;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class ContainerChecks
{
    private static void Check(bool value, string message = "Container assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body, int status = 200) => new((HttpStatusCode)status) { Content = new TrackedContent(body) };
    private static string Handshake(int index, int minor = 56) => index switch
    {
        0 => "OK", 1 => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""", _ => "{}"
    };
    private static FakeTransport Transport(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> resource, int minor = 56)
        => new((request, index, token) => index < 3 ? Task.FromResult(Response(Handshake(index, minor))) : resource(request, index - 3, token));
    private static async Task<IEngineConnection> Connect(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected);
        Check(attempt.Connection is IContainerReader);
        return attempt.Connection!;
    }
    private static IContainerReader Reader(IEngineConnection connection) => (IContainerReader)connection;

    internal static async Task ContractFixtures()
    {
        for (int minor = 40; minor <= 56; minor++)
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Containers");
            var list = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.list.json"));
            var inspect = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.inspect.json"));
            var urls = new List<string>();
            var transport = Transport((r, n, _) => { urls.Add(r.RequestUri!.PathAndQuery); return Task.FromResult(Response(n == 0 ? list : inspect)); }, minor);
            await using var connection = await Connect(transport);
            var listing = await Reader(connection).ListContainersAsync();
            Check(listing.IsSuccess, $"1.{minor} list: {listing.Failure}");
            var detail = await Reader(connection).InspectContainerAsync(listing.Value![0].Id);
            Check(detail.IsSuccess, $"1.{minor} inspect: {detail.Failure}");
            Check(detail.Value!.RawJson == inspect);
            Check(urls[0] == $"/v1.{minor}/containers/json?all=true" && urls[1].StartsWith($"/v1.{minor}/containers/"));
            Check(!transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
        }
    }

    internal static async Task Projection()
    {
        var states = new[] { "created", "running", "paused", "restarting", "removing", "exited", "dead", "future-state" };
        var list = JsonSerializer.Serialize(states.Select((s, i) => new { Id = new string((char)('a' + i), 64), State = s, Status = $"actual {s}", Extra = new { any = 42 } }));
        const string raw = """
          { "Id":"full-identity", "Name":"/sample", "Image":"sha256:image", "Config":{"Image":"sample:tag","Env":["SECRET=preserve-me"]},
            "Created":"2024-01-01T01:02:03.123456789Z", "State":{"Status":"paused","Running":true,"Paused":true,"Restarting":false,"Dead":false,"ExitCode":0,"StartedAt":"2024-01-02T00:00:00Z","FinishedAt":"0001-01-01T00:00:00Z","Health":{"Status":"healthy","FailingStreak":0,"Log":[{"Output":"private output"}]}},
            "Mounts":[{"Type":"bind","Source":"/private","Destination":"/data","RW":false}],
            "NetworkSettings":{"Ports":{"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"8080"}],"53/udp":null},"Networks":{"custom":{"NetworkID":"net","IPAddress":"172.18.0.2","Aliases":["sample"]}}},
            "Unknown":{"sensitive":"keep-original"} }
          """;
        var transport = Transport((_, n, _) => Task.FromResult(Response(n == 0 ? list : n == 1 ? raw : "{\"Id\":\"minimal\"}")));
        await using var connection = await Connect(transport);
        var reader = Reader(connection);
        var rows = (await reader.ListContainersAsync()).Value!;
        Check(rows.Select(c => c.State).SequenceEqual(states) && rows.All(c => c.Id.Length == 64));
        Check(rows.All(c => c.Names is null && c.Ports is null && c.Created is null && c.Health is null));
        var detail = (await reader.InspectContainerAsync("sample")).Value!;
        Check(detail.RawJson == raw && detail.Image == "sample:tag" && detail.ImageId == "sha256:image");
        Check(detail.State?.Status == "paused" && detail.State.Running == true && detail.State.Paused == true);
        Check(detail.State!.StartedAt.HasValue && detail.State.FinishedAt is null && detail.State.Health?.Status == "healthy");
        Check(detail.Ports![0].PublicPort == 8080 && detail.Ports[1].PublicPort is null);
        Check(detail.Mounts![0].ReadWrite == false && detail.Networks![0].NetworkId == "net");
        var minimal = (await reader.InspectContainerAsync("minimal")).Value!;
        Check(minimal.State is null && minimal.Created is null && minimal.Networks is null);
    }

    internal static async Task Diagnostics()
    {
        // Exercise the typed subset through every negotiated version, independently of response examples.
        for (int minor = 40; minor <= 56; minor++)
        foreach (var (fields, oom, restarts) in new (string Fields, bool? Oom, long? Restarts)[]
        {
            ("\"State\":{\"OOMKilled\":true},\"RestartCount\":7", true, 7),
            ("\"State\":{\"OOMKilled\":false},\"RestartCount\":0", false, 0),
            ("\"State\":{\"OOMKilled\":null},\"RestartCount\":null", null, null),
            ("\"State\":{}", null, null),
            ("\"State\":null", null, null),
            ("\"RestartCount\":9223372036854775807", null, long.MaxValue)
        })
        {
            string raw = " {\n\"Id\":\"diagnostic\", " + fields
                + ", \"FutureDiagnostic\":{\"private\":\"preserve \\u263a\"},\"Config\":{\"Env\":[\"SECRET=original\"]}\n}\n";
            var paths = new List<string>();
            var transport = Transport((r, _, _) => { paths.Add(r.RequestUri!.AbsolutePath); return Task.FromResult(Response(raw)); }, minor);
            await using var connection = await Connect(transport);
            var result = await Reader(connection).InspectContainerAsync("diagnostic");
            Check(result.IsSuccess && result.Value!.State?.OOMKilled == oom && result.Value.RestartCount == restarts,
                $"1.{minor} diagnostics must distinguish false/zero from unknown: {result.Failure}");
            Check(result.Value!.RawJson == raw && paths.Single() == $"/v1.{minor}/containers/diagnostic/json",
                "Diagnostics must preserve exact raw JSON and negotiated acquisition");
        }
        foreach (string field in new[] { "\"State\":{\"OOMKilled\":VALUE}", "\"RestartCount\":VALUE" })
        foreach (string value in field.Contains("OOMKilled")
            ? new[] { "\"true\"", "0", "1", "{}", "[]" }
            : new[] { "\"7\"", "true", "false", "{}", "[]", "1.5", "9223372036854775808" })
        {
            string raw = "{\"Id\":\"diagnostic\"," + field.Replace("VALUE", value) + "}";
            var transport = Transport((_, _, _) => Task.FromResult(Response(raw)));
            await using var connection = await Connect(transport);
            var failure = (await Reader(connection).InspectContainerAsync("diagnostic")).Failure;
            Check(failure?.Kind == EngineResourceFailureKind.MalformedData && failure.HttpStatusCode == 200
                && failure.DaemonMessage is null, "Invalid diagnostic types must fail without leaking body content");
        }
    }

    internal static async Task ErrorsAndIdentity()
    {
        var urls = new List<string>();
        var statuses = new[] { 404, 409, 400, 422, 500, 503, 401, 403 };
        var kinds = new[] { EngineResourceFailureKind.NotFound, EngineResourceFailureKind.Conflict,
            EngineResourceFailureKind.InvalidRequest, EngineResourceFailureKind.InvalidRequest, EngineResourceFailureKind.DaemonFailure,
            EngineResourceFailureKind.DaemonFailure, EngineResourceFailureKind.AccessDenied, EngineResourceFailureKind.AccessDenied };
        var transport = Transport((r, n, _) =>
        {
            urls.Add(r.RequestUri!.PathAndQuery);
            return Task.FromResult(Response("{\"message\":\"original private daemon message\",\"extra\":true}", statuses[n]));
        });
        await using var connection = await Connect(transport);
        var reader = Reader(connection);
        for (int n = 0; n < statuses.Length; n++)
        {
            var result = await reader.InspectContainerAsync("a/b?c#d% space ü");
            Check(result.Failure?.Kind == kinds[n] && result.Failure.HttpStatusCode == statuses[n]);
            Check(result.Failure!.DaemonMessage == "original private daemon message" && !result.Failure.Description.Contains("private"));
        }
        Check(urls.All(u => u == "/v1.56/containers/a%2Fb%3Fc%23d%25%20space%20%C3%BC/json"));
        foreach (var identity in new[] { "", " ", ".", "..", new string('x', 4097) })
            Check((await reader.InspectContainerAsync(identity)).Failure?.Kind == EngineResourceFailureKind.InvalidRequest);
        Check(urls.Count == statuses.Length && !transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
        var plain = Transport((_, _, _) => Task.FromResult(Response("unaltered plain message", 500)));
        await using var plainConnection = await Connect(plain);
        Check((await Reader(plainConnection).ListContainersAsync()).Failure?.DaemonMessage == "unaltered plain message");
        var denied = Transport((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("wrapped", new UnauthorizedAccessException())));
        await using var deniedConnection = await Connect(denied);
        Check((await Reader(deniedConnection).ListContainersAsync()).Failure?.Kind == EngineResourceFailureKind.AccessDenied);
    }

    internal static async Task MalformedAndBounds()
    {
        foreach (var body in new[] { "{bad", "{}", "null", "[{}]", "[{\"Id\":2}]", "[{\"Id\":\"x\",\"Names\":1}]", "[{\"Id\":\"x\",\"Created\":9223372036854775807}]" })
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var connection = await Connect(transport);
            var failure = (await Reader(connection).ListContainersAsync()).Failure;
            Check(failure?.Kind == EngineResourceFailureKind.MalformedData && failure.HttpStatusCode == 200);
            Check(transport.Handler.Contents.All(c => c.Disposed) && !transport.Handler.Disposed);
        }
        foreach (var body in new[] { "[]", "{}", "{\"Id\":\"x\",\"State\":[]}", "{\"Id\":\"x\",\"Created\":\"bad\"}", "{\"Id\":\"x\",\"NetworkSettings\":{\"Ports\":{\"bad\":null}}}" })
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var connection = await Connect(transport);
            Check((await Reader(connection).InspectContainerAsync("x")).Failure?.Kind == EngineResourceFailureKind.MalformedData);
        }
        foreach (var known in new[] { true, false })
        foreach (var status in new[] { 200, 404, 500 })
        {
            HttpContent content = known ? new TrackedContent(new string('x', 1025)) : new UnknownLengthContent(new string('x', 1025));
            var transport = Transport((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
            await using var connection = await Connect(transport, new() { MaximumResponseBytes = 1024 });
            var failure = (await Reader(connection).ListContainersAsync()).Failure!;
            Check(failure.HttpStatusCode == status && failure.DaemonMessage is null);
            Check(failure.Kind == (status == 200 ? EngineResourceFailureKind.MalformedData : status == 404 ? EngineResourceFailureKind.NotFound : EngineResourceFailureKind.DaemonFailure));
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
    }

    private static async Task ExpectCanceled(Task task)
    { try { await task; throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { } }

    internal static async Task CancellationAndDisposal()
    {
        using var already = new CancellationTokenSource(); already.Cancel();
        var ready = Transport((_, _, _) => Task.FromResult(Response("[]")));
        await using (var connection = await Connect(ready))
        {
            await ExpectCanceled(Reader(connection).ListContainersAsync(already.Token));
            Check(ready.Handler.Paths.Count == 3);
        }
        foreach (var mode in new[] { "cancel", "deadline", "dispose", "wrapped-cancel" })
        foreach (var body in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new StalledStream();
            var transport = Transport(async (_, _, token) =>
            {
                if (body) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (mode == "wrapped-cancel") { throw new HttpRequestException("wrapped cancellation"); }
                return Response("[]");
            });
            var connection = await Connect(transport, new() { RequestTimeout = TimeSpan.FromMilliseconds(150) });
            var pending = Reader(connection).ListContainersAsync(cancel.Token);
            await (body ? stream.Started.Task : started.Task).WaitAsync(TimeSpan.FromSeconds(2));
            if (mode is "cancel" or "wrapped-cancel") { cancel.Cancel(); await ExpectCanceled(pending); }
            else
            {
                if (mode == "dispose") await connection.DisposeAsync();
                Check((await pending).Failure?.Kind == EngineResourceFailureKind.TransportLoss);
            }
            Check(body ? stream.Disposed : true);
            Check(transport.Handler.Disposed == (mode == "dispose"));
            await connection.DisposeAsync(); await connection.DisposeAsync();
            Check(transport.Handler.DisposeCount == 1);
            try { await Reader(connection).ListContainersAsync(); throw new InvalidOperationException("Disposed call accepted"); }
            catch (ObjectDisposedException) { }
        }
    }

    internal static async Task Continuity()
    {
        foreach (var phase in new[] { "before", "headers", "body" })
        {
            var transport = new ResourceContinuityTransport(phase);
            await using var connection = await Connect(transport);
            if (phase == "before") transport.Counter.Opened();
            var result = await Reader(connection).ListContainersAsync();
            Check(result.Failure?.Kind == EngineResourceFailureKind.TransportLoss);
            Check(transport.Handler.Paths.Count == (phase == "before" ? 3 : 4));
            Check(!transport.Handler.Disposed);
            Check((await connection.CheckLivenessAsync())?.Kind == EngineConnectionFailureKind.TransientTransportFailure);
        }
    }

    private sealed class ResourceContinuityTransport(string phase) : IDockerTransportFactory
    {
        public PipeConnectionCounter Counter { get; } = new();
        public TrackingHandler Handler { get; private set; } = null!;
        public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
        {
            Counter.Opened();
            Handler = new TrackingHandler((_, n, _) =>
            {
                if (n < 3) return Task.FromResult(Response(Handshake(n)));
                if (phase == "headers") Counter.Opened();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = phase == "body" ? new StreamContent(new ReopenStream(Counter)) : new TrackedContent("[]") });
            });
            return new ContinuityHandler(Handler, Counter);
        }
    }
    private sealed class ReopenStream(PipeConnectionCounter counter) : MemoryStream("[]"u8.ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { counter.Opened(); return base.ReadAsync(buffer, cancellationToken); }
    }
}
