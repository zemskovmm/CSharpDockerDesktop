using System.Text;
using System.Net.Sockets;
using System.Text.Json;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>Thin Engine handshake. A successful session owns its transport and borrowed resource capabilities.</summary>
public sealed class DockerEngineConnectionClient : IEngineConnectionClient
{
    // Increase only with schema compatibility evidence, not from the daemon's advertised maximum.
    public static EngineApiVersion MinimumApi { get; } = new(1, 40);
    public static EngineApiVersion MaximumApi { get; } = new(1, 56);
    private readonly DockerConnectionOptions options;
    private readonly IDockerTransportFactory transport;

    public DockerEngineConnectionClient(DockerConnectionOptions? options = null)
        : this(new LocalDockerTransport(), options) { }

    internal DockerEngineConnectionClient(IDockerTransportFactory transport, DockerConnectionOptions? options = null)
    {
        this.transport = transport;
        this.options = options ?? new();
        this.options.Validate();
    }

    public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        endpoint ??= LocalDockerTransport.DefaultEndpoint;
        HttpClient? http = null;
        try
        {
            // Validation is shared by all transports, including deterministic test transports.
            LocalDockerTransport.Validate(endpoint);
            var handler = transport.Create(endpoint, options);
            var continuity = handler as ITransportContinuity;
            http = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri("http://docker/"), // Logical HTTP authority only; ConnectCallback opens the local transport.
                Timeout = Timeout.InfiniteTimeSpan
            };
            var ping = await GetAsync(http, "_ping", cancellationToken).ConfigureAwait(false);
            if (Encoding.UTF8.GetString(ping).Trim() != "OK") throw Malformed("The Engine readiness response is not OK.");
            var pipeGeneration = continuity?.ConnectionGeneration;
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
            if (continuity?.ConnectionGeneration != pipeGeneration
                || continuity is not null && !continuity.Seal(pipeGeneration!.Value))
                throw TransportReopened();
            var connection = new DockerEngineConnection(http, metadata, this, continuity, pipeGeneration, options);
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

    internal ValueTask<Stream> OpenExecTransportAsync(EngineEndpoint endpoint, CancellationToken token)
        => transport is IDockerDuplexTransportFactory duplex ? duplex.OpenDuplexAsync(endpoint, options, token)
            : ValueTask.FromException<Stream>(new IOException("This transport does not support owned duplex attachments."));

    internal async Task<EngineConnectionFailure?> CheckLivenessAsync(HttpClient http, ITransportContinuity? continuity, long? generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (continuity?.ConnectionGeneration != generation) throw TransportReopened();
            var ping = await GetAsync(http, "_ping", token).ConfigureAwait(false);
            if (continuity?.ConnectionGeneration != generation) throw TransportReopened();
            if (Encoding.UTF8.GetString(ping).Trim() != "OK") throw Malformed("The Engine readiness response is not OK.");
            token.ThrowIfCancellationRequested();
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (TryDescribeFailure(ex, out _))
        {
            token.ThrowIfCancellationRequested();
            TryDescribeFailure(ex, out var failure);
            return failure;
        }
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
    private static ConnectionProtocolException TransportReopened() => new(new(EngineConnectionFailureKind.TransientTransportFailure,
        "The Engine transport reopened. A new connection must negotiate current Engine metadata."));
    private static ConnectionProtocolException Malformed(string description) => new(new(EngineConnectionFailureKind.MalformedResponse, description));
    internal static bool TryDescribeFailure(Exception exception, out EngineConnectionFailure? failure)
    {
        failure = null;
        // SocketsHttpHandler wraps ConnectCallback failures. Preserve their classification.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConnectionProtocolException protocol) { failure = protocol.Failure; return true; }
            if (current is UnauthorizedAccessException || (current is IOException && (current.HResult & 0xffff) == 5))
            { failure = new(EngineConnectionFailureKind.AccessDenied, "Access to the Docker endpoint was denied. Check the current user's Engine permissions; no elevation or permission changes were attempted."); return true; }
            if (current is SocketException socket)
            {
                if (socket.SocketErrorCode == SocketError.AccessDenied)
                { failure = new(EngineConnectionFailureKind.AccessDenied, "Access to the Docker socket was denied. Check the current user's Engine permissions."); return true; }
                if (socket.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.ConnectionRefused || socket.NativeErrorCode == 2)
                { failure = new(EngineConnectionFailureKind.MissingEndpoint, "The Docker socket is missing or the Engine is not listening. Start the Engine or check the selected socket."); return true; }
            }
            if (current is FileNotFoundException or DirectoryNotFoundException)
            { failure = new(EngineConnectionFailureKind.MissingEndpoint, "The selected Docker endpoint does not exist."); return true; }
        }
        if (exception is HttpRequestException or IOException or OperationCanceledException or TimeoutException)
        { failure = new(EngineConnectionFailureKind.TransientTransportFailure, "The Engine connection was interrupted or unavailable."); return true; }
        return false;
    }
}

