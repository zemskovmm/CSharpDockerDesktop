# Application volume synchronization

`DesktopSession` owns volume reconciliation on the connection owner's existing Engine event subscription. There is no volume-specific connection, negotiation, subscription, retry loop or event history. The shared observer retains only the precise timestamp high-water cursor and selected Engine identity; replay uses its existing fractional overlap and recovery policy. Duplicate observations and equal timestamps are never deduplicated.

## Observation and snapshot boundaries

The first volume list waits for observation establishment. Missing event capability, a classified non-transport opening failure or the existing opening deadline releases that wait and leaves manual browsing available. The opening deadline releases snapshot fallback even if the opener ignores cancellation. That outstanding opener remains owned: a late subscription is disposed and joined before the retained connection can be released. Classified observation failures remain visible through the shared connection snapshot.

A single pending volume flag bridges observations and reopened-stream requests consumed before the volume coordinator exists. The initial list does not consume that flag. An observation before or during the initial snapshot therefore leaves a subsequent authoritative read pending. Later lists consume observations already pending when they start; observations arriving during I/O leave another read pending. Useful list results can publish while another reconciliation is pending, so a sustained stream does not prevent progress.

Every `volume` action, including unknown actions, invalidates the list. No event inserts/removes rows or patches driver, scope, mountpoint, labels or raw inspection data. Event attributes and action strings are not retained by the volume coordinator. Other resource types retain their independent shared-subscription dispatch.

## Identity and inspection

Docker's volume actor identity is the full volume name in `Actor.ID`, exposed as `EngineEvent.ResourceId`; `Actor.Attributes["name"]` is not an identity fallback. The official Moby [volume store](https://github.com/moby/moby/blob/v28.5.2/volume/service/store.go) passes `v.Name()` to the [volume event emitter](https://github.com/moby/moby/blob/v28.5.2/daemon/events.go), which assigns it to `Actor.ID`. Attribute values cannot override that identity or supply authoritative metadata.

Names use full ordinal equality, preserving case, Unicode, punctuation and surrounding whitespace in otherwise usable names. The observer uses the established read-name usability bounds: null/empty/whitespace-only, over 4096 UTF-16 code units, exact dot segments, controls and unmatched surrogates are unusable. Such an identity conservatively invalidates any requested selected inspection. Usable unrelated identities need only invalidate the list. No trimming, shortening, prefix matching or image-reference interpretation occurs.

A matching or unusable actor identity immediately clears requested exposed inspection/raw JSON and failure content, advances the detail revision and schedules an authoritative replacement. Explicitly cleared inspection stays NotRequested. Reopening observation also invalidates requested selected detail, because any selected volume may have changed in the gap.

Event invalidation does not repeatedly cancel active work. The superseded request keeps its original deadline and connection ownership. Revision, generation, exact selection, cancellation and deadline checks reject its later successful result, identity mismatch or classified error, including NotFound. A still-current connection's TransportLoss result remains continuity evidence even when its detail content was superseded. A non-cooperative request exceeding its deadline invalidates exposed shared state promptly; its eventual response must still be joined.

## Evidence, scheduling and ownership

The [volume browsing evidence contract](volume-browsing.md) remains unchanged. A non-null warning-free current list establishes presence/absence; a warning-bearing or null collection cannot establish disappearance. Incomplete results update returned names and retain useful omitted rows. Exact warning order, duplicates, whitespace and null/empty distinctions remain immutable in `LatestListing`. A current exact-name Inspect NotFound can establish absence independently while leaving that latest listing untouched. A destroy/delete/prune observation alone never confirms a selected volume absent.

One worker serializes volume list/detail I/O. Capacity-one wakeups, pending flags and revisions bound scheduler state independently of event volume. Event-driven list turns wait for the shared configurable reconciliation batch interval. When list and requested detail are both pending, their turns alternate. Bursts cause one immediate detail-content invalidation notification and coalesced rereads, not one notification/read per event. Manual list and inspection refresh remain available.

Transport loss and request deadlines signal the existing connection owner and invalidate every borrowed family before cancellation. Reconnect obtains a fresh volume generation and empty in-memory snapshot, then repeats observation-before-snapshot synchronization. Replacement, disconnect and disposal fence publication first, cancel work and join reads/openers/subscriptions before connection disposal. No old raw content can publish into the replacement generation. Non-transport observation failures do not disable manual browsing. Volume observations do not initiate container/image reads or cancel their operations or relevant Logs.

Names, attributes, labels, mountpoints, warnings, raw JSON and original daemon messages remain sensitive in-memory caller data. This workflow adds no application logging, telemetry or external reporting.

## Simulated verification and remaining real checks

On 2026-10-14 the executable harness passed **193/193 groups** on macOS 26.7.1 with the established .NET SDK 10.0.402 environment. The Release solution build completed with **zero warnings and errors**. Eleven new deterministic volume synchronization groups use controlled connection/read/opening capabilities, bounded observation channels, event-consumption barriers and manually released batch/retry delays. They retain all earlier volume browsing, infrastructure, container/image reconciliation, operation and Logs coverage.

The new checks cover startup observation coordination and bridge-only invalidation; observations before/during initial and subsequent snapshots; duplicate/equal-timestamp/unknown-action delivery; exact full-name versus attribute/missing/unusable identity; immediate requested-content clearing and NotRequested preservation; obsolete non-cooperative successes and classified failures including NotFound; warning-bearing/null/complete collection disappearance evidence and exact retained warnings; 3000-observation burst coalescing, bounded buffering and serialized fair list/detail turns; classified opening failure, deadline fallback and reopening; precise reconnect overlap and generation fencing; event-invalidated deadlines/TransportLoss; independent container/image observations, lifecycle/image commands and active Logs on one subscription; and joined retry/disconnect/disposal.

These are simulated ownership/scheduling checks, not real Docker event captures or Windows runtime evidence. Real Windows named-pipe permissions, Docker Desktop/WSL2 volume-driver warnings and external volume event behavior, daemon restart/replay, workstation budgets and future volume GUI behavior remain outstanding. No volume presentation, dependency lookup or deletion is delivered here.
