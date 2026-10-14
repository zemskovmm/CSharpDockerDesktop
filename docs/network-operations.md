# Application network deletion coordination

`DesktopSession` implements `INetworkOperations`, borrowing the retained negotiated connection's `INetworkCommands` and `INetworkReader`. The caller owns deliberate confirmation and displaying inspected dependencies. This Application contract does not add a deletion dialog or connect the Networks page to deletion; presentation integration remains pending.

## Admission and ownership

`NetworkOperationRequest` captures the complete original ordinal network ID, the original browsing connection generation and the exact completed preparation identity/revision. Admission validates and consumes the current `PrecheckEligible` preparation under the same session lock as preparation close, replacement, invalidation, events and connection changes. Built-in bridge/host/none evidence, attached networks, unavailable or malformed attachment evidence, contradictory names, mismatched identities, loading, failed, stale or consumed preparations cannot authorize dispatch. Rejection returns classified `NotDispatched` evidence without consuming useful preparation or replacing the retained operation.

The operation privately retains the original immutable completed preparation. The public preparation advances its revision and becomes `Consumed`; it cannot authorize a second command. There is one retained operation, completion receipt and latest observation, with one pending operation and no queue. Concurrent identical requests share the retained completion, including after completion. Superseded receipts cannot match the current preparation. Preparing again while operation work is pending is rejected, keeping acquisition and admission bounded. Every later deletion attempt requires a fresh explicit preparation and confirmation.

Only ordinary deletion is dispatched, once, with the captured complete ID. IDs are not trimmed, folded, shortened or replaced with names. Selection, cleared Inspect and navigation neither retarget the request nor abandon required observation. No dependencies are deleted, and no mutation is automatically retried.

## Retained outcomes

`NetworkOperation` is an immutable private snapshot. `NetworkOperationChanged` may originate off-thread; a future presentation observer must read the latest snapshot and marshal it for display. Observer exceptions are isolated without logging. Collections and nested metadata are copied into read-only projections. Raw Inspect, labels, attachment identities, names and original daemon messages/bodies remain private in-memory evidence, with no automatic logging or reporting path.

| Evidence | Meaning |
| --- | --- |
| `CommandOutcome` | The original `NetworkCommandResult`, including `NetworkCommandCanceledException.Outcome`, disposition, classified failure, HTTP status, private daemon message/body and unavailable-body flag. |
| `Interruption` | Caller cancellation, command deadline or connection change; retained independently of later protocol evidence. |
| `NotDispatched` | No mutation dispatch is established. |
| `ObservedAbsent` | A current authoritative full list excludes the exact ID and a current exact-target Inspect reports NotFound. |
| `ObservedPresent` | A current full list includes the exact ID and current exact-target Inspect returns that same complete ID. |
| `Unresolved` | Read failure, contradictory evidence, exhausted follow-up budget, event invalidation, incomplete conflict attachment evidence or interrupted continuity prevents resolution. |
| `ObservedConnectionGeneration` | Generation of the accepted observation; the original request generation is retained separately. |
| `ObservationListFailure`, `ObservationFailure`, `ObservationInvalidated` | Separate list/target acquisition failures and freshness evidence. |
| `RefreshedInspection`, `AttachmentObservationState`, `AttachmentFailure` | Conflict attachment evidence is complete, unavailable, malformed, failed or invalidated. It never becomes renewed preparation or confirmation. |

Acknowledgement and a command 404 alone cannot establish absence. Acknowledged-but-present and uncertain-but-absent are valid combinations. These observations describe current state at the reported generation; they do not attribute that state to the command or promise permanent absence. Full-list and Inspect disagreement remains explicitly unresolved. A conflict with unavailable, malformed or failed attachment evidence remains unresolved after the bounded observation budget even when current presence is observed; `ObservedExists` retains that independent presence evidence. The original conflict is preserved throughout.

## Scheduling and recovery

Every possibly dispatched result uses the existing network read scheduler to acquire a full list followed by exact-target Inspect, independent of selection. This also refreshes inspected attachments after conflict. A capacity-one signal and one pending observation slot bound work. Browsing receives a turn between observation attempts, and existing list/detail alternation remains. Caller cancellation controls the command token; reconciliation uses the session lifetime so possible dispatch is still observed.

