namespace CSharpDockerDesktop.Domain;

public enum NetworkCommandDisposition { NotDispatched, Acknowledged, DaemonResponse, Uncertain }
public enum NetworkCommandFailureKind
{
    NotFound, Conflict, InvalidRequest, AccessDenied, DaemonFailure, UnexpectedResponse,
    TransportFailure, Deadline, ConnectionDisposed, StaleConnection, CapacityExceeded, Canceled
}

public sealed record NetworkCommandFailure(NetworkCommandFailureKind Kind, string Description);

/// <summary>A protocol outcome, never proof of network absence or of which operation caused current state.
/// Original daemon message and body are sensitive; never log either automatically.</summary>
public sealed record NetworkCommandResult(NetworkCommandDisposition Disposition,
    int? HttpStatusCode = null, string? DaemonMessage = null, string? DaemonErrorBody = null,
    NetworkCommandFailure? Failure = null, bool ErrorBodyUnavailable = false)
{
    public bool RequiresReconciliation => Disposition != NetworkCommandDisposition.NotDispatched;
}

/// <summary>Normal cancellation retaining possible dispatch and available response evidence.</summary>
public sealed class NetworkCommandCanceledException(NetworkCommandResult outcome, CancellationToken token)
    : OperationCanceledException("The network command was canceled; inspect Outcome before reconciling.", token)
{
    public NetworkCommandResult Outcome { get; } = outcome;
}
