# CSharpDockerDesktop

A C#/.NET 10 Uno Skia Desktop client for an existing local Docker Engine. Windows x64 is the delivery baseline. The desktop opens without Docker or Administrator privileges and provides keyboard-accessible Containers, Images, Volumes, and Networks pages. At launch it asynchronously connects to the default local Windows pipe, displays actual Engine metadata, monitors readiness and recovers after unavailability or transport loss. Containers now provides a virtualized browser, responsive search, manual refresh, full-ID copying, Overview details, searchable raw JSON Inspect and bounded recent/live Logs through that retained connection. Container events reconcile authoritative browser state. Start, Stop, Restart and confirmed Delete actions expose pending work and retained authoritative outcomes through Application-owned coordination. Ordinary deletion and explicit dangerous Force Delete preserve volumes. Images provides virtualized browsing, Overview, raw Inspect and confirmed non-force deletion with retained command/observation evidence. Volumes provides virtualized search, exact-name copying, Overview and raw Inspect with retained incomplete-list evidence and deliberate daemon-warning reveal, plus confirmed non-force deletion after fresh running/stopped dependency preparation. The retained connection also supplies fresh read-only volume dependency acquisition and borrowed non-force volume deletion. Application-owned volume deletion coordination consumes exact preparation evidence and retains authoritative outcomes. Networks and other live tools remain pending.

## Prerequisites

- Windows x64 with the .NET 10 SDK. `global.json` selects stable .NET 10 and pins Uno.Sdk 6.7.30.
- Internet access for the initial NuGet restore. Desktop-only builds need no Android, iOS, or WebAssembly workloads.
- A graphical desktop session to launch the UI. Visual Studio with Uno support is optional; the CLI is sufficient.

## Restore, build, and launch

Run from the repository root:

```sh
dotnet restore CSharpDockerDesktop.slnx
dotnet build CSharpDockerDesktop.slnx --no-restore
dotnet run --project src/CSharpDockerDesktop.Presentation -f net10.0-desktop --no-build
```

Use Tab to reach navigation items, arrow keys to move among them, and Enter/Space to select a page. Docker Engine status remains visible while navigating. In Containers, Tab reaches search and refresh, arrow keys move through rows, and Enter opens Overview for the selected container. Tab also reaches state-aware Start, Stop, Restart, Delete and dangerous Force Delete controls. Delete confirmation captures the full identity and connection generation, defaults focus to Cancel, and states whether force is enabled; neither choice removes volumes. Operations continue across navigation, with command-pending, reconciliation and retained observed/unresolved outcomes displayed independently of selection. Original operation daemon content is shown only by deliberate button activation. Details has keyboard-accessible Overview/Inspect/Logs controls, refresh, full-ID copy and Back to containers. Inspect provides search over collapsed content, a virtualized JSON list, Expand/Collapse selected, and explicit Copy selected path/value buttons. Arrow keys select nodes; Right/Left expand/collapse a selected object or array, and Enter toggles it. Paths and sensitive-copy behavior are documented beside the viewer. Search covers loaded names, short/full IDs, images, state/status and available health; missing metadata is shown as unknown. Logs uses a virtualized bounded window, retained-output search, Older/Newer matches, stream selection where distinguishable, explicit Autoscroll, local Clear, and a live-follow preference applied on reopening. Closing Logs or leaving details/navigation immediately ends ownership and joins asynchronous cleanup. Tab also reaches Retry connection and the expandable connection details. Diagnostics distinguish unavailable pipes, permissions, compatibility, invalid responses and daemon/transport errors. A daemon message is shown only when you choose Show daemon message. Test all four pages with Docker absent/stopped and without elevation; start/stop/restart the Engine while the GUI stays open.

The host explicitly uses Uno's Win32 shell on Windows. Its AppKit shell is enabled for local UI development checks on a Mac with a .NET 10 SDK; this does not establish a macOS delivery contract.

## Produce the Windows x64 application

On a Windows x64 machine with the .NET 10 SDK:

```sh
dotnet publish src/CSharpDockerDesktop.Presentation/CSharpDockerDesktop.Presentation.csproj -c Release -f net10.0-desktop -r win-x64 -p:PublishProfile=win-x64 -o artifacts/win-x64
```

Launch `artifacts/win-x64/CSharpDockerDesktop.Presentation.exe`. Distribute the complete output folder; the profile is self-contained, without trimming, NativeAOT, or single-file packaging. No installer or update mechanism is selected. Windows publish and runtime behavior must be verified on Windows; a Mac build alone cannot establish them.

## Responsibilities

- **Presentation**: Uno host, navigation, views, and display text; calls Application services.
- **Application**: connection contracts and observable session coordination, cancellation, recovery and lifetime ownership.
- **Domain**: endpoint identity, actual Engine metadata, negotiated API ranges, capabilities and distinguishable failures, independent of the UI framework.
- **Infrastructure.Docker**: thin asynchronous Engine handshake and retained-transport liveness using HTTP over the local Windows named pipe; no Uno dependency.

