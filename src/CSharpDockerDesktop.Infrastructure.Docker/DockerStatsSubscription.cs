using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>Independent freshly negotiated Stats transport, with bounded opening and delivery.</summary>
internal sealed class DockerStatsSubscription : IContainerStatsSubscription
{
    private readonly DockerEngineConnection owner;
    private readonly DockerEngineConnectionClient client;
    private readonly DockerConnectionOptions options;
    private readonly string containerId;
    private readonly CancellationTokenSource stop;
    private readonly Action<DockerStatsSubscription> released;
    private readonly Channel<ContainerStatsSample> channel;
    private readonly TaskCompletionSource<ContainerStatsSubscriptionAttempt> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private Task<ContainerStatsCompletion> producer = null!;
    private Task? disposal;
    private DockerEngineConnection? streamConnection;
    private int disposed, invalidated;

    internal DockerStatsSubscription(DockerEngineConnection owner, DockerEngineConnectionClient client,
        DockerConnectionOptions options, string containerId, CancellationToken lifetime, CancellationToken caller,
        Action<DockerStatsSubscription> released)
    {
        (this.owner, this.client, this.options, this.containerId, this.released) = (owner, client, options, containerId, released);
        ContainerId = containerId;
        stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
        channel = Channel.CreateBounded<ContainerStatsSample>(new BoundedChannelOptions(options.StatsBufferCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false });
        Samples = new CurrentReader(this, channel.Reader);
        if (owner.Continuity is { } continuity) continuity.Reopened += Invalidate;
    }

    public string ContainerId { get; }
    public ChannelReader<ContainerStatsSample> Samples { get; }
    public Task<ContainerStatsCompletion> Completion => producer;
    internal Task<ContainerStatsSubscriptionAttempt> Opened => opened.Task;
    internal void Start() => producer = Task.Run(ProduceAsync);
    private bool Current()
    {
        if (Volatile.Read(ref disposed) != 0 || stop.IsCancellationRequested) return false;
        if (streamConnection is { HasTransportContinuity: false } || !owner.HasContinuity)
        { Invalidate(); return false; }
        return Volatile.Read(ref disposed) == 0 && !stop.IsCancellationRequested;
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
        throw new StatsProtocolException(new(ContainerStatsFailureKind.TransportLoss, "The selected Engine transport changed."));
    }

    private async Task<ContainerStatsCompletion> ProduceAsync()
    {
        ContainerStatsFailure? failure = null;
        HttpResponseMessage? response = null;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            opening.CancelAfter(options.StatsOpeningTimeout);
            try
            {
                EnsureCurrent();
                var attempt = await client.ConnectAsync(owner.Metadata.Endpoint, opening.Token).ConfigureAwait(false);
                if (attempt.Connection is not DockerEngineConnection connection)
                    throw new StatsProtocolException(FromConnectionFailure(attempt.Failure!));
                streamConnection = connection;
                if (connection.Continuity is { } continuity) continuity.Reopened += Invalidate;
                var selected = owner.Metadata;
                var observed = connection.Metadata;
                if (string.IsNullOrWhiteSpace(selected.EngineId) || selected.EngineId != observed.EngineId
                    || selected.Endpoint != observed.Endpoint || selected.EngineVersion != observed.EngineVersion
                    || selected.OSType != observed.OSType || selected.RuntimeOS != observed.RuntimeOS)
                    throw new StatsProtocolException(new(ContainerStatsFailureKind.TransportLoss,
                        "Stats transport metadata differs from the selected connection; negotiate a new connection."));
                if (selected.Api != observed.Api)
                    throw new StatsProtocolException(new(ContainerStatsFailureKind.IncompatibleApi,
                        "Stats transport API compatibility differs from the selected connection."));
                EnsureCurrent();
                await InspectAsync(connection, opening.Token).ConfigureAwait(false);
                var path = $"v{selected.Api.Negotiated}/containers/{Uri.EscapeDataString(ContainerId)}/stats?stream=true";
                using var message = new HttpRequestMessage(HttpMethod.Get, path);
                response = await connection.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, opening.Token).ConfigureAwait(false);
                EnsureCurrent();
                if (!response.IsSuccessStatusCode)
                    throw new StatsProtocolException(await HttpFailureAsync(response, opening.Token).ConfigureAwait(false));
                var media = response.Content.Headers.ContentType?.MediaType;
                if (media is not null && media != "application/json")
                    throw new StatsProtocolException(new(ContainerStatsFailureKind.MalformedSample, "The Engine returned an unsupported Stats stream format."));
                var stream = await response.Content.ReadAsStreamAsync(opening.Token).ConfigureAwait(false);
                opening.Token.ThrowIfCancellationRequested(); EnsureCurrent();
                opening.CancelAfter(Timeout.InfiniteTimeSpan);
                opened.TrySetResult(ContainerStatsSubscriptionAttempt.Subscribed(this));
                await DockerStatsDecoder.DecodeAsync(stream, ContainerId, owner.Metadata.OSType ?? owner.Metadata.RuntimeOS, options, async output =>
                {
                    EnsureCurrent();
                    await channel.Writer.WriteAsync(output, stop.Token).ConfigureAwait(false);
                    EnsureCurrent();
                }, stop.Token).ConfigureAwait(false);
                EnsureCurrent();
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested && opening.IsCancellationRequested)
            { failure = new(ContainerStatsFailureKind.OpeningDeadline, "Opening container Stats exceeded its deadline."); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (Volatile.Read(ref invalidated) != 0)
                failure = new(ContainerStatsFailureKind.TransportLoss, "The Engine transport changed; a fresh negotiation is required.");
        }
        catch (StatsProtocolException ex) { failure = ex.Failure; }
        catch (DecoderFallbackException) { failure = new(ContainerStatsFailureKind.MalformedSample, "The Engine returned malformed UTF-8 Stats JSON."); }
        catch (JsonException) { failure = new(ContainerStatsFailureKind.MalformedSample, "The Engine returned malformed Stats JSON or container configuration."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            if (!stop.IsCancellationRequested)
            {
                DockerEngineConnectionClient.TryDescribeFailure(ex, out var described);
                failure = new(described?.Kind == EngineConnectionFailureKind.AccessDenied ? ContainerStatsFailureKind.AccessDenied
                    : ContainerStatsFailureKind.TransportLoss, "The Engine Stats transport was interrupted or unavailable.");
            }
        }
        finally
        {
            if (owner.Continuity is { } ownerContinuity) ownerContinuity.Reopened -= Invalidate;
            if (streamConnection?.Continuity is { } streamContinuity) streamContinuity.Reopened -= Invalidate;
            response?.Dispose();
            if (streamConnection is { } connection) await connection.DisposeAsync().ConfigureAwait(false);
            if (failure?.Kind == ContainerStatsFailureKind.TransportLoss) Invalidate();
            // Replacement can race cleanup after event handlers have been detached.
            _ = Current();
            if (Volatile.Read(ref invalidated) != 0)
                failure = new(ContainerStatsFailureKind.TransportLoss, "The Engine transport changed; a fresh negotiation is required.");
            if (stop.IsCancellationRequested) while (channel.Reader.TryRead(out _)) { }
            if (stop.IsCancellationRequested && Volatile.Read(ref invalidated) == 0) failure = null;
            channel.Writer.TryComplete();
            opened.TrySetResult(ContainerStatsSubscriptionAttempt.Failed(failure ?? new(ContainerStatsFailureKind.TransportLoss,
                "The Stats opening ended before a subscription was available.")));
        }
        return failure is not null ? new(ContainerStatsTermination.Failed, failure)
            : new(stop.IsCancellationRequested ? ContainerStatsTermination.Canceled : ContainerStatsTermination.CleanEnd);
    }

