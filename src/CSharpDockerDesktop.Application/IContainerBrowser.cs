using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>In-memory state scoped to one negotiated connection. Ready with no rows is a successful empty list.</summary>
public sealed record ContainerBrowsingSnapshot(long ConnectionGeneration, ResourceReadState State,
    IReadOnlyList<ContainerSummary> Containers, string? SelectedContainerId = null,
    ResourceReadState InspectionState = ResourceReadState.Unavailable, ContainerInspection? Inspection = null,
    EngineResourceFailure? Failure = null, EngineResourceFailure? InspectionFailure = null);

/// <summary>Read-only browsing through the session's retained connection; never owns or exposes its transport.</summary>
public interface IContainerBrowser
{
    ContainerBrowsingSnapshot ContainerSnapshot { get; }
    /// <summary>Read the latest immutable snapshot; notification can originate off the UI thread.</summary>
    event EventHandler? ContainersChanged;
    /// <summary>Supersedes pending refreshes. At most one container read executes at a time.</summary>
    void RefreshContainers();
    /// <summary>Select a full ID from the current snapshot. Null or a disappeared ID clears selection and cancels inspection.</summary>
    void SelectContainer(string? containerId);
    void RefreshContainerInspection();
    /// <summary>Cancel and clear inspection while retaining selection.</summary>
    void ClearContainerInspection();
}
