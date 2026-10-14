# Engine connection boundary

Application exposes `IEngineConnectionClient`, `IEngineConnection` and `EngineConnectionAttempt`. Domain owns endpoint identity, metadata, capability observations, API compatibility and failure kinds. Docker Infrastructure implements HTTP, JSON decoding and pipe transport. Presentation contains no Docker protocol code. The current desktop session remains disconnected until a later coordinator composes this client.

```csharp
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Infrastructure.Docker;

IEngineConnectionClient client = new DockerEngineConnectionClient();
var attempt = await client.ConnectAsync(cancellationToken: shutdownToken);
if (attempt.Connection is { } connection)
{
    await using (connection)
    {
        var metadata = connection.Metadata;
        // Retain the connection for the session's lifetime in a real coordinator.
    }
}
else
{
    var failure = attempt.Failure!;
    // Present Kind/Description. DaemonMessage is untrusted content for deliberate display,
    // not automatic logging or telemetry. Cancellation throws OperationCanceledException.
}
```

## Endpoint and ownership

A null endpoint selects `EngineEndpoint.LocalWindows`, address `npipe://./pipe/docker_engine`, without invoking or locating Docker CLI. Explicit endpoints must be `EngineTransport.WindowsNamedPipe` and local `npipe://./pipe/<name>` addresses. Remote pipe authorities, invalid pipe names, TCP/TLS, SSH, Unix sockets and WSL bridges are rejected. Non-Windows production callers receive `UnsupportedEndpoint`; a WSL Unix path is never treated as a Windows connection. Alternate pipes can be selected explicitly, but no alternate Docker-compatible daemon is certified by these fixtures.

`SocketsHttpHandler.ConnectCallback` opens a local asynchronous `NamedPipeClientStream`; `http://docker/` is only the logical HTTP authority and cannot cause a network connection. Proxies and redirects are disabled. A successful connection retains its handler and pipe for later session reuse. `DisposeAsync` closes it idempotently. Failed/canceled attempts dispose their `HttpClient` and handler; failed/canceled pipe connects dispose the attempted stream. Every request and response body is disposed. A client instance holds no active connections of its own, so the caller owns every returned successful session independently.

Each short request has a configurable five-second deadline, each pipe connect a two-second availability deadline, and every response a one-MiB byte limit. These are bounded initial implementation defaults, not performance promises or future streaming lifetimes. Cancellation covers send, connect and response reading. There are no automatic retries, reconnect coordination, event streams or resource operations.

## Handshake and compatibility evidence

The handshake is `GET /_ping` (requires `OK`), then unversioned `GET /version` for discovery, then `GET /v<negotiated>/info`. The client requires nonempty `Version`, numeric `ApiVersion` and numeric `MinAPIVersion` to establish actual identity and a valid daemon range. A missing range is malformed, rather than an invented minimum. Other consumed metadata is optional, and absent or JSON-null values remain null. Unrecognized additive fields are ignored. Known fields with incompatible JSON types produce `MalformedResponse`.

Client minimum is **1.40** and maximum is **1.56** for the implemented handshake schemas. Choose the highest version in the client/daemon range overlap. No versioned request occurs if the ranges do not overlap. Future resource schemas must establish their own compatibility evidence before depending on this maximum.

Reviewed official sources on 2026-10-14:

