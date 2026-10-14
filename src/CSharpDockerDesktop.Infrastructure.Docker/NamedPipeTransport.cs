using System.IO.Pipes;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal interface IDockerTransportFactory
{
    HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options);
}

internal interface ITransportContinuity
{
    long ConnectionGeneration { get; }
}

internal sealed class PipeConnectionCounter
{
    private long generation;
    public long Generation => Interlocked.Read(ref generation);
    public void Opened() => Interlocked.Increment(ref generation);
}

internal sealed class ContinuityHandler(HttpMessageHandler inner, PipeConnectionCounter counter)
    : DelegatingHandler(inner), ITransportContinuity
{
    public long ConnectionGeneration => counter.Generation;
}

internal sealed class NamedPipeTransport(bool permitTestHost = false) : IDockerTransportFactory
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
            MaxConnectionsPerServer = 1,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync((int)Math.Ceiling(options.PipeConnectTimeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
                    counter.Opened();
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

    internal static string GetLocalPipeName(EngineEndpoint endpoint)
    {
        const string prefix = "npipe://./pipe/";
        if (endpoint.Transport != EngineTransport.WindowsNamedPipe || endpoint.Address is null
            || !endpoint.Address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ConnectionProtocolException(new(EngineConnectionFailureKind.UnsupportedEndpoint,
                "Only local Windows named-pipe endpoints in the form npipe://./pipe/<name> are supported. TCP, SSH, Unix sockets and WSL bridges are unavailable."));
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
