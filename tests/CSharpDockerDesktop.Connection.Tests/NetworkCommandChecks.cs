using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class NetworkCommandChecks
{
    private static void Check(bool value, string message = "Network command assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body = "", int status = 204)
        => new((HttpStatusCode)status) { Content = new TrackedContent(body) };
    private static string Handshake(int index, int minor = 56) => index switch
    { 0 => "OK", 1 => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""", _ => "{}" };
    private static FakeTransport Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> command, int minor = 56)
        => new((request, index, token) => index < 3 ? Task.FromResult(Response(Handshake(index, minor), 200))
            : request.Method == HttpMethod.Delete ? command(request, token)
            : Task.FromResult(Response(request.RequestUri!.AbsolutePath == "/_ping" ? "OK"
                : request.RequestUri.AbsolutePath.EndsWith("/volumes") ? "{\"Volumes\":[]}" : "[]", 200)));
    private static async Task<IEngineConnection> Connect(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected && attempt.Connection is INetworkCommands);
        return attempt.Connection!;
    }
    private static async Task<NetworkCommandResult> Canceled(Task<NetworkCommandResult> pending, CancellationToken caller)
    {
        try { await pending; throw new InvalidOperationException("Expected cancellation"); }
        catch (NetworkCommandCanceledException ex) { Check(pending.IsCanceled && ex.CancellationToken == caller); return ex.Outcome; }
    }

    internal static async Task OfficialContracts()
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "NetworkCommands", "contract-evidence.json")));
        var versions = evidence.RootElement.GetProperty("versions").EnumerateArray().ToArray();
        Check(versions.Length == 17);
        foreach (var version in versions)
        {
            var api = version.GetProperty("api").GetString()!;
            int minor = int.Parse(api.Split('.')[1]);
            Check(version.GetProperty("sha256").GetString()!.Length == 64);
            Check(version.GetProperty("method").GetString() == "DELETE" && version.GetProperty("path").GetString() == "/networks/{id}");
            var parameters = version.GetProperty("parameters").EnumerateArray().ToArray();
            Check(parameters.Length == 1 && parameters[0].GetProperty("name").GetString() == "id" && parameters[0].GetProperty("required").GetBoolean());
            Check(version.GetProperty("errorResponse").GetProperty("properties").GetProperty("message").GetProperty("type").GetString() == "string");
            foreach (var response in version.GetProperty("responses").EnumerateObject())
            {
                int status = int.Parse(response.Name);
                Check(status is 204 or 403 or 404 or 500);
                if (status == 204) Check(!response.Value.TryGetProperty("schema", out _));
                else Check(response.Value.GetProperty("schema").GetProperty("$ref").GetString() == "#/definitions/ErrorResponse");
                string? path = null; var requests = new List<TrackedContent>();
                var transport = Transport((request, _) =>
                {
                    Check(request.Method == HttpMethod.Delete && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact && request.Version == new Version(1, 1));
                    Check(request.Content is null);
                    path = request.RequestUri!.PathAndQuery;
                    var content = new TrackedContent(""); request.Content = content; requests.Add(content);
                    return Task.FromResult(Response(status == 204 ? "" : "{\"message\":\"original daemon dependency reason\",\"Future\":true}", status));
                }, minor);
                await using var connection = await Connect(transport);
                var result = await ((INetworkCommands)connection).DeleteNetworkAsync("plugin/full?name");
                Check(path == $"/v{api}/networks/plugin%2Ffull%3Fname");
                Check(result.HttpStatusCode == status && result.RequiresReconciliation);
                if (status == 204)
                    Check(result.Disposition == NetworkCommandDisposition.Acknowledged && result.Failure is null && result.DaemonErrorBody is null);
                else
                {
                    Check(result.Disposition == NetworkCommandDisposition.DaemonResponse && result.DaemonMessage == "original daemon dependency reason");
                    Check(result.DaemonErrorBody == "{\"message\":\"original daemon dependency reason\",\"Future\":true}");
                    Check(result.Failure?.Kind == (status == 404 ? NetworkCommandFailureKind.NotFound
                        : status == 403 ? NetworkCommandFailureKind.AccessDenied : NetworkCommandFailureKind.DaemonFailure));
                    Check(!result.Failure!.Description.Contains("dependency reason"));
                }
                Check(requests.All(c => c.Disposed) && transport.Handler.Contents.All(c => c.Disposed));
                Check(transport.Handler.Paths.Count == 4, "Exactly one mutation, without conflict escalation or dependent-container removal");
                Check((await ((INetworkReader)connection).ListNetworksAsync()).IsSuccess);
                Check((await ((IImageReader)connection).ListImagesAsync()).IsSuccess);
                Check((await ((IContainerReader)connection).ListContainersAsync()).IsSuccess);
                Check(await connection.CheckLivenessAsync() is null && !transport.Handler.Disposed);
            }
        }
    }

    internal static async Task IdentitiesAndFailures()
    {
        var paths = new List<string>();
        var transport = Transport((request, _) => { paths.Add(request.RequestUri!.PathAndQuery); return Task.FromResult(Response()); });
        var owner = await Connect(transport); var commands = (INetworkCommands)owner;
        foreach (var name in new[] { new string('a', 64), "ub46u786mu5bj9fyrjxq8lm77", "Plugin/../full?force=true#fragment", " x ", "ü/🙂", "x%2Fy", "x\\y", "/", new string('a', 4096) })
        {
            var result = await commands.DeleteNetworkAsync(name);
            Check(paths.Last() == "/v1.56/networks/" + Uri.EscapeDataString(name) + "", "Accepted original IDs are never trimmed, normalized or truncated");
            Check(result.Disposition == NetworkCommandDisposition.Acknowledged && result.RequiresReconciliation);
        }
        int count = transport.Handler.Paths.Count;
        foreach (var invalid in new[] { null, "", " \t", ".", "..", "x\r\n", "x\0", "\ud800", "\udc00", new string('a', 4097) })
        {
            var result = await commands.DeleteNetworkAsync(invalid!);
            Check(result.Disposition == NetworkCommandDisposition.NotDispatched && !result.RequiresReconciliation && result.Failure?.Kind == NetworkCommandFailureKind.InvalidRequest);
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = await Canceled(commands.DeleteNetworkAsync("original", cancel.Token), cancel.Token);
        Check(canceled.Disposition == NetworkCommandDisposition.NotDispatched && !canceled.RequiresReconciliation && canceled.HttpStatusCode is null);
        Check(transport.Handler.Paths.Count == count);
        await owner.DisposeAsync();
        Check((await commands.DeleteNetworkAsync("original")).Failure?.Kind == NetworkCommandFailureKind.ConnectionDisposed);
        Check(transport.Handler.Paths.Count == count && transport.Handler.DisposeCount == 1);
        var staleTransport = new ContinuityTestTransport();
        await using var stale = await Connect(staleTransport); staleTransport.Counter.Opened();
        var staleResult = await ((INetworkCommands)stale).DeleteNetworkAsync("original");
        Check(staleResult.Disposition == NetworkCommandDisposition.NotDispatched && staleResult.Failure?.Kind == NetworkCommandFailureKind.StaleConnection);
        Check(staleTransport.Handler.Paths.Count == 3);
        foreach (var status in new[] { 200, 201, 202, 301, 304, 400, 401, 403, 404, 409, 422, 500, 503, 600 })
        {
            var failed = Transport((_, _) => Task.FromResult(Response("unchanged private daemon message", status)));
            await using var connection = await Connect(failed);
            var result = await ((INetworkCommands)connection).DeleteNetworkAsync("original");
            Check(result.Disposition == NetworkCommandDisposition.DaemonResponse && result.DaemonMessage == "unchanged private daemon message"
                && result.DaemonErrorBody == "unchanged private daemon message" && result.HttpStatusCode == status && result.RequiresReconciliation);
            Check(result.Failure?.Kind == (status == 404 ? NetworkCommandFailureKind.NotFound : status == 409 ? NetworkCommandFailureKind.Conflict
                : status is 401 or 403 ? NetworkCommandFailureKind.AccessDenied : status is >= 500 and < 600 ? NetworkCommandFailureKind.DaemonFailure
                : status is >= 400 and < 500 ? NetworkCommandFailureKind.InvalidRequest : NetworkCommandFailureKind.UnexpectedResponse));
            Check(failed.Handler.Paths.Count == 4);
        }
        foreach (var exception in new Exception[] { new IOException("secret"), new HttpRequestException("secret"), new HttpRequestException("wrapped", new UnauthorizedAccessException()) })
        {
            var failed = Transport((_, _) => Task.FromException<HttpResponseMessage>(exception));
            await using var connection = await Connect(failed);
            var result = await ((INetworkCommands)connection).DeleteNetworkAsync("original");
            Check(result.Disposition == NetworkCommandDisposition.Uncertain && result.RequiresReconciliation && result.HttpStatusCode is null);
            Check(result.Failure?.Kind == (exception.InnerException is UnauthorizedAccessException ? NetworkCommandFailureKind.AccessDenied : NetworkCommandFailureKind.TransportFailure));
            Check(!result.Failure!.Description.Contains("secret") && failed.Handler.Paths.Count == 4);
        }
        foreach (var message in new[] { "network private-name id original has active endpoints",
            "error while removing network: network private-name id original has active endpoints",
            "network private-name id other has active endpoints", "configuration network \"config-name\" is in use",
            "error while removing network: configuration network \"config-name\" is in use", "authorization denied", "bridge is a pre-defined network and cannot be removed" })
        {
            var forbidden = Transport((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new { message }), 403)));
            await using var connection = await Connect(forbidden);
            var result = await ((INetworkCommands)connection).DeleteNetworkAsync("original");
            Check(result.HttpStatusCode == 403 && result.DaemonMessage == message && result.RequiresReconciliation);
            Check(result.Failure?.Kind == (message.EndsWith(" id original has active endpoints", StringComparison.Ordinal) || message.EndsWith("\" is in use", StringComparison.Ordinal)
                ? NetworkCommandFailureKind.Conflict : NetworkCommandFailureKind.AccessDenied));
        }
        var statusException = Transport((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("secret", null, HttpStatusCode.Forbidden)));
        await using var statusOwner = await Connect(statusException);
        var denied = await ((INetworkCommands)statusOwner).DeleteNetworkAsync("original");
        Check(denied.Disposition == NetworkCommandDisposition.Uncertain && denied.HttpStatusCode == 403 && denied.Failure?.Kind == NetworkCommandFailureKind.AccessDenied);
    }

    internal static async Task ErrorsAndBounds()
    {
        foreach (var body in new[] { "", " ", "null", "{}", "{", "{\"message\":7}", "{\"message\":\"original 🙂\",\"Future\":true}", "plain original error" })
        {
            var source = new ImageCommandBodyStream(body, fragment: 1);
            var transport = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(source) }));
            await using var owner = await Connect(transport);
            var result = await ((INetworkCommands)owner).DeleteNetworkAsync("original");
            Check(result.Disposition == NetworkCommandDisposition.DaemonResponse && result.Failure?.Kind == NetworkCommandFailureKind.Conflict
                && result.RequiresReconciliation && !result.ErrorBodyUnavailable && result.DaemonErrorBody == body && source.Disposed);
            if (body.Contains("original 🙂")) Check(result.DaemonMessage == "original 🙂");
        }
        foreach (var known in new[] { false, true })
        foreach (var length in new[] { 64, 65, 1000000 })
        {
            var body = "{\"message\":\"" + new string('a', length - 14) + "\"}";
            Check(Encoding.UTF8.GetByteCount(body) == length);
            HttpContent content = known ? new TrackedContent(body) : new UnknownLengthContent(body);
            var transport = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = content }));
            await using var owner = await Connect(transport, new() { MaximumCommandErrorBytes = 64 });
            var result = await ((INetworkCommands)owner).DeleteNetworkAsync("original");
            Check(result.ErrorBodyUnavailable == (length > 64) && result.HttpStatusCode == 409);
            Check(result.DaemonErrorBody == (length == 64 ? body : null));
            Check(result.Disposition == NetworkCommandDisposition.DaemonResponse && result.Failure?.Kind == NetworkCommandFailureKind.Conflict);
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
        var truncatedBody = new TrackedContent("{\"message\":\"cut");
        truncatedBody.Headers.ContentLength = 100;
        var truncated = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = truncatedBody }));
        await using var truncatedOwner = await Connect(truncated);
        var shortResult = await ((INetworkCommands)truncatedOwner).DeleteNetworkAsync("original");
        Check(shortResult.Disposition == NetworkCommandDisposition.Uncertain && shortResult.HttpStatusCode == 409
            && shortResult.ErrorBodyUnavailable && shortResult.DaemonErrorBody is null
            && shortResult.Failure?.Kind == NetworkCommandFailureKind.TransportFailure && truncatedBody.Disposed);
        var invalidUtf8 = new MemoryStream([0xff, 0xfe, 0x7b]);
        var malformedEncoding = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(invalidUtf8) }));
        await using var malformedOwner = await Connect(malformedEncoding);
        var malformedResult = await ((INetworkCommands)malformedOwner).DeleteNetworkAsync("original");
        Check(malformedResult.Disposition == NetworkCommandDisposition.DaemonResponse && malformedResult.HttpStatusCode == 409
            && malformedResult.ErrorBodyUnavailable && malformedResult.DaemonErrorBody is null && malformedResult.DaemonMessage is null
            && malformedResult.Failure?.Kind == NetworkCommandFailureKind.Conflict && !invalidUtf8.CanRead,
            "Invalid UTF-8 must not be presented as the unchanged original body via replacement characters");
        var counted = new CountedErrorStream();
        var oversized = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StreamContent(counted) }));
        await using var bounded = await Connect(oversized, new() { MaximumCommandErrorBytes = 64 });
        Check((await ((INetworkCommands)bounded).DeleteNetworkAsync("original")).ErrorBodyUnavailable && counted.BytesRead == 65 && counted.Disposed);
        // Full response content survives caller cancellation at EOF, and still cannot acknowledge success.
        using var caller = new CancellationTokenSource();
        const string complete = "{\"message\":\"complete original error\"}";
        var eof = new ImageCommandBodyStream(complete, onEnd: caller.Cancel);
        var interrupted = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(eof) }));
        await using var cancelOwner = await Connect(interrupted);
        var canceled = await Canceled(((INetworkCommands)cancelOwner).DeleteNetworkAsync("original", caller.Token), caller.Token);
        Check(canceled.Disposition == NetworkCommandDisposition.Uncertain && canceled.HttpStatusCode == 409 && !canceled.ErrorBodyUnavailable
            && canceled.DaemonErrorBody == complete && canceled.DaemonMessage == "complete original error" && eof.Disposed);
        var lostBody = new LostNetworkCommandStream();
        var lost = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(lostBody) }));
        await using var lostOwner = await Connect(lost);
        var unavailable = await ((INetworkCommands)lostOwner).DeleteNetworkAsync("original");
        Check(unavailable.Disposition == NetworkCommandDisposition.Uncertain && unavailable.HttpStatusCode == 409 && unavailable.ErrorBodyUnavailable
            && unavailable.Failure?.Kind == NetworkCommandFailureKind.TransportFailure && lostBody.Disposed && lost.Handler.Paths.Count == 4);
    }

    internal static async Task LifetimesAndSharedAdmission()
    {
        foreach (var mode in new[] { "cancel", "wrapped-cancel", "deadline", "dispose", "continuity", "lost-response" })
        foreach (var phase in new[] { "send", "error-body" })
        {
            using var caller = new CancellationTokenSource();
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stalled = new StalledStream(); var counter = new PipeConnectionCounter(); counter.Opened();
            int mutations = 0;
            var transport = Transport(async (_, token) =>
            {
                mutations++;
                if (phase == "error-body") return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(stalled) };
                issued.TrySetResult();
                if (mode == "lost-response") throw new IOException("lost after daemon applied mutation");
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (mode == "wrapped-cancel") { throw new HttpRequestException("wrapped caller cancellation"); }
                return Response();
            });
            var owner = await Connect(new GuardedCommandTransport(transport.Handler, counter), new() { CommandTimeout = TimeSpan.FromMilliseconds(120), MaximumConcurrentCommands = 1 });
            var pending = ((INetworkCommands)owner).DeleteNetworkAsync("original", caller.Token);
            await (phase == "send" ? issued.Task : stalled.Started.Task).WaitAsync(TimeSpan.FromSeconds(3));
            if (mode != "lost-response" || phase != "send")
            {
                Check((await ((IContainerCommands)owner).StartContainerAsync("target")).Failure?.Kind == ContainerCommandFailureKind.CapacityExceeded);
                Check((await ((IImageCommands)owner).DeleteImageAsync("repo")).Failure?.Kind == ImageCommandFailureKind.CapacityExceeded);
                Check((await ((IVolumeCommands)owner).DeleteVolumeAsync("volume")).Failure?.Kind == VolumeCommandFailureKind.CapacityExceeded);
            }
            NetworkCommandResult result;
            if (mode is "cancel" or "wrapped-cancel") { caller.Cancel(); result = await Canceled(pending, caller.Token); }
            else { if (mode == "dispose") await owner.DisposeAsync(); if (mode == "continuity") counter.Opened(); result = await pending; }
            Check(result.Disposition == NetworkCommandDisposition.Uncertain && result.RequiresReconciliation && mutations == 1 && transport.Handler.Paths.Count == 4);
            Check(phase == "send" || result.HttpStatusCode == 409 && result.ErrorBodyUnavailable && stalled.Disposed);
            Check(result.Failure?.Kind == (mode is "cancel" or "wrapped-cancel" ? NetworkCommandFailureKind.Canceled : mode == "dispose" ? NetworkCommandFailureKind.ConnectionDisposed
                : mode == "continuity" ? NetworkCommandFailureKind.StaleConnection : mode == "lost-response" && phase == "send" ? NetworkCommandFailureKind.TransportFailure : NetworkCommandFailureKind.Deadline));
            if (mode is not ("dispose" or "continuity"))
            {
                Check((await ((INetworkReader)owner).ListNetworksAsync()).IsSuccess);
                Check((await ((INetworkCommands)owner).DeleteNetworkAsync("original")).Failure?.Kind != NetworkCommandFailureKind.CapacityExceeded, "Every termination releases shared admission");
            }
            await owner.DisposeAsync(); Check(transport.Handler.DisposeCount == 1);
        }
        // Container/image/volume commands also occupy network deletion's finite shared admission gate.
        foreach (var occupiedKind in new[] { "container", "image", "volume" })
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shared = new FakeTransport(async (request, index, token) =>
            {
                if (index < 3) return Response(Handshake(index), 200);
                if (!request.RequestUri!.AbsolutePath.Contains("/networks/"))
                { started.TrySetResult(); await release.Task.WaitAsync(token); return occupiedKind == "image" ? Response("[]", 200) : Response(); }
                return Response();
            });
            await using var connection = await Connect(shared, new() { MaximumConcurrentCommands = 1 });
            Task occupied = occupiedKind == "image" ? ((IImageCommands)connection).DeleteImageAsync("repo")
                : occupiedKind == "volume" ? ((IVolumeCommands)connection).DeleteVolumeAsync("volume") : ((IContainerCommands)connection).StartContainerAsync("target");
            await started.Task;
            var rejected = await ((INetworkCommands)connection).DeleteNetworkAsync("original");
            Check(rejected.Disposition == NetworkCommandDisposition.NotDispatched && rejected.Failure?.Kind == NetworkCommandFailureKind.CapacityExceeded && !rejected.RequiresReconciliation);
            release.TrySetResult(); await occupied;
            Check((await ((INetworkCommands)connection).DeleteNetworkAsync("original")).Disposition == NetworkCommandDisposition.Acknowledged);
        }
    }

    internal static async Task LateResponseFences()
    {
        foreach (var phase in new[] { "headers", "error-body" })
        foreach (var mode in new[] { "cancel", "deadline", "dispose", "continuity" })
        {
            using var caller = new CancellationTokenSource();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var counter = new PipeConnectionCounter(); counter.Opened();
            var content = new TrackedContent("");
            var lateBody = new LateNetworkCommandStream(release.Task, started);
            var transport = Transport(async (_, _) =>
            {
                if (phase == "headers") { started.TrySetResult(); await release.Task; return new(HttpStatusCode.NoContent) { Content = content }; }
                return new(HttpStatusCode.Conflict) { Content = new StreamContent(lateBody) };
            });
            var owner = await Connect(new GuardedCommandTransport(transport.Handler, counter), new() { CommandTimeout = TimeSpan.FromMilliseconds(70), MaximumConcurrentCommands = 1 });
            var pending = ((INetworkCommands)owner).DeleteNetworkAsync("original", caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (mode == "cancel") caller.Cancel();
            if (mode == "dispose") await owner.DisposeAsync();
            if (mode == "continuity") counter.Opened();
            if (mode == "deadline") await Task.Delay(110);
            release.TrySetResult();
            var result = mode == "cancel" ? await Canceled(pending, caller.Token) : await pending;
            Check(result.Disposition == NetworkCommandDisposition.Uncertain && result.RequiresReconciliation && result.HttpStatusCode == (phase == "headers" ? 204 : 409),
                "A cancellation-ignoring completion cannot publish acknowledgement after lifetime/continuity ends");
            Check(result.Failure?.Kind == (mode == "cancel" ? NetworkCommandFailureKind.Canceled : mode == "dispose" ? NetworkCommandFailureKind.ConnectionDisposed
                : mode == "continuity" ? NetworkCommandFailureKind.StaleConnection : NetworkCommandFailureKind.Deadline));
            Check(phase == "headers" ? content.Disposed : lateBody.Disposed);
            Check(transport.Handler.Paths.Count == 4);
            await owner.DisposeAsync(); Check(transport.Handler.DisposeCount == 1);
        }
    }

    internal static async Task NamedPipeNoReplay()
    {
        foreach (var loseAfterMutation in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var name = "cdd-network-command-" + Guid.NewGuid().ToString("N")[..8];
            using var first = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            int mutations = 0;
            async Task Original()
            {
                await first.WaitForConnectionAsync(deadline.Token);
                for (int n = 0; n < 3; n++) { Check((await ReadRequest(first, deadline.Token)).StartsWith("GET ")); await WriteResponse(first, Handshake(n), deadline.Token, close: n == 2 && !loseAfterMutation); }
                if (loseAfterMutation)
                {
                    Check((await ReadRequest(first, deadline.Token)).StartsWith("DELETE /v1.56/networks/plugin%2Ffull%3Fname HTTP/1.1"));
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
            var result = await ((INetworkCommands)owner).DeleteNetworkAsync("plugin/full?name", deadline.Token);
            await serving;
            Check(result.Disposition == NetworkCommandDisposition.Uncertain && result.RequiresReconciliation && mutations == (loseAfterMutation ? 1 : 0));
            Check(!replacementConnected.IsCompleted, "A waiting replacement daemon must never receive implicit mutation replay");
            var stale = await ((INetworkCommands)owner).DeleteNetworkAsync("plugin/full?name");
            Check(stale.Disposition == NetworkCommandDisposition.NotDispatched && stale.Failure?.Kind == NetworkCommandFailureKind.StaleConnection);
            async Task NewDaemon()
            {
                await replacementConnected;
                for (int n = 0; n < 4; n++)
                {
                    Check((await ReadRequest(replacement, deadline.Token)).StartsWith("GET "), "Fresh negotiation and reconciliation never repeat deletion");
                    await WriteResponse(replacement, n < 3 ? Handshake(n) : mutations == 1 ? "[]" : "[{\"Id\":\"plugin/full?name\"}]", deadline.Token);
                }
                try { Check(await replacement.ReadAsync(new byte[1], deadline.Token) == 0); } catch (IOException) { }
            }
            var recovery = NewDaemon();
            var fresh = await new DockerEngineConnectionClient(factory).ConnectAsync(endpoint, deadline.Token); Check(fresh.IsConnected);
            var networks = await ((INetworkReader)fresh.Connection!).ListNetworksAsync(deadline.Token);
            Check(networks.Value!.Count == (mutations == 1 ? 0 : 1));
            await fresh.Connection!.DisposeAsync(); await recovery;
        }
    }

    internal static async Task NamedPipeIssuedLifetimes()
    {
        foreach (var mode in new[] { "cancel", "deadline", "dispose" })
        foreach (var errorHeaders in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var caller = new CancellationTokenSource();
            var name = "cdd-network-command-" + Guid.NewGuid().ToString("N")[..8];
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Serve()
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                for (int n = 0; n < 3; n++) { await ReadRequest(pipe, deadline.Token); await WriteResponse(pipe, Handshake(n), deadline.Token); }
                Check((await ReadRequest(pipe, deadline.Token)).StartsWith("DELETE /v1.56/networks/original"));
                if (errorHeaders)
                {
                    await pipe.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 409 Conflict\r\nContent-Length: 100\r\n\r\n{\"message\":\"in-use\""), deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                }
                issued.TrySetResult();
                try { Check(await pipe.ReadAsync(new byte[1], deadline.Token) == 0); } catch (IOException) { }
            }
            var serving = Serve();
            var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true), new() { CommandTimeout = TimeSpan.FromMilliseconds(180) })
                .ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token); Check(attempt.IsConnected);
            await using var owner = attempt.Connection!;
            var pending = ((INetworkCommands)owner).DeleteNetworkAsync("original", caller.Token); await issued.Task.WaitAsync(deadline.Token);
            NetworkCommandResult result;
            if (mode == "cancel") { caller.Cancel(); result = await Canceled(pending, caller.Token); }
            else { if (mode == "dispose") await owner.DisposeAsync(); result = await pending; }
            Check(result.Disposition == NetworkCommandDisposition.Uncertain && result.RequiresReconciliation);
            Check(result.Failure?.Kind == (mode == "cancel" ? NetworkCommandFailureKind.Canceled : mode == "deadline" ? NetworkCommandFailureKind.Deadline : NetworkCommandFailureKind.ConnectionDisposed));
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

internal sealed class LateNetworkCommandStream(Task release, TaskCompletionSource started) : MemoryStream("{\"message\":\"original conflict\"}"u8.ToArray())
{
    public bool Disposed { get; private set; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { started.TrySetResult(); await release; return await base.ReadAsync(buffer, CancellationToken.None); }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}

internal sealed class LostNetworkCommandStream : MemoryStream
{
    public bool Disposed { get; private set; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromException<int>(new IOException("lost original response"));
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}
