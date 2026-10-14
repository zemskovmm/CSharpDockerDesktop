# Application-owned volume deletion preparation

`DesktopSession` implements `IVolumeDeletionPreparation`. This is read-only preparation for a future confirmation: it does not display a dialog, authorize deletion, call `IVolumeCommands`, remove a container, or coordinate deletion. The session borrows `IVolumeDependencyReader` from its retained negotiated connection. It never opens another connection or derives dependencies from container browser rows.

## Explicit request and retained evidence

A consumer explicitly calls `PrepareVolumeDeletionAsync(new VolumeDeletionTarget(connectionGeneration, originalName), cancellationToken)`. The generation comes from the current resource browsing snapshots. The full original name is preserved using ordinal equality; case, Unicode, punctuation and whitespace are not normalized. Existing volume-name usability bounds apply. Selection, inspection clearing and navigation do not change the captured target. The target need not be the current browser selection or a retained row; a dependency query does not establish volume existence.

The returned task finishes after all work borrowed by that request has returned. `VolumeDeletionPreparation` exposes the latest admitted immutable snapshot during this time; `VolumeDeletionPreparationChanged` may run off the UI thread. Consumers should read the latest snapshot and marshal presentation work themselves. Notifications are coalesced by snapshot identity, and an observer exception is isolated without logging it. No names, state, daemon messages or other metadata are logged automatically.

A snapshot captures its target, `VolumePreparationIdentity` (monotonically allocated preparation ID plus revision), phase, dependency evidence and classified failure. Revisions change with state transitions. Phases distinguish Loading, Completed, Invalidated and Failed. Completed preserves all three Domain completeness states:

- Complete includes running and stopped related containers with full IDs, available names and runtime state, and may have an empty related collection.
- Incomplete retains established relationships and the full unverified candidate IDs.
- Unavailable preserves a null related collection rather than fabricating empty relationships.

Missing metadata remains null. All nested evidence collections are immutable. `HasUsableEvidence` is true for current Completed Complete/Incomplete evidence; Incomplete still requires deliberate handling of its uncertainty. Unavailable is acquired evidence but has no usable relationship collection. There is no “safe to delete” property.

Failures distinguish invalid input, unavailable capability, stale generation, exhausted admission, caller cancellation, whole-request deadline, acquisition failure and invalidation causes. Acquisition failure retains the classified `EngineResourceFailure`, including any original private daemon message, solely as deliberate consumer data. A dependency-query NotFound can refer to a candidate container that disappeared, so it does not establish absence of the volume. An unexpected borrowed-reader exception produces a fixed description without copying exception content.

Invalid/stale/missing-capability/canceled-before-admission/capacity rejections return an explicit Failed snapshot with zero identity, without replacing the retained admitted request. Consumers must inspect the returned result rather than treating an older retained snapshot as the outcome of a rejected request.

## Admission, cancellation and ownership

At most one daemon acquisition and one waiting replacement exist per session. A new admitted request invalidates and cancels its predecessor immediately, then joins its predecessor before acquiring fresh evidence. While a replacement waits, further requests are rejected with CapacityExceeded; no queue grows with repeated clicks. Each admitted request has a whole-request deadline using `ConnectionRecoveryOptions.OperationTimeout`, including time waiting for the previous non-cooperative request. It is not reset between the candidate list and individual inspections. The borrowed reader also enforces its own acquisition budgets.

Caller cancellation immediately exposes Invalidated/Canceled state and fences publication. `CloseVolumeDeletionPreparationAsync(identity)` invalidates the matching preparation ID and joins its work. Revision changes within that same request do not prevent closing; an older preparation ID cannot close a replacement. Events invalidate without canceling/requerying the acquisition repeatedly. Even an invalidated request remains owned until its borrowed I/O returns. Thus a non-cooperative reader can delay task completion, replacement, disconnect or disposal, but cannot publish usable late evidence or permit concurrent replacement queries.

