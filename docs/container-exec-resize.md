# Borrowed TTY exec resize

The owning negotiated Engine connection exposes `IContainerExec.ResizeTtyExecAsync(instance, columns, rows, token)`. The caller supplies the exact `ContainerExecInstance` and both dimensions explicitly. This protocol capability does not create/start an exec, select dimensions, change container lifecycle, or coordinate terminal sessions/UI. An attachment is neither required nor consumed; an existing duplex attachment keeps its own transport and lifetime.

Admission requires complete 64-character lowercase hexadecimal container and exec IDs and each dimension in **1..65535**. This is a deliberate bounded client contract; the official API specifies integer dimensions without prescribing this client limit. Zero, negative, oversized and missing/invalid identities fail locally before any request. Nothing trims/resolves identities or substitutes a default dimension. Results preserve the requested container/exec pair and columns/rows.

## Binding and official request

Under one admitted command slot and deadline, `GET /v{negotiated}/exec/{execId}/json` checks the exact `ID` and `ContainerID`, and requires `ProcessConfig.tty=true`. Missing, mismatched, malformed or unavailable binding evidence prevents the resize POST. This read uses the same retained ordinary HTTP pool rather than consuming attachment bytes. Running/exit fields are not interpreted as proof of eligibility or process state; the daemon evaluates the resize. The preflight does not lock daemon state, so disappearance/state changes between the read and POST can still produce daemon errors.

The mutation sends exactly:

```text
POST /v{negotiated}/exec/{execId}/resize?h={rows}&w={columns}
```

It uses HTTP/1.1 exact, no request body, no upgrade, invariant decimal values and the owning connection's negotiated API. Width maps to columns (`w`), height to rows (`h`); asymmetric tests exercise both orderings. The connection's sealed ordinary pool refuses replacement transport after negotiation. There is no reconnect, redirect or application retry to dispatch against a replacement Engine, and no automatic replay after uncertain dispatch.

