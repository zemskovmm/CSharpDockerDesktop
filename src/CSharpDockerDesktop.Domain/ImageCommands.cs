namespace CSharpDockerDesktop.Domain;

public enum ImageCommandDisposition { NotDispatched, Acknowledged, DaemonResponse, Uncertain }
public enum ImageCommandFailureKind
{
    NotFound, Conflict, InvalidRequest, AccessDenied, DaemonFailure, UnexpectedResponse,
    TransportFailure, Deadline, ConnectionDisposed, StaleConnection, CapacityExceeded, Canceled
}

/// <summary>Original daemon evidence is sensitive; never log it automatically.</summary>
public sealed record ImageRemovalRecord(string? Untagged = null, string? Deleted = null);
public sealed record ImageCommandFailure(ImageCommandFailureKind Kind, string Description);

/// <summary>A protocol outcome, never proof of image absence or of which operation caused current state.</summary>
public sealed record ImageCommandResult(ImageCommandDisposition Disposition,
    int? HttpStatusCode = null, string? DaemonMessage = null,
    IReadOnlyList<ImageRemovalRecord>? RemovalRecords = null, ImageCommandFailure? Failure = null,
    bool ResponseBodyUnavailable = false)
{
    public bool RequiresReconciliation => Disposition != ImageCommandDisposition.NotDispatched;
}

/// <summary>Normal cancellation retaining possible dispatch and completed removal-response evidence.</summary>
public sealed class ImageCommandCanceledException(ImageCommandResult outcome, CancellationToken token)
    : OperationCanceledException("The image command was canceled; inspect Outcome before reconciling.", token)
{
    public ImageCommandResult Outcome { get; } = outcome;
}
