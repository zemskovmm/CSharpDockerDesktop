# Terminal surface feasibility research

Research date: 2026-10-15. This is evidence for the unresolved IDD-0013 spike, **not a durable component decision or IDD-0008 conformance claim**. Production navigation, dependencies and exec/session ownership are unchanged.

## Recommendation

Continue evaluating **bundled xterm.js inside Uno's Skia WebView2**, with an opaque sandboxed terminal frame and a narrow in-process bridge. It is the most credible low-porting-cost surface examined for the required Win32 host. The isolated runnable prototype is `tools/TerminalSurfacePrototype`; it references the existing Application terminal boundary and implements a synthetic echo fixture. It has no Docker access or command default.

Do **not** promote this prototype into production yet. Required Windows visual/input evidence and real Windows Engine interaction are unavailable. The stock pinned Uno WebView environment does not expose the documented option that prevents automatic WebView2 crash-dump upload. The isolated source-patch experiment below now demonstrates option propagation and a rebuilt host for both Windows backends, but has no Windows runtime, crash or network evidence. The Windows privacy blocker therefore remains open. Resolve those runtime observations and runtime-distribution obligations before any sensitive real-session trial. A narrowly maintained Uno upstream change/fork remains viable at source/build level; a C# terminal core with a purpose-built Uno renderer remains the browser-free alternative. A plain-text Logs view cannot substitute for this work.

## Comparison

“Documented/source” means upstream evidence; “observed” means this prototype run. No candidate received a Windows runtime test in this investigation.

| Candidate | Required Win32 Uno Skia fit | Terminal behavior and integration work | Retention / offline / licensing | Assessment |
| --- | --- | --- | --- | --- |
| xterm.js 5.5.0 in Uno WebView2 | Pinned Uno source contains an HWND WebView2 backend, with WebView2Aot default on .NET 10. It is not a claim based on Uno WebAssembly support. macOS uses WKWebView, a different engine. | Byte-oriented `write(Uint8Array)`, parser callbacks, `onData`/`onBinary`, selection, paste and resize APIs. ANSI/VT, fragmented UTF-8, control bytes, copy/paste and explicit resize observed on macOS. C# must serialize delivery, enforce revocation, manage clipboard and forward dimensions to Application. IME, key bindings, browser focus and accessibility need Windows testing. | Local JS/CSS with no third-party runtime JS dependencies; finite scrollback is configurable. Row limits alone do not bound every parser/glyph allocation. MIT xterm notice; Apache-2.0 Uno; additional WebView SDK/binding notices and separate proprietary Edge runtime terms. Offline Windows requires an installed runtime or explicit offline runtime distribution. | Best prototype candidate; production privacy and Windows validation remain open. |
| XtermSharp core + new Uno/Skia renderer | .NET Standard 2.0 core is UI independent; supplied frontends are Cocoa and Terminal.Gui, not Uno Skia Win32. A new renderer/input/selection/clipboard/accessibility implementation is required. | Upstream describes VT100/Xterm parsing and byte `Feed`; keyboard/mouse handling exists in supplied frontends. README identifies incomplete grapheme handling and missing scrollback/reflow. Those frontend claims do not demonstrate Uno behavior. No prototype here. | MIT; core references NStack.Core 0.12.0 (upstream license text is BSD three-clause; exact package/source notice review required if adopted). Can distribute offline without a browser runtime. Bounded renderer/history/parser ownership would be our responsibility. | Credible fallback if WebView privacy cannot be resolved, but substantially more product-quality UI work. |
| Microsoft Windows Terminal `TermControl` | Native C++/WinRT Windows XAML control; source depends on `Windows.UI.Xaml`. This is not the required Uno Skia HWND surface. No supported drop-in Uno adapter was established. | Mature terminal rendering, selection, input and resize in Windows Terminal do not prove embeddability in this app. Would need a native control/connection adapter, hosting, packaging and lifecycle work; do not replace Docker exec with a local ConPTY shell. No prototype here. | MIT repository license with third-party notices, native binaries, fonts and build/distribution obligations to audit. Offline is possible in principle but no compatible packaged control was established. | Useful behavior/source reference; poor immediate fit without a separate native-host investigation. |