    private async Task InspectAsync(DockerEngineConnection connection, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get,
            $"v{connection.Metadata.Api.Negotiated}/containers/{Uri.EscapeDataString(containerId)}/json");
        using var response = await connection.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        EnsureCurrent();
        if (!response.IsSuccessStatusCode) throw new StatsProtocolException(await HttpFailureAsync(response, token).ConfigureAwait(false));
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var (bytes, truncated) = await ReadPrefixAsync(stream, options.MaximumResponseBytes, token).ConfigureAwait(false);
        if (truncated) throw new StatsProtocolException(new(ContainerStatsFailureKind.MalformedSample, "Container configuration exceeds the configured size limit."));
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Id", out var id)
            || id.ValueKind != JsonValueKind.String)
            throw new StatsProtocolException(new(ContainerStatsFailureKind.MalformedSample, "Container inspection has no valid identity."));
        try
        {
            if (id.GetString() != ContainerId)
                throw new StatsProtocolException(new(ContainerStatsFailureKind.InvalidRequest,
                    "Stats requires the exact inspected container ID; names and prefixes are not accepted."));
        }
        catch (InvalidOperationException)
        { throw new StatsProtocolException(new(ContainerStatsFailureKind.MalformedSample, "Container inspection has an invalid Unicode identity.")); }
        token.ThrowIfCancellationRequested(); EnsureCurrent();
    }

    private async Task<ContainerStatsFailure> HttpFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var (bytes, truncated) = await ReadPrefixAsync(stream, options.MaximumStatsErrorBytes, token).ConfigureAwait(false);
        string? original = bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
        if (!truncated)
        {
            try
            {
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("message", out var field)
                    && field.ValueKind == JsonValueKind.String) original = field.GetString();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        int status = (int)response.StatusCode;
        var kind = status switch
        {
            404 => ContainerStatsFailureKind.NotFound,
            401 or 403 => ContainerStatsFailureKind.AccessDenied,
            >= 400 and < 500 => ContainerStatsFailureKind.InvalidRequest,
            _ => ContainerStatsFailureKind.DaemonError
        };
        return new(kind, $"The Engine returned HTTP {status}.", status, original, truncated);
    }

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
    private static ContainerStatsFailure FromConnectionFailure(EngineConnectionFailure failure)
        => new(failure.Kind switch
        {
            EngineConnectionFailureKind.IncompatibleApi => ContainerStatsFailureKind.IncompatibleApi,
            EngineConnectionFailureKind.UnsupportedEndpoint => ContainerStatsFailureKind.InvalidRequest,
            EngineConnectionFailureKind.AccessDenied => ContainerStatsFailureKind.AccessDenied,
            EngineConnectionFailureKind.DaemonHttpError when failure.HttpStatusCode is 401 or 403 => ContainerStatsFailureKind.AccessDenied,
            EngineConnectionFailureKind.DaemonHttpError => ContainerStatsFailureKind.DaemonError,
            EngineConnectionFailureKind.MalformedResponse => ContainerStatsFailureKind.MalformedSample,
            _ => ContainerStatsFailureKind.TransportLoss
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
    private sealed class CurrentReader(DockerStatsSubscription subscription, ChannelReader<ContainerStatsSample> inner) : ChannelReader<ContainerStatsSample>
    {
        public override Task Completion => inner.Completion;
        public override bool TryRead(out ContainerStatsSample item)
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
internal sealed class StatsProtocolException(ContainerStatsFailure failure) : Exception(failure.Description)
{
    internal ContainerStatsFailure Failure { get; } = failure;
}
