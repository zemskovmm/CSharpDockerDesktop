using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerVolumeCommands
{
    internal static async Task<VolumeCommandResult> ExecuteAsync(HttpClient http, EngineApiVersion api,
        string name, DockerConnectionOptions options,
        ITransportContinuity? continuity, long? generation, SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        var dispatched = false;
        int? status = null;
        string? daemonMessage = null;
        string? daemonErrorBody = null;
        bool bodyUnavailable = false;
        VolumeCommandResult Result(VolumeCommandDisposition disposition, VolumeCommandFailureKind kind, string description)
            => new(disposition, status, daemonMessage, daemonErrorBody, Failure: new(kind, description), ErrorBodyUnavailable: bodyUnavailable);
        void CheckCancellation()
        {
            if (caller.IsCancellationRequested)
                throw new VolumeCommandCanceledException(Result(dispatched ? VolumeCommandDisposition.Uncertain : VolumeCommandDisposition.NotDispatched,
                    VolumeCommandFailureKind.Canceled, "The volume command was canceled."), caller);
        }
        bool Stale() => continuity?.ConnectionGeneration != generation;
        CheckCancellation();
        if (!DockerVolumeName.IsValid(name))
            return Result(VolumeCommandDisposition.NotDispatched, VolumeCommandFailureKind.InvalidRequest,
                "Supply a valid bounded complete volume name.");
        if (lifetime.IsCancellationRequested)
            return Result(VolumeCommandDisposition.NotDispatched, VolumeCommandFailureKind.ConnectionDisposed, "The Engine connection was disposed.");
        if (Stale())
            return Result(VolumeCommandDisposition.NotDispatched, VolumeCommandFailureKind.StaleConnection, "Negotiate a new Engine connection before issuing commands.");
        if (!await slots.WaitAsync(0).ConfigureAwait(false))
            return Result(VolumeCommandDisposition.NotDispatched, VolumeCommandFailureKind.CapacityExceeded, "The connection's command capacity is occupied.");
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
            // Preserve the validated name exactly as one component, with ordinary non-force semantics.
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"v{api}/volumes/{Uri.EscapeDataString(name)}?force=false")
            {
                // HTTP/1.1 only: no alternate protocol/auth/redirect retry path.
                Version = new Version(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            CheckCancellation();
            if (lifetime.IsCancellationRequested || Stale())
                return Result(VolumeCommandDisposition.NotDispatched,
                    Stale() ? VolumeCommandFailureKind.StaleConnection : VolumeCommandFailureKind.ConnectionDisposed,
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
                    (daemonErrorBody, daemonMessage) = await Task.Run(() =>
                        (Encoding.UTF8.GetString(body), ErrorMessage(body))).ConfigureAwait(false);
                }
            }
            CheckCancellation();
            deadline.Token.ThrowIfCancellationRequested();
            if (Stale())
                return Result(VolumeCommandDisposition.Uncertain, VolumeCommandFailureKind.StaleConnection, "The Engine transport changed after command dispatch.");
            if (status == 204)
                return new(VolumeCommandDisposition.Acknowledged, status);
            return Result(VolumeCommandDisposition.DaemonResponse, status switch
            {
                404 => VolumeCommandFailureKind.NotFound,
                409 => VolumeCommandFailureKind.Conflict,
                401 or 403 => VolumeCommandFailureKind.AccessDenied,
                >= 400 and < 500 => VolumeCommandFailureKind.InvalidRequest,
                >= 500 and < 600 => VolumeCommandFailureKind.DaemonFailure,
                _ => VolumeCommandFailureKind.UnexpectedResponse
            }, $"The Engine returned HTTP {status} for the volume command.");
        }
        catch (VolumeCommandCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or ConnectionProtocolException)
        {
            if (ex is HttpRequestException { StatusCode: { } availableStatus }) status ??= (int)availableStatus;
            CheckCancellation();
            var kind = lifetime.IsCancellationRequested ? VolumeCommandFailureKind.ConnectionDisposed
                : Stale() || changed.IsCancellationRequested ? VolumeCommandFailureKind.StaleConnection
                : deadline.IsCancellationRequested ? VolumeCommandFailureKind.Deadline
                : AccessDenied(ex) ? VolumeCommandFailureKind.AccessDenied : VolumeCommandFailureKind.TransportFailure;
            return Result(dispatched ? VolumeCommandDisposition.Uncertain : VolumeCommandDisposition.NotDispatched, kind,
                kind switch
                {
                    VolumeCommandFailureKind.ConnectionDisposed => "The connection was disposed after command dispatch.",
                    VolumeCommandFailureKind.StaleConnection => "The negotiated transport changed; reconcile through a new connection.",
                    VolumeCommandFailureKind.Deadline => "The volume command exceeded its deadline; reconcile current state.",
                    VolumeCommandFailureKind.AccessDenied => "Access to the Engine was denied.",
                    _ => "The command response was interrupted or unavailable; reconcile current state."
                });
        }
        finally
        {
            if (continuity is not null) continuity.Reopened -= Reopened;
            slots.Release();
        }
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
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > maximum) return null;
            buffer.Write(chunk, 0, count);
        }
    }
}
