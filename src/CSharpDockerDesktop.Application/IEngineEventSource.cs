using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Borrowed capability of a negotiated connection. One live or undisposed subscription per connection.</summary>
public interface IEngineEventSource
{
    /// <summary>Observe containers, images, volumes and networks. Token also controls the established lifetime.</summary>
    /// <remarks>Opening cancellation throws OperationCanceledException. No automatic retry/reopening.</remarks>
    Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Caller disposes subscriptions; connection disposal also cancels and joins their work.</summary>
public interface IEngineEventSubscription : IAsyncDisposable
{
    ChannelReader<EngineEvent> Events { get; }
    /// <summary>Always completes after producer/resource cleanup. Null means normal cancellation/disposal.</summary>
    /// <remarks>Read buffered observations then inspect this result on channel completion. Disposal clears pending delivery.</remarks>
    Task<EngineEventFailure?> Completion { get; }
}

public sealed class EngineEventSubscriptionAttempt
{
    private EngineEventSubscriptionAttempt(IEngineEventSubscription? subscription, EngineEventFailure? failure)
        => (Subscription, Failure) = (subscription, failure);
    public IEngineEventSubscription? Subscription { get; }
    public EngineEventFailure? Failure { get; }
    public bool IsSubscribed => Subscription is not null;
    public static EngineEventSubscriptionAttempt Subscribed(IEngineEventSubscription subscription) => new(subscription, null);
    public static EngineEventSubscriptionAttempt Failed(EngineEventFailure failure) => new(null, failure);
}
