using System.Collections.Immutable;
using System.Globalization;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static partial class DockerContainerExec
{
    internal static async Task<ContainerExecResizeResult> ResizeAsync(HttpClient http, EngineApiVersion api,
        ContainerExecInstance instance, int columns, int rows, DockerConnectionOptions options,
        ITransportContinuity? continuity, long? generation, SemaphoreSlim slots,
        CancellationToken lifetime, CancellationToken caller)
    {
        var resize = new Exchange();
        var binding = new Exchange();
        ContainerExecFailure? failure = null;
        bool Stale() => continuity?.ConnectionGeneration != generation;
        ContainerExecResizeResult Result()
        {
            var result = new ContainerExecResizeResult(!resize.Dispatched ? ContainerExecResizeDisposition.NotDispatched
                : resize.Status >= 400 ? ContainerExecResizeDisposition.DaemonRejected
                : resize.Status == 200 && failure is null ? ContainerExecResizeDisposition.Acknowledged
                : ContainerExecResizeDisposition.Uncertain, instance, columns, rows, resize.Evidence, binding.Evidence, failure);
            if (caller.IsCancellationRequested)
                throw new ContainerExecResizeCanceledException(result with
                {
                    Disposition = result.Disposition == ContainerExecResizeDisposition.Acknowledged
                        ? ContainerExecResizeDisposition.Uncertain : result.Disposition,
                    Failure = Failure(ContainerExecFailureKind.Canceled)
                }, caller);
            return result;
        }
        if (caller.IsCancellationRequested) return Result();
        if (instance is null || !ValidIdentity(instance.ContainerId) || !ValidIdentity(instance.ExecId)
            || columns is < 1 or > 65535 || rows is < 1 or > 65535)
        {
            failure = new(ContainerExecFailureKind.InvalidRequest, "Supply exact Engine container/exec identities and explicit columns/rows from 1 through 65535.");
            return Result();
        }
        if (lifetime.IsCancellationRequested) { failure = Failure(ContainerExecFailureKind.ConnectionDisposed); return Result(); }
        if (Stale()) { failure = Failure(ContainerExecFailureKind.StaleConnection); return Result(); }
        if (!await slots.WaitAsync(0).ConfigureAwait(false))
        { failure = Failure(ContainerExecFailureKind.CapacityExceeded); return Result(); }

        using var changed = new CancellationTokenSource();
        void Reopened() { try { changed.Cancel(); } catch (ObjectDisposedException) { } }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime, changed.Token);
        // One finite lifetime includes binding verification, HTTP queuing, headers, body and cleanup.
        deadline.CancelAfter(options.CommandTimeout);
        if (continuity is not null) continuity.Reopened += Reopened;
        try
        {
            // Resize's route contains only exec ID. Verify the exact container binding on this same
            // sealed ordinary pool before permitting the mutation; no creation/start or attachment I/O.
            await ObserveAsync($"exec/{instance.ExecId}/json", false).ConfigureAwait(false);
            failure = binding.Failure;
            if (failure is null && (binding.Inspection?.Id != instance.ExecId
                || binding.Inspection.ContainerId != instance.ContainerId
                || binding.Inspection.ProcessConfiguration?.Tty != true))
                failure = new(ContainerExecFailureKind.InvalidRequest, "The Engine did not establish the exact requested TTY exec binding.");
            if (failure is null)
            {
                var query = $"h={rows.ToString(CultureInfo.InvariantCulture)}&w={columns.ToString(CultureInfo.InvariantCulture)}";
                await ObserveAsync($"exec/{instance.ExecId}/resize?{query}", true).ConfigureAwait(false);
                failure = resize.Failure;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or ConnectionProtocolException)
        { failure = Failure(AccessDenied(ex) ? ContainerExecFailureKind.AccessDenied : ContainerExecFailureKind.TransportFailure); }
        finally
        {
            if (continuity is not null) continuity.Reopened -= Reopened;
            slots.Release();
        }
        // Late/noncooperative response or cleanup evidence must never become current success.
        if (caller.IsCancellationRequested) failure = Failure(ContainerExecFailureKind.Canceled);
        else if (lifetime.IsCancellationRequested) failure = Failure(ContainerExecFailureKind.ConnectionDisposed);
        else if (Stale() || changed.IsCancellationRequested) failure = Failure(ContainerExecFailureKind.StaleConnection);
        else if (deadline.IsCancellationRequested) failure = Failure(ContainerExecFailureKind.Deadline);
        return Result();

        async Task ObserveAsync(string path, bool mutation)
        {
            using var request = new HttpRequestMessage(mutation ? HttpMethod.Post : HttpMethod.Get, $"v{api}/{path}")
            { Version = new(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            deadline.Token.ThrowIfCancellationRequested();
            if (Stale()) throw new IOException();
            var exchange = mutation ? resize : binding;
            if (mutation) resize.Dispatched = true; // Conservative SendAsync/queuing boundary; never replay.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            exchange.Status = (int)response.StatusCode;
            var bytes = await ReadBoundedAsync(response.Content, exchange.Status == 200
                ? options.MaximumExecResponseBytes : options.MaximumCommandErrorBytes, deadline.Token).ConfigureAwait(false);
            if (bytes is null)
                exchange.Failure = Failure(exchange.Status == 200 ? ContainerExecFailureKind.MalformedResponse : HttpFailure(exchange.Status.Value));
            else
            {
                exchange.Body = ImmutableArray.CreateRange(bytes);
                exchange.BodyUnavailable = false;
                if (mutation && exchange.Status == 200)
                {
                    // Official acknowledgement has no payload. Preserve unexpected bytes privately.
                    if (bytes.Length != 0) exchange.Failure = Failure(ContainerExecFailureKind.MalformedResponse);
                }
                else await Task.Run(() => Decode(exchange, bytes, instance, false, options)).ConfigureAwait(false);
            }
        }
    }
}
