# Application container Logs sessions

`DesktopSession` implements the presentation-independent `IContainerLogs`. An explicit `OpenContainerLogs(fullId, follow, tailLines)` admits one user-requested Logs session only when the complete ID is present in the current authoritative Ready container snapshot and the connection offers `IContainerLogSource`. Admission returns immediately; state transitions are asynchronous. The overload with an out admissionFailure classifies invalid parameters, unavailable/current-snapshot requirements and absent full identities without replacing an already owned session. The session captures the container snapshot generation, endpoint and Engine identity. A replacement cancels the previous request and joins its opening, ingestion and subscription disposal before acquiring another subscription. One runner and one latest pending request coalesce rapid replacements, including sources that return successful subscriptions after cancellation; every late successful result is disposed.

The contract exposes immutable `ContainerLogsSnapshot` metadata and bounded `ReadContainerLogs(afterEntryId, maximumEntries)` pages. State distinguishes opening, streaming, recovery, natural completion, classified failure, cancellation and close. Original bounded daemon messages remain explicit caller data in `Failure`; no message or log text is automatically logged, displayed or transmitted. Stdout/stderr and authoritative combined TTY output remain distinguishable.

## Reading and notification lifetime

`ContainerLogsChanged` is a coalesced signal from a background task. One bounded channel slot carries a signal, never a queue of full output snapshots. The notifier publishes at most once per configured interval and reads the latest state. The presenter should read metadata and only the needed page, marshal the result to its UI, and reject a page if its SessionId or ViewRevision is obsolete. Handlers should return promptly. The [Logs presentation](container-log-presentation.md) consumes these notifications with bounded dispatch/page work, retained search/filtering and explicit autoscroll. Pause semantics remain unresolved and no Pause control is added.

A page contains immutable string segments in first-observation order. An unfinished segment can acquire more text without changing EntryId: reread visible pages when ViewRevision changes, rather than assuming an entry ID means immutable future contents. IDs are Application-local and never identify daemon messages. Pages remain valid immutable objects after Clear/close, but are obsolete for the current view. Their lifetime belongs to the caller; presenters must bound their own retained page/cache references. Reading copies only a bounded requested page, not the whole retained buffer; a requested page size is capped by the configured limit. Ingestion appends under a short coordination lock to bounded builders and yields between configured batches; it does not copy the display buffer per fragment.

Each stream assembles independently across frames and fragments. CR/LF, Unicode characters and original timestamp prefixes remain in Text. LF closes a logical line; frames and empty boundary markers do not manufacture lines. Interleaving preserves each stream's text and orders entries by the first fragment of each line/segment. Long lines split at the character budget without splitting a surrogate pair within a decoded fragment. `Continued` indicates continuation; `Partial` identifies an unfinished/split line. A changed original timestamp inside a line starts another bounded segment carrying that timestamp, so available metadata is preserved. EOF retains any partial line. Eviction removes oldest segments predictably; evicted unfinished segments cannot reappear on later completion. EvictedEntries and DiscardedCharacters expose bounded loss. The retained entry/text budgets, at most three unfinished builders, and independent replay entry/text budgets constrain memory; no allocation follows the size of an entire line.

`ClearContainerLogs` removes the local retained view and unfinished assembly under the ingestion lock and advances its revision. It sends no Docker request, does not reopen initial history, and does not alter the independently retained cursor/comparison bookkeeping. Previously accepted text or delayed page publications cannot restore the cleared view. Subsequent unread source output is new ingestion. `CloseContainerLogsAsync` ends ownership immediately, rejects further publications from that request and joins pending opening, reader and asynchronous subscription disposal. Selection changes, authoritative deletion and Logs not-found end ownership too. Not-found also removes the stale container row. Disconnect and application disposal close and join all Logs work together with reads, events and operations. Disposal additionally cancels and joins the notification task.

## Recovery and limits

