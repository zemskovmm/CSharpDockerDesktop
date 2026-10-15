using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal sealed partial class DockerEngineConnection
{
    private readonly HashSet<Task> execOperations = [];
    private readonly SemaphoreSlim execInspectionSlots = new(options.MaximumConcurrentExecInspections);

    public Task<ContainerExecCreationResult> CreateTtyExecAsync(ContainerExecCreateRequest request, CancellationToken cancellationToken = default)
        => TrackExecOperation(() => DockerContainerExec.CreateAsync(http, Metadata.Api.Negotiated, request, options,
            continuity, generation, commandSlots, lifetime.Token, cancellationToken));

    public Task<ContainerExecInspectionResult> InspectExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default)
        => TrackExecOperation(() => DockerContainerExec.InspectAsync(http, Metadata.Api.Negotiated, instance, options,
            continuity, generation, execInspectionSlots, lifetime.Token, cancellationToken));

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
