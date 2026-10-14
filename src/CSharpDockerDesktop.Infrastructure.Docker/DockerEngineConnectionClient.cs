using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>Thin, read-only Engine handshake. A successful session owns its HTTP transport.</summary>
public sealed class DockerEngineConnectionClient : IEngineConnectionClient
{
    // Increase only with schema compatibility evidence, not from the daemon's advertised maximum.
    public static EngineApiVersion MinimumApi { get; } = new(1, 40);
    public static EngineApiVersion MaximumApi { get; } = new(1, 56);
    private readonly DockerConnectionOptions options;
    private readonly IDockerTransportFactory transport;

    public DockerEngineConnectionClient(DockerConnectionOptions? options = null)
        : this(new NamedPipeTransport(), options) { }

    internal DockerEngineConnectionClient(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        this.transport = transport;
        this.options = options ?? new();
        this.options.Validate();
    }

    public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        endpoint ??= EngineEndpoint.LocalWindows;
        HttpClient? http = null;
        try
        {
            // Validation is shared by all transports, including deterministic test transports.
            NamedPipeTransport.GetLocalPipeName(endpoint);
            http = new HttpClient(transport.Create(endpoint, options), disposeHandler: true)
            {
                BaseAddress = new Uri("http://docker/"), // Logical HTTP authority only; ConnectCallback opens the pipe.
                Timeout = Timeout.InfiniteTimeSpan
            };
            var ping = await GetAsync(http, "_ping", cancellationToken).ConfigureAwait(false);
            if (Encoding.UTF8.GetString(ping).Trim() != "OK") throw Malformed("The Engine readiness response is not OK.");
            using var version = Parse(await GetAsync(http, "version", cancellationToken).ConfigureAwait(false));
            var v = version.RootElement;
            var engineVersion = RequiredString(v, "Version");
            if (!EngineApiVersion.TryParse(RequiredString(v, "ApiVersion"), out var daemonMax)
                || !EngineApiVersion.TryParse(RequiredString(v, "MinAPIVersion"), out var daemonMin)
                || daemonMin.CompareTo(daemonMax) > 0) throw Malformed("The Engine API range is missing or invalid.");
            var negotiated = daemonMax.CompareTo(MaximumApi) < 0 ? daemonMax : MaximumApi;
            var minimum = daemonMin.CompareTo(MinimumApi) > 0 ? daemonMin : MinimumApi;
            if (minimum.CompareTo(negotiated) > 0)
                throw new ConnectionProtocolException(new(EngineConnectionFailureKind.IncompatibleApi,
                    $"Engine API {daemonMin}–{daemonMax} does not overlap client API {MinimumApi}–{MaximumApi}."));
            var api = new EngineApiCompatibility(MinimumApi, MaximumApi, daemonMin, daemonMax, negotiated);
            using var info = Parse(await GetAsync(http, $"v{negotiated}/info", cancellationToken).ConfigureAwait(false));
            var i = info.RootElement;
            string? swarmState = null;
            if (i.TryGetProperty("Swarm", out var swarm) && swarm.ValueKind != JsonValueKind.Null)
            {
                if (swarm.ValueKind != JsonValueKind.Object) throw Malformed("Engine field Swarm is not an object.");
                swarmState = OptionalString(swarm, "LocalNodeState");
            }
            var capabilities = new EngineCapabilities(OptionalBool(i, "MemoryLimit"), OptionalBool(i, "SwapLimit"),
                OptionalBool(i, "CpuCfsPeriod"), OptionalBool(i, "CpuCfsQuota"), OptionalBool(i, "CPUShares"),
                OptionalBool(i, "CPUSet"), OptionalBool(i, "PidsLimit"), OptionalBool(i, "IPv4Forwarding"),
                OptionalBool(i, "ExperimentalBuild") ?? OptionalBool(v, "Experimental"), swarmState);
            var metadata = new EngineMetadata(endpoint, engineVersion, api, OptionalString(v, "Os"), OptionalString(v, "Arch"),
                OptionalString(v, "KernelVersion"), OptionalString(i, "ID"), OptionalString(i, "Name"),
                OptionalString(i, "OperatingSystem"), OptionalString(i, "OSType"), OptionalString(i, "Architecture"),
                OptionalString(i, "ServerVersion"), capabilities);
            cancellationToken.ThrowIfCancellationRequested();
            var connection = new DockerEngineConnection(http, metadata);
            http = null; // Transfer sole ownership to the successful session.
            return EngineConnectionAttempt.Connected(connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (TryDescribeFailure(ex, out _))
        {
            // Cancellation takes precedence even when a transport wraps it in HttpRequestException.
            cancellationToken.ThrowIfCancellationRequested();
            TryDescribeFailure(ex, out var failure);
            return EngineConnectionAttempt.Failed(failure!);
        }
        finally { http?.Dispose(); }
    }

    private async Task<byte[]> GetAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            byte[] body;
            try { body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false); }
            catch (ConnectionProtocolException ex) when (!response.IsSuccessStatusCode && ex.Failure.Kind == EngineConnectionFailureKind.MalformedResponse)
            {
                // Even an oversized error body is still an HTTP failure. Keep the status; do not retain unbounded content.
                throw new ConnectionProtocolException(new(EngineConnectionFailureKind.DaemonHttpError,
                    $"The Engine returned HTTP {(int)response.StatusCode}; its error body exceeds the configured size limit.", (int)response.StatusCode));
            }
            if (!response.IsSuccessStatusCode)
            {
                string? message;
                try
                {
                    using var error = JsonDocument.Parse(body);
                    message = error.RootElement.ValueKind == JsonValueKind.Object
                        && error.RootElement.TryGetProperty("message", out var field) && field.ValueKind == JsonValueKind.String
                        ? field.GetString() : null;
                }
                catch (JsonException) { message = body.Length == 0 ? null : Encoding.UTF8.GetString(body); }
                throw new ConnectionProtocolException(new(EngineConnectionFailureKind.DaemonHttpError,
                    $"The Engine returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode, message));
            }
            return body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectionProtocolException(new(EngineConnectionFailureKind.TransientTransportFailure, "The Engine request exceeded its deadline."));
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > options.MaximumResponseBytes) throw Malformed("The Engine response exceeds the configured size limit.");
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > options.MaximumResponseBytes) throw Malformed("The Engine response exceeds the configured size limit.");
            buffer.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    private static JsonDocument Parse(byte[] body)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw Malformed("The Engine response is not a JSON object.");
        }
        catch (JsonException) { throw Malformed("The Engine returned malformed JSON."); }
    }
    private static string RequiredString(JsonElement element, string name)
        => OptionalString(element, name) is { Length: > 0 } value ? value : throw Malformed($"Engine field {name} is missing or empty.");
    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Malformed($"Engine field {name} is not a string.");
        return value.GetString();
    }
    private static bool? OptionalBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Malformed($"Engine field {name} is not a boolean.") };
    }
    private static ConnectionProtocolException Malformed(string description) => new(new(EngineConnectionFailureKind.MalformedResponse, description));
    private static bool TryDescribeFailure(Exception exception, out EngineConnectionFailure? failure)
    {
        failure = null;
        // SocketsHttpHandler wraps ConnectCallback failures. Preserve their classification.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConnectionProtocolException protocol) { failure = protocol.Failure; return true; }
            if (current is UnauthorizedAccessException || (current is IOException && (current.HResult & 0xffff) == 5))
            { failure = new(EngineConnectionFailureKind.AccessDenied, "Access to the Docker pipe was denied. Check the current user's Engine permissions; no elevation or permission changes were attempted."); return true; }
            if (current is FileNotFoundException or DirectoryNotFoundException)
            { failure = new(EngineConnectionFailureKind.MissingEndpoint, "The selected Docker pipe does not exist."); return true; }
        }
        if (exception is HttpRequestException or IOException or OperationCanceledException or TimeoutException)
        { failure = new(EngineConnectionFailureKind.TransientTransportFailure, "The Engine connection was interrupted or unavailable."); return true; }
        return false;
    }
}

internal sealed class DockerEngineConnection(HttpClient http, EngineMetadata metadata) : IEngineConnection
{
    private int disposed;
    public EngineMetadata Metadata { get; } = metadata;
    // Reserved for protocol operations behind future application contracts; never exposed to Presentation.
    internal HttpClient Http { get; } = http;
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Http.Dispose();
        return ValueTask.CompletedTask;
    }
}
