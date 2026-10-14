# Borrowed container Stats acquisition

A negotiated connection exposes Application `IContainerStatsSource`. `SubscribeStatsAsync` requires the exact full container ID already obtained by inspection/listing. It performs an authoritative Inspect on its independent transport and requires `Inspect.Id` to equal that ID. It never resolves a name or prefix into another target. Invalid bounded identities fail before transport allocation; inspection disappearance remains `NotFound`.

This capability acquires raw counters only. DesktopSession metrics/history ownership, active-view selection, approximately one-Hz display, five-minute retention, derived measurements, charts and Stats UI remain subsequent work. There is no continuous stream per table row and no automatic reconnect/replay here.

## Ownership and negotiated transport

The capability borrows the selected connection. The caller owns `IContainerStatsSubscription.DisposeAsync`; disposing it leaves the selected connection usable. Connection disposal cancels and joins every Stats producer along with existing streams. Each Stats subscription owns a separately negotiated HTTP/pipe pool, following events/logs. Admission is separate from log/command admission; ordinary reads, commands and liveness retain their original sealed connection without renegotiation.

Before requesting/publishing Stats, the new pool validates the selected endpoint, a nonempty matching Engine ID, Engine version, platform metadata, complete API compatibility and transport generation. It requests `GET /v{negotiated}/containers/{escaped-full-id}/stats?stream=true`. It deliberately omits `one-shot`: API 1.40 has no such option, and later contracts restrict it to `stream=false`. A returned sample ID/platform, when supplied, must agree with the selected identity/platform. Missing sample IDs remain associated with the ID established by Inspect; missing platform evidence stays unknown when connection metadata also lacks it.

Opening includes negotiation, Inspect and response headers/body acquisition under a dedicated deadline. The ordinary short request deadline still bounds each handshake request, but neither that deadline nor the opening deadline limits a healthy established stream. A stalled/empty established body waits for explicit cancellation; it emits no invented samples or heartbeat measurements. Consumer cancellation of one channel read does not end the subscription: cancel the opening token or dispose the subscription.

## Raw evidence

Domain records and immutable maps/arrays preserve unsigned 64-bit counter precision, null/unavailable versus zero, and missing versus observed-empty collections. They retain current and previous CPU total/system counters, online/per-core CPU evidence, user/kernel counters, Windows processor count, read/preread timestamps, Linux memory usage/limit/cache/cgroup evidence, Windows commit/private working set, per-interface network counters and per-device/per-operation byte counters. Windows storage read/write byte/count counters remain separate from Linux Block I/O. No counter is converted through floating point, aggregated, or called filesystem disk-space usage.

Timestamps retain original RFC3339 text, signed Unix seconds and nanoseconds without fractional DateTime rounding. Missing/empty timestamps are unavailable. A daemon's year-one zero timestamp is preserved as historical raw evidence; it is not a current measurement. CPU totals have nanosecond units on Linux and 100ns units on Windows. Absent Linux system/per-core/memory/Block I/O evidence is not manufactured for Windows; Windows memory percentage/limit cannot be inferred from private working set.

All bounded `memory_stats.stats` keys are retained, including `cache`, `total_inactive_file` and `inactive_file`. These allow later Docker CLI cache-adjusted semantics to distinguish older Engines, cgroup v1 and cgroup v2. The Engine reference's illustrative formula uses `cache` for v1; current Docker CLI uses `total_inactive_file`, with `cache` used by Docker 19.03 and older. Acquisition makes no formula choice. See the [official CLI explanation](https://docs.docker.com/reference/cli/docker/container/stats/) and [CLI helpers](https://github.com/docker/cli/blob/v28.0.0/cli/command/container/stats_helpers.go).

## Completion, privacy and bounds

Read `Samples` until it ends, then inspect `Completion`. Completion occurs after the HTTP response and dedicated pool are closed. `CleanEnd` retains legitimate queued final samples; natural pool disposal does not invalidate its negotiated generation. `Canceled` covers caller token, subscription or owner disposal. `Failed` reports malformed samples, transport loss and other classified failures. Malformed/truncated tails retain already validated earlier samples with a failed completion; an incomplete final sample is never published. Transport loss, cancellation/disposal and supersession suppress queued output, including a read crossing cleanup. Generation is checked after callback detachment and on both sides of dequeue/availability, preserving the event/log cleanup-race fix. A change after completed cleanup suppresses later delivery without rewriting the historical completion result.

Failures distinguish `NotFound`, `AccessDenied`, `InvalidRequest`, `IncompatibleApi`, `DaemonError`, `MalformedSample`, `OpeningDeadline`, `TransportLoss` and `SubscriptionLimit`. Bounded original HTTP daemon content and status are deliberate caller data, with a truncation flag; descriptions contain no raw daemon payload. This feature adds no automatic logging or telemetry.