Every official [API 1.40](https://docs.docker.com/reference/api/engine/version/v1.40.yaml) through [API 1.56](https://docs.docker.com/reference/api/engine/version/v1.56.yaml) schema defines required string `id` and integer query `h`/`w`, HTTP **200** with no payload schema, and **400/404/500** JSON error responses. The description requires a TTY created and started with TTY enabled. `Fixtures/Exec/resize-contract-evidence.json` retains only selected parameters, response contracts and error schema, with source URLs and SHA-256 hashes of all seventeen complete official documents. Executable fixture requests are independently constructed. Defensive classification also handles 401/403/409 and other unexpected statuses.

This narrow capability accepts an empty HTTP 200 acknowledgement. Unexpected nonempty success bodies remain bounded private evidence and produce `Uncertain/MalformedResponse`; unexpected non-error statuses produce `Uncertain/UnexpectedResponse`. HTTP acknowledgement describes the resize request. It does **not** prove rendered dimensions, actual window size, a running process, successful start or exit.

## Outcomes and cancellation

| Disposition | Meaning |
| --- | --- |
| `NotDispatched` | The resize POST never crossed SendAsync: invalid input/binding, rejected preflight, occupied admission, ended/stale connection or pre-dispatch cancellation/deadline. Binding reads may already have occurred. |
| `Acknowledged` | Empty HTTP 200 observed and current connection/lifetime fences passed through response cleanup. `IsAcknowledged` is true. |
| `DaemonRejected` | An actual error HTTP status for the resize POST was observed. Status survives interrupted, canceled or oversized body reads. |
| `Uncertain` | POST dispatch was possible but current acknowledgement was not established: lost response, unsupported response, deadline, cancellation, disposal or generation change. |

SendAsync, including HTTP queuing, is the conservative possible-dispatch boundary. A failure after that point cannot prove the resize was not applied. No outcome authorizes automatic replay. A later explicit request is a distinct mutation. Inspection may obtain process state but does not prove rendered dimensions.

`ContainerExecResizeCanceledException` is normal canceled-Task termination and carries `Outcome` plus the original cancellation token. `Response` belongs only to resize; `BindingResponse` belongs only to preflight, so successful inspection cannot be mistaken for resize acknowledgement. `AcknowledgementObserved` means historical HTTP 200 resize headers were observed, even if cancellation, lost body, disposal or staleness invalidates current success. Late observed rejection remains `DaemonRejected`; cancellation replaces the classified failure while retaining status and bounded private evidence. `IsAcknowledged` remains false for every canceled/stale/ended outcome.

Complete bounded response bytes are immutable private data. They preserve malformed JSON/UTF-8 without automatic logging/export. An interrupted/oversized body retains status and `BodyUnavailable=true` with no partial body. Original JSON `message` or bounded non-JSON error text is exposed separately; sanitized failure descriptions and result/evidence string representations omit daemon content. Binding inspection may contain sensitive process data and has the same privacy restrictions.

## Bounds and ownership

Resize shares `MaximumConcurrentCommands` (default 4) with exec creation and container/image/volume/network mutations. Admission is immediate, without a pending waiter queue. The slot covers binding validation, dispatch, complete response reads and cleanup. Exec inspections have their independent finite gate; resource operations and existing stream admissions are unchanged.

One finite `CommandTimeout` (default 30 seconds) includes binding establishment, HTTP queuing, mutation headers/body and cleanup fences. It does not become an attachment lifetime. Caller cancellation, owner lifetime and transport generation changes cancel admitted work. Successful/preflight response budget is `MaximumExecResponseBytes` (default 64 KiB); errors use `MaximumCommandErrorBytes` (default 64 KiB). Reads consume at most budget+1 bytes. Binding JSON parsing runs away from the UI thread. At most two bounded response bodies are retained per result; there is no growing resize history.

Connection disposal cancels and joins admitted resize work with existing exec operations, attachments and streams. Repeated disposal joins the same cleanup. An arbitrary noncooperative transport can delay that join; capacity stays occupied until actual work/cleanup finishes. Late headers, bodies and cleanup callbacks retain historical evidence but cannot publish current success after cancellation/deadline/disposal/generation change. Resize cancellation does not dispose a duplex attachment. A changed owning generation still invalidates the attachment under its existing continuity contract.

## Simulated evidence and remaining gap

Recorded validation on macOS 26.7.1: the Release focused exec harness passed **19/19 groups**, the full Release harness passed **384/384 groups**, and the Release solution build completed with **0 warnings and 0 errors**. These runs use simulated daemon responses; the all-version request checks cover every API from 1.40 through 1.56.

The `--exec` harness covers negotiated API 1.40–1.56, independent exact requests, dimension ordering/invariant formatting, identity/dimension rejection, binding/TTY mismatches, classified errors, malformed/private/oversized bodies, local no-dispatch results, acknowledgement/rejection, canceled Tasks, deadlines, disposal before/after dispatch, late noncooperative headers/bodies/cleanup, stale generations, joined repeated disposal and shared mutation admission with independent reads.

Controlled named pipes exercise actual `SocketsHttpHandler` framing, empty success, unavailable retained transport, resize applied with lost response, cancellation/deadline/disposal after issuance, and refusal to open a replacement pipe or replay POST. A simultaneous fixture exercises resize while raw duplex exec input/output, Logs, Events, Stats, resource reads/commands and liveness remain active; no extra handshake is introduced. Separate attachment fixtures exercise all negotiated versions and show resize cancellation leaving the raw attachment usable.

These are simulated daemon tests on macOS with explicit test-host permission, not a real Windows Engine. Remaining required evidence includes Windows named-pipe cancellation/permissions, Docker Desktop/WSL2 and supported real Engine versions, actual TTY window-size observation after resize, running/non-TTY/not-yet-started/exited/paused/deleted exec behavior, concurrent output/input/resize under load and Engine restart. Terminal rendering, ANSI/UTF-8 interpretation, keyboard/copy/paste and Application session coordination remain separate work. No terminal conformance is claimed.
