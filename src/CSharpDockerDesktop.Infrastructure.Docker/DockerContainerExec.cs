using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerContainerExec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<ContainerExecCreationResult> CreateAsync(HttpClient http, EngineApiVersion api,
        ContainerExecCreateRequest request, DockerConnectionOptions options, ITransportContinuity? continuity,
        long? generation, SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        ContainerExecCreationResult result;
        if (caller.IsCancellationRequested)
            result = new(ContainerExecCreationDisposition.NotDispatched, Failure: Failure(ContainerExecFailureKind.Canceled));
        else if (!TrySnapshot(request, options, out var arguments))
            result = new(ContainerExecCreationDisposition.NotDispatched, Failure: Failure(ContainerExecFailureKind.InvalidRequest));
        else
        {
            var exchange = await ExchangeAsync(http, api, new(request.ContainerId, ""), arguments, options,
                continuity, generation, slots, lifetime, caller).ConfigureAwait(false);
            result = new(!exchange.Dispatched ? ContainerExecCreationDisposition.NotDispatched
                : exchange.Status >= 400 ? ContainerExecCreationDisposition.DaemonRejected
                : exchange.Acknowledged is not null && exchange.Failure is null ? ContainerExecCreationDisposition.Acknowledged
                : ContainerExecCreationDisposition.Uncertain, exchange.Acknowledged, exchange.Evidence, exchange.Failure);
        }
        if (caller.IsCancellationRequested) throw new ContainerExecCreateCanceledException(result with
        {
            Disposition = result.Disposition == ContainerExecCreationDisposition.Acknowledged ? ContainerExecCreationDisposition.Uncertain : result.Disposition,
            Failure = Failure(ContainerExecFailureKind.Canceled)
        }, caller);
        return result;
    }

    internal static async Task<ContainerExecInspectionResult> InspectAsync(HttpClient http, EngineApiVersion api,
        ContainerExecInstance instance, DockerConnectionOptions options, ITransportContinuity? continuity,
        long? generation, SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (instance is null || !ValidIdentity(instance.ContainerId) || !ValidIdentity(instance.ExecId))
            return new(Failure: Failure(ContainerExecFailureKind.InvalidRequest));
        var exchange = await ExchangeAsync(http, api, instance, null, options,
            continuity, generation, slots, lifetime, caller).ConfigureAwait(false);
        caller.ThrowIfCancellationRequested();
        return new(exchange.Failure is null ? exchange.Inspection : null, exchange.Evidence, exchange.Failure);
    }

    private sealed class Exchange
    {
        public bool Dispatched;
        public int? Status;
        public ImmutableArray<byte> Body = [];
        public bool BodyUnavailable = true;
        public string? DaemonMessage;
        public ContainerExecInstance? Acknowledged;
        public ContainerExecInspection? Inspection;
        public ContainerExecFailure? Failure;
        public ContainerExecResponseEvidence? Evidence => Status is { } status ? new(status, Body, BodyUnavailable, DaemonMessage) : null;
    }

    private static async Task<Exchange> ExchangeAsync(HttpClient http, EngineApiVersion api, ContainerExecInstance instance,
        string[]? arguments, DockerConnectionOptions options, ITransportContinuity? continuity, long? generation,
        SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        bool create = arguments is not null;
        var result = new Exchange();
        bool Stale() => continuity?.ConnectionGeneration != generation;
        if (caller.IsCancellationRequested) { result.Failure = Failure(ContainerExecFailureKind.Canceled); return result; }
        if (lifetime.IsCancellationRequested) { result.Failure = Failure(ContainerExecFailureKind.ConnectionDisposed); return result; }
        if (Stale()) { result.Failure = Failure(ContainerExecFailureKind.StaleConnection); return result; }
        if (!await slots.WaitAsync(0).ConfigureAwait(false))
        { result.Failure = Failure(ContainerExecFailureKind.CapacityExceeded); return result; }
        using var changed = new CancellationTokenSource();
        void Reopened() { try { changed.Cancel(); } catch (ObjectDisposedException) { } }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime, changed.Token);
        deadline.CancelAfter(create ? options.CommandTimeout : options.RequestTimeout);
        if (continuity is not null) continuity.Reopened += Reopened;
        try
        {
            var path = create ? $"containers/{Uri.EscapeDataString(instance.ContainerId)}/exec" : $"exec/{Uri.EscapeDataString(instance.ExecId)}/json";
            using var request = new HttpRequestMessage(create ? HttpMethod.Post : HttpMethod.Get, $"v{api}/{path}")
            { Version = new(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            if (create)
            {
                var body = await Task.Run(() => Serialize(arguments!), deadline.Token).ConfigureAwait(false);
                if (body.Length > options.MaximumExecRequestBytes)
                    throw new ExecRequestException();
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new("application/json");
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (Stale()) throw new IOException();
            // Conservative possible-dispatch boundary, including HTTP queuing. No retries/replacement pool.
            result.Dispatched = true;
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
            {
                result.Status = (int)response.StatusCode;
                var bytes = await ReadBoundedAsync(response.Content,
                    result.Status == (create ? 201 : 200) ? options.MaximumExecResponseBytes : options.MaximumCommandErrorBytes,
                    deadline.Token).ConfigureAwait(false);
                if (bytes is not null)
                {
                    result.Body = ImmutableArray.CreateRange(bytes);
                    result.BodyUnavailable = false;
                    // Deliberately decode bounded late evidence even if a noncooperative read ignored cancellation.
                    // It stays historical evidence; the post-cleanup fence below rejects it as a current success.
                    await Task.Run(() => Decode(result, bytes, instance, create, options)).ConfigureAwait(false);
                }
                else result.Failure = Failure(result.Status == (create ? 201 : 200)
                    ? ContainerExecFailureKind.MalformedResponse : HttpFailure(result.Status.Value));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or ConnectionProtocolException or ExecRequestException)
        {
            result.Failure = Failure(ex is ExecRequestException ? ContainerExecFailureKind.InvalidRequest
                : AccessDenied(ex) ? ContainerExecFailureKind.AccessDenied : ContainerExecFailureKind.TransportFailure);
        }
        finally
        {
            if (continuity is not null) continuity.Reopened -= Reopened;
            slots.Release();
        }
        // Check after response/request cleanup too, including callbacks captured before unsubscription.
        if (caller.IsCancellationRequested) result.Failure = Failure(ContainerExecFailureKind.Canceled);
        else if (lifetime.IsCancellationRequested) result.Failure = Failure(ContainerExecFailureKind.ConnectionDisposed);
        else if (Stale() || changed.IsCancellationRequested) result.Failure = Failure(ContainerExecFailureKind.StaleConnection);
        else if (deadline.IsCancellationRequested) result.Failure = Failure(ContainerExecFailureKind.Deadline);
        return result;
    }

    private sealed class ExecRequestException : Exception;

    private static void Decode(Exchange result, byte[] bytes, ContainerExecInstance instance, bool create, DockerConnectionOptions options)
    {
        var successStatus = create ? 201 : 200;
        try
        {
            _ = StrictUtf8.GetString(bytes);
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
            ValidateJson(json.RootElement);
            if (result.Status != successStatus)
            {
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                    result.DaemonMessage = String(json.RootElement, "message");
                result.Failure = Failure(HttpFailure(result.Status!.Value));
            }
            else
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (create)
                {
                    var id = String(root, "Id");
                    if (!ValidIdentity(id)) throw new JsonException();
                    result.Acknowledged = new(instance.ContainerId, id!);
                }
                else result.Inspection = Inspect(root, instance, options);
            }
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            // Preserve arbitrary error bytes/text, including valid JSON of an unexpected shape.
            if (result.Status != successStatus)
            {
                try { result.DaemonMessage = bytes.Length == 0 ? null : StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { }
                result.Failure = Failure(HttpFailure(result.Status!.Value));
            }
            else result.Failure = Failure(ContainerExecFailureKind.MalformedResponse);
        }
    }

    private static ContainerExecInspection Inspect(JsonElement root, ContainerExecInstance instance, DockerConnectionOptions options)
    {
        var id = String(root, "ID");
        var containerId = String(root, "ContainerID");
        if (id is not null && (!ValidIdentity(id) || id != instance.ExecId)
            || containerId is not null && (!ValidIdentity(containerId) || containerId != instance.ContainerId)) throw new JsonException();
        ContainerExecProcessConfiguration? process = null;
        if (Field(root, "ProcessConfig") is { } config)
        {
            if (config.ValueKind != JsonValueKind.Object) throw new JsonException();
            ImmutableArray<string>? arguments = null;
            if (Field(config, "arguments") is { } args)
            {
                if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() > options.MaximumExecArguments) throw new JsonException();
                var builder = ImmutableArray.CreateBuilder<string>(args.GetArrayLength());
                foreach (var arg in args.EnumerateArray())
                {
                    if (arg.ValueKind != JsonValueKind.String) throw new JsonException();
                    var value = arg.GetString()!;
                    if (StrictUtf8.GetByteCount(value) > options.MaximumExecArgumentBytes || value.Contains('\0')) throw new JsonException();
                    builder.Add(value);
                }
                arguments = builder.MoveToImmutable();
            }
            process = new(Boolean(config, "tty"), Boolean(config, "privileged"), String(config, "user"), String(config, "entrypoint"), arguments);
        }
        return new(instance, id, containerId, Boolean(root, "Running"), Integer(root, "ExitCode"), Integer(root, "Pid"),
            Boolean(root, "OpenStdin"), Boolean(root, "OpenStdout"), Boolean(root, "OpenStderr"), Boolean(root, "CanRemove"), String(root, "DetachKeys"), process);
    }

    private static JsonElement? Field(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    private static string? String(JsonElement root, string name) => Field(root, name) is { } value
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException() : null;
    private static bool? Boolean(JsonElement root, string name) => Field(root, name) is { } value ? value.ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new JsonException() } : null;
    private static long? Integer(JsonElement root, string name) => Field(root, name) is { } value
        ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw new JsonException() : null;

    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                ValidateJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
        else if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
    }

    // Full Docker container/exec IDs, never mutable names or ambiguous prefixes. The Engine schema says
    // string; this capability deliberately narrows admission to Moby's complete generated identity shape.
    private static bool ValidIdentity(string? identity) => identity is { Length: 64 }
        && identity.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');

    private static bool TrySnapshot(ContainerExecCreateRequest? request, DockerConnectionOptions options, out string[] arguments)
    {
        arguments = [];
        if (request is null || !ValidIdentity(request.ContainerId) || request.Arguments is null
            || request.Arguments.Count < 1 || request.Arguments.Count > options.MaximumExecArguments) return false;
        arguments = new string[request.Arguments.Count];
        long total = 0;
        for (int n = 0; n < arguments.Length; n++)
        {
            var argument = request.Arguments[n];
            if (argument is null || n == 0 && argument.Length == 0 || argument.Length > options.MaximumExecArgumentBytes || argument.Contains('\0')) return false;
            int bytes;
            try { bytes = StrictUtf8.GetByteCount(argument); }
            catch (EncoderFallbackException) { return false; }
            if (bytes > options.MaximumExecArgumentBytes || (total += bytes) > options.MaximumExecRequestBytes) return false;
            arguments[n] = argument;
        }
        return true;
    }

    private static byte[] Serialize(string[] arguments)
    {
        // Input already bounded; escaping can allocate at most six times that input before the exact wire-size check.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("AttachStdin", true); writer.WriteBoolean("AttachStdout", true); writer.WriteBoolean("AttachStderr", true);
            writer.WriteBoolean("Tty", true);
            writer.WriteStartArray("Cmd"); foreach (var argument in arguments) writer.WriteStringValue(argument); writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) return null;
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, maximum + 1)];
        while (true)
        {
            int count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximum + 1L - buffer.Length)), token).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > maximum) return null;
            buffer.Write(chunk, 0, count);
        }
    }

    private static bool AccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is UnauthorizedAccessException || current is IOException && (current.HResult & 0xffff) == 5) return true;
        return false;
    }
    private static ContainerExecFailureKind HttpFailure(int status) => status switch
    {
        404 => ContainerExecFailureKind.NotFound, 409 => ContainerExecFailureKind.Conflict,
        401 or 403 => ContainerExecFailureKind.AccessDenied, >= 400 and < 500 => ContainerExecFailureKind.InvalidRequest,
        >= 500 => ContainerExecFailureKind.DaemonFailure, _ => ContainerExecFailureKind.UnexpectedResponse
    };
    private static ContainerExecFailure Failure(ContainerExecFailureKind kind) => new(kind, kind switch
    {
        ContainerExecFailureKind.InvalidRequest => "Supply exact bounded Engine identities and an explicit bounded argument vector.",
        ContainerExecFailureKind.MalformedResponse => "The Engine exec response was malformed, mismatched, incomplete, or exceeded its budget.",
        ContainerExecFailureKind.Canceled => "The exec request was canceled.",
        ContainerExecFailureKind.Deadline => "The exec request exceeded its deadline.",
        ContainerExecFailureKind.StaleConnection => "The negotiated Engine transport changed; obtain a new connection before further requests.",
        ContainerExecFailureKind.ConnectionDisposed => "The owning Engine connection was disposed.",
        ContainerExecFailureKind.CapacityExceeded => "The connection's exec request capacity is occupied.",
        ContainerExecFailureKind.TransportFailure => "The exec response was interrupted or unavailable.",
        ContainerExecFailureKind.AccessDenied => "Access to the Engine was denied.",
        _ => "The Engine returned an unsuccessful status for the exec request."
    });
}
