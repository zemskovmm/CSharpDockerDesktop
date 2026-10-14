using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerResourceReader
{
    internal static async Task<EngineResourceResult<T>> ReadAsync<T>(HttpClient http, EngineApiVersion api,
        string path, Func<byte[], T> decode, DockerConnectionOptions options, ITransportContinuity? continuity,
        long? generation, CancellationToken lifetime, CancellationToken caller, string resource = "container") where T : class
    {
        caller.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
        deadline.CancelAfter(options.RequestTimeout);
        int? status = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            EnsureContinuity(continuity, generation);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"v{api}/{path}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            EnsureContinuity(continuity, generation);
            byte[] body;
            try { body = await ReadBoundedAsync(response.Content, options.MaximumResponseBytes, deadline.Token, resource).ConfigureAwait(false); }
            catch (ResourceProtocolException) when (!response.IsSuccessStatusCode)
            {
                EnsureContinuity(continuity, generation);
                deadline.Token.ThrowIfCancellationRequested();
                return EngineResourceResult<T>.Failed(HttpFailure(status.Value, null, "The error body exceeds the configured size limit."));
            }
            EnsureContinuity(continuity, generation);
            deadline.Token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
                return EngineResourceResult<T>.Failed(HttpFailure(status.Value, ErrorMessage(body)));
            // Parsing stays off the caller's UI thread even when transport completes synchronously.
            var result = await Task.Run(() => decode(body), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            EnsureContinuity(continuity, generation);
            return EngineResourceResult<T>.Success(result);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ResourceProtocolException or JsonException or HttpRequestException
            or IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
        {
            caller.ThrowIfCancellationRequested();
            var failure = ex switch
            {
                ResourceProtocolException protocol => protocol.Failure with { HttpStatusCode = status },
                JsonException => new(EngineResourceFailureKind.MalformedData, $"The Engine returned malformed {resource} JSON.", status),
                _ when AccessDenied(ex) => new(EngineResourceFailureKind.AccessDenied, "Access to the Engine was denied.", status),
                _ => new EngineResourceFailure(EngineResourceFailureKind.TransportLoss,
                    lifetime.IsCancellationRequested ? "The Engine connection was disposed."
                    : deadline.IsCancellationRequested ? $"The {resource} request exceeded its deadline."
                    : "The Engine transport was interrupted or unavailable.", status)
            };
            return EngineResourceResult<T>.Failed(failure);
        }
    }

    private static void EnsureContinuity(ITransportContinuity? continuity, long? generation)
    {
        if (continuity?.ConnectionGeneration != generation)
            throw new ResourceProtocolException(new(EngineResourceFailureKind.TransportLoss,
                "The Engine transport reopened. A new handshake must negotiate current metadata."));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token, string resource)
    {
        if (content.Headers.ContentLength > maximum) throw Oversized(resource);
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, maximum + 1)];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > maximum) throw Oversized(resource);
            buffer.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    private static ResourceProtocolException Oversized(string resource)
        => new(new(EngineResourceFailureKind.MalformedData, $"The {resource} response exceeds the configured size limit."));

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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return body.Length == 0 ? null : Encoding.UTF8.GetString(body); }
    }

    private static EngineResourceFailure HttpFailure(int status, string? message, string? suffix = null)
        => new(status switch
        {
            404 => EngineResourceFailureKind.NotFound,
            409 or 304 => EngineResourceFailureKind.Conflict,
            401 or 403 => EngineResourceFailureKind.AccessDenied,
            >= 400 and < 500 => EngineResourceFailureKind.InvalidRequest,
            _ => EngineResourceFailureKind.DaemonFailure
        }, $"The Engine returned HTTP {status}." + (suffix is null ? "" : " " + suffix), status, message);
}

internal sealed class ResourceProtocolException(EngineResourceFailure failure) : Exception(failure.Description)
{
    public EngineResourceFailure Failure { get; } = failure;
}
