using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed read-only connection capability. Requires the exact inspected container ID, not a name/prefix.
/// Opening validates identity, then requests continuous raw counters. No reconnect, replay or derived measurements.</summary>
public interface IContainerStatsSource
{
    /// <remarks>Token governs opening and established lifetime. Opening cancellation throws; established cancellation
    /// completes normally. Empty/stalled streams produce no samples and do not establish container running state.</remarks>
    Task<ContainerStatsSubscriptionAttempt> SubscribeStatsAsync(string containerId, CancellationToken cancellationToken = default);
}

/// <summary>Caller owns disposal. Disposal cancels and joins the producer and its independent transport.
/// The borrowed selected connection remains owned by its caller; its disposal joins all subscriptions.</summary>
public interface IContainerStatsSubscription : IAsyncDisposable
{
    string ContainerId { get; }
    ChannelReader<ContainerStatsSample> Samples { get; }
    /// <summary>Completes after transport cleanup. Clean EOF retains buffered samples for draining.
    /// Cancellation/disposal/transport replacement suppress buffered delivery, including after completion.
    /// Admission remains reserved until disposal. Inspect this result after reading the channel.</summary>
    Task<ContainerStatsCompletion> Completion { get; }
}

public sealed class ContainerStatsSubscriptionAttempt
{
    private ContainerStatsSubscriptionAttempt(IContainerStatsSubscription? subscription, ContainerStatsFailure? failure)
        => (Subscription, Failure) = (subscription, failure);
    public IContainerStatsSubscription? Subscription { get; }
    public ContainerStatsFailure? Failure { get; }
    public bool IsSubscribed => Subscription is not null;
    public static ContainerStatsSubscriptionAttempt Subscribed(IContainerStatsSubscription subscription) => new(subscription, null);
    public static ContainerStatsSubscriptionAttempt Failed(ContainerStatsFailure failure) => new(null, failure);
}