Disconnect, Retry, connection loss and session disposal fence preparation before canceling shared work. The connection owner joins preparation alongside existing resources, commands, events and Logs before disposing the connection. Preparation never disposes the borrowed connection itself. A dependency TransportLoss or whole-request deadline signals the existing connection-owned recovery workflow; there is no additional retry loop. Recovery does not resurrect a previous preparation or acquire dependencies automatically. A fresh explicit preparation is required.

## Confirmation validation and invalidation

A future confirmation can capture the completed snapshot's identity/revision and call `IsVolumeDeletionPreparationCurrent(identity)`. It checks the exact retained revision, current connection and usable evidence. Reading an immutable old snapshot by itself does not establish continuing usability.

Usability is invalidated by:

- Supersession, explicit close or caller cancellation.
- Connection replacement/disconnect/loss/disposal, including failures detected by other retained capabilities.
- Any observed container event: even an unknown container may have become a new dependency, and running/stopped/name metadata can change.
- A volume event with the exact target name, or missing/unusable identity. A different usable ordinal volume name does not invalidate this target. Actor attributes never substitute for identity.
- Event-observation startup/reopening/failure gaps, which could hide relevant changes.
- An accepted authoritative volume list omitting the target, or current exact-target volume Inspect NotFound. Null collections and warning-bearing lists cannot prove absence. Obsolete Inspect results cannot invalidate a newer target through stale absence evidence.

Events occurring after admission but before or during acquisition invalidate the same Loading state, so a later successful response cannot publish Completed evidence. Events before admission do not poison a fresh explicit query. Duplicate or continuous events retain one invalidation and do not trigger dependency polling or an automatic acquisition loop. Acquired evidence may remain on an Invalidated snapshot for deliberate display as obsolete data, but identity validation and `HasUsableEvidence` reject it.

These fences do not make daemon reads transactional. The candidate list and individual inspections occur at different times; events can be missed or delayed, and a new dependency can appear immediately after validation. Complete empty evidence does not prove that a volume exists, that deletion is authorized, or that deletion will succeed. A future confirmation/deletion coordinator must preserve these limits and handle the daemon's final response and the precheck/delete race.

## Verification evidence and limits

The deterministic executable harness uses synthetic dependency readers, controlled resource lists and inspections, event barriers, delayed cancellation-ignoring requests and controlled recovery clocks. It covers exact names/full IDs, running/stopped/missing metadata, immutable evidence, complete empty/incomplete/unavailable distinctions, classified/private failures, bounded replacement and rejection, active/queued cancellation, whole-request deadlines, late responses, events before/during/after acquisition, observation gaps, authoritative disappearance versus incomplete lists, selection independence, stale generations and shared recovery. Teardown cases exercise loss from containers, images, events, Logs, commands and liveness, and verify joining before connection disposal. Preparation coexists with active Logs/events, resource browsing and container/image operations.

On 2026-10-14, the full harness passed **221/221 groups on macOS 26.7.1 using .NET SDK 10.0.402**. The Release solution build passed with **zero warnings and zero errors**. No project verification policy exists. The harness also includes the existing real SocketsHttpHandler/synthetic named-pipe server checks. These are simulations; they do not establish real Windows named-pipe ACLs, a running Windows Docker Desktop/WSL2 daemon's dependency/race semantics, delivered Windows behavior or workstation performance. Those real-environment checks remain outstanding. See [dependency acquisition](volume-dependencies.md), [volume reconciliation](volume-reconciliation.md) and [volume browsing](volume-browsing.md).

## Atomic operation consumption

[Application-owned volume deletion](volume-operations.md) now consumes the exact completed ID/revision and target under the same session lock as preparation invalidation. The retained preparation advances to `Consumed`; the operation keeps its original immutable completed evidence. Unavailable evidence cannot admit deletion; usable incomplete evidence remains incomplete. Fresh preparation is rejected with `CapacityExceeded` while a volume operation or its borrowed work is pending. Conflict refresh stays operation observation evidence and never silently creates another confirmable preparation. The caller still owns dependency display, the permanent-data-loss warning, and deliberate confirmation; volume deletion presentation is pending.
