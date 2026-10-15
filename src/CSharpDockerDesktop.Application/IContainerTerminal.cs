using System.Collections.Immutable;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public enum ContainerTerminalPhase { Closed, AwaitingEligibility, Creating, Starting, Attached, Draining, Interrupted, Rejected }
public enum ContainerTerminalAdmission { Accepted, InvalidRequest, Unavailable, WrongSelection, NotFound, Ineligible }
public sealed record ContainerTerminalOpenResult(ContainerTerminalAdmission Admission, long SessionId = 0)
{
    public bool Accepted => Admission == ContainerTerminalAdmission.Accepted;
}
/// <summary>Only non-payload state. Observed fields are daemon evidence, never inferred from EOF or acknowledgement.</summary>
public sealed record ContainerTerminalSnapshot(long SessionId = 0, long ConnectionGeneration = 0,
    string? ContainerId = null, ContainerTerminalPhase Phase = ContainerTerminalPhase.Closed,
    bool LifecycleCurrent = false, ContainerExecCreationDisposition Creation = ContainerExecCreationDisposition.NotDispatched,
    ContainerExecStartDisposition Start = ContainerExecStartDisposition.NotDispatched, bool? ObservedRunning = null,
    long? ObservedExitCode = null, ContainerExecFailureKind? Failure = null, ContainerExecFailureKind? InspectionFailure = null,
    ContainerExecAttachmentTermination? AttachmentEnd = null)
{
    public override string ToString() => $"Terminal {SessionId}: {Phase}; creation {Creation}; start {Start}; failure {Failure}";
}
/// <summary>Explicit private evidence read. Fixed slots are overwritten, not an outcome history. Never log/export automatically.</summary>
public sealed record ContainerTerminalEvidence(ContainerExecCreationResult? Creation, ContainerExecStartResult? Start,
    ContainerExecInspectionResult? Inspection, ContainerExecResizeResult? Resize, ContainerExecWriteResult? Input,
    ContainerExecFailure? CleanupFailure = null)
{
    public override string ToString() => nameof(ContainerTerminalEvidence);
}
public sealed record ContainerTerminalOutput(long SessionId, ImmutableArray<byte> Bytes, bool Completed, bool Revoked)
{
    public override string ToString() => $"Terminal output {SessionId}; completed {Completed}; revoked {Revoked}";
}
public interface IContainerTerminal
{
    ContainerTerminalSnapshot ContainerTerminal { get; }
    event EventHandler? ContainerTerminalChanged;
    ContainerTerminalOpenResult OpenContainerTerminal(string containerId, IReadOnlyList<string> arguments, int columns, int rows);
    ContainerTerminalEvidence? ReadContainerTerminalEvidence(long sessionId);
    /// <summary>One admitted consumer wait. Bytes must only be delivered while IsContainerTerminalCurrent remains true.</summary>
    Task<ContainerTerminalOutput> ReadContainerTerminalOutputAsync(long sessionId, CancellationToken cancellationToken = default);
    bool IsContainerTerminalCurrent(long sessionId, string containerId, long connectionGeneration);
    Task<ContainerExecWriteResult> WriteContainerTerminalAsync(long sessionId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    Task<ContainerExecResizeResult> ResizeContainerTerminalAsync(long sessionId, int columns, int rows);
    Task CloseContainerTerminalAsync();
}
/// <summary>Independent finite coordination budgets; implementation defaults, not durable product policy.</summary>
public sealed record ContainerTerminalOptions
{
    public int MaximumArguments { get; init; } = 128;
    public int MaximumArgumentBytes { get; init; } = 4096;
    public int MaximumCommandBytes { get; init; } = 32768;
    public int OutputChunks { get; init; } = 16;
    public int OutputChunkBytes { get; init; } = 4096;
    public int MaximumInputBytes { get; init; } = 4096;
    public int MaximumEvidenceBytes { get; init; } = 8192;
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan InspectionInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan NotificationInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    internal void Validate()
    {
        if (MaximumArguments is < 1 or > 4096 || MaximumArgumentBytes is < 1 or > 1048576
            || MaximumCommandBytes is < 1 or > 1048576 || OutputChunks is < 1 or > 1024
            || OutputChunkBytes is < 1 or > 1048576 || MaximumInputBytes is < 1 or > 1048576
            || MaximumEvidenceBytes is < 1 or > 1048576 || !Valid(OperationTimeout)
            || !Valid(InspectionInterval) || !Valid(NotificationInterval)) throw new ArgumentOutOfRangeException(nameof(ContainerTerminalOptions));
        static bool Valid(TimeSpan t) => t > TimeSpan.Zero && t.TotalMilliseconds <= int.MaxValue;
    }
}
