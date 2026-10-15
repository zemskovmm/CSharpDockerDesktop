# Borrowed exec creation and inspection

The retained negotiated Engine connection exposes Application `IContainerExec`. This document covers `CreateTtyExecAsync` and `InspectExecAsync`; [attached TTY start](../src/CSharpDockerDesktop.Infrastructure.Docker/EXEC-ATTACHMENT.md) and [borrowed TTY resize](container-exec-resize.md) have separate protocol and ownership contracts. These capabilities are independent of Uno, terminal rendering and Application session coordination. Their simulated evidence does not establish interactive terminal conformance.

## Explicit requests and identity

Creation requires the full container ID previously obtained from the selected Engine and an explicit argument vector. There is no shell default, joining/splitting of arguments, command substitution, command-selection policy or implicit container operation. The first argument must be nonempty; subsequent empty strings, spaces, Unicode, quotes, backslashes and non-NUL control characters remain literal arguments. Invalid Unicode and NUL are rejected. The capability snapshots the bounded vector before asynchronous I/O, so later changes to a caller's list cannot change the submitted command.

`POST /v{negotiated}/containers/{containerId}/exec` serializes exactly `AttachStdin=true`, `AttachStdout=true`, `AttachStderr=true`, `Tty=true` and `Cmd=[...]`. It omits privileged/user/environment/working-directory overrides, detach configuration and console size. The daemon's ordinary defaults apply. Creation allocates an exec instance; it does not start the command. Running-container eligibility is enforced by the daemon, without a potentially stale client preflight or an implicit container start. Paused/stopped/restarting/disappeared targets retain classified daemon errors.

