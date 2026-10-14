# Application-owned read-only network browsing

`DesktopSession` implements `INetworkBrowser` using the retained negotiated connection's borrowed `INetworkReader`. It starts one fresh list for each usable connection generation, including automatic recovery and manual Retry. It does not negotiate again, open a second connection, subscribe to events or own/dispose a borrowed reader. The existing session connection workflow owns recovery and transport disposal. Network event reconciliation, network desktop presentation and deletion are separate implementation boundaries.

## Snapshot and evidence

`NetworkBrowsingSnapshot` exposes connection generation, useful network rows, complete selected network ID, independent list/detail read states and separate original classified failures. `NetworksChanged` can arrive off the UI thread: consumers should dispatch a coalesced notification and read the latest immutable snapshot. Exceptions from network observers are isolated without logging their content.

Selection accepts only the complete ordinal ID of a current row, including retained rows during refresh/failure. Names, shortened IDs and case-insensitive discovery matches are display data, not request identities. Null or absent IDs clear selection. Reselecting the same ID retains an existing or explicitly cleared inspection. Refresh inspection reloads that selection; clear inspection immediately removes raw detail and leaves selection in NotRequested state. A list refresh preserves completed useful detail, fences older in-flight detail, reloads it if still requested, and never reopens explicitly cleared detail.

| Outcome | Snapshot behavior |
| --- | --- |
| Initial read or explicit list refresh | Loading, with useful current-generation rows/selection retained. HasRetainedRows distinguishes retained data; IsAuthoritative is false. |
| Successful list | Ready and authoritative; replace rows, deduplicate by complete ordinal ID in response order. An empty array means observed empty. Authoritative absence clears vanished selection and raw detail. |
| Non-transport list failure | Failed, preserving rows, selection and completed detail. Preserve failure kind, status and exact daemon message separately from safe description. |
| Selected Inspect success | Ready detail for exactly the requested ID. A mismatched returned ID becomes MalformedData without exposing its raw inspection. |
| Selected Inspect failure | Failed detail with its own failure; list failure stays independent. Current NotFound removes that row and clears selection/raw detail. Superseded NotFound has no effect. |
| Missing borrowed capability | Unavailable list/detail, no fabricated empty success and no reconnect. |
| Shared connection invalidation | Unavailable, with rows, selection and detail cleared before cancellation; replacement starts without old cached evidence. |

Missing/null metadata remains unavailable. Empty strings/maps, empty IPAM configuration and false internal status remain available values. IPAM itself, IPAM configuration/options/auxiliary maps and inspected attachment maps preserve null versus empty. Snapshot publication defensively copies row labels, inspection labels, IPAM collections/maps and attachment maps into read-only wrappers, including when a capability supplied mutable backing objects. Domain entries are immutable records. Raw Inspect remains the exact original text, including unknown/private fields, whitespace and numeric lexemes; it is never reconstructed from projections.

List rows deliberately carry no attachment-completeness claim. Inspected attachments describe the returned response only. Empty inspected attachments do not establish cluster-wide emptiness, visibility of every service/driver attachment, or deletion safety. This browsing capability cannot authorize deletion.

## Scheduling, fences and ownership

One network worker serializes list/detail work. A capacity-one bounded wake channel and one pending flag per request family coalesce bursts; no request queue grows with refresh or selection count. When list and detail are both pending, the scheduler alternates turns so a pending inspection receives admission between list turns. Network browsing has its own worker and does not take over the Containers, Images, Volumes, commands, events or Logs schedulers.

Every request receives cancellation and the session's bounded OperationTimeout. A separate deadline remains active after superseding cancellation, including if a reader ignores its token. Connection identity, generation, list/detail revisions, selected ID, request cancellation and deadline state fence both successes and failures. Refresh supersedes old list/detail results; selection/clear supersede detail only. Snapshots/revisions change before cancellation callbacks can reenter. A canceled obsolete transport response cannot begin competing recovery, remove a newer row or revive raw content.

A current transport-loss result or expired owned request signals the existing connection owner and invalidates shared state. The deadline promptly exposes unavailable state even while a non-cooperative reader remains outstanding. Access denial, malformed evidence, daemon failure, invalid request, conflict and NotFound retain their separate classifications without causing reconnect. A missing capability remains unavailable. Ordinary network refresh/selection/clear does not cancel unrelated readers, operations, events or Logs; actual shared transport loss follows their existing suspension/recovery behavior without mutation replay.

Disconnect, Retry, recovery replacement and disposal invalidate publication before cancellation, then asynchronously join the network worker together with all other connection-owned work before disposing the transport. A reader that never completes can therefore keep teardown pending; it cannot publish after its deadline or release the transport underneath active work. Repeated disconnect/disposal joins remain valid. Connection creation stays serialized while that join is pending.

Network identities, metadata, labels, addresses, attachments, raw inspection, original daemon messages and observer exception content are never automatically logged or transmitted. No logging or telemetry path was added.

## Simulated evidence and remaining validation

On 2026-10-15, the established executable harness passed **279/279 groups** on **macOS 26.7.1 with .NET SDK 10.0.402**. The full Release solution build passed with **zero warnings and zero errors**. No `.idd/verification.yaml` is configured; the repository's executable harness and solution build were used.

Thirteen new deterministic network browsing groups cover exact full identity, unavailable/empty metadata and IPAM/attachment evidence, exact raw inspection, copied nested immutable collections, successful/empty/missing-capability startup, retained rows during refresh/failure, authoritative disappearance and target NotFound, identity mismatch, all failure kinds and original messages, selection/refresh/clear races, cancellation callback ordering, 1,000-request bursts and alternating bounded admission, stale successful/failed completions, cooperative and non-cooperative deadlines, automatic recovery, generation replacement, and repeated joined disconnect/shutdown.

All-capability fixtures keep container/image/volume browsing, one event subscription, lifecycle/image operations and active Logs on the same retained connection. Successful network reads and explicit refresh do not cancel those capabilities or add negotiation/subscriptions/disposal. Loss from each shared family, events, commands, Logs and liveness invalidates network state while a delayed network read remains joined. Network-origin loss/deadline uses the same owner, joins the old connection, loads new authoritative state and restores Logs without replaying either command. Existing network protocol and simultaneous synthetic named-pipe fixtures remain independent evidence; see [network acquisition](network-acquisition.md).

These are controlled scheduling/ownership and build results. Real Windows named-pipe ACLs, non-elevated Docker Desktop/WSL2 operation, daemon restart behavior, driver/swarm attachment visibility, Windows delivery and workstation CPU/memory/snapshot-latency targets remain unverified. This task supplies no network UI, external-event reconciliation or deletion evidence.
