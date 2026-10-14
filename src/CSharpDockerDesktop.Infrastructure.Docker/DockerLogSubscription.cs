using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>Independent freshly negotiated log transport, with bounded opening and delivery.</summary>
internal sealed class DockerLogSubscription : IContainerLogSubscription
{
    private readonly DockerEngineConnection owner;
    private readonly DockerEngineConnectionClient client;
    private readonly DockerConnectionOptions options;
    private readonly ContainerLogRequest request;
    private readonly CancellationTokenSource stop;
    private readonly Action<DockerLogSubscription> released;
    private readonly Channel<ContainerLogOutput> channel;
    private readonly TaskCompletionSource<ContainerLogSubscriptionAttempt> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private Task<ContainerLogCompletion> producer = null!;
    private Task? disposal;
    private DockerEngineConnection? streamConnection;
    private int disposed, invalidated, streamEnded;

    internal DockerLogSubscription(DockerEngineConnection owner, DockerEngineConnectionClient client,
        DockerConnectionOptions options, ContainerLogRequest request, CancellationToken lifetime, CancellationToken caller,
        Action<DockerLogSubscription> released)
    {
        (this.owner, this.client, this.options, this.request, this.released) = (owner, client, options, request, released);
        ContainerId = request.ContainerId;
        stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
        channel = Channel.CreateBounded<ContainerLogOutput>(new BoundedChannelOptions(options.LogBufferCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false });
        Output = new CurrentReader(this, channel.Reader);
        if (owner.Continuity is { } continuity) continuity.Reopened += Invalidate;
    }

    public string ContainerId { get; private set; }
    public bool IsTty { get; private set; }
    public ChannelReader<ContainerLogOutput> Output { get; }
    public Task<ContainerLogCompletion> Completion => producer;
    internal Task<ContainerLogSubscriptionAttempt> Opened => opened.Task;
    internal void Start() => producer = Task.Run(ProduceAsync);
    private bool Current()
    {
        if (Volatile.Read(ref disposed) != 0 || stop.IsCancellationRequested) return false;
        if (owner.HasContinuity && (Volatile.Read(ref streamEnded) != 0 || streamConnection is null || streamConnection.HasContinuity)) return true;
        Invalidate(); return false;
    }
    private void Invalidate()
    {
        lock (gate)
        {
            if (disposed != 0) return;
            Volatile.Write(ref invalidated, 1); stop.Cancel();
        }
    }
    private void EnsureCurrent()
    {
        if (Current()) return;
        stop.Token.ThrowIfCancellationRequested();
        throw new LogProtocolException(new(ContainerLogFailureKind.TransportLoss, "The selected Engine transport changed."));
    }