A transient Logs transport loss or opening deadline signals the existing connection owner. Logs never creates a second connection coordinator. A still-requested follow session waits for the old connection's reads/events/operations/Logs cleanup, the replacement handshake and a fresh successful authoritative list before reopening. Same endpoint, known equal Engine identity and exact full container identity are mandatory. An explicit connection Retry also uses this process; Disconnect/close never resurrect a session. Natural EOF, finite-history EOF and permanent failures never trigger automatic Logs replay. Driver unavailability, denied access, malformed output, daemon errors, admission failure, not-found and cancellation remain distinct.

Recovery attempts are bounded over the user-requested session, with a cancellable delay. The connection owner's recovery delay/cooldown remains independently bounded. An exhausted Logs session stays failed until another explicit request. A source returning after its opening deadline is disposed and classified as an opening deadline. Established streams have no ordinary short-request timeout.

The cursor is the exact maximum observed nonnegative Unix timestamp, retaining nanoseconds. Recovery subtracts the configured small overlap without passing through DateTime or floating point. Historical replay always has a finite positive tail, even when the original request had tail zero. Missing timestamps never invent a cursor: a fresh finite tail is requested and continuity becomes Uncertain.

Comparison is a separate bounded window of assembled segments, scoped to this request/Engine/container. It compares timestamp, stream, exact text and segment state, consuming ordered occurrences rather than suppressing a global set of matching messages. Fragment sequence/frame IDs play no role; different fragmentation preserves comparison. Repeated identical messages and timestamps consume only the observed replay multiplicity, allowing subsequent matching messages through. Evidence evicted inside the overlap, missing/partial timestamps, interrupted assembly, mismatched replay and incomplete replay expose Uncertain. Ambiguous comparison coverage disables suppression and retains replay. Pending unfinished replay is released at the next notification batch with Uncertain rather than hiding live partial output indefinitely.

BoundedOverlap reports matching bounded replay evidence, **not a guarantee of complete continuity**: Docker provides no stable message IDs, finite history can omit messages, and indistinguishable repeated timestamps/text can leave ambiguous replay alignment. The Logs presenter exposes this limitation and the eviction/loss counters. No unlimited history request or globally persistent identical-text filter is used.

## Implementation defaults

All settings are validated through `ContainerLogsOptions`; they are adjustable profiling choices, not fixed product requirements.

| Setting | Default |
| --- | --- |
| Initial / maximum historical tail | 300 / 10,000 lines |
| Retained entries / UTF-16 text budget | 4,096 / 2 Mi characters |
| Segment character limit | 4,096 |
| Page size limit | 256 entries (validated maximum 1,024) |
| Replay comparison entries / characters | 512 / 256 Ki characters |
| Ingestion yield batch | 128 fragments |
| Notification interval | 100 ms |
| Replay overlap | 1 second (validated maximum 10 seconds) |
| Recovery delay / maximum attempts | 2 seconds / 5 |

Retention characters measure managed UTF-16 storage; replay strings, builders, metadata and caller-owned pages add bounded separate overhead. The upper bounds are resource limits, not claims about working set, latency or Windows GUI performance.

## Verification evidence

The dependency-free controlled connection/subscription harness exercises high-volume retention and notification coalescing, interleaved fragmented assembly, CR/LF and Unicode content, immutable pages and Clear barriers, rapid replacement/late opens, joined asynchronous disposal, selection/deletion/not-found, terminal failures, finite and follow EOF, exact reconnect cursors, changed fragmentation, repeated timestamp/message multiplicity, missing/exhausted replay evidence, cancellation during authoritative recovery, stale output, Engine identity mismatch, bounded attempts, and simultaneous browsing/event reconciliation/lifecycle operations. These are simulated Application evidence; the existing Infrastructure harness separately exercises actual controlled named-pipe HTTP streams.

The full harness passes 100/100 groups on macOS 26.7.1; the solution builds without warnings/errors. No real Windows Docker Desktop/WSL2 daemon or Windows GUI session is available here. Real daemon restart/replay behavior, logging drivers, access permissions, GUI responsiveness and workstation performance targets remain unverified; this task claims no real-Windows evidence.