internal sealed partial class DockerEngineConnection(HttpClient http, EngineMetadata metadata, DockerEngineConnectionClient client, ITransportContinuity? continuity, long? generation, DockerConnectionOptions options) : IEngineConnection, IContainerReader, IImageReader, IVolumeReader, INetworkReader, IVolumeDependencyReader, IEngineEventSource, IContainerCommands, IImageCommands, IVolumeCommands, INetworkCommands, IContainerLogSource, IContainerStatsSource, IContainerExec
{
    private int disposed;
    private readonly SemaphoreSlim commandSlots = new(options.MaximumConcurrentCommands);
    private readonly SemaphoreSlim volumeDependencySlots = new(options.MaximumConcurrentVolumeDependencyQueries);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object eventGate = new();
    private DockerEventSubscription? eventSubscription;
    private Task? disposal;
    private readonly HashSet<DockerLogSubscription> logSubscriptions = [];
    private readonly HashSet<DockerStatsSubscription> statsSubscriptions = [];
    internal HttpClient Http => http;
    internal ITransportContinuity? Continuity => continuity;
    // A subscription closes its dedicated pool before consumers finish draining. Local disposal
    // ends requests, but does not invalidate observations from this negotiated generation.
    internal bool HasTransportContinuity => continuity?.ConnectionGeneration == generation;
    internal bool HasContinuity => HasTransportContinuity && Volatile.Read(ref disposed) == 0;

