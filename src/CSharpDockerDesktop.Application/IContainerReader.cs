using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>A borrowed, read-only capability of a negotiated IEngineConnection. Its owner controls lifetime.</summary>
public interface IContainerReader
{
    /// <summary>Includes stopped containers. Cancellation throws OperationCanceledException.</summary>
    Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default);
    /// <summary>Accepts an ID or name. Raw inspect contains sensitive data; never automatically log it.</summary>
    Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default);
}
