using System.Collections.Immutable;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class ExecAttachmentChecks
{
    private static readonly ContainerExecInstance Instance = new(new('a', 64), new('b', 64));
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(6);
    private const string Upgrade = "HTTP/1.1 101 UPGRADED\r\nContent-Type: application/vnd.docker.raw-stream\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n";
    private static readonly byte[] Terminal = [0xe2, 0x82, 0xac, 0xf0, 0x9f, 0x99, 0x82, 0x1b, 0x5b, 0x33, 0x31, 0x6d, 0x00, 0x03, 0xff, 0x0d, 0x0a];
    private static string Version(int minor) => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64"}""";
    private const string Info = "{\"ID\":\"engine-fixture\",\"OSType\":\"linux\",\"ServerVersion\":\"fixture\"}";
    private static string Inspect => $$$"""{"ID":"{{{Instance.ExecId}}}","ContainerID":"{{{Instance.ContainerId}}}","Running":false,"OpenStdin":true,"OpenStdout":true,"OpenStderr":true,"ProcessConfig":{"tty":true}}""";
    private static byte[] Response(string body, int status = 200) => Response(Encoding.UTF8.GetBytes(body), status);
    private static byte[] Response(byte[] body, int status = 200) => Encoding.ASCII.GetBytes($"HTTP/1.1 {status} response\r\nContent-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
    private static byte[] Prefix(int minor = 56, string? info = null, string? inspection = null) => Response("OK").Concat(Response(Version(minor))).Concat(Response(info ?? Info)).Concat(Response(inspection ?? Inspect)).ToArray();
    private static byte[] Wire(string header = Upgrade, byte[]? output = null, int minor = 56) => Prefix(minor).Concat(Encoding.ASCII.GetBytes(header)).Concat(output ?? []).ToArray();
    private static void Check(bool yes, string message = "Exec attachment assertion failed") { if (!yes) throw new InvalidOperationException(message); }
    private static async Task<IEngineConnection> Connect(FixtureTransport transport, DockerConnectionOptions? options = null)
    {
        var attempt = await new DockerEngineConnectionClient(transport, options).ConnectAsync(); Check(attempt.IsConnected); return attempt.Connection!;
    }
    private static async Task<byte[]> Read(IContainerExecAttachment attachment, int length)
    {
        using var deadline = new CancellationTokenSource(Limit); var bytes = new List<byte>();
        while (bytes.Count < length && await attachment.Output.WaitToReadAsync(deadline.Token))
            while (attachment.Output.TryRead(out var chunk)) bytes.AddRange(chunk);
        Check(bytes.Count == length, $"Expected {length} bytes, got {bytes.Count}"); return bytes.ToArray();
    }
    private static async Task<ContainerExecStartResult> Canceled(Task<ContainerExecStartResult> task)
    {
        try { await task.WaitAsync(Limit); throw new InvalidOperationException("Cancellation was expected"); }
        catch (ContainerExecStartCanceledException ex) { Check(task.IsCanceled); return ex.Outcome; }
    }
    internal static async Task ContractsAndRawDuplex()
    {
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/Exec/start-contract-evidence.json")));
        var versions = source.RootElement.GetProperty("versions").EnumerateArray().ToArray(); Check(versions.Length == 17);
        foreach (var version in versions)
        {
            int minor = int.Parse(version.GetProperty("api").GetString()!.Split('.')[1]);
            var schema = version.GetProperty("body").GetProperty("properties");
            Check(schema.GetProperty("Detach").GetProperty("type").GetString() == "boolean" && schema.GetProperty("Tty").GetProperty("type").GetString() == "boolean");
            Check(version.GetProperty("statuses").EnumerateArray().Select(x => x.GetString()).Contains("200"));
            var raw = new FixtureStream(Wire(output: Terminal, minor: minor)) { Fragment = minor % 2 == 0 ? 1 : 4096 };
            var transport = new FixtureTransport(raw, minor);
            await using var owner = await Connect(transport, new() { RequestTimeout = TimeSpan.FromMilliseconds(50), ExecOpeningTimeout = Limit });
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance); Check(result.Disposition == ContainerExecStartDisposition.Established && result.UpgradeObserved);
            await using var attachment = result.Attachment!;
            Check((await Read(attachment, Terminal.Length)).SequenceEqual(Terminal), "Raw fragmented UTF-8/ANSI/control bytes must survive unchanged");
            var input = new byte[] { 3, 4, 0, 27, 91, 65, 0xe2, 0x82, 0xac, 13 };
            Check((await attachment.WriteAsync(input)).Disposition == ContainerExecWriteDisposition.Written);
            Check(raw.Input.SequenceEqual(input));
            Check(raw.Requests.Count == 5 && raw.Requests.Take(4).All(r => r.StartsWith("GET ")));
            Check(raw.Requests[4].StartsWith($"POST /v1.{minor}/exec/{Instance.ExecId}/start HTTP/1.1\r\n"));
            Check(raw.StartBody == "{\"Detach\":false,\"Tty\":true}" && raw.Requests[4].Contains("Upgrade: tcp\r\n"));
            Check((await ((IContainerExec)owner).InspectExecAsync(Instance)).IsSuccess);
            Check((await ((IContainerReader)owner).ListContainersAsync()).IsSuccess && transport.OpenCount == 1);
            raw.End(); Check((await attachment.Completion.WaitAsync(Limit)).Termination == ContainerExecAttachmentTermination.EndOfStream);
            Check(raw.Disposed && (await attachment.WriteAsync(input)).Disposition == ContainerExecWriteDisposition.NotWritten);
            Check(!result.ToString().Contains(Instance.ExecId));
        }
        // Coalesced final headers + TTY bytes must remain available after EOF and normal cleanup.
        var end = new FixtureStream(Wire(output: Terminal)) { Eof = true };
        await using (var owner = await Connect(new(end)))
        {
            var opened = await ((IContainerExec)owner).StartTtyExecAsync(Instance); var attachment = opened.Attachment!;
            await attachment.Completion.WaitAsync(Limit); Check((await Read(attachment, Terminal.Length)).SequenceEqual(Terminal));
            await attachment.DisposeAsync();
        }
    }
    internal static async Task RejectionsFramingAndContinuity()
    {
        foreach (int status in new[] { 400, 401, 403, 404, 409, 500 })
        {
            var body = new byte[] { 0xff, 0x00, 0x03, 0x1b };
            var raw = new FixtureStream(Prefix().Concat(Response(body, status)).ToArray()) { Eof = true };
            var transport = new FixtureTransport(raw); await using var owner = await Connect(transport);
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.DaemonRejected && result.Attachment is null);
            Check(result.Response!.HttpStatusCode == status && result.Response.RawBody.SequenceEqual(body) && !result.Response.BodyUnavailable);
            Check(raw.Disposed && transport.OpenCount == 1 && raw.Requests.Count(r => r.StartsWith("POST ")) == 1);
        }
        foreach (var header in new[] {
            Upgrade.Replace("101 UPGRADED", "200 OK"), Upgrade.Replace("Upgrade: tcp", "Upgrade: websocket"),
            Upgrade.Replace("Connection: Upgrade\r\n", ""), Upgrade.Replace("raw-stream", "multiplexed-stream"),
            Upgrade.Replace("\r\n\r\n", "\r\nContent-Length: 0\r\n\r\n"), Upgrade.Replace("\r\n\r\n", "\r\nTransfer-Encoding: chunked\r\n\r\n"),
            Upgrade.Replace("\r\n\r\n", "\r\nUpgrade: tcp\r\n\r\n"), "HTTP/1.0 101 OK\r\n\r\n", "HTTP/1.1 101 X\r\n Folded: x\r\n\r\n",
            "HTTP/1.1 101 X\r\nX: " + new string('a', 20000) + "\r\n\r\n", "HTTP/1.1 101 X\n\n" })
        {
            var raw = new FixtureStream(Wire(header)) { Eof = true }; await using var owner = await Connect(new(raw));
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.Uncertain && result.Failure?.Kind == ContainerExecFailureKind.MalformedResponse && raw.Disposed, $"Malformed header {header[..Math.Min(90, header.Length)]}: {result}, disposed {raw.Disposed}");
        }
        foreach (var prefix in new[] { Prefix(info: Info.Replace("engine-fixture", "another-engine")), Prefix(55),
            Prefix(inspection: Inspect.Replace(Instance.ContainerId, new string('c', 64))), Prefix(inspection: Inspect.Replace("\"tty\":true", "\"tty\":false")),
            Prefix(inspection: Inspect.Replace("\"OpenStdin\":true", "\"OpenStdin\":false")) })
        {
            var raw = new FixtureStream(prefix) { Eof = true }; await using var owner = await Connect(new(raw));
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.NotDispatched && raw.Requests.All(x => x.StartsWith("GET ")) && raw.Disposed);
        }
        foreach (var response in new[] { Response("{\"message\":\"private access denial\"}", 403),
            Encoding.ASCII.GetBytes("HTTP/1.1 200 X\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"),
            Encoding.ASCII.GetBytes("HTTP/1.1 200 X\r\nTransfer-Encoding: chunked\r\nContent-Length: 2\r\n\r\n"),
            Encoding.ASCII.GetBytes("HTTP/1.1 200 X\r\nTransfer-Encoding: chunked\r\n\r\nZ\r\n") })
        {
            var deniedStream = new FixtureStream(response) { Eof = true }; await using var selected = await Connect(new(deniedStream));
            var result = await ((IContainerExec)selected).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.NotDispatched && deniedStream.Requests.Count == 1 && deniedStream.Disposed);
            if (result.Response is { } evidence) Check(evidence.HttpStatusCode == 403 && evidence.DaemonMessage == "private access denial");
        }
        // Chunked preflight and bounded/truncated original daemon errors.
        var preflight = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nOK\r\n0\r\n\r\n")
            .Concat(Prefix().Skip(Response("OK").Length)).ToArray();
        foreach (var error in new[] { Response("{\"message\":\"private rejection\"}", 409), Response(new byte[129], 500),
            Encoding.ASCII.GetBytes("HTTP/1.1 409 X\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nbad\r\n0\r\n\r\n") })
        {
            var raw = new FixtureStream(preflight.Concat(error).ToArray()) { Eof = true }; await using var owner = await Connect(new(raw), new() { MaximumCommandErrorBytes = 64 });
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.DaemonRejected && result.Response!.RawBody.Length <= 64);
            if (result.Response!.HttpStatusCode == 500) Check(result.Response.BodyUnavailable && result.Response.RawBody.Length == 64);
            if (result.Response.DaemonMessage is { } message) Check(!result.ToString().Contains(message));
        }
    }
    internal static async Task CancellationAdmissionAndOwnership()
    {
        // Cancellation before any possible start dispatch, including noncooperative transport opening.
        using (var caller = new CancellationTokenSource())
        {
            var raw = new FixtureStream(Wire()); var transport = new FixtureTransport(raw);
            await using var owner = await Connect(transport); caller.Cancel();
            Check((await Canceled(((IContainerExec)owner).StartTtyExecAsync(Instance, caller.Token))).Disposition == ContainerExecStartDisposition.NotDispatched && transport.OpenCount == 0);
        }
        foreach (bool afterDispatch in new[] { false, true })
        {
            using var caller = new CancellationTokenSource();
            var raw = new FixtureStream(afterDispatch ? Prefix() : []) { Eof = false };
            var transport = new FixtureTransport(raw); await using var owner = await Connect(transport);
            var pending = ((IContainerExec)owner).StartTtyExecAsync(Instance, caller.Token);
            await (afterDispatch ? raw.Started.Task : raw.Reading.Task).WaitAsync(Limit); caller.Cancel();
            var result = await Canceled(pending);
            Check(result.Disposition == (afterDispatch ? ContainerExecStartDisposition.Uncertain : ContainerExecStartDisposition.NotDispatched) && raw.Disposed);
        }
        // Historical response evidence survives cancellation after daemon acknowledgement/rejection.
        foreach (bool upgrade in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var bytes = upgrade ? Wire() : Prefix().Concat(Response("{\"message\":\"private error\"}", 409)).ToArray();
            var historical = new FixtureStream(bytes) { Eof = true, Fragment = 1 };
            if (upgrade) historical.ReadProgress = n => { if (n == bytes.Length) cancellation.Cancel(); };
            else historical.BeforeDispose = cancellation.Cancel;
            await using var selected = await Connect(new(historical));
            var result = await Canceled(((IContainerExec)selected).StartTtyExecAsync(Instance, cancellation.Token));
            Check(result.Disposition == (upgrade ? ContainerExecStartDisposition.Uncertain : ContainerExecStartDisposition.DaemonRejected));
            Check(result.Attachment is null && result.UpgradeObserved == upgrade);
            if (!upgrade) Check(result.Response!.DaemonMessage == "private error" && !result.Response.BodyUnavailable);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var partial = new FixtureStream(Prefix().Concat(Encoding.ASCII.GetBytes("HTTP/1.1 409 X\r\nContent-Length: 100\r\n\r\nprivate prefix")).ToArray());
            await using var selected = await Connect(new(partial));
            var pending = ((IContainerExec)selected).StartTtyExecAsync(Instance, cancellation.Token);
            await partial.Stalled.Task.WaitAsync(Limit); cancellation.Cancel(); var result = await Canceled(pending);
            Check(result.Disposition == ContainerExecStartDisposition.DaemonRejected && result.Response!.BodyUnavailable
                && Encoding.UTF8.GetString(result.Response.RawBody.AsSpan()) == "private prefix");
        }
        foreach (bool duringPreflight in new[] { false, true })
        {
            var bytes = Wire(); var raw = new FixtureStream(bytes) { Fragment = 1 }; var transport = new FixtureTransport(raw);
            await using var selected = await Connect(transport);
            if (duringPreflight) raw.ReadProgress = n => { if (n == Prefix().Length) transport.Counter.Opened(); };
            else transport.Counter.Opened();
            var result = await ((IContainerExec)selected).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.NotDispatched && result.Failure?.Kind == ContainerExecFailureKind.StaleConnection);
            Check(raw.Requests.All(r => r.StartsWith("GET ")) && transport.OpenCount == (duringPreflight ? 1 : 0));
        }
        {
            var raw = new FixtureStream(Prefix()); var transport = new FixtureTransport(raw); await using var selected = await Connect(transport);
            var pending = ((IContainerExec)selected).StartTtyExecAsync(Instance); await raw.Stalled.Task.WaitAsync(Limit);
            transport.Counter.Opened(); var result = await pending.WaitAsync(Limit);
            Check(result.Disposition == ContainerExecStartDisposition.Uncertain && result.Failure?.Kind == ContainerExecFailureKind.StaleConnection && raw.Disposed);
        }
        var lost = new FixtureStream(Prefix()) { Eof = true }; var lossTransport = new FixtureTransport(lost);
        await using (var owner = await Connect(lossTransport))
        {
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.Uncertain && lossTransport.OpenCount == 1 && lost.Requests.Count == 5);
        }
        var stalled = new FixtureStream(Prefix());
        await using (var owner = await Connect(new(stalled), new() { ExecOpeningTimeout = TimeSpan.FromMilliseconds(80) }))
        {
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance);
            Check(result.Disposition == ContainerExecStartDisposition.Uncertain && result.Failure?.Kind == ContainerExecFailureKind.Deadline && stalled.Disposed);
        }
        // Output buffering backpressures the transport; input remains independently usable.
        var rawOutput = new FixtureStream(Wire(output: new byte[40000]));
        var fixture = new FixtureTransport(rawOutput);
        await using (var owner = await Connect(fixture, new() { MaximumConcurrentExecAttachments = 1, ExecOutputCapacity = 1, MaximumExecOutputChunkBytes = 32, MaximumExecInputBytes = 8 }))
        {
            var exec = (IContainerExec)owner; var result = await exec.StartTtyExecAsync(Instance); var attachment = result.Attachment!;
            Check((await exec.StartTtyExecAsync(Instance)).Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
            Check((await attachment.WriteAsync(new byte[9])).Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
            Check((await attachment.WriteAsync(new byte[] { 3 })).Disposition == ContainerExecWriteDisposition.Written);
            var writeEntered = rawOutput.BlockWrites();
            var writing = attachment.WriteAsync(new byte[] { 4 }); await writeEntered.WaitAsync(Limit);
            Check((await attachment.WriteAsync(new byte[] { 5 })).Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
            Check((await ((IContainerExec)owner).InspectExecAsync(Instance)).IsSuccess);
            Check(rawOutput.BytesRead < 8000, "Bounded output must stop physical reads under backpressure");
            var dispose = owner.DisposeAsync().AsTask(); await dispose.WaitAsync(Limit); await writing;
            Check(!await attachment.Output.WaitToReadAsync() && rawOutput.Disposed);
            await attachment.DisposeAsync(); await owner.DisposeAsync();
        }
        // A stale generation revokes queued output and input, even when EOF has already completed.
        foreach (bool eof in new[] { false, true })
        {
            var raw = new FixtureStream(Wire(output: Terminal)) { Eof = eof }; var transport = new FixtureTransport(raw);
            await using var owner = await Connect(transport); var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance); var attachment = result.Attachment!;
            await attachment.Output.WaitToReadAsync().AsTask().WaitAsync(Limit);
            if (eof) await attachment.Completion.WaitAsync(Limit);
            transport.Counter.Opened();
            Check(!attachment.Output.TryRead(out _) && !await attachment.Output.WaitToReadAsync());
            Check((await attachment.WriteAsync(new byte[] { 3 })).Disposition == ContainerExecWriteDisposition.NotWritten);
            await attachment.DisposeAsync().AsTask().WaitAsync(Limit); Check(raw.Disposed);
        }
    }

    internal static async Task NoncooperativeCleanupAndInput()
    {
        // A stream ignoring read cancellation and disposal remains admitted until the actual read returns.
        var raw = new FixtureStream(Wire()) { IgnoreReadCancellation = true };
        var transport = new FixtureTransport(raw);
        await using (var owner = await Connect(transport, new() { MaximumConcurrentExecAttachments = 1 }))
        {
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance); var attachment = result.Attachment!;
            await raw.Stalled.Task.WaitAsync(Limit);
            var first = attachment.DisposeAsync().AsTask(); var second = attachment.DisposeAsync().AsTask(); Check(ReferenceEquals(first, second));
            Check((await ((IContainerExec)owner).StartTtyExecAsync(Instance)).Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
            var owning = owner.DisposeAsync().AsTask(); Check(!first.IsCompleted && !owning.IsCompleted);
            raw.End(); await Task.WhenAll(first, owning).WaitAsync(Limit); Check(raw.Disposed);
        }
        // Noncooperative connection opening and stream disposal are also joined.
        foreach (bool opening in new[] { false, true })
        {
            var stream = new FixtureStream(Wire()); var fixture = new FixtureTransport(stream);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (opening) fixture.OpenGate = release.Task; else stream.DisposeGate = release.Task;
            var owner = await Connect(fixture);
            var pending = ((IContainerExec)owner).StartTtyExecAsync(Instance);
            if (opening) await fixture.OpenEntered.Task.WaitAsync(Limit); else Check((await pending).Attachment is not null);
            var cleanup = owner.DisposeAsync().AsTask(); Check(!cleanup.IsCompleted);
            Check((await ((IContainerExec)owner).StartTtyExecAsync(Instance)).Disposition == ContainerExecStartDisposition.NotDispatched);
            release.TrySetResult(); await cleanup.WaitAsync(Limit);
            if (opening) Check((await pending).Disposition == ContainerExecStartDisposition.NotDispatched);
            Check(stream.Disposed);
        }
        // Cancellation during an input write may have applied bytes: close and preserve uncertain input result.
        using var caller = new CancellationTokenSource();
        var writeStream = new FixtureStream(Wire());
        await using (var owner = await Connect(new(writeStream)))
        {
            var attachment = (await ((IContainerExec)owner).StartTtyExecAsync(Instance)).Attachment!;
            var entered = writeStream.BlockWrites(); writeStream.IgnoreWriteCancellation = true;
            var pending = attachment.WriteAsync(new byte[] { 3, 0, 27 }, caller.Token); await entered.WaitAsync(Limit); caller.Cancel();
            await writeStream.CloseEntered.Task.WaitAsync(Limit);
            var cleanup = attachment.DisposeAsync().AsTask(); Check(!cleanup.IsCompleted);
            writeStream.ReleaseWrite(); var result = await pending.WaitAsync(Limit);
            Check(result.Disposition == ContainerExecWriteDisposition.Uncertain && result.Failure?.Kind == ContainerExecFailureKind.Canceled);
            await cleanup.WaitAsync(Limit); Check(writeStream.Input.SequenceEqual(new byte[] { 3, 0, 27 }));
        }
    }

    internal static async Task NamedPipeDuplexAndLostStart()
    {
        foreach (bool lost in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(Limit);
            var name = "cdd-attach-" + Guid.NewGuid().ToString("N")[..8];
            using var ordinary = new NamedPipeServerStream(name, PipeDirection.InOut, 3, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            int starts = 0;
            var inputCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Handshake(Stream pipe, bool dedicated)
            {
                foreach (var (path, body) in new[] { ("/_ping", "OK"), ("/version", Version(56)), ("/v1.56/info", Info) })
                {
                    Check((await Request(pipe, deadline.Token)).Header.StartsWith($"GET {path} HTTP/1.1"));
                    await pipe.WriteAsync(Response(body), deadline.Token);
                }
                if (dedicated)
                {
                    Check((await Request(pipe, deadline.Token)).Header.StartsWith($"GET /v1.56/exec/{Instance.ExecId}/json HTTP/1.1"));
                    await pipe.WriteAsync(Response(Inspect), deadline.Token);
                }
            }
            var servingOrdinary = Task.Run(async () =>
            {
                await ordinary.WaitForConnectionAsync(deadline.Token); await Handshake(ordinary, false);
                Check((await Request(ordinary, deadline.Token)).Header.StartsWith("GET /v1.56/containers/json"));
                await ordinary.WriteAsync(Response("[]"), deadline.Token);
                Check((await Request(ordinary, deadline.Token)).Header.StartsWith($"GET /v1.56/exec/{Instance.ExecId}/json"));
                await ordinary.WriteAsync(Response(Inspect), deadline.Token);
            });
            var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true)).ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token);
            Check(attempt.IsConnected); await using var owner = attempt.Connection!;
            using var dedicated = new NamedPipeServerStream(name, PipeDirection.InOut, 3, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var servingAttachment = Task.Run(async () =>
            {
                await dedicated.WaitForConnectionAsync(deadline.Token); await Handshake(dedicated, true);
                var request = await Request(dedicated, deadline.Token);
                Check(request.Header.StartsWith($"POST /v1.56/exec/{Instance.ExecId}/start HTTP/1.1") && Encoding.ASCII.GetString(request.Body) == "{\"Detach\":false,\"Tty\":true}");
                Interlocked.Increment(ref starts);
                if (lost) { dedicated.Dispose(); return; }
                // Fragment header and coalesce its final byte with terminal output.
                var header = Encoding.ASCII.GetBytes(Upgrade);
                for (int n = 0; n < header.Length - 1; n++) await dedicated.WriteAsync(header.AsMemory(n, 1), deadline.Token);
                await dedicated.WriteAsync(header[^1..].Concat(Terminal).ToArray(), deadline.Token);
                var input = new byte[Terminal.Length]; await dedicated.ReadExactlyAsync(input, deadline.Token); Check(input.SequenceEqual(Terminal));
                await inputCompleted.Task.WaitAsync(deadline.Token);
                dedicated.Dispose();
            });
            var result = await ((IContainerExec)owner).StartTtyExecAsync(Instance, deadline.Token);
            if (lost) Check(result.Disposition == ContainerExecStartDisposition.Uncertain);
            else
            {
                Check(result.Disposition == ContainerExecStartDisposition.Established);
                await using var attachment = result.Attachment!;
                Check((await Read(attachment, Terminal.Length)).SequenceEqual(Terminal));
                Check((await attachment.WriteAsync(Terminal)).Disposition == ContainerExecWriteDisposition.Written);
                inputCompleted.TrySetResult();
                Check((await attachment.Completion.WaitAsync(Limit)).Termination == ContainerExecAttachmentTermination.EndOfStream);
            }
            Check((await ((IContainerReader)owner).ListContainersAsync(deadline.Token)).IsSuccess);
            Check((await ((IContainerExec)owner).InspectExecAsync(Instance, deadline.Token)).IsSuccess);
            await Task.WhenAll(servingAttachment, servingOrdinary).WaitAsync(Limit);
            using var replacement = new NamedPipeServerStream(name, PipeDirection.InOut, 3, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var replay = replacement.WaitForConnectionAsync(deadline.Token); Check(starts == 1 && !replay.IsCompleted);
        }
    }
    private static async Task<(string Header, byte[] Body)> Request(Stream pipe, CancellationToken token)
    {
        var header = new StringBuilder(); var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        { Check(header.Length < 8192 && await pipe.ReadAsync(one, token) == 1); header.Append((char)one[0]); }
        var length = header.ToString().Split("\r\n").SingleOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        int n = length is null ? 0 : int.Parse(length.Split(':')[1]); var body = new byte[n]; await pipe.ReadExactlyAsync(body, token); return (header.ToString(), body);
    }
    private sealed class FixtureTransport(FixtureStream stream, int minor = 56) : IDockerTransportFactory, IDockerDuplexTransportFactory
    {
        internal PipeConnectionCounter Counter { get; } = new();
        internal int OpenCount;
        internal Task? OpenGate;
        internal TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
        { Counter.Opened(); return new ContinuityHandler(new Handler(minor), Counter); }
        public async ValueTask<Stream> OpenDuplexAsync(EngineEndpoint endpoint, DockerConnectionOptions options, CancellationToken token)
        { Interlocked.Increment(ref OpenCount); OpenEntered.TrySetResult(); if (OpenGate is { } wait) await wait; return stream; }
    }
    private sealed class Handler(int minor) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path == "/_ping" ? "OK" : path == "/version" ? Version(minor) : path.EndsWith("/info") ? Info
                : path.EndsWith("/json") && path.Contains("/exec/") ? Inspect : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private sealed class FixtureStream(byte[] response) : Stream
    {
        private int position; private readonly object gate = new();
        private readonly MemoryStream requests = new(); private bool attached;
        private readonly TaskCompletionSource end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? writeGate, writeEntered;
        internal readonly List<string> Requests = [];
        internal readonly List<byte> Input = [];
        internal string? StartBody;
        internal int Fragment = 4096;
        internal bool Eof, IgnoreReadCancellation, IgnoreWriteCancellation, Disposed;
        internal Task? DisposeGate;
        internal Action? BeforeDispose;
        internal Action<int>? ReadProgress;
        internal int BytesRead => position;
        internal TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void End() { Eof = true; end.TrySetResult(); }
        internal Task BlockWrites() { writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously); writeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); return writeEntered.Task; }
        internal void ReleaseWrite() => writeGate!.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            if (position < response.Length)
            {
                int n = Math.Min(Math.Min(buffer.Length, Fragment), response.Length - position);
                response.AsMemory(position, n).CopyTo(buffer); position += n; ReadProgress?.Invoke(position); return n;
            }
            if (Eof) return 0;
            Stalled.TrySetResult();
            if (IgnoreReadCancellation) await end.Task; else await end.Task.WaitAsync(cancellationToken);
            return 0;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (attached)
            {
                if (writeGate is not null) { writeEntered!.TrySetResult(); if (IgnoreWriteCancellation) await writeGate.Task; else await writeGate.Task.WaitAsync(cancellationToken); }
                lock (gate) Input.AddRange(buffer.ToArray()); return;
            }
            requests.Write(buffer.Span);
            var bytes = requests.ToArray(); var text = Encoding.ASCII.GetString(bytes); int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0) return;
            var header = text[..(split + 4)];
            var length = header.Split("\r\n").SingleOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            int size = length is null ? 0 : int.Parse(length.Split(':')[1]); if (bytes.Length < split + 4 + size) return;
            Requests.Add(header); requests.SetLength(0);
            if (header.StartsWith("POST ")) { StartBody = Encoding.ASCII.GetString(bytes, split + 4, size); attached = true; Started.TrySetResult(); }
        }
        public override async ValueTask DisposeAsync()
        {
            CloseEntered.TrySetResult(); BeforeDispose?.Invoke(); if (DisposeGate is { } wait) await wait;
            Disposed = true; if (!IgnoreReadCancellation) End();
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
