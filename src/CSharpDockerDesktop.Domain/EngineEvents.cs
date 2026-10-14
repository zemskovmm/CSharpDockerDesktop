using System.Globalization;

namespace CSharpDockerDesktop.Domain;

/// <summary>Exact daemon timestamps. Neither value is an event ID; duplicates are legitimate observations.</summary>
public sealed record EngineEventTimestamp(long? UnixSeconds, long? UnixNanoseconds);

/// <summary>A synchronization observation, not an authoritative resource patch. Metadata can be sensitive.</summary>
public sealed record EngineEvent(string Type, string Action, string? ResourceId,
    IReadOnlyDictionary<string, string>? ActorAttributes, string? Scope, EngineEventTimestamp Timestamp);

/// <summary>Explicit Unix timestamp cursor; callers choose reconnect overlap. No timestamp-based deduplication.</summary>
public readonly record struct EngineEventCursor(long UnixSeconds, int Nanoseconds = 0)
{
    public override string ToString()
    {
        if (UnixSeconds < 0 || Nanoseconds is < 0 or > 999_999_999)
            throw new ArgumentOutOfRangeException(nameof(EngineEventCursor));
        return string.Create(CultureInfo.InvariantCulture, $"{UnixSeconds}.{Nanoseconds:D9}");
    }
}

public enum EngineEventFailureKind
{
    SubscriptionLimit, OpeningDeadline, AccessDenied, InvalidRequest, DaemonHttpError,
    MalformedEvent, OversizedEvent, TransportLoss, UnexpectedEnd
}

/// <summary>DaemonMessage is original user-controlled content; never automatically log it or event metadata.</summary>
public sealed record EngineEventFailure(EngineEventFailureKind Kind, string Description,
    int? HttpStatusCode = null, string? DaemonMessage = null);