Default implementation tuning, validated with finite upper limits in `DockerConnectionOptions`:

| Allocation | Default |
| --- | --- |
| Concurrent Stats subscriptions per selected connection, including opening/completed subscriptions until disposal | 2 |
| Whole opening deadline | 10 seconds |
| Buffered samples per subscription | 8 |
| Individual JSON object | 256 KiB |
| Total retained collection entries per sample, across both per-CPU arrays, memory keys, interfaces and I/O entries | 1,024 |
| HTTP daemon error prefix | 16 KiB |
| JSON nesting depth / decoder read buffer | 64 / 4 KiB |
| Inspect response / exact identity length | shared 1 MiB / 4,096 characters |

A bounded `Channel<T>` waits when full. At most one decoded sample is pending delivery in addition to the channel, one bounded JSON object and the fixed read buffer; it does not accumulate read-ahead samples. Cancellation interrupts opening, reading and blocked delivery. Disposal joins the producer and transport, including a late response from a controlled noncooperative transport. Admission stays reserved until disposal even at clean EOF, bounding retained completed channels as well.

The incremental object framer accepts fragmented and consecutive JSON objects without requiring line separators. System.Text.Json validates syntax and a strict UTF-8 pass validates even unknown text fields. Unknown additive fields are tolerated inside the byte/depth bounds. Known fields with malformed types, invalid counters/timestamps, collection excess, oversized samples or partial JSON at clean EOF fail explicitly. EOF between complete objects (with optional whitespace) is clean. I/O exceptions are transport loss; no reconnect is attempted.

## Schema sources and stopped/paused containers

All seventeen [official OpenAPI documents from 1.40](https://docs.docker.com/reference/api/engine/version/v1.40.yaml) through [1.56](https://docs.docker.com/reference/api/engine/version/v1.56.yaml) were inspected. `Fixtures/Stats/contract-evidence.json` records each URL, SHA-256, request parameters, response statuses, response schema and typed root fields. The response is generic-object with examples through 1.47; typed Stats definitions start at 1.48; `os_type` appears at 1.52. `stream=true` is common across the full range. Platform fields also exist in [Moby's API-1.40-era types](https://github.com/moby/moby/blob/v19.03.0/api/types/stats.go). Independent synthetic Linux/cgroup and Windows fixtures follow these official examples/types rather than production serialization.

The CLI documentation describes live data for running containers and says stopped containers do not return data. The protocol must also tolerate Engine implementations returning empty/zero-date structures: [old collection code](https://github.com/moby/moby/blob/v19.03.0/daemon/stats/collector.go) publishes identity-only evidence for stopped/missing tasks, while [current daemon Stats code](https://github.com/moby/moby/blob/v28.0.0/daemon/stats.go) returns empty evidence for unavailable tasks and cleanly ends when collection closes. Neither absence nor zeros establish a running container. The Stats API specifies no separate paused-container status or guarantee of fresh advancing counters. The [Linux task collector](https://github.com/moby/moby/blob/v28.0.0/daemon/stats_unix.go) and [Windows HCS collector](https://github.com/moby/moby/blob/v28.0.0/daemon/stats_windows.go) obtain platform task counters; paused behavior can therefore stall or retain unchanged counters. This is an inference from the collection paths, not a promised paused-stream behavior. Future coordination must reconcile authoritative lifecycle state and freshness before presenting current measurements, especially on stop/pause. Acquisition neither rejects a paused container based on a guessed rule nor synthesizes activity.

## Verification and remaining limits

The dependency-free harness covers every negotiated API version; cgroup v1/v2 and Windows availability; missing/zero/empty/null evidence; unsigned maximum counters; additive fields; exact timestamps; one-byte/random fragmentation; consecutive objects; malformed types/UTF-8, truncation, sample/depth/collection limits; slow-consumer backpressure; admission; opening/read/blocked-delivery cancellation; clean EOF draining; owner/stream generation changes; and joined disposal. The existing gated cleanup tests now exercise Stats for dequeue/availability races before and after cleanup, including replacement after callback detachment without consumer participation.

Controlled named-pipe HTTP uses four independent pools with Stats, logs and events open together while all resource families, mutations, dependency requests and liveness run on the selected ordinary connection. Request counts establish four handshakes total and one request per live stream. This tests actual SocketsHttpHandler framing, escaped negotiated requests and transport cleanup on the local test host; it is not a real Docker daemon.

Real Windows named-pipe permissions and Engine restart, Docker Desktop WSL2, Windows process/Hyper-V isolation, running/stopped/paused lifecycle behavior, live cgroup variants, long-duration memory/CPU profiles and comparison of future derived values against Docker CLI still require real Windows/Docker runs. No Stats UI or graphical verification is claimed by this acquisition task.
