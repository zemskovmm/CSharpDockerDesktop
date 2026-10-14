using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

// Connection coordination belongs here when Engine integration is implemented.
// Opening the foundation shell performs no Engine I/O.
public sealed class DesktopSession
{
    public EngineConnectionState EngineState => EngineConnectionState.NotConnected;
}
