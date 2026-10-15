# Borrowed attached TTY exec protocol

`IContainerExec.StartTtyExecAsync` starts the supplied exact container/exec binding once. It does not create an exec, choose a command, reconnect, resize, or change container lifecycle. The selected connection is borrowed; the returned `IContainerExecAttachment` is owned and must be disposed. Application terminal-session coordination and the terminal surface remain separate work.

## Contract evidence

`Fixtures/Exec/start-contract-evidence.json` records the official Docker OpenAPI start body and statuses, URL and complete-document SHA-256 for each negotiated API 1.40 through 1.56. The common body sets `Detach=false,Tty=true`; optional ConsoleSize is deliberately omitted. The schema documents status 200. The wire upgrade requires additional source evidence: `Fixtures/Exec/hijack-source-evidence.json` records bounded findings and hashes from the official 1.40-era router, current router at a pinned commit, and pinned official hijack client.

Those routers return 101 with `Connection: Upgrade`, `Upgrade: tcp`, and `application/vnd.docker.raw-stream` when Upgrade is requested. Their 200 branch is the historical non-upgrade path. The official client requests Upgrade and requires 101, retaining read-ahead bytes. This capability implements that upgrade branch only. It rejects 200, wrong media/protocol, absent upgrade tokens, duplicate/invalid/oversized headers, and HTTP body framing on an upgrade. It never interprets terminal bytes as HTTP chunks or Docker multiplex frames.

The router emits attachment headers before invoking process start. Consequently an established attachment proves only transport establishment. A missing executable or other late start failure may be raw terminal output. EOF, cancellation and disposal neither prove process exit nor kill the process. `InspectExecAsync` remains authoritative for reported Running/ExitCode fields.

## Selected Engine continuity

A dedicated `NamedPipeClientStream` opens the same selected local endpoint once. On that very physical stream, bounded HTTP/1.1 readiness, version, negotiated-version info and exec-inspection requests verify the Engine identity, version/API range, OS context, exact container/exec binding, TTY and enabled standard streams before the start POST. Missing Engine identity, mismatched metadata/binding or unsupported framing prevents start. The parser supports bounded Content-Length or simple chunked preflight responses, rejects close-required preflight responses, and retains bytes read beyond the final header boundary. Chunk extensions and nonempty trailers are deliberately outside this narrow Engine response contract.

No HTTP pool participates in start; there is no stream replacement, redirect, retry, informational-response retry, or replay. The retained ordinary connection remains available for resource, exec-inspection and [explicit resize](../../docs/container-exec-resize.md) requests. Events, Logs and Stats keep their existing dedicated paths. The owner generation fences both delivery and input. A generation change cancels active work and revokes queued output, including output retained after EOF. A new selected connection is required after generation change.

## Outcome and sensitive evidence

The possible-dispatch boundary is immediately before the first start POST write. Results distinguish NotDispatched, observed DaemonRejected, Established and Uncertain. Once dispatch is possible, missing/malformed/lost responses remain uncertain. No outcome authorizes automatic create/start retry. Start cancellation throws `ContainerExecStartCanceledException`, retaining disposition, original HTTP rejection evidence when observed, and historical `UpgradeObserved`. A canceled historical upgrade is not returned as a usable attachment.

Original rejected HTTP bodies are deliberate sensitive evidence. They remain bounded raw bytes, including invalid UTF-8 and malformed JSON. Oversized bodies retain a bounded prefix and mark BodyUnavailable; failures while reading the body preserve observed HTTP status. Safe `ToString` methods do not emit that body, daemon message, terminal commands or input. Nothing logs or transforms raw TTY content.

## Ownership and budgets

The start token governs establishment and the established lifetime. `ExecOpeningTimeout` bounds pipe/open/preflight/start establishment, independently of ordinary RequestTimeout/CommandTimeout; the deadline is removed after the upgrade. Established reads have explicit cancellation with no ordinary short-request timeout. PipeConnectTimeout separately bounds normal physical connection attempts.

The owner admits at most MaximumConcurrentExecAttachments. Each attachment has bounded Channel output (ExecOutputCapacity times MaximumExecOutputChunkBytes plus one producer chunk and a 4096-byte HTTP read-ahead buffer). Slow output consumers backpressure physical reads. Bytes are immutable fragments; callers perform terminal decoding/rendering. UTF-8 character boundaries, ANSI sequences, control bytes and invalid byte sequences are all preserved. Multiple output readers compete for fragments; applications needing one complete stream should use one reader. Canceling an individual channel wait only cancels that wait.

Input accepts at most MaximumExecInputBytes per call and one admitted write, with immediate capacity rejection and no input queue. It snapshots input before I/O; simultaneous read and write are supported. Cancellation before dispatch yields NotWritten; interrupted/canceled potentially applied writes yield Uncertain and terminate attachment transport. Input is never retried. Partial write counts cannot be inferred from the Stream API. There is no half-close, detach-key interpretation or invented kill command.

Attachment disposal cancels work, revokes output delivery, closes the stream and joins producer and admitted input. Owner disposal cancels and joins all admitted establishment and attachments alongside the existing capabilities. Repeated disposal joins the same task. Closing transport starts promptly on cancellation, but capacity remains occupied while a noncooperative opening/read/write/dispose still owns work. No replacement work bypasses that ownership. Completion is published after actual stream cleanup and admitted input join. Normal EOF permits bounded output draining; cancellation/disposal/generation changes suppress it. After normal EOF the transport and admission are released; callers still dispose their attachment object.

## Evidence and remaining validation

`ExecAttachmentChecks` independently constructs executable fixtures for every API 1.40–1.56, one-byte/coalesced header and output fragmentation, UTF-8/ANSI/control/raw bytes, duplex input/output, EOF draining, bounded headers/errors/output/input/admission, malformed and rejected handshakes, cancellation on each side of dispatch, opening deadlines, applied-start/lost-response with one POST, stale generations, concurrent input/disposal, noncooperative open/read/write/dispose and retained resource/exec inspection.

Controlled `NamedPipeServerStream` tests exercise actual local pipe streams, same-pipe preflight, fragmented upgrade, read-ahead output, exact control-byte input, EOF and lost response without replacement/replay, while the retained ordinary pipe serves resources and inspection. These run with the explicit test-host permit on macOS; they simulate an Engine and do not establish Windows Docker compatibility.

Outstanding real validation includes Windows named-pipe permissions and cancellation, Docker Desktop/WSL2 Engine versions, real exec command failure/inspection, full-duplex terminal exchange, container disappearance/Engine restart and coexistence under real load. Rendering, keyboard/copy/paste, terminal surface selection and Application terminal-session coordination are outside this change. Resize has separate simulated protocol evidence; actual dimensions still require real Windows/Engine validation. Fixtures establish protocol and ownership behavior, not interactive-terminal conformance under IDD-0008/IDD-0013.
