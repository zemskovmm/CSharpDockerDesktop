using CSharpDockerDesktop.Domain;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed protocol capability. Does not select a command, reconnect, or own the selected connection.</summary>
public interface IContainerExec
{
    Task<ContainerExecCreationResult> CreateTtyExecAsync(ContainerExecCreateRequest request, CancellationToken cancellationToken = default);
    /// <summary>One-shot start on the exact binding. The token governs establishment and attachment lifetime.
    /// Cancellation throws ContainerExecStartCanceledException with historical outcome evidence; never retry automatically.</summary>
    Task<ContainerExecStartResult> StartTtyExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default);
    Task<ContainerExecInspectionResult> InspectExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default);
}

/// <summary>The caller supplies a full 64-character lowercase hexadecimal container Id and explicit argv; arguments are snapshotted before I/O.</summary>
public sealed record ContainerExecCreateRequest(string ContainerId, IReadOnlyList<string> Arguments)
{
    public override string ToString() => nameof(ContainerExecCreateRequest);
}

/// <summary>Owned raw combined TTY stream; bytes are sensitive and must not be logged. No decoding or multiplex framing.
/// Output uses bounded backpressure. Canceling an individual channel wait does not cancel the attachment.
/// Owner disposal or the start token cancels this attachment. EOF/closure never establishes process exit or kills it.</summary>
public interface IContainerExecAttachment : IAsyncDisposable
{
    ContainerExecInstance Instance { get; }
    ChannelReader<ImmutableArray<byte>> Output { get; }
    /// <summary>Completes only after stream cleanup and admitted input work join. EOF permits buffered draining;
    /// cancellation/disposal/generation change revoke buffered delivery. Inspect exec separately for running/exit evidence.</summary>
    Task<ContainerExecAttachmentCompletion> Completion { get; }
    /// <summary>Copies bounded input before I/O. One admitted write, no pending queue. Cancellation during a possible
    /// write terminates the attachment and returns uncertain write evidence. Never replays input. No half-close/kill.</summary>
    Task<ContainerExecWriteResult> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}

/// <summary>Attachment is usable only for Established without Failure. Response and UpgradeObserved are historical evidence.</summary>
public sealed record ContainerExecStartResult(ContainerExecStartDisposition Disposition,
    IContainerExecAttachment? Attachment = null, ContainerExecResponseEvidence? Response = null,
    ContainerExecFailure? Failure = null, bool UpgradeObserved = false)
{
    public override string ToString() => $"Exec start {Disposition}; failure: {Failure?.Kind}";
}
public sealed class ContainerExecStartCanceledException(ContainerExecStartResult outcome, CancellationToken token)
    : OperationCanceledException("Exec start was canceled; inspect Outcome before further action.", token)
{
    public ContainerExecStartResult Outcome { get; } = outcome;
}
