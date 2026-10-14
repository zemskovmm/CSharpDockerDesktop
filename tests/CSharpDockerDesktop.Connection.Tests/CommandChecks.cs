using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class CommandChecks
{
    private static void Check(bool condition, string message = "Command assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body = "", int status = 204)
        => new((HttpStatusCode)status) { Content = new TrackedContent(body) };
    private static string Handshake(int index, int minor = 56) => index switch
    {
        0 => "OK", 1 => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""", _ => "{}"
    };
    private static FakeTransport Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> command, int minor = 56)
        => new((request, index, token) => index < 3 ? Task.FromResult(Response(Handshake(index, minor), 200))
            : request.Method == HttpMethod.Get ? Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/containers/json") ? "[]" : "{\"Id\":\"target\"}", 200))
            : command(request, token));
    private static async Task<IEngineConnection> Connect(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected && attempt.Connection is IContainerCommands);
        return attempt.Connection!;
    }
    private static Task<ContainerCommandResult> Send(IContainerCommands commands, ContainerCommand command, CancellationToken token = default)
        => command switch
        {
            ContainerCommand.Start => commands.StartContainerAsync("target", token),
            ContainerCommand.Stop => commands.StopContainerAsync("target", cancellationToken: token),
            ContainerCommand.Restart => commands.RestartContainerAsync("target", cancellationToken: token),
            _ => commands.DeleteContainerAsync("target", cancellationToken: token)
        };
    private static async Task<ContainerCommandResult> Canceled(Task<ContainerCommandResult> pending)
    {
        try { await pending; throw new InvalidOperationException("Expected command cancellation"); }
        catch (ContainerCommandCanceledException ex)
        { Check(ex is OperationCanceledException && pending.IsCanceled); return ex.Outcome; }
    }

    internal static async Task OfficialContracts()
    {
        var evidencePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Commands", "contract-evidence.json");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
        var versions = evidence.RootElement.GetProperty("versions").EnumerateArray().ToArray();
        Check(versions.Length == 17);
        foreach (var version in versions)
        {
            var api = version.GetProperty("api").GetString()!;
            var minor = int.Parse(api.Split('.')[1]);
            foreach (var endpoint in version.GetProperty("endpoints").EnumerateArray())
            {
                var path = endpoint.GetProperty("path").GetString()!;
                var command = path.EndsWith("/start") ? ContainerCommand.Start : path.EndsWith("/stop") ? ContainerCommand.Stop
                    : path.EndsWith("/restart") ? ContainerCommand.Restart : ContainerCommand.Delete;
                var parameters = endpoint.GetProperty("parameters").EnumerateArray().ToArray();
                Check(parameters.Any(p => p.GetProperty("name").GetString() == "id" && p.GetProperty("type").GetString() == "string"));
                if (command is ContainerCommand.Stop or ContainerCommand.Restart)
                    Check(parameters.Single(p => p.GetProperty("name").GetString() == "t").GetProperty("type").GetString() == "integer");
                if (command == ContainerCommand.Delete)
                    foreach (var name in new[] { "v", "force" })
                        Check(parameters.Single(p => p.GetProperty("name").GetString() == name).GetProperty("default").GetBoolean() == false);
                foreach (var statusProperty in endpoint.GetProperty("responses").EnumerateObject())
                {
                    int status = int.Parse(statusProperty.Name);
                    var requests = new List<(string Method, string Path)>();
                    var requestContents = new List<TrackedContent>();
                    var transport = Transport((request, _) =>
                    {
                        requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery));
                        // Makes request disposal observable without changing the production transport.
                        var content = new TrackedContent(""); request.Content = content; requestContents.Add(content);
                        return Task.FromResult(Response(status == 204 || status == 304 ? "" : "{\"message\":\"original daemon reason\"}", status));
                    }, minor);
                    await using var connection = await Connect(transport);
                    var capability = (IContainerCommands)connection;
                    var result = status == 204 && command == ContainerCommand.Stop ? await capability.StopContainerAsync("target", 15)
                        : status == 204 && command == ContainerCommand.Restart ? await capability.RestartContainerAsync("target", 15)
                        : await Send(capability, command);
                    Check(requests.Single().Method == endpoint.GetProperty("method").GetString());
                    var expectedPath = $"/v{api}" + path.Replace("{id}", "target") + (command == ContainerCommand.Delete ? "?force=false&v=false" : "");
                    if (status == 204 && command is ContainerCommand.Stop or ContainerCommand.Restart) expectedPath += "?t=15";
                    Check(requests.Single().Path == expectedPath);
                    Check(result.Command == command && result.HttpStatusCode == status && result.RequiresReconciliation);
                    if (status is 204 or 304)
                    {
                        Check(result.Disposition == ContainerCommandDisposition.Acknowledged && result.Failure is null);
                        Check(result.Acknowledgement == (status == 204 ? ContainerCommandAcknowledgement.Accepted : ContainerCommandAcknowledgement.AlreadyInRequestedState));
                    }
                    else
                    {
                        Check(result.Disposition == ContainerCommandDisposition.DaemonResponse && result.DaemonMessage == "original daemon reason");
                        Check(result.Failure!.Kind == (status == 404 ? ContainerCommandFailureKind.NotFound : status == 409 ? ContainerCommandFailureKind.Conflict
                            : status == 400 ? ContainerCommandFailureKind.InvalidRequest : ContainerCommandFailureKind.DaemonFailure));
                        Check(!result.Failure.Description.Contains("original"));
                    }
                    if (command == ContainerCommand.Delete && status == 204)
                    {
                        var forced = await capability.DeleteContainerAsync("target", force: true);
                        Check(forced.Disposition == ContainerCommandDisposition.Acknowledged);
                        Check(requests.Last() == ("DELETE", $"/v{api}/containers/target?force=true&v=false"));
                    }
                    Check(requestContents.All(c => c.Disposed) && transport.Handler.Contents.All(c => c.Disposed) && !transport.Handler.Disposed);
                    Check((await ((IContainerReader)connection).ListContainersAsync()).IsSuccess);
                }
            }
        }
    }

    internal static async Task ParametersAndFailures()
    {
        var requests = new List<(string Method, string Path)>();
        var transport = Transport((request, _) => { requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery)); return Task.FromResult(Response()); });
        await using var connection = await Connect(transport);
        var commands = (IContainerCommands)connection;
        await commands.StartContainerAsync("/sample.name-1");
        await commands.StopContainerAsync("target", 0);
        await commands.RestartContainerAsync("target", 60);
        await commands.DeleteContainerAsync("target");
        await commands.DeleteContainerAsync("target", force: true);
        Check(requests.SequenceEqual(new[]
        {
            ("POST", "/v1.56/containers/%2Fsample.name-1/start"), ("POST", "/v1.56/containers/target/stop?t=0"),
            ("POST", "/v1.56/containers/target/restart?t=60"), ("DELETE", "/v1.56/containers/target?force=false&v=false"),
            ("DELETE", "/v1.56/containers/target?force=true&v=false")
        }));
        foreach (var command in Enum.GetValues<ContainerCommand>())
        foreach (var status in new[] { 200, 202, 304, 400, 401, 403, 404, 409, 422, 500, 503 })
        {
            var failed = Transport((_, _) => Task.FromResult(Response("unmodified plain daemon text", status)));
            await using var owner = await Connect(failed);
            var result = await Send((IContainerCommands)owner, command);
            var ack = status == 304 && command is ContainerCommand.Start or ContainerCommand.Stop;
            Check(result.Disposition == (ack ? ContainerCommandDisposition.Acknowledged : ContainerCommandDisposition.DaemonResponse));
            Check(result.HttpStatusCode == status && result.DaemonMessage == "unmodified plain daemon text");
            if (!ack)
                Check(result.Failure!.Kind == (status == 404 ? ContainerCommandFailureKind.NotFound : status == 409 ? ContainerCommandFailureKind.Conflict
                    : status is 401 or 403 ? ContainerCommandFailureKind.AccessDenied : status >= 500 ? ContainerCommandFailureKind.DaemonFailure
                    : status >= 400 ? ContainerCommandFailureKind.InvalidRequest : ContainerCommandFailureKind.UnexpectedResponse));
            Check(failed.Handler.Paths.Count == 4, "Failures must never stop/escalate/retry implicitly");
        }
        foreach (var error in new Exception[] { new IOException("secret"), new HttpRequestException("secret"), new HttpRequestException("wrapped", new UnauthorizedAccessException()) })
        {
            var failed = Transport((_, _) => Task.FromException<HttpResponseMessage>(error));
            await using var owner = await Connect(failed);
            var result = await ((IContainerCommands)owner).DeleteContainerAsync("target");
            Check(result.Disposition == ContainerCommandDisposition.Uncertain && result.Failure!.Kind ==
                (error.InnerException is UnauthorizedAccessException ? ContainerCommandFailureKind.AccessDenied : ContainerCommandFailureKind.TransportFailure));
            Check(!result.Failure!.Description.Contains("secret") && failed.Handler.Paths.Count == 4);
        }
    }

    internal static async Task BeforeDispatch()
    {
        var transport = Transport((_, _) => Task.FromResult(Response()));
        var owner = await Connect(transport);
        var commands = (IContainerCommands)owner;
        foreach (var identity in new[] { "", " ", ".", "..", "/", "//name", "a/b", "x?force=true", "x#fragment", "x%2Fname", "x\\y", "x\r\n", "x\0", "ü", "\ud800", new string('a', 4097) })
        {
            Check((await commands.StartContainerAsync(identity)).Disposition == ContainerCommandDisposition.NotDispatched);
            Check((await commands.DeleteContainerAsync(identity, true)).Failure?.Kind == ContainerCommandFailureKind.InvalidRequest);
        }
        Check((await commands.StopContainerAsync("target", -1)).Failure?.Kind == ContainerCommandFailureKind.InvalidRequest);
        Check((await commands.RestartContainerAsync("target", -2)).Disposition == ContainerCommandDisposition.NotDispatched);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        foreach (var command in Enum.GetValues<ContainerCommand>())
        {
            var outcome = await Canceled(Send(commands, command, cancel.Token));
            Check(outcome.Disposition == ContainerCommandDisposition.NotDispatched && !outcome.RequiresReconciliation);
        }
        Check(transport.Handler.Paths.Count == 3);
        await owner.DisposeAsync();
        Check((await commands.StartContainerAsync("target")).Failure?.Kind == ContainerCommandFailureKind.ConnectionDisposed);
        Check(transport.Handler.Paths.Count == 3 && transport.Handler.DisposeCount == 1);
        var continuity = new ContinuityTestTransport();
        await using var stale = await Connect(continuity);
        continuity.Counter.Opened();
        var result = await ((IContainerCommands)stale).DeleteContainerAsync("target");
        Check(result.Disposition == ContainerCommandDisposition.NotDispatched && result.Failure?.Kind == ContainerCommandFailureKind.StaleConnection);
        Check(continuity.Handler.Paths.Count == 3);
    }

    internal static async Task UncertainLifetimes()
    {
        foreach (var command in Enum.GetValues<ContainerCommand>())
        foreach (var mode in new[] { "cancel", "deadline", "dispose", "wrapped-cancel", "lost-response", "continuity" })
        foreach (var body in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new StalledStream();
            var counter = new PipeConnectionCounter(); counter.Opened();
            var applied = 0;
            var ordinary = Transport(async (_, token) =>
            {
                Interlocked.Increment(ref applied);
                if (body) return new(HttpStatusCode.InternalServerError) { Content = new StreamContent(stream) };
                started.TrySetResult();
                if (mode == "lost-response") throw new IOException("response lost after applying command");
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (mode == "wrapped-cancel") { throw new HttpRequestException("wrapped"); }
                return Response();
            });
            var transport = new GuardedCommandTransport(ordinary.Handler, counter);
            var owner = await Connect(transport, new() { CommandTimeout = TimeSpan.FromMilliseconds(100), GracefulCommandTimeout = TimeSpan.FromMilliseconds(100) });
            var pending = Send((IContainerCommands)owner, command, cancel.Token);
            await (body ? stream.Started.Task : started.Task).WaitAsync(TimeSpan.FromSeconds(3));
            ContainerCommandResult result;
            if (mode is "cancel" or "wrapped-cancel") { cancel.Cancel(); result = await Canceled(pending); }
            else
            {
                if (mode == "dispose") await owner.DisposeAsync();
                if (mode == "continuity") counter.Opened();
                result = await pending;
            }
            Check(result.Disposition == ContainerCommandDisposition.Uncertain && result.RequiresReconciliation);
            Check(!body || result.HttpStatusCode == 500);
            Check(applied == 1 && ordinary.Handler.Paths.Count == 4, "Uncertain mutation cannot be replayed");
            Check(!body || stream.Disposed);
            Check(ordinary.Handler.Disposed == (mode == "dispose"));
            if (mode is "cancel" or "wrapped-cancel") Check(result.Failure?.Kind == ContainerCommandFailureKind.Canceled);
            if (mode == "deadline" || mode == "lost-response" && body) Check(result.Failure?.Kind == ContainerCommandFailureKind.Deadline);
            if (mode == "dispose") Check(result.Failure?.Kind == ContainerCommandFailureKind.ConnectionDisposed);
            if (mode == "continuity") Check(result.Failure?.Kind == ContainerCommandFailureKind.StaleConnection);
            if (mode == "lost-response" && !body) Check(result.Failure?.Kind == ContainerCommandFailureKind.TransportFailure);
            if (mode is "cancel" or "wrapped-cancel" or "deadline" or "lost-response")
                Check((await ((IContainerReader)owner).ListContainersAsync()).IsSuccess, "Borrowed commands must retain the connection");
            await owner.DisposeAsync();
            Check(ordinary.Handler.DisposeCount == 1);
        }
    }

    internal static async Task BoundsAndGracefulLifetime()
    {
        foreach (var known in new[] { false, true })
        foreach (var status in new[] { 400, 404, 409, 500 })
        foreach (var length in new[] { 64, 65, 1000000 })
        {
            var message = new string('x', length - 14); // JSON overhead is 14 bytes.
            var json = "{\"message\":\"" + message + "\"}";
            Check(Encoding.UTF8.GetByteCount(json) == length);
            HttpContent content = known ? new TrackedContent(json) : new UnknownLengthContent(json);
            var transport = Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
            await using var owner = await Connect(transport, new() { MaximumCommandErrorBytes = 64 });
            var result = await ((IContainerCommands)owner).DeleteContainerAsync("target");
            Check(result.Disposition == ContainerCommandDisposition.DaemonResponse && result.HttpStatusCode == status);
            Check(result.DaemonMessage == (length == 64 ? message : null) && result.ErrorBodyUnavailable == (length > 64));
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
        var counted = new CountedErrorStream();
        var unbounded = Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(counted) }));
        await using (var bounded = await Connect(unbounded, new() { MaximumCommandErrorBytes = 64 }))
        {
            var failure = await ((IContainerCommands)bounded).DeleteContainerAsync("target");
            Check(failure.Failure?.Kind == ContainerCommandFailureKind.Conflict && failure.ErrorBodyUnavailable);
            Check(counted.BytesRead == 65 && counted.Disposed, "Error streams must read at most budget+1, then release");
        }
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = Transport(async (_, token) => { stalled.TrySetResult(); await Task.Delay(150, token); return Response(); });
        await using var connection = await Connect(slow, new() { RequestTimeout = TimeSpan.FromMilliseconds(30),
            CommandTimeout = TimeSpan.FromMilliseconds(40), GracefulCommandTimeout = TimeSpan.FromSeconds(1), MaximumConcurrentCommands = 1 });
        var commands = (IContainerCommands)connection;
        var stopping = commands.StopContainerAsync("target", 10);
        await stalled.Task;
        var overload = await commands.DeleteContainerAsync("target");
        Check(overload.Disposition == ContainerCommandDisposition.NotDispatched && overload.Failure?.Kind == ContainerCommandFailureKind.CapacityExceeded);
        Check((await stopping).Disposition == ContainerCommandDisposition.Acknowledged);
        Check((await commands.RestartContainerAsync("target", 10)).Disposition == ContainerCommandDisposition.Acknowledged);
        Check((await commands.StartContainerAsync("target")).Failure?.Kind == ContainerCommandFailureKind.Deadline);
        Check((await ((IContainerReader)connection).ListContainersAsync()).IsSuccess);
    }

    internal static async Task NamedPipeNoReplay()
    {
        foreach (var loseAfterMutation in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var name = "cdd-command-" + Guid.NewGuid().ToString("N")[..8];
            using var first = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            int mutations = 0;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Original()
            {
                await first.WaitForConnectionAsync(deadline.Token);
                for (int n = 0; n < 3; n++)
                {
                    var request = await ReadRequest(first, deadline.Token);
                    Check(request.StartsWith("GET "));
                    await WriteResponse(first, Handshake(n), 200, deadline.Token, close: n == 2 && !loseAfterMutation);
                }
                if (loseAfterMutation)
                {
                    var command = await ReadRequest(first, deadline.Token);
                    Check(command.StartsWith("POST /v1.56/containers/target/start HTTP/1.1"));
                    Interlocked.Increment(ref mutations); // Mutation applied, response deliberately lost.
                }
                first.Dispose(); closed.TrySetResult();
            }
            var serving = Original();
            var factory = new NamedPipeTransport(permitTestHost: true);
            var endpoint = new EngineEndpoint("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe);
            var attempt = await new DockerEngineConnectionClient(factory).ConnectAsync(endpoint, deadline.Token);
            Check(attempt.IsConnected, attempt.Failure?.Description ?? "Pipe connection failed");
            using var replacement = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var replacementConnected = replacement.WaitForConnectionAsync(deadline.Token);
            await using var connection = attempt.Connection!;
            var result = await ((IContainerCommands)connection).StartContainerAsync("target", deadline.Token);
            await serving; await closed.Task;
            Check(result.Disposition == ContainerCommandDisposition.Uncertain);
            Check(mutations == (loseAfterMutation ? 1 : 0));
            Check(!replacementConnected.IsCompleted, "No replacement pipe may receive an implicit mutation replay");
            var stale = await ((IContainerCommands)connection).DeleteContainerAsync("target");
            Check(stale.Disposition == ContainerCommandDisposition.NotDispatched && stale.Failure?.Kind == ContainerCommandFailureKind.StaleConnection);
            Check((await ((IContainerReader)connection).ListContainersAsync()).Failure?.Kind == EngineResourceFailureKind.TransportLoss);
            // Only an explicit fresh connection can reach replacement; reconcile the fixture's actual applied state.
            async Task NewDaemon()
            {
                await replacementConnected;
                for (int n = 0; n < 4; n++)
                {
                    var request = await ReadRequest(replacement, deadline.Token);
                    Check(request.StartsWith("GET "), "Fresh reconciliation must never repeat mutation");
                    await WriteResponse(replacement, n < 3 ? Handshake(n) : "{\"Id\":\"target\",\"State\":{\"Status\":\"" + (mutations == 1 ? "running" : "exited") + "\"}}", 200, deadline.Token);
                }
                var one = new byte[1];
                try { Check(await replacement.ReadAsync(one, deadline.Token) == 0); } catch (IOException) { }
            }
            var recovery = NewDaemon();
            var fresh = await new DockerEngineConnectionClient(factory).ConnectAsync(endpoint, deadline.Token);
            Check(fresh.IsConnected);
            var inspect = await ((IContainerReader)fresh.Connection!).InspectContainerAsync("target", deadline.Token);
            Check(inspect.Value?.State?.Status == (mutations == 1 ? "running" : "exited"));
            await fresh.Connection!.DisposeAsync(); await recovery;
        }
    }

    internal static async Task NamedPipeIssuedCancellation()
    {
        foreach (var mode in new[] { "cancel", "deadline", "dispose" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var caller = new CancellationTokenSource();
            var name = "cdd-command-" + Guid.NewGuid().ToString("N")[..8];
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Serve()
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                for (int n = 0; n < 3; n++) { await ReadRequest(pipe, deadline.Token); await WriteResponse(pipe, Handshake(n), 200, deadline.Token); }
                Check((await ReadRequest(pipe, deadline.Token)).StartsWith("POST /v1.56/containers/target/stop?t=20"));
                issued.TrySetResult();
                // Response is stalled after issuance; client must close the active request stream on every termination path.
                var one = new byte[1];
                try { Check(await pipe.ReadAsync(one, deadline.Token) == 0); } catch (IOException) { }
            }
            var serving = Serve();
            var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true),
                new() { GracefulCommandTimeout = TimeSpan.FromMilliseconds(150) }).ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token);
            Check(attempt.IsConnected); await using var owner = attempt.Connection!;
            var pending = ((IContainerCommands)owner).StopContainerAsync("target", 20, caller.Token);
            await issued.Task.WaitAsync(deadline.Token);
            ContainerCommandResult result;
            if (mode == "cancel") { caller.Cancel(); result = await Canceled(pending); }
            else { if (mode == "dispose") await owner.DisposeAsync(); result = await pending; }
            Check(result.Disposition == ContainerCommandDisposition.Uncertain);
            Check(result.Failure?.Kind == (mode == "cancel" ? ContainerCommandFailureKind.Canceled : mode == "deadline"
                ? ContainerCommandFailureKind.Deadline : ContainerCommandFailureKind.ConnectionDisposed));
            await serving;
        }
    }

    private static async Task<string> ReadRequest(Stream pipe, CancellationToken token)
    {
        var headers = new StringBuilder(); var one = new byte[1];
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Check(headers.Length < 8192 && await pipe.ReadAsync(one, token) == 1, "Incomplete fixture request");
            headers.Append((char)one[0]);
        }
        return headers.ToString();
    }
    private static async Task WriteResponse(Stream pipe, string body, int status, CancellationToken token, bool close = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await pipe.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} fixture\r\nContent-Length: {bytes.Length}\r\n" + (close ? "Connection: close\r\n" : "") + "\r\n"), token);
        await pipe.WriteAsync(bytes, token); await pipe.FlushAsync(token);
    }
}

internal sealed class GuardedCommandTransport(HttpMessageHandler handler, PipeConnectionCounter counter) : IDockerTransportFactory
{
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options) => new ContinuityHandler(handler, counter);
}

internal sealed class CountedErrorStream : Stream
{
    public int BytesRead { get; private set; }
    public bool Disposed { get; private set; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        buffer.Span.Fill((byte)'x'); BytesRead += buffer.Length; return ValueTask.FromResult(buffer.Length);
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