Application depends on Domain; Docker Infrastructure depends on Application/Domain; Presentation calls Application; its App composition root constructs the Infrastructure client. Healthy sessions check readiness on the retained transport rather than creating new handshakes. Recoverable failures use bounded retry bursts and a cooldown, continuing until recovery or shutdown. Manual retry cancels superseded work. Explicit disconnect cancels and joins owned work, and a later Start obtains a fresh connection. The [Application container browser](docs/container-acquisition.md#application-browsing-state) maintains in-memory snapshots and selected inspection with bounded cancelable reads. The Containers screen consumes the Application browsing and operation contracts, retains selection by full identity, cancels details when closed, and rejects notifications queued before navigation. Deletion confirmations are single-use and invalidated by selection, state, disappearance, generation, disconnect or page changes; accepted operations stay session-owned. Images integrates Application-owned browsing and non-force deletion; Volumes integrates Application-owned read-only browsing and shared automatic updates; Networks remains a placeholder; the Application Logs session supports explicit bounded reading and recovery while its presentation provides retained plain-text search, stream filters, combined TTY output, live follow, autoscroll and local Clear; Terminal and Stats remain pending. Read [container presentation and desktop verification](docs/container-acquisition.md#container-presentation) for delivered behavior and evidence limits.

## Engine connection client

`IEngineConnectionClient.ConnectAsync` opens an explicitly supplied local Windows named pipe, or defaults to `npipe://./pipe/docker_engine` without Docker CLI discovery. It checks readiness, discovers the daemon version/API range, negotiates client API **1.40–1.56**, and fetches Engine information using that negotiated version. A successful `IEngineConnection` retains the HTTP transport; its owner must dispose it when replaced or on shutdown. Failed and canceled attempts release the transport.

The maximum is supported by focused handshake and implemented resource-schema checks, not a claim that all resource APIs are implemented. The retained connection also provides borrowed read-only volume listing (including daemon warnings) and exact raw inspection; [volume acquisition and API 1.40–1.56 evidence](docs/volume-acquisition.md) documents its contract and simulation limits. [Application-owned volume browsing](docs/volume-browsing.md) adds immutable snapshots, retained partial-list evidence, selection, bounded inspection and shared recovery. [Volume reconciliation](docs/volume-reconciliation.md) and [volume browsing presentation](docs/volume-browsing.md#read-only-volume-presentation) use shared automatic updates. [Borrowed volume dependency acquisition](docs/volume-dependencies.md) obtains fresh running/stopped container evidence with exact inspected mount matching; the [session-owned preparation capability](docs/volume-deletion-preparation.md) retains cancellable confirmation evidence with event/generation invalidation. The [borrowed non-force volume deletion capability](docs/volume-commands.md) and [Application-owned volume deletion coordination](docs/volume-operations.md) preserve preparation, command and observation evidence; [volume deletion presentation](docs/volume-deletion-presentation.md) now adds data-loss warnings, cancellable fresh prechecks, explicit confirmation and retained operation evidence. The retained connection now also provides borrowed read-only image listing and inspection; [image acquisition and API 1.40–1.56 evidence](docs/image-acquisition.md) documents its protocol contract. The [borrowed non-force image deletion capability](docs/image-commands.md) is available; its [session-owned coordination](docs/image-operations.md) and [confirmed deletion presentation](docs/image-browsing.md#image-deletion-presentation) retain command evidence separately from authoritative observations. The client exposes optional metadata as unknown when absent. It separates daemon HTTP status/messages from missing pipe, access denial, unsupported endpoint/platform, incompatible API, malformed response and transient failure. Cancellation propagates normally. It does not log raw responses or discover environment/context precedence.

Read [borrowed read-only container logs, bounded framing and lifetime evidence](docs/container-logs.md); Read [Application Logs sessions, bounded page reads and recovery](docs/container-log-sessions.md); Read [Logs presentation, bounded retained search and graphical evidence](docs/container-log-presentation.md). Read [borrowed lifecycle commands, uncertainty and lifecycle compatibility evidence](docs/container-commands.md) and [borrowed image deletion, removal evidence and verification limits](docs/image-commands.md). Read [connection boundary, compatibility evidence and Windows smoke procedures](docs/engine-connection.md) for usage and limits. To run independent checks without Uno, Docker, or external test packages:

```sh
dotnet run --project tests/CSharpDockerDesktop.Connection.Tests
```

This executable suite returns a nonzero exit code on any failure. Its controlled named-pipe HTTP server exercises real `SocketsHttpHandler` framing and transport ownership on supported .NET hosts. Real Windows Docker/ACL smoke evidence is still required; a passing Mac fixture does not establish Windows compatibility.
