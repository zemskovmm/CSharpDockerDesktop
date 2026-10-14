using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed mutation capability of a negotiated connection; its owner controls lifetime.</summary>
public interface IVolumeCommands
{
    /// <summary>Ordinary non-force removal of one complete original volume name.
    /// An acknowledgement does not prove absence; reconcile every possibly dispatched call.</summary>
    /// <remarks>Caller cancellation throws VolumeCommandCanceledException with available dispatch/response evidence.</remarks>
    Task<VolumeCommandResult> DeleteVolumeAsync(string name, CancellationToken cancellationToken = default);
}
