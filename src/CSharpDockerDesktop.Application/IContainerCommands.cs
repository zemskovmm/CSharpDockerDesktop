using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed lifecycle capability of a negotiated connection; its owner controls lifetime.
/// No result establishes final state. Reconcile dispatched operations with authoritative reads; never blindly replay.</summary>
public interface IContainerCommands
{
    /// <remarks>Cancellation throws ContainerCommandCanceledException (an OperationCanceledException) with dispatch evidence.</remarks>
    Task<ContainerCommandResult> StartContainerAsync(string identity, CancellationToken cancellationToken = default);
    /// <summary>Null uses the daemon's grace period; a supplied value is a nonnegative number of seconds.</summary>
    Task<ContainerCommandResult> StopContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default);
    Task<ContainerCommandResult> RestartContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default);
    /// <summary>Force requires an explicit caller choice. Both choices preserve volumes; neither silently stops or escalates.</summary>
    Task<ContainerCommandResult> DeleteContainerAsync(string identity, bool force = false, CancellationToken cancellationToken = default);
}
