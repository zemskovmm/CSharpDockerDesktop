using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Current-generation rows remain useful during Loading/Failed reads. Only Ready establishes
/// authoritative list absence. Null metadata/IPAM/attachments remain unavailable, not observed empty.
/// Neither lists nor inspected attachment observations establish cluster-wide completeness or deletion safety.
/// Nested evidence is immutable; raw JSON and daemon messages must not be automatically logged/transmitted.</summary>
public sealed record NetworkBrowsingSnapshot(long ConnectionGeneration, ResourceReadState State,
    IReadOnlyList<NetworkSummary> Networks, string? SelectedNetworkId = null,
    ResourceReadState InspectionState = ResourceReadState.Unavailable, NetworkInspection? Inspection = null,
    EngineResourceFailure? Failure = null, EngineResourceFailure? InspectionFailure = null)
{
    public bool IsAuthoritative => State == ResourceReadState.Ready;
    public bool HasRetainedRows => Networks.Count > 0 && State != ResourceReadState.Ready;
}

/// <summary>Application-owned read-only browsing using the retained connection's borrowed reader.</summary>
public interface INetworkBrowser
{
    NetworkBrowsingSnapshot NetworkSnapshot { get; }
    /// <summary>May originate off the UI thread. Dispatch and read the latest immutable snapshot.</summary>
    event EventHandler? NetworksChanged;
    void RefreshNetworks();
    /// <summary>Admit only a complete ordinal ID from current rows. Null or absent IDs clear selection.</summary>
    void SelectNetwork(string? id);
    void RefreshNetworkInspection();
    /// <summary>Supersede detail work and clear raw data while retaining selection.</summary>
    void ClearNetworkInspection();
}
