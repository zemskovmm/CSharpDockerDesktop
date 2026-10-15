using System.Net;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

internal static class ExecChecks
{
    internal static readonly string ContainerId = new('a', 64);
    internal static readonly string ExecId = new('b', 64);
    private static readonly ContainerExecInstance Instance = new(ContainerId, ExecId);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static string Created => $$$"""{"Id":"{{{ExecId}}}","future":{"accepted":true}}""";
    private static string Inspected => $$"""{"ID":"{{ExecId}}","ContainerID":"{{ContainerId}}","Running":false,"ExitCode":2,"Pid":42000,"OpenStdin":true,"OpenStdout":true,"OpenStderr":true,"CanRemove":false,"DetachKeys":"","ProcessConfig":{"tty":true,"privileged":false,"user":"1000","entrypoint":"sh","arguments":["-c","exit 2"]},"future":true}""";
    private static ContainerExecCreateRequest Request(IReadOnlyList<string>? args = null) => new(ContainerId, args ?? ["explicit-command"]);
    private static void Check(bool condition, string message = "Exec assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body, int status = 200) => new((HttpStatusCode)status) { Content = new TrackedContent(body) };
    private static string Handshake(int index, int minor) => index switch
    { 0 => "OK", 1 => $$"""{"Version":"fixture","ApiVersion":"1.{{minor}}","MinAPIVersion":"1.40"}""", _ => "{}" };
    private static FakeTransport Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action, int minor = 56)
        => new((request, n, token) => n < 3 ? Task.FromResult(Response(Handshake(n, minor)))
            : request.RequestUri!.AbsolutePath.EndsWith("/containers/json") ? Task.FromResult(Response("[]")) : action(request, token));
    private static async Task<IEngineConnection> Connect(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        var result = await new DockerEngineConnectionClient(transport, options).ConnectAsync();
        Check(result.IsConnected && result.Connection is IContainerExec); return result.Connection!;
    }
    private static async Task<ContainerExecCreationResult> Canceled(Task<ContainerExecCreationResult> task, CancellationToken token)
    {
        try { await task; throw new InvalidOperationException("Expected exec cancellation"); }
        catch (ContainerExecCreateCanceledException ex) { Check(task.IsCanceled && ex.CancellationToken == token); return ex.Outcome; }
    }
    private static async Task CanceledInspection(Task<ContainerExecInspectionResult> task)
    { try { await task; throw new InvalidOperationException("Expected inspect cancellation"); } catch (OperationCanceledException) { Check(task.IsCanceled); } }

