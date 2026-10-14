namespace CSharpDockerDesktop.Domain;

/// <summary>Nullable fields and collections are unknown when omitted, rather than empty or false.</summary>
public sealed record ContainerSummary(string Id, IReadOnlyList<string>? Names, string? Image,
    string? ImageId, string? State, string? Status, DateTimeOffset? Created,
    IReadOnlyList<ContainerPort>? Ports, IReadOnlyList<ContainerMount>? Mounts,
    IReadOnlyList<ContainerNetwork>? Networks, ContainerHealth? Health);

public sealed record ContainerPort(int? PrivatePort, string? Protocol, string? HostIP, int? PublicPort);
public sealed record ContainerMount(string? Type, string? Name, string? Source, string? Destination,
    string? Driver, string? Mode, bool? ReadWrite, string? Propagation);
public sealed record ContainerNetwork(string Name, string? NetworkId, string? EndpointId,
    string? IPAddress, int? IPPrefixLength, string? Gateway, string? GlobalIPv6Address,
    int? GlobalIPv6PrefixLength, string? IPv6Gateway, string? MacAddress, IReadOnlyList<string>? Aliases);
public sealed record ContainerHealth(string? Status, long? FailingStreak);
public sealed record ContainerRuntimeState(string? Status, bool? Running, bool? Paused,
    bool? Restarting, bool? Dead, long? ExitCode, string? Error, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, ContainerHealth? Health, bool? OOMKilled = null);

/// <summary>RawJson is the actual UTF-8 inspect response, independently retained with unknown and sensitive fields.</summary>
public sealed record ContainerInspection(string Id, string? Name, string? Image, string? ImageId,
    DateTimeOffset? Created, ContainerRuntimeState? State, IReadOnlyList<ContainerPort>? Ports,
    IReadOnlyList<ContainerMount>? Mounts, IReadOnlyList<ContainerNetwork>? Networks, string RawJson,
    long? RestartCount = null);

public enum EngineResourceFailureKind
{
    NotFound, Conflict, InvalidRequest, DaemonFailure, MalformedData, AccessDenied, TransportLoss
}

/// <summary>DaemonMessage is original user-controlled content and must not be automatically logged.</summary>
public sealed record EngineResourceFailure(EngineResourceFailureKind Kind, string Description,
    int? HttpStatusCode = null, string? DaemonMessage = null);

public sealed class EngineResourceResult<T> where T : class
{
    private EngineResourceResult(T? value, EngineResourceFailure? failure) => (Value, Failure) = (value, failure);
    public T? Value { get; }
    public EngineResourceFailure? Failure { get; }
    public bool IsSuccess => Value is not null;
    public static EngineResourceResult<T> Success(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);
    public static EngineResourceResult<T> Failed(EngineResourceFailure failure) => new(null, failure ?? throw new ArgumentNullException(nameof(failure)));
}
