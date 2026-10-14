using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed mutation capability of a negotiated connection; its owner controls lifetime.</summary>
public interface INetworkCommands
{
    /// <summary>Ordinary non-force removal of one complete original network ID.
    /// An acknowledgement does not prove absence; reconcile every possibly dispatched call.</summary>
    /// <remarks>Caller supplies the full original daemon ID, never a name/prefix. Syntax validation cannot prove completeness.
    /// This low-level capability does not establish eligibility, dependency freshness or user confirmation.
    /// Caller cancellation throws NetworkCommandCanceledException with available dispatch/response evidence.</remarks>
    Task<NetworkCommandResult> DeleteNetworkAsync(string id, CancellationToken cancellationToken = default);
}
