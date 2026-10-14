using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Opens an explicitly selected endpoint. Null selects the local Windows pipe without CLI discovery.</summary>
public interface IEngineConnectionClient
{
    /// <remarks>Cancellation throws OperationCanceledException; it is not a connection failure.</remarks>
    Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default);
}

/// <summary>The caller owns a successful connection and must dispose it on replacement or shutdown.</summary>
public interface IEngineConnection : IAsyncDisposable
{
    EngineMetadata Metadata { get; }
}

public sealed class EngineConnectionAttempt
{
    private EngineConnectionAttempt(IEngineConnection? connection, EngineConnectionFailure? failure)
        => (Connection, Failure) = (connection, failure);
    public IEngineConnection? Connection { get; }
    public EngineConnectionFailure? Failure { get; }
    public bool IsConnected => Connection is not null;
    public static EngineConnectionAttempt Connected(IEngineConnection connection) => new(connection, null);
    public static EngineConnectionAttempt Failed(EngineConnectionFailure failure) => new(null, failure);
}
