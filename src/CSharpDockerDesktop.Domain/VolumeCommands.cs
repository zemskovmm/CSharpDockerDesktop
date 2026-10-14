namespace CSharpDockerDesktop.Domain;

public enum VolumeCommandDisposition { NotDispatched, Acknowledged, DaemonResponse, Uncertain }
public enum VolumeCommandFailureKind
{
    NotFound, Conflict, InvalidRequest, AccessDenied, DaemonFailure, UnexpectedResponse,
    TransportFailure, Deadline, ConnectionDisposed, StaleConnection, CapacityExceeded, Canceled
}

public sealed record VolumeCommandFailure(VolumeCommandFailureKind Kind, string Description);

/// <summary>A protocol outcome, never proof of volume absence or of which operation caused current state.
/// Original daemon message and body are sensitive; never log either automatically.</summary>
public sealed record VolumeCommandResult(VolumeCommandDisposition Disposition,
    int? HttpStatusCode = null, string? DaemonMessage = null, string? DaemonErrorBody = null,
    VolumeCommandFailure? Failure = null, bool ErrorBodyUnavailable = false)
{
    public bool RequiresReconciliation => Disposition != VolumeCommandDisposition.NotDispatched;
}

/// <summary>Normal cancellation retaining possible dispatch and available response evidence.</summary>
public sealed class VolumeCommandCanceledException(VolumeCommandResult outcome, CancellationToken token)
    : OperationCanceledException("The volume command was canceled; inspect Outcome before reconciling.", token)
{
    public VolumeCommandResult Outcome { get; } = outcome;
}
