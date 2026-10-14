# Application-owned network synchronization

`DesktopSession` reconciles networks through the existing shared Engine observation workflow. One retained negotiated connection and one subscription serve Containers, Images, Volumes and Networks. `INetworkReader` remains a borrowed list/inspect capability, independent of the other resource readers. No network-specific subscription, connection, recovery owner, mutation or event history is introduced.

## Startup and observation gaps

The first network list waits for shared observation establishment. Missing observation capability, classified non-transport opening failure or the existing bounded opening deadline releases snapshot admission so browsing remains available. A non-cooperative opener cannot postpone the fallback snapshot indefinitely; the owner retains its eventual result and disposes any late subscription before transport disposal. Observation diagnostics and bounded retry/cooldown remain owned by the existing shared workflow.

One pending invalidation bit on the shared coordinator bridges observations arriving before the network coordinator exists. The initial snapshot preserves this bit for an authoritative follow-up. Later list reads consume only observations pending when admitted; observations during the read leave another read pending. Useful list results may publish during sustained events instead of waiting for a quiet interval. Reopening after an observation gap refreshes networks and invalidates requested selected inspection, including its raw JSON and failure content. An explicit inspection clear remains NotRequested.

## Invalidation and evidence

Every ordinal `network` event type dispatches an invalidation hint, regardless of action, including future/unknown actions. Duplicate observations and equal timestamps are accepted. Resource names and actor attributes never substitute for `EngineEvent.ResourceId`. Events do not patch row properties, IPAM, attachments, resource existence or deletion eligibility.

All network events schedule a list reread. A usable full actor ID matching the selected full ID also invalidates requested detail immediately. Matching is ordinal, without trimming, shortening or case folding. Opaque identities may contain non-hexadecimal text, Unicode, spaces and escaped path characters. Missing, whitespace-only, oversized, dot-segment, control-character or malformed UTF-16 identities conservatively invalidate requested selected detail. A different usable full identity only invalidates the list. An authoritative list excluding the selected identity or a still-current exact Inspect NotFound supplies disappearance evidence; an event action alone does not.

Detail invalidation clears inspection/raw JSON and failure content before notification, advances the detail publication fence, and leaves replacement Inspect pending. It does not repeatedly cancel active I/O under event bursts. Superseded success, NotFound, malformed and other non-transport failure results cannot resurrect detail, erase newer rows or replace current diagnostics. An actual TransportLoss from an event-superseded request on the still-current connection remains continuity evidence for the shared recovery owner. The independent original request deadline remains effective even when a non-cooperative request ignores superseding cancellation.

Authoritative responses preserve the existing [network browsing evidence contract](network-browsing.md): immutable copied snapshots; null versus empty metadata, IPAM and attachments; exact raw Inspect text; and separate classified original failures. List rows never establish attachment completeness. Inspected attachments describe only the returned response, do not establish cluster-wide completeness and cannot authorize deletion.

## Bounded scheduling and lifetime

A capacity-one wake channel, pending list/detail flags and revision counters bound network scheduling state independently of event count. The existing configurable `ContainerReconciliationOptions.BatchInterval` batches event-driven list admission. A burst clears exposed selected detail once until its state changes; it does not allocate a notification or queued request per observation. One network worker serializes list/detail I/O and alternates admission when both are pending, preserving manual refresh and useful list publication under sustained invalidation.

Network events do not schedule unrelated resource reads or cancel lifecycle/image operations, observation or Logs. Shared container/image/volume events still reconcile their own families independently. The shared high-water cursor preserves nanosecond precision and the configured replay overlap; network observations participate in that cursor without retaining metadata or event history.

Reconnect starts fresh network state, repeats observation-before-snapshot synchronization and fences previous generation publications. Disconnect, Retry, transport recovery and disposal invalidate publication before cancellation, then join network requests, shared observation work, non-cooperative openers and late subscriptions before disposing the transport. An owned request that never finishes can keep teardown pending but cannot revive state after invalidation/deadline. No network metadata, event attributes, raw JSON, addresses, attachment content or original daemon messages are added to automatic logging/reporting.

## Simulated verification and outstanding platform evidence

On 2026-10-15, the dependency-free executable harness passed **291/291 groups** on **macOS 26.7.1 with .NET SDK 10.0.402**, and the full Release solution build passed with **zero warnings and zero errors**. No project verification policy is configured. Twelve new deterministic network reconciliation groups cover:

- A forced observation-before-coordinator bridge with and without later observations, initial/later snapshot boundaries, unknown actions and resource types, and duplicate/equal nanosecond timestamps.
- Full opaque ordinal IDs and unusable IDs, immediate inspection/failure clearing, explicit NotRequested preservation across events and reopening, exact raw evidence, obsolete success and all non-transport failures, current NotFound and authoritative disappearance.
- Bounded sustained bursts, coalesced notifications, serialized alternating list/detail admission, manual refresh, and useful snapshots despite ongoing events.
- Classified opening failure, opening deadline fallback with a non-cooperative opener, gap reopening, precise shared replay overlap, reconnect generations, event-invalidated request deadlines, current transport-loss evidence, and joined Retry/disconnect/disposal with late results.
- Independent missing resource capabilities and one shared subscription alongside unrelated resource readers, pending lifecycle/image operations and active Logs, with no event-driven interference.

These controlled scheduling, synthetic transport and build results do not establish real Windows/Docker behavior. Real Windows named-pipe permissions, non-elevated Docker Desktop/WSL2 use, external CLI operations and daemon restart, driver/swarm attachment visibility, Windows delivery, and measured workstation CPU/memory/snapshot latency remain unverified. Network UI and deletion remain separate implementation boundaries.
