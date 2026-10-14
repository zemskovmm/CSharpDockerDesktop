using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed mutation capability of a negotiated connection; its owner controls lifetime.</summary>
public interface IImageCommands
{
    /// <summary>Non-force removal of one image ID or repository reference. Daemon-default parent pruning applies.
    /// Removal records do not prove the requested identity is absent; reconcile every possibly dispatched call.</summary>
    /// <remarks>Caller cancellation throws ImageCommandCanceledException with available dispatch/response evidence.</remarks>
    Task<ImageCommandResult> DeleteImageAsync(string reference, CancellationToken cancellationToken = default);
}
