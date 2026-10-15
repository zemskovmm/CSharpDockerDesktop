using System.Collections.Immutable;

namespace CSharpDockerDesktop.Domain;

/// <summary>Exact identities from the selected Engine. Scope is the owning negotiated connection, not a global cache key.</summary>
public sealed record ContainerExecInstance(string ContainerId, string ExecId);

public enum ContainerExecCreationDisposition { NotDispatched, Acknowledged, DaemonRejected, Uncertain }
public enum ContainerExecFailureKind
{
    InvalidRequest, NotFound, Conflict, AccessDenied, DaemonFailure, UnexpectedResponse, MalformedResponse,
    TransportFailure, Deadline, CapacityExceeded, StaleConnection, ConnectionDisposed, Canceled
}
public sealed record ContainerExecFailure(ContainerExecFailureKind Kind, string Description);

/// <summary>Deliberate sensitive evidence, including invalid UTF-8 or malformed JSON. Never automatically log/export this body or message.</summary>
public sealed record ContainerExecResponseEvidence(int HttpStatusCode, ImmutableArray<byte> RawBody,
    bool BodyUnavailable, string? DaemonMessage = null)
{
    public override string ToString() => $"Exec response HTTP {HttpStatusCode}; body unavailable: {BodyUnavailable}";
}

/// <summary>Protocol history only. AcknowledgedInstance may survive cancellation/staleness as historical evidence; it is not a running session.</summary>
public sealed record ContainerExecCreationResult(ContainerExecCreationDisposition Disposition,
    ContainerExecInstance? AcknowledgedInstance = null, ContainerExecResponseEvidence? Response = null, ContainerExecFailure? Failure = null)
{
    public bool IsUsableAcknowledgement => Disposition == ContainerExecCreationDisposition.Acknowledged && AcknowledgedInstance is not null && Failure is null;
    public override string ToString() => $"Exec creation {Disposition}; failure: {Failure?.Kind}";
}

public sealed class ContainerExecCreateCanceledException(ContainerExecCreationResult outcome, CancellationToken token)
    : OperationCanceledException("Exec creation was canceled; inspect Outcome before taking any further action.", token)
{
    public ContainerExecCreationResult Outcome { get; } = outcome;
}

/// <summary>Null is unavailable, never a default command, false running state, or successful exit. Raw response distinguishes missing/null.</summary>
public sealed record ContainerExecProcessConfiguration(bool? Tty, bool? Privileged, string? User,
    string? Entrypoint, ImmutableArray<string>? Arguments)
{
    public override string ToString() => nameof(ContainerExecProcessConfiguration);
}

/// <summary>Actual daemon fields, including ExitCode while Running is true. Neither false nor zero proves the exec ever started.</summary>
public sealed record ContainerExecInspection(ContainerExecInstance RequestedInstance, string? Id, string? ContainerId,
    bool? Running, long? ExitCode, long? Pid, bool? OpenStdin, bool? OpenStdout, bool? OpenStderr,
    bool? CanRemove, string? DetachKeys, ContainerExecProcessConfiguration? ProcessConfiguration)
{
    public override string ToString() => nameof(ContainerExecInspection);
}

public sealed record ContainerExecInspectionResult(ContainerExecInspection? Value = null,
    ContainerExecResponseEvidence? Response = null, ContainerExecFailure? Failure = null)
{
    public bool IsSuccess => Value is not null && Failure is null;
    public override string ToString() => $"Exec inspection success: {IsSuccess}; failure: {Failure?.Kind}";
}

public enum ContainerExecStartDisposition { NotDispatched, DaemonRejected, Established, Uncertain }
public enum ContainerExecAttachmentTermination { EndOfStream, Canceled, Failed }
public sealed record ContainerExecAttachmentCompletion(ContainerExecAttachmentTermination Termination, ContainerExecFailure? Failure = null);
public enum ContainerExecWriteDisposition { NotWritten, Written, Uncertain }
public sealed record ContainerExecWriteResult(ContainerExecWriteDisposition Disposition, ContainerExecFailure? Failure = null);
