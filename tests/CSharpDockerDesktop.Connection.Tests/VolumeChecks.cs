using System.Net;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class VolumeChecks
{
    private static void Check(bool value, string message = "Volume assertion failed")
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
        Check(attempt.IsConnected && attempt.Connection is IVolumeReader);
        return attempt.Connection!;
    }
    private static IVolumeReader Reader(IEngineConnection connection) => (IVolumeReader)connection;

    internal static async Task ContractFixtures()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Volumes");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "schema-evidence.json")));
        Check(evidence.RootElement.GetArrayLength() == 17);
        for (int minor = 40; minor <= 56; minor++)
        {
            var schema = evidence.RootElement[minor - 40];
            Check(schema.GetProperty("api").GetString() == $"1.{minor}" && schema.GetProperty("sha256").GetString()!.Length == 64);
            Check(schema.GetProperty("source").GetString() == $"https://docs.docker.com/reference/api/engine/version/v1.{minor}.yaml");
            var fields = schema.GetProperty("volumeFieldTypes");
            foreach (var field in new[] { "Name", "Driver", "Scope", "Mountpoint" }) Check(fields.GetProperty(field).GetString() == "string");
            Check(fields.GetProperty("Labels").GetString() == "object" && fields.TryGetProperty("ClusterVolume", out _) == (minor >= 42));
            Check(schema.GetProperty("labelValueType").GetString() == "string");
            Check(schema.GetProperty("listFieldTypes").GetProperty("Volumes").GetString() == "array"
                && schema.GetProperty("listFieldTypes").GetProperty("Warnings").GetString() == "array"
                && schema.GetProperty("warningItems").GetProperty("type").GetString() == "string");
            Check(schema.GetProperty("listResponseSchema").GetProperty("$ref").GetString() == "#/definitions/VolumeListResponse"
                && schema.GetProperty("inspectResponseSchema").GetProperty("$ref").GetString() == "#/definitions/Volume");
            var parameter = schema.GetProperty("inspectParameters")[0];
            Check(parameter.GetProperty("name").GetString() == "name" && parameter.GetProperty("in").GetString() == "path"
                && parameter.GetProperty("required").GetBoolean() && parameter.GetProperty("type").GetString() == "string");
            Check(schema.GetProperty("listParameters")[0].GetProperty("name").GetString() == "filters");
            Check(schema.GetProperty("listResponses").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "200", "500" })
                && schema.GetProperty("inspectResponses").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "200", "404", "500" }));
            var list = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.list.json"));
            var inspect = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.inspect.json"));
            var urls = new List<string>();
            var transport = Transport((request, n, _) =>
            {
                Check(request.Method == HttpMethod.Get);
                urls.Add(request.RequestUri!.PathAndQuery);
                return Task.FromResult(Response(n == 0 ? list : inspect));
            }, minor);
            await using var owner = await Connect(transport);
            var listing = (await Reader(owner).ListVolumesAsync()).Value!;
            var volume = listing.Volumes![0];
            Check(volume.Name == $"tardis-complete-name-api-1.{minor}" && volume.Driver == "custom"
                && volume.Scope == "local" && volume.Mountpoint == "/var/lib/docker/volumes/tardis"
                && volume.Labels!["com.example.some-label"] == "some-value");
            Check(listing.Volumes[1].Labels!.Count == 0 && listing.Volumes[1].Driver == "" && listing.HasWarnings);
            Check(listing.Warnings!.Single() == "synthetic volume driver warning: incomplete listing");
            var detail = (await Reader(owner).InspectVolumeAsync(volume.Name)).Value!;
            Check(detail.Name == volume.Name && detail.Labels!["private.synthetic"] == "retain-private" && detail.RawJson == inspect);
            Check(urls.SequenceEqual(new[] { $"/v1.{minor}/volumes", $"/v1.{minor}/volumes/{Uri.EscapeDataString(volume.Name)}" }));
            Check(transport.Handler.Paths.Count == 5 && !transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
        }
    }

    internal static async Task ProjectionAndWarnings()
    {
        const string raw = "  {\r\n \"Name\":\"Complete/插件 Volume?%#🙂\",\"Driver\":\"custom\",\"Scope\":\"global\",\"Mountpoint\":\"C:\\\\private\\\\volume\",\"Labels\":{\"private\":\"retain-secret\",\"empty\":\"\"},\"Status\":{\"credential\":\"retain-sensitive\"},\"Options\":{\"token\":\"retain-private\"},\"Future\":{\"number\":9007199254740993,\"lexeme\":1.2300e2} }\n";
        var transport = Transport((_, _, _) => Task.FromResult(Response(raw)));
        await using var owner = await Connect(transport);
        var detail = (await Reader(owner).InspectVolumeAsync("alias")).Value!;
        Check(detail.Name == "Complete/插件 Volume?%#🙂" && detail.Driver == "custom" && detail.Scope == "global"
            && detail.Mountpoint == "C:\\private\\volume" && detail.Labels!["private"] == "retain-secret" && detail.RawJson == raw);
        Check(((IDictionary<string, string>)detail.Labels!).IsReadOnly);
        try { ((IDictionary<string, string>)detail.Labels!).Add("mutation", "forbidden"); throw new Exception("Mutable labels"); }
        catch (NotSupportedException) { }
        foreach (var suffix in new[] { "", ",\"Driver\":null,\"Scope\":null,\"Mountpoint\":null,\"Labels\":null", ",\"Driver\":\"\",\"Scope\":\"\",\"Mountpoint\":\"\",\"Labels\":{}" })
        {
            var sparse = Transport((_, _, _) => Task.FromResult(Response("{\"Name\":\"x\"" + suffix + "}")));
            await using var connection = await Connect(sparse);
            var value = (await Reader(connection).InspectVolumeAsync("x")).Value!;
            Check(value.Driver == (suffix.Contains("{}") ? "" : null) && value.Scope == value.Driver && value.Mountpoint == value.Driver);
            Check(suffix.Contains("{}") ? value.Labels!.Count == 0 : value.Labels is null);
        }
        foreach (var volumes in new[] { "", "\"Volumes\":null,", "\"Volumes\":[],", "\"Volumes\":[{\"Name\":\"x\"}]," })
        foreach (var warnings in new[] { "", "\"Warnings\":null,", "\"Warnings\":[],", "\"Warnings\":[\"private driver warning\",\"\",\"private driver warning\"]," })
        {
            var body = "{" + volumes + warnings + "\"Future\":true}";
            var sample = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var connection = await Connect(sample);
            var listing = (await Reader(connection).ListVolumesAsync()).Value!;
            Check((listing.Volumes is null) == (volumes == "" || volumes.Contains("null")));
            Check((listing.Warnings is null) == (warnings == "" || warnings.Contains("null")));
            Check(listing.HasWarnings == warnings.Contains("private"));
            if (listing.Warnings is { } items)
            {
                Check(((IList<string>)items).IsReadOnly);
                if (listing.HasWarnings) Check(items.SequenceEqual(new[] { "private driver warning", "", "private driver warning" }));
            }
            if (listing.Volumes is { } rows) Check(((IList<VolumeSummary>)rows).IsReadOnly);
        }
    }

    internal static async Task ErrorsAndNames()
    {
        var names = new[] { "volume", new string('a', 64), "SHA256:not-an-image-reference", "A+B_C.-", "driver/name", "a/b?c#d% space ü🙂",
            " leading and trailing ", "a\\b", "a/../b", "/name", "name/", "name%2Fother", "name:tag", new string('x', 4096) };
        var transport = Transport((_, _, _) => Task.FromResult(Response("{\"Name\":\"authoritative-complete-name\"}")), 40);
        await using var owner = await Connect(transport);
        foreach (var name in names) Check((await Reader(owner).InspectVolumeAsync(name)).Value!.Name == "authoritative-complete-name");
        Check(transport.Handler.Paths.Skip(3).SequenceEqual(names.Select(name => $"/v1.40/volumes/{Uri.EscapeDataString(name)}")));
        foreach (var name in new[] { null, "", " \t ", ".", "..", "x\n", "x\0", "x\u0085", "\uD800", "x\uDC00", new string('x', 4097) })
            Check((await Reader(owner).InspectVolumeAsync(name!)).Failure?.Kind == EngineResourceFailureKind.InvalidRequest);
        Check(transport.Handler.Paths.Count == names.Length + 3 && !transport.Handler.Disposed);
        foreach (var inspect in new[] { false, true })
        foreach (var status in new[] { 304, 400, 401, 403, 404, 409, 422, 500, 503 })
        {
            var failed = Transport((_, _, _) => Task.FromResult(Response("{\"message\":\"private original daemon message\"}", status)));
            await using var connection = await Connect(failed);
            var failure = inspect ? await InspectFailureAsync(connection) : await ListFailureAsync(connection);
            var kind = status switch { 404 => EngineResourceFailureKind.NotFound, 304 or 409 => EngineResourceFailureKind.Conflict,
                401 or 403 => EngineResourceFailureKind.AccessDenied, < 500 => EngineResourceFailureKind.InvalidRequest, _ => EngineResourceFailureKind.DaemonFailure };
            Check(failure?.Kind == kind && failure.HttpStatusCode == status && failure.DaemonMessage == "private original daemon message"
                && !failure.Description.Contains("private") && failed.Handler.Contents.All(c => c.Disposed));
        }
        foreach (var inspect in new[] { false, true })
        {
            var denied = Transport((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private", new UnauthorizedAccessException())));
            await using var connection = await Connect(denied);
            Check((inspect ? await InspectFailureAsync(connection) : await ListFailureAsync(connection))?.Kind == EngineResourceFailureKind.AccessDenied);
        }
        var plain = Transport((_, _, _) => Task.FromResult(Response("private plain message", 500)));
        await using var plainOwner = await Connect(plain);
        Check((await ListFailureAsync(plainOwner))?.DaemonMessage == "private plain message");
    }

    internal static async Task MalformedAndBounds()
    {
        var invalidVolumes = new[] { "{}", "null", "[]", "{\"Name\":null}", "{\"Name\":2}", "{\"Name\":\" \"}", "{\"Name\":\"..\"}",
            "{\"Name\":\"\\uD800\"}", "{\"Name\":\"x\\u0000\"}", "{\"Name\":\"x\",\"Name\":\"y\"}",
            "{\"Name\":\"x\",\"Driver\":1}", "{\"Name\":\"x\",\"Scope\":[]}", "{\"Name\":\"x\",\"Mountpoint\":{}}",
            "{\"Name\":\"x\",\"Labels\":[]}", "{\"Name\":\"x\",\"Labels\":{\"a\":1}}", "{\"Name\":\"x\",\"Labels\":{\"a\":null}}",
            "{\"Name\":\"x\",\"Labels\":{\"\\uD800\":\"x\"}}", "{\"Name\":\"x\",\"Labels\":{\"a\":\"\\uD800\"}}", "{\"Name\":\"x\",\"Labels\":{\"a\":\"1\",\"a\":\"2\"}}" };
        foreach (var inspect in new[] { false, true })
        foreach (var invalid in invalidVolumes)
        {
            var body = inspect ? invalid : "{\"Volumes\":[" + invalid + "]}";
            var transport = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var owner = await Connect(transport);
            var failure = inspect ? await InspectFailureAsync(owner) : await ListFailureAsync(owner);
            Check(failure?.Kind == EngineResourceFailureKind.MalformedData && failure.HttpStatusCode == 200);
            Check(transport.Handler.Contents.All(c => c.Disposed) && !transport.Handler.Disposed);
        }
        foreach (var body in new[] { "", "{bad", "null", "[]", "{\"Volumes\":1}", "{\"Warnings\":1}", "{\"Warnings\":[null]}", "{\"Warnings\":[3]}",
            "{\"Warnings\":[\"\\uD800\"]}", "{\"Volumes\":[],\"Volumes\":null}", "{\"Warnings\":[],\"Warnings\":[]}" })
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var owner = await Connect(transport);
            Check((await ListFailureAsync(owner))?.Kind == EngineResourceFailureKind.MalformedData);
        }
        // Strict UTF-8 for raw Inspect, including an invalid byte hidden in an unknown field.
        var invalidUtf8 = "{\"Name\":\"x\",\"Future\":\""u8.ToArray().Concat(new byte[] { 0xff }).Concat("\"}"u8.ToArray()).ToArray();
        var badText = Transport((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(invalidUtf8) }));
        await using (var owner = await Connect(badText)) Check((await InspectFailureAsync(owner))?.Kind == EngineResourceFailureKind.MalformedData);
        foreach (var known in new[] { true, false })
        foreach (var status in new[] { 200, 404, 500 })
        foreach (var inspect in new[] { false, true })
        {
            HttpContent content = known ? new TrackedContent(new string('x', 1025)) : new UnknownLengthContent(new string('x', 1025));
            var transport = Transport((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
            await using var owner = await Connect(transport, new() { MaximumResponseBytes = 1024 });
            var failure = inspect ? await InspectFailureAsync(owner) : await ListFailureAsync(owner);
            Check(failure?.HttpStatusCode == status && failure.DaemonMessage is null);
            Check(failure!.Kind == (status == 200 ? EngineResourceFailureKind.MalformedData : status == 404 ? EngineResourceFailureKind.NotFound : EngineResourceFailureKind.DaemonFailure));
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
        foreach (var inspect in new[] { false, true })
        {
            var raw = inspect ? "{\"Name\":\"x\"}" : "{\"Volumes\":[],\"Warnings\":[]}";
            raw += new string(' ', 1024 - System.Text.Encoding.UTF8.GetByteCount(raw));
            var transport = Transport((_, _, _) => Task.FromResult(Response(raw)));
            await using var owner = await Connect(transport, new() { MaximumResponseBytes = 1024 });
            Check((inspect ? await InspectFailureAsync(owner) : await ListFailureAsync(owner)) is null);
        }
    }

    private static async Task ExpectCanceled(Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { } }

    internal static async Task CancellationAndDisposal()
    {
        using var already = new CancellationTokenSource(); already.Cancel();
        var ready = Transport((_, _, _) => Task.FromResult(Response("{\"Volumes\":[]}")));
        await using (var connection = await Connect(ready))
        {
            await ExpectCanceled(() => Reader(connection).ListVolumesAsync(already.Token));
            await ExpectCanceled(() => Reader(connection).InspectVolumeAsync("x", already.Token));
            await ExpectCanceled(() => Reader(connection).InspectVolumeAsync("", already.Token));
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
                return Response("{\"Volumes\":[]}");
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
            try { await Reader(connection).ListVolumesAsync(); throw new InvalidOperationException("Disposed call accepted"); }
            catch (ObjectDisposedException) { }
            try { await Reader(connection).InspectVolumeAsync("x"); throw new InvalidOperationException("Disposed inspect accepted"); }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task<EngineResourceFailure?> InspectFailureAsync(IEngineConnection connection, CancellationToken token = default)
        => (await Reader(connection).InspectVolumeAsync("x", token)).Failure;
    private static async Task<EngineResourceFailure?> ListFailureAsync(IEngineConnection connection, CancellationToken token = default)
        => (await Reader(connection).ListVolumesAsync(token)).Failure;

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
            var stream = new LateStream(inspect ? "{\"Name\":\"x\"}"u8.ToArray() : "{\"Volumes\":[]}"u8.ToArray(), started, canceled, release);
            var content = body ? (HttpContent)new StreamContent(stream) : new TrackedContent(inspect ? "{\"Name\":\"x\"}" : "{\"Volumes\":[]}");
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
        // Exercise the shared decoder's last publication fence deterministically, after valid volume JSON is parsed.
        foreach (var inspect in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            var transport = new ResourceContinuityTransport("decode");
            await using var connection = await Connect(transport);
            using var lifetime = new CancellationTokenSource();
            var owner = (DockerEngineConnection)connection;
            var generation = owner.Continuity!.ConnectionGeneration;
            void EndContinuity()
            { if (cancel) lifetime.Cancel(); else transport.Counter.Opened(); }
            EngineResourceFailure? failure;
            if (inspect)
            {
                var result = await DockerResourceReader.ReadAsync(owner.Http, connection.Metadata.Api.Negotiated,
                    "volumes/x", bytes =>
                    {
                        var volume = DockerVolumeJson.Inspect(bytes);
                        EndContinuity();
                        return volume;
                    }, new(), owner.Continuity, generation, lifetime.Token, default, "volume");
                failure = result.Failure;
            }
            else
            {
                var result = await DockerResourceReader.ReadAsync(owner.Http, connection.Metadata.Api.Negotiated,
                    "volumes", bytes =>
                    {
                        var volumes = DockerVolumeJson.List(bytes);
                        EndContinuity();
                        return volumes;
                    }, new(), owner.Continuity, generation, lifetime.Token, default, "volume");
                failure = result.Failure;
            }
            Check(failure?.Kind == EngineResourceFailureKind.TransportLoss && !transport.Handler.Disposed);
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
            Handler = new TrackingHandler((request, n, _) =>
            {
                if (n < 3) return Task.FromResult(Response(Handshake(n)));
                if (phase == "headers") Counter.Opened();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = phase == "body" ? new StreamContent(new ReopenStream(Counter))
                    : new TrackedContent(request.RequestUri!.AbsolutePath.EndsWith("/x", StringComparison.Ordinal) ? "{\"Name\":\"x\"}" : "{\"Volumes\":[]}") });
            });
            return new ContinuityHandler(Handler, Counter);
        }
    }
    private sealed class ReopenStream(PipeConnectionCounter counter) : MemoryStream("{\"Volumes\":[]}"u8.ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { counter.Opened(); return base.ReadAsync(buffer, cancellationToken); }
    }
}
