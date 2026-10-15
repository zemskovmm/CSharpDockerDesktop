using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal sealed class DockerExecAttachment : IContainerExecAttachment
{
    private readonly DockerEngineConnection owner;
    private readonly DockerEngineConnectionClient client;
    private readonly DockerConnectionOptions options;
    private readonly Action<DockerExecAttachment> released;
    private readonly CancellationTokenSource stop;
    private readonly CancellationTokenSource io;
    private readonly Channel<ImmutableArray<byte>> channel;
    private readonly TaskCompletionSource<ContainerExecStartResult> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private Stream? stream;
    private Task? close, disposal;
    private Task<ContainerExecWriteResult>? input;
    private Task<ContainerExecAttachmentCompletion> producer = null!;
    private bool established, ending;
    private ContainerExecResponseEvidence? preflightEvidence;
    private ContainerExecFailure? inputFailure;
    private int revoked, stale;
    private readonly CancellationTokenRegistration stopped;

    internal DockerExecAttachment(DockerEngineConnection owner, DockerEngineConnectionClient client, DockerConnectionOptions options,
        ContainerExecInstance instance, CancellationToken lifetime, CancellationToken caller, Action<DockerExecAttachment> released)
    {
        (this.owner, this.client, this.options, Instance, this.released) = (owner, client, options, instance, released);
        stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
        io = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        channel = Channel.CreateBounded<ImmutableArray<byte>>(new BoundedChannelOptions(options.ExecOutputCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true, AllowSynchronousContinuations = false });
        Output = new CurrentReader(this, channel.Reader);
        stopped = stop.Token.Register(() => { Interlocked.Exchange(ref revoked, 1); _ = CloseAsync(); });
        if (owner.Continuity is { } continuity) continuity.Reopened += Invalidate;
    }
    public ContainerExecInstance Instance { get; }
    public ChannelReader<ImmutableArray<byte>> Output { get; }
    public Task<ContainerExecAttachmentCompletion> Completion => producer;
    internal Task<ContainerExecStartResult> Opened => opened.Task;
    internal void Start() => producer = Task.Run(RunAsync);
    private bool CurrentDelivery()
    {
        if (Volatile.Read(ref revoked) == 0 && !stop.IsCancellationRequested && owner.HasContinuity) return true;
        while (channel.Reader.TryRead(out _)) { }
        return false;
    }
    private void EnsureCurrent()
    {
        if (!owner.HasContinuity) { Invalidate(); throw new ExecAttachmentProtocolException(ContainerExecFailureKind.StaleConnection); }
        io.Token.ThrowIfCancellationRequested();
    }
    private void Invalidate()
    {
        Interlocked.Exchange(ref stale, 1); Interlocked.Exchange(ref revoked, 1);
        stop.Cancel();
    }
    internal static ContainerExecFailure Failure(ContainerExecFailureKind kind) => new(kind, kind switch
    {
        ContainerExecFailureKind.StaleConnection => "The selected Engine transport changed; obtain a fresh negotiated connection.",
        ContainerExecFailureKind.CapacityExceeded => "The bounded exec attachment or input capacity is occupied.",
        ContainerExecFailureKind.InvalidRequest => "Exec attachment requires an exact TTY exec binding and bounded raw input.",
        ContainerExecFailureKind.Deadline => "Establishing the exec attachment exceeded its deadline.",
        ContainerExecFailureKind.Canceled => "The exec attachment operation was canceled.",
        ContainerExecFailureKind.ConnectionDisposed => "The owning Engine connection was disposed.",
        ContainerExecFailureKind.MalformedResponse => "The Engine attachment handshake or framing is unsupported, malformed or over budget.",
        _ => "The Engine exec attachment was rejected, interrupted or unavailable."
    });
    private ContainerExecFailureKind CancellationKind() => !owner.HasTransportContinuity || Volatile.Read(ref stale) != 0
        ? ContainerExecFailureKind.StaleConnection : !owner.HasContinuity ? ContainerExecFailureKind.ConnectionDisposed : ContainerExecFailureKind.Canceled;

    private async Task<ContainerExecAttachmentCompletion> RunAsync()
    {
        bool dispatched = false, upgrade = false, eof = false;
        ContainerExecResponseEvidence? evidence = null;
        ContainerExecFailure? failure = null;
        int? status = null;
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(io.Token);
        opening.CancelAfter(options.ExecOpeningTimeout);
        // Deadline cancellation also closes the stream, while the work remains admitted until it actually returns.
        using var timeoutClose = opening.Token.Register(() => { _ = CloseAsync(); });
        try
        {
            EnsureCurrent();
            var connected = await client.OpenExecTransportAsync(owner.Metadata.Endpoint, opening.Token).ConfigureAwait(false);
            lock (gate) stream = connected;
            if (opening.IsCancellationRequested) { await CloseAsync().ConfigureAwait(false); opening.Token.ThrowIfCancellationRequested(); }
            var wire = new DockerExecWire(connected, options);
            await PreflightAsync(wire, opening.Token).ConfigureAwait(false);
            EnsureCurrent(); opening.Token.ThrowIfCancellationRequested();
            // First possible byte of POST. No wrapper can replace the stream or replay this request.
            dispatched = true;
            await wire.SendAsync("POST", $"v{owner.Metadata.Api.Negotiated}/exec/{Instance.ExecId}/start",
                "{\"Detach\":false,\"Tty\":true}"u8.ToArray(), true, opening.Token).ConfigureAwait(false);
            var header = await wire.ReadHeaderAsync(opening.Token).ConfigureAwait(false); status = header.Status;
            if (status >= 400)
            {
                evidence = new(status.Value, [], true);
                using var prefix = new MemoryStream();
                try
                {
                    var body = await wire.BodyAsync(header, options.MaximumCommandErrorBytes, false, opening.Token,
                        bytes => prefix.Write(bytes.Span)).ConfigureAwait(false);
                    evidence = Evidence(status.Value, body.Bytes, body.Truncated);
                }
                catch { evidence = Evidence(status.Value, prefix.ToArray(), true); throw; }
                throw new ExecAttachmentProtocolException(HttpFailure(status.Value));
            }
            DockerExecWire.ValidateUpgrade(header); upgrade = true;
            EnsureCurrent(); opening.Token.ThrowIfCancellationRequested();
            opening.CancelAfter(Timeout.InfiniteTimeSpan);
            lock (gate) established = true;
            opened.TrySetResult(new(ContainerExecStartDisposition.Established, this, UpgradeObserved: true));
            var chunk = new byte[options.MaximumExecOutputChunkBytes];
            while (true)
            {
                EnsureCurrent();
                int n = await wire.ReadAsync(chunk, io.Token).ConfigureAwait(false);
                EnsureCurrent();
                if (n == 0) { eof = true; break; }
                await channel.Writer.WriteAsync(ImmutableArray.Create(chunk, 0, n), io.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or TimeoutException
            or ObjectDisposedException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidOperationException
            or ExecAttachmentProtocolException or ConnectionProtocolException)
        {
            failure = Failure(ex is ExecAttachmentProtocolException protocol ? protocol.Kind
                : ex is JsonException or DecoderFallbackException or InvalidOperationException ? ContainerExecFailureKind.MalformedResponse
                : ex is UnauthorizedAccessException ? ContainerExecFailureKind.AccessDenied : ContainerExecFailureKind.TransportFailure);
        }
        finally
        {
            bool openingDeadline = !upgrade && opening.IsCancellationRequested && !io.IsCancellationRequested;
            lock (gate) ending = true;
            if (!eof) Interlocked.Exchange(ref revoked, 1);
            io.Cancel();
            try { await CloseAsync().ConfigureAwait(false); }
            catch (Exception) { failure ??= Failure(ContainerExecFailureKind.TransportFailure); }
            Task? writing; lock (gate) writing = input;
            if (writing is not null) await writing.ConfigureAwait(false);
            if (stop.IsCancellationRequested) failure = inputFailure ?? Failure(CancellationKind());
            else if (openingDeadline) failure = Failure(ContainerExecFailureKind.Deadline);
            if (!owner.HasContinuity) { Interlocked.Exchange(ref revoked, 1); failure = Failure(CancellationKind()); }
            if (Volatile.Read(ref revoked) != 0) while (channel.Reader.TryRead(out _)) { }
            channel.Writer.TryComplete();
            evidence ??= preflightEvidence;
            // A rejected HTTP response remains historical rejection even if its body read was canceled/lost.
            opened.TrySetResult(new(!dispatched ? ContainerExecStartDisposition.NotDispatched : status >= 400
                ? ContainerExecStartDisposition.DaemonRejected : ContainerExecStartDisposition.Uncertain,
                Response: evidence, Failure: failure, UpgradeObserved: upgrade));
            // Retain owner/generation fencing after EOF; delivery checks the owner directly.
            if (owner.Continuity is { } continuity) continuity.Reopened -= Invalidate;
            stopped.Dispose();
            released(this);
        }
        return new(eof && failure is null ? ContainerExecAttachmentTermination.EndOfStream
            : failure?.Kind is ContainerExecFailureKind.Canceled or ContainerExecFailureKind.ConnectionDisposed
                ? ContainerExecAttachmentTermination.Canceled : ContainerExecAttachmentTermination.Failed, failure);
    }

    private async Task PreflightAsync(DockerExecWire wire, CancellationToken token)
    {
        async Task<byte[]> Get(string path)
        {
            EnsureCurrent(); token.ThrowIfCancellationRequested();
            await wire.SendAsync("GET", path, null, false, token).ConfigureAwait(false);
            var header = await wire.ReadHeaderAsync(token).ConfigureAwait(false);
            if (header.Status != 200)
            {
                preflightEvidence = new(header.Status, [], true);
                using var prefix = new MemoryStream();
                try
                {
                    var error = await wire.BodyAsync(header, options.MaximumCommandErrorBytes, false, token,
                        bytes => prefix.Write(bytes.Span)).ConfigureAwait(false);
                    preflightEvidence = Evidence(header.Status, error.Bytes, error.Truncated);
                }
                catch { preflightEvidence = Evidence(header.Status, prefix.ToArray(), true); throw; }
                throw new ExecAttachmentProtocolException(HttpFailure(header.Status));
            }
            var body = await wire.BodyAsync(header, options.MaximumResponseBytes, true, token).ConfigureAwait(false);
            if (body.Truncated) throw DockerExecWire.Bad();
            EnsureCurrent(); token.ThrowIfCancellationRequested(); return body.Bytes;
        }
        if (Encoding.UTF8.GetString(await Get("_ping").ConfigureAwait(false)).Trim() != "OK") throw DockerExecWire.Bad();
        using var version = Json(await Get("version").ConfigureAwait(false));
        var selected = owner.Metadata; var v = version.RootElement;
        if (Text(v, "Version") != selected.EngineVersion || Text(v, "Os") != selected.RuntimeOS
            || Text(v, "Arch") != selected.RuntimeArchitecture
            || Text(v, "ApiVersion") != selected.Api.DaemonMaximum.ToString()
            || Text(v, "MinAPIVersion") != selected.Api.DaemonMinimum.ToString())
            throw new ExecAttachmentProtocolException(ContainerExecFailureKind.StaleConnection);
        using var info = Json(await Get($"v{selected.Api.Negotiated}/info").ConfigureAwait(false));
        var i = info.RootElement;
        if (string.IsNullOrWhiteSpace(selected.EngineId) || Text(i, "ID") != selected.EngineId
            || Text(i, "OSType") != selected.OSType || Text(i, "ServerVersion") != selected.InfoServerVersion)
            throw new ExecAttachmentProtocolException(ContainerExecFailureKind.StaleConnection);
        using var inspection = Json(await Get($"v{selected.Api.Negotiated}/exec/{Instance.ExecId}/json").ConfigureAwait(false));
        var exec = inspection.RootElement;
        if (Text(exec, "ID") != Instance.ExecId || Text(exec, "ContainerID") != Instance.ContainerId
            || !exec.TryGetProperty("ProcessConfig", out var process) || process.ValueKind != JsonValueKind.Object
            || !True(process, "tty") || !True(exec, "OpenStdin") || !True(exec, "OpenStdout") || !True(exec, "OpenStderr"))
            throw new ExecAttachmentProtocolException(ContainerExecFailureKind.InvalidRequest);
    }
    private static JsonDocument Json(byte[] bytes)
    {
        _ = new UTF8Encoding(false, true).GetString(bytes);
        var json = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
        try { Validate(json.RootElement); return json; } catch { json.Dispose(); throw; }
        static void Validate(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject()) { if (!names.Add(property.Name)) throw DockerExecWire.Bad(); Validate(property.Value); }
            }
            else if (root.ValueKind == JsonValueKind.Array) foreach (var item in root.EnumerateArray()) Validate(item);
        }
    }
    private static string? Text(JsonElement root, string key) => root.TryGetProperty(key, out var field) && field.ValueKind != JsonValueKind.Null ? field.GetString() : null;
    private static bool True(JsonElement root, string key) => root.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
    private static ContainerExecResponseEvidence Evidence(int status, byte[] bytes, bool truncated)
    {
        string? message = null;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes); message = text;
            using var json = JsonDocument.Parse(bytes); message = Text(json.RootElement, "message") ?? text;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException) { }
        return new(status, ImmutableArray.CreateRange(bytes), truncated, message);
    }
    private static ContainerExecFailureKind HttpFailure(int status) => status switch
    {
        404 => ContainerExecFailureKind.NotFound, 409 => ContainerExecFailureKind.Conflict,
        401 or 403 => ContainerExecFailureKind.AccessDenied, >= 400 and < 500 => ContainerExecFailureKind.InvalidRequest,
        >= 500 => ContainerExecFailureKind.DaemonFailure, _ => ContainerExecFailureKind.UnexpectedResponse
    };
    private Task CloseAsync()
    {
        lock (gate)
        {
            // An opening transport may still be noncooperatively returning; it is closed when assigned.
            if (stream is null) return Task.CompletedTask;
            return close ??= Task.Run(async () => await stream.DisposeAsync().ConfigureAwait(false));
        }
    }
    public Task<ContainerExecWriteResult> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.NotWritten, Failure(ContainerExecFailureKind.Canceled)));
            if (bytes.Length > options.MaximumExecInputBytes) return Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.NotWritten, Failure(ContainerExecFailureKind.InvalidRequest)));
            if (!established || ending || !CurrentDelivery() || stop.IsCancellationRequested) return Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.NotWritten, Failure(CancellationKind())));
            if (input is { IsCompleted: false }) return Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.NotWritten, Failure(ContainerExecFailureKind.CapacityExceeded)));
            // Publish a join before transport code can reenter cancellation/disposal.
            var done = new TaskCompletionSource<ContainerExecWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input = done.Task;
            var snapshot = bytes.ToArray();
            _ = Task.Run(async () => done.TrySetResult(await WriteCoreAsync(snapshot, cancellationToken).ConfigureAwait(false)));
            return input;
        }
    }
    private async Task<ContainerExecWriteResult> WriteCoreAsync(byte[] bytes, CancellationToken caller)
    {
        bool dispatched = false;
        using var token = CancellationTokenSource.CreateLinkedTokenSource(caller, io.Token);
        try
        {
            EnsureCurrent(); token.Token.ThrowIfCancellationRequested();
            dispatched = true;
            using var canceledWrite = caller.Register(() => stop.Cancel());
            await stream!.WriteAsync(bytes, token.Token).ConfigureAwait(false);
            await stream.FlushAsync(token.Token).ConfigureAwait(false);
            EnsureCurrent(); token.Token.ThrowIfCancellationRequested();
            return new(ContainerExecWriteDisposition.Written);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ExecAttachmentProtocolException
            or UnauthorizedAccessException or TimeoutException or HttpRequestException or InvalidOperationException)
        {
            var kind = caller.IsCancellationRequested ? ContainerExecFailureKind.Canceled
                : !owner.HasContinuity || io.IsCancellationRequested ? CancellationKind()
                : ex is ExecAttachmentProtocolException protocol ? protocol.Kind : ContainerExecFailureKind.TransportFailure;
            var failure = Failure(kind);
            if (dispatched)
            {
                lock (gate) inputFailure = failure;
                stop.Cancel();
            }
            return new(dispatched ? ContainerExecWriteDisposition.Uncertain : ContainerExecWriteDisposition.NotWritten, failure);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); disposal = done.Task;
            Interlocked.Exchange(ref revoked, 1); stop.Cancel();
            _ = Task.Run(async () => { try { await producer.ConfigureAwait(false); while (channel.Reader.TryRead(out _)) { } done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } });
            return new(disposal);
        }
    }
    private sealed class CurrentReader(DockerExecAttachment attachment, ChannelReader<ImmutableArray<byte>> inner) : ChannelReader<ImmutableArray<byte>>
    {
        public override Task Completion => inner.Completion;
        public override bool TryRead(out ImmutableArray<byte> item)
        {
            if (attachment.CurrentDelivery() && inner.TryRead(out item) && attachment.CurrentDelivery()) return true;
            item = default; return false;
        }
        public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            => attachment.CurrentDelivery() && await inner.WaitToReadAsync(cancellationToken).ConfigureAwait(false) && attachment.CurrentDelivery();
    }
}
