using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed read-only capability of a negotiated connection; independent bounded stream admission.</summary>
public interface IContainerLogSource
{
    /// <summary>Inspect authoritative TTY configuration, then open finite history and optional live follow.</summary>
    /// <remarks>Invalid input fails before transport allocation. Token governs opening and established lifetime.
    /// Opening cancellation throws OperationCanceledException; established cancellation is normal completion.
    /// No automatic reconnect/retry. Caller chooses Since and overlap; infrastructure never deduplicates.</remarks>
    Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Caller owns subscription disposal; connection disposal cancels and joins all producer/transport work.</summary>
public interface IContainerLogSubscription : IAsyncDisposable
{
    string ContainerId { get; }
    bool IsTty { get; }
    ChannelReader<ContainerLogOutput> Output { get; }
    /// <summary>Completes after transport cleanup; read buffered output then inspect termination.</summary>
    /// <remarks>Disposal/cancellation/continuity loss invalidates buffered output. Natural EOF retains it.
    /// Capacity remains reserved until caller/owner disposal, including after natural completion.</remarks>
    Task<ContainerLogCompletion> Completion { get; }
}

public sealed class ContainerLogSubscriptionAttempt
{
    private ContainerLogSubscriptionAttempt(IContainerLogSubscription? subscription, ContainerLogFailure? failure)
        => (Subscription, Failure) = (subscription, failure);
    public IContainerLogSubscription? Subscription { get; }
    public ContainerLogFailure? Failure { get; }
    public bool IsSubscribed => Subscription is not null;
    public static ContainerLogSubscriptionAttempt Subscribed(IContainerLogSubscription subscription) => new(subscription, null);
    public static ContainerLogSubscriptionAttempt Failed(ContainerLogFailure failure) => new(null, failure);
}
