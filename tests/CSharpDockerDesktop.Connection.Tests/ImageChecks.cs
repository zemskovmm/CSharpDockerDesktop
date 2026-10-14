using System.Net;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class ImageChecks
{
    private static void Check(bool value, string message = "Image assertion failed")
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
        Check(attempt.Connection is IImageReader);
        return attempt.Connection!;
    }
    private static IImageReader Reader(IEngineConnection connection) => (IImageReader)connection;

    internal static async Task ContractFixtures()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "schema-evidence.json")));
        Check(evidence.RootElement.GetArrayLength() == 17);
        for (int minor = 40; minor <= 56; minor++)
        {
            var schema = evidence.RootElement[minor - 40];
            Check(schema.GetProperty("api").GetString() == $"1.{minor}" && schema.GetProperty("sha256").GetString()!.Length == 64);
            Check(schema.GetProperty("listFieldTypes").GetProperty("Created").GetString() == "integer"
                && schema.GetProperty("inspectFieldTypes").GetProperty("Created").GetString() == "string"
                && schema.GetProperty("inspectFieldTypes").GetProperty("Config").GetString() == "ImageConfig");
            Check(schema.GetProperty("inspectFieldTypes").TryGetProperty("ContainerConfig", out _) == (minor < 45));
            Check(schema.GetProperty("inspectFieldTypes").TryGetProperty("VirtualSize", out _) == (minor < 44));
            Check(schema.GetProperty("configFieldTypes").TryGetProperty("Tty", out _) == (minor < 50));
            Check(schema.GetProperty("inspectFieldTypes").TryGetProperty("Identity", out _) == (minor >= 53));
            var list = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.list.json"));
            var inspect = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.inspect.json"));
            var minimal = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.minimal-inspect.json"));
            var urls = new List<string>();
            var transport = Transport((r, n, _) => { Check(r.Method == HttpMethod.Get); urls.Add(r.RequestUri!.PathAndQuery); return Task.FromResult(Response(n == 0 ? list : n == 1 ? inspect : minimal)); }, minor);
            await using var connection = await Connect(transport);
            var listing = await Reader(connection).ListImagesAsync();
            Check(listing.IsSuccess, $"1.{minor} list: {listing.Failure}");
            var row = listing.Value![0]; var untagged = listing.Value[1];
            Check(row.Id.StartsWith("sha256:") && row.Id.Length == 71 && row.RepoTags!.Count == 4 && row.RepoDigests!.Count == 2);
            Check(row.Created == DateTimeOffset.FromUnixTimeSeconds(1644009612) && row.Size == 172064416);
            Check(untagged.RepoTags!.Count == (minor < 43 ? 1 : 0) && untagged.RepoDigests!.Count == (minor < 43 ? 1 : 0));
            Check(untagged.Created == DateTimeOffset.UnixEpoch && untagged.Size == 0);
            var detail = await Reader(connection).InspectImageAsync(row.Id);
            Check(detail.IsSuccess, $"1.{minor} inspect: {detail.Failure}");
            Check(detail.Value!.RawJson == inspect && detail.Value.Configuration!.Environment![0] == "SYNTHETIC_SECRET=retain-private");
            Check(detail.Value.OS == "linux" && detail.Value.Architecture == "arm" && detail.Value.Variant == "v7");
            Check(detail.Value.RootFileSystem!.Layers!.Count == 2 && detail.Value.Configuration!.WorkingDirectory == "/app");
            Check(detail.Value.Configuration!.Entrypoint is null == (minor >= 52));
            var empty = (await Reader(connection).InspectImageAsync(untagged.Id)).Value!;
            Check(empty.Created is null && empty.Size == 0 && empty.RootFileSystem!.Layers!.Count == 0 && empty.Configuration is not null);
            Check(urls[0] == $"/v1.{minor}/images/json?all=true&digests=true" && urls[1] == $"/v1.{minor}/images/{Uri.EscapeDataString(row.Id)}/json");
            Check(!transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
        }
    }

    internal static async Task Projection()
    {
        const string raw = """
          { "Id":"sha256:full-daemon-identity", "RepoTags":["repo/name:Tag"], "RepoDigests":["repo/name@sha256:full"],
            "Created":"2024-01-01T01:02:03.123456789Z", "Size":9007199254740993, "Os":"linux", "OsVersion":"", "Architecture":"arm64", "Variant":"v8",
            "ContainerConfig":{"Env":["WRONG=deprecated"]}, "Config": { "User":"", "Env":["SECRET=preserve-me"], "Cmd":[], "Entrypoint":null,
            "WorkingDir":"/private", "Labels":{"a":"","b":null}, "ExposedPorts":{"80/tcp":{}}, "Volumes":{"/data":null}, "OnBuild":[], "StopSignal":"SIGTERM",
            "Shell":["/bin/sh","-c"], "ArgsEscaped":false, "Healthcheck":{"Test":["CMD","true"]}, "Unknown":{"number":9007199254740993} },
            "RootFS":{"Type":"layers","Layers":["sha256:layer1","sha256:layer2"]}, "Unknown":{"sensitive":"keep-original"} }
          """;
        var transport = Transport((_, n, _) => Task.FromResult(Response(n == 0 ? raw : "{\"Id\":\"minimal\"}")));
        await using var connection = await Connect(transport);
        var detail = (await Reader(connection).InspectImageAsync("repo/name:Tag")).Value!;
        Check(detail.RawJson == raw && detail.Id == "sha256:full-daemon-identity" && detail.Size == 9007199254740993);
        Check(detail.OS == "linux" && detail.OSVersion == "" && detail.Architecture == "arm64" && detail.Variant == "v8" && detail.Created.HasValue);
        Check(detail.RepoTags!.Single() == "repo/name:Tag" && detail.RepoDigests!.Single() == "repo/name@sha256:full");
        var config = detail.Configuration!;
        Check(config.Environment!.Single() == "SECRET=preserve-me" && config.Command!.Count == 0 && config.Entrypoint is null && config.User == "");
        Check(config.Labels!.Count == 2 && config.Labels["a"] == "" && config.Labels["b"] is null);
        Check(config.ExposedPorts!.Single() == "80/tcp" && config.Volumes!.Single() == "/data" && config.OnBuild!.Count == 0);
        Check(config.WorkingDirectory == "/private" && config.StopSignal == "SIGTERM" && config.Shell!.Count == 2 && config.ArgsEscaped == false);
        Check(raw.Contains(config.RawJson) && config.RawJson.Contains("9007199254740993") && config.RawJson.Contains("Healthcheck"));
        Check(detail.RootFileSystem!.Type == "layers" && detail.RootFileSystem.Layers!.SequenceEqual(new[] { "sha256:layer1", "sha256:layer2" }));
        Check(((IList<string>)config.Environment!).IsReadOnly && ((IDictionary<string, string?>)config.Labels!).IsReadOnly);
        var minimal = (await Reader(connection).InspectImageAsync("minimal")).Value!;
        Check(minimal.Created is null && minimal.Size is null && minimal.RepoTags is null && minimal.Configuration is null && minimal.RootFileSystem is null);
        foreach (var nulls in new[] { "", ",\"RepoTags\":null,\"RepoDigests\":null,\"Created\":null,\"Size\":null,\"Config\":null,\"RootFS\":null" })
        {
            var sparse = Transport((_, _, _) => Task.FromResult(Response("{\"Id\":\"x\"" + nulls + "}")));
            await using var sparseConnection = await Connect(sparse);
            var value = (await Reader(sparseConnection).InspectImageAsync("x")).Value!;
            Check(value.RepoTags is null && value.RepoDigests is null && value.Size is null && value.Created is null && value.Configuration is null && value.RootFileSystem is null);
        }
        var metadata = Transport((_, _, _) => Task.FromResult(Response("[{\"Id\":\"x\"},{\"Id\":\"y\",\"RepoTags\":null,\"RepoDigests\":null,\"Created\":null,\"Size\":null},{\"Id\":\"z\",\"RepoTags\":[],\"RepoDigests\":[],\"Created\":0,\"Size\":0}]")));
        await using var metadataConnection = await Connect(metadata);
        var rows = (await Reader(metadataConnection).ListImagesAsync()).Value!;
        Check(rows.Take(2).All(row => row.RepoTags is null && row.RepoDigests is null && row.Size is null && row.Created is null));
        Check(rows[2].RepoTags!.Count == 0 && rows[2].RepoDigests!.Count == 0 && rows[2].Created == DateTimeOffset.UnixEpoch && rows[2].Size == 0);
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
            var result = await reader.InspectImageAsync("registry.example:5000/a/b:Tag");
            Check(result.Failure?.Kind == kinds[n] && result.Failure.HttpStatusCode == statuses[n]);
            Check(result.Failure!.DaemonMessage == "original private daemon message" && !result.Failure.Description.Contains("private"));
        }
        Check(urls.All(u => u == "/v1.56/images/registry.example%3A5000%2Fa%2Fb%3ATag/json"));
        foreach (var identity in new[] { "", " ", ".", "..", "a/b?c#d% space ü", "a//b", "/image", "image/", "image:", "Name", "a/../b", "name@sha256:bad", "name%2Fother", "x\n", "x\0", "a\\b", "repo:" + new string('z', 129), new string('x', 256), new string('x', 4097) })
            Check((await reader.InspectImageAsync(identity)).Failure?.Kind == EngineResourceFailureKind.InvalidRequest);
        Check(urls.Count == statuses.Length && !transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
        Check((await reader.InspectImageAsync(null!)).Failure?.Kind == EngineResourceFailureKind.InvalidRequest);
        Check(urls.Count == statuses.Length);
        var references = new[] { new string('a', 64), "abc123", "sha256:" + new string('b', 64), "alpine", "library/alpine",
            "registry.example:5000/ns/image:Tag_1.2", "repo/name@sha256:" + new string('c', 64),
            "repo/name:tag@sha256:" + new string('d', 64), "[2001:db8::1]:5000/ns/image:tag", "example/a__b--c.d:TAG" };
        var escaped = new List<string>();
        var escaping = Transport((r, _, _) => { escaped.Add(r.RequestUri!.PathAndQuery); return Task.FromResult(Response("{\"Id\":\"authoritative-id\"}")); }, 40);
        await using var escapingConnection = await Connect(escaping);
        foreach (var reference in references)
            Check((await Reader(escapingConnection).InspectImageAsync(reference)).Value?.Id == "authoritative-id");
        Check(escaped.SequenceEqual(references.Select(reference => $"/v1.40/images/{Uri.EscapeDataString(reference)}/json")));
        Check(escaped[5] == "/v1.40/images/registry.example%3A5000%2Fns%2Fimage%3ATag_1.2/json"
            && escaped[6].Contains("repo%2Fname%40sha256%3A") && escaped[8].Contains("%5B2001%3Adb8%3A%3A1%5D%3A5000"));
        for (int n = 0; n < statuses.Length; n++)
        {
            var listing = Transport((_, _, _) => Task.FromResult(Response("{\"message\":\"original private daemon message\"}", statuses[n])));
            await using var listingConnection = await Connect(listing);
            var failure = (await Reader(listingConnection).ListImagesAsync()).Failure!;
            Check(failure.Kind == kinds[n] && failure.DaemonMessage == "original private daemon message" && !failure.Description.Contains("private"));
        }
        var plain = Transport((_, _, _) => Task.FromResult(Response("unaltered plain message", 500)));
        await using var plainConnection = await Connect(plain);
        Check((await Reader(plainConnection).ListImagesAsync()).Failure?.DaemonMessage == "unaltered plain message");
        var denied = Transport((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("wrapped", new UnauthorizedAccessException())));
        await using var deniedConnection = await Connect(denied);
        Check((await Reader(deniedConnection).ListImagesAsync()).Failure?.Kind == EngineResourceFailureKind.AccessDenied);
    }

    internal static async Task MalformedAndBounds()
    {
        foreach (var body in new[] { "{bad", "{}", "null", "[{}]", "[{\"Id\":2}]", "[{\"Id\":\" \"}]", "[{\"Id\":\"\\uD800\"}]", "[{\"Id\":\"x\",\"RepoTags\":1}]", "[{\"Id\":\"x\",\"RepoDigests\":[null]}]", "[{\"Id\":\"x\",\"Size\":-1}]", "[{\"Id\":\"x\",\"Size\":1.5}]", "[{\"Id\":\"x\",\"Created\":9223372036854775807}]" })
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var connection = await Connect(transport);
            var failure = (await Reader(connection).ListImagesAsync()).Failure;
            Check(failure?.Kind == EngineResourceFailureKind.MalformedData && failure.HttpStatusCode == 200);
            Check(transport.Handler.Contents.All(c => c.Disposed) && !transport.Handler.Disposed);
        }
        foreach (var body in new[] { "[]", "{}", "{bad", "null", "{\"Id\":\"x\",\"Config\":[]}", "{\"Id\":\"x\",\"Created\":\"bad\"}", "{\"Id\":\"x\",\"RootFS\":{\"Layers\":1}}",
            "{\"Id\":\"x\",\"Config\":{\"Env\":[1]}}", "{\"Id\":\"x\",\"Config\":{\"Labels\":{\"bad\":true}}}", "{\"Id\":\"x\",\"Config\":{\"Volumes\":{\"/bad\":1}}}",
            "{\"Id\":\"x\",\"Config\":{\"Labels\":{\"\\uD800\":\"value\"}}}",
            "{\"Id\":\"x\",\"Config\":{\"ArgsEscaped\":0}}", "{\"Id\":\"x\",\"Os\":1}", "{\"Id\":\"x\",\"Size\":9223372036854775808}" })
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var connection = await Connect(transport);
            Check((await Reader(connection).InspectImageAsync("x")).Failure?.Kind == EngineResourceFailureKind.MalformedData);
        }
        foreach (var known in new[] { true, false })
        foreach (var status in new[] { 200, 404, 500 })
        foreach (var inspect in new[] { false, true })
        {
            HttpContent content = known ? new TrackedContent(new string('x', 1025)) : new UnknownLengthContent(new string('x', 1025));
            var transport = Transport((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
            await using var connection = await Connect(transport, new() { MaximumResponseBytes = 1024 });
            var failure = inspect ? (await Reader(connection).InspectImageAsync("x")).Failure! : (await Reader(connection).ListImagesAsync()).Failure!;
            Check(failure.HttpStatusCode == status && failure.DaemonMessage is null);
            Check(failure.Kind == (status == 200 ? EngineResourceFailureKind.MalformedData : status == 404 ? EngineResourceFailureKind.NotFound : EngineResourceFailureKind.DaemonFailure));
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
        foreach (var error in new[] { "{bad error", "", "null", "{\"message\":1}", "{\"message\":null}", "{\"message\":\"\"}", "{\"message\":\"\\uD800\"}" })
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(error, 404)));
            await using var connection = await Connect(transport);
            var failure = (await Reader(connection).InspectImageAsync("x")).Failure!;
            Check(failure.Kind == EngineResourceFailureKind.NotFound && failure.HttpStatusCode == 404);
            Check(failure.DaemonMessage == (error == "{bad error" || error.Contains("\\uD800") ? error : error == "{\"message\":\"\"}" ? "" : null));
            Check(transport.Handler.Contents.All(c => c.Disposed));
        }
    }

    private static async Task ExpectCanceled(Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { } }

    internal static async Task CancellationAndDisposal()
    {
        using var already = new CancellationTokenSource(); already.Cancel();
        var ready = Transport((_, _, _) => Task.FromResult(Response("[]")));
        await using (var connection = await Connect(ready))
        {
            await ExpectCanceled(() => Reader(connection).ListImagesAsync(already.Token));
            await ExpectCanceled(() => Reader(connection).InspectImageAsync("x", already.Token));
            await ExpectCanceled(() => Reader(connection).InspectImageAsync("", already.Token));
            Check(ready.Handler.Paths.Count == 3);
        }
        foreach (var mode in new[] { "cancel", "deadline", "dispose", "wrapped-cancel" })
        foreach (var body in new[] { false, true })
        foreach (var inspect in new[] { false, true })
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
            Task<EngineResourceFailure?> FailureAsync()
                => inspect ? InspectFailureAsync(connection, cancel.Token) : ListFailureAsync(connection, cancel.Token);
            var pending = FailureAsync();
            await (body ? stream.Started.Task : started.Task).WaitAsync(TimeSpan.FromSeconds(2));
            if (mode is "cancel" or "wrapped-cancel") { cancel.Cancel(); await ExpectCanceled(() => pending); }
            else
            {
                if (mode == "dispose") await connection.DisposeAsync();
                var failure = await pending;
                Check(failure?.Kind == EngineResourceFailureKind.TransportLoss && failure.Description.Contains(mode == "dispose" ? "disposed" : "deadline"));
            }
            Check(body ? stream.Disposed : true);
            Check(transport.Handler.Disposed == (mode == "dispose"));
            await connection.DisposeAsync(); await connection.DisposeAsync();
            Check(transport.Handler.DisposeCount == 1);
            try { await Reader(connection).ListImagesAsync(); throw new InvalidOperationException("Disposed call accepted"); }
            catch (ObjectDisposedException) { }
            try { await Reader(connection).InspectImageAsync("x"); throw new InvalidOperationException("Disposed inspect accepted"); }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task<EngineResourceFailure?> InspectFailureAsync(IEngineConnection connection, CancellationToken token = default)
        => (await Reader(connection).InspectImageAsync("x", token)).Failure;
    private static async Task<EngineResourceFailure?> ListFailureAsync(IEngineConnection connection, CancellationToken token = default)
        => (await Reader(connection).ListImagesAsync(token)).Failure;

    internal static async Task LateResponses()
    {
        // Misbehaving transport/body ignores cancellation until released. It must never publish late success.
        foreach (var inspect in new[] { false, true })
        foreach (var body in new[] { false, true })
        foreach (var mode in new[] { "caller", "dispose", "deadline" })
        {
            using var caller = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new LateStream(inspect ? "{\"Id\":\"x\"}"u8.ToArray() : "[]"u8.ToArray(), started, canceled, release);
            var content = body ? (HttpContent)new StreamContent(stream) : new TrackedContent(inspect ? "{\"Id\":\"x\"}" : "[]");
            var transport = Transport(async (_, _, token) =>
            {
                if (!body)
                {
                    using var registration = token.Register(() => canceled.TrySetResult());
                    started.TrySetResult();
                    await release.Task;
                }
                return new(HttpStatusCode.OK) { Content = content };
            });
            await using var connection = await Connect(transport, new() { RequestTimeout = TimeSpan.FromMilliseconds(250) });
            var pending = inspect ? InspectFailureAsync(connection, caller.Token) : ListFailureAsync(connection, caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (mode == "caller") caller.Cancel();
            if (mode == "dispose") await connection.DisposeAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!pending.IsCompleted, "Fixture must actually deliver a late result");
            release.SetResult();
            if (mode == "caller") await ExpectCanceled(() => pending);
            else Check((await pending)?.Kind == EngineResourceFailureKind.TransportLoss);
            Check(body ? stream.Disposed : ((TrackedContent)content).Disposed);
            Check(transport.Handler.Disposed == (mode == "dispose"));
        }
        // Exercise the shared decoder's last publication fence deterministically, after valid image JSON is parsed.
        foreach (var cancel in new[] { false, true })
        {
            var transport = new ResourceContinuityTransport("decode");
            await using var connection = await Connect(transport);
            using var lifetime = new CancellationTokenSource();
            var owner = (DockerEngineConnection)connection;
            var generation = owner.Continuity!.ConnectionGeneration;
            var result = await DockerResourceReader.ReadAsync(owner.Http, connection.Metadata.Api.Negotiated,
                "images/json?all=true&digests=true", bytes =>
                {
                    var images = DockerImageJson.List(bytes);
                    if (cancel) lifetime.Cancel(); else transport.Counter.Opened();
                    return images;
                }, new(), owner.Continuity, generation, lifetime.Token, default, "image");
            Check(result.Failure?.Kind == EngineResourceFailureKind.TransportLoss && !transport.Handler.Disposed);
        }
    }

    private sealed class LateStream(byte[] bytes, TaskCompletionSource started, TaskCompletionSource canceled,
        TaskCompletionSource release) : Stream
    {
        private bool sent;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent) return 0;
            using var registration = cancellationToken.Register(() => canceled.TrySetResult());
            started.TrySetResult();
            await release.Task;
            bytes.CopyTo(buffer); sent = true; return bytes.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }

    internal static async Task Continuity()
    {
        foreach (var phase in new[] { "before", "headers", "body" })
        foreach (var inspect in new[] { false, true })
        {
            var transport = new ResourceContinuityTransport(phase);
            await using var connection = await Connect(transport);
            if (phase == "before") transport.Counter.Opened();
            var failure = inspect ? await InspectFailureAsync(connection) : await ListFailureAsync(connection);
            Check(failure?.Kind == EngineResourceFailureKind.TransportLoss);
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
