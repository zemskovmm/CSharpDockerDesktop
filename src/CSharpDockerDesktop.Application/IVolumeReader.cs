using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed read-only capability. The connection owner retains all lifetime/transport ownership.</summary>
public interface IVolumeReader
{
    /// <summary>Lists volumes and retains daemon warnings. Null collections are unavailable; warnings must not be hidden as empty success.</summary>
    Task<EngineResourceResult<VolumeListing>> ListVolumesAsync(CancellationToken cancellationToken = default);

    /// <summary>Uses the complete original name or ID. Raw JSON and metadata must never be automatically logged. Caller cancellation throws.</summary>
    Task<EngineResourceResult<VolumeInspection>> InspectVolumeAsync(string name, CancellationToken cancellationToken = default);
}
