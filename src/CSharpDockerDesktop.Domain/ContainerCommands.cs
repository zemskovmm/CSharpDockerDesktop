namespace CSharpDockerDesktop.Domain;

public enum ContainerCommand { Start, Stop, Restart, Delete }
public enum ContainerCommandDisposition { NotDispatched, Acknowledged, DaemonResponse, Uncertain }
public enum ContainerCommandAcknowledgement { Accepted, AlreadyInRequestedState }
public enum ContainerCommandFailureKind
{
    NotFound, Conflict, InvalidRequest, AccessDenied, DaemonFailure, UnexpectedResponse,
    TransportFailure, Deadline, ConnectionDisposed, StaleConnection, CapacityExceeded, Canceled
}

/// <summary>Original daemon content is sensitive and must never be logged automatically.</summary>
public sealed record ContainerCommandFailure(ContainerCommandFailureKind Kind, string Description);

/// <summary>A protocol outcome, never a statement of the container's final state.</summary>
public sealed record ContainerCommandResult(ContainerCommand Command, ContainerCommandDisposition Disposition,
    int? HttpStatusCode = null, string? DaemonMessage = null,
    ContainerCommandAcknowledgement? Acknowledgement = null, ContainerCommandFailure? Failure = null,
    bool ErrorBodyUnavailable = false)
{
    /// <summary>Every dispatched command needs authoritative reads, including a daemon rejection or 304.</summary>
    public bool RequiresReconciliation => Disposition != ContainerCommandDisposition.NotDispatched;
}

/// <summary>Cancellation remains normal termination. Outcome retains possible dispatch and response evidence.</summary>
public sealed class ContainerCommandCanceledException(ContainerCommandResult outcome, CancellationToken token)
    : OperationCanceledException("The container command was canceled; inspect Outcome before reconciling.", token)
{
    public ContainerCommandResult Outcome { get; } = outcome;
}