Admission and response advance read fences, preventing pre-command and pre-response reads from establishing an outcome or restoring obsolete browser/Inspect content. Network events and event-stream gaps advance the observation epoch; once operation evidence exists they also fence in-flight browsing lists. Event-obsolete observations cannot resolve an operation or republish stale rows. A bounded follow-up obtains fresh evidence. Browser refresh and selection cannot cancel required operation reads or disable their independent request deadline.

Commands have an independent deadline, including when caller cancellation is ignored. Read deadlines use the session's existing ordinary request timeout. Deadlines and transport loss fence publication and signal the shared connection recovery owner. They do not create another connection owner. Late command responses remain useful private protocol evidence, including an acknowledgement received after teardown was fenced, but cannot prove absence or restore resource state.

Retry, disconnect and disposal fence publication before cancellation and join command, observation, preparation and browsing work before releasing the borrowed connection. Non-cooperative implementations can delay asynchronous teardown until they return; the transport is never released early. Events, Logs and other resource operations remain under their existing shared ownership.

A replacement connection can resume observation only when its actual endpoint and known nonempty Engine ID exactly match the command origin. Different/unknown Engine identity, an unknown origin, or another endpoint leaves the previous result unresolved. Recovery never replays the mutation. Identical requests share the current observation completion receipt after recovery; a caller holding an older completed receipt can observe later progress through the retained snapshot/events. `NetworkOperationOptions` bounds each connection observation pass (default four attempts), command timeout and follow-up interval. Interrupted I/O may produce no completed receipt; retention remains one snapshot and bounded counters.

## Simulated verification

Thirteen new deterministic harness groups control command/read completions, event barriers, concurrent admission gates and follow-up/recovery delays. They cover atomic preparation consumption, retained/older replay protection, concurrent capacity, built-in/attached/unavailable/malformed/contradictory/stale/loading/failed/consumed evidence rejection, exact Unicode/whitespace/punctuation IDs, immutable conflict attachments, isolated observers, selection independence, all protocol dispositions and original private outcomes, cancellation before and after possible dispatch, acknowledged-but-present and uncertain-but-absent results, failed/contradictory reads, pre-command/pre-response/event-obsolete fences, conflict attachment event/transport races, same/different/unknown Engine and endpoint recovery, command/read deadlines, late non-cooperative command/list/Inspect completion, Retry/disconnect/disposal joins, and coexistence with browsing, container/image commands, shared events and Logs.

The existing network preparation groups separately cover replacement, close, invalidation, cancellation and joined non-cooperative preparation ownership. Existing lower-layer network protocol fixtures and synthetic named-pipe tests remain separate evidence. The focused harness selector is `--network-operations`; the complete existing harness and Release solution build are the acceptance runs.

## Outstanding real Windows/Docker evidence

These cases simulate daemon responses and do not delete a real network. macOS harness/build results do not establish Windows non-elevated named-pipe permissions, Docker Desktop/WSL2 attachment/deletion races, real Engine restart behavior, alternative Engine compatibility, Windows packaging/delivery or workstation performance. Those require recorded real Windows/Docker runs. Confirmation/dependency display and actual network deletion presentation also remain a subsequent task.

On 2026-10-15, the complete harness passed **325/325 groups on macOS 26.7.1** using the established isolated .NET SDK environment. All **13/13 focused operation groups** also passed after strengthening the non-cooperative preparation/teardown case. The Release solution build passed with **zero warnings and zero errors**. The first complete run passed 324/325: the existing `VolumeActorIdentityAndConservativeDetail` case timed out awaiting its controlled event batching delay at `VolumeReconciliationChecks.cs:99`; the final complete run passed that case and every other group. No volume production or test code was changed. These recorded runs supplement the controlled race assertions; a rerun alone is not evidence of race correctness. Local logs: `/private/tmp/network-operations-complete.log`, `/private/tmp/network-operations-final.log`, `/private/tmp/network-operations-focused-final.log` and `/private/tmp/network-operations-release-final.log`.
