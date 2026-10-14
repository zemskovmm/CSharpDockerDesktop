using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Useful rows are retained during refresh/failure and merged during incomplete listings.
/// LatestListing preserves the latest accepted collection and warnings, including null versus empty.
/// Only IsAuthoritative establishes list absence. All nested collections are immutable.</summary>
public sealed record VolumeBrowsingSnapshot(long ConnectionGeneration, ResourceReadState State,
    IReadOnlyList<VolumeSummary> Volumes, string? SelectedVolumeName = null,
    ResourceReadState InspectionState = ResourceReadState.Unavailable, VolumeInspection? Inspection = null,
    EngineResourceFailure? Failure = null, EngineResourceFailure? InspectionFailure = null,
    VolumeListing? LatestListing = null, VolumeListCompleteness Completeness = VolumeListCompleteness.Unavailable,
    bool HasRetainedRows = false)
{
    public IReadOnlyList<string>? Warnings => LatestListing?.Warnings;
    public bool HasWarnings => LatestListing?.HasWarnings == true;
    public bool IsAuthoritative => State == ResourceReadState.Ready && Completeness == VolumeListCompleteness.Complete;
}

/// <summary>Application-owned read-only browsing on the retained negotiated connection.</summary>
public interface IVolumeBrowser
{
    VolumeBrowsingSnapshot VolumeSnapshot { get; }
    /// <summary>Notification may originate off the UI thread. Read the latest immutable snapshot.</summary>
    event EventHandler? VolumesChanged;
    void RefreshVolumes();
    /// <summary>Select a complete ordinal name in usable rows; null or absent names clear selection.</summary>
    void SelectVolume(string? name);
    void RefreshVolumeInspection();
    /// <summary>Supersede detail work and clear raw data, retaining selection.</summary>
    void ClearVolumeInspection();
}