- [Docker API 1.40 OpenAPI schema](https://docs.docker.com/reference/api/engine/version/v1.40.yaml): baseline SystemVersion/SystemInfo fields, readiness and daemon error shape.
- [Docker API 1.55 OpenAPI schema](https://docs.docker.com/reference/api/engine/version/v1.55.yaml) and [1.56 OpenAPI schema](https://docs.docker.com/reference/api/engine/version/v1.56.yaml): consumed discovery/info fields retain their string/boolean/object shapes. Their schema difference introduces no handshake change.
- [Official API history](https://docs.docker.com/reference/api/engine/version-history/): additions to info (for example NRI, Containerd and DiscoveredDevices) do not affect selected fields. Removed KernelMemoryTCP and BridgeNf fields are not consumed. The [API matrix](https://docs.docker.com/reference/api/engine/) lists released Engine ranges with a 1.44 minimum, covered by tests.

Focused fixture evidence verifies baseline 1.40 metadata, a synthetic 1.56 selected-field response with current additive fields, every negotiated minor from 1.40 through 1.56, newer ranges clamped at 1.56, and contemporary ranges 1.44–1.52 and 1.44–1.53. Fixtures are schema-reviewed synthetic responses, not captured Docker output. Runtime architecture (`/version` Arch) remains distinct from host architecture (`/info` Architecture); no equality or replacement is assumed. Capabilities are nullable observed limit/experimental/Swarm fields, not an invented list of supported resource operations.

These checks support a narrowly implemented handshake maximum. They do not certify real Windows Engines, complete Docker compatibility, or future resource APIs.

## Failure contract

| Failure kind | Meaning |
| --- | --- |
| MissingEndpoint | Missing pipe or no available pipe instance within the connect deadline; a busy pipe can also reach this deadline. |
| AccessDenied | Pipe access was denied by the operating system. No elevation or ACL change is attempted. |
| UnsupportedEndpoint | Unsupported transport, malformed local pipe address, remote pipe authority or non-Windows platform. |
| IncompatibleApi | No overlap between observed daemon range and 1.40–1.56; info is not requested. |
| DaemonHttpError | Daemon returned non-success HTTP status; status and bounded original JSON `message`/plain-text error are retained separately. An oversized error retains status without body. |
| MalformedResponse | Invalid readiness/JSON/known field types/API range or a successful response exceeds the size limit. |
| TransientTransportFailure | Interrupted HTTP/pipe I/O, request deadline or transport unavailability after connection. |

HTTP 403 is a daemon HTTP failure; pipe ACL denial is `AccessDenied`. A generic network exception's potentially sensitive text is not copied into routine diagnostics. There is no automatic response logging. Caller cancellation throws `OperationCanceledException` rather than reporting a failure.

## Independent automated checks

```sh
dotnet run --project tests/CSharpDockerDesktop.Connection.Tests
```

The dependency-free .NET 10 executable exits nonzero on failures and needs neither Uno nor Docker. Coverage includes negotiation/request paths, incompatible ranges, absent/additive fields, HTTP errors at every handshake stage, malformed/oversized bodies with known and unknown lengths, cancellation at every stage and during response reading, deadlines, failure classification and response/handler disposal. A real controlled named-pipe server verifies HTTP request framing, negotiated info path and pipe closure on successful session disposal and failed/canceled attempts. Missing/canceled pipe connection checks use the same production transport primitives.

Recorded evidence: **15/15 test groups passed on macOS 26.7.1 with .NET SDK 10.0.402**. The internal test-only platform override permits .NET's Unix implementation of named pipes for this controlled fixture; production remains Windows-only. This proves the HTTP callback path and ownership on this host. It does not prove Windows named-pipe ACLs or real Docker Desktop compatibility. No real-Windows, WSL2-backed Docker Desktop or alternate-daemon run was available for this change.

## Windows smoke procedure

Use Windows x64, .NET 10 and a normal non-elevated account. Record OS, SDK, Docker/Desktop version, backend, actual endpoint, returned API range/negotiation and outcome. Run the independent checks above first; their controlled pipe group then exercises Win32 pipes on that host.

1. **Reachable:** Start an existing Docker Desktop Engine (including its WSL2 backend) with pipe access already granted to the account. Run:

   ```powershell
   dotnet run --project tests/CSharpDockerDesktop.Connection.Tests -- --smoke
   ```

   Expect exit 0 and actual endpoint/transport, Engine version, daemon range, negotiated version, OS and host architecture. Repeat with Docker CLI absent from PATH; results must remain the same. No Docker command is invoked by this client.

2. **Missing:** Choose a pipe name that is not present:

   ```powershell
   dotnet run --project tests/CSharpDockerDesktop.Connection.Tests -- --smoke npipe://./pipe/cdd-no-engine-smoke
   ```

   Expect exit 2, `MissingEndpoint`, and bounded completion. Also stop the Engine and rerun the default smoke probe, then start it and run a fresh probe to verify it can establish a new connection. This checks separate connection attempts; desktop restart recovery is not yet implemented.

3. **Denied access:** Use a dedicated controlled pipe, leaving real Docker permissions unchanged. In a separate **Windows PowerShell 5.1** window under the same normal account, run this .NET Framework fixture (its ACL APIs differ from PowerShell 7):

   ```powershell
   $server = [System.IO.Pipes.NamedPipeServerStream]::new(
       'cdd-denied-smoke', [System.IO.Pipes.PipeDirection]::InOut, 1,
       [System.IO.Pipes.PipeTransmissionMode]::Byte,
       [System.IO.Pipes.PipeOptions]::Asynchronous)
   try {
       $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
       $acl = $server.GetAccessControl()
       $deny = [System.IO.Pipes.PipeAccessRule]::new($sid,
           [System.IO.Pipes.PipeAccessRights]::ReadWrite,
           [System.Security.AccessControl.AccessControlType]::Deny)
       $acl.AddAccessRule($deny)
       $server.SetAccessControl($acl)
       Read-Host 'Denied fixture is active; press Enter after probing'
   } finally { $server.Dispose() }
   ```

   While the fixture is active, run from the other non-elevated window:

   ```powershell
   dotnet run --project tests/CSharpDockerDesktop.Connection.Tests -- --smoke npipe://./pipe/cdd-denied-smoke
   ```

   Expect exit 2 with `AccessDenied`, distinct from the missing-pipe result. Close the fixture by pressing Enter. The client must neither elevate nor modify permissions. This Windows-only fixture/procedure has not been executed on the current Mac host; record real evidence before claiming Windows permission verification.
