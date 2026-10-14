# Volume deletion presentation

`VolumeBrowserView` and `VolumesPage` use the existing Application-owned
`IVolumeDeletionPreparation` and `IVolumeOperations` services. Presentation does
not obtain a Docker connection, issue commands, request force, remove containers,
or replay an uncertain mutation. The accepted operation and its reconciliation
remain owned by `DesktopSession` after selection changes, navigation and page
disposal. The session's retained operation appears when Volumes is reopened.

## Explicit preparation and confirmation

Delete is available from the volume list and details, including the Delete key
when focus is outside an editable text field. Opening it captures the entire
ordinal volume name and connection generation and starts a new dependency
preparation. The confirmation prominently warns that deleting the volume can
permanently remove data and cannot be undone. It explains that prechecks cannot
prove safety or close the race with a daemon dependency decision.

| Evidence | Presentation and admission |
| --- | --- |
| Loading | Fresh running/stopped dependency lookup is pending; Delete disabled. |
| Complete, empty | Explicit successful empty query; no claim of safety; confirmation available. |
| Complete, relationships | Every found relationship is accessible, including stopped containers. |
| Incomplete | Found relationships and unverified candidate IDs remain separate accessible rows. Missing mount evidence and possible additional relationships are explained; the user may explicitly confirm non-force deletion. |
| Unavailable | Unavailable candidate evidence cannot authorize deletion. Any retained unverified IDs remain accessible. |
| Failed | Classified failure and HTTP status appear without private daemon text; Delete disabled. Original failure evidence requires deliberate reveal. |
| Invalidated | Delete disabled; found evidence may remain visibly historical. Cancel and a new explicit precheck are required. |

Dependency lists use finite viewports and recycling `ItemsStackPanel`s. A lazy
collection projects found relationships and unverified candidates without building
one display string or visual for every dependency. Visible previews are bounded;
deliberate selected-row copy preserves full IDs, all available names, original
name/state and running flags. No found relationship is dropped from either
collection. Null and empty metadata remain distinct.

Cancel is the dialog default and receives initial focus. Closing restores the
invoking control when usable, otherwise details Back or the selected volume row.
The exact completed preparation ID/revision and target are captured for
confirmation. Submission re-reads the live preparation and resource state, then
consumes that confirmation by reference identity before calling Application.
Application performs the final atomic admission check. A second invocation,
reentrant notification, completed dialog callback or former page lifetime cannot
submit the request again.

Selection changes never change the captured target: they close the presentation.
Connection generation changes, unavailable/disappeared resources, lost detail
ownership and preparation invalidation disable or dismiss it. Merely returning
to the old selection never renews authorization. Cancel/close/navigation fence
callbacks immediately at CloseButtonClick/Closing, before close-animation completion, cancel the preparation token, close only its matching
preparation ID and asynchronously join borrowed work. A late non-cooperative
acquisition cannot restore the dialog, close a replacement preparation or dispatch
deletion. Navigation does not wait synchronously for the join. Once admitted, the
operation uses the session lifetime rather than the canceled presentation token.

## Retained operation evidence

Ordinary status distinguishes pending command, reconciliation, original command
disposition/status/classified failure, admission rejection, interruption,
authoritative present/absent observation, invalidated observation and unresolved
outcome. Acknowledgement is never labeled deletion. Presence by volume name does
not establish identity continuity: a name may have been recreated. Neither
presence nor absence establishes which command caused the observed state.

Dependency conflicts explain non-force refusal, show refreshed evidence and
require a fresh explicit precheck and confirmation for another attempt. The
client neither retries nor treats conflict-refreshed dependencies as permission.
Incomplete/unavailable/failed/invalidated refresh states remain explicit.

Original precheck relationships, refreshed conflict relationships, original
daemon messages/error bodies, private failure descriptions and observation
warnings remain discoverable through deliberate Show/Hide. The retained
dependency source selector labels both collections historical. Warning ordering,
duplicates, whitespace, null and empty content are preserved. Ordinary status
contains classifications rather than arbitrary daemon/failure text. No evidence
is automatically logged, exported or transmitted.

