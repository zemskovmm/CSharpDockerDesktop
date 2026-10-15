using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal sealed partial class DockerEngineConnection
{
    private readonly HashSet<DockerExecAttachment> execAttachments = [];

    public async Task<ContainerExecStartResult> StartTtyExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default)
    {
        ContainerExecStartResult? rejected = null;
        DockerExecAttachment? attachment = null;
        lock (eventGate)
        {
            ContainerExecFailureKind? kind = cancellationToken.IsCancellationRequested ? ContainerExecFailureKind.Canceled
                : disposed != 0 ? ContainerExecFailureKind.ConnectionDisposed
                : !HasContinuity ? ContainerExecFailureKind.StaleConnection
                : instance is null || !ValidExecIdentity(instance.ContainerId) || !ValidExecIdentity(instance.ExecId) ? ContainerExecFailureKind.InvalidRequest
                : execAttachments.Count >= options.MaximumConcurrentExecAttachments ? ContainerExecFailureKind.CapacityExceeded : null;
            if (kind is { } error) rejected = new(ContainerExecStartDisposition.NotDispatched, Failure: DockerExecAttachment.Failure(error));
            else
            {
                attachment = new(this, client, options, instance!, lifetime.Token, cancellationToken,
                    ended => { lock (eventGate) execAttachments.Remove(ended); });
                execAttachments.Add(attachment);
                attachment.Start();
            }
        }
        var result = rejected ?? await attachment!.Opened.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || result.Disposition == ContainerExecStartDisposition.Established && !HasContinuity)
        {
            if (attachment is not null) await attachment.DisposeAsync().ConfigureAwait(false);
            result = result with { Attachment = null,
                Disposition = result.Disposition == ContainerExecStartDisposition.Established ? ContainerExecStartDisposition.Uncertain : result.Disposition,
                Failure = DockerExecAttachment.Failure(cancellationToken.IsCancellationRequested ? ContainerExecFailureKind.Canceled
                    : HasTransportContinuity ? ContainerExecFailureKind.ConnectionDisposed : ContainerExecFailureKind.StaleConnection) };
        }
        if (cancellationToken.IsCancellationRequested) throw new ContainerExecStartCanceledException(result, cancellationToken);
        return result;
    }
    private static bool ValidExecIdentity(string? id) => id is { Length: 64 } && id.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');

    private readonly HashSet<Task> execOperations = [];
    private readonly SemaphoreSlim execInspectionSlots = new(options.MaximumConcurrentExecInspections);

    public Task<ContainerExecCreationResult> CreateTtyExecAsync(ContainerExecCreateRequest request, CancellationToken cancellationToken = default)
        => TrackExecOperation(() => DockerContainerExec.CreateAsync(http, Metadata.Api.Negotiated, request, options,
            continuity, generation, commandSlots, lifetime.Token, cancellationToken));

    public Task<ContainerExecInspectionResult> InspectExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default)
        => TrackExecOperation(() => DockerContainerExec.InspectAsync(http, Metadata.Api.Negotiated, instance, options,
            continuity, generation, execInspectionSlots, lifetime.Token, cancellationToken));

    public Task<ContainerExecResizeResult> ResizeTtyExecAsync(ContainerExecInstance instance, int columns, int rows, CancellationToken cancellationToken = default)
        => TrackExecOperation(() => DockerContainerExec.ResizeAsync(http, Metadata.Api.Negotiated, instance, columns, rows,
            options, continuity, generation, commandSlots, lifetime.Token, cancellationToken));

    private Task<T> TrackExecOperation<T>(Func<Task<T>> operation)
    {
        lock (eventGate)
        {
            // Ended owner calls return bounded NotDispatched/ConnectionDisposed without becoming new owned work.
            if (disposed != 0) return operation();
            // Register before invoking: synchronous fake/fast transport work may reenter owner disposal.
            var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            execOperations.Add(joined.Task);
            Task<T> task;
            try { task = operation(); }
            catch { execOperations.Remove(joined.Task); joined.TrySetResult(); throw; }
            _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lock (eventGate) { execOperations.Remove(joined.Task); joined.TrySetResult(); }
            },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
}
