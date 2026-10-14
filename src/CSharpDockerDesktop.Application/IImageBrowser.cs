using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>In-memory state for one retained negotiated connection. Ready with no images is a successful empty list.</summary>
public sealed record ImageBrowsingSnapshot(long ConnectionGeneration, ResourceReadState State,
    IReadOnlyList<ImageSummary> Images, string? SelectedImageId = null,
    ResourceReadState InspectionState = ResourceReadState.Unavailable, ImageInspection? Inspection = null,
    EngineResourceFailure? Failure = null, EngineResourceFailure? InspectionFailure = null);

/// <summary>Read-only image browsing. The session owns work; the reader borrows its retained connection.</summary>
public interface IImageBrowser
{
    ImageBrowsingSnapshot ImageSnapshot { get; }
    /// <summary>Read the latest immutable snapshot. Notification may originate off the UI thread.</summary>
    event EventHandler? ImagesChanged;
    /// <summary>Coalesces and supersedes pending refreshes. Image list and inspection requests are serialized.</summary>
    void RefreshImages();
    /// <summary>Select a complete authoritative ID from the current rows; null or an absent ID clears selection.</summary>
    void SelectImage(string? imageId);
    void RefreshImageInspection();
    /// <summary>Cancels pending detail work and clears inspection while retaining a still-valid selection.</summary>
    void ClearImageInspection();
}