Private reveal preparation runs away from the UI thread. Immutable evidence
identity, reveal revisions, page epochs and live service reads fence queued
reveal/copy callbacks. Changing/hiding evidence or disposing a page cannot publish
obsolete data. Notification coalescing shares the browser's bounded UI dispatch.
Existing search, volume warning distinctions, Overview, raw Inspect, deliberate
copy and table/Inspect virtualization remain in place.

## Verification scope

Deterministic checks compile the actual Presentation state without a graphical
host and use controlled Application contracts and a queued dispatcher. The smoke
host supplies only synthetic data and has no Docker access. It includes explicit
dependency/operation scenarios and an actual Uno-control interaction sequence.
Native keyboard checks are separate from programmatic control activation.

Real Windows Win32 behavior, Windows clipboard/screen-reader integration, named
pipe ACLs, Docker Desktop/WSL2 deletion/events, real dependency races and workstation
performance targets require a Windows/Docker environment. macOS synthetic checks
do not establish those properties.

## Recorded synthetic evidence — 2026-10-14

On macOS 26.7.1, using the isolated .NET SDK 10.0.402 environment, the complete
existing executable harness passed **255/255 groups**, including **15 added
volume-deletion presentation groups**. These cover preparation cancellation,
exact ordinal names and completed revisions, running/stopped relationships,
complete-empty/incomplete/unavailable/failed evidence, full private copy and
superseded copy cancellation, duplicate/reentrant submission, selection changes,
queued event/generation/resource invalidation, slow non-cooperative navigation
and joined cleanup, stale dialog/reveal/copy callbacks, every admission rejection
and interruption, conflict refresh, lost response/no replay, name recreation,
retained operation evidence on reentry, 30,000 dependency rows and existing
Inspect coexistence. Release solution and smoke-host builds both completed with
**zero warnings and errors**; self-contained `osx-arm64` smoke publication also
succeeded. No project verification policy is configured.

The smoke host's **Run volume deletion UI checks** sequence passed all **14
synthetic Uno/macOS interaction groups**, using actual page controls, standard
Uno automation providers, real ContentDialogs, keyboard-focus assertions and
recycling viewports. It exercises cancellation during slow preparation; complete
running/stopped, incomplete, unavailable and failed evidence; exact case and
whitespace/Unicode names; default Cancel and invoking-control focus restoration;
duplicate submission; selection changes; event/generation invalidation during
confirmation; navigation during a non-cooperative precheck; conflict-refreshed
relationships and recovery text; lost response without replay and retained
private evidence after reopening; acknowledged-but-present recreation text; and
coexistence with Overview/Inspect and Delete in details. This local viewport
realized **4 of 30,000 dependency rows**, with the final unverified candidate
reachable, and **1 of 20,000 browsing rows**. These counts establish bounded
realization for the fixture, not a performance benchmark. The scenario chooser
also exposes complete-empty evidence, target disappearance and not-dispatched
outcomes for interactive inspection.

A separate native AppKit keyboard check used Tab to reach the volume list,
Down to select the case-distinct `volume-exact-name`, and Delete to open a new
precheck. The completed dialog visibly showed the exact quoted name, generation,
full running/stopped container IDs and states, the data-loss warning and the
precheck race explanation without clipped text. Cancel had the focus outline;
Enter canceled the dialog, restored the selected-row focus outline and left
“No volume deletion in this session.” No command was dispatched. The AppKit
accessibility tree was incomplete after dynamic navigation, so screenshots and
native forward keyboard traversal supplied this additional evidence. Native
screen-reader semantics and Windows keyboard/clipboard behavior remain unverified.

During development, a full harness run transiently failed the existing
`EventChecks.SchemasAndFragmentation` assertion. The subsequent full runs passed
255/255; no event protocol code or test expectations were changed. Graphical QA
also exposed fixed-width dialog clipping and page button bindings that did not
survive the local Uno loading lifecycle. The final dialog uses constrained
wrapping and idempotent bindings to loaded page controls; the final complete
interaction sequence passes through those controls. The smoke driver scopes
lookups to the current page and waits for dynamically revealed controls.

These are synthetic capabilities and local macOS graphical observations. They
provide no real daemon deletion, Windows delivery, Windows named-pipe ACL,
Docker Desktop/WSL2 dependency/event, clipboard-content, screen-reader, DPI or
workstation CPU/memory/latency evidence. Those checks remain outstanding.
