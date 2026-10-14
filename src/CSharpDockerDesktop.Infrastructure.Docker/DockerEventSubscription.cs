using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>One dedicated, freshly negotiated HTTP/pipe pool, independent of the owner's ordinary pool.</summary>
internal sealed class DockerEventSubscription : IEngineEventSubscription
{
    private readonly DockerEngineConnection owner;
    private readonly DockerEngineConnectionClient client;
    private readonly DockerConnectionOptions options;
    private readonly string? cursor;
    private readonly CancellationTokenSource stop;
    private readonly Action<DockerEventSubscription> released;
    private readonly Channel<EngineEvent> channel;
    private readonly TaskCompletionSource<EngineEventSubscriptionAttempt> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private Task<EngineEventFailure?> producer = null!;
    private Task? disposal;
    private DockerEngineConnection? streamConnection;
    private int invalidated;
    private int streamEnded;
    private int disposed;

    internal DockerEventSubscription(DockerEngineConnection owner, DockerEngineConnectionClient client,
        DockerConnectionOptions options, string? cursor, CancellationToken lifetime, CancellationToken caller,
        Action<DockerEventSubscription> released)
    {
        (this.owner, this.client, this.options, this.cursor, this.released) = (owner, client, options, cursor, released);
        stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
        channel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(options.EventBufferCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false });
        Events = new CurrentReader(this, channel.Reader);
        if (owner.Continuity is { } continuity) continuity.Reopened += Invalidate;
    }

    internal Task<EngineEventSubscriptionAttempt> Opened => opened.Task;
    public ChannelReader<EngineEvent> Events { get; }
    public Task<EngineEventFailure?> Completion => producer;
    internal void Start() => producer = Task.Run(ProduceAsync);

    private bool Current()
    {
        if (Volatile.Read(ref disposed) != 0 || stop.IsCancellationRequested) return false;
        if (owner.HasContinuity && (Volatile.Read(ref streamEnded) != 0 || streamConnection is null || streamConnection.HasContinuity)) return true;
        Invalidate();
        return false;
    }
    private void Invalidate()
    {
        lock (gate)
        {
            if (disposed != 0) return;
            Volatile.Write(ref invalidated, 1);
            stop.Cancel();
        }
    }
    private void EnsureCurrent()
    {
        if (!Current())
        {
            stop.Token.ThrowIfCancellationRequested();
            throw new EventProtocolException(new(EngineEventFailureKind.TransportLoss, "The Engine transport changed; a fresh negotiation is required."));
        }
    }

    private async Task<EngineEventFailure?> ProduceAsync()
    {
        EngineEventFailure? failure = null;
        HttpResponseMessage? response = null;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            opening.CancelAfter(options.EventOpeningTimeout);
            try
            {
                EnsureCurrent();
                // Each pool retains one HTTP connection. A second pool must independently negotiate,
                // rather than treating a legitimate concurrent pipe as a replacement of the first.
                var attempt = await client.ConnectAsync(owner.Metadata.Endpoint, opening.Token).ConfigureAwait(false);
                if (attempt.Connection is not DockerEngineConnection connection)
                    throw new EventProtocolException(FromConnectionFailure(attempt.Failure!));
                streamConnection = connection;
                if (connection.Continuity is { } continuity) continuity.Reopened += Invalidate;
                var selected = owner.Metadata;
                var observed = connection.Metadata;
                if (selected.EngineId != observed.EngineId || selected.EngineVersion != observed.EngineVersion
                    || selected.Api != observed.Api || selected.OSType != observed.OSType)
                    throw new EventProtocolException(new(EngineEventFailureKind.TransportLoss,
                        "Event transport metadata differs from the selected connection; negotiate a new connection."));
                EnsureCurrent();
                const string filters = "{\"type\":[\"container\",\"image\",\"volume\",\"network\"]}";
                var path = $"v{selected.Api.Negotiated}/events?filters={Uri.EscapeDataString(filters)}"
                    + (cursor is null ? "" : "&since=" + Uri.EscapeDataString(cursor));
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Accept.ParseAdd("application/json"); // Stable newline-delimited JSON, including API 1.52+.
                response = await connection.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, opening.Token).ConfigureAwait(false);
                EnsureCurrent();
                if (!response.IsSuccessStatusCode)
                    throw new EventProtocolException(await HttpFailureAsync(response, opening.Token).ConfigureAwait(false));
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && mediaType is not ("application/json" or "application/x-ndjson" or "application/jsonl"))
                    throw new EventProtocolException(new(EngineEventFailureKind.MalformedEvent, "The Engine returned an unsupported event stream format."));
                var stream = await response.Content.ReadAsStreamAsync(opening.Token).ConfigureAwait(false);
                opening.Token.ThrowIfCancellationRequested();
                EnsureCurrent();
                opening.CancelAfter(Timeout.InfiniteTimeSpan);
                opened.TrySetResult(EngineEventSubscriptionAttempt.Subscribed(this));
                // Only lifetime cancellation applies after opening; no ordinary request deadline.
                await DecodeAsync(stream, stop.Token).ConfigureAwait(false);
                EnsureCurrent();
                failure = new(EngineEventFailureKind.UnexpectedEnd, "The Engine event stream ended unexpectedly.");
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested && opening.IsCancellationRequested)
            { failure = new(EngineEventFailureKind.OpeningDeadline, "Opening the Engine event stream exceeded its deadline."); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (Volatile.Read(ref invalidated) != 0)
                failure = new(EngineEventFailureKind.TransportLoss, "The Engine transport changed; a fresh negotiation is required.");
        }
        catch (EventProtocolException ex) { failure = ex.Failure; }
        catch (JsonException) { failure = new(EngineEventFailureKind.MalformedEvent, "The Engine returned malformed event JSON."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            if (!stop.IsCancellationRequested)
            {
                DockerEngineConnectionClient.TryDescribeFailure(ex, out var described);
                failure = new(described?.Kind == EngineConnectionFailureKind.AccessDenied ? EngineEventFailureKind.AccessDenied
                    : EngineEventFailureKind.TransportLoss, "The Engine event transport was interrupted or unavailable.");
            }
        }
        finally
        {
            if (owner.Continuity is { } ownerContinuity) ownerContinuity.Reopened -= Invalidate;
            if (streamConnection?.Continuity is { } streamContinuity) streamContinuity.Reopened -= Invalidate;
            Volatile.Write(ref streamEnded, 1);
            response?.Dispose();
            if (streamConnection is { } connection) await connection.DisposeAsync().ConfigureAwait(false);
            // Canceled/replaced observations must not survive their selected connection lifetime.
            if (stop.IsCancellationRequested || failure?.Kind == EngineEventFailureKind.TransportLoss)
                while (channel.Reader.TryRead(out _)) { }
            if (stop.IsCancellationRequested && Volatile.Read(ref invalidated) == 0) failure = null;
            channel.Writer.TryComplete();
            opened.TrySetResult(EngineEventSubscriptionAttempt.Failed(failure ?? new(EngineEventFailureKind.TransportLoss,
                "The event subscription was canceled before it opened.")));
        }
        return failure;
    }

    private async Task DecodeAsync(Stream stream, CancellationToken token)
    {
        var chunk = new byte[4096];
        var record = new byte[options.MaximumEventBytes];
        int length = 0, count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            EnsureCurrent();
            for (int i = 0; i < count; i++)
            {
                if (chunk[i] == (byte)'\n')
                {
                    await DeliverAsync(record.AsMemory(0, length), token).ConfigureAwait(false);
                    length = 0;
                }
                else
                {
                    if (length == record.Length)
                        throw new EventProtocolException(new(EngineEventFailureKind.OversizedEvent, "An Engine event exceeds the configured size limit."));
                    record[length++] = chunk[i];
                }
            }
        }
        if (length != 0) await DeliverAsync(record.AsMemory(0, length), token).ConfigureAwait(false);
    }

    private async Task DeliverAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (bytes.Span.IndexOfAnyExcept((byte)' ', (byte)'\r', (byte)'\t') < 0) return;
        EnsureCurrent();
        var observation = DockerEventJson.Decode(bytes);
        await channel.Writer.WriteAsync(observation, token).ConfigureAwait(false); // Backpressure; never drop.
        EnsureCurrent();
    }

    private async Task<EngineEventFailure> HttpFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        int status = (int)response.StatusCode;
        string? message = null;
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        bool oversized = false;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > options.MaximumResponseBytes) { oversized = true; break; }
            body.Write(chunk, 0, count);
        }
        if (!oversized)
        {
            var bytes = body.ToArray();
            try
            {
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("message", out var field)
                    && field.ValueKind == JsonValueKind.String) message = field.GetString();
            }
            catch (JsonException) { message = bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes); }
        }
        return new(status is 401 or 403 ? EngineEventFailureKind.AccessDenied
            : status is >= 400 and < 500 ? EngineEventFailureKind.InvalidRequest : EngineEventFailureKind.DaemonHttpError,
            $"The Engine returned HTTP {status}." + (oversized ? " Its error body exceeds the configured size limit." : ""), status, message);
    }

    private static EngineEventFailure FromConnectionFailure(EngineConnectionFailure failure)
        => new(failure.Kind switch
        {
            EngineConnectionFailureKind.AccessDenied => EngineEventFailureKind.AccessDenied,
            EngineConnectionFailureKind.DaemonHttpError when failure.HttpStatusCode is 401 or 403 => EngineEventFailureKind.AccessDenied,
            EngineConnectionFailureKind.DaemonHttpError => EngineEventFailureKind.DaemonHttpError,
            EngineConnectionFailureKind.MalformedResponse => EngineEventFailureKind.MalformedEvent,
            _ => EngineEventFailureKind.TransportLoss
        }, failure.Description, failure.HttpStatusCode, failure.DaemonMessage);

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= Task.Run(DisposeCoreAsync));
    }
    private async Task DisposeCoreAsync()
    {
        lock (gate)
        {
            Volatile.Write(ref disposed, 1);
            stop.Cancel();
        }
        await producer.ConfigureAwait(false);
        while (channel.Reader.TryRead(out _)) { }
        released(this);
        lock (gate) stop.Dispose();
    }

    private sealed class CurrentReader(DockerEventSubscription subscription, ChannelReader<EngineEvent> inner) : ChannelReader<EngineEvent>
    {
        public override Task Completion => inner.Completion;
        public override bool TryRead(out EngineEvent item)
        {
            if (subscription.Current() && inner.TryRead(out item!)) return true;
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

internal static class DockerEventJson
{
    internal static EngineEvent Decode(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Malformed();
        var type = Text(root, "Type");
        var action = Text(root, "Action");
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(action)) throw Malformed();
        string? id = null;
        IReadOnlyDictionary<string, string>? attributes = null;
        if (root.TryGetProperty("Actor", out var actor) && actor.ValueKind != JsonValueKind.Null)
        {
            if (actor.ValueKind != JsonValueKind.Object) throw Malformed();
            id = Text(actor, "ID");
            if (actor.TryGetProperty("Attributes", out var fields) && fields.ValueKind != JsonValueKind.Null)
            {
                if (fields.ValueKind != JsonValueKind.Object) throw Malformed();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in fields.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.String) throw Malformed();
                    values[field.Name] = field.Value.GetString()!;
                }
                attributes = new ReadOnlyDictionary<string, string>(values);
            }
        }
        return new(type, action, id, attributes, Text(root, "scope"), new(Number(root, "time"), Number(root, "timeNano")));
    }
    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        return field.ValueKind == JsonValueKind.String ? field.GetString() : throw Malformed();
    }
    private static long? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var value) ? value : throw Malformed();
    }
    private static EventProtocolException Malformed() => new(new(EngineEventFailureKind.MalformedEvent, "The Engine event has missing or invalid fields."));
}

internal sealed class EventProtocolException(EngineEventFailure failure) : Exception(failure.Description)
{
    internal EngineEventFailure Failure { get; } = failure;
}
