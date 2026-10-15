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

Each short request has a configurable five-second deadline, each pipe connect a two-second availability deadline, and every response a one-MiB byte limit. These are bounded initial implementation defaults, not performance promises or future streaming lifetimes. Cancellation covers send, connect and response reading. The Application coordinator supplies recovery and lifetime management described below. Session-owned acquisition, automatic event reconciliation and resource browsing are available for Containers, Images, Volumes and Networks. The [Application container contracts](container-acquisition.md) describe container acquisition; each resource family has its own browsing and operation contracts.

## Desktop connection workflow

At launch DesktopSession.Start() opens the default local Windows pipe asynchronously. **Select Engine endpoint** contains a bounded address draft, **Apply / Connect** and **Use default local pipe**. Typing changes only the draft. Applying invokes the Application selection contract; the default action resets the draft and explicitly selects the local Windows pipe. Selection remains in memory. Retry always reconnects the actual selection, preserving any unapplied draft. Selecting the already-selected endpoint is a no-op; Retry forces fresh negotiation. No Docker CLI context is invented or discovered.

The visible selected endpoint/transport, Connecting, Connected, Unavailable, Connection lost and Unsupported remain above resource navigation. Details distinguish selected endpoint/transport from the actual established metadata endpoint/transport, which are unavailable until connection succeeds. They also show Engine identity/version, daemon API range, negotiated API, operating systems, host/runtime architecture and nullable capabilities. Absent values display unavailable or unknown. Diagnostics and recovery text name the selected address. Daemon messages remain separate from ordinary diagnostics; the user explicitly expands details and chooses **Show daemon message** to see them. A new connection snapshot hides and clears the previous reveal even when another endpoint or retry returns identical text. There is no automatic daemon-response logging.

A successful session checks GET /_ping on the retained HTTP transport. Healthy checks do not fetch version/info or replace the connection. The pipe transport tracks each successful pipe open: if HTTP pooling transparently reopens a broken pipe, the old metadata is invalidated and the coordinator performs a fresh readiness/version/negotiated-info handshake. Reopening during the handshake also rejects potentially mixed metadata. Liveness is read-only, cancelable, deadline-bound and response-size-bound, using the same classified failure contract as connection setup.

Missing endpoints, transient transport failures and daemon HTTP errors recover automatically while the GUI remains open. Recovery is bounded in bursts: initially six retries at three-second delays, then a thirty-second cooldown before the next burst. Healthy liveness checks initially wait ten seconds. Application operations have a twenty-second deadline in addition to the Infrastructure short-request deadlines. These are configurable implementation defaults in ConnectionRecoveryOptions, not accepted product performance requirements. Tests inject a controlled delay to advance scheduling without sleeping through the production intervals. Access denial, unsupported endpoint/platform, incompatible API and malformed responses preserve their distinct diagnostics and wait for manual retry rather than hammering the Engine. A zero retry budget disables automatic recovery for controlled tests or explicit composition choices.

**Retry connection** is a normal keyboard-accessible button. It cancels the current attempt, health check or recovery wait, then starts one new serialized workflow. Rapid retries supersede queued work; they never overlap active attempts or health checks. Every returned successful connection is owned even when cancellation arrives just before success. Superseded or expired results are disposed and never published. Closing the window cancels pending I/O and waits, detaches UI notifications and disposes the retained connection. Shutdown and disposal are idempotent. Cooperative cancellation is required by the client contract; a client that ignores cancellation is awaited for safe disposal rather than overlapped with another handshake.

Recovery publishes newly negotiated metadata, opens negotiated event observation and starts a fresh authoritative container list through the retained borrowed reader. Container resource or event TransportLoss immediately triggers this recovery workflow; ordinary resource/daemon and classified event-stream failures remain distinguishable. Session-owned reads and observation are canceled and drained before connection disposal, and their snapshots are invalidated on replacement/loss. Connected indicates readiness/metadata; the separate container snapshot reports resource loading/success/failure. The Containers page shows the independent resource loading/error state and read-only Overview while keeping shell navigation and connection recovery available. [Container event reconciliation](container-reconciliation.md) now provides automatic authoritative container updates and bounded observation recovery. Images, Volumes and Networks also obtain fresh authoritative state through the shared session. Containers expose lifecycle actions; Images, Volumes and Networks expose explicit confirmed deletion under their resource-specific safety contracts. These implemented paths and simulated checks do not establish full Engine compatibility.

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


