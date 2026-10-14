using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed read-only capability. The connection owner retains transport and disposal ownership.</summary>
public interface INetworkReader
{
    /// <summary>List metadata never establishes attachment completeness. Caller cancellation throws.</summary>
    Task<EngineResourceResult<IReadOnlyList<NetworkSummary>>> ListNetworksAsync(CancellationToken cancellationToken = default);

    /// <summary>Uses the complete original network ID. All metadata and raw JSON are private and must not be automatically logged.</summary>
    Task<EngineResourceResult<NetworkInspection>> InspectNetworkAsync(string id, CancellationToken cancellationToken = default);
}