Primary comparison sources, inspected on the research date:

- [Pinned Uno 6.7.135 WebView documentation](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/doc/articles/controls/WebView.md), [Win32 provider and backend](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/WebView/Win32NativeWebView.cs), [WebView2Aot backend](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/WebView/Win32NativeAotWebView.cs).
- [xterm 5.5 API declarations](https://github.com/xtermjs/xterm.js/blob/5.5.0/typings/xterm.d.ts), [MIT license](https://github.com/xtermjs/xterm.js/blob/5.5.0/LICENSE), [security guidance](https://xtermjs.org/docs/guides/security/), [combining-character storage](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/buffer/BufferLine.ts). The version is an intentionally pinned experiment, not a claim that 5.5.0 is the newest release or approved for production.
- [XtermSharp README](https://github.com/migueldeicaza/XtermSharp), [core project](https://github.com/migueldeicaza/XtermSharp/blob/master/XtermSharp/XtermSharp.csproj), [license](https://github.com/migueldeicaza/XtermSharp/blob/master/LICENSE), [NStack license](https://github.com/tui-cs/NStack/blob/develop/LICENSE.md). These moving source references describe the alternatives reviewed, not dependencies introduced here.
- [Windows Terminal control source](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControl.cpp), [license](https://github.com/microsoft/terminal/blob/main/LICENSE), [third-party notices](https://github.com/microsoft/terminal/blob/main/NOTICE.md).

## Prototype boundary and finite budgets

The separate `net10.0-desktop` C# executable uses `UseWin32()` and `UseMacOS()`. It enables `WebView` only in its own project and is intentionally excluded from the production solution/navigation. Presentation calls `IContainerTerminal`; the controlled implementation supplies the same session IDs, raw byte delivery, input, resize and close shape. It never starts a process, chooses argv, changes Docker state, or treats Ctrl+C as a lifecycle command.

Output uses a four-slot bounded `Channel<byte[]>`. Fixture chunks are at most 4096 bytes (sustained data is batched in 128-line chunks); a blocked producer awaits capacity. The bridge has at most one output chunk awaiting xterm's parsing acknowledgment. There is no timer feeding an unlimited JS write queue. Input uses 16 slots of at most 4096 bytes; overflow is visibly rejected. Retained screen history is 200 scrollback lines plus a fixed 80×24 or 100×30 viewport. Copy is explicit and capped to 4 KiB; paste is explicit and rejects UTF-8 input above 4 KiB.

A **1 MiB cumulative output budget per fixture** additionally closes and disposes the terminal. This is deliberately a research limit: it prevents a single cell's combining-character string or partial escape payload from growing for an unlimited session. It is not a proposed production “end the user's shell after 1 MiB” policy. A production renderer must bound pathological cell/parser/link state without silently changing the accepted interactive contract. A finite row count alone is insufficient evidence of bounded memory. Browser engine caches and process RSS still require measurements.

Closing/replacing cancels delivery, revokes the identity, closes the Application boundary, joins the producer/pump, and disposes the old terminal before starting the next one. Both C# delivery and JS acknowledgment check identities. An old queued write cannot target the new terminal object. Repeated close/reopen is supported. This fixture does not establish that a real exec process exits when its client closes.

## Content isolation and privacy

All JS/CSS are bundled resources, loaded using one in-memory HTML document. There is no HTTP listener, WebSocket, localhost service, CDN, browser-to-Docker proxy, generic URL launcher or generic host-object API. Container bytes cross as base64 encoded JSON and go only to xterm's byte parser, never `innerHTML`, `eval` or a document title.

The terminal runs in `srcdoc` with `sandbox="allow-scripts"`; it lacks same-origin, popup, top-navigation, custom-protocol, forms and download permissions. The trusted outer document permits no remote frames or connections. Both documents use a restrictive nonce CSP. The outer bridge checks the source window, message size and a closed message vocabulary. Native navigation allows only the exact initial in-memory document during bootstrap and cancels subsequent navigation; new-window requests are handled without launching a URL. OSC 8 links have an inert handler; OSC 52 clipboard operations are consumed. Clipboard writes require a pending explicit Copy action for the current generation.

This frame isolation matters: pinned Uno's [macOS native source](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOWebView.m) handles `mailto` and `tel` before the usual navigation event. Native cancellation alone was therefore rejected as sufficient isolation. [HTML sandbox rules](https://html.spec.whatwg.org/multipage/iframe-embed-object.html#attr-iframe-sandbox) and [CSP frame restrictions](https://www.w3.org/TR/CSP3/#directive-frame-src) describe the added controls. Windows enforcement still needs direct testing.

No application logging provider, telemetry service or automatic crash reporter is initialized. xterm logging and native developer tools are disabled, including Debug builds. The prototype keeps optimization enabled to exclude Uno's development-server/hot-reload runtime assets. Status reports contain counts, dimensions and fixed booleans, never payloads. The [pinned macOS managed backend](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Controls/WebView/MacOSNativeWebView.cs) can log script arguments when Debug logging is enabled; enabling a provider later would require explicit category filtering and tests. The development fixture must contain synthetic data only.

**Windows privacy remains unverified:** Microsoft's [privacy documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/data-privacy) identifies automatic minidump reporting and the documented custom-reporting option. The experiment below sets that option before creation and installs no uploader. Application logger settings, CSP and developer-tools settings do not establish browser diagnostic behavior. No unsupported Chromium switch is used. macOS and cross-build checks cannot establish Windows privacy.

## Windows environment adaptation experiment

**Result: feasible source/API/build adaptation; Windows privacy and terminal conformance unresolved.** `tools/TerminalSurfacePrototype/WindowsHost` supplies a checksum-pinned source preparation script, a two-file Uno patch, experiment-only policy glue, a narrow host build, focused checks and an isolated cross-publish procedure. It rebuilds the existing Win32 host against the prototype's pinned Uno binaries; it does not replace the UI stack, alter production dependencies, modify installed packages, or install a runtime. Ordinary prototype builds now refuse Windows UI startup unless the patched host's exact protocol marker is present. The marker is a compatibility gate, not a cryptographic trust boundary against modified local binaries.

### Pinned host/API evidence

The pinned [FeatureConfiguration source](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI/FeatureConfiguration.cs) exposes developer-tools, SSO and browser arguments, without custom crash reporting. Uno's [environment wrapper](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI/UI/Xaml/Controls/WebView/Core/CoreWebView2Environment.cs) explicitly states that `CreateAsync` is unimplemented. Passing an application-created native environment through the ordinary public wrapper is therefore unavailable in this pin. Changing settings after `EnsureCoreWebView2Async` would be too late.

| Windows backend in Uno 6.7.135 | Pinned creation path | Experimental pre-creation option |
| --- | --- | --- |
| `webview2aot`, default on .NET 10 | [Win32NativeAotWebView](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/WebView/Win32NativeAotWebView.cs) calls `CreateCoreWebView2EnvironmentWithOptions` through WebView2Aot 1.5.0. | `put_IsCustomCrashReportingEnabled(BOOL.TRUE)` and checked HRESULT, then getter verification, on the actual options object supplied to creation. |
| `microsoft.web.webview2`, explicitly supported by the [provider](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/WebView/Win32NativeWebView.cs) | Aliased Microsoft.Web.WebView2 SDK 1.0.4191.47 calls `CoreWebView2Environment.CreateAsync(..., options: options)`. | `IsCustomCrashReportingEnabled = true`, checked before the same options object is supplied to creation. |

Microsoft's [options3 contract](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2environmentoptions3) documents the upload behavior and its false default; it and [environment11 dump-folder support](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2environment11) were introduced in SDK 1.0.1518.46. The [WebView2Aot options implementation inspected](https://github.com/smourier/WebView2Aot/blob/b326911d90fb37b1de4e76f64263b0aa63bd62b1/WebView2/Manual/CoreWebView2EnvironmentOptions.cs) exposes options3. That upstream inspection commit is not claimed as the package's source revision: NuGet 1.5.0 metadata records `20260611105545`, rather than an immutable Git SHA. The package content hash remains locked, and its actual options type compiled and executed in the checks.

### Initialization and failure behavior

1. Before the Uno host or any WebView is constructed, the prototype requires `TerminalResearchWebViewPolicy.Protocol == uno-6.7.135-custom-crash-v1` and invokes `Prepare`. The stock package, missing assembly, incompatible marker or preparation failure returns exit code 2 with a fixed diagnostic. No default browser environment is created on that route.
2. Preparation rejects all inherited `WEBVIEW2_*` environment overrides, enabled SSO and additional browser arguments. It permits only the two backend names above, selecting `webview2aot` for an empty selector. The patched provider also throws for unknown names and missing managed backend assemblies, rather than using its original unknown-selector fallback.
3. Each backend queries the available browser before creation. Stable numeric versions below the pinned WebView2Aot compatibility floor **149.0.4022.49**, absent versions and prerelease suffixes are rejected. This conservative experiment floor comes from that package's `CORE_WEBVIEW_TARGET_PRODUCT_VERSION`; it is not the earliest runtime supporting custom crash reporting. SDK default target-compatibility behavior is preserved. No older-runtime path is added.
4. Exactly one environment may claim the fresh profile. Both option factories enable and read back custom reporting and exclusive user-data-folder access **before** their respective environment creation call. Failed setters/getters or a second claim abort initialization. No retry creates default options or changes backend.
5. Before controller creation, the returned environment must report the owned profile and a dump folder within it. Missing required interfaces, an overridden profile, or an external dump location aborts. The browser-exit handler is registered before the controller is opened. Both asynchronous initialization paths complete their task with failure when these checks throw; the managed path no longer leaves its constructor waiting indefinitely on an uncompleted failure task. Runtime calls that never return still have no constructor timeout in this pin.
6. No dump reader, upload endpoint, reporting SDK or `ProcessFailed` upload handler is added. The only dump-related operation is eventual removal of the owned profile. The xterm assets, sandbox, CSP, navigation guard and synthetic Application boundary are preserved. There is no real Docker connection.

### Local retention and joined cleanup

Preparation creates a fresh `%LOCALAPPDATA%\TerminalSurfacePrototype\WindowsResearch\<GUID>` per process. Exclusive access and the fresh path avoid intentionally joining an existing browser profile. User-data and dump paths are checked against this owned folder; the check does not assert that a browser creates no other files on the machine.

On a normal Windows close, `AppWindow.Closing` is canceled while cleanup runs with the UI event loop alive. The prototype revokes and joins its synthetic output/producer/input delivery, closes the tracked controller on the UI thread, waits up to ten seconds for `BrowserProcessExited`, then attempts profile deletion on a worker thread. Microsoft's [browser-exit contract](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2environment5) defines that event after the browser's associated processes release resources. Its [UDF guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder) also requires ending the session and waiting before deletion. The Closing cancellation path is supported by the pinned [Win32 window source](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Window/Win32WindowWrapper.cs).

A close failure, missing exit evidence after a creation attempt, exit timeout, or deletion error retains the profile and produces a fixed retention diagnostic. The window then closes; it does not claim successful erasure. Startup failure before a normal window exists, force termination, an application/OS crash, or power loss can also leave the whole profile, including local minidumps, indefinitely. There is no automatic scavenger, disk quota, secure erase, backup/snapshot removal, or deletion of dumps outside the owned folder. Previous stock-Uno `ApplicationData.Current.LocalFolder/WebView2` profiles are not touched. Inspect retained synthetic artifacts manually only after all related browser processes have exited. No sensitive sessions are authorized by this cleanup experiment.

Custom crash reporting does not disable dump creation or every diagnostic channel. Microsoft's privacy documentation separately describes required diagnostics and Windows reporting. CSP covers document requests, not all browser background traffic; host/OS/third-party crash collectors and SmartScreen behavior also need review on the named Windows machine. A configuration getter proves the supplied value, not actual runtime uptake or an absence of transmissions.

### Reproducible build and focused checks

Versions: Uno.Sdk **6.7.30**; Uno packages **6.7.135**, source **da217df2312e9e39676880f9cb4b8d519cc297e0**; WebView2Aot **1.5.0**; DirectNAot/Extensions **1.6.1**; Microsoft.Web.WebView2 **1.0.4191.47**. The rebuilt host retains assembly identity `Uno.UI.Runtime.Skia.Win32, Version=255.255.255.255`. `Host.csproj` uses .NET 10/C# 13 and the original upstream SkiaSharp **3.119.0 compile API** (its own lock file); the final prototype retains its existing **3.119.2** runtime. Only the rebuilt host DLL/PDB are copied into a fresh isolated publish folder. No host-builder dependency DLL is copied over the prototype runtime.

Acquire the exact public source archive, then run the supplied script from a shell with .NET 10, Python 3 and Git (cross-publishing on macOS is supported):

```sh
curl -fL https://codeload.github.com/unoplatform/uno/tar.gz/da217df2312e9e39676880f9cb4b8d519cc297e0 -o /tmp/terminal-uno-source.tar.gz
sh tools/TerminalSurfacePrototype/WindowsHost/build-windows-host.sh /tmp/terminal-uno-source.tar.gz
```

The archive SHA-256 is `7e84fc9f25e70e71736a8715f8da6df2396e70c878ea4a1998462d6d8b6f0cd4`. Preparation checks it before writing, extracts only the Win32 host into a fresh generated directory, checks/applies the exact patch, and refuses to overwrite existing source. The script uses locked restores and emits a unique `artifacts/terminal-surface/windows-privacy/build-*/app` directory. The optional `DOTNET` variable selects the executable. Source acquisition may be performed ahead of an offline build with all locked packages available. It never rewrites a package-cache assembly or an upstream checkout.

The Apache-2.0 Uno license remains in `ThirdParty/Uno-LICENSE.md`; `ThirdParty/Uno-TerminalResearch-MODIFICATIONS.txt` identifies modified upstream files and the experimental addition. Preserve those and the existing WebView2/binding/xterm notices with any shared experimental output. The script redistributes no Edge runtime or installer. Runtime acquisition, license review and deployment remain separate requirements below.

Recorded on arm64 macOS 26.7.1 with .NET SDK 10.0.402:

- The complete patched Win32 host and both backend API paths compile with zero warnings/errors; locked Windows x64 self-contained prototype publish succeeds.
- Focused source checks verify that runtime validation and the exact options supplier precede each environment creation call, environment tracking precedes controller creation, failure completes initialization, and unknown backend selection cannot fall back. Missing-option and missing-runtime-check mutations are rejected.
- The actual WebView2Aot options instance returns true for custom reporting and exclusive access. Microsoft managed options are compiled; their execution is skipped on macOS and still required on Windows.
- Configuration checks cover unprepared initialization, unknown backend, environment/browser-argument/SSO overrides, old/malformed/prerelease version strings, fresh profile allocation, path mismatch/external dump location, duplicate claims and retained profiles without exit evidence. Separate processes check stock-host rejection and patched-host marker acceptance.
- Simulated lifecycle checks hold deletion until an exit task succeeds; missing/failed/stalled exit, controller-close failure and deletion failure return failure without a premature deletion. These exercise the actual join/delete coordinator with controlled callbacks, not actual Windows processes.
- The original `--check-fixture` raw-byte, `0x03`, resize, revocation, backpressure and joined-producer regression checks pass. Browser document resources are unchanged.

**Still unavailable:** execution of either backend on Windows, the actual installed runtime version, real option acceptance during environment creation, deliberate synthetic browser crashes/minidumps, network capture before/during/after crash, physical profile deletion after browser exit, and the Windows Closing/UI path. Run both backend selectors in separate fresh processes on the named Windows workstation, verify stock-host failure, missing/old runtime failure and cleanup-retention failures, then inspect only fixed synthetic sentinels in locally retained dumps/diagnostics and capture outbound traffic. Verify source/API expectations against observed behavior before any privacy conclusion. The existing Windows terminal/input/real-Engine checklist below remains outstanding.

If maintaining this exact host patch is unacceptable, the smallest alternatives are an Uno upstream option/environment-factory hook wired through **both** constructors (with the same failure and lifecycle controls), or a narrowly versioned Uno host fork carrying these changes. Neither is presently delivered as a production dependency. A dedicated C# core/Uno renderer avoids browser crash reporting but requires the renderer work described in the comparison. No production component is selected at this boundary.

## Pinned dependencies and distribution obligations

`global.json` pins Uno.Sdk 6.7.30; the prototype's `packages.lock.json` records resolved NuGet versions/content hashes. Use locked restore for the same target/RID; adding a new publish RID requires an explicit lock-file refresh. JS assets are committed, so npm is not a build/runtime requirement.

| Component | Experiment version | Source/license and obligations |
| --- | --- | --- |
| `@xterm/xterm` | 5.5.0 | npm tarball `https://registry.npmjs.org/@xterm/xterm/-/xterm-5.5.0.tgz`; MIT copyright/permission notice is in `ThirdParty/xterm-LICENSE`. No addons or remote fonts. |
| Uno.WinUI and Skia hosts | 6.7.135 | Apache-2.0; source commit `da217df2312e9e39676880f9cb4b8d519cc297e0`; license copied in `ThirdParty/Uno-LICENSE.md`. Preserve applicable notices, license and any required modification attribution on distribution. |
| WebView2Aot | 1.5.0 | MIT, [upstream](https://github.com/smourier/WebView2Aot); package license copied. Default Windows .NET 10 backend in this Uno pin; no AOT delivery policy selected. |
| DirectNAot / DirectNAot.Extensions | 1.6.1 / 1.6.1 | MIT; separate package license files copied. Transitive bindings for the WebView2Aot backend. |
| Microsoft.Web.WebView2 SDK/loader | 1.0.4191.47 | Package's BSD-style Microsoft three-clause license and accompanying NOTICE copied. These SDK terms are distinct from the runtime's terms. Ship the correct architecture loader if distributing Windows builds. |
| Microsoft Edge WebView2 Runtime | Not installed/pinned/tested here | Separately licensed Microsoft runtime. No runtime binaries or installer are redistributed by this change. A production experiment must record the exact installed runtime or pin an acquired Fixed Version package. |
| macOS WKWebView | OS 26.7.1, build 25G241 | System WebKit used for local evidence; does not substitute for Edge runtime evidence or establish macOS delivery support. |

Vendored SHA-256:

```text
xterm-5.5.0.tgz bd954fa721872170188cc5d7e83e88db3c83c9a18a4e8d24c2783d26491f59d2
xterm.js       1f991ac3b4b283ebf96e60ae23a00a52765dd3a2e46fa6fdda9f1aab032f7495
xterm.css      ba8e6985669488981ccf40c0cefe3aba80722cb6c92de7ad628b0bd717faf2b6
```

Current runtime terms were read in Microsoft's [download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) x64 Evergreen download dialog without accepting or downloading a runtime. They permit object-code redistribution subject to significant app functionality, protective downstream terms, required notices, acquisition from Microsoft, distribution restrictions and an indemnity obligation. They also cover data collection and required SmartScreen notice if enabled. This is a material packaging/legal review item, not an MIT-only dependency. Recheck the exact chosen runtime's terms before redistribution, including Fixed Version terms if that mode is chosen.

Microsoft documents [Evergreen offline standalone installers and Fixed Version distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution). Both allow offline deployment; Fixed Version adds over 250 MB and requires deliberate security updates. Evergreen's online bootstrapper alone does not establish offline installability. This research chooses neither installer mode nor runtime version. Existing .NET/Uno/Skia/font dependency notices remain necessary; these incremental notices are not a complete release bill of materials. Linux GTK dependencies in the desktop graph are not evidence of Linux product support.

## Observed macOS development evidence

Host: arm64 macOS 26.7.1 (25G241); .NET SDK 10.0.402; Uno pins above; Release, self-contained `osx-arm64` development bundle; system WKWebView. Observations were obtained through the native prototype UI, not a standalone browser page. Only fixed synthetic text was used.

| Exercise | Observed result | Limit |
| --- | --- | --- |
| Split output | Every byte of the initial UTF-8/ANSI sample was delivered separately; `café λ 世界 🐳` rendered intact, RED was red, cursor movement produced `12XYZ`. Initial delivered bytes: 189. | Selected sequences, not exhaustive VT/Unicode conformance. |
| Keyboard / control byte | Typed text echoed; actual Ctrl+C reached the controlled boundary as one `0x03`. Toolbar Ctrl+C also increments that counter. | Echo fixture cannot prove a real process handles SIGINT or that Docker lifecycle stays unchanged. |
| Selection/copy/paste | Select fixture → Copy selection → Paste echoed `UTF-8: ` and added seven input bytes through the sandbox/native bridge. | Programmatic selection exercised; mouse-selection ergonomics and all clipboard shortcuts need Windows/manual acceptance. |
| Resize | 80×24 changed to 100×30 in both xterm metadata and the controlled boundary. | Explicit dimensions, not automatic pixel-fit or actual Engine resize. |
| Sustained output | 10,000 lines / 200,189 bytes completed; xterm retained 224 rows at 80×24 with 200 scrollback, oldest sample no longer present. | Workload evidence, not a workstation CPU/RSS benchmark. |
| Backpressure | Hold delivery yielded 4/4 queued chunks, zero rendered bytes and a blocked producer. | Fixed fixture capacities, not a tuning decision. |
| Close/replacement | Close revoked pending output; Close/replace pending produced only the new 189-byte sample. Repeated Close then New worked after correcting a cancellation-source lifetime defect found during this investigation. | Does not test actual daemon, process death or endpoint recovery. |
| Malicious-looking output | Literal `<script>…</script>` was rendered as text, OSC 8 link inert, OSC 52 did not replace explicit copy/paste flow. | No complete security audit implied. |
| Embedded navigation | Remote popup denied, fetch rejected, remote frame navigation left the local terminal active. Final metadata reported `connect-src: 2`, `frame-src: 2`, `externalPopupBlocked: true`, `topNavigationBlocked: true`. Earlier unsandboxed top-level probes incremented native navigation/popup cancellation counters. | Final sandbox prevents these before native events; Windows enforcement and runtime network traces remain outstanding. |
| Lifetime byte cap | The 100,000-line producer was canceled at the 1 MiB output lifetime limit, surface cleared, and New fixture reopened successfully. | Research safety limit, not a production long-running terminal policy. |

The instructions below reproduce these scenarios and expose metadata counters in the UI. The fixture checks additionally verify raw-byte preservation, `0x03`, dimension propagation, pending-read revocation, repeated close/reopen, four-slot backpressure and cancellation/join of the producer. No sensitive payload is printed by those checks.

## Windows-specific evidence and exact gaps

**Source/build evidence only:** the selected Uno package contains the Win32 HWND backend and .NET 10 WebView2Aot selection. The isolated project compiles with `UseWin32()` and references the existing Application interface. This is stronger evidence of host feasibility than another Uno target, but it is not a Windows run.

A locked `win-x64` self-contained cross-publish succeeded on macOS and produced `TerminalSurfacePrototype.exe`, `WebView2.dll` and `WebView2Loader.dll`. This verifies package/build preparation only; it neither executes Windows native code nor provides the Edge browser runtime.

**Unavailable:** no Windows execution environment or authorized reachable Windows Docker endpoint was supplied for this task. The development host has no `docker` CLI and no configured Engine endpoint for this experiment. No real Engine call was made, and no container/shell was created. Existing Application/protocol harness results remain simulated evidence, as described in `container-terminal-sessions.md`.

Still obtain on a named Windows workstation and real Engine through the existing client: exact runtime/architecture, non-elevated startup, offline install, UTF-8/ANSI rendering, keyboard/IME/focus, mouse selection and copy/paste, control byte `0x03` with lifecycle observations, process-reported resize, named-pipe duplex exchange, explicit command failure/distroless behavior, paused/stopped/disappeared container diagnostics, EOF/daemon disconnect, close/replacement during delivery, repeated opening and stream/resource release. Record actual exec-inspection evidence separately from client state, and prove the crash/privacy configuration before introducing sensitive data. None of these are inferred from macOS or mocked acknowledgments.

## Reproduce

From the repository root with .NET 10 and the pinned Uno SDK available:

```sh
dotnet restore tools/TerminalSurfacePrototype/TerminalSurfacePrototype.csproj -p:Configuration=Release --locked-mode
dotnet build tools/TerminalSurfacePrototype/TerminalSurfacePrototype.csproj -c Release --no-restore
dotnet tools/TerminalSurfacePrototype/bin/Release/net10.0-desktop/TerminalSurfacePrototype.dll --check-fixture
dotnet run --project tools/TerminalSurfacePrototype/TerminalSurfacePrototype.csproj -c Release --no-build
```

Windows requires the WebView2 Runtime; this research intentionally supplies no installer. Ordinary builds now refuse Windows browser startup; use the optional patched-host procedure above for a synthetic-only Windows experiment. The privacy blocker remains unresolved until Windows evidence is recorded. For an offline experiment, restore/build dependencies in a connected build environment first, bundle xterm locally as here, and install a reviewed offline runtime package on the isolated test machine.

macOS UI automation may need a development `.app` wrapper rather than launching the DLL directly. A reproducible wrapper script is supplied as `tools/TerminalSurfacePrototype/run-macos.sh`; it publishes into ignored `artifacts/terminal-surface/macos` and does not alter production files.

1. New fixture → Report bounds: verify the Unicode line, red text and `12XYZ`; metadata booleans should be true.
2. Click the terminal, type a short synthetic string, press Enter and Ctrl+C; Report bounds shows input/control-byte counts. Select fixture → Copy selection → Paste verifies explicit clipboard exchange.
3. Switch between both dimension buttons and inspect xterm/boundary dimensions.
4. 10,000 lines → Report bounds: expect 224 retained rows and eventual producer completion. Hold delivery → 10,000 lines → Report bounds: expect 4/4 pending. Close, New fixture, then Close/replace pending: expect no old sustained lines in the replacement.
5. Probe popup/fetch/navigation/URL handler: the local terminal must remain active, sandbox popup/fetch indicators should report blocking, and no external page or URL handler should appear. Report bounds includes CSP directive counts. Use only the fixed probes.
6. 1 MiB cap: wait for the explicit lifetime-limit status and cleared terminal, then verify New fixture reopens successfully.

Future promotion requires the separately recorded Windows results and a deliberate component decision outside this experiment. This document does not modify Intent, Engineering, verification policy or Factory state.

## Verification recorded

- Release prototype build and locked restore: pass, zero warnings/errors.
- Optional patched Win32 host, both backend API paths, reproducible Windows x64 publish and 12 host-check scenarios: pass at source/configuration/build/simulated-lifecycle level; zero build warnings/errors. Windows execution remains unavailable.
- Pinned-archive mismatch rejects before source writes; shell/Python/JavaScript syntax and patch whitespace checks: pass.
- Native macOS Release fixture interactions: observations above; the main app process had no listening TCP sockets during the check. Source inspection also found no listener/server or URL-launch API in the prototype.
- Focused `--check-fixture` checks: pass; JavaScript and macOS wrapper syntax checks: pass.
- Windows x64 self-contained cross-publish with locked restore: pass; runtime execution unavailable.
- Existing Release solution build: pass, zero warnings/errors.
- Existing independent regression suite: **398/398 groups passed on macOS 26.7.1**. This remains simulated protocol/Application evidence, not real Windows Docker evidence.
