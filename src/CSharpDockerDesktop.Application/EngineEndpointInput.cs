using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Describes explicit input without discovering, normalizing or validating a transport.
/// Infrastructure remains responsible for accepting platform-local transports and classifying other addresses.</summary>
public static class EngineEndpointInput
{
    public static EngineEndpoint FromAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new(address, address.StartsWith("npipe:", StringComparison.OrdinalIgnoreCase)
            ? EngineTransport.WindowsNamedPipe : address.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
                ? EngineTransport.UnixSocket : EngineTransport.Unsupported);
    }
}
