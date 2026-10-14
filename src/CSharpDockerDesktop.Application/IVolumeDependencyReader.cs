using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed read-only capability; the retained Engine connection owns the transport and lifetime.</summary>
public interface IVolumeDependencyReader
{
    /// <summary>
    /// Queries fresh daemon evidence for one complete original volume name, including stopped containers.
    /// Complete empty evidence is not proof that the volume exists or that deletion will succeed.
    /// Missing mount evidence remains unavailable/incomplete; failures and private daemon messages remain distinct.
    /// Caller cancellation throws. No metadata is automatically logged and no resource is mutated.
    /// </summary>
    Task<EngineResourceResult<VolumeDependencies>> QueryVolumeDependenciesAsync(string name,
        CancellationToken cancellationToken = default);
}
