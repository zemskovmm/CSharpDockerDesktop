using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerContainerCommands
{
    internal static async Task<ContainerCommandResult> ExecuteAsync(HttpClient http, EngineApiVersion api,
        ContainerCommand command, string identity, int? grace, bool force, DockerConnectionOptions options,
        ITransportContinuity? continuity, long? generation, SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        var dispatched = false;
        int? status = null;
        string? daemonMessage = null;
        bool bodyUnavailable = false;
        ContainerCommandResult Result(ContainerCommandDisposition disposition, ContainerCommandFailureKind kind, string description)
            => new(command, disposition, status, daemonMessage, Failure: new(kind, description), ErrorBodyUnavailable: bodyUnavailable);
        void CheckCancellation()
        {
            if (caller.IsCancellationRequested)
                throw new ContainerCommandCanceledException(Result(dispatched ? ContainerCommandDisposition.Uncertain : ContainerCommandDisposition.NotDispatched,
                    ContainerCommandFailureKind.Canceled, "The container command was canceled."), caller);
        }
        bool Stale() => continuity?.ConnectionGeneration != generation;
        CheckCancellation();
        // Docker IDs/names are ASCII; canonical inspect names may have one leading '/'.
        var name = identity?.StartsWith('/') == true ? identity[1..] : identity;
        if (string.IsNullOrEmpty(name) || identity!.Length > 4096 || !char.IsAsciiLetterOrDigit(name[0])
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) || grace < 0)
            return Result(ContainerCommandDisposition.NotDispatched, ContainerCommandFailureKind.InvalidRequest,
                "Supply a valid container ID/name and a nonnegative grace period.");
        if (lifetime.IsCancellationRequested)
            return Result(ContainerCommandDisposition.NotDispatched, ContainerCommandFailureKind.ConnectionDisposed, "The Engine connection was disposed.");
        if (Stale())
            return Result(ContainerCommandDisposition.NotDispatched, ContainerCommandFailureKind.StaleConnection, "Negotiate a new Engine connection before issuing commands.");
        if (!await slots.WaitAsync(0).ConfigureAwait(false))
            return Result(ContainerCommandDisposition.NotDispatched, ContainerCommandFailureKind.CapacityExceeded, "The connection's command capacity is occupied.");
        using var changed = new CancellationTokenSource();
        void Reopened()
        {
            // An invocation captured before unsubscription may race the local source's disposal.
            try { changed.Cancel(); } catch (ObjectDisposedException) { }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime, changed.Token);
        deadline.CancelAfter(command is ContainerCommand.Stop or ContainerCommand.Restart ? options.GracefulCommandTimeout : options.CommandTimeout);
        if (continuity is not null) continuity.Reopened += Reopened;
        try
        {
            var path = $"v{api}/containers/{Uri.EscapeDataString(identity)}";
            path += command switch
            {
                ContainerCommand.Start => "/start",
                ContainerCommand.Stop => "/stop" + GraceQuery(grace),
                ContainerCommand.Restart => "/restart" + GraceQuery(grace),
                _ => $"?force={(force ? "true" : "false")}&v=false"
            };
            using var request = new HttpRequestMessage(command == ContainerCommand.Delete ? HttpMethod.Delete : HttpMethod.Post, path)
            {
                // HTTP/1.1 only: no alternate protocol/auth/redirect retry path.
                Version = new Version(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            CheckCancellation();
            if (lifetime.IsCancellationRequested || Stale())
                return Result(ContainerCommandDisposition.NotDispatched,
                    Stale() ? ContainerCommandFailureKind.StaleConnection : ContainerCommandFailureKind.ConnectionDisposed,
                    "The negotiated connection ended before dispatch.");
            // Conservative boundary: SendAsync may queue before writing. From this point failure cannot prove non-application.
            dispatched = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (status != 204)
            {
                bodyUnavailable = true;
                var body = await ReadErrorAsync(response.Content, options.MaximumCommandErrorBytes, deadline.Token).ConfigureAwait(false);
                bodyUnavailable = body is null;
                if (body is not null) daemonMessage = await Task.Run(() => ErrorMessage(body), deadline.Token).ConfigureAwait(false);
            }
            CheckCancellation();
            deadline.Token.ThrowIfCancellationRequested();
            if (Stale())
                return Result(ContainerCommandDisposition.Uncertain, ContainerCommandFailureKind.StaleConnection, "The Engine transport changed after command dispatch.");
            if (status == 204 || status == 304 && command is ContainerCommand.Start or ContainerCommand.Stop)
                return new(command, ContainerCommandDisposition.Acknowledged, status, daemonMessage,
                    status == 304 ? ContainerCommandAcknowledgement.AlreadyInRequestedState : ContainerCommandAcknowledgement.Accepted,
                    ErrorBodyUnavailable: bodyUnavailable);
            return Result(ContainerCommandDisposition.DaemonResponse, status switch
            {
                404 => ContainerCommandFailureKind.NotFound,
                409 => ContainerCommandFailureKind.Conflict,
                401 or 403 => ContainerCommandFailureKind.AccessDenied,
                >= 400 and < 500 => ContainerCommandFailureKind.InvalidRequest,
                >= 500 => ContainerCommandFailureKind.DaemonFailure,
                _ => ContainerCommandFailureKind.UnexpectedResponse
            }, $"The Engine returned HTTP {status} for the container command.");
        }
        catch (ContainerCommandCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or ConnectionProtocolException)
        {
            CheckCancellation();
            var kind = lifetime.IsCancellationRequested ? ContainerCommandFailureKind.ConnectionDisposed
                : Stale() || changed.IsCancellationRequested ? ContainerCommandFailureKind.StaleConnection
                : deadline.IsCancellationRequested ? ContainerCommandFailureKind.Deadline
                : AccessDenied(ex) ? ContainerCommandFailureKind.AccessDenied : ContainerCommandFailureKind.TransportFailure;
            return Result(dispatched ? ContainerCommandDisposition.Uncertain : ContainerCommandDisposition.NotDispatched, kind,
                kind switch
                {
                    ContainerCommandFailureKind.ConnectionDisposed => "The connection was disposed after command dispatch.",
                    ContainerCommandFailureKind.StaleConnection => "The negotiated transport changed; reconcile through a new connection.",
                    ContainerCommandFailureKind.Deadline => "The container command exceeded its deadline; reconcile current state.",
                    ContainerCommandFailureKind.AccessDenied => "Access to the Engine was denied.",
                    _ => "The command response was interrupted or unavailable; reconcile current state."
                });
        }
        finally
        {
            if (continuity is not null) continuity.Reopened -= Reopened;
            slots.Release();
        }
    }

    private static string GraceQuery(int? seconds) => seconds.HasValue ? "?t=" + seconds.Value.ToString(CultureInfo.InvariantCulture) : "";
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
