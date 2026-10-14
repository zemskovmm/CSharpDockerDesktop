# Read-only container acquisition

Application exposes `IContainerReader`. The negotiated Docker `IEngineConnection` implements this borrowed capability; Application coordination can obtain it with `connection is IContainerReader reader`. The owner of the connection remains responsible for replacement and disposal. The reader has no independent transport, disposal, Uno, or HTTP API. DesktopSession integration, resource tables, lifecycle mutations and event synchronization remain pending.

`ListContainersAsync` sends `GET /v<negotiated>/containers/json?all=true`, including stopped containers. `InspectContainerAsync(identity)` sends `GET /v<negotiated>/containers/<escaped identity>/json`. IDs/names are encoded as one path segment; blank, excessive and dot-segment identities are rejected without I/O. Returned IDs are preserved in full rather than shortened. Read operations perform no mutations and no automatic retry.

Domain projections retain actual state/status strings, names, image references/IDs, ports, creation time, mounts and per-network relationships. Inspection also provides actual runtime flags, exit information, start/finish timestamps and health. Structured list health is consumed when supplied (introduced in API 1.52). Missing/null optional fields and collections remain unknown; empty collections remain empty. Future state strings and additive JSON fields are accepted. Incompatible known field types, invalid timestamps and invalid identities in responses are malformed data. Docker's zero start/finish time means never started/finished and projects to null. Projected timestamps have .NET's 100-nanosecond precision.

`ContainerInspection.RawJson` independently retains the complete actual UTF-8 response, including formatting, nanosecond timestamps, unknown properties, environment values and other sensitive content. It is not reserialized from projected fields or redacted. No response, identity, environment, mount path or raw inspect is automatically logged. Callers must keep deliberate display/copy/export separate from ordinary diagnostics and telemetry.

## Failure and lifetime contract

Results contain either a value or an `EngineResourceFailure`, with available HTTP status and the daemon's original JSON `message` or plain error text. Routine descriptions exclude that potentially sensitive message.

| Kind | Meaning |
| --- | --- |
| NotFound | HTTP 404: container disappeared or identity not found. |
| Conflict | HTTP 409 (or unexpected 304): state/dependency conflict. |
| InvalidRequest | Other 4xx: invalid/incompatible request, including API rejection. |
| AccessDenied | HTTP 401/403 or operating-system access denial, including wrapped denial. |
| DaemonFailure | Other non-success HTTP status, including 5xx. |
| MalformedData | Invalid JSON/consumed schema or successful body exceeding the byte limit. |
| TransportLoss | Interrupted transport, short-operation deadline, disposed connection or reopened pipe. |

Caller cancellation throws `OperationCanceledException`, even when HTTP wraps the cancellation; it is normal termination. A call begun after connection disposal throws `ObjectDisposedException`. Disposal cancels in-flight resource reads and they return TransportLoss unless caller cancellation takes precedence. Every request, response and response stream is released on success, failure and cancellation. Borrowed operations never dispose the connection's HTTP client. Connection disposal remains idempotent and solely owned by DesktopSession/the connection owner.

Requests and bounded response reading are asynchronous. JSON projection runs off the caller's thread. The existing `DockerConnectionOptions` governs short reads: initially five-second deadlines and one-MiB bodies, configurable up to sixteen MiB; this is a resource budget, not a latency promise. System.Text.Json also bounds JSON nesting to 64 levels. Oversized daemon errors keep their status/classification without retaining unbounded content. Pipe generation is checked before sending, after headers/body and after projection. A result from a reopened transport is rejected until a fresh connection negotiates current metadata; old readers do not silently retry against the replacement daemon.

## Resource compatibility evidence

Reviewed on 2026-10-14: the official [API 1.40 schema](https://docs.docker.com/reference/api/engine/version/v1.40.yaml), [API 1.56 schema](https://docs.docker.com/reference/api/engine/version/v1.56.yaml), every intermediate minor's versioned schema, and [Docker API history](https://docs.docker.com/reference/api/engine/version-history/). Both resource endpoints and the all-containers flag retain their selected contract across 1.40–1.56. The per-network `NetworkSettings.Networks` map is used across the range; deprecated top-level network addresses removed in 1.52 are not consumed. New fields, including structured summary health, remain optional.

`tests/CSharpDockerDesktop.Connection.Tests/Fixtures/Containers/schema-evidence.json` records the URL and SHA-256 of each of the seventeen reviewed official contracts, selected root fields and nested state/network types. Its thirty-four response fixtures use official endpoint examples where present and selected-field synthetic responses derived from schema examples/types otherwise. These are documentation fixtures, not captured daemon responses. All seventeen versions execute both resource operations through the retained test transport with negotiated URLs, and inspection verifies exact raw JSON preservation. Handshake fixtures alone are not resource compatibility evidence.

The focused suite covers running/stopped/transitional/future states, unknown/empty fields, sensitive and additive inspect content, encoded identities, HTTP errors, malformed and known/unknown-length oversized responses, caller cancellation, wrapped cancellation, body/send deadlines, connection disposal and reopening before/during resource reads. The controlled named-pipe fixture now lists and inspects on the same pipe as handshake/liveness and verifies closure only when the owner disposes the connection.

Run `dotnet run --project tests/CSharpDockerDesktop.Connection.Tests`. Recorded result: **30/30 groups on macOS 26.7.1, .NET SDK 10.0.402**, including all existing coordinator/connection checks. The test-only platform override exercises .NET's Unix named-pipe implementation. Real Windows pipe ACLs, Windows Docker Desktop/WSL2 container reads and restart continuity, alternate daemons, and workload/performance acceptance remain unverified on this Mac host. This boundary establishes only the selected read-only container schema subset, not full Docker API compatibility or desktop resource reconciliation.