Admission deliberately requires complete 64-character lowercase hexadecimal Docker container and exec IDs. Names, prefixes and display text are rejected instead of being resolved to potentially different targets. The official OpenAPI type is only `string`; this narrower client boundary follows [Moby's full ID generation](https://github.com/moby/moby/blob/v28.0.0/pkg/stringid/stringid.go) and [exec configuration creation](https://github.com/moby/moby/blob/v28.0.0/container/exec.go). Alternative Docker-compatible implementations with other ID formats require separate compatibility evidence before widening this boundary. IDs are never trimmed or case-normalized.

`ContainerExecInstance` binds the acknowledged exec ID to the requested container ID. Inspection requests exactly `GET /v{negotiated}/exec/{execId}/json`. If the response supplies `ID` or `ContainerID`, it must match that binding. Missing/null identity fields remain unavailable and do not get replaced with the request identity; `RequestedInstance` separately records request context. Caller-created bindings are accepted only as explicit requests, not as proof that creation occurred. Bindings and responses belong to their selected Engine context; callers must not silently reuse them after switching Engines.

## Ownership, lifetimes and admission

Both operations borrow the owning connection's existing HttpClient, sealed pipe, API negotiation and transport-generation evidence. Neither creates a transport, reconnects, negotiates independently, retries automatically, or disposes the owner. HTTP/1.1 exact requests use the same System.Text.Json/HttpClient/SocketsHttpHandler/NamedPipeClientStream primitives as other resource capabilities. The production sealed pool refuses replacement pipes before they can carry a request.

Creation shares `MaximumConcurrentCommands` with container, image, volume and network mutations. Inspection has a separate finite `MaximumConcurrentExecInspections` gate. Admission is immediate: capacity produces a classified local failure without an unbounded waiter queue. The gate remains occupied through response decoding and cleanup. Resource operations, liveness and independent log/event/Stats streams retain their established ownership.

Creation uses `CommandTimeout`; inspection uses `RequestTimeout`. Caller cancellation, owner disposal and generation changes cancel active work. Inspection never publishes stale success, including late headers/bodies and changes during cleanup. Cancellation of inspection remains normal `OperationCanceledException` termination. Owner disposal cancels and joins every admitted exec operation and every existing stream. Repeated owner disposal joins the same work; a controlled transport that ignores cancellation keeps that join pending until its actual work and response cleanup finish. Admission remains bounded during such a delay. Requests after disposal do not dispatch. The capability cannot force an arbitrary noncooperative transport to finish.

## Creation outcome and cancellation evidence

`ContainerExecCreationResult` distinguishes:

| Disposition | Evidence |
| --- | --- |
| `NotDispatched` | No SendAsync boundary crossed: invalid request, occupied admission, ended/stale connection, or cancellation before dispatch. |
| `Acknowledged` | HTTP 201 plus a validated complete `Id`, with current connection/lifetime evidence through cleanup. `IsUsableAcknowledgement` is true. |
| `DaemonRejected` | An actual HTTP error status (400 or greater) was observed. The status survives an interrupted or oversized error body; it describes the daemon response rather than proving any final container state. |
| `Uncertain` | Dispatch was possible, but usable acknowledgement was not established: lost response, unexpected non-error status, missing/malformed `Id`, budget excess, deadline, cancellation, disposal or stale generation. |

SendAsync is a conservative possible-dispatch boundary, including internal HTTP queuing. A send failure cannot prove the daemon never allocated an exec. No outcome permits automatic repeated creation. A lost response without an exec ID cannot be recovered by inventing an ID or treating container state as proof of this exec. An explicit later create is a new mutation and may allocate another instance.

Caller cancellation throws `ContainerExecCreateCanceledException`, which remains a canceled Task and carries `Outcome`. A complete valid identity received late is preserved as `AcknowledgedInstance` historical evidence even when cancellation/staleness/disposal prevents a usable acknowledgement. `Disposition` remains `Uncertain` and `IsUsableAcknowledgement` remains false in those cases. An observed rejection remains `DaemonRejected` even if its error-body read was canceled. HTTP status, any complete bounded body, unavailable-body state and sanitized failure survive in the outcome. Malformed/missing creation IDs never populate `AcknowledgedInstance`. Only cancellation before possible dispatch proves `NotDispatched`.

## Inspection and sensitive evidence

Immutable Domain inspection evidence retains nullable `Running`, signed 64-bit `ExitCode` and `Pid`, stdin/stdout/stderr flags, removable/detach evidence and process configuration (`tty`, `privileged`, `user`, `entrypoint`, immutable `arguments`). Missing/null fields remain unavailable; the original body preserves which occurred. Wrong known types, duplicate JSON keys, invalid UTF-8/escaped Unicode, depth excess and supplied identity mismatch fail explicitly. Unknown additive fields are retained in the bounded original body without breaking supported decoding. Empty and absent process argument arrays remain distinct.

An exit code is preserved even when `Running=true`; it is not interpreted as completion. `Running=false`, PID zero, or exit zero alone cannot establish that an exec has ever started. [Moby's inspection implementation](https://github.com/moby/moby/blob/v28.0.0/daemon/inspect.go) exposes the configuration's running/exit evidence independently; [its newly created configuration](https://github.com/moby/moby/blob/v28.0.0/container/exec.go) has no start history. This capability adds no synthesized completion or success label.

`ContainerExecResponseEvidence.RawBody` is immutable original bytes, including malformed JSON and invalid UTF-8, retained only when the entire body fits its budget. HTTP status and `BodyUnavailable` distinguish a missing/interrupted/oversized body from an observed empty one. There is no retained partial body. `DaemonMessage` separately exposes the actual JSON `message`, or bounded plain error text when JSON cannot supply it. The raw bytes preserve all other original content. Classified descriptions contain no daemon exception text or command. Response/result/process/request string representations exclude raw bodies and arguments.

All original responses, process arguments and daemon messages are deliberate sensitive caller data. This implementation adds no logging, telemetry, terminal-content collection or automatic export. Applications must not log/export this evidence automatically. The Engine itself may emit exec command text in events; that daemon behavior is outside this client's logging control, and those events remain sensitive under the existing event contract.

## Bounds

These implementation defaults are validated, configurable tuning rather than product guarantees:

| Allocation/lifetime | Default |
| --- | --- |
| Concurrent creations | Existing shared command gate: 4 |
| Concurrent inspections | 4 |
| Argument count / individual UTF-8 bytes | 256 / 16 KiB |
| Aggregate unescaped argument bytes / exact serialized request bytes | 64 KiB / 64 KiB |
| Successful response / daemon error body | 64 KiB / existing 64 KiB |
| Creation / inspection deadline | Existing 30 seconds / 5 seconds |
| JSON depth / read chunk | 64 / at most 8 KiB |

Unescaped argument bytes are checked before serialization; the exact escaped request byte length is checked before dispatch. Temporary serialization is bounded by six times the admitted input plus finite JSON/vector overhead. Argument count and per-argument byte limits also apply to inspected process arguments. Response reads consume at most budget+1 bytes, reject an oversized known Content-Length before reading, and release response content on every completed path. Parsing and serialization run away from the caller's UI thread. No collection or retained history grows with session duration.

## Supported schema evidence

All seventeen official [OpenAPI 1.40](https://docs.docker.com/reference/api/engine/version/v1.40.yaml) through [1.56](https://docs.docker.com/reference/api/engine/version/v1.56.yaml) documents were reviewed. `Fixtures/Exec/contract-evidence.json` records each URL and SHA-256 with selected creation properties, creation-response schema, inspection schema, process configuration and response statuses. The independent executable fixtures check every negotiated version.

Across this range the selected request fields, HTTP 201 `Id` creation payload and HTTP 200 inspection fields are stable. The create contract lists 404/409/500; inspection lists 404/500. Unlisted 400/401/403/409/other statuses are still classified defensively. `ConsoleSize` first appears in the 1.42 creation schema and remains omitted here because resizing/initial terminal dimensions are outside this task. The creation response definition changes its reference name from `IdResponse` to `IDResponse` at 1.48, while the wire field remains exactly `Id`. Inspection continues to use uppercase `ID` and `ContainerID` and lowercase process-configuration fields throughout. No version is selected based on a daemon-advertised maximum alone.

## Verification and limits

The executable harness `--exec` covers negotiated request shapes, literal argument serialization/snapshotting, no shell default, unavailable/null/additive fields, full identities and mismatch rejection, malformed payloads/UTF-8/types, request/response/error bounds, classified errors and private evidence, cancellation before/after dispatch, uncertainty and no replay, shared mutation/independent read admission, generation changes, late noncooperative headers/bodies and joined repeated disposal. Controlled named pipes exercise creation applied with lost response, closed transport, issued cancellation/deadline/disposal, and coexistence with live logs/events/Stats, resource operations and liveness without an extra handshake or stream interruption.

The local fixture host exercises real SocketsHttpHandler framing over controlled pipe streams; it is not a real Engine or Windows permission/isolation test. Real Windows named pipes, Docker Desktop WSL2, supported real Engine versions, running/paused/stopped/deleted containers, Windows isolation and actual process exit behavior still require Windows/Engine evidence. Attached duplex exchange and resize now have separate simulated protocol evidence. Real TTY rendering, UTF-8/ANSI interaction, input/control bytes, resize, EOF and terminal closure still require real Engine/Windows validation. This change makes no interactive-terminal conformance claim.