The borrowed event capability uses a separately negotiated, bounded stream transport while preserving ordinary-pool continuity checks. See [event observations and verification](engine-events.md).

## Explicit Application endpoint selection

`DesktopSession.SelectEndpoint(EngineEndpoint)` changes the selected endpoint on the existing session and starts connection work asynchronously. It requires an explicit non-null endpoint. Selection is in memory; there is no environment/context discovery, precedence policy, persistence, added transport, or Engine runtime management. The desktop still starts with the default local Windows named pipe. Presentation now exposes this contract through the explicit endpoint draft and Apply/default actions described above. Address interpretation stays in Application; local-pipe syntax validation and all transport/Docker work stay in Infrastructure.

Selecting a different endpoint synchronously revokes the old generation and clears all four resource snapshots, selections and Inspect details. Volume and Network deletion preparations become invalid immediately, including a preparation whose read ignores cancellation. Logs and Stats view ownership ends and terminal ownership is interrupted. Historical mutation and terminal response evidence retains its original target, endpoint, Engine identity and connection generation, including late protocol responses. Raw daemon content remains deliberate display data, never automatically logged. A matching resource ID or even matching Engine ID at another endpoint cannot reconcile an earlier mutation or authorize a captured deletion confirmation. No mutation, terminal command or input is replayed.

The connection snapshot immediately names the new endpoint in Connecting, without old metadata or diagnostics. Subsequent Unavailable, Unsupported, Connected and recovery states continue to name that captured endpoint. Docker Infrastructure remains responsible for validating it and classifying failures. Unix/WSL-only sockets, remote pipe hosts and unsupported schemes acquire no transport and never fall back to the default pipe.

One active workflow owns connection negotiation, borrowed reads/events/streams and transport cleanup. A single replaceable pending selection retains only the latest request. Rapid endpoint changes and Retry calls do not create a chain of waiting tasks or overlapping handshakes. The old workflow must finish cancellation and join all acquired work before the latest selection can negotiate; controlled noncooperative I/O or cleanup therefore delays replacement without blocking the selecting caller. Each workflow captures its endpoint and generation before running. It cannot connect to a later selection, publish late metadata or schedule recovery for that later selection. Returning to an earlier endpoint negotiates a fresh connection and authoritative snapshots. Endpoint changes reset event replay continuity; old Logs/Stats/terminal views do not reopen automatically.

Selecting the current endpoint is a no-op. `Retry()` forces fresh negotiation of the current selection, preserving existing automatic recovery and same-Engine reconciliation behavior. `DisconnectAsync()` clears pending connection work and joins cleanup owned at that boundary; a subsequent Start/Retry or different selection can reconnect. Concurrent disposal prevents further admission and joins the active workflow and acquired cleanup exactly once. Operations that ignore cancellation are awaited until they finish, never overlapped or abandoned.

Focused simulated checks are available with:

```sh
dotnet run --project tests/CSharpDockerDesktop.Connection.Tests -- --endpoint-selection
```

The fixtures use two explicit named-pipe endpoints with overlapping full Container/Image/Network IDs, volume names and Engine IDs. They exercise 5,000 coalesced selections during a controlled noncooperative handshake, selection during recovery, return negotiation, late resource/event/stream delivery, active deletion preparation, stale confirmation rejection, acknowledged/rejected/uncertain mutation evidence for all four resource families, live Logs/Stats/terminal cleanup, invalid endpoint rejection and concurrent selection/disconnect/disposal. Synchronization uses bounded waits and explicit completion barriers, without timing-dependent sleeps. These are simulated coordination and infrastructure validation results on macOS, not real Windows/Docker or performance evidence. Windows Win32 behavior, pipe ACLs, real Docker Desktop/WSL2 recovery and alternate-daemon compatibility still require the real-host checks above.

