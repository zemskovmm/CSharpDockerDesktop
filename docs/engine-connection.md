# Engine connection boundary

Application exposes `IEngineConnectionClient`, `IEngineConnection` and `EngineConnectionAttempt`. Domain owns endpoint identity, metadata, capability observations, API compatibility and failure kinds. Docker Infrastructure implements HTTP, JSON decoding and pipe transport. Presentation contains no Docker protocol code. The desktop App composition root supplies this client to Application-owned DesktopSession. Presentation observes immutable connection snapshots and queues updates to the UI thread; connection work never waits on the UI dispatcher.

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

Each short request has a configurable five-second deadline, each pipe connect a two-second availability deadline, and every response a one-MiB byte limit. These are bounded initial implementation defaults, not performance promises or future streaming lifetimes. Cancellation covers send, connect and response reading. The Application coordinator supplies recovery and lifetime management described below. Read-only container acquisition is available through the [borrowed Application reader](container-acquisition.md); event streams and desktop resource coordination remain pending.

## Desktop connection workflow

At launch DesktopSession.Start() opens the default local Windows pipe asynchronously. The selection is labeled **Default local Windows pipe (no Docker CLI context selected)**. It is not an invented Docker CLI context. Connecting, Connected, Unavailable, Connection lost and Unsupported remain visible above resource navigation. Details show the actual endpoint, transport, version, daemon API range, negotiated API, operating systems, host/runtime architecture and nullable capabilities. Absent values display unknown. Daemon messages remain separate from ordinary diagnostics; the user explicitly expands details and chooses **Show daemon message** to see them. No automatic daemon-response logging is introduced.

A successful session checks GET /_ping on the retained HTTP transport. Healthy checks do not fetch version/info or replace the connection. The pipe transport tracks each successful pipe open: if HTTP pooling transparently reopens a broken pipe, the old metadata is invalidated and the coordinator performs a fresh readiness/version/negotiated-info handshake. Reopening during the handshake also rejects potentially mixed metadata. Liveness is read-only, cancelable, deadline-bound and response-size-bound, using the same classified failure contract as connection setup.

Missing endpoints, transient transport failures and daemon HTTP errors recover automatically while the GUI remains open. Recovery is bounded in bursts: initially six retries at three-second delays, then a thirty-second cooldown before the next burst. Healthy liveness checks initially wait ten seconds. Application operations have a twenty-second deadline in addition to the Infrastructure short-request deadlines. These are configurable implementation defaults in ConnectionRecoveryOptions, not accepted product performance requirements. Tests inject a controlled delay to advance scheduling without sleeping through the production intervals. Access denial, unsupported endpoint/platform, incompatible API and malformed responses preserve their distinct diagnostics and wait for manual retry rather than hammering the Engine. A zero retry budget disables automatic recovery for controlled tests or explicit composition choices.

**Retry connection** is a normal keyboard-accessible button. It cancels the current attempt, health check or recovery wait, then starts one new serialized workflow. Rapid retries supersede queued work; they never overlap active attempts or health checks. Every returned successful connection is owned even when cancellation arrives just before success. Superseded or expired results are disposed and never published. Closing the window cancels pending I/O and waits, detaches UI notifications and disposes the retained connection. Shutdown and disposal are idempotent. Cooperative cancellation is required by the client contract; a client that ignores cancellation is awaited for safe disposal rather than overlapped with another handshake.

Recovery publishes newly negotiated metadata. Desktop resource snapshots, event reconciliation and mutations remain pending; Connected indicates readiness/metadata only. Resource pages explicitly say loading is pending and update their empty-state explanation with live connectivity. The separately verified container acquisition boundary does not establish IDD-0006 resource reconciliation or complete Engine compatibility.

## Desktop runtime evidence

The connection suite passes **24/24 groups on macOS 26.7.1 with .NET SDK 10.0.402**. Deterministic coordinator checks cover initial failure/recovery, healthy retention, loss/fresh metadata, every failure kind, retry bursts/cooldown, overlapping retry cancellation, late/expired result disposal, and shutdown during connection, liveness and recovery. Liveness checks cover actual retained named-pipe HTTP requests as well as controlled malformed/daemon/transport errors, deadlines and cancellation. Transport-generation fixtures cover reopen before/during ping and during handshake. The solution builds with zero warnings and errors.

The AppKit desktop process was launched for a local interaction check, but native UI inspection reported that the Mac was locked and automatic unlock failed. Startup/navigation/status/retry could not be visually or interactively verified in this session. Windows Win32 desktop startup, named-pipe ACLs, Docker Desktop/WSL2 restart recovery and real daemon compatibility also remain unverified on the Mac development host. Follow the smoke procedure below and record real Windows evidence before claiming those results.

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

Recorded evidence: **24/24 test groups passed on macOS 26.7.1 with .NET SDK 10.0.402**. The internal test-only platform override permits .NET's Unix implementation of named pipes for this controlled fixture; production remains Windows-only. This proves the HTTP callback path and ownership on this host. It does not prove Windows named-pipe ACLs or real Docker Desktop compatibility. No real-Windows, WSL2-backed Docker Desktop or alternate-daemon run was available for this change.

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

   Expect exit 2, `MissingEndpoint`, and bounded completion. Also stop the Engine and rerun the default smoke probe, then start it and run a fresh probe to verify it can establish a new connection. Also launch the desktop before starting the Engine, navigate all four pages during retries, and start it while the GUI stays open. Expect automatic Connected status with fresh metadata. Stop/restart it again and expect Connection lost followed by fresh negotiation without a GUI restart. Keep the Engine down longer than a retry burst to check continuing recovery after cooldown.

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