    internal static async Task OfficialContractsAndArguments()
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Exec", "contract-evidence.json")));
        var versions = evidence.RootElement.GetProperty("versions").EnumerateArray().ToArray(); Check(versions.Length == 17);
        foreach (var version in versions)
        {
            int minor = int.Parse(version.GetProperty("api").GetString()!.Split('.')[1]);
            var properties = version.GetProperty("createProperties");
            Check(properties.GetProperty("Cmd").GetProperty("type").GetString() == "array");
            Check(properties.GetProperty("Privileged").GetProperty("default").GetBoolean() == false);
            Check(properties.TryGetProperty("ConsoleSize", out _) == (minor >= 42));
            Check(version.GetProperty("creationResponse").GetProperty("properties").GetProperty("Id").GetProperty("type").GetString() == "string");
            Check(version.GetProperty("inspectionSchema").GetProperty("properties").GetProperty("Running").GetProperty("type").GetString() == "boolean");
            var argv = new[] { "/explicit command", "", "two words", "quote\"\\tab\tline\n", "日本🙂", "$HOME; $(inert)" };
            var requests = new List<(string Method, string Path, string? Body)>();
            var transport = Transport(async (request, token) =>
            {
                Check(request.Version == new Version(1, 1) && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact);
                string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
                requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
                if (request.Method == HttpMethod.Get) return Response(Inspected);
                Check(request.Content!.Headers.ContentType!.MediaType == "application/json"); return Response(Created, 201);
            }, minor);
            await using var connection = await Connect(transport);
            var capability = (IContainerExec)connection;
            var mutable = argv.ToList();
            var creating = capability.CreateTtyExecAsync(Request(mutable)); mutable[0] = "changed-after-invocation";
            var created = await creating;
            Check(created.IsUsableAcknowledgement && created.AcknowledgedInstance == Instance && !created.Response!.BodyUnavailable);
            Check(Encoding.UTF8.GetString(created.Response!.RawBody.AsSpan()) == Created);
            var inspected = await capability.InspectExecAsync(created.AcknowledgedInstance!);
            Check(inspected.IsSuccess && inspected.Value!.Id == ExecId && inspected.Value.ContainerId == ContainerId && inspected.Value.RequestedInstance == Instance);
            Check(inspected.Value!.Running == false && inspected.Value.ExitCode == 2 && inspected.Value.Pid == 42000);
            Check(inspected.Value.ProcessConfiguration!.Arguments!.Value.SequenceEqual(new[] { "-c", "exit 2" }));
            Check(requests.Count == 2 && requests[0].Path == $"/v1.{minor}/containers/{ContainerId}/exec" && requests[1] == ("GET", $"/v1.{minor}/exec/{ExecId}/json", null));
            using var json = JsonDocument.Parse(requests[0].Body!);
            var root = json.RootElement; Check(root.EnumerateObject().Count() == 5);
            foreach (var flag in new[] { "AttachStdin", "AttachStdout", "AttachStderr", "Tty" }) Check(root.GetProperty(flag).GetBoolean());
            Check(root.GetProperty("Cmd").EnumerateArray().Select(x => x.GetString()).SequenceEqual(argv));
            Check((await ((IContainerReader)connection).ListContainersAsync()).IsSuccess && transport.Handler.Paths.Count == 6 && !transport.Handler.Disposed);
            Check(!created.ToString().Contains(ExecId) && !inspected.ToString().Contains("exit 2") && !inspected.Response!.ToString().Contains("exit 2"));
        }
    }

    internal static async Task MissingNullAndMalformedEvidence()
    {
        foreach (var body in new[] { "{}", "{\"ID\":null,\"ContainerID\":null,\"Running\":null,\"ExitCode\":null,\"Pid\":null,\"ProcessConfig\":null}",
            "{\"Running\":true,\"ExitCode\":0,\"Pid\":0}", "{\"Running\":false,\"ExitCode\":-1,\"ProcessConfig\":{\"arguments\":[]}}",
            "{\"ExitCode\":9223372036854775807,\"ProcessConfig\":{\"arguments\":null,\"tty\":null,\"user\":null},\"future\":[1,null,{}]}" })
        {
            await using var owner = await Connect(Transport((_, _) => Task.FromResult(Response(body))));
            var result = await ((IContainerExec)owner).InspectExecAsync(Instance); Check(result.IsSuccess);
            Check(Encoding.UTF8.GetString(result.Response!.RawBody.AsSpan()) == body && result.Value!.Id is null && result.Value.ContainerId is null);
            if (body is "{}" || body.Contains("\"Running\":null")) Check(result.Value!.Running is null && result.Value.ExitCode is null && result.Value.Pid is null);
            if (body.Contains("\"Running\":true")) Check(result.Value!.Running == true && result.Value.ExitCode == 0);
        }
        var invalidInspect = new[] { "[]", "null", "", "{", "{\"ID\":12}", "{\"ID\":\"other\"}", "{\"ContainerID\":\"other\"}",
            "{\"Running\":0}", "{\"ExitCode\":false}", "{\"ExitCode\":1.5}", "{\"ExitCode\":9223372036854775808}", "{\"Pid\":\"1\"}",
            "{\"OpenStdout\":0}", "{\"CanRemove\":\"false\"}", "{\"DetachKeys\":[]}", "{\"ProcessConfig\":[]}",
            "{\"ProcessConfig\":{\"tty\":1}}", "{\"ProcessConfig\":{\"arguments\":[null]}}", "{\"ProcessConfig\":{\"arguments\":\"one\"}}",
            "{\"Running\":true,\"Running\":false}", "{\"future\":\"\\ud800\"}", new string('[', 65) + new string(']', 65) };
        foreach (var body in invalidInspect)
        {
            await using var owner = await Connect(Transport((_, _) => Task.FromResult(Response(body))));
            var result = await ((IContainerExec)owner).InspectExecAsync(Instance);
            Check(!result.IsSuccess && result.Value is null && result.Failure?.Kind == ContainerExecFailureKind.MalformedResponse, body);
            Check(!result.Response!.BodyUnavailable && Encoding.UTF8.GetString(result.Response.RawBody.AsSpan()) == body);
        }
        foreach (var body in new[] { "{}", "{\"Id\":null}", "{\"Id\":\"\"}", "{\"Id\":false}", "{\"ID\":\"wrong case\"}",
            "{\"Id\":\"a/b\"}", "{\"Id\":\"first\",\"Id\":\"second\"}", "{\"Id\":\"valid\",\"future\":\"\\ud800\"}", "", "[]", "{" })
        {
            await using var owner = await Connect(Transport((_, _) => Task.FromResult(Response(body, 201))));
            var result = await ((IContainerExec)owner).CreateTtyExecAsync(Request());
            Check(result.Disposition == ContainerExecCreationDisposition.Uncertain && result.AcknowledgedInstance is null
                && result.Failure?.Kind == ContainerExecFailureKind.MalformedResponse && !result.IsUsableAcknowledgement, body);
        }
        foreach (bool create in new[] { false, true })
        {
            byte[] body = [(byte)'{', (byte)'"', (byte)'x', (byte)'"', (byte)':', (byte)'"', 0xff, (byte)'"', (byte)'}'];
            await using var owner = await Connect(Transport((_, _) => Task.FromResult(new HttpResponseMessage(create ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = new ByteArrayContent(body) })));
            var exec = (IContainerExec)owner;
            if (create) { var result = await exec.CreateTtyExecAsync(Request()); Check(result.Failure?.Kind == ContainerExecFailureKind.MalformedResponse && result.Response!.RawBody.SequenceEqual(body)); }
            else { var result = await exec.InspectExecAsync(Instance); Check(result.Failure?.Kind == ContainerExecFailureKind.MalformedResponse && result.Response!.RawBody.SequenceEqual(body)); }
        }
    }

    internal static async Task InputsAndBounds()
    {
        var transport = Transport((_, _) => Task.FromResult(Response(Created, 201)));
        await using var owner = await Connect(transport, new() { MaximumExecArguments = 2, MaximumExecArgumentBytes = 32, MaximumExecRequestBytes = 128 });
        var exec = (IContainerExec)owner;
        foreach (var id in new[] { "", ".", "..", "mutable-name", new string('a', 12), new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64), "/name", "x/y", "x?query", "x%20", "x#frag", "x\\name", "\0", "with space", "日本", "\ud800", new string('x', 4097) })
        {
            Check((await exec.CreateTtyExecAsync(new(id, ["cmd"]))).Disposition == ContainerExecCreationDisposition.NotDispatched);
            Check((await exec.InspectExecAsync(new(ContainerId, id))).Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
            Check((await exec.InspectExecAsync(new(id, ExecId))).Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
        }
        foreach (var args in new IReadOnlyList<string>[] { [], [""], [null!], ["cmd", null!], ["cmd", "\0"], ["cmd", "\ud800"], ["cmd", new string('a', 33)], ["cmd", "a", "b"], ["cmd", new string('日', 11)], [new string('\u0001', 24)] })
            Check((await exec.CreateTtyExecAsync(Request(args))).Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
        Check((await exec.CreateTtyExecAsync(null!)).Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
        Check((await exec.InspectExecAsync(null!)).Failure?.Kind == ContainerExecFailureKind.InvalidRequest);
        Check(transport.Handler.Paths.Count == 3, "Invalid input must never dispatch");
        Check((await exec.CreateTtyExecAsync(Request(["cmd", ""]))).IsUsableAcknowledgement);
        foreach (bool known in new[] { false, true })
        foreach (int status in new[] { 201, 409 })
        foreach (int length in new[] { 128, 129, 100000 })
        {
            var body = status == 201 ? "{\"Id\":\"" + ExecId + "\"}" + new string(' ', length - 73) : "{\"message\":\"" + new string('x', length - 14) + "\"}";
            Check(Encoding.UTF8.GetByteCount(body) == length);
            var content = known ? (HttpContent)new TrackedContent(body) : new UnknownLengthContent(body);
            await using var connection = await Connect(Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content })),
                new() { MaximumExecResponseBytes = 128, MaximumCommandErrorBytes = 128 });
            var result = await ((IContainerExec)connection).CreateTtyExecAsync(Request());
            Check(result.Response!.BodyUnavailable == (length > 128) && result.Response.RawBody.Length == (length > 128 ? 0 : 128));
            Check(result.Disposition == (status == 409 ? ContainerExecCreationDisposition.DaemonRejected : length == 128 ? ContainerExecCreationDisposition.Acknowledged : ContainerExecCreationDisposition.Uncertain));
            Check(known ? ((TrackedContent)content).Disposed : ((UnknownLengthContent)content).Disposed);
        }
        foreach (bool create in new[] { true, false })
        {
            var stream = new CountedErrorStream();
            await using var connection = await Connect(Transport((_, _) => Task.FromResult(new HttpResponseMessage(create ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = new StreamContent(stream) })),
                new() { MaximumExecResponseBytes = 64 });
            if (create) Check((await ((IContainerExec)connection).CreateTtyExecAsync(Request())).Response!.BodyUnavailable);
            else Check((await ((IContainerExec)connection).InspectExecAsync(Instance)).Response!.BodyUnavailable);
            Check(stream.BytesRead == 65 && stream.Disposed);
        }
    }

    internal static async Task ClassifiedFailures()
    {
        foreach (bool create in new[] { true, false })
        foreach (int status in new[] { 200, 201, 202, 204, 301, 304, 400, 401, 403, 404, 409, 422, 500, 503 })
        foreach (var body in new[] { "{\"message\":\"sensitive original daemon error\",\"extra\":1}", "sensitive original daemon error" })
        {
            var transport = Transport((_, _) => Task.FromResult(Response(body, status)));
            await using var owner = await Connect(transport);
            ContainerExecResponseEvidence evidence; ContainerExecFailure failure;
            if (create)
            {
                var result = await ((IContainerExec)owner).CreateTtyExecAsync(Request());
                Check(result.Disposition == (status >= 400 ? ContainerExecCreationDisposition.DaemonRejected : ContainerExecCreationDisposition.Uncertain));
                Check(!result.IsUsableAcknowledgement); evidence = result.Response!; failure = result.Failure!;
            }
            else
            {
                var result = await ((IContainerExec)owner).InspectExecAsync(Instance);
                // A 200 object containing only unknown fields is valid incomplete observation.
                if (status == 200 && body.StartsWith('{')) { Check(result.IsSuccess && result.Value!.Running is null); continue; }
                Check(!result.IsSuccess); evidence = result.Response!; failure = result.Failure!;
            }
            Check(evidence.HttpStatusCode == status && Encoding.UTF8.GetString(evidence.RawBody.AsSpan()) == body);
            Check(failure.Kind == (status == (create ? 201 : 200) ? ContainerExecFailureKind.MalformedResponse
                : status == 404 ? ContainerExecFailureKind.NotFound : status == 409 ? ContainerExecFailureKind.Conflict
                : status is 401 or 403 ? ContainerExecFailureKind.AccessDenied : status >= 500 ? ContainerExecFailureKind.DaemonFailure
                : status >= 400 ? ContainerExecFailureKind.InvalidRequest : ContainerExecFailureKind.UnexpectedResponse));
            if (status != (create ? 201 : 200)) Check(evidence.DaemonMessage == "sensitive original daemon error");
            Check(!failure.Description.Contains("sensitive") && !evidence.ToString().Contains("sensitive") && transport.Handler.Paths.Count == 4);
        }
        foreach (bool create in new[] { true, false })
        foreach (var error in new Exception[] { new IOException("private"), new HttpRequestException("private"), new HttpRequestException("private", new UnauthorizedAccessException()) })
        {
            var transport = Transport((_, _) => Task.FromException<HttpResponseMessage>(error));
            await using var owner = await Connect(transport);
            var kind = error.InnerException is UnauthorizedAccessException ? ContainerExecFailureKind.AccessDenied : ContainerExecFailureKind.TransportFailure;
            if (create) { var result = await ((IContainerExec)owner).CreateTtyExecAsync(Request()); Check(result.Disposition == ContainerExecCreationDisposition.Uncertain && result.Failure?.Kind == kind); }
            else Check((await ((IContainerExec)owner).InspectExecAsync(Instance)).Failure?.Kind == kind);
            Check(transport.Handler.Paths.Count == 4, "Transport failures must never repeat creation");
        }
    }

    internal static async Task CooperativeLifetimesAndAdmission()
    {
        var ordinary = Transport((_, _) => Task.FromResult(Response(Created, 201)));
        var owner = await Connect(ordinary);
        using (var caller = new CancellationTokenSource())
        {
            caller.Cancel(); var before = await Canceled(((IContainerExec)owner).CreateTtyExecAsync(Request(), caller.Token), caller.Token);
            Check(before.Disposition == ContainerExecCreationDisposition.NotDispatched && before.Response is null);
            await CanceledInspection(((IContainerExec)owner).InspectExecAsync(Instance, caller.Token));
        }
        await owner.DisposeAsync();
        Check((await ((IContainerExec)owner).CreateTtyExecAsync(Request())).Failure?.Kind == ContainerExecFailureKind.ConnectionDisposed);
        Check((await ((IContainerExec)owner).InspectExecAsync(Instance)).Failure?.Kind == ContainerExecFailureKind.ConnectionDisposed && ordinary.Handler.Paths.Count == 3);

        foreach (bool create in new[] { false, true })
        foreach (bool body in new[] { false, true })
        foreach (var ending in new[] { "cancel", "deadline", "dispose", "generation", "wrapped-cancel" })
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new StalledStream(); var counter = new PipeConnectionCounter(); counter.Opened();
            var transport = Transport(async (_, token) =>
            {
                if (body) return new(create ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = new StreamContent(stream) };
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (ending == "wrapped-cancel") { throw new HttpRequestException("sensitive wrapped cancel"); }
                return Response(Created, 201);
            });
            var connection = await Connect(new GuardedCommandTransport(transport.Handler, counter), new()
            { CommandTimeout = TimeSpan.FromMilliseconds(120), RequestTimeout = TimeSpan.FromMilliseconds(120), MaximumConcurrentCommands = 1, MaximumConcurrentExecInspections = 1 });
            var exec = (IContainerExec)connection;
            Task<ContainerExecCreationResult>? creating = create ? exec.CreateTtyExecAsync(Request(), caller.Token) : null;
            Task<ContainerExecInspectionResult>? inspecting = create ? null : exec.InspectExecAsync(Instance, caller.Token);
            await (body ? stream.Started.Task : entered.Task).WaitAsync(Limit);
            if (create)
            {
                Check((await exec.CreateTtyExecAsync(Request())).Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
                Check((await ((IContainerCommands)connection).StartContainerAsync("target")).Failure?.Kind == ContainerCommandFailureKind.CapacityExceeded);
                Check((await ((IImageCommands)connection).DeleteImageAsync("repo")).Failure?.Kind == ImageCommandFailureKind.CapacityExceeded);
                Check((await ((IVolumeCommands)connection).DeleteVolumeAsync("volume")).Failure?.Kind == VolumeCommandFailureKind.CapacityExceeded);
                Check((await ((INetworkCommands)connection).DeleteNetworkAsync("network")).Failure?.Kind == NetworkCommandFailureKind.CapacityExceeded);
            }
            else Check((await exec.InspectExecAsync(Instance)).Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
            if (ending is "cancel" or "wrapped-cancel") caller.Cancel();
            if (ending == "dispose") await connection.DisposeAsync().AsTask().WaitAsync(Limit);
            if (ending == "generation") counter.Opened();
            var expected = ending is "cancel" or "wrapped-cancel" ? ContainerExecFailureKind.Canceled : ending == "dispose" ? ContainerExecFailureKind.ConnectionDisposed
                : ending == "generation" ? ContainerExecFailureKind.StaleConnection : ContainerExecFailureKind.Deadline;
            if (create)
            {
                var result = caller.IsCancellationRequested ? await Canceled(creating!, caller.Token) : await creating!;
                Check(result.Disposition == ContainerExecCreationDisposition.Uncertain && result.Failure?.Kind == expected && result.Response?.HttpStatusCode == (body ? 201 : null));
            }
            else if (caller.IsCancellationRequested) await CanceledInspection(inspecting!);
            else Check((await inspecting!).Failure?.Kind == expected && !(await inspecting!).IsSuccess);
            Check(!body || stream.Disposed);
            Check(transport.Handler.Paths.Count == 4, "Bounded calls cannot replay");
            if (ending != "dispose") Check((await ((IContainerReader)connection).ListContainersAsync()).IsSuccess == (ending != "generation"));
            await connection.DisposeAsync(); Check(transport.Handler.DisposeCount == 1);
        }
    }

    internal static async Task LateEvidenceAndJoinedDisposal()
    {
        foreach (bool create in new[] { true, false })
        foreach (var phase in new[] { "headers", "body", "cleanup" })
        foreach (var ending in new[] { "cancel", "deadline", "dispose", "generation" })
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var counter = new PipeConnectionCounter(); counter.Opened();
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new LateStream(Encoding.UTF8.GetBytes(create ? Created : Inspected), phase == "body" ? release.Task : Task.CompletedTask, entered);
            stream.Reading = token => token.Register(() => expired.TrySetResult());
            Task? disposal = null;
            IEngineConnection? owner = null;
            void End()
            {
                if (ending == "cancel") caller.Cancel();
                if (ending == "generation") counter.Opened();
                if (ending == "dispose")
                {
                    disposal = owner!.DisposeAsync().AsTask();
                    Check(!disposal.IsCompleted && !owner.DisposeAsync().AsTask().IsCompleted, "Disposal must join registered work even during synchronous response cleanup");
                }
            }
            var transport = Transport(async (request, token) =>
            {
                _ = token.Register(() => expired.TrySetResult());
                if (phase == "headers") { entered.TrySetResult(); await release.Task; }
                return new(create ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = new StreamContent(stream) };
            });
            owner = await Connect(new GuardedCommandTransport(transport.Handler, counter), new()
            { CommandTimeout = TimeSpan.FromMilliseconds(80), RequestTimeout = TimeSpan.FromMilliseconds(80), MaximumConcurrentCommands = 1, MaximumConcurrentExecInspections = 1 });
            if (phase == "cleanup") stream.Cleanup = End;
            var exec = (IContainerExec)owner;
            Task<ContainerExecCreationResult>? creating = create ? exec.CreateTtyExecAsync(Request(), caller.Token) : null;
            Task<ContainerExecInspectionResult>? inspecting = create ? null : exec.InspectExecAsync(Instance, caller.Token);
            if (phase != "cleanup")
            {
                await entered.Task.WaitAsync(Limit);
                if (ending == "deadline") await expired.Task.WaitAsync(Limit); else End();
                Check(!(creating as Task ?? inspecting!).IsCompleted && (disposal is null || !disposal.IsCompleted), "Noncooperative transport work remains joined");
                if (ending == "dispose") Check(!owner.DisposeAsync().AsTask().IsCompleted, "Repeated disposal must join the same work");
                release.TrySetResult();
            }
            // Cleanup's deadline case has no blocking work and is therefore a legitimate current success.
            bool success = phase == "cleanup" && ending == "deadline";
            if (create)
            {
                var result = ending == "cancel" ? await Canceled(creating!, caller.Token) : await creating!;
                Check(result.IsUsableAcknowledgement == success);
                if (!success) Check(result.Failure?.Kind == (ending == "cancel" ? ContainerExecFailureKind.Canceled : ending == "deadline" ? ContainerExecFailureKind.Deadline
                    : ending == "dispose" ? ContainerExecFailureKind.ConnectionDisposed : ContainerExecFailureKind.StaleConnection));
                // Late headers can be discarded by HttpClient; a complete late body/cleanup retains the valid creation identity.
                if (phase is "body" or "cleanup") Check(result.AcknowledgedInstance == Instance && Encoding.UTF8.GetString(result.Response!.RawBody.AsSpan()) == Created);
            }
            else if (ending == "cancel") await CanceledInspection(inspecting!);
            else Check((await inspecting!).IsSuccess == success && ((await inspecting!).IsSuccess || (await inspecting!).Value is null));
            if (disposal is not null) await disposal.WaitAsync(Limit);
            await owner.DisposeAsync(); Check(stream.Disposed && transport.Handler.DisposeCount == 1 && transport.Handler.Paths.Count == 4);
        }
    }

    internal static async Task HistoricalEvidenceAndSharedAdmission()
    {
        // A transport cancellation callback may synchronously reenter owner disposal.
        var reentered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IEngineConnection? reentrantOwner = null;
        var reentrant = Transport(async (request, token) =>
        {
            using var registration = token.Register(() => reentered.TrySetResult(reentrantOwner!.DisposeAsync().AsTask()));
            opening.TrySetResult(); await finish.Task;
            return Response(Created, 201);
        });
        reentrantOwner = await Connect(reentrant);
        var reentrantCreate = ((IContainerExec)reentrantOwner).CreateTtyExecAsync(Request());
        await opening.Task.WaitAsync(Limit);
        var closing = reentrantOwner.DisposeAsync().AsTask();
        Check(ReferenceEquals(closing, await reentered.Task.WaitAsync(Limit)) && !closing.IsCompleted && reentrant.Handler.DisposeCount == 1);
        finish.TrySetResult();
        Check((await reentrantCreate).Failure?.Kind == ContainerExecFailureKind.ConnectionDisposed);
        await closing.WaitAsync(Limit);

        foreach (bool complete in new[] { true, false })
        foreach (int status in new[] { 201, 409 })
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var body = status == 201 ? Created : "{\"message\":\"sensitive daemon rejection\"}";
            var late = new LateStream(Encoding.UTF8.GetBytes(body), Task.CompletedTask, entered) { Cleanup = caller.Cancel };
            var stalled = new StalledStream();
            var transport = Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StreamContent(complete ? late : stalled) }));
            await using var owner = await Connect(transport);
            var pending = ((IContainerExec)owner).CreateTtyExecAsync(Request(), caller.Token);
            if (!complete) { await stalled.Started.Task.WaitAsync(Limit); caller.Cancel(); }
            var result = await Canceled(pending, caller.Token);
            Check(result.Disposition == (status == 409 ? ContainerExecCreationDisposition.DaemonRejected : ContainerExecCreationDisposition.Uncertain));
            Check(!result.IsUsableAcknowledgement && result.Failure?.Kind == ContainerExecFailureKind.Canceled
                && result.Response!.HttpStatusCode == status && result.Response.BodyUnavailable == !complete);
            Check((result.AcknowledgedInstance is not null) == (complete && status == 201));
            if (complete) Check(Encoding.UTF8.GetString(result.Response!.RawBody.AsSpan()) == body);
            if (complete && status == 409) Check(result.Response!.DaemonMessage == "sensitive daemon rejection");
        }
        var stale = new ContinuityTestTransport();
        await using (var owner = await Connect(stale))
        {
            stale.Counter.Opened();
            var result = await ((IContainerExec)owner).CreateTtyExecAsync(Request());
            Check(result.Disposition == ContainerExecCreationDisposition.NotDispatched && result.Failure?.Kind == ContainerExecFailureKind.StaleConnection);
            Check((await ((IContainerExec)owner).InspectExecAsync(Instance)).Failure?.Kind == ContainerExecFailureKind.StaleConnection && stale.Handler.Paths.Count == 3);
        }
        foreach (var occupiedKind in new[] { "container", "image", "volume", "network" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = Transport(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/exec")) return Response(Created, 201);
                if (request.RequestUri.AbsolutePath.StartsWith("/v1.56/exec/")) return Response(Inspected);
                entered.TrySetResult(); await release.Task.WaitAsync(token);
                return occupiedKind == "image" ? Response("[]") : Response("", 204);
            });
            await using var owner = await Connect(transport, new() { MaximumConcurrentCommands = 1, MaximumConcurrentExecInspections = 1 });
            var exec = (IContainerExec)owner;
            Task occupied = occupiedKind == "container" ? ((IContainerCommands)owner).StartContainerAsync("target")
                : occupiedKind == "image" ? ((IImageCommands)owner).DeleteImageAsync("repo")
                : occupiedKind == "volume" ? ((IVolumeCommands)owner).DeleteVolumeAsync("volume") : ((INetworkCommands)owner).DeleteNetworkAsync("network");
            await entered.Task.WaitAsync(Limit);
            Check((await exec.CreateTtyExecAsync(Request())).Failure?.Kind == ContainerExecFailureKind.CapacityExceeded);
            Check((await exec.InspectExecAsync(Instance)).IsSuccess, "Exec reads have independent finite admission");
            release.TrySetResult(); await occupied;
            Check((await exec.CreateTtyExecAsync(Request())).IsUsableAcknowledgement);
        }
        foreach (var config in new[] { "{\"arguments\":[\"one\",\"two\",\"three\"]}", "{\"arguments\":[\"123456789\"]}" })
        {
            await using var owner = await Connect(Transport((_, _) => Task.FromResult(Response("{\"ProcessConfig\":" + config + "}"))),
                new() { MaximumExecArguments = 2, MaximumExecArgumentBytes = 8 });
            Check((await ((IContainerExec)owner).InspectExecAsync(Instance)).Failure?.Kind == ContainerExecFailureKind.MalformedResponse);
        }
    }

    internal static async Task NamedPipeCreationOutcomes()
    {
        foreach (var mode in new[] { "closed-before", "lost-after", "cancel", "deadline", "dispose" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var caller = new CancellationTokenSource();
            var name = "cdd-exec-" + Guid.NewGuid().ToString("N")[..8];
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            int applied = 0;
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Serve()
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                for (int n = 0; n < 3; n++)
                {
                    Check((await ReadPipeRequest(pipe, deadline.Token)).Headers.StartsWith("GET "));
                    var body = Encoding.UTF8.GetBytes(Handshake(n, 56));
                    await pipe.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n" + (n == 2 && mode == "closed-before" ? "Connection: close\r\n" : "") + "\r\n"), deadline.Token);
                    await pipe.WriteAsync(body, deadline.Token); await pipe.FlushAsync(deadline.Token);
                }
                if (mode == "closed-before") { pipe.Dispose(); return; }
                var request = await ReadPipeRequest(pipe, deadline.Token);
                Check(request.Headers.StartsWith($"POST /v1.56/containers/{ContainerId}/exec HTTP/1.1"));
                using var json = JsonDocument.Parse(request.Body);
                Check(json.RootElement.GetProperty("Cmd").EnumerateArray().Single().GetString() == "explicit-command");
                Interlocked.Increment(ref applied); issued.TrySetResult();
                if (mode == "lost-after") { pipe.Dispose(); return; }
                var one = new byte[1];
                try { Check(await pipe.ReadAsync(one, deadline.Token) == 0); } catch (IOException) { }
            }
            var serving = Serve();
            var attempt = await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true),
                new() { CommandTimeout = TimeSpan.FromMilliseconds(180) }).ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token);
            Check(attempt.IsConnected); await using var owner = attempt.Connection!;
            using var replacement = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var replacementConnected = replacement.WaitForConnectionAsync(deadline.Token);
            var pending = ((IContainerExec)owner).CreateTtyExecAsync(Request(), caller.Token);
            if (mode is "cancel" or "deadline" or "dispose") await issued.Task.WaitAsync(deadline.Token);
            if (mode == "cancel") caller.Cancel();
            if (mode == "dispose") await owner.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            var result = mode == "cancel" ? await Canceled(pending, caller.Token) : await pending.WaitAsync(deadline.Token);
            await serving;
            Check(result.Disposition == ContainerExecCreationDisposition.Uncertain && result.AcknowledgedInstance is null);
            Check(applied == (mode == "closed-before" ? 0 : 1) && !replacementConnected.IsCompleted, "The retained pool must not reconnect or replay exec creation");
            if (mode is "cancel" or "deadline" or "dispose") Check(result.Failure?.Kind == (mode == "cancel" ? ContainerExecFailureKind.Canceled
                : mode == "deadline" ? ContainerExecFailureKind.Deadline : ContainerExecFailureKind.ConnectionDisposed));
        }
    }

    private static async Task<(string Headers, byte[] Body)> ReadPipeRequest(Stream stream, CancellationToken token)
    {
        var header = new StringBuilder(); var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        { Check(header.Length < 8192 && await stream.ReadAsync(one, token) == 1); header.Append((char)one[0]); }
        var lengthLine = header.ToString().Split("\r\n").SingleOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        int length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1]); Check(length <= 4096);
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token); return (header.ToString(), body);
    }

    private sealed class LateStream(byte[] bytes, Task release, TaskCompletionSource entered) : Stream
    {
        private bool read;
        public Action? Cleanup { get; set; }
        public Action<CancellationToken>? Reading { get; set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) return 0;
            Reading?.Invoke(cancellationToken);
            entered.TrySetResult(); await release; // Deliberately ignores cancellation.
            bytes.AsMemory().CopyTo(buffer); read = true; return bytes.Length;
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
