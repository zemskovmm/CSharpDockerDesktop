using System.Net;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class NetworkChecks
{
    private static void Check(bool value, string message = "Network assertion failed")
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
        Check(attempt.IsConnected && attempt.Connection is INetworkReader);
        return attempt.Connection!;
    }
    private static INetworkReader Reader(IEngineConnection connection) => (INetworkReader)connection;

    internal static async Task ContractFixtures()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Networks");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "schema-evidence.json")));
        Check(evidence.RootElement.GetArrayLength() == 17);
        for (int minor = 40; minor <= 56; minor++)
        {
            var schema = evidence.RootElement[minor - 40];
            Check(schema.GetProperty("api").GetString() == $"1.{minor}" && schema.GetProperty("sha256").GetString()!.Length == 64);
            Check(schema.GetProperty("source").GetString() == $"https://docs.docker.com/reference/api/engine/version/v1.{minor}.yaml");
            Check(schema.GetProperty("listDescription").GetString()!.Contains("not propagated in API versions 1.28 and up"));
            var definitions = schema.GetProperty("definitions");
            var fields = definitions.GetProperty("Network").GetProperty("properties");
            foreach (var name in new[] { "Id", "Name", "Driver", "Scope" }) Check(fields.GetProperty(name).GetProperty("type").GetString() == "string");
            Check(fields.GetProperty("Internal").GetProperty("type").GetString() == "boolean");
            Check(fields.TryGetProperty("EnableIPv4", out _) == (minor >= 47));
            Check(fields.TryGetProperty("ConfigOnly", out _) == (minor >= 41));
            Check(fields.TryGetProperty("Containers", out _) == (minor < 52));
            Check(definitions.TryGetProperty("IPAMConfig", out _) == (minor >= 41));
            Check(schema.GetProperty("listResponseSchema").GetProperty("items").GetProperty("$ref").GetString()
                == (minor < 52 ? "#/definitions/Network" : "#/definitions/NetworkSummary"));
            Check(schema.GetProperty("inspectResponseSchema").GetProperty("$ref").GetString()
                == (minor < 52 ? "#/definitions/Network" : "#/definitions/NetworkInspect"));
            var parameter = schema.GetProperty("inspectParameters")[0];
            Check(parameter.GetProperty("name").GetString() == "id" && parameter.GetProperty("required").GetBoolean());
            Check(schema.GetProperty("inspectParameters")[1].GetProperty("name").GetString() == "verbose");
            Check(schema.GetProperty("listResponses").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "200", "500" }));
            Check(schema.GetProperty("inspectResponses").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "200", "404", "500" }));
            var list = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.list.json"));
            var inspect = await File.ReadAllTextAsync(Path.Combine(folder, $"v1.{minor}.inspect.json"));
            var transport = Transport((request, n, _) =>
            {
                Check(request.Method == HttpMethod.Get);
                return Task.FromResult(Response(n == 0 ? list : inspect));
            }, minor);
            await using var owner = await Connect(transport);
            var rows = (await Reader(owner).ListNetworksAsync()).Value!;
            var network = rows.Single();
            Check(network.Name == $"synthetic-network-1.{minor}" && network.Driver == "custom" && network.Scope == "swarm" && network.Internal == false);
            var detail = (await Reader(owner).InspectNetworkAsync(network.Id)).Value!;
            Check(detail.Id == network.Id && detail.RawJson == inspect && detail.Labels!["private.synthetic"] == "retain-private");
            Check(detail.Ipam!.Driver == "custom-ipam" && detail.Ipam.Options!["secret"] == "retain-private");
            var ipam = detail.Ipam.Config!.Single();
            Check(ipam.Subnet == "172.19.0.0/16" && ipam.IPRange == "172.19.1.0/24" && ipam.Gateway == "172.19.0.1");
            Check(minor == 40 ? ipam.AuxiliaryAddresses is null : ipam.AuxiliaryAddresses!["router"] == "172.19.0.254");
            var attachment = detail.Containers!.Single();
            Check(attachment.Key.Length == 64 && attachment.Value.Name == "synthetic-container"
                && attachment.Value.EndpointId!.Length == 64 && attachment.Value.IPv4Address == "172.19.0.2/16" && attachment.Value.IPv6Address == "");
            Check(transport.Handler.Paths.Skip(3).SequenceEqual(new[] { $"/v1.{minor}/networks", $"/v1.{minor}/networks/{network.Id}" }));
            Check(!transport.Handler.Disposed && transport.Handler.Paths.Count == 5 && transport.Handler.Contents.All(c => c.Disposed));
            Check(((IList<NetworkSummary>)rows).IsReadOnly && ((IDictionary<string, NetworkAttachment>)detail.Containers!).IsReadOnly
                && ((IList<NetworkIpamConfiguration>)detail.Ipam.Config!).IsReadOnly && ((IDictionary<string, string>)detail.Labels!).IsReadOnly);
        }
    }

    internal static async Task ProjectionsAndRaw()
    {
        foreach (var suffix in new[] { "", ",\"Name\":null,\"Driver\":null,\"Scope\":null,\"Internal\":null,\"Labels\":null,\"IPAM\":null,\"Containers\":null",
            ",\"Name\":\"\",\"Driver\":\"\",\"Scope\":\"\",\"Internal\":false,\"Labels\":{},\"IPAM\":{},\"Containers\":{}" })
        {
            var body = "{\"Id\":\"x\"" + suffix + "}";
            var transport = Transport((_, _, _) => Task.FromResult(Response(body)));
            await using var owner = await Connect(transport);
            var detail = (await Reader(owner).InspectNetworkAsync("x")).Value!;
            var empty = suffix.Contains("{}");
            Check(detail.Name == (empty ? "" : null) && detail.Driver == detail.Name && detail.Scope == detail.Name
                && detail.Internal == (empty ? false : null));
            Check(empty ? detail.Labels!.Count == 0 && detail.Containers!.Count == 0 && detail.Ipam is not null
                : detail.Labels is null && detail.Containers is null && detail.Ipam is null);
            if (empty) Check(detail.Ipam!.Driver is null && detail.Ipam.Config is null && detail.Ipam.Options is null);
        }
        foreach (var config in new[] { "{}", "{\"Driver\":null,\"Config\":null,\"Options\":null}", "{\"Driver\":\"\",\"Config\":[],\"Options\":{}}",
            "{\"Config\":[{}, {\"Subnet\":\"\",\"IPRange\":\"\",\"Gateway\":\"\",\"AuxiliaryAddresses\":{}}]}" })
        {
            var sample = Transport((_, _, _) => Task.FromResult(Response("{\"Id\":\"x\",\"IPAM\":" + config + "}")));
            await using var owner = await Connect(sample);
            var ipam = (await Reader(owner).InspectNetworkAsync("x")).Value!.Ipam!;
            if (config.Contains("[]")) Check(ipam.Driver == "" && ipam.Config!.Count == 0 && ipam.Options!.Count == 0);
            else if (config.Contains("Subnet")) Check(ipam.Config![0].Subnet is null && ipam.Config[0].AuxiliaryAddresses is null
                && ipam.Config[1].Subnet == "" && ipam.Config[1].AuxiliaryAddresses!.Count == 0);
            else Check(ipam.Driver is null && ipam.Config is null && ipam.Options is null);
        }
        const string raw = "  {\r\n\"Id\":\"x\",\"Name\":\"private🙂\",\"Internal\":true,\"Scope\":\"future\",\"IPAM\":{\"Config\":[{\"AuxAddress\":\"legacy-driver-value\",\"Future\":{\"opaque\":true}}]},\"Containers\":{\"lb-driver-specific\":{},\"complete-id\":{\"EndpointID\":\"\",\"IPv4Address\":\"driver-specific-address\"}},\"Services\":{\"private\":{}},\"Future\":{\"large\":9007199254740993,\"lexeme\":1.2300e2}}\n";
        var exact = Transport((_, _, _) => Task.FromResult(Response(raw)));
        await using (var owner = await Connect(exact))
        {
            var detail = (await Reader(owner).InspectNetworkAsync("x")).Value!;
            Check(detail.RawJson == raw && detail.Internal == true && detail.Scope == "future");
            Check(detail.Containers!["lb-driver-specific"].Name is null && detail.Containers["complete-id"].EndpointId == ""
                && detail.Containers["complete-id"].IPv4Address == "driver-specific-address");
        }
        // List attachment/IPAM payloads are deliberately unselected, never promoted into inspection evidence.
        var listing = Transport((_, _, _) => Task.FromResult(Response("[{\"Id\":\"x\",\"Containers\":123,\"IPAM\":false,\"Future\":{}}]")));
        await using (var owner = await Connect(listing)) Check((await Reader(owner).ListNetworksAsync()).Value!.Single().Id == "x");
        var emptyList = Transport((_, _, _) => Task.FromResult(Response("[]")));
        await using (var owner = await Connect(emptyList)) Check((await Reader(owner).ListNetworksAsync()).Value!.Count == 0);
    }

    internal static async Task IdentitiesAndFailures()
    {
        var ids = new[] { new string('a', 64), "swarm25characteropaqueid123", "x", "a/b?c#d% space ü🙂", "a\\b", "a/../b", "name%2Fother", new string('x', 4096) };
        foreach (var id in ids)
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(JsonSerializer.Serialize(new { Id = id }))));
            await using var owner = await Connect(transport);
            Check((await Reader(owner).InspectNetworkAsync(id)).Value!.Id == id);
            Check(transport.Handler.Paths.Last() == "/v1.56/networks/" + Uri.EscapeDataString(id));
        }
        var invalid = Transport((_, _, _) => throw new Exception("Invalid ID reached transport"));
        await using (var owner = await Connect(invalid))
        {
            foreach (var id in new[] { null, "", " \t ", ".", "..", "x\0", "x\n", "x\u0085", "\uD800", "x\uDC00", new string('x', 4097) })
                Check((await Reader(owner).InspectNetworkAsync(id!)).Failure?.Kind == EngineResourceFailureKind.InvalidRequest);
            Check(invalid.Handler.Paths.Count == 3);
        }
        var mismatch = Transport((_, _, _) => Task.FromResult(Response("{\"Id\":\"different-complete-id\"}")));
        await using (var owner = await Connect(mismatch)) Check((await InspectFailureAsync(owner))?.Kind == EngineResourceFailureKind.MalformedData);
        foreach (var inspect in new[] { false, true })
        foreach (var status in new[] { 304, 400, 401, 403, 404, 409, 422, 500, 503 })
        {
            var failed = Transport((_, _, _) => Task.FromResult(Response("{\"message\":\"private original daemon message\"}", status)));
            await using var owner = await Connect(failed);
            var failure = inspect ? await InspectFailureAsync(owner) : await ListFailureAsync(owner);
            var kind = status switch { 404 => EngineResourceFailureKind.NotFound, 304 or 409 => EngineResourceFailureKind.Conflict,
                401 or 403 => EngineResourceFailureKind.AccessDenied, < 500 => EngineResourceFailureKind.InvalidRequest, _ => EngineResourceFailureKind.DaemonFailure };
            Check(failure?.Kind == kind && failure.HttpStatusCode == status && failure.DaemonMessage == "private original daemon message"
                && !failure.Description.Contains("private") && failed.Handler.Contents.All(c => c.Disposed));
        }
        foreach (var inspect in new[] { false, true })
        {
            var denied = Transport((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private", new UnauthorizedAccessException())));
            await using var owner = await Connect(denied);
            Check((inspect ? await InspectFailureAsync(owner) : await ListFailureAsync(owner))?.Kind == EngineResourceFailureKind.AccessDenied);
            var plain = Transport((_, _, _) => Task.FromResult(Response("private plain message", 500)));
            await using var plainOwner = await Connect(plain);
            Check((inspect ? await InspectFailureAsync(plainOwner) : await ListFailureAsync(plainOwner))?.DaemonMessage == "private plain message");
        }
    }

    internal static async Task MalformedAndBounds()
    {
        var invalid = new[] { "{}", "null", "[]", "{\"Id\":null}", "{\"Id\":2}", "{\"Id\":\" \"}", "{\"Id\":\"..\"}", "{\"Id\":\"\\uD800\"}",
            "{\"Id\":\"x\\u0000\"}", "{\"Id\":\"x\",\"Id\":\"y\"}", "{\"Id\":\"x\",\"Id\":null}", "{\"Id\":\"x\",\"Name\":2}",
            "{\"Id\":\"x\",\"Driver\":[]}", "{\"Id\":\"x\",\"Scope\":{}}", "{\"Id\":\"x\",\"Internal\":0}", "{\"Id\":\"x\",\"Internal\":\"false\"}",
            "{\"Id\":\"x\",\"Labels\":[]}", "{\"Id\":\"x\",\"Labels\":{\"a\":null}}", "{\"Id\":\"x\",\"Labels\":{\"a\":1}}",
            "{\"Id\":\"x\",\"Labels\":{\"\\uD800\":\"x\"}}", "{\"Id\":\"x\",\"Labels\":{\"a\":\"\\uD800\"}}", "{\"Id\":\"x\",\"Labels\":{\"a\":\"1\",\"a\":\"2\"}}" };
        foreach (var inspect in new[] { false, true })
        foreach (var sample in invalid)
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(inspect ? sample : "[" + sample + "]")));
            await using var owner = await Connect(transport);
            Check((inspect ? await InspectFailureAsync(owner) : await ListFailureAsync(owner))?.Kind == EngineResourceFailureKind.MalformedData, sample);
        }
        var invalidDetails = new[] { "\"IPAM\":1", "\"IPAM\":{\"Driver\":1}", "\"IPAM\":{\"Config\":{}}", "\"IPAM\":{\"Config\":[null]}",
            "\"IPAM\":{\"Config\":[{\"Subnet\":1}]}", "\"IPAM\":{\"Config\":[{\"Gateway\":false}]}", "\"IPAM\":{\"Config\":[{\"IPRange\":1}]}",
            "\"IPAM\":{\"Config\":[{\"AuxiliaryAddresses\":[]}]}", "\"IPAM\":{\"Config\":[{\"AuxiliaryAddresses\":{\"a\":null}}]}",
            "\"IPAM\":{\"Options\":{\"a\":null}}", "\"IPAM\":{},\"IPAM\":{}", "\"Containers\":[]", "\"Containers\":{\"x\":null}",
            "\"Containers\":{\"\":{}}", "\"Containers\":{\"..\":{}}", "\"Containers\":{\"\\uD800\":{}}", "\"Containers\":{\"x\":{},\"x\":{}}",
            "\"Containers\":{\"x\":{\"EndpointID\":1}}", "\"Containers\":{\"x\":{\"IPv4Address\":[]}}", "\"Containers\":{\"x\":{\"IPv6Address\":false}}",
            "\"Containers\":{\"x\":{\"MacAddress\":1}}", "\"Containers\":{\"x\":{\"Name\":1}}" };
        foreach (var sample in invalidDetails)
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response("{\"Id\":\"x\"," + sample + "}")));
            await using var owner = await Connect(transport);
            Check((await InspectFailureAsync(owner))?.Kind == EngineResourceFailureKind.MalformedData, sample);
        }
        foreach (var sample in new[] { "", "{bad", "null", "{}", "[null]" })
        {
            var transport = Transport((_, _, _) => Task.FromResult(Response(sample)));
            await using var owner = await Connect(transport);
            Check((await ListFailureAsync(owner))?.Kind == EngineResourceFailureKind.MalformedData);
        }
        var invalidUtf8 = "{\"Id\":\"x\",\"Future\":\""u8.ToArray().Concat(new byte[] { 0xff }).Concat("\"}"u8.ToArray()).ToArray();
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
            var raw = inspect ? "{\"Id\":\"x\"}" : "[]";
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
        var ready = Transport((_, _, _) => Task.FromResult(Response("[]")));
        await using (var connection = await Connect(ready))
        {
            await ExpectCanceled(() => Reader(connection).ListNetworksAsync(already.Token));
            await ExpectCanceled(() => Reader(connection).InspectNetworkAsync("x", already.Token));
            await ExpectCanceled(() => Reader(connection).InspectNetworkAsync("", already.Token));
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
            try { await Reader(connection).ListNetworksAsync(); throw new InvalidOperationException("Disposed call accepted"); }
            catch (ObjectDisposedException) { }
            try { await Reader(connection).InspectNetworkAsync("x"); throw new InvalidOperationException("Disposed inspect accepted"); }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task<EngineResourceFailure?> InspectFailureAsync(IEngineConnection connection, CancellationToken token = default)
        => (await Reader(connection).InspectNetworkAsync("x", token)).Failure;
    private static async Task<EngineResourceFailure?> ListFailureAsync(IEngineConnection connection, CancellationToken token = default)
        => (await Reader(connection).ListNetworksAsync(token)).Failure;

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
        // Exercise the shared decoder's last publication fence deterministically, after valid network JSON is parsed.
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
                    "networks/x", bytes =>
                    {
                        var network = DockerNetworkJson.Inspect(bytes);
                        EndContinuity();
                        return network;
                    }, new(), owner.Continuity, generation, lifetime.Token, default, "network");
                failure = result.Failure;
            }
            else
            {
                var result = await DockerResourceReader.ReadAsync(owner.Http, connection.Metadata.Api.Negotiated,
                    "networks", bytes =>
                    {
                        var networks = DockerNetworkJson.List(bytes);
                        EndContinuity();
                        return networks;
                    }, new(), owner.Continuity, generation, lifetime.Token, default, "network");
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
                    : new TrackedContent(request.RequestUri!.AbsolutePath.EndsWith("/x", StringComparison.Ordinal) ? "{\"Id\":\"x\"}" : "[]") });
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
