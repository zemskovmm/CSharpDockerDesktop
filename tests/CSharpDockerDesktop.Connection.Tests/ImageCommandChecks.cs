using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class ImageCommandChecks
{
    private static void Check(bool value, string message = "Image command assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body = "[]", int status = 200)
        => new((HttpStatusCode)status) { Content = new TrackedContent(body) };
    private static string Handshake(int index, int minor = 56) => index switch
    { 0 => "OK", 1 => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""", _ => "{}" };
    private static FakeTransport Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> command, int minor = 56)
        => new((request, index, token) => index < 3 ? Task.FromResult(Response(Handshake(index, minor)))
            : request.Method == HttpMethod.Delete ? command(request, token)
            : Task.FromResult(Response(request.RequestUri!.AbsolutePath == "/_ping" ? "OK"
                : request.RequestUri.AbsolutePath.EndsWith("/json") && !request.RequestUri.AbsolutePath.Contains("/images/json")
                    && !request.RequestUri.AbsolutePath.Contains("/containers/json") ? "{\"Id\":\"target\"}" : "[]")));
    private static async Task<IEngineConnection> Connect(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected && attempt.Connection is IImageCommands);
        return attempt.Connection!;
    }
    private static async Task<ImageCommandResult> Canceled(Task<ImageCommandResult> pending)
    {
        try { await pending; throw new InvalidOperationException("Expected cancellation"); }
        catch (ImageCommandCanceledException ex) { Check(pending.IsCanceled); return ex.Outcome; }
    }

    internal static async Task OfficialContracts()
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "ImageCommands", "contract-evidence.json")));
        var versions = evidence.RootElement.GetProperty("versions").EnumerateArray().ToArray();
        Check(versions.Length == 17);
        foreach (var version in versions)
        {
            var api = version.GetProperty("api").GetString()!;
            int minor = int.Parse(api.Split('.')[1]);
            Check(version.GetProperty("sha256").GetString()!.Length == 64);
            Check(version.GetProperty("method").GetString() == "DELETE" && version.GetProperty("path").GetString() == "/images/{name}");
            var parameters = version.GetProperty("parameters").EnumerateArray().ToArray();
            foreach (var name in new[] { "force", "noprune" })
                Check(!parameters.Single(p => p.GetProperty("name").GetString() == name).GetProperty("default").GetBoolean());
            var schema = version.GetProperty("responses").GetProperty("200").GetProperty("schema");
            Check(schema.GetProperty("type").GetString() == "array");
            foreach (var name in new[] { "Untagged", "Deleted" })
                Check(version.GetProperty("removalRecord").GetProperty("properties").GetProperty(name).GetProperty("type").GetString() == "string");
            foreach (var response in version.GetProperty("responses").EnumerateObject())
            {
                int status = int.Parse(response.Name);
                string? path = null; var requests = new List<TrackedContent>();
                var transport = Transport((request, _) =>
                {
                    Check(request.Method == HttpMethod.Delete && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact && request.Version == new Version(1, 1));
                    path = request.RequestUri!.PathAndQuery;
                    var content = new TrackedContent(""); request.Content = content; requests.Add(content);
                    return Task.FromResult(Response(status == 200 ? "[{\"Untagged\":\"repo/tag:latest\"},{\"Deleted\":\"sha256:original\"},{\"Deleted\":\"sha256:parent\"}]"
                        : "{\"message\":\"original daemon dependency reason\"}", status));
                }, minor);
                await using var connection = await Connect(transport);
                var result = await ((IImageCommands)connection).DeleteImageAsync("repo/tag:latest");
                Check(path == $"/v{api}/images/repo%2Ftag%3Alatest?force=false&noprune=false");
                Check(result.HttpStatusCode == status && result.RequiresReconciliation);
                if (status == 200)
                {
                    Check(result.Disposition == ImageCommandDisposition.Acknowledged && result.Failure is null);
                    Check(result.RemovalRecords!.SequenceEqual(new[] { new ImageRemovalRecord(Untagged: "repo/tag:latest"),
                        new ImageRemovalRecord(Deleted: "sha256:original"), new ImageRemovalRecord(Deleted: "sha256:parent") }));
                    Check(((IList<ImageRemovalRecord>)result.RemovalRecords!).IsReadOnly);
                }
                else
                {
                    Check(result.Disposition == ImageCommandDisposition.DaemonResponse && result.DaemonMessage == "original daemon dependency reason");
                    Check(result.Failure?.Kind == (status == 404 ? ImageCommandFailureKind.NotFound
                        : status == 409 ? ImageCommandFailureKind.Conflict : ImageCommandFailureKind.DaemonFailure));
                    Check(!result.Failure!.Description.Contains("dependency reason"));
                }
                Check(requests.All(c => c.Disposed) && transport.Handler.Contents.All(c => c.Disposed));
                Check(transport.Handler.Paths.Count == 4, "Ordinary deletion and conflicts issue exactly one mutation without escalation");
                Check((await ((IImageReader)connection).ListImagesAsync()).IsSuccess);
                Check((await ((IImageReader)connection).InspectImageAsync("repo/tag:latest")).IsSuccess);
                Check((await ((IContainerReader)connection).ListContainersAsync()).IsSuccess);
                Check((await ((IContainerReader)connection).InspectContainerAsync("target")).IsSuccess);
                Check(await connection.CheckLivenessAsync() is null && !transport.Handler.Disposed);
            }
        }
    }

    internal static async Task ReferencesAndFailures()
    {
        var paths = new List<string>();
        var transport = Transport((request, _) => { paths.Add(request.RequestUri!.PathAndQuery); return Task.FromResult(Response("[{\"Untagged\":\"still-tagged-elsewhere\",\"Future\":{\"x\":1}}]")); });
        var owner = await Connect(transport); var commands = (IImageCommands)owner;
        foreach (var reference in new[] { "sha256:" + new string('a', 64), new string('a', 12), "registry.example:5000/a/b:Tag", "example/repo@sha256:" + new string('b', 64), "[::1]:5000/repo:Tag" })
        {
            var result = await commands.DeleteImageAsync(reference);
            Check(paths.Last() == "/v1.56/images/" + Uri.EscapeDataString(reference) + "?force=false&noprune=false");
            Check(result.Disposition == ImageCommandDisposition.Acknowledged && result.RemovalRecords!.Single().Deleted is null && result.RequiresReconciliation);
        }
        int count = transport.Handler.Paths.Count;
        foreach (var invalid in new[] { "", " ", ".", "..", "/x", "x/../y", "x?force=true", "x#fragment", "x%2Fname", "x\\y", "x\r\n", "x\0", "ü", "\ud800", new string('a', 4097), new string('a', 256), "Repo/Image", "repo:bad+tag" })
        {
            var result = await commands.DeleteImageAsync(invalid);
            Check(result.Disposition == ImageCommandDisposition.NotDispatched && !result.RequiresReconciliation && result.Failure?.Kind == ImageCommandFailureKind.InvalidRequest);
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = await Canceled(commands.DeleteImageAsync("repo", cancel.Token));
        Check(canceled.Disposition == ImageCommandDisposition.NotDispatched && !canceled.RequiresReconciliation);
        Check(transport.Handler.Paths.Count == count);
        await owner.DisposeAsync();
        Check((await commands.DeleteImageAsync("repo")).Failure?.Kind == ImageCommandFailureKind.ConnectionDisposed);
        var staleTransport = new ContinuityTestTransport();
        await using var stale = await Connect(staleTransport); staleTransport.Counter.Opened();
        Check((await ((IImageCommands)stale).DeleteImageAsync("repo")).Failure?.Kind == ImageCommandFailureKind.StaleConnection);
        Check(staleTransport.Handler.Paths.Count == 3);
        foreach (var status in new[] { 202, 204, 304, 400, 401, 403, 404, 409, 422, 500, 503 })
        {
            var failed = Transport((_, _) => Task.FromResult(Response("unchanged private daemon message", status)));
            await using var connection = await Connect(failed);
            var result = await ((IImageCommands)connection).DeleteImageAsync("repo");
            Check(result.Disposition == ImageCommandDisposition.DaemonResponse && result.DaemonMessage == "unchanged private daemon message");
            Check(result.Failure?.Kind == (status == 404 ? ImageCommandFailureKind.NotFound : status == 409 ? ImageCommandFailureKind.Conflict
                : status is 401 or 403 ? ImageCommandFailureKind.AccessDenied : status >= 500 ? ImageCommandFailureKind.DaemonFailure
                : status >= 400 ? ImageCommandFailureKind.InvalidRequest : ImageCommandFailureKind.UnexpectedResponse));
            Check(failed.Handler.Paths.Count == 4);
        }
        foreach (var exception in new Exception[] { new IOException("secret"), new HttpRequestException("secret"), new HttpRequestException("wrapped", new UnauthorizedAccessException()) })
        {
            var failed = Transport((_, _) => Task.FromException<HttpResponseMessage>(exception));
            await using var connection = await Connect(failed);
            var result = await ((IImageCommands)connection).DeleteImageAsync("repo");
            Check(result.Disposition == ImageCommandDisposition.Uncertain && result.RequiresReconciliation);
            Check(result.Failure?.Kind == (exception.InnerException is UnauthorizedAccessException ? ImageCommandFailureKind.AccessDenied : ImageCommandFailureKind.TransportFailure));
            Check(!result.Failure!.Description.Contains("secret") && failed.Handler.Paths.Count == 4);
        }
    }

    internal static async Task ResponsesAndBounds()
    {
        foreach (var body in new[] { "[]", "[{\"Untagged\":\"repo:tag\"}]", "[{\"Deleted\":\"sha256:x\"}]", "[{\"Untagged\":\"repo:tag\",\"Deleted\":\"sha256:x\",\"Future\":true}]" })
        {
            var source = new ImageCommandBodyStream(body, fragment: 1);
            var transport = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) }));
            await using var owner = await Connect(transport);
            var result = await ((IImageCommands)owner).DeleteImageAsync("repo");
            Check(result.Disposition == ImageCommandDisposition.Acknowledged && result.RequiresReconciliation && !result.ResponseBodyUnavailable && source.Disposed);
        }
        foreach (var body in new[] { "", " ", "null", "{}", "[", "[null]", "[{}]", "[{\"Deleted\":1}]", "[{\"Untagged\":null}]", "[{\"Deleted\":\"\"}]", "[{\"Deleted\":\"x\",\"Deleted\":\"y\"}]", "[] trailing", "[{\"Deleted\":\"good\"},{\"Deleted\":false}]" })
        {
            var transport = Transport((_, _) => Task.FromResult(Response(body)));
            await using var owner = await Connect(transport);
            var result = await ((IImageCommands)owner).DeleteImageAsync("repo");
            Check(result.Disposition == ImageCommandDisposition.DaemonResponse && result.Failure?.Kind == ImageCommandFailureKind.UnexpectedResponse && result.ResponseBodyUnavailable);
            if (body.Contains("good")) Check(result.RemovalRecords!.Single().Deleted == "good", "Completed records survive a later malformed record");
        }
        foreach (var known in new[] { false, true })
        foreach (var length in new[] { 64, 65, 1000000 })
        foreach (var status in new[] { 200, 409 })
        {
            var body = status == 200 ? "[{\"Deleted\":\"" + new string('a', length - 16) + "\"}]" : "{\"message\":\"" + new string('a', length - 14) + "\"}";
            Check(Encoding.UTF8.GetByteCount(body) == length);
            HttpContent content = known ? new TrackedContent(body) : new UnknownLengthContent(body);
            var transport = Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
            await using var owner = await Connect(transport, new() { MaximumImageCommandResponseBytes = 64, MaximumCommandErrorBytes = 64 });
            var result = await ((IImageCommands)owner).DeleteImageAsync("repo");
            Check(result.ResponseBodyUnavailable == (length > 64));
            Check(result.Disposition == (status == 200 && length == 64 ? ImageCommandDisposition.Acknowledged : ImageCommandDisposition.DaemonResponse));
            Check(status != 409 || result.Failure?.Kind == ImageCommandFailureKind.Conflict);
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
        foreach (var status in new[] { 200, 500 })
        {
            var counted = new CountedErrorStream();
            var transport = Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(counted) }));
            await using var owner = await Connect(transport, new() { MaximumImageCommandResponseBytes = 64, MaximumCommandErrorBytes = 64 });
            var result = await ((IImageCommands)owner).DeleteImageAsync("repo");
            Check(result.ResponseBodyUnavailable && counted.BytesRead == 65 && counted.Disposed);
        }
        foreach (var size in new[] { 63, 1024 * 1024 + 1 })
        {
            try { _ = new DockerEngineConnectionClient(new() { MaximumImageCommandResponseBytes = size }); throw new InvalidOperationException("Expected budget validation"); }
            catch (ArgumentOutOfRangeException) { }
        }
    }

    internal static async Task LifetimesAndSharedAdmission()
    {
        foreach (var mode in new[] { "cancel", "wrapped-cancel", "deadline", "dispose", "continuity", "lost-response" })
        foreach (var phase in new[] { "send", "success-body", "error-body" })
        {
            using var caller = new CancellationTokenSource();
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stalled = new StalledStream(); var counter = new PipeConnectionCounter(); counter.Opened();
            int mutations = 0;
            var transport = Transport(async (_, token) =>
            {
                mutations++;
                if (phase != "send") return new HttpResponseMessage(phase == "success-body" ? HttpStatusCode.OK : HttpStatusCode.Conflict) { Content = new StreamContent(stalled) };
                issued.TrySetResult();
                if (mode == "lost-response") throw new IOException("lost after daemon applied mutation");
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (mode == "wrapped-cancel") { throw new HttpRequestException("wrapped caller cancellation"); }
                return Response();
            });
            var owner = await Connect(new GuardedCommandTransport(transport.Handler, counter), new() { CommandTimeout = TimeSpan.FromMilliseconds(100), MaximumConcurrentCommands = 1 });
            var pending = ((IImageCommands)owner).DeleteImageAsync("repo", caller.Token);
            await (phase == "send" ? issued.Task : stalled.Started.Task).WaitAsync(TimeSpan.FromSeconds(3));
            if (mode != "lost-response" || phase != "send")
                Check((await ((IContainerCommands)owner).StartContainerAsync("target")).Failure?.Kind == ContainerCommandFailureKind.CapacityExceeded);
            ImageCommandResult result;
            if (mode is "cancel" or "wrapped-cancel") { caller.Cancel(); result = await Canceled(pending); }
            else { if (mode == "dispose") await owner.DisposeAsync(); if (mode == "continuity") counter.Opened(); result = await pending; }
            Check(result.Disposition == ImageCommandDisposition.Uncertain && result.RequiresReconciliation && mutations == 1 && transport.Handler.Paths.Count == 4);
            Check(phase == "send" || result.HttpStatusCode == (phase == "success-body" ? 200 : 409) && result.ResponseBodyUnavailable && stalled.Disposed);
            Check(result.Failure?.Kind == (mode is "cancel" or "wrapped-cancel" ? ImageCommandFailureKind.Canceled : mode == "dispose" ? ImageCommandFailureKind.ConnectionDisposed
                : mode == "continuity" ? ImageCommandFailureKind.StaleConnection : mode == "lost-response" && phase == "send" ? ImageCommandFailureKind.TransportFailure : ImageCommandFailureKind.Deadline));
            if (mode is not ("dispose" or "continuity"))
            {
                Check((await ((IImageReader)owner).ListImagesAsync()).IsSuccess);
                Check((await ((IImageCommands)owner).DeleteImageAsync("repo")).Failure?.Kind != ImageCommandFailureKind.CapacityExceeded, "Every termination releases shared admission");
            }
            await owner.DisposeAsync(); Check(transport.Handler.DisposeCount == 1);
        }
        // Container-to-image admission uses the very same gate; finite container work releases it.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new FakeTransport(async (request, index, token) =>
        {
            if (index < 3) return Response(Handshake(index));
            if (request.Method == HttpMethod.Post) { started.TrySetResult(); await release.Task.WaitAsync(token); return Response("", 204); }
            return Response();
        });
        await using var connection = await Connect(shared, new() { MaximumConcurrentCommands = 1 });
        var container = ((IContainerCommands)connection).StartContainerAsync("target"); await started.Task;
        var rejected = await ((IImageCommands)connection).DeleteImageAsync("repo");
        Check(rejected.Disposition == ImageCommandDisposition.NotDispatched && rejected.Failure?.Kind == ImageCommandFailureKind.CapacityExceeded && !rejected.RequiresReconciliation);
        release.TrySetResult(); Check((await container).Disposition == ContainerCommandDisposition.Acknowledged);
        Check((await ((IImageCommands)connection).DeleteImageAsync("repo")).Disposition == ImageCommandDisposition.Acknowledged);
        // Cancellation after a full 200 body arrives but before decode completes retains response evidence.
        using var decodeCancel = new CancellationTokenSource();
        var bodyStream = new ImageCommandBodyStream("[{\"Deleted\":\"applied\"}]", onEnd: decodeCancel.Cancel);
        var decoding = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(bodyStream) }));
        await using var decodeOwner = await Connect(decoding);
        var interrupted = await Canceled(((IImageCommands)decodeOwner).DeleteImageAsync("repo", decodeCancel.Token));
        Check(interrupted.Disposition == ImageCommandDisposition.Uncertain && interrupted.HttpStatusCode == 200 && interrupted.RequiresReconciliation && interrupted.ResponseBodyUnavailable && bodyStream.Disposed);
    }

    internal static async Task NamedPipeNoReplay()
    {
        foreach (var loseAfterMutation in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var name = "cdd-image-command-" + Guid.NewGuid().ToString("N")[..8];
            using var first = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            int mutations = 0;
            async Task Original()
            {
                await first.WaitForConnectionAsync(deadline.Token);
                for (int n = 0; n < 3; n++) { Check((await ReadRequest(first, deadline.Token)).StartsWith("GET ")); await WriteResponse(first, Handshake(n), deadline.Token, close: n == 2 && !loseAfterMutation); }
                if (loseAfterMutation)
                {
                    Check((await ReadRequest(first, deadline.Token)).StartsWith("DELETE /v1.56/images/repo%2Ftag%3Alatest?force=false&noprune=false HTTP/1.1"));
                    Interlocked.Increment(ref mutations);
                }
                first.Dispose();
            }
            var serving = Original();
            var factory = new NamedPipeTransport(permitTestHost: true);
            var endpoint = new EngineEndpoint("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe);
            var attempt = await new DockerEngineConnectionClient(factory).ConnectAsync(endpoint, deadline.Token); Check(attempt.IsConnected);
            using var replacement = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var replacementConnected = replacement.WaitForConnectionAsync(deadline.Token);
            await using var owner = attempt.Connection!;
            var result = await ((IImageCommands)owner).DeleteImageAsync("repo/tag:latest", deadline.Token);
            await serving;
            Check(result.Disposition == ImageCommandDisposition.Uncertain && result.RequiresReconciliation && mutations == (loseAfterMutation ? 1 : 0));
            Check(!replacementConnected.IsCompleted, "A waiting replacement daemon must never receive implicit mutation replay");
            var stale = await ((IImageCommands)owner).DeleteImageAsync("repo/tag:latest");
            Check(stale.Disposition == ImageCommandDisposition.NotDispatched && stale.Failure?.Kind == ImageCommandFailureKind.StaleConnection);
            async Task NewDaemon()
            {
                await replacementConnected;
                for (int n = 0; n < 4; n++)
                {
                    Check((await ReadRequest(replacement, deadline.Token)).StartsWith("GET "), "Fresh negotiation and reconciliation never repeat deletion");
                    await WriteResponse(replacement, n < 3 ? Handshake(n) : mutations == 1 ? "[]" : "[{\"Id\":\"retained\"}]", deadline.Token);
                }
                try { Check(await replacement.ReadAsync(new byte[1], deadline.Token) == 0); } catch (IOException) { }
            }
            var recovery = NewDaemon();
            var fresh = await new DockerEngineConnectionClient(factory).ConnectAsync(endpoint, deadline.Token); Check(fresh.IsConnected);
            var images = await ((IImageReader)fresh.Connection!).ListImagesAsync(deadline.Token);
            Check(images.Value!.Count == (mutations == 1 ? 0 : 1));
            await fresh.Connection!.DisposeAsync(); await recovery;
        }
    }

    internal static async Task LateResponseFences()
    {
        foreach (var phase in new[] { "headers", "body" })
        foreach (var mode in new[] { "cancel", "deadline", "dispose", "continuity" })
        {
            using var caller = new CancellationTokenSource();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var counter = new PipeConnectionCounter(); counter.Opened();
            var content = new TrackedContent("[{\"Deleted\":\"applied\"}]");
            var lateBody = new LateImageCommandStream(release.Task, started);
            var transport = Transport(async (_, _) =>
            {
                if (phase == "headers") { started.TrySetResult(); await release.Task; return new(HttpStatusCode.OK) { Content = content }; }
                return new(HttpStatusCode.OK) { Content = new StreamContent(lateBody) };
            });
            var owner = await Connect(new GuardedCommandTransport(transport.Handler, counter), new() { CommandTimeout = TimeSpan.FromMilliseconds(70), MaximumConcurrentCommands = 1 });
            var pending = ((IImageCommands)owner).DeleteImageAsync("repo", caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (mode == "cancel") caller.Cancel();
            if (mode == "dispose") await owner.DisposeAsync();
            if (mode == "continuity") counter.Opened();
            if (mode == "deadline") await Task.Delay(110);
            release.TrySetResult();
            var result = mode == "cancel" ? await Canceled(pending) : await pending;
            Check(result.Disposition == ImageCommandDisposition.Uncertain && result.RequiresReconciliation && result.HttpStatusCode == 200,
                "A late successful response cannot publish acknowledgement after lifetime/continuity ends");
            Check(result.Failure?.Kind == (mode == "cancel" ? ImageCommandFailureKind.Canceled : mode == "dispose" ? ImageCommandFailureKind.ConnectionDisposed
                : mode == "continuity" ? ImageCommandFailureKind.StaleConnection : ImageCommandFailureKind.Deadline));
            Check(phase == "headers" ? content.Disposed : lateBody.Disposed);
            Check(transport.Handler.Paths.Count == 4);
            await owner.DisposeAsync(); Check(transport.Handler.DisposeCount == 1);
        }
    }

    internal static async Task NamedPipeIssuedLifetimes()
    {
        foreach (var mode in new[] { "cancel", "deadline", "dispose" })
        foreach (var successHeaders in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var caller = new CancellationTokenSource();
            var name = "cdd-image-command-" + Guid.NewGuid().ToString("N")[..8];
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Serve()
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                for (int n = 0; n < 3; n++) { await ReadRequest(pipe, deadline.Token); await WriteResponse(pipe, Handshake(n), deadline.Token); }
                Check((await ReadRequest(pipe, deadline.Token)).StartsWith("DELETE /v1.56/images/repo?force=false&noprune=false"));
                if (successHeaders)
                {
                    await pipe.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n[{\"Deleted\":\"applied\"}"), deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                }
                issued.TrySetResult();
                try { Check(await pipe.ReadAsync(new byte[1], deadline.Token) == 0); } catch (IOException) { }
            }
            var serving = Serve();
            var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true), new() { CommandTimeout = TimeSpan.FromMilliseconds(180) })
                .ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token); Check(attempt.IsConnected);
            await using var owner = attempt.Connection!;
            var pending = ((IImageCommands)owner).DeleteImageAsync("repo", caller.Token); await issued.Task.WaitAsync(deadline.Token);
            ImageCommandResult result;
            if (mode == "cancel") { caller.Cancel(); result = await Canceled(pending); }
            else { if (mode == "dispose") await owner.DisposeAsync(); result = await pending; }
            Check(result.Disposition == ImageCommandDisposition.Uncertain && result.RequiresReconciliation);
            Check(result.Failure?.Kind == (mode == "cancel" ? ImageCommandFailureKind.Canceled : mode == "deadline" ? ImageCommandFailureKind.Deadline : ImageCommandFailureKind.ConnectionDisposed));
            await serving;
        }
    }

    private static async Task<string> ReadRequest(Stream pipe, CancellationToken token)
    {
        var headers = new StringBuilder(); var one = new byte[1];
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        { Check(headers.Length < 8192 && await pipe.ReadAsync(one, token) == 1); headers.Append((char)one[0]); }
        return headers.ToString();
    }
    private static async Task WriteResponse(Stream pipe, string body, CancellationToken token, bool close = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await pipe.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\n" + (close ? "Connection: close\r\n" : "") + "\r\n"), token);
        await pipe.WriteAsync(bytes, token); await pipe.FlushAsync(token);
    }
}

internal sealed class LateImageCommandStream(Task release, TaskCompletionSource started) : MemoryStream("[{\"Deleted\":\"applied\"}]"u8.ToArray())
{
    public bool Disposed { get; private set; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { started.TrySetResult(); await release; return await base.ReadAsync(buffer, CancellationToken.None); }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}

internal sealed class ImageCommandBodyStream(string body, int fragment = 4096, Action? onEnd = null) : Stream
{
    private readonly byte[] bytes = Encoding.UTF8.GetBytes(body);
    private int position;
    public bool Disposed { get; private set; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = Math.Min(Math.Min(buffer.Length, fragment), bytes.Length - position);
        bytes.AsMemory(position, count).CopyTo(buffer); position += count;
        if (count == 0) onEnd?.Invoke();
        return ValueTask.FromResult(count);
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
