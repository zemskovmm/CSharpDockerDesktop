using System.Net.Sockets;
using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;
using CSharpDockerDesktop.Presentation;

internal static class UnixSocketChecks
{
    public static async Task LocalTransport()
    {
        if (OperatingSystem.IsWindows()) return;
        // A short path also fits macOS's sockaddr_un limit.
        var path = "/tmp/cdd-" + Guid.NewGuid().ToString("N")[..12] + ".sock";
        var endpoint = EngineEndpointInput.FromAddress("unix://" + path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(2);
        try
        {
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptAsync(deadline.Token);
                using var stream = new NetworkStream(accepted);
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string[] paths = ["/_ping", "/version", "/v1.40/info", "/v1.40/containers/json?all=true"];
                string[] bodies = ["OK", "{\"Version\":\"fixture\",\"ApiVersion\":\"1.40\",\"MinAPIVersion\":\"1.40\"}", "{}", "[]"];
                for (int i = 0; i < paths.Length; i++)
                {
                    var line = await reader.ReadLineAsync(deadline.Token);
                    Check(line == $"GET {paths[i]} HTTP/1.1", line ?? "No request");
                    while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                    var body = bodies[i];
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}"), deadline.Token);
                }
            });
            var result = await new DockerEngineConnectionClient().ConnectAsync(endpoint, deadline.Token);
            Check(result.IsConnected, result.Failure?.Description ?? "No connection");
            await using (var connection = result.Connection!)
            {
                Check(connection.Metadata.Endpoint == endpoint, "Exact endpoint retained");
                var containers = await ((IContainerReader)connection).ListContainersAsync(deadline.Token);
                Check(containers.IsSuccess, "Unix socket resource request");
            }
            await server;
            // Exec uses this owned bidirectional stream independently from the HTTP pool.
            var accept = listener.AcceptAsync(deadline.Token).AsTask();
            await using var duplex = await new UnixSocketTransport().OpenDuplexAsync(endpoint, new(), deadline.Token);
            using var peer = await accept;
            await duplex.WriteAsync(new byte[] { 0, 3, 255 }, deadline.Token);
            var received = new byte[3];
            using var peerStream = new NetworkStream(peer);
            await peerStream.ReadExactlyAsync(received, deadline.Token);
            Check(received.SequenceEqual(new byte[] { 0, 3, 255 }), "Raw input");
            await peerStream.WriteAsync(new byte[] { 27, 91, 65 }, deadline.Token);
            await duplex.ReadExactlyAsync(received, deadline.Token);
            Check(received.SequenceEqual(new byte[] { 27, 91, 65 }), "Raw output");
        }
        finally { listener.Dispose(); File.Delete(path); }
        var missing = await new DockerEngineConnectionClient().ConnectAsync(endpoint, deadline.Token);
        Check(missing.Failure?.Kind == EngineConnectionFailureKind.MissingEndpoint, "Missing socket classification");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await new DockerEngineConnectionClient().ConnectAsync(endpoint, canceled.Token); throw new Exception("Cancellation missing"); }
        catch (OperationCanceledException) { }
        foreach (var address in new[] { "unix://remote/socket", "unix:relative", "unix:///tmp/x?query", "unix:///tmp/x\n", "unix:///" })
        {
            var rejected = await new DockerEngineConnectionClient().ConnectAsync(EngineEndpointInput.FromAddress(address));
            Check(rejected.Failure?.Kind == EngineConnectionFailureKind.UnsupportedEndpoint, "Malformed socket accepted");
        }
        if (OperatingSystem.IsMacOS())
        {
            Check(LocalDockerTransportDefault.Endpoint.Transport == EngineTransport.UnixSocket, "Mac default transport");
            await using var session = new DesktopSession(new DockerEngineConnectionClient(), defaultEndpoint: LocalDockerTransportDefault.Endpoint);
            var editor = new ConnectionEditorView(session);
            editor.Draft = "unix:///tmp/other.sock";
            editor.UseDefault();
            Check(editor.Draft == LocalDockerTransportDefault.Endpoint.Address && editor.Selection.Contains("macOS"), "Default editor selection");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