    private async Task<ContainerLogCompletion> ProduceAsync()
    {
        ContainerLogFailure? failure = null;
        HttpResponseMessage? response = null;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            opening.CancelAfter(options.LogOpeningTimeout);
            try
            {
                EnsureCurrent();
                var attempt = await client.ConnectAsync(owner.Metadata.Endpoint, opening.Token).ConfigureAwait(false);
                if (attempt.Connection is not DockerEngineConnection connection)
                    throw new LogProtocolException(FromConnectionFailure(attempt.Failure!));
                streamConnection = connection;
                if (connection.Continuity is { } continuity) continuity.Reopened += Invalidate;
                var selected = owner.Metadata;
                var observed = connection.Metadata;
                if (selected.EngineId != observed.EngineId || selected.EngineVersion != observed.EngineVersion
                    || selected.Api != observed.Api || selected.OSType != observed.OSType)
                    throw new LogProtocolException(new(ContainerLogFailureKind.TransportLoss,
                        "Logs transport metadata differs from the selected connection; negotiate a new connection."));
                EnsureCurrent();
                await InspectAsync(connection, opening.Token).ConfigureAwait(false);
                var path = $"v{selected.Api.Negotiated}/containers/{Uri.EscapeDataString(ContainerId)}/logs"
                    + $"?stdout=true&stderr=true&timestamps=true&details=false&tail={request.TailLines.ToString(CultureInfo.InvariantCulture)}"
                    + "&follow=" + (request.Follow ? "true" : "false")
                    + (request.Since is { } since ? "&since=" + Uri.EscapeDataString(since.ToString()) : "");
                using var message = new HttpRequestMessage(HttpMethod.Get, path);
                response = await connection.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, opening.Token).ConfigureAwait(false);
                EnsureCurrent();
                if (!response.IsSuccessStatusCode)
                    throw new LogProtocolException(await HttpFailureAsync(response, opening.Token).ConfigureAwait(false));
                var media = response.Content.Headers.ContentType?.MediaType;
                if (media is not null && media != "application/vnd.docker.raw-stream"
                    && (!IsTty && media != "application/vnd.docker.multiplexed-stream" || IsTty))
                    throw new LogProtocolException(new(ContainerLogFailureKind.MalformedOutput, "The Engine returned an unsupported logs stream format."));
                var stream = await response.Content.ReadAsStreamAsync(opening.Token).ConfigureAwait(false);
                opening.Token.ThrowIfCancellationRequested(); EnsureCurrent();
                opening.CancelAfter(Timeout.InfiniteTimeSpan);
                opened.TrySetResult(ContainerLogSubscriptionAttempt.Subscribed(this));
                await DockerLogDecoder.DecodeAsync(stream, IsTty, options, async output =>
                {
                    EnsureCurrent();
                    await channel.Writer.WriteAsync(output, stop.Token).ConfigureAwait(false);
                    EnsureCurrent();
                }, stop.Token).ConfigureAwait(false);
                EnsureCurrent();
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested && opening.IsCancellationRequested)
            { failure = new(ContainerLogFailureKind.OpeningDeadline, "Opening container logs exceeded its deadline."); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (Volatile.Read(ref invalidated) != 0)
                failure = new(ContainerLogFailureKind.TransportLoss, "The Engine transport changed; a fresh negotiation is required.");
        }
        catch (LogProtocolException ex) { failure = ex.Failure; }
        catch (DecoderFallbackException) { failure = new(ContainerLogFailureKind.MalformedOutput, "The Engine returned malformed UTF-8 logs."); }
        catch (JsonException) { failure = new(ContainerLogFailureKind.MalformedOutput, "The Engine returned malformed container configuration."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            if (!stop.IsCancellationRequested)
            {
                DockerEngineConnectionClient.TryDescribeFailure(ex, out var described);
                failure = new(described?.Kind == EngineConnectionFailureKind.AccessDenied ? ContainerLogFailureKind.AccessDenied
                    : ContainerLogFailureKind.TransportLoss, "The Engine logs transport was interrupted or unavailable.");
            }
        }
        finally
        {
            if (owner.Continuity is { } ownerContinuity) ownerContinuity.Reopened -= Invalidate;
            if (streamConnection?.Continuity is { } streamContinuity) streamContinuity.Reopened -= Invalidate;
            Volatile.Write(ref streamEnded, 1);
            response?.Dispose();
            if (streamConnection is { } connection) await connection.DisposeAsync().ConfigureAwait(false);
            if (failure?.Kind == ContainerLogFailureKind.TransportLoss) Invalidate();
            if (stop.IsCancellationRequested) while (channel.Reader.TryRead(out _)) { }
            if (stop.IsCancellationRequested && Volatile.Read(ref invalidated) == 0) failure = null;
            channel.Writer.TryComplete();
            opened.TrySetResult(ContainerLogSubscriptionAttempt.Failed(failure ?? new(ContainerLogFailureKind.TransportLoss,
                "The logs opening ended before a subscription was available.")));
        }
        return failure is not null ? new(ContainerLogTermination.Failed, failure)
            : new(stop.IsCancellationRequested ? ContainerLogTermination.Canceled : ContainerLogTermination.CleanEnd);
    }

