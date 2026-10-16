using System.IO.Pipes;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal interface IDockerTransportFactory
{
    HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options);
}

internal interface IDockerDuplexTransportFactory
{
    ValueTask<Stream> OpenDuplexAsync(EngineEndpoint endpoint, DockerConnectionOptions options, CancellationToken token);
}

internal interface ITransportContinuity
{
    long ConnectionGeneration { get; }
    event Action? Reopened;
    // Production transport refuses replacement sockets once the metadata has been negotiated.
    // Default supports controlled protocol-only fixtures that do not create physical sockets.
    bool Seal(long generation) => ConnectionGeneration == generation;
}

internal sealed class PipeConnectionCounter
{
    private long generation;
    private bool sealedConnection;
    private readonly object gate = new();
    public bool Seal(long expected)
    {
        lock (gate)
        {
            if (generation != expected) return false;
            sealedConnection = true;
            return true;
        }
    }
    public void EnsureCanOpen()
    {
        lock (gate)
            if (sealedConnection)
            {
                // Invalidate observers even though the replacement is refused before it can carry requests.
                Opened();
                throw new ConnectionProtocolException(new(EngineConnectionFailureKind.TransientTransportFailure,
                    "The negotiated transport cannot reopen. Establish a new negotiated connection."));
            }
    }
    public void OpenTransport()
    {
        lock (gate)
        {
            EnsureCanOpen();
            Opened();
        }
    }
    public long Generation => Interlocked.Read(ref generation);
    public event Action? Reopened;
    public void Opened()
    {
        if (Interlocked.Increment(ref generation) > 1) Reopened?.Invoke();
    }
}

internal sealed class ContinuityHandler(HttpMessageHandler inner, PipeConnectionCounter counter)
    : DelegatingHandler(inner), ITransportContinuity
{
    public long ConnectionGeneration => counter.Generation;
    public bool Seal(long generation) => counter.Seal(generation);
    public event Action? Reopened
    {
        add => counter.Reopened += value;
        remove => counter.Reopened -= value;
    }
}

internal sealed class NamedPipeTransport(bool permitTestHost = false) : IDockerTransportFactory, IDockerDuplexTransportFactory
{
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
    {
        if (!OperatingSystem.IsWindows() && !permitTestHost)
            throw new ConnectionProtocolException(new(EngineConnectionFailureKind.UnsupportedEndpoint,
                "The local Docker named-pipe transport requires Windows. WSL Unix sockets cannot be used directly from this client."));
        var name = GetLocalPipeName(endpoint);
        var counter = new PipeConnectionCounter();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            // Ordinary and event pools each own one connection. Independent pools negotiate
            // independently; a replacement within either pool invalidates its metadata.
            MaxConnectionsPerServer = 1,
            ConnectCallback = async (_, cancellationToken) =>
            {
                counter.EnsureCanOpen();
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync((int)Math.Ceiling(options.PipeConnectTimeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
                    counter.OpenTransport();
                    return pipe; // SocketsHttpHandler now owns the stream.
                }
                catch (TimeoutException)
                {
                    pipe.Dispose();
                    throw new ConnectionProtocolException(new(EngineConnectionFailureKind.MissingEndpoint,
                        "The local Docker pipe was not available within the connection deadline. Start the Engine or check the selected pipe."));
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            }
        };
        return new ContinuityHandler(handler, counter);
    }

    public async ValueTask<Stream> OpenDuplexAsync(EngineEndpoint endpoint, DockerConnectionOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() && !permitTestHost)
            throw new ConnectionProtocolException(new(EngineConnectionFailureKind.UnsupportedEndpoint, "Docker named pipes require Windows."));
        var pipe = new NamedPipeClientStream(".", GetLocalPipeName(endpoint), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)Math.Ceiling(options.PipeConnectTimeout.TotalMilliseconds), token).ConfigureAwait(false);
            return pipe;
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static string GetLocalPipeName(EngineEndpoint endpoint)
    {
        const string prefix = "npipe://./pipe/";
        if (endpoint.Transport != EngineTransport.WindowsNamedPipe || endpoint.Address is null
            || !endpoint.Address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ConnectionProtocolException(new(EngineConnectionFailureKind.UnsupportedEndpoint,
                "Use a local Windows named pipe (npipe://./pipe/<name>) or a macOS Unix socket (unix:///absolute/path). TCP, SSH and WSL bridges are unavailable."));
        var name = endpoint.Address[prefix.Length..];
        if (name.Length == 0 || name.Length > 200 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new ConnectionProtocolException(new(EngineConnectionFailureKind.UnsupportedEndpoint, "The local named-pipe name is invalid."));
        return name;
    }
}

internal sealed class ConnectionProtocolException(EngineConnectionFailure failure) : Exception(failure.Description)
{
    public EngineConnectionFailure Failure { get; } = failure;
}