    public async Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = since?.ToString(); // Validate before allocating any transport/work.
        DockerEventSubscription subscription;
        lock (eventGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!HasContinuity)
                return EngineEventSubscriptionAttempt.Failed(new(EngineEventFailureKind.TransportLoss,
                    "The Engine transport changed; negotiate a new connection before observing events."));
            if (eventSubscription is not null)
                return EngineEventSubscriptionAttempt.Failed(new(EngineEventFailureKind.SubscriptionLimit,
                    "Dispose the existing event subscription before opening another."));
            subscription = new DockerEventSubscription(this, client, options, cursor, lifetime.Token, cancellationToken,
                ended => { lock (eventGate) { if (ReferenceEquals(eventSubscription, ended)) eventSubscription = null; } });
            eventSubscription = subscription;
            subscription.Start();
        }
        var result = await subscription.Opened.ConfigureAwait(false);
        if (!result.IsSubscribed || cancellationToken.IsCancellationRequested || !HasContinuity)
            await subscription.DisposeAsync().ConfigureAwait(false);
        if (result.IsSubscribed && !HasContinuity)
            result = EngineEventSubscriptionAttempt.Failed(new(EngineEventFailureKind.TransportLoss, "The selected connection ended while opening events."));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    public async Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || string.IsNullOrWhiteSpace(request.ContainerId) || request.ContainerId.Length > 4096
            || request.ContainerId is "." or ".." || request.TailLines < 0 || request.TailLines > options.MaximumLogTailLines
            || request.Since is { IsValid: false })
            return ContainerLogSubscriptionAttempt.Failed(new(ContainerLogFailureKind.InvalidRequest,
                "Logs require a valid container identity, finite bounded tail, and valid timestamp cursor."));
        DockerLogSubscription subscription;
        lock (eventGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!HasContinuity)
                return ContainerLogSubscriptionAttempt.Failed(new(ContainerLogFailureKind.TransportLoss,
                    "The selected connection changed; negotiate a new connection before opening logs."));
            if (logSubscriptions.Count >= options.MaximumConcurrentLogSubscriptions)
                return ContainerLogSubscriptionAttempt.Failed(new(ContainerLogFailureKind.SubscriptionLimit,
                    "Dispose an existing logs subscription before opening another."));
            subscription = new(this, client, options, request, lifetime.Token, cancellationToken,
                ended => { lock (eventGate) logSubscriptions.Remove(ended); });
            logSubscriptions.Add(subscription);
            subscription.Start();
        }
        var result = await subscription.Opened.ConfigureAwait(false);
        if (!result.IsSubscribed || cancellationToken.IsCancellationRequested || !HasContinuity)
            await subscription.DisposeAsync().ConfigureAwait(false);
        if (result.IsSubscribed && !HasContinuity)
            result = ContainerLogSubscriptionAttempt.Failed(new(ContainerLogFailureKind.TransportLoss,
                "The selected connection ended while opening logs."));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    public async Task<ContainerStatsSubscriptionAttempt> SubscribeStatsAsync(string containerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(containerId) || containerId.Length > 4096 || containerId is "." or ".."
            || containerId.Any(char.IsControl) || !ValidStatsIdentity(containerId)
            || containerId.Any(char.IsWhiteSpace))
            return ContainerStatsSubscriptionAttempt.Failed(new(ContainerStatsFailureKind.InvalidRequest,
                "Stats requires a non-empty bounded exact container identity without whitespace or controls."));
        DockerStatsSubscription subscription;
        lock (eventGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!HasContinuity)
                return ContainerStatsSubscriptionAttempt.Failed(new(ContainerStatsFailureKind.TransportLoss,
                    "The selected connection changed; negotiate a new connection before opening Stats."));
            if (statsSubscriptions.Count >= options.MaximumConcurrentStatsSubscriptions)
                return ContainerStatsSubscriptionAttempt.Failed(new(ContainerStatsFailureKind.SubscriptionLimit,
                    "Dispose an existing Stats subscription before opening another."));
            subscription = new(this, client, options, containerId, lifetime.Token, cancellationToken,
                ended => { lock (eventGate) statsSubscriptions.Remove(ended); });
            statsSubscriptions.Add(subscription);
            subscription.Start();
        }
        var result = await subscription.Opened.ConfigureAwait(false);
        if (!result.IsSubscribed || cancellationToken.IsCancellationRequested || !HasContinuity)
            await subscription.DisposeAsync().ConfigureAwait(false);
        if (result.IsSubscribed && !HasContinuity)
            result = ContainerStatsSubscriptionAttempt.Failed(new(ContainerStatsFailureKind.TransportLoss,
                "The selected connection ended while opening Stats."));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    private static bool ValidStatsIdentity(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsLowSurrogate(value[i])) return false;
            if (char.IsHighSurrogate(value[i]) && (++i >= value.Length || !char.IsLowSurrogate(value[i]))) return false;
        }
        return true;
    }
    public Task<ContainerCommandResult> StartContainerAsync(string identity, CancellationToken cancellationToken = default)
        => CommandAsync(ContainerCommand.Start, identity, null, false, cancellationToken);
    public Task<ContainerCommandResult> StopContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default)
        => CommandAsync(ContainerCommand.Stop, identity, gracePeriodSeconds, false, cancellationToken);
    public Task<ContainerCommandResult> RestartContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default)
        => CommandAsync(ContainerCommand.Restart, identity, gracePeriodSeconds, false, cancellationToken);
    public Task<ContainerCommandResult> DeleteContainerAsync(string identity, bool force = false, CancellationToken cancellationToken = default)
        => CommandAsync(ContainerCommand.Delete, identity, null, force, cancellationToken);
    private Task<ContainerCommandResult> CommandAsync(ContainerCommand command, string identity, int? grace, bool force, CancellationToken token)
        => DockerContainerCommands.ExecuteAsync(http, Metadata.Api.Negotiated, command, identity, grace, force,
            options, continuity, generation, commandSlots, lifetime.Token, token);
    public Task<ImageCommandResult> DeleteImageAsync(string reference, CancellationToken cancellationToken = default)
        => DockerImageCommands.ExecuteAsync(http, Metadata.Api.Negotiated, reference, options,
            continuity, generation, commandSlots, lifetime.Token, cancellationToken);
    public Task<VolumeCommandResult> DeleteVolumeAsync(string name, CancellationToken cancellationToken = default)
        => DockerVolumeCommands.ExecuteAsync(http, Metadata.Api.Negotiated, name, options,
            continuity, generation, commandSlots, lifetime.Token, cancellationToken);
    public Task<NetworkCommandResult> DeleteNetworkAsync(string id, CancellationToken cancellationToken = default)
        => DockerNetworkCommands.ExecuteAsync(http, Metadata.Api.Negotiated, id, options,
            continuity, generation, commandSlots, lifetime.Token, cancellationToken);
    public EngineMetadata Metadata { get; } = metadata;
    public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return client.CheckLivenessAsync(http, continuity, generation, cancellationToken);
    }
    public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, "containers/json?all=true",
            DockerContainerJson.List, options, continuity, generation, lifetime.Token, cancellationToken);
    }
    public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 4096 || identity is "." or "..")
            return Task.FromResult(EngineResourceResult<ContainerInspection>.Failed(new(EngineResourceFailureKind.InvalidRequest,
                "A non-empty container identity of at most 4096 characters is required.")));
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, $"containers/{Uri.EscapeDataString(identity)}/json",
            DockerContainerJson.Inspect, options, continuity, generation, lifetime.Token, cancellationToken);
    }
    public Task<EngineResourceResult<IReadOnlyList<ImageSummary>>> ListImagesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, "images/json?all=true&digests=true",
            DockerImageJson.List, options, continuity, generation, lifetime.Token, cancellationToken, "image");
    }
    public Task<EngineResourceResult<ImageInspection>> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!DockerImageReference.IsValid(reference))
            return Task.FromResult(EngineResourceResult<ImageInspection>.Failed(new(EngineResourceFailureKind.InvalidRequest,
                "A valid bounded image ID, repository name, tag or digest reference is required.")));
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, $"images/{Uri.EscapeDataString(reference)}/json",
            DockerImageJson.Inspect, options, continuity, generation, lifetime.Token, cancellationToken, "image");
    }
    public Task<EngineResourceResult<IReadOnlyList<NetworkSummary>>> ListNetworksAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, "networks",
            DockerNetworkJson.List, options, continuity, generation, lifetime.Token, cancellationToken, "network");
    }
    public Task<EngineResourceResult<NetworkInspection>> InspectNetworkAsync(string id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!DockerNetworkId.IsValid(id))
            return Task.FromResult(EngineResourceResult<NetworkInspection>.Failed(new(EngineResourceFailureKind.InvalidRequest,
                "A complete bounded network ID without controls or invalid Unicode is required.")));
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, $"networks/{Uri.EscapeDataString(id)}",
            body => DockerNetworkJson.Inspect(body, id), options, continuity, generation, lifetime.Token, cancellationToken, "network");
    }
    public Task<EngineResourceResult<VolumeListing>> ListVolumesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, "volumes",
            DockerVolumeJson.List, options, continuity, generation, lifetime.Token, cancellationToken, "volume");
    }
    public Task<EngineResourceResult<VolumeInspection>> InspectVolumeAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!DockerVolumeName.IsValid(name))
            return Task.FromResult(EngineResourceResult<VolumeInspection>.Failed(new(EngineResourceFailureKind.InvalidRequest,
                "A non-empty bounded volume name or ID without controls or invalid Unicode is required.")));
        return DockerResourceReader.ReadAsync(http, Metadata.Api.Negotiated, $"volumes/{Uri.EscapeDataString(name)}",
            DockerVolumeJson.Inspect, options, continuity, generation, lifetime.Token, cancellationToken, "volume");
    }
    public Task<EngineResourceResult<VolumeDependencies>> QueryVolumeDependenciesAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return DockerVolumeDependencies.QueryAsync(http, Metadata.Api.Negotiated, name, options,
            continuity, generation, volumeDependencySlots, lifetime.Token, cancellationToken);
    }
    public ValueTask DisposeAsync()
    {
        lock (eventGate)
        {
            if (disposal is not null) return new(disposal);
            // Publish the join before cancellation callbacks can synchronously reenter disposal.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            Volatile.Write(ref disposed, 1);
            lifetime.Cancel();
            http.Dispose();
            var pending = logSubscriptions.Select(subscription => subscription.DisposeAsync().AsTask()).ToList();
            pending.AddRange(execOperations);
            pending.AddRange(execAttachments.Select(attachment => attachment.DisposeAsync().AsTask()));
            pending.AddRange(statsSubscriptions.Select(subscription => subscription.DisposeAsync().AsTask()));
            if (eventSubscription is { } subscription) pending.Add(subscription.DisposeAsync().AsTask());
            _ = Task.WhenAll(pending).ContinueWith(ended =>
            {
                if (ended.Exception is { } error) completion.TrySetException(error.InnerExceptions);
                else if (ended.IsCanceled) completion.TrySetCanceled();
                else completion.TrySetResult();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Keep lifetime source usable by racing borrowed calls; it owns no wait handle.
            return new(disposal);
        }
    }
}
