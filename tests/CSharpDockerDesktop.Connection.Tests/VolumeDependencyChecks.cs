using System.Net;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class VolumeDependencyChecks
{
    private static void Check(bool value, string message = "Volume dependency assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body, int status = 200) => new((HttpStatusCode)status) { Content = new TrackedContent(body) };
    private static string Handshake(int n, int minor) => n switch
    { 0 => "OK", 1 => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""", _ => "{}" };
    private static FakeTransport Transport(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send, int minor = 56)
        => new((r, n, t) => n < 3 ? Task.FromResult(Response(Handshake(n, minor))) : send(r, n - 3, t));
    private static async Task<IEngineConnection> Connect(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected && attempt.Connection is IVolumeDependencyReader);
        return attempt.Connection!;
    }
    private static Task<EngineResourceResult<VolumeDependencies>> Query(IEngineConnection c, string name = "data", CancellationToken token = default)
        => ((IVolumeDependencyReader)c).QueryVolumeDependenciesAsync(name, token);
    private static string Inspect(string id, string mounts = "[]", string? state = null)
        => JsonSerializer.Serialize(new { Id = id, Name = "/" + id, State = new { Status = state, Running = state == "running" } })[..^1] + ",\"Mounts\":" + mounts + "}";
    private static string Mount(string name, string type = "volume") => JsonSerializer.Serialize(new[] { new { Type = type, Name = name, Source = name, Destination = name } });
    private static async Task Canceled(Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected caller cancellation"); } catch (OperationCanceledException) { } }

    internal static async Task ContractsAndExactMatching()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "VolumeDependencies");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "contract-evidence.json")));
        Check(evidence.RootElement.GetArrayLength() == 17);
        for (var minor = 40; minor <= 56; minor++)
        {
            var entry = evidence.RootElement[minor - 40];
            Check(entry.GetProperty("api").GetString() == $"1.{minor}" && entry.GetProperty("sha256").GetString()!.Length == 64);
            Check(entry.GetProperty("source").GetString() == $"https://docs.docker.com/reference/api/engine/version/v1.{minor}.yaml");
            Check(entry.GetProperty("allParameter").GetProperty("type").GetString() == "boolean"
                && entry.GetProperty("filtersParameter").GetProperty("type").GetString() == "string"
                && entry.GetProperty("filtersParameter").GetProperty("description").GetString()!.Contains("volume name"));
            Check(entry.GetProperty("mountFieldTypes").GetProperty("Name").GetString() == "string"
                && entry.GetProperty("mountFieldTypes").GetProperty("Source").GetString() == "string");
            Check(entry.GetProperty("listResponses").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "200", "400", "500" })
                && entry.GetProperty("inspectResponses").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "200", "404", "500" }));
            var idParameter = entry.GetProperty("inspectParameters")[0];
            Check(idParameter.GetProperty("name").GetString() == "id" && idParameter.GetProperty("in").GetString() == "path"
                && idParameter.GetProperty("required").GetBoolean() && idParameter.GetProperty("type").GetString() == "string");
            var first = new string('a', 64); var second = new string('b', 64);
            var bodies = new[] { JsonSerializer.Serialize(new[] { new { Id = first, Names = new[] { "/original-running", "/alias" }, State = "running" },
                new { Id = second, Names = new[] { "/original-stopped" }, State = "exited" }, new { Id = "unrelated", Names = Array.Empty<string>(), State = "created" },
                new { Id = "bind", Names = Array.Empty<string>(), State = "running" } }),
                Inspect(first, Mount("data"), "running"), Inspect(second, Mount("data"), "exited"), Inspect("unrelated", Mount("data-other")), Inspect("bind", Mount("data", "bind")) };
            var urls = new List<string>();
            var transport = Transport((r, n, _) => { Check(r.Method == HttpMethod.Get); urls.Add(r.RequestUri!.PathAndQuery); return Task.FromResult(Response(bodies[n])); }, minor);
            await using var owner = await Connect(transport);
            var result = (await Query(owner)).Value!;
            Check(result.VolumeName == "data" && result.Completeness == VolumeDependencyCompleteness.Complete && result.Containers!.Count == 2);
            Check(result.Containers![0].Id == first && result.Containers![0].Names!.SequenceEqual(new[] { "/original-running", "/alias" })
                && result.Containers![0].Name == "/" + first && result.Containers![0].State == "running" && result.Containers![0].Running == true);
            Check(result.Containers![1].Id == second && result.Containers![1].State == "exited" && result.Containers![1].Running == false);
            var filters = urls[0].Split("filters=")[1];
            using var parsed = JsonDocument.Parse(Uri.UnescapeDataString(filters));
            Check(parsed.RootElement.GetProperty("volume")[0].GetString() == "data" && urls[0].StartsWith($"/v1.{minor}/containers/json?all=true&filters="));
            Check(urls.Skip(1).SequenceEqual(new[] { first, second, "unrelated", "bind" }.Select(id => $"/v1.{minor}/containers/{id}/json")));
            Check(!transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
        }
        foreach (var type in new[] { "bind", "tmpfs", "npipe" })
        {
            var t = Transport((_, n, _) => Task.FromResult(Response(n == 0 ? "[{\"Id\":\"full\"}]" : Inspect("full", Mount("data", type)))));
            await using var c = await Connect(t); Check((await Query(c)).Value!.Containers!.Count == 0);
        }
        // Result constructors defensively copy caller-owned collections, and accessors expose read-only wrappers.
        var names = new List<string> { "/private" };
        var dependency = new VolumeContainerDependency("full", names, null, null, null); names.Clear();
        var list = new List<VolumeContainerDependency> { dependency }; var unverified = new List<string> { "unknown" };
        var snapshot = new VolumeDependencies("data", list, unverified); list.Clear(); unverified.Clear();
        Check(snapshot.Containers!.Count == 1 && snapshot.Containers[0].Names!.Count == 1 && snapshot.UnverifiedContainerIds.Count == 1
            && ((IList<VolumeContainerDependency>)snapshot.Containers).IsReadOnly && ((IList<string>)snapshot.Containers[0].Names!).IsReadOnly);
    }

    internal static async Task NamesAndEvidenceStates()
    {
        foreach (var name in new[] { "driver/name", "a/b?c#d% space ü🙂", " leading and trailing ", "a\\b", "quote\"and&equals=", "name%2Fother", "name:tag", new string('x', 4096) })
        {
            var t = Transport((r, n, _) =>
            {
                if (n == 0)
                {
                    using var filters = JsonDocument.Parse(Uri.UnescapeDataString(r.RequestUri!.Query.Split("filters=")[1]));
                    Check(filters.RootElement.GetProperty("volume")[0].GetString() == name);
                    return Task.FromResult(Response("[{\"Id\":\"complete/id?%\"}]"));
                }
                Check(r.RequestUri!.PathAndQuery == "/v1.56/containers/complete%2Fid%3F%25/json");
                return Task.FromResult(Response(Inspect("complete/id?%", Mount(name))));
            });
            await using var c = await Connect(t); Check((await Query(c, name)).Value!.Containers!.Count == 1);
        }
        var valid = Transport((_, _, _) => Task.FromResult(Response("[]")));
        await using (var c = await Connect(valid))
        {
            foreach (var name in new[] { null, "", " ", ".", "..", "x\n", "x\0", "x\u0085", "\uD800", "x\uDC00", new string('x', 4097) })
                Check((await Query(c, name!)).Failure?.Kind == EngineResourceFailureKind.InvalidRequest);
            Check(valid.Handler.Paths.Count == 3);
        }
        foreach (var body in new[] { "[]", "null" })
        {
            var t = Transport((_, _, _) => Task.FromResult(Response(body))); await using var c = await Connect(t);
            var result = (await Query(c)).Value!;
            Check(body == "[]" ? result.Completeness == VolumeDependencyCompleteness.Complete && result.Containers!.Count == 0
                : result.Completeness == VolumeDependencyCompleteness.Unavailable && result.Containers is null);
        }
        foreach (var mounts in new[] { "null", "[{\"Name\":\"data\"}]", "[{\"Type\":\"volume\"}]", "[{\"Type\":\"future\",\"Name\":\"data\"}]", "[{\"Type\":\"cluster\",\"Name\":\"data\"}]" })
        {
            var t = Transport((_, n, _) => Task.FromResult(Response(n == 0 ? "[{\"Id\":\"known\"},{\"Id\":\"unknown\"}]"
                : n == 1 ? Inspect("known", Mount("data")) : Inspect("unknown", mounts))));
            await using var c = await Connect(t); var result = (await Query(c)).Value!;
            Check(result.Completeness == VolumeDependencyCompleteness.Incomplete && result.Containers!.Single().Id == "known"
                && result.UnverifiedContainerIds.Single() == "unknown");
        }
        foreach (var name in new[] { "Data", "data-long", "prefix-data", "/var/lib/docker/volumes/data/_data", "data%2Fother" })
        {
            var t = Transport((_, n, _) => Task.FromResult(Response(n == 0 ? "[{\"Id\":\"c\"}]" : Inspect("c", Mount(name)))));
            await using var c = await Connect(t); Check((await Query(c)).Value!.Containers!.Count == 0);
        }
    }

    internal static async Task FailuresAndBounds()
    {
        foreach (var inspect in new[] { false, true })
        foreach (var status in new[] { 304, 400, 401, 403, 404, 409, 422, 500, 503 })
        {
            var t = Transport((_, n, _) => Task.FromResult(inspect && n == 0 ? Response("[{\"Id\":\"full\"}]") : Response("{\"message\":\"private daemon volume/container credential message\"}", status)));
            await using var c = await Connect(t); var failure = (await Query(c)).Failure!;
            var kind = status switch { 404 => EngineResourceFailureKind.NotFound, 304 or 409 => EngineResourceFailureKind.Conflict,
                401 or 403 => EngineResourceFailureKind.AccessDenied, < 500 => EngineResourceFailureKind.InvalidRequest, _ => EngineResourceFailureKind.DaemonFailure };
            Check(failure.Kind == kind && failure.HttpStatusCode == status && failure.DaemonMessage == "private daemon volume/container credential message"
                && !failure.Description.Contains("private") && t.Handler.Contents.All(c => c.Disposed));
        }
        var malformedLists = new[] { "", "{bad", "{}", "1", "[null]", "[{}]", "[{\"Id\":\"x\",\"Id\":\"y\"}]", "[{\"Id\":\"x\"},{\"Id\":\"x\"}]",
            "[{\"Id\":\"..\"}]", "[{\"Id\":\"x\",\"Names\":[null]}]", "[{\"Id\":\"x\",\"State\":2}]", "[{\"Id\":\"\\uD800\"}]" };
        foreach (var body in malformedLists)
        {
            var t = Transport((_, _, _) => Task.FromResult(Response(body))); await using var c = await Connect(t);
            Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.MalformedData);
        }
        var malformedInspects = new[] { "null", "[]", "{}", "{\"Id\":\"other\",\"Mounts\":[]}", "{\"Id\":\"x\",\"Mounts\":{}}", "{\"Id\":\"x\",\"Mounts\":[null]}",
            "{\"Id\":\"x\",\"Mounts\":[{\"Type\":1}]}", "{\"Id\":\"x\",\"Mounts\":[{\"Type\":\"volume\",\"Name\":\"\"}]}",
            "{\"Id\":\"x\",\"Mounts\":[],\"Mounts\":[]}", "{\"Id\":\"x\",\"State\":{\"Running\":2}}", "{\"Id\":\"x\",\"Name\":\"\\uD800\"}" };
        foreach (var body in malformedInspects)
        {
            var t = Transport((_, n, _) => Task.FromResult(Response(n == 0 ? "[{\"Id\":\"x\"}]" : body))); await using var c = await Connect(t);
            Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.MalformedData);
        }
        foreach (var inspect in new[] { false, true })
        foreach (var known in new[] { false, true })
        foreach (var status in new[] { 200, 404, 500 })
        {
            HttpContent content = known ? new TrackedContent(new string('x', 1025)) : new UnknownLengthContent(new string('x', 1025));
            var t = Transport((_, n, _) => Task.FromResult(inspect && n == 0 ? Response("[{\"Id\":\"x\"}]") : new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
            await using var c = await Connect(t, new() { MaximumResponseBytes = 1024 }); var failure = (await Query(c)).Failure!;
            Check(failure.Kind == (status == 200 ? EngineResourceFailureKind.MalformedData : status == 404 ? EngineResourceFailureKind.NotFound : EngineResourceFailureKind.DaemonFailure)
                && failure.HttpStatusCode == status && failure.DaemonMessage is null);
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
        var tooMany = Transport((_, _, _) => Task.FromResult(Response("[{\"Id\":\"a\"},{\"Id\":\"b\"}]")));
        await using (var c = await Connect(tooMany, new() { MaximumVolumeDependencyContainers = 1 }))
            Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.MalformedData && tooMany.Handler.Paths.Count == 4);
        var denied = Transport((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private", new UnauthorizedAccessException())));
        await using (var c = await Connect(denied)) Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.AccessDenied);
        var plain = Transport((_, _, _) => Task.FromResult(Response("private plain daemon message", 500)));
        await using (var c = await Connect(plain)) Check((await Query(c)).Failure?.DaemonMessage == "private plain daemon message");
        foreach (var inspect in new[] { false, true })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(inspect ? "{\"Id\":\"x\",\"Future\":\"" : "[{\"Id\":\"x\",\"Future\":\"")
                .Concat(new byte[] { 0xff }).Concat(System.Text.Encoding.UTF8.GetBytes(inspect ? "\"}" : "\"}]")).ToArray();
            var t = Transport((_, n, _) => Task.FromResult(inspect && n == 0 ? Response("[{\"Id\":\"x\"}]")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            await using var c = await Connect(t); Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.MalformedData);
        }
        var exact = Transport((_, _, _) => Task.FromResult(Response("[]" + new string(' ', 1022))));
        await using (var c = await Connect(exact, new() { MaximumResponseBytes = 1024 }))
            Check((await Query(c)).Value!.Completeness == VolumeDependencyCompleteness.Complete);
        // Individually bounded valid inspections may still exceed the whole result's retained metadata budget.
        var metadata = Transport((_, n, _) => Task.FromResult(Response(n == 0 ? "[{\"Id\":\"a\"},{\"Id\":\"b\"},{\"Id\":\"c\"}]"
            : Inspect(n == 1 ? "a" : n == 2 ? "b" : "c", Mount("data"), new string('s', 400)))));
        await using (var c = await Connect(metadata, new() { MaximumResponseBytes = 1024 }))
            Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.MalformedData);
        foreach (var options in new[] { new DockerConnectionOptions { MaximumConcurrentVolumeDependencyQueries = 0 },
            new DockerConnectionOptions { MaximumConcurrentVolumeDependencyQueries = 65 }, new DockerConnectionOptions { MaximumVolumeDependencyContainers = 0 },
            new DockerConnectionOptions { MaximumVolumeDependencyContainers = 65537 } })
        {
            try { _ = new DockerEngineConnectionClient(Transport((_, _, _) => Task.FromResult(Response("[]"))), options); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("Expected invalid dependency budget rejection");
        }
    }

    internal static async Task CancellationAdmissionAndLateFences()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var ready = Transport((_, _, _) => Task.FromResult(Response("[]")));
        await using (var c = await Connect(ready))
        {
            await Canceled(() => Query(c, token: canceled.Token));
            await Canceled(() => Query(c, "", canceled.Token));
            Check(ready.Handler.Paths.Count == 3);
        }
        foreach (var inspect in new[] { false, true })
        foreach (var body in new[] { false, true })
        foreach (var mode in new[] { "caller", "dispose", "deadline", "wrapped" })
        {
            using var caller = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new StalledStream();
            var t = Transport(async (_, n, token) =>
            {
                if (inspect && n == 0) return Response("[{\"Id\":\"full\"}]");
                if (body) return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (mode == "wrapped") { throw new HttpRequestException("wrapped private cancellation"); }
                return Response("[]");
            });
            var c = await Connect(t, new() { RequestTimeout = TimeSpan.FromMilliseconds(120) });
            var pending = Query(c, token: caller.Token);
            await (body ? stream.Started.Task : started.Task).WaitAsync(TimeSpan.FromSeconds(2));
            if (mode is "caller" or "wrapped") caller.Cancel();
            if (mode == "dispose") await c.DisposeAsync();
            if (mode is "caller" or "wrapped") await Canceled(() => pending);
            else Check((await pending).Failure?.Kind == EngineResourceFailureKind.TransportLoss);
            if (body) Check(stream.Disposed);
            Check(t.Handler.Disposed == (mode == "dispose"));
            await c.DisposeAsync();
            try { await Query(c); throw new InvalidOperationException("Expected disposed owner"); } catch (ObjectDisposedException) { }
            Check(t.Handler.DisposeCount == 1);
        }
        // Non-cooperative response headers/bodies must never publish their late valid response.
        foreach (var inspect in new[] { false, true })
        foreach (var body in new[] { false, true })
        foreach (var mode in new[] { "caller", "dispose", "deadline", "reopen" })
        {
            using var caller = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceledRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var content = body ? (HttpContent)new LateStreamContent(inspect ? Inspect("full", Mount("data")) : "[]", started, canceledRequest, release)
                : new TrackedContent(inspect ? Inspect("full", Mount("data")) : "[]");
            var t = new DependencyContinuityTransport(async (_, n, token) =>
            {
                if (inspect && n == 0) return Response("[{\"Id\":\"full\"}]");
                if (!body)
                {
                    using var registration = token.Register(() => canceledRequest.TrySetResult());
                    started.TrySetResult(); await release.Task;
                }
                return new(HttpStatusCode.OK) { Content = content };
            });
            await using var c = await Connect(t, new() { RequestTimeout = TimeSpan.FromMilliseconds(160) });
            var pending = Query(c, token: caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (mode == "caller") caller.Cancel();
            if (mode == "dispose") await c.DisposeAsync();
            if (mode == "reopen") t.Counter.Opened();
            else await canceledRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!pending.IsCompleted); release.TrySetResult();
            if (mode == "caller") await Canceled(() => pending);
            else Check((await pending).Failure?.Kind == EngineResourceFailureKind.TransportLoss);
            Check(body ? ((LateStreamContent)content).Disposed : ((TrackedContent)content).Disposed);
        }
        // Admission is shared by dependency calls alone. A queued cancellation does not dispatch or cancel the admitted query.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bounded = Transport(async (r, _, _) =>
        {
            if (r.RequestUri!.Query.Contains("filters=")) { entered.TrySetResult(); await finish.Task; return Response("[]"); }
            return Response(r.RequestUri.AbsolutePath == "/_ping" ? "OK" : "{\"Volumes\":[]}");
        });
        await using (var c = await Connect(bounded, new() { MaximumConcurrentVolumeDependencyQueries = 1 }))
        {
            var first = Query(c); await entered.Task;
            using var stop = new CancellationTokenSource(); var second = Query(c, token: stop.Token); stop.Cancel();
            await Canceled(() => second); Check(bounded.Handler.Paths.Count == 4);
            Check((await ((IVolumeReader)c).ListVolumesAsync()).IsSuccess && await c.CheckLivenessAsync() is null);
            finish.TrySetResult(); Check((await first).Value!.Completeness == VolumeDependencyCompleteness.Complete);
            Check(!bounded.Handler.Disposed);
        }
        // The whole query deadline cannot be reset by each successful candidate request.
        var delayed = Transport(async (_, n, token) =>
        {
            await Task.Delay(60, token);
            return Response(n == 0 ? "[{\"Id\":\"a\"},{\"Id\":\"b\"},{\"Id\":\"c\"}]" : Inspect(n == 1 ? "a" : n == 2 ? "b" : "c", Mount("data")));
        });
        await using (var c = await Connect(delayed, new() { RequestTimeout = TimeSpan.FromMilliseconds(150) }))
            Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.TransportLoss);
    }

    internal static async Task ContinuityAndResourceCoexistence()
    {
        foreach (var inspect in new[] { false, true })
        foreach (var phase in new[] { "before", "headers", "body" })
        {
            DependencyContinuityTransport t = null!;
            t = new((_, n, _) =>
            {
                if (inspect && n == 0) return Task.FromResult(Response("[{\"Id\":\"full\"}]"));
                if (phase == "headers") t.Counter.Opened();
                return Task.FromResult(phase == "body" ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StreamContent(new ReopenStream(t.Counter, inspect ? Inspect("full", Mount("data")) : "[]")) }
                    : Response(inspect ? Inspect("full", Mount("data")) : "[]"));
            });
            await using var c = await Connect(t);
            if (phase == "before") t.Counter.Opened();
            Check((await Query(c)).Failure?.Kind == EngineResourceFailureKind.TransportLoss && !t.Handler.Disposed);
            Check(t.Handler.Paths.Count == (phase == "before" ? 3 : inspect ? 5 : 4));
        }
        // Decoder's final publication fence, using the same shared resource primitive as every candidate read.
        foreach (var end in new[] { "caller", "lifetime", "generation" })
        {
            var t = new DependencyContinuityTransport((_, _, _) => Task.FromResult(Response("[]")));
            await using var c = await Connect(t); using var caller = new CancellationTokenSource(); using var ownerLifetime = new CancellationTokenSource();
            var owner = (DockerEngineConnection)c;
            var pending = DockerResourceReader.ReadAsync(owner.Http, c.Metadata.Api.Negotiated, "containers/json?all=true",
                bytes =>
                {
                    var value = DockerContainerJson.List(bytes);
                    if (end == "caller") caller.Cancel(); else if (end == "lifetime") ownerLifetime.Cancel(); else t.Counter.Opened();
                    return value;
                }, new(), owner.Continuity, owner.Continuity!.ConnectionGeneration, ownerLifetime.Token, caller.Token, "volume dependency");
            if (end == "caller") await Canceled(() => pending); else Check((await pending).Failure?.Kind == EngineResourceFailureKind.TransportLoss);
        }
        // Exercise real SocketsHttpHandler multiplexing with active Logs/events and all retained capabilities.
        await LogChecks.SimultaneousNamedPipe();
    }

    private sealed class DependencyContinuityTransport(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : IDockerTransportFactory
    {
        public PipeConnectionCounter Counter { get; } = new();
        public TrackingHandler Handler { get; private set; } = null!;
        public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
        {
            Counter.Opened();
            Handler = new((r, n, t) => n < 3 ? Task.FromResult(Response(Handshake(n, 56))) : send(r, n - 3, t));
            return new ContinuityHandler(Handler, Counter);
        }
    }
    private sealed class ReopenStream(PipeConnectionCounter counter, string body) : MemoryStream(System.Text.Encoding.UTF8.GetBytes(body))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { counter.Opened(); return base.ReadAsync(buffer, cancellationToken); }
    }
    private sealed class LateStreamContent(string body, TaskCompletionSource started, TaskCompletionSource canceled, TaskCompletionSource release) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new LateStream(body, started, canceled, release));
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
    private sealed class LateStream(string body, TaskCompletionSource started, TaskCompletionSource canceled, TaskCompletionSource release) : MemoryStream(System.Text.Encoding.UTF8.GetBytes(body))
    {
        private bool released;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!released)
            {
                using var registration = cancellationToken.Register(() => canceled.TrySetResult());
                started.TrySetResult(); await release.Task; released = true;
            }
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
    }
}
