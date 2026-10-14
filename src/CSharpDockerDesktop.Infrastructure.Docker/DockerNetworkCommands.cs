using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerNetworkCommands
{
    internal static async Task<NetworkCommandResult> ExecuteAsync(HttpClient http, EngineApiVersion api,
        string id, DockerConnectionOptions options,
        ITransportContinuity? continuity, long? generation, SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        var dispatched = false;
        int? status = null;
        string? daemonMessage = null;
        string? daemonErrorBody = null;
        bool bodyUnavailable = false;
        NetworkCommandResult Result(NetworkCommandDisposition disposition, NetworkCommandFailureKind kind, string description)
            => new(disposition, status, daemonMessage, daemonErrorBody, Failure: new(kind, description), ErrorBodyUnavailable: bodyUnavailable);
        void CheckCancellation()
        {
            if (caller.IsCancellationRequested)
                throw new NetworkCommandCanceledException(Result(dispatched ? NetworkCommandDisposition.Uncertain : NetworkCommandDisposition.NotDispatched,
                    NetworkCommandFailureKind.Canceled, "The network command was canceled."), caller);
        }
        bool Stale() => continuity?.ConnectionGeneration != generation;
        CheckCancellation();
        if (!DockerNetworkId.IsValid(id))
            return Result(NetworkCommandDisposition.NotDispatched, NetworkCommandFailureKind.InvalidRequest,
                "Supply a valid bounded complete network ID.");
        if (lifetime.IsCancellationRequested)
            return Result(NetworkCommandDisposition.NotDispatched, NetworkCommandFailureKind.ConnectionDisposed, "The Engine connection was disposed.");
        if (Stale())
            return Result(NetworkCommandDisposition.NotDispatched, NetworkCommandFailureKind.StaleConnection, "Negotiate a new Engine connection before issuing commands.");
        if (!await slots.WaitAsync(0).ConfigureAwait(false))
            return Result(NetworkCommandDisposition.NotDispatched, NetworkCommandFailureKind.CapacityExceeded, "The connection's command capacity is occupied.");
        using var changed = new CancellationTokenSource();
        void Reopened()
        {
            // An invocation captured before unsubscription may race the local source's disposal.
            try { changed.Cancel(); } catch (ObjectDisposedException) { }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime, changed.Token);
        deadline.CancelAfter(options.CommandTimeout);
        if (continuity is not null) continuity.Reopened += Reopened;
        try
        {
            // Preserve the validated ID exactly as one component, with ordinary non-force semantics.
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"v{api}/networks/{Uri.EscapeDataString(id)}")
            {
                // HTTP/1.1 only: no alternate protocol/auth/redirect retry path.
                Version = new Version(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            CheckCancellation();
            if (lifetime.IsCancellationRequested || Stale())
                return Result(NetworkCommandDisposition.NotDispatched,
                    Stale() ? NetworkCommandFailureKind.StaleConnection : NetworkCommandFailureKind.ConnectionDisposed,
                    "The negotiated connection ended before dispatch.");
            deadline.Token.ThrowIfCancellationRequested();
            // Conservative boundary: SendAsync may queue before writing. From this point failure cannot prove non-application.
            dispatched = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (status != 204)
            {
                bodyUnavailable = true;
                var body = await ReadErrorAsync(response.Content, options.MaximumCommandErrorBytes, deadline.Token).ConfigureAwait(false);
                bodyUnavailable = body is null;
                if (body is not null)
                {
                    // Retain the complete bounded original body even if cancellation arrives at EOF.
                    // Decode off the caller thread, then fence publication below.
                    (daemonErrorBody, daemonMessage, bodyUnavailable) = await Task.Run(() =>
                    {
                        try { return ((string?)new UTF8Encoding(false, true).GetString(body), ErrorMessage(body), false); }
                        catch (DecoderFallbackException) { return ((string?)null, (string?)null, true); }
                    }).ConfigureAwait(false);
                }
            }
            CheckCancellation();
            deadline.Token.ThrowIfCancellationRequested();
            if (Stale())
                return Result(NetworkCommandDisposition.Uncertain, NetworkCommandFailureKind.StaleConnection, "The Engine transport changed after command dispatch.");
            if (status == 204)
                return new(NetworkCommandDisposition.Acknowledged, status);
            return Result(NetworkCommandDisposition.DaemonResponse, status switch
            {
                404 => NetworkCommandFailureKind.NotFound,
                409 => NetworkCommandFailureKind.Conflict,
                403 when IsDependencyConflict(daemonMessage, id) => NetworkCommandFailureKind.Conflict,
                401 or 403 => NetworkCommandFailureKind.AccessDenied,
                >= 400 and < 500 => NetworkCommandFailureKind.InvalidRequest,
                >= 500 and < 600 => NetworkCommandFailureKind.DaemonFailure,
                _ => NetworkCommandFailureKind.UnexpectedResponse
            }, $"The Engine returned HTTP {status} for the network command.");
        }
        catch (NetworkCommandCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or ConnectionProtocolException)
        {
            if (ex is HttpRequestException { StatusCode: { } availableStatus }) status ??= (int)availableStatus;
            CheckCancellation();
            var kind = lifetime.IsCancellationRequested ? NetworkCommandFailureKind.ConnectionDisposed
                : Stale() || changed.IsCancellationRequested ? NetworkCommandFailureKind.StaleConnection
                : deadline.IsCancellationRequested ? NetworkCommandFailureKind.Deadline
                : AccessDenied(ex) ? NetworkCommandFailureKind.AccessDenied : NetworkCommandFailureKind.TransportFailure;
            return Result(dispatched ? NetworkCommandDisposition.Uncertain : NetworkCommandDisposition.NotDispatched, kind,
                kind switch
                {
                    NetworkCommandFailureKind.ConnectionDisposed => "The connection was disposed after command dispatch.",
                    NetworkCommandFailureKind.StaleConnection => "The negotiated transport changed; reconcile through a new connection.",
                    NetworkCommandFailureKind.Deadline => "The network command exceeded its deadline; reconcile current state.",
                    NetworkCommandFailureKind.AccessDenied => "Access to the Engine was denied.",
                    _ => "The command response was interrupted or unavailable; reconcile current state."
                });
        }
        finally
        {
            if (continuity is not null) continuity.Reopened -= Reopened;
            slots.Release();
        }
    }

    // Docker exposes only message text, not a stable daemon error code. Match the established
    // dependency error forms; retain unrecognized 403 responses as denial with their original body.
    private static bool IsDependencyConflict(string? message, string id)
    {
        const string wrapper = "error while removing network: ";
        if (message?.StartsWith(wrapper, StringComparison.Ordinal) == true) message = message[wrapper.Length..];
        return message is not null && (message.StartsWith("network ", StringComparison.Ordinal)
            && message.EndsWith($" id {id} has active endpoints", StringComparison.Ordinal)
            || message.StartsWith("configuration network \"", StringComparison.Ordinal)
            && message.EndsWith("\" is in use", StringComparison.Ordinal));
    }

    private static bool AccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is UnauthorizedAccessException || current is IOException && (current.HResult & 0xffff) == 5
                || current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }) return true;
        return false;
    }
    private static string? ErrorMessage(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("message", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return body.Length == 0 ? null : Encoding.UTF8.GetString(body); }
    }
    private static async Task<byte[]?> ReadErrorAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) return null;
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, maximum + 1)];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximum + 1L - buffer.Length)), token).ConfigureAwait(false);
            if (count == 0)
            {
                if (content.Headers.ContentLength is { } expected && buffer.Length != expected)
                    throw new IOException("The network command error body ended before its declared length.");
                return buffer.ToArray();
            }
            if (buffer.Length + count > maximum) return null;
            buffer.Write(chunk, 0, count);
        }
    }
}
