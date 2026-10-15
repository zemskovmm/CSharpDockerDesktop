using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class ExecResizeChecks
{
    private static readonly ContainerExecInstance Instance = new(new('a', 64), new('b', 64));
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(6);
    private static string Inspection => $$$"""{"ID":"{{{Instance.ExecId}}}","ContainerID":"{{{Instance.ContainerId}}}","ProcessConfig":{"tty":true}}""";
    private static string Handshake(int n, int minor) => n switch
    { 0 => "OK", 1 => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""", _ => "{}" };
    private static void Check(bool yes, string message = "Exec resize assertion failed") { if (!yes) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body = "", int status = 200) => new((HttpStatusCode)status) { Content = new TrackedContent(body) };
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action, int minor = 56) : IDockerTransportFactory
    {
        internal PipeConnectionCounter Counter { get; } = new();
        internal TrackingHandler Handler { get; } = new((r, n, t) => n < 3 ? Task.FromResult(Response(Handshake(n, minor))) : action(r, t));
        public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
        { Counter.Opened(); return new ContinuityHandler(Handler, Counter); }
    }
    private static Transport Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? resize = null, int minor = 56)
        => new((r, t) => r.RequestUri!.AbsolutePath.EndsWith("/resize") ? (resize?.Invoke(r, t) ?? Task.FromResult(Response()))
            : Task.FromResult(Response(r.RequestUri.AbsolutePath.Contains("/exec/") ? Inspection : "[]")), minor);
    private static async Task<IEngineConnection> Connect(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(attempt.IsConnected); return attempt.Connection!;
    }
    private static async Task<ContainerExecResizeResult> Canceled(Task<ContainerExecResizeResult> task, CancellationToken token)
    {
        try { await task.WaitAsync(Limit); throw new InvalidOperationException("Expected resize cancellation"); }
        catch (ContainerExecResizeCanceledException ex) { Check(task.IsCanceled && ex.CancellationToken == token); return ex.Outcome; }
    }

    internal static async Task OfficialContractsAndDimensions()
    {
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/Exec/resize-contract-evidence.json")));
        var versions = source.RootElement.GetProperty("versions").EnumerateArray().ToArray(); Check(versions.Length == 17);
        foreach (var version in versions)
        {
            int minor = int.Parse(version.GetProperty("api").GetString()!.Split('.')[1], CultureInfo.InvariantCulture);
            Check(version.GetProperty("operationId").GetString() == "ExecResize");
            var parameters = version.GetProperty("parameters").EnumerateArray().ToArray();
            Check(parameters.Length == 3);
            foreach (var (name, place, type) in new[] { ("id", "path", "string"), ("h", "query", "integer"), ("w", "query", "integer") })
            {
                var parameter = parameters.Single(p => p.GetProperty("name").GetString() == name);
                Check(parameter.GetProperty("in").GetString() == place && parameter.GetProperty("type").GetString() == type && parameter.GetProperty("required").GetBoolean());
            }
            var responses = version.GetProperty("responses");
            Check(responses.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "200", "400", "404", "500" }));
            Check(!responses.GetProperty("200").TryGetProperty("schema", out _));
            Check(version.GetProperty("errorSchema").GetProperty("properties").GetProperty("message").GetProperty("type").GetString() == "string");
            foreach (var (columns, rows) in new[] { (132, 37), (1, 65535), (65535, 1) })
            {
                var transport = Fixture((r, _) =>
                {
                    Check(r.Method == HttpMethod.Post && r.Content is null && r.Version == new Version(1, 1) && r.VersionPolicy == HttpVersionPolicy.RequestVersionExact);
                    Check(r.RequestUri!.PathAndQuery == $"/v1.{minor}/exec/{Instance.ExecId}/resize?h={rows}&w={columns}");
                    return Task.FromResult(Response());
                }, minor);
                await using var owner = await Connect(transport);
                var original = CultureInfo.CurrentCulture;
                ContainerExecResizeResult result;
                try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA"); result = await ((IContainerExec)owner).ResizeTtyExecAsync(Instance, columns, rows); }
                finally { CultureInfo.CurrentCulture = original; }
                Check(result.IsAcknowledged && result.AcknowledgementObserved && result.RequestedInstance == Instance && result.Columns == columns && result.Rows == rows);
                Check(result.Response is { HttpStatusCode: 200, BodyUnavailable: false } && result.Response.RawBody.IsEmpty && result.BindingResponse?.HttpStatusCode == 200);
                Check(transport.Handler.Paths.Skip(3).SequenceEqual(new[] { $"/v1.{minor}/exec/{Instance.ExecId}/json", $"/v1.{minor}/exec/{Instance.ExecId}/resize" }));
                Check((await ((IContainerReader)owner).ListContainersAsync()).IsSuccess && !transport.Handler.Disposed);
                Check(!result.ToString().Contains(Instance.ExecId) && !result.BindingResponse!.ToString().Contains(Instance.ContainerId));
            }
        }
    }

    internal static async Task ValidationBindingAndErrors()
    {
        var transport = Fixture(); await using var owner = await Connect(transport); var exec = (IContainerExec)owner;
        foreach (var instance in new[] { null, new ContainerExecInstance("short", Instance.ExecId), new(Instance.ContainerId, "bad/exec"), new(new('A', 64), Instance.ExecId), new(Instance.ContainerId, new('g', 64)), new(null!, Instance.ExecId), new(Instance.ContainerId, null!) })
        {
            var result = await exec.ResizeTtyExecAsync(instance!, 80, 24);
            Check(result.Disposition == ContainerExecResizeDisposition.NotDispatched && result.Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
        }
        foreach (var (columns, rows) in new[] { (0, 24), (80, 0), (-1, 1), (1, -1), (65536, 1), (1, 65536), (int.MaxValue, 1), (1, int.MinValue) })
            Check((await exec.ResizeTtyExecAsync(Instance, columns, rows)).Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check((await Canceled(exec.ResizeTtyExecAsync(Instance, 80, 24, canceled.Token), canceled.Token)).Disposition == ContainerExecResizeDisposition.NotDispatched);
        Check(transport.Handler.Paths.Count == 3);

        foreach (var body in new[] { "{}", Inspection.Replace(Instance.ContainerId, new string('c', 64)), Inspection.Replace(Instance.ExecId, new string('d', 64)),
            Inspection.Replace("true", "false"), Inspection.Replace("true", "null"), "{\"ID\":\"short\"}", "{", "[]" })
        {
            var fixture = new Transport((_, _) => Task.FromResult(Response(body)));
            await using var bindingOwner = await Connect(fixture);
            var result = await ((IContainerExec)bindingOwner).ResizeTtyExecAsync(Instance, 80, 24);
            Check(result.Disposition == ContainerExecResizeDisposition.NotDispatched && !result.AcknowledgementObserved && result.Response is null && result.BindingResponse is not null);
            Check(fixture.Handler.Paths.Count == 4);
        }
        foreach (var (status, kind) in new[] { (400, ContainerExecFailureKind.InvalidRequest), (401, ContainerExecFailureKind.AccessDenied),
            (403, ContainerExecFailureKind.AccessDenied), (404, ContainerExecFailureKind.NotFound), (409, ContainerExecFailureKind.Conflict), (500, ContainerExecFailureKind.DaemonFailure) })
        foreach (var body in new[] { "{\"message\":\"sensitive daemon text\"}", "sensitive raw text", "", "[1]" })
        {
            await using var errorOwner = await Connect(Fixture((_, _) => Task.FromResult(Response(body, status))));
            var result = await ((IContainerExec)errorOwner).ResizeTtyExecAsync(Instance, 80, 24);
            Check(result.Disposition == ContainerExecResizeDisposition.DaemonRejected && result.Failure?.Kind == kind && !result.IsAcknowledged);
            Check(result.Response?.HttpStatusCode == status && !result.Response.BodyUnavailable && Encoding.UTF8.GetString(result.Response.RawBody.AsSpan()) == body);
            if (body.StartsWith("{", StringComparison.Ordinal)) Check(result.Response!.DaemonMessage == "sensitive daemon text");
            Check(!result.ToString().Contains("sensitive") && !result.Response!.ToString().Contains("sensitive"));
        }
        foreach (int status in new[] { 200, 201, 204, 302 })
        {
            await using var unexpectedOwner = await Connect(Fixture((_, _) => Task.FromResult(Response("unexpected body", status))));
            var result = await ((IContainerExec)unexpectedOwner).ResizeTtyExecAsync(Instance, 80, 24);
            Check(result.Disposition == ContainerExecResizeDisposition.Uncertain && result.Failure?.Kind == (status == 200 ? ContainerExecFailureKind.MalformedResponse : ContainerExecFailureKind.UnexpectedResponse));
            Check(result.AcknowledgementObserved == (status == 200));
        }
        foreach (bool knownLength in new[] { false, true })
        foreach (int status in new[] { 200, 400 })
        {
            HttpContent content = knownLength ? new TrackedContent(new string('x', 257)) : new UnknownLengthContent(new string('x', 257));
            await using var boundedOwner = await Connect(Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content })),
                new() { MaximumCommandErrorBytes = 64, MaximumExecResponseBytes = 256 });
            var result = await ((IContainerExec)boundedOwner).ResizeTtyExecAsync(Instance, 80, 24);
            Check(result.Response!.BodyUnavailable && result.Response.RawBody.IsEmpty);
            Check(result.Disposition == (status == 400 ? ContainerExecResizeDisposition.DaemonRejected : ContainerExecResizeDisposition.Uncertain));
        }
        byte[] invalidUtf8 = [0xff, 0x00, 0x1b];
        await using (var rawOwner = await Connect(Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new ByteArrayContent(invalidUtf8) }))))
        {
            var result = await ((IContainerExec)rawOwner).ResizeTtyExecAsync(Instance, 80, 24);
            Check(result.Response!.RawBody.SequenceEqual(invalidUtf8) && result.Response.DaemonMessage is null);
        }
    }

    internal static async Task AdmissionAndCooperativeLifetimes()
    {
        foreach (var ending in new[] { "cancel", "deadline", "dispose", "generation" })
        foreach (int status in new[] { 200, 409 })
        {
            using var caller = new CancellationTokenSource();
            var body = new StalledStream();
            var transport = Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) }));
            var owner = await Connect(transport, new() { CommandTimeout = ending == "deadline" ? TimeSpan.FromMilliseconds(100) : Limit });
            var pending = ((IContainerExec)owner).ResizeTtyExecAsync(Instance, 132, 37, caller.Token);
            await body.Started.Task.WaitAsync(Limit);
            if (ending == "cancel") caller.Cancel();
            if (ending == "generation") transport.Counter.Opened();
            Task? disposal = ending == "dispose" ? owner.DisposeAsync().AsTask() : null;
            var result = ending == "cancel" ? await Canceled(pending, caller.Token) : await pending.WaitAsync(Limit);
            Check(result.Disposition == (status == 200 ? ContainerExecResizeDisposition.Uncertain : ContainerExecResizeDisposition.DaemonRejected));
            Check(result.Response is { BodyUnavailable: true } && result.Response.HttpStatusCode == status && result.AcknowledgementObserved == (status == 200));
            Check(!result.IsAcknowledged && body.Disposed);
            if (disposal is not null) await disposal.WaitAsync(Limit);
            await owner.DisposeAsync();
        }
        foreach (var ending in new[] { "cancel", "deadline", "dispose", "generation" })
        foreach (var stage in new[] { "binding", "resize" })
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new Transport(async (r, token) =>
            {
                bool resize = r.RequestUri!.AbsolutePath.EndsWith("/resize");
                if (resize || stage == "binding")
                {
                    using var registration = token.Register(() => expired.TrySetResult()); entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Response(Inspection);
            });
            var owner = await Connect(transport, new() { CommandTimeout = ending == "deadline" ? TimeSpan.FromMilliseconds(120) : Limit });
            var pending = ((IContainerExec)owner).ResizeTtyExecAsync(Instance, 132, 37, caller.Token);
            await entered.Task.WaitAsync(Limit);
            if (ending == "cancel") caller.Cancel();
            if (ending == "generation") transport.Counter.Opened();
            Task? disposal = ending == "dispose" ? owner.DisposeAsync().AsTask() : null;
            await expired.Task.WaitAsync(Limit);
            var result = ending == "cancel" ? await Canceled(pending, caller.Token) : await pending.WaitAsync(Limit);
            Check(result.Disposition == (stage == "binding" ? ContainerExecResizeDisposition.NotDispatched : ContainerExecResizeDisposition.Uncertain));
            var expected = ending == "cancel" ? ContainerExecFailureKind.Canceled : ending == "deadline" ? ContainerExecFailureKind.Deadline
                : ending == "dispose" ? ContainerExecFailureKind.ConnectionDisposed : ContainerExecFailureKind.StaleConnection;
            Check(result.Failure?.Kind == expected && !result.IsAcknowledged && !result.AcknowledgementObserved);
            if (disposal is not null) await disposal.WaitAsync(Limit);
            await owner.DisposeAsync(); Check(transport.Handler.DisposeCount == 1);
        }
        foreach (var occupiedKind in new[] { "resize", "create", "container", "image", "volume", "network" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new Transport(async (r, token) =>
            {
                var path = r.RequestUri!.AbsolutePath;
                if (r.Method == HttpMethod.Get) return Response(path.Contains("/exec/") ? Inspection : "[]");
                entered.TrySetResult(); await release.Task.WaitAsync(token);
                return path.EndsWith("/exec") ? Response($$"""{"Id":"{{Instance.ExecId}}"}""", 201)
                    : path.EndsWith("/resize") ? Response() : Response(occupiedKind == "image" ? "[]" : "", occupiedKind == "image" ? 200 : 204);
            });
            await using var owner = await Connect(transport, new() { MaximumConcurrentCommands = 1 }); var exec = (IContainerExec)owner;
            Task occupied = occupiedKind switch
            {
                "resize" => exec.ResizeTtyExecAsync(Instance, 80, 24), "create" => exec.CreateTtyExecAsync(new(Instance.ContainerId, ["command"])),
                "container" => ((IContainerCommands)owner).StartContainerAsync("target"), "image" => ((IImageCommands)owner).DeleteImageAsync("repo"),
                "volume" => ((IVolumeCommands)owner).DeleteVolumeAsync("volume"), _ => ((INetworkCommands)owner).DeleteNetworkAsync("network")
            };
            await entered.Task.WaitAsync(Limit);
            var rejected = await exec.ResizeTtyExecAsync(Instance, 90, 25);
            Check(rejected.Disposition == ContainerExecResizeDisposition.NotDispatched && rejected.Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
            Check((await exec.InspectExecAsync(Instance)).IsSuccess && (await ((IContainerReader)owner).ListContainersAsync()).IsSuccess);
            Check((await exec.CreateTtyExecAsync(new(Instance.ContainerId, ["command"]))).Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
            release.TrySetResult(); await occupied;
            Check((await exec.ResizeTtyExecAsync(Instance, 90, 25)).IsAcknowledged);
        }
        var stale = Fixture(); var staleOwner = await Connect(stale); stale.Counter.Opened();
        Check((await ((IContainerExec)staleOwner).ResizeTtyExecAsync(Instance, 80, 24)).Failure?.Kind == ContainerExecFailureKind.StaleConnection && stale.Handler.Paths.Count == 3);
        await staleOwner.DisposeAsync();
        Check((await ((IContainerExec)staleOwner).ResizeTtyExecAsync(Instance, 80, 24)).Failure?.Kind == ContainerExecFailureKind.ConnectionDisposed && stale.Handler.Paths.Count == 3);
    }

    internal static async Task LateEvidenceAndJoinedDisposal()
    {
        foreach (var ending in new[] { "cancel", "deadline", "dispose", "generation" })
        foreach (var stage in new[] { "binding-headers", "binding-body", "headers", "body", "cleanup" })
        foreach (int status in new[] { 200, 409 })
        {
            // Binding uses the successful inspection contract; rejection variants concern resize only.
            if (stage.StartsWith("binding", StringComparison.Ordinal) && status != 200) continue;
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool bindingStage = stage.StartsWith("binding", StringComparison.Ordinal);
            var late = new LateStream(Encoding.UTF8.GetBytes(bindingStage ? Inspection : status == 200 ? "" : "{\"message\":\"sensitive late rejection\"}"),
                stage.EndsWith("body", StringComparison.Ordinal) ? release.Task : Task.CompletedTask, entered);
            Transport? transport = null;
            void End()
            {
                if (ending == "cancel") caller.Cancel();
                if (ending == "generation") transport!.Counter.Opened();
            }
            transport = new Transport(async (r, token) =>
            {
                bool resize = r.RequestUri!.AbsolutePath.EndsWith("/resize");
                if (!resize && !bindingStage) return Response(Inspection);
                token.Register(() => expired.TrySetResult());
                if (stage.EndsWith("headers", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task; }
                late.Reading = t => t.Register(() => expired.TrySetResult());
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(late) };
            });
            var owner = await Connect(transport, new() { CommandTimeout = ending == "deadline" ? TimeSpan.FromMilliseconds(100) : Limit });
            if (stage == "cleanup") late.Cleanup = () =>
            {
                entered.TrySetResult(); End();
                if (ending == "dispose") _ = owner.DisposeAsync();
                if (ending == "deadline") expired.Task.GetAwaiter().GetResult();
            };
            var pending = ((IContainerExec)owner).ResizeTtyExecAsync(Instance, 132, 37, caller.Token);
            await entered.Task.WaitAsync(Limit);
            if (stage != "cleanup") End();
            Task? disposal = ending == "dispose" ? owner.DisposeAsync().AsTask() : null;
            if (stage != "cleanup")
            {
                await expired.Task.WaitAsync(Limit);
                Check(!pending.IsCompleted && (disposal is null || !disposal.IsCompleted), "Noncooperative admitted resize must remain joined");
                release.TrySetResult();
            }
            var result = ending == "cancel" ? await Canceled(pending, caller.Token) : await pending.WaitAsync(Limit);
            Check(result.Disposition == (bindingStage ? ContainerExecResizeDisposition.NotDispatched
                : status == 409 ? ContainerExecResizeDisposition.DaemonRejected : ContainerExecResizeDisposition.Uncertain));
            var expected = ending == "cancel" ? ContainerExecFailureKind.Canceled : ending == "deadline" ? ContainerExecFailureKind.Deadline
                : ending == "dispose" ? ContainerExecFailureKind.ConnectionDisposed : ContainerExecFailureKind.StaleConnection;
            Check(result.Failure?.Kind == expected && !result.IsAcknowledged && result.AcknowledgementObserved == (!bindingStage && status == 200));
            var evidence = bindingStage ? result.BindingResponse : result.Response;
            Check(evidence is { BodyUnavailable: false } && late.Disposed);
            if (status == 409) Check(evidence!.DaemonMessage == "sensitive late rejection");
            Check(transport.Handler.Paths.Count == (bindingStage ? 4 : 5));
            if (disposal is not null) { Check(ReferenceEquals(disposal, owner.DisposeAsync().AsTask())); await disposal.WaitAsync(Limit); }
            await owner.DisposeAsync(); Check(transport.Handler.DisposeCount == 1);
        }
    }

    internal static async Task NamedPipeOutcomesAndNoReplay()
    {
        foreach (var mode in new[] { "ack", "closed-before", "lost-after", "cancel", "deadline", "dispose" })
        {
            using var limit = new CancellationTokenSource(Limit); using var caller = new CancellationTokenSource();
            var name = "cdd-resize-" + Guid.NewGuid().ToString("N")[..8];
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int applied = 0;
            async Task Serve()
            {
                await pipe.WaitForConnectionAsync(limit.Token);
                for (int n = 0; n < 4; n++)
                {
                    var request = await ReadRequest(pipe, limit.Token); Check(request.StartsWith("GET ", StringComparison.Ordinal));
                    await Send(pipe, n < 3 ? Handshake(n, 56) : Inspection, limit.Token, close: n == 3 && mode == "closed-before");
                }
                if (mode == "closed-before") { pipe.Dispose(); return; }
                var resize = await ReadRequest(pipe, limit.Token);
                Check(resize.StartsWith($"POST /v1.56/exec/{Instance.ExecId}/resize?h=37&w=132 HTTP/1.1\r\n", StringComparison.Ordinal));
                Check(!resize.Contains("Upgrade:", StringComparison.OrdinalIgnoreCase));
                applied++; issued.TrySetResult();
                if (mode == "ack") { await Send(pipe, "", limit.Token); return; }
                if (mode == "lost-after") { pipe.Dispose(); return; }
                try { Check(await pipe.ReadAsync(new byte[1], limit.Token) == 0); } catch (IOException) { }
            }
            var serving = Serve();
            var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true), new() { CommandTimeout = TimeSpan.FromMilliseconds(180) })
                .ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), limit.Token);
            Check(attempt.IsConnected); await using var owner = attempt.Connection!;
            using var replacement = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var replacementConnected = replacement.WaitForConnectionAsync(limit.Token);
            var pending = ((IContainerExec)owner).ResizeTtyExecAsync(Instance, 132, 37, caller.Token);
            if (mode is "cancel" or "deadline" or "dispose") await issued.Task.WaitAsync(Limit);
            if (mode == "cancel") caller.Cancel();
            if (mode == "dispose") await owner.DisposeAsync().AsTask().WaitAsync(Limit);
            var result = mode == "cancel" ? await Canceled(pending, caller.Token) : await pending.WaitAsync(Limit);
            await serving.WaitAsync(Limit);
            Check(result.Disposition == (mode == "ack" ? ContainerExecResizeDisposition.Acknowledged : ContainerExecResizeDisposition.Uncertain));
            Check(applied == (mode == "closed-before" ? 0 : 1) && !replacementConnected.IsCompleted, "No replacement transport or resize replay is permitted");
            if (mode is "cancel" or "deadline" or "dispose") Check(result.Failure?.Kind == (mode == "cancel" ? ContainerExecFailureKind.Canceled
                : mode == "deadline" ? ContainerExecFailureKind.Deadline : ContainerExecFailureKind.ConnectionDisposed));
        }
    }
    private static async Task<string> ReadRequest(Stream pipe, CancellationToken token)
    {
        var header = new StringBuilder(); var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        { Check(header.Length < 8192 && await pipe.ReadAsync(one, token) == 1); header.Append((char)one[0]); }
        return header.ToString();
    }
    private static async Task Send(Stream pipe, string body, CancellationToken token, bool close = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await pipe.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\n{(close ? "Connection: close\r\n" : "")}\r\n"), token);
        await pipe.WriteAsync(bytes, token); await pipe.FlushAsync(token);
    }
    private sealed class LateStream(byte[] bytes, Task release, TaskCompletionSource entered) : Stream
    {
        private bool read;
        internal Action? Cleanup;
        internal Action<CancellationToken>? Reading;
        internal bool Disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) return 0;
            Reading?.Invoke(cancellationToken); entered.TrySetResult(); await release;
            bytes.CopyTo(buffer); read = true; return bytes.Length;
        }
        protected override void Dispose(bool disposing) { if (disposing && !Disposed) { Disposed = true; Cleanup?.Invoke(); } base.Dispose(disposing); }
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
}
