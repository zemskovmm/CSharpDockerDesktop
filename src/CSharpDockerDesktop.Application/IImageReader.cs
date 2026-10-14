using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>A borrowed, read-only capability of a negotiated connection. The connection owner controls lifetime.</summary>
public interface IImageReader
{
    /// <summary>Lists local images, including intermediate images, with available digests. Cancellation throws OperationCanceledException.</summary>
    Task<EngineResourceResult<IReadOnlyList<ImageSummary>>> ListImagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Accepts image IDs, repository names, tags or digest references. Configuration and raw JSON must never be automatically logged.</summary>
    Task<EngineResourceResult<ImageInspection>> InspectImageAsync(string reference, CancellationToken cancellationToken = default);
}