    private async Task InspectAsync(DockerEngineConnection connection, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get,
            $"v{connection.Metadata.Api.Negotiated}/containers/{Uri.EscapeDataString(request.ContainerId)}/json");
        using var response = await connection.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        EnsureCurrent();
        if (!response.IsSuccessStatusCode) throw new LogProtocolException(await HttpFailureAsync(response, token).ConfigureAwait(false));
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var (bytes, truncated) = await ReadPrefixAsync(stream, options.MaximumResponseBytes, token).ConfigureAwait(false);
        if (truncated) throw new LogProtocolException(new(ContainerLogFailureKind.MalformedOutput, "Container configuration exceeds the configured size limit."));
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 4096 || id.GetString() is "." or ".."
            || !root.TryGetProperty("Config", out var config) || config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("Tty", out var tty) || tty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new LogProtocolException(new(ContainerLogFailureKind.MalformedOutput, "Container identity or authoritative TTY configuration is unavailable."));
        ContainerId = id.GetString()!; IsTty = tty.GetBoolean();
        if (root.TryGetProperty("HostConfig", out var host) && host.ValueKind == JsonValueKind.Object
            && host.TryGetProperty("LogConfig", out var logging) && logging.ValueKind == JsonValueKind.Object
            && logging.TryGetProperty("Type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "none")
            throw new LogProtocolException(new(ContainerLogFailureKind.LoggingUnavailable, "This container has logging disabled."));
        token.ThrowIfCancellationRequested(); EnsureCurrent();
    }

    private async Task<ContainerLogFailure> HttpFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var (bytes, truncated) = await ReadPrefixAsync(stream, options.MaximumLogErrorBytes, token).ConfigureAwait(false);
        string? original = bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
        if (!truncated)
        {
            try
            {
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("message", out var field)
                    && field.ValueKind == JsonValueKind.String) original = field.GetString();
            }
            catch (JsonException) { }
        }
        int status = (int)response.StatusCode;
        // Only the daemon's established capability diagnostic is classified as unavailable; no driver-name whitelist.
        var kind = status switch
        {
            404 => ContainerLogFailureKind.NotFound,
            401 or 403 => ContainerLogFailureKind.AccessDenied,
            _ when LoggingUnavailable(original)
                => ContainerLogFailureKind.LoggingUnavailable,
            >= 400 and < 500 => ContainerLogFailureKind.InvalidRequest,
            _ => ContainerLogFailureKind.DaemonError
        };
        return new(kind, $"The Engine returned HTTP {status}.", status, original, truncated);
    }

    internal static bool LoggingUnavailable(string? message)
        => message?.Contains("configured logging driver does not support reading", StringComparison.OrdinalIgnoreCase) == true;

    internal static async Task<(byte[] Bytes, bool Truncated)> ReadPrefixAsync(Stream stream, int maximum, CancellationToken token)
    {
        var bytes = new byte[maximum]; int length = 0;
        while (length < maximum)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) return (bytes[..length], false);
            length += count;
        }
        var extra = new byte[1];
        return (bytes, await stream.ReadAsync(extra, token).ConfigureAwait(false) != 0);
    }
    private static ContainerLogFailure FromConnectionFailure(EngineConnectionFailure failure)
        => new(failure.Kind switch
        {
            EngineConnectionFailureKind.AccessDenied => ContainerLogFailureKind.AccessDenied,
            EngineConnectionFailureKind.DaemonHttpError when failure.HttpStatusCode is 401 or 403 => ContainerLogFailureKind.AccessDenied,
            EngineConnectionFailureKind.DaemonHttpError => ContainerLogFailureKind.DaemonError,
            EngineConnectionFailureKind.MalformedResponse => ContainerLogFailureKind.MalformedOutput,
            _ => ContainerLogFailureKind.TransportLoss
        }, failure.Description, failure.HttpStatusCode, failure.DaemonMessage);

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            Volatile.Write(ref disposed, 1); stop.Cancel();
            return new(disposal = Task.Run(DisposeCoreAsync));
        }
    }
    private async Task DisposeCoreAsync()
    {
        await producer.ConfigureAwait(false);
        while (channel.Reader.TryRead(out _)) { }
        released(this);
        lock (gate) stop.Dispose();
    }
    private sealed class CurrentReader(DockerLogSubscription subscription, ChannelReader<ContainerLogOutput> inner) : ChannelReader<ContainerLogOutput>
    {
        public override Task Completion => inner.Completion;
        public override bool TryRead(out ContainerLogOutput item)
        {
            if (subscription.Current() && inner.TryRead(out item!) && subscription.Current()) return true;
            item = null!; return false;
        }
        public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (!subscription.Current()) return false;
            var available = await inner.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            return subscription.Current() && available;
        }
    }
}
internal sealed class LogProtocolException(ContainerLogFailure failure) : Exception(failure.Description)
{
    internal ContainerLogFailure Failure { get; } = failure;
}
