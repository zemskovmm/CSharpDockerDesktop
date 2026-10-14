using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerImageCommands
{
    internal static async Task<ImageCommandResult> ExecuteAsync(HttpClient http, EngineApiVersion api,
        string reference, DockerConnectionOptions options, ITransportContinuity? continuity, long? generation,
        SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        var dispatched = false;
        int? status = null;
        string? daemonMessage = null;
        var bodyUnavailable = false;
        var records = new List<ImageRemovalRecord>();
        ImageCommandResult Result(ImageCommandDisposition disposition, ImageCommandFailureKind? kind = null, string? description = null)
            => new(disposition, status, daemonMessage, Array.AsReadOnly(records.ToArray()),
                kind is { } value ? new(value, description!) : null, bodyUnavailable);
        void CheckCancellation()
        {
            if (caller.IsCancellationRequested)
                throw new ImageCommandCanceledException(Result(dispatched ? ImageCommandDisposition.Uncertain : ImageCommandDisposition.NotDispatched,
                    ImageCommandFailureKind.Canceled, "The image command was canceled."), caller);
        }
        bool Stale() => continuity?.ConnectionGeneration != generation;
        CheckCancellation();
        if (!DockerImageReference.IsValid(reference))
            return Result(ImageCommandDisposition.NotDispatched, ImageCommandFailureKind.InvalidRequest,
                "Supply a valid bounded image ID or repository reference.");
        if (lifetime.IsCancellationRequested)
            return Result(ImageCommandDisposition.NotDispatched, ImageCommandFailureKind.ConnectionDisposed, "The Engine connection was disposed.");
        if (Stale())
            return Result(ImageCommandDisposition.NotDispatched, ImageCommandFailureKind.StaleConnection, "Negotiate a new Engine connection before issuing commands.");
        if (!await slots.WaitAsync(0).ConfigureAwait(false))
            return Result(ImageCommandDisposition.NotDispatched, ImageCommandFailureKind.CapacityExceeded, "The connection's command capacity is occupied.");
        using var changed = new CancellationTokenSource();
        void Reopened() { try { changed.Cancel(); } catch (ObjectDisposedException) { } }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime, changed.Token);
        deadline.CancelAfter(options.CommandTimeout);
        if (continuity is not null) continuity.Reopened += Reopened;
        try
        {
            // Explicit ordinary non-force removal; noprune=false preserves the documented daemon default.
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"v{api}/images/{Uri.EscapeDataString(reference)}?force=false&noprune=false")
            { Version = new Version(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            CheckCancellation();
            if (lifetime.IsCancellationRequested || Stale())
                return Result(ImageCommandDisposition.NotDispatched,
                    Stale() ? ImageCommandFailureKind.StaleConnection : ImageCommandFailureKind.ConnectionDisposed,
                    "The negotiated connection ended before dispatch.");
            // SendAsync is the conservative possible-dispatch boundary, including internal HTTP queuing.
            dispatched = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            bodyUnavailable = true;
            var body = await ReadBodyAsync(response.Content,
                status == 200 ? options.MaximumImageCommandResponseBytes : options.MaximumCommandErrorBytes, deadline.Token).ConfigureAwait(false);
            bool valid = false;
            if (body is not null)
            {
                // Parsing stays off the caller/UI thread. Bounds limit even an uninterruptible JSON token scan.
                valid = await Task.Run(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (status != 200) { daemonMessage = ErrorMessage(body); return true; }
                    return DecodeRecords(body, records, deadline.Token);
                }, deadline.Token).ConfigureAwait(false);
                bodyUnavailable = !valid;
            }
            CheckCancellation();
            deadline.Token.ThrowIfCancellationRequested();
            if (Stale())
                return Result(ImageCommandDisposition.Uncertain, ImageCommandFailureKind.StaleConnection, "The Engine transport changed after command dispatch.");
            if (status == 200 && valid) return Result(ImageCommandDisposition.Acknowledged);
            return Result(ImageCommandDisposition.DaemonResponse, status switch
            {
                404 => ImageCommandFailureKind.NotFound,
                409 => ImageCommandFailureKind.Conflict,
                401 or 403 => ImageCommandFailureKind.AccessDenied,
                >= 400 and < 500 => ImageCommandFailureKind.InvalidRequest,
                >= 500 => ImageCommandFailureKind.DaemonFailure,
                _ => ImageCommandFailureKind.UnexpectedResponse
            }, status == 200 ? "The Engine removal response was malformed, empty or exceeded its budget."
                : $"The Engine returned HTTP {status} for the image command.");
        }
        catch (ImageCommandCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or ConnectionProtocolException)
        {
            CheckCancellation();
            var kind = lifetime.IsCancellationRequested ? ImageCommandFailureKind.ConnectionDisposed
                : Stale() || changed.IsCancellationRequested ? ImageCommandFailureKind.StaleConnection
                : deadline.IsCancellationRequested ? ImageCommandFailureKind.Deadline
                : AccessDenied(ex) ? ImageCommandFailureKind.AccessDenied : ImageCommandFailureKind.TransportFailure;
            return Result(dispatched ? ImageCommandDisposition.Uncertain : ImageCommandDisposition.NotDispatched, kind, kind switch
            {
                ImageCommandFailureKind.ConnectionDisposed => "The connection was disposed during the image command.",
                ImageCommandFailureKind.StaleConnection => "The negotiated transport changed; reconcile through a new connection.",
                ImageCommandFailureKind.Deadline => "The image command exceeded its deadline; reconcile current state.",
                ImageCommandFailureKind.AccessDenied => "Access to the Engine was denied.",
                _ => "The image command response was interrupted or unavailable; reconcile current state."
            });
        }
        finally
        {
            if (continuity is not null) continuity.Reopened -= Reopened;
            slots.Release();
        }
    }

    private static bool DecodeRecords(byte[] body, List<ImageRemovalRecord> records, CancellationToken token)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.Object) return false;
                string? untagged = null, deleted = null;
                foreach (var property in item.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (property.Name is not ("Untagged" or "Deleted")) continue;
                    if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.Value.GetString())) return false;
                    if (property.Name == "Untagged") { if (untagged is not null) return false; untagged = property.Value.GetString(); }
                    else { if (deleted is not null) return false; deleted = property.Value.GetString(); }
                }
                if (untagged is null && deleted is null) return false;
                records.Add(new(untagged, deleted));
            }
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool AccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is UnauthorizedAccessException || current is IOException && (current.HResult & 0xffff) == 5) return true;
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
    private static async Task<byte[]?> ReadBodyAsync(HttpContent content, int maximum, CancellationToken token)
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
