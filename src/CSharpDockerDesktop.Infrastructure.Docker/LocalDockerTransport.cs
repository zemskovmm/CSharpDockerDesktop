using System.Net.Sockets;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>Platform default only; does not discover contexts or change daemon configuration.</summary>
public static class LocalDockerTransportDefault
{
    public static EngineEndpoint Endpoint => OperatingSystem.IsMacOS()
        ? new("unix://" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock"), EngineTransport.UnixSocket)
        : EngineEndpoint.LocalWindows;
}

internal sealed class LocalDockerTransport : IDockerTransportFactory, IDockerDuplexTransportFactory
{
    internal static EngineEndpoint DefaultEndpoint => LocalDockerTransportDefault.Endpoint;
    internal static void Validate(EngineEndpoint endpoint)
    {
        if (endpoint.Transport == EngineTransport.UnixSocket) UnixSocketTransport.GetPath(endpoint);
        else NamedPipeTransport.GetLocalPipeName(endpoint);
    }
    private static IDockerTransportFactory For(EngineEndpoint endpoint) => endpoint.Transport == EngineTransport.UnixSocket
        ? new UnixSocketTransport() : new NamedPipeTransport();
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options) => For(endpoint).Create(endpoint, options);
    public ValueTask<Stream> OpenDuplexAsync(EngineEndpoint endpoint, DockerConnectionOptions options, CancellationToken token)
        => ((IDockerDuplexTransportFactory)For(endpoint)).OpenDuplexAsync(endpoint, options, token);
}

internal sealed class UnixSocketTransport : IDockerTransportFactory, IDockerDuplexTransportFactory
{
    internal static string GetPath(EngineEndpoint endpoint)
    {
        const string prefix = "unix:///";
        if (endpoint.Transport != EngineTransport.UnixSocket || endpoint.Address is null
            || !endpoint.Address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw Unsupported("Use a local Unix socket in the form unix:///absolute/path/docker.sock.");
        // Keep the exact filesystem spelling. URI decoding or normalization could select a different socket.
        var path = endpoint.Address[7..];
        if (path.Length <= 1 || path.StartsWith("//", StringComparison.Ordinal) || path.Any(char.IsControl)
            || path.Contains('?') || path.Contains('#'))
            throw Unsupported("The Unix socket requires an absolute local path without control characters, query or fragment.");
        try { _ = new UnixDomainSocketEndPoint(path); }
        catch (ArgumentException) { throw Unsupported("The Unix socket path exceeds the platform socket path limit or is invalid."); }
        return path;
    }
    private static ConnectionProtocolException Unsupported(string message) => new(new(EngineConnectionFailureKind.UnsupportedEndpoint, message));
    private static void RequirePlatform()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw Unsupported("Local Unix sockets require a Unix host. Windows cannot directly use WSL-only sockets.");
    }
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
    {
        RequirePlatform();
        GetPath(endpoint);
        var counter = new PipeConnectionCounter();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 1,
            ConnectCallback = async (_, token) =>
            {
                counter.EnsureCanOpen();
                var stream = await OpenDuplexAsync(endpoint, options, token).ConfigureAwait(false);
                try { counter.OpenTransport(); return stream; }
                catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
            }
        };
        return new ContinuityHandler(handler, counter);
    }
    public async ValueTask<Stream> OpenDuplexAsync(EngineEndpoint endpoint, DockerConnectionOptions options, CancellationToken token)
    {
        RequirePlatform();
        var path = GetPath(endpoint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.PipeConnectTimeout);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            socket.Dispose();
            throw new ConnectionProtocolException(new(EngineConnectionFailureKind.TransientTransportFailure, "The Docker socket connection exceeded its deadline."));
        }
        catch { socket.Dispose(); throw; }
    }
}
