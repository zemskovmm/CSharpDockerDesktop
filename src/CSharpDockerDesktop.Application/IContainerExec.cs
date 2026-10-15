using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed protocol capability. Does not start/attach, select a command, reconnect, or own the connection.</summary>
public interface IContainerExec
{
    Task<ContainerExecCreationResult> CreateTtyExecAsync(ContainerExecCreateRequest request, CancellationToken cancellationToken = default);
    Task<ContainerExecInspectionResult> InspectExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default);
}

/// <summary>The caller supplies a full 64-character lowercase hexadecimal container Id and explicit argv; arguments are snapshotted before I/O.</summary>
public sealed record ContainerExecCreateRequest(string ContainerId, IReadOnlyList<string> Arguments)
{
    public override string ToString() => nameof(ContainerExecCreateRequest);
}