Recorded for this Application selection change: **7/7 focused endpoint groups and 405/405 full regression groups passed on macOS 26.7.1** using .NET SDK 10.0.402. The Release solution build completed with zero warnings and errors. No project verification policy is configured; the repository executable checks and solution build supplied the verification evidence.

## Desktop endpoint editor and operation provenance evidence

The address editor is limited to 2,048 characters, has a visible draft label and an accessible name, and requires an explicit ordinary button action. Empty/oversized drafts cannot apply. Unsupported Unix/WSL, remote-pipe, TCP, SSH, malformed pipe and other scheme input remains the selected input and receives Infrastructure's classified explanation without transport allocation or fallback. Presentation neither parses endpoint syntax nor performs Docker I/O. The default local pipe remains the initial selection; no discovery, environment precedence, persistence, new transport or runtime management was added.

The connection region scrolls within a finite height so expanded details or long input cannot consume resource navigation. Endpoint and metadata fields use finite display previews (2,048 characters per field), connection explanations are capped at 4,096 characters, and deliberately revealed daemon text at 16,384 characters with an explicit truncation marker. The retained source evidence is unchanged. Connection notifications coalesce to one queued UI update; a skipped/late notification reads the latest immutable session state. Reveals are owned by snapshot identity, so equal text cannot carry permission to display across selection, retry or recovery. Unloading the shell clears reveal ownership.

Container, Image, Volume and Network operation status now identifies its **originating endpoint**, transport, available Engine identity (explicitly **unavailable** when absent), original resource target and connection generation. A result from an unavailable or different generation is visibly labeled historical. Endpoint provenance remains distinct even when both Engines expose the same full IDs, volume names and Engine IDs. Original command dispositions, observations and deliberately revealed response evidence remain intact; switching the shell selection never issues an old operation again.

On 2026-10-15, **12/12 focused endpoint groups** and **410/410 full regression groups** passed on macOS 26.7.1 using .NET SDK 10.0.402. Five added groups exercise the actual Presentation editor state through controlled Application connections: draft/apply/default/Retry separation and input/display bounds; rapid replacement during deliberately delayed cleanup and late resource delivery; identical daemon text with endpoint/retry reveal ownership; unsupported input without fallback/transport allocation; and bounded historical provenance with unavailable identity. Existing mutation isolation groups additionally attach the real resource Presentation views to the controlled session after switching, verifying retained status for all four resource families without replay. The Release solution and graphical smoke host build with zero warnings/errors; the smoke host publishes self-contained for osx-arm64.

The existing synthetic desktop smoke host now offers **Engine shell fixture**, **Release Engine attempt**, and **Run endpoint shell checks**. Set `CSHARPDOCKER_ENDPOINT_UI_CHECKS=1` when launching it to run actual shell control checks automatically. The local run passed both groups: real TextBox/Apply controls with focus and bounded input, navigation through all four pages while the Application connection is deliberately delayed, real Retry/default controls preserving the draft/selection distinction, and explicit identical-message reveal isolation. These controls use a synthetic connection client and perform no Docker transport or mutations. Programmatic focus and automation-peer invocation are recorded separately from native keyboard evidence.

A separate native AppKit interaction pasted an exact address into the editor, used Tab and Enter to activate Apply, observed visible Apply focus and Connecting text naming `npipe://./pipe/native_keyboard`, then used Tab/Enter to return to the default local pipe. With that connection still deliberately delayed, native Down/Enter moved resource navigation from Images to Volumes and showed the corresponding page with keyboard focus. The AppKit accessibility tree was incomplete after dynamic navigation, so screenshots supplied visual confirmation. Native character synthesis through the local automation tool produced unexpected characters; exact pasted input and Tab/Enter activation were verified independently. Windows Win32 keyboard/screen-reader behavior, DPI/small-window coverage, real Docker Desktop/WSL2/alternate-daemon compatibility, actual Windows pipe ACLs and workstation performance targets remain unverified. The synthetic results do not establish those real-host properties.
