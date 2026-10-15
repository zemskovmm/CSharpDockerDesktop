# Terminal surface feasibility research

Research date: 2026-10-15. This is evidence for the unresolved IDD-0013 spike, **not a durable component decision or IDD-0008 conformance claim**. Production navigation, dependencies and exec/session ownership are unchanged.

## Recommendation

Continue evaluating **bundled xterm.js inside Uno's Skia WebView2**, with an opaque sandboxed terminal frame and a narrow in-process bridge. It is the most credible low-porting-cost surface examined for the required Win32 host. The isolated runnable prototype is `tools/TerminalSurfacePrototype`; it references the existing Application terminal boundary and implements a synthetic echo fixture. It has no Docker access or command default.

Do **not** promote this prototype into production yet. Whole-browser bounded memory remains unestablished. The post-mitigation aggregate baseline below adds a ten-minute generation, repeated sessions and native memory measurements; stable logical holdings and selected object collection do not establish a browser lifetime bound. Required Windows visual/input evidence and real Windows Engine interaction are unavailable. The stock pinned Uno WebView environment does not expose the documented option that prevents automatic WebView2 crash-dump upload. The isolated source-patch experiment below now demonstrates option propagation and a rebuilt host for both Windows backends, but has no Windows runtime, crash or network evidence. The Windows privacy blocker therefore remains open. Resolve those runtime observations and runtime-distribution obligations before any sensitive real-session trial. A narrowly maintained Uno upstream change/fork remains viable at source/build level; a C# terminal core with a purpose-built Uno renderer remains the browser-free alternative. A plain-text Logs view cannot substitute for this work.

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

A **1 MiB cumulative output budget per ordinary fixture** still closes and disposes the terminal. This is deliberately a research limit, not a proposed production session policy. The baseline investigation below confirmed unbounded combining-cell and accessibility text; the ordered source experiments bound individual combined cells, announcement/echo storage and width-cache keys, while overall memory remains unresolved; incomplete OSC/DCS strings already have a separate upstream limit. Only fixed, finite, synthetic diagnostics may exceed 1 MiB, with a separate 64 MiB ceiling. This exception does not enable unrestricted long-running sessions. Row counts and bridge budgets do not establish bounded browser memory.

Closing/replacing cancels delivery, revokes the identity, closes the Application boundary, joins the producer/pump, and disposes the old terminal before starting the next one. Both C# delivery and JS acknowledgment check identities. An old queued write cannot target the new terminal object. Repeated close/reopen is supported. This fixture does not establish that a real exec process exits when its client closes.

## Combined-cell source experiment, 2026-10-15

The isolated prototype now uses **modified MIT xterm.js 5.5.0**, rebuilt from the checksum-pinned npm source with `XtermSource/combined-cell-limit.patch`. This is a narrow experiment, not component selection, an accepted production failure policy, or overall bounded-memory conformance. Production projects, Application contracts and terminal integration are unchanged.

**Experimental limit: 256 UTF-16 units per combined-cell string**, including the leading scalar; supplementary scalars count as two units. This intentionally small finite research threshold preserves ordinary accents, Unicode and fragmented UTF-8 while making adversarial failure easy to reproduce. It is not a Unicode grapheme limit recommendation. `BufferLine.addCodepointToCell` checks existing length plus the incoming scalar's numeric UTF-16 length **before** scalar conversion or concatenation. A cell of exactly 256 units is allowed; the next addition throws a dedicated fixed error without changing the cell. The legacy `set` and `setCell` imports also reject oversized combined values before map insertion. Insert/delete, clone/copy and reflow move bounded strings rather than concatenate strings; stale overwritten entries are still included in diagnostics.

Transient reasoning: an accepted append can keep the previous at-most-255-unit string, the at-most-two-unit scalar and the at-most-256-unit result alive simultaneously (at most 513 logical UTF-16 units for those three strings, conservatively; 1026 bytes at two bytes/unit). A rejected append constructs neither an added scalar string nor an oversized concatenation. This is logical payload reasoning, **not a JS heap allocation bound**: engine ropes, headers, GC, shared references, line copies, parser buffers and renderer allocations add costs. The existing 4096-byte write limit and single acknowledged write remain. Renderer width measurement can repeat a cell 32 times; this patch does not bound the number of cache entries or their browser allocations.

`WriteBuffer` catches only the dedicated limit error, permanently revokes its writer, clears queued data/callbacks, and sends the fixed `combined-cell-limit` argument through a documented **experimental callback extension**, not a stock API. It emits no success acknowledgment and parses no remaining bytes in that or queued writes. Other parser errors keep upstream behavior. The sandbox immediately fences/disposes that terminal and reports only fixed code plus generation/sequence. C# checks both identities, revokes the session, cancels the pending acknowledgment, closes the Application fixture and joins the pump and producer through the existing serialized cleanup. The fixed visible status is `Prototype failed: combined-cell allocation limit exceeded; session closed.` Previously parsed bytes in the failing write can have affected the discarded surface; no atomic-write or continuation guarantee is implied. The user can deliberately open a new fixture; stale writes and delayed failure cannot target its generation.

The **ordinary 1 MiB cutoff**, finite 64 MiB diagnostic ceiling, four-slot delivery channel, one outstanding browser write, bounded input/copy/paste, opaque sandbox/CSP, OSC 52 blocking, deliberate clipboard flow and Windows startup privacy gate remain. Accessibility remains enabled. Healthy sessions are not reset, output is not silently truncated, and limits are not enforced by polling private counters after growth.

### Reproduce source, build and focused checks

The complete commands are in [`XtermSource/README.md`](../tools/TerminalSurfacePrototype/XtermSource/README.md). Preparation checks the archive and patch hashes before source writes, extracts into a fresh ignored directory, checks/applies the patch, and never edits installed packages or an upstream checkout. Isolated `npm ci --ignore-scripts` uses a committed lock file (esbuild 0.25.0, integrity-pinned platform binaries); Node 22.23.1/npm 10.9.8 were used. The build resolves the npm archive's const enums from `.d.ts` sources, bundles the browser public entry, verifies the rebuilt hash and runs source checks. .NET builds consume the committed asset and need no npm/network. Preserve the existing MIT notice and `ThirdParty/xterm-TerminalResearch-MODIFICATIONS.txt` when sharing the experiment.

```text
npm archive SHA-256: bd954fa721872170188cc5d7e83e88db3c83c9a18a4e8d24c2783d26491f59d2
source patch SHA-256: 987c7af36b0d4cd142f1fdf7fbeaf916a59f385dc9fe6890f5af5c8455c685a4
accessibility patch SHA-256: 912b96318c8a8723390db3392c6ed53074025a1ee99551c563c3454f7b0fb400
width-cache patch SHA-256: 1b48215fd5de51219fa5ca224c90d815fb93e391751b427d6a8b3c5d6dc798da
current modified Web/xterm.js SHA-256: 019495f1709e55b6ccfed75ba045483bc3ea60714f2aa7652be1fc268f1116f5
unchanged Web/xterm.css SHA-256: ba8e6985669488981ccf40c0cefe3aba80722cb6c92de7ad628b0bd717faf2b6
```

After the documented Release build/publish, use the actual Uno/browser runner:

```sh
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app --workload combined --workload combining --workload lifecycle --workload cutoff --output artifacts/terminal-surface/combined-checks
```

Source/unit checks cover exact/over limit, UTF-16 supplementary scalar accounting, overwritten stale entries, insert/delete, shrink/grow, clone/copy/import, pre-growth rejection and permanent writer/queue revocation. They do not stand in for browser evidence. The `combined` native check sends byte-fragmented exact and over-limit input through `IContainerTerminal`, checks both screens, edits, overwrite, resize and normal-screen wrapping/reflow, then checks close/replacement during failure and stale generation rejection. It also executes the existing real native clipboard copy/paste, ANSI/UTF-8, resize and Ctrl+C synthetic Application-boundary check. The former `combining` workload remains finite but now requires early failure, canceled/joined blocked producer and successful reopening. Diagnostics contain only metadata/fixed outcomes, never terminal strings or input.

Native results on the same macOS 26.7.1/arm64 host and actual Uno/WKWebView Release bundle: **`combined`, `combining`, `lifecycle`, `cutoff`, `normal` and `alternate` passed**. Both byte-fragmented exact-limit screens retained the full 256-unit cell. Insert/delete left three retained map entries (768 units total) while each remained at most 256; overwrite left stale entries (514 units total) while the active two-unit cell was correct. Normal-screen wrapped-row reflow and alternate-screen resize preserved their selected combining text; alternate-screen shrink is not claimed to reflow offscreen columns. Both over-limit fragmented cases closed with the fixed failure; failed pump/producer joined, concurrent close/new and in-flight replacement completed, stale writes/delayed failures did not affect the new 189-byte fixture. The large finite combining producer canceled before completion with queued/blocked delivery and reopened successfully. The 14,100,000-byte normal Unicode/ANSI burst and 8000 alternate transitions also passed under the modified asset. Source/unit checks, a second fresh-directory byte-identical rebuild, preparation mismatch rejection, raw-byte fixture checks and Release build/publish passed separately. Metadata/RSS files are reproducible under the ignored directory shown above; these brief samples provide no sustained-memory claim.

**Remaining gaps after the combined-cell work:** the accessibility and width-cache holdings are addressed by the subsequent narrow experiments below; browser heap/native memory and long-duration/repeated-session RSS remain unresolved. One finite per-cell string bound does not establish bounded total combined storage independently of buffer dimensions/history, or bounded overall terminal memory. The selected synthetic edits are not exhaustive VT/Unicode/accessibility acceptance. Windows runtime rendering/input, WebView2 crash/privacy behavior and network evidence remain unavailable; the Windows startup gate stays in place. Real Engine duplex exec, resize/control-byte/lifecycle and failure evidence remain unavailable. No production conformance or durable terminal component decision follows from this task.

## Accessibility-retention source experiment, 2026-10-15

**Result: the three identified accessibility holdings have finite experimental source budgets, with selected actual Uno/WKWebView checks passing.** This does not establish bounded overall browser memory, assistive-technology acceptance, Windows runtime behavior, real Engine behavior, or production component selection. The historical 262,144-character live-region result below remains a stock baseline. Production dependencies, navigation, Application contracts, durable Intent/Engineering and the Windows startup gate are unchanged.

The second ordered, checksum-pinned `XtermSource/accessibility-limit.patch` changes the pinned source rather than polling private counters after allocation. Experimental choices are:

| Holding | Admission limit | Accounting |
| --- | --- | --- |
| `_charsToAnnounce` plus live-region text | **16,384 UTF-16 units combined** | Each holding is individually bounded by the aggregate. Pending-to-live transfer does not charge pending twice. Supplementary scalars use two units. |
| `_charsToConsume` echo queue | **256 entries**, **1,024 total UTF-16 units**, **16 units per entry** | Check both entry count and units before insertion; subtract the shifted entry's full length on ordinary echo/mismatch consumption. Control keys do not enter the queue. |

These small finite values make exact boundaries and no-echo failures reproducible while accommodating the selected ordinary fixtures. They are experimental limits, not production tuning recommendations or a guarantee that every legitimate workload fits. The announcement budget admits roughly 32 KiB of logical UTF-16 payload and echo roughly 2 KiB; object/array overhead, DOM storage, temporary concatenations, string sharing/ropes, copies, parser/render state and garbage collection are additional. No JS heap or process-memory bound follows from these counts.

Prospective announcement growth is checked **before concatenation or DOM assignment**. The check includes both the incoming character and, at the upstream twenty-first newline, the entire `Strings.tooMuchOutput` message. Tab expansion uses guarded individual spaces. Transfer revalidates the combined state before DOM append and then empties pending text. Existing key behavior clears the live region while preserving pending output and the bounded echo queue; blur explicitly clears pending/live text, echo entries and the unit counter so a delayed refresh cannot resurrect old announcements. Disposal clears all three holdings and cancels the debouncer; late guarded work cannot append. Accessibility, normal echo suppression, the upstream excessive-output behavior and row navigation stay enabled below the budgets. Healthy output is not periodically reset and no output is silently truncated.

A violation during output parsing throws only the dedicated, content-free `accessibility-limit` error. `WriteBuffer` permanently revokes its writer and queued callbacks before reporting that fixed code through the experimental callback extension. Dedicated asynchronous parser rejections and reentrant failures also revoke without running a stale continuation or acknowledging success. Keyboard/render callbacks catch the dedicated error locally and revoke through a research-only core event; no budget exception escapes the timer or keyboard stack. That event also handles failures while no output write is pending.

The sandbox fences input and acknowledgments immediately and schedules disposal after the parser/key/render stack unwinds. It sends only fixed code, generation and the most recent write sequence (zero before a first write); diagnostic runs additionally attach numeric retention counts. C# validates both identities and uses the existing serialized cleanup to revoke the session, cancel any acknowledgment, close the controlled fixture, and join pump/producer. The fixed visible status is `Prototype failed: accessibility retention limit exceeded; session closed.` A failing write may have parsed a prefix, but it receives no success acknowledgment. The user can deliberately open a new fixture; stale writes, failures and delayed callbacks cannot revoke its new generation. The combined-cell limit of 256 units, ordinary **1 MiB cumulative cutoff**, finite **64 MiB diagnostic ceiling**, channel bounds, clipboard/input bounds and privacy controls remain.

### Reproduction and evidence

Use [`XtermSource/README.md`](../tools/TerminalSurfacePrototype/XtermSource/README.md) to prepare a fresh directory, verify all three patch hashes before source writes, apply combined-cell, accessibility, then width-cache patches, install locked build tools, rebuild/check and compare the asset. The checksum block above identifies the current asset. A second fresh-directory build was byte-identical. The MIT license and updated modification notice remain bundled. Normal .NET builds use the committed asset without npm/network.

After publishing the development bundle, run:

```sh
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app --workload a11y --workload a11y-boundaries --workload attributes --output artifacts/terminal-surface/accessibility-final
```

Instrumentation reports only pending units, live units, their aggregate, echo entries, total echo units, maximum entry units and row count, alongside existing parser/buffer/cache counts. Failure snapshots distinguish received bytes from acknowledged delivery. No terminal strings, input, announcement text or selections are printed. The `a11y` and `attributes` runners now expect explicit early accessibility failure and reopening, replacing their historical completion expectation. Both fixtures remain finite; they do not disable accessibility or bypass the ordinary cutoff outside diagnostic runs.

**Source/unit plus simulated-DOM evidence:** focused checks pass for exact/over aggregate and echo limits, mixed pending/live state, transfer, supplementary Unicode, tabs, the excessive-output message, control keys, matching/mismatching echo consumption, clear/blur, disposal and delayed callbacks. Additional writer checks cover failed/queued/future writes, rejected parser promises, reentrant failure and an outstanding parser promise resolved after revocation. These are source/simulated checks, **not browser or screen-reader acceptance**.

**Native evidence:** Release self-contained arm64 macOS 26.7.1 / system WKWebView through actual Uno, the controlled Application boundary, four-slot channel, sandbox bridge and acknowledged byte writes:

| Fixed fixture | Observed result |
| --- | --- |
| `a11y` carriage-return producer | Failure snapshot: **16,384 pending + 0 live units**, 0 echo entries. 36,837 bytes had reached the browser; only 32,744 were acknowledged. The failing 4,093-byte write was not acknowledged, the blocked finite producer canceled, pump/producer joined and a fresh 189-byte fixture opened. This replaces retention of the baseline's 262,144 live units. |
| `a11y-boundaries` supplementary/fragmented output | Full 40,960-byte fixture accepted exactly **16,384 units**. Mixed **8,192 live + 8,192 pending units** are accepted, then transfer to **16,384 live + 0 pending**; the subsequent extra character fails without acknowledgment. Individual UTF-8 bytes at the boundary are fragmented. |
| Tabs and no-echo keyboard | Tab expansion fails at the aggregate boundary. **256** synthetic DOM keydown events retain **256 entries / 256 units / 1-unit maximum entry**; an ordinary matching output consumes one entry with no announcement. Blur empties the queue and both announcement holdings. The next key beyond a fresh exact queue fails while an injected asynchronous CSI parser write is outstanding; cleanup cancels its acknowledgment and a later resolution does not affect replacement. |
| Delayed render, navigation, close/replacement | A fixed injected prospective character at an exact boundary is executed by the actual accessibility debouncer and fails through its guarded callback. This is **fault injection**, not a naturally occurring transfer overrun: normal admission already prevents aggregate excess. Ordinary Unicode/ANSI/tab output, pending-text clearing on blur and actual accessibility boundary-row scrolling pass. Disposal with a refresh pending, concurrent close/new, failure during in-flight replacement, stale failure and late parser/debouncer callbacks leave the new 189-byte fixture active. |
| `attributes` | Its carriage-return printable output now reaches the accessibility budget and ends explicitly, joining delivery and reopening. This is the expected new result, not a claim that its original 50,000-character fixture completes. |

The native `combined`, `combining`, `lifecycle`, `cutoff`, `normal`, `alternate`, `links`, `osc`, `dcs` and `csi` regressions also pass with the new asset. This retains both-screen combined-cell/edit/reflow evidence, UTF-8/ANSI, synthetic clipboard/control-byte/resize checks, stale-generation rejection, the 1 MiB cutoff and producer cancellation. The normal workload still delivers **14,100,000 bytes** under its finite diagnostic ceiling. Focused raw-byte fixture checks, fresh pinned reproduction, Release prototype/solution builds and the existing **398/398** simulated regression groups pass separately. A self-contained Windows x64 cross-publish also passes (single-node MSBuild); this is build evidence only, with Windows runtime execution unavailable. Reproducible native metadata and one-second RSS samples are under ignored `artifacts/terminal-surface/accessibility-final`; the final mixed-state boundary check is under `accessibility-final-boundaries`.

**Limitations:** synthetic DOM keys do not establish physical keyboard/IME ergonomics. Actual screen-reader speech, assistive-technology integration and Windows accessibility acceptance were not exercised. The delayed-render and held-parser cases are explicit fault injection on the actual browser path; they do not prove that an ordinary render organically exceeds the checked aggregate. Echo total-unit and per-entry boundaries additionally have source/simulated evidence, not real multi-character keyboard input acceptance. Brief RSS samples remain observations only. The next experiment addresses the width-cache map; overall browser/native memory, sustained/repeated-session plateau, Windows privacy/runtime behavior, real Engine interaction and production surface selection remain separate unresolved questions. No screen-reader, bounded-memory or terminal production conformance is asserted.

## DOM width-cache retention experiment, 2026-10-15

**Result: the prototype's DOM renderer cache now has bounded retained keys, with actual Uno/WKWebView eviction evidence.** This closes only the identified unbounded width-map holding. It does not establish bounded overall browser/native memory or sustained-session conformance. Production Terminal presentation is absent, component selection remains unresolved, and Windows privacy/runtime and real Engine interaction remain unvalidated. Production dependencies, Application contracts, durable Intent/Engineering and the Windows privacy gate are unchanged.

The third ordered, checksum-pinned `XtermSource/width-cache-limit.patch` sets finite experimental budgets of **512 map entries and 32,768 aggregate key UTF-16 units**. Both limits apply to one shared map across regular, bold, italic and bold-italic, including non-Latin and combined text. These deliberately modest budgets expose both capacity conditions in short finite diagnostics; they are not production tuning policy. Before inserting a positive measured width, FIFO eviction removes oldest keys until both limits admit the new key. Hits do not change FIFO order or grow retention. Missing/evicted entries use the original DOM width measurement again; the cache never terminates a session, truncates output, disables accessibility or resets the terminal. A one-unit variant prefix replaces ambiguous B/I suffixes. Widths for different literal strings and variants cannot collide through suffix concatenation.

Allocation accounting is separate from an overall memory claim:

| Holding or temporary | Experimental bound / reasoning |
| --- | --- |
| Retained map keys | At most **32,768 UTF-16 units** (64 KiB logical UTF-16 payload), including the one-unit variant prefix; at most **512 entries** limits map/value/node overhead independently. No history of font names is added to keys. |
| Regular flat cache | Unchanged **256 Float32 values / 1,024 bytes**, only regular one-unit code points below 256. Bold/italic variants use the shared bounded map. |
| Prospective key | Existing combined-cell admission caps renderer cell strings at **256 units**, so a prefixed key is at most **257 units**, including supplementary characters counted as two. One prospective key is allocated before eviction; deleted key references/garbage may remain until collection. An out-of-contract key above the aggregate budget is measured without allocating/admitting a map key. |
| Measurement text | Original **32 repetitions** of a valid cell produce at most **8,192 units / 16 KiB logical payload**. The selected one of four hidden spans is cleared in `finally` immediately after reading width, including exceptions. Between calls, all four spans retain **zero text units**. There may be JS and DOM copies during assignment/layout; clearing does not guarantee immediate reclamation. |
| Clear, fonts, disposal | Clear/font invalidation discards the map and zeros unit accounting/flat entries and span text; unchanged font settings preserve cache hits. Numeric saturated counters/high-water marks describe the cache lifetime across invalidations. Disposal releases map and flat storage, clears/removes span nodes and drops span references. |

Object/Map overhead, string ropes/sharing/copies, allocator/GC behavior, retired maps awaiting collection, bounded buffer combined-cell maps, parser/accessibility state, font/layout caches and other WebKit/native holdings are outside these logical payload bounds. A cache hit still creates a short prospective lookup key; eviction trades retained keys for future layout work. A long combined cell remains intact. This task does not claim a total browser heap/RSS cap, latency guarantee, or a long-session plateau. Numeric diagnostic snapshots also create temporary arrays/strings and are not a heap profiler.

### Source and native evidence

The updated modification notice and committed rebuilt `Web/xterm.js` correspond to the manifest above. A fresh preparation applied all three pinned patches; a fresh locked `npm ci --ignore-scripts` and rebuild produced a byte-identical asset. Corrupting the third patch in an isolated preparation fixture was rejected before any destination/source creation. The focused source checks use actual `WidthCache` with deterministic simulated layout: exact **512 / 513 entry** and **32,768 / over-budget aggregate** boundaries, independently recomputed units, FIFO eviction/remeasurement, repeated hits, four variants/literal suffix separation, flat storage, supplementary/256-unit combined strings, zero-width admission, exception cleanup, clear, all font-setting invalidations, no-op font changes and disposal. They are source evidence, not browser or assistive-technology acceptance.

After publishing the Release arm64 development bundle, reproduce the native cache checks with:

```sh
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app --workload width-cache --workload width-cache-long --output artifacts/terminal-surface/width-cache-final-v3
```

Actual macOS 26.7.1 arm64 / system WKWebView / Uno, through the controlled Application fixture, four-slot channel, sandbox bridge and acknowledged UTF-8 byte writes: `width-cache` sends **48 batches of 64 distinct three-unit combined cells (3,072 cells)**; `width-cache-long` sends **16 batches of 64 distinct exact 256-unit combined cells (1,024 cells)**. Each uses all four font variants equally. Larger batches are split into at most 4,093-byte chunks, including mid-scalar/escape boundaries. Native readiness requires all 64 current cells to have actual cached rendered widths plus at least 64 new measurements. Parsed writes may coalesce, or a partially delivered cell may render; no assertion assumes a render for each write, and exact eviction counts can vary with scheduling.

Deliberate blur/refocus before each batch and recovery clears earlier pending/live announcements through the normal accessibility behavior. Screen reader mode, accessible rows and announcement processing remain enabled. A long batch announces exactly **16,384 units**, the separate accessibility limit; this controlled focus pattern prevents that limit from masking cache churn. It is not acceptance of unlimited uninterrupted screen-reader output. No cache injection or terminal reset is used. Fixed refresh/font probes request normal redraw and change font size 14→15→14. Readiness waits handle asynchronous character-size/font invalidation rather than assuming a fixed number of renders.

Both native cache fixtures **passed**. Counts at the end of capacity churn, before recovery:

| Fixture | Delivered bytes | Retained entries / key units | Peak entries / key units | Evictions | Measurements / map hits |
| --- | --- | --- | --- | --- | --- |
| `width-cache` | **15,912** | **512 / 2,048** | **512 / 2,048** | **2,560** | **3,075 / 6,080** |
| `width-cache-long` | **523,448** | **127 / 32,639** | **127 / 32,639** | **897** | **1,027 / 2,944** |

A final long key occupies 257 units, so 127 fit and the next triggers unit-budget eviction well before 512 entries. Independent key summation exactly matched the internal unit counter in every checked batch; both recorded peaks stayed within budgets. Measurement counts include the fixed flat-cache measurements. Map hits increased by **64** on a deliberate redraw in each fixture, while measurements stayed unchanged (**3,075** and **1,027** respectively). Replaying the first evicted 64-cell row added exactly **64** measurements and **64** evictions in each fixture. Its numeric sum of measured cell widths matched the original first-row value, **538 CSS pixels**, rather than returning a stale font/variant width. Font-size change invalidated and remeasured the 64 cells, yielding **576 CSS pixels** at 15px; the cache then held **64 / 256 units** for short cells and **64 / 16,448 units** for long cells. Hidden measurement text was **0 units** in all sampled states. Combined cells remained intact at **3** and **256 units**. Accessible rows remained **24** during churn. Resize 80×24→40×10→100×30, recovery text, close/new, stale-generation fencing, synthetic clipboard/control-byte checks and replacement with the ordinary **189-byte** Unicode/ANSI fixture passed.

With the final asset, native `combined`, `combining`, `a11y-boundaries`, `a11y`, `attributes`, `lifecycle`, `cutoff`, `normal`, `alternate`, `links`, `osc`, `dcs` and `csi` regressions passed. The normal diagnostic still delivered **14,100,000 bytes**. Raw-byte fixture checks, fresh source/build checks, Release prototype/solution builds and the independent **398/398** simulated regression groups also passed. Release arm64 self-contained publishing and Windows x64 cross-publishing passed; cross-publishing supplies no Windows execution/privacy evidence.

These short finite diagnostics, metadata logs and one-second RSS samples are under ignored `artifacts/terminal-surface/width-cache-final-v3`. WebKit helper PIDs are newly observed candidates, not definitive attribution if another browser starts concurrently. No forced collection or memory plateau acceptance is used. The ordinary **1 MiB cumulative cutoff** and finite **64 MiB diagnostic ceiling** remain. This focused experiment ends here; further overall-memory work requires its own workload/host baseline and browser/native retained-memory evidence. Windows rendering/input, WebView2 privacy/crash/network behavior, real Engine duplex sessions and production component selection remain unresolved.

## Matched lifecycle controls, 2026-10-15

**Result: the large repeated-session WebContent footprint rise reproduced only
with the populated workload in this comparison.** The empty bridge/session
control stayed near 24M; idle xterm construction/disposal ended near 28M; the
populated reference rose from 81.2M at first idle to 471.3M at final idle.
These are three short synthetic runs on the same macOS host, not an object-level
leak diagnosis, a lifetime bound or a component decision. The patched renderer,
ordinary **1 MiB cutoff**, finite **64 MiB diagnostic ceiling**, sandbox/CSP,
navigation protections, enabled accessibility and privacy restrictions are
unchanged. No forced collection, memory-pressure trigger, reset of a healthy
measured session, heap dump, sensitive reporting or developer tools were enabled.

### Matching and lifecycle evidence

The prototype README now reproduces `aggregate-empty-cycles`,
`aggregate-idle-cycles` and `aggregate-cycles` sequentially in **three fresh actual
Release Uno macOS development applications**. Each retains one WKWebView and
one opaque `allow-scripts` iframe for all twelve sessions, at **1024×566 CSS
pixels**, with fixed **80×24** terminal geometry. The empty control still loads
and parses the same xterm library/CSS; its session starts skip xterm construction
and opening. This is a bridge/session lifecycle within the existing persistent
frame, **not repeated iframe/WebView creation, navigation or library unloading**.
Those other lifetimes are outside this comparison; no unrelated browser was
substituted. Idle xterm uses the same options, focus and accessibility but
receives no terminal output. The populated reference retains its 24 paced ticks,
normal/alternate population and cache churn.

All measured baselines now precede **any xterm construction**; the historical
aggregate baseline below warmed up with a compatibility fixture first. Controls
wait the matching **24×60 ms** output window and **1,200 ms** render-settle
period; actual populated delivery adds its small parse/bridge overhead. The
shared `populated` checkpoint name is the active-state comparison point, even
for empty/idle controls. All variants block the same finite producer on four
native channel slots with browser delivery held, then revoke/cancel/join it and
the pump. That producer sends no bytes to the control surfaces. First/final
populated and idle checkpoints dwell eight seconds for capture. First/final
closed checkpoints have an eight-second capture dwell plus the ordinary
eight-second idle; other cycles idle eight seconds. These small cadence changes
apply equally to the new reference and both controls; absolute historical/new
baseline values are not asserted as identical.

During measurement the empty control records **12 starts, zero constructed /
opened / disposed terminals, zero render events and zero accepted writes**.
Idle records **12 constructed / opened / disposed, 12 render events and zero
accepted writes**. Populated records **12 constructed / opened / disposed,
377 observed render events, 300 accepted writes**, and **33,725 received bytes
per cycle**, with the same 224 normal/24 alternate lines and bounded logical
holdings. Render-event totals are observations, not fixed expectations. Every
close removes all terminal nodes, joins both tasks and leaves no acknowledgment
or input item pending. Each variant rejects **12 old-generation writes and 12
current-closed-generation writes** without changing received bytes. Idle and
populated final checkpoints observe **0 selected retired roots / 12 wrapper
finalizations**, without a collection-deadline assertion. After measurement,
all variants additionally replace an idle terminal with the empty control,
prove no prior terminal survives, reopen compatibility output, reject a stale
failure and run clipboard/control-byte/resize/backpressure/replacement and fixed
popup/fetch/navigation/custom-protocol probes. These later checks intentionally
construct terminals even in the empty-control application.

### Native comparison and limits

Host/runtime/Uno/WebKit conditions match the aggregate research above. Each
matched runner passed in **about 189 seconds**. App/helper PIDs were respectively
**6543/6546/6547/6548**, **7373/7377/7378/7379** and
**7873/7876/7877/7878** (helper order GPU, Networking, WebContent).
Each sequence kept one candidate per helper role. PIDs are newly observed since
launch, not definitive ownership: existing desktop/browser processes remain and
launchd parentage cannot exclude a concurrent unrelated WebKit launch.

| Corresponding checkpoint | Empty WebContent footprint | Idle xterm footprint | Populated footprint |
| --- | ---: | ---: | ---: |
| Never-constructed baseline | 24.5M | 24.7M | 24.6M |
| First active (`populated`) | 24.1M | 27.5M | 84.9M |
| First closed | 23.7M | 27.1M | 83.4M |
| First idle | 23.7M | 26.3M | 81.2M |
| Final active (`populated`) | 23.8M | 28.7M | 472.0M |
| Final closed | 23.7M | 28.5M | 471.5M |
| Final idle | 23.7M | 28.4M | 471.3M |

These are `vmmap` physical-footprint strings in its reported units. App and
candidate-helper RSS are captured separately at every corresponding checkpoint
in the [metadata summary](../tools/TerminalSurfacePrototype/Results/matched-controls-2026-10-15.json).
For orientation, first→final idle RSS in **MiB** was:

| Variant | App | WebContent | GPU | Networking |
| --- | ---: | ---: | ---: | ---: |
| Empty | 234.4→248.6 | 37.5→37.7 | 25.6→25.6 | 13.0→13.0 |
| Idle xterm | 240.6→245.8 | 44.6→50.2 | 27.8→28.1 | 13.1→13.1 |
| Populated | 242.9→252.6 | 94.9→199.2 | 41.8→44.5 | 13.1→13.1 |

The app also rises in controls; that separate process behavior is not assigned
to the WebContent graphics category. Native category reports were captured for
the app and every candidate helper at all seven stages. At first/final idle,
WebContent dirty owned-unmapped graphics was **9568 KB/9568 KB** for both
controls, versus **40 MB/371 MB** populated. Dirty WebKit malloc was
**9056 KB/8944 KB** empty, **11 MB/12 MB** idle, and **31 MB/90 MB** populated.
The populated final report also classified **205 MB graphics / 70 MB WebKit
malloc** as reclaimable; peak physical footprint was **685.4M**. Category names
and reclaimable classifications do not identify ownership paths or reclamation
deadlines. RSS, physical footprint and reclaimable values are different metrics;
they are not independent allocations to add together.

Each run has **165 RSS samples**, nominally one second apart, with actual
intervals **1.03–3.59 seconds** across the three runs. Sequential native tooling
causes the longer gaps. The summary retains per-stage native-clock requests,
outer-clock capture start times, closest RSS sample times, all app/helper
footprints, selected WebContent category columns, hashes and assertion outcomes.
Raw metadata/captures remain under ignored
`artifacts/terminal-surface/matched-controls-2026-10-15`; `summarize-controls.py`
reproduces the committed metadata-only projection. Capture stages are held but
are not simultaneous, and diagnostic inspection/tooling allocates memory.

The controls **weaken** a dominant explanation based solely on repeated
bridge/session calls, native channel cancellation, or blank xterm
construction/disposal under this cadence. They **support** an association with
paths exercised by the populated workload: parsing, rendered glyph/layout work,
accessibility, and their browser/native allocations remain coupled. They do not
distinguish those paths or rule out interactions with Uno/WebKit and process
caches. Small idle growth and one short empty plateau establish no lifetime
bound. Selected JS root collection still cannot account for every detached
object or native allocation. The next evidence boundary is a retained-object or
native allocation-ownership graph for the output-exercised path, obtained with
supported, privacy-preserving synthetic instrumentation; category totals alone
cannot cross it. No speculative renderer repair is made here. Windows
WebView2/privacy/input, real screen-reader use, real Engine duplex sessions and
production resource/readiness acceptance remain unresolved.

Focused verification for this change: the three matched native runs and their
control/cancellation/stale-generation/security assertions pass. Native `combined`,
`a11y-boundaries`, `cutoff`, `links`, `width-cache` and `normal` regressions also
pass with the final bundle, covering both-screen edits/reflow, allocation failure
and delayed callbacks, the ordinary 1 MiB cutoff, inert OSC links, bounded cache
churn and 14,100,000-byte diagnostic output. Raw fixture checks pass. Release
prototype build/macOS self-contained publish and production solution build pass
with zero warnings/errors; JS/Python/shell syntax and whitespace checks pass.
The prior 398-group suite remains historical evidence, not a rerun for this
harness-only change. The unchanged renderer asset remains `019495f1709e55b6ccfed75ba045483bc3ea60714f2aa7652be1fc268f1116f5`.

## Aggregate retention baseline after the three patches, 2026-10-15

**Assessment: the selected active payload holdings now have a reproducible aggregate model and selected disposed JS roots were naturally collected, but repeated sessions still showed increasing WebContent RSS and a much larger native graphics footprint. Whole-browser bounded memory and native reclamation remain unestablished. Keep the ordinary 1 MiB cutoff.** This baseline uses the existing three source patches without adding a renderer mitigation or a production policy. It does not resolve the terminal-component decision.

### Fixed workload and checkpoint contract

`aggregate-sustained` sends 1,200 half-second ticks in one generation. Each tick emits 16 Unicode/ANSI lines alternating ordinary and combined text, carriage-return overwrites, extended underline/color attributes, then 64 distinct short combined cells across four font variants. It churns scrollback and the bounded cache, below the 64 MiB diagnostic ceiling, without deliberately reaching combined-cell or accessibility failure limits. There is no terminal reset, blur/refocus, forced collection or external input during that generation. The existing accessibility twenty-first-newline excessive-output behavior remains; this is not continuous screen-reader speech acceptance. A fixed short OSC title is set once; the title-stack request remains disabled by the default window-options policy.

`aggregate-cycles` repeats 12 sessions in one native application/browser lifetime. Each uses 24 fixed ticks with 60 ms pacing, fills normal history, then fills the alternate screen while normal remains retained. It next holds native delivery, starts the finite normal producer, waits for all four channel slots and verifies the producer is blocked. Close revokes identity, cancels and joins producer/pump; an old-generation byte write must leave received-byte metadata unchanged. Every cycle records a fresh-terminal baseline, populated state, post-close state and eight-second idle state. The runner also records an empty-document baseline. Selected checkpoints dwell eight seconds for native tooling; the sustained post-close idle is 30 seconds. Both diagnostics reopen the ordinary 189-byte compatibility fixture and run clipboard/control-byte/resize/backpressure/replacement checks. Interaction is disabled during automated runs. These are fixed synthetic fixtures through the actual Application boundary, bounded channel, native bridge, sandbox and acknowledged byte writer, not direct renderer injection.

### Aggregate source audit and counter meaning

The audit uses the checksum-verified npm archive and the three ordered patches listed above. A fresh `aggregate-reproduce` preparation/rebuild produced the same pinned asset. Instrumentation is outside that asset, in the experiment wrapper. It emits counts/booleans only, including stale metadata; it never records terminal text, title text, commands, input or selection.

| Holding | Pinned source behavior and conditional bound | Numeric evidence |
| --- | --- | --- |
| Both screen histories and line storage | [`Buffer._getCorrectBufferLength`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/buffer/Buffer.ts) gives normal `rows + 200` and alternate `rows`; [`CircularList`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/CircularList.ts) can retain removed/popped lines outside logical length. [`BufferService.scroll`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/services/BufferService.ts) also retains one cached blank line. At fixed 80×24 there are 248 backing slots plus that line, at most 249 distinct held lines / 19,920 positions. Counters enumerate unique objects from the entire backing arrays plus the cached line, not only visible rows. | `normalLines`, `alternateLines`, capacities, `backingSlots`, `retainedLineObjects`, `nonLogicalLineObjects`; byte counters separately sum typed-array views and deduplicated backing buffers. |
| Combined and extended maps, including stale entries | Patched `BufferLine` rejects combined strings above 256 units before append/import. Position-keyed maps can keep old strings/attributes when ordinary text overwrites a cell; insert/delete/copy move bounded entries, fill clears maps, shrink removes keys beyond columns. Under these fixed dimensions at most 19,920 map positions per map give at most **5,099,520 combined UTF-16 units** (about 9.73 MiB at two bytes/unit), independent of observed tiny values. Extended objects carry numeric underline/color/link fields, not a transcript. | `combinedEntries`, `combinedUnits`, `maxCombinedUnits`, `extendedEntries`; stale counts test the corresponding live cell flag and still charge the old entry. Shared strings/attribute objects can be counted more than once; these are conservative logical payload sums. |
| Dimensions, resize and line backing buffers | These are bounds for this 80×24 diagnostic, not a global xterm dimension bound. Other existing checks reach 100×30: 260 circular slots plus a cached line, up to 26,100 positions. `BufferLine.resize` can shrink the view while keeping a larger allocation until `cleanupMemory`; reflow also creates transient line copies. | `maxLineColumns`, `cellArrayBytes`, `cellBackingBytes`, cleanup tasks/callbacks. Sampled backing sizes are not an allocation-history bound. No arbitrary terminal-size input was added. |
| Parser, title and stacks | Pinned `OscHandler`/`DcsHandler` cap each registered handler at 10,000,000 units, clearing after an over-limit append; conversion/concatenation has transient overshoot. Fixed handlers can duplicate a payload, so the per-handler cap is not an aggregate 10 MB promise. `Params` uses 32 parameter and 32 subparameter slots. `InputHandler` keeps current title/icon and two ten-entry title stacks; defaults disable stack operations here. Its UTF-32 decode buffer starts at 4,096 entries; this ≤4,093-byte path does not require growth. | `parserUnits` sums active handler data, `parserLimited`, `params`, `parserState`, `parserPaused`, `parseBufferBytes`, title/stack units and entries. Large completed title/icon payloads remain a separate source allowance, not a new small title budget. |
| Links, markers and decorations | The existing OSC 8 hook consumes completed metadata before built-in link creation; OSC 52 is consumed too. `OscLinkService` would otherwise retain marker-linked records until disposal. Proposed APIs are disabled, no addons/providers/decorations are registered, and no diagnostic registers markers. | Link and marker counts include both buffers and must stay zero. The unfinished-handler cap still applies before hooks complete. This does not generalize to enabling additional providers/addons. |
| Renderer and accessibility | Patched width keys retain ≤512 entries / 32,768 units, flat cache 1,024 bytes; hidden measurement text is cleared. `DomRenderer` replaces viewport row children; visible text and accessibility row copies scale with rows×columns×256, not total emitted bytes. `AccessibilityManager` uses a WeakMap for column mappings, limits pending/live to 16,384 units and echo to 256 entries / 1,024 units, and clears them on disposal. Fixed scratch cells may retain a bounded previous combined string. Default contrast ratio 1 bypasses the renderer's color-contrast cache; this audit does not cover a different contrast policy. | Width accounting/peaks/evictions/measurements, renderer rows/nodes, accessible rows/row units, pending/live/echo units, total attached DOM nodes. DOM counts do not count detached native layout/font/GPU allocations or prove a heap-size bound. |
| Queues and callbacks | Four output slots, one ≤4,096-byte acknowledged write, one producer fragment, 16 input slots and copy/paste caps remain. Patched `WriteBuffer` fences dedicated failures. Upstream synchronous parse completion clears queued data/callback arrays; render debounce uses one animation frame and accessibility debounce one trailing timeout. Resize cleanup uses an idle task queue. Registered wrapper handlers are fixed per terminal, not per output fragment. | Native pending/input/ack state and joined-task flags; JS write chunks/callbacks/pending units, render frame/callback count, accessibility refresh flag and cleanup tasks/callbacks. These inspect known queues, not an exhaustive enumeration of browser event-loop/native callbacks. |
| Closed sessions | Width-cache disposal empties its map, accounting and spans; render frames are canceled and terminal nodes removed. Disposal need not zero every old buffer object: after references are released, collection is a separate step. The closed fixture still holds up to four queued chunks until the next open replaces its channel. The outer bridge holds only the latest numeric stats object. | Closed snapshots record empty attached document, disposed cache/write/render values and joined native tasks. Up to four weak references per aggregate terminal observe wrapper/core/buffer-set/renderer reachability; finalization observes the public wrapper only. Counters do not root terminal objects, and no forced GC is used. |

This model separates currently reachable payload from retired objects awaiting collection, transient allocations and process memory. It does not add the cells, maps, DOM copies and RSS into an alleged exact total: JS string representations/sharing, object headers, allocator slack, collection timing, native renderer/font caches, GPU resources and shared framework pages differ. Metadata snapshots themselves create temporary sets, entries arrays and translated screen strings. The audit covers the configured patched surface and these dimensions/handlers, not every xterm option or every edit/reflow combination.

### Native measurement conditions and scope

Host: Apple M5 Pro / Mac17,9, 48 GiB RAM, arm64 macOS 26.7.1 (25G241), .NET SDK 10.0.402 / runtime 10.0.13, pinned Uno 6.7.135, Release self-contained `osx-arm64` development bundle, system WKWebView / WebKit **21624 (21624.5.1.11.3)**. The version and bundle hashes are recorded with the results. The terminal uses 80×24, 200 scrollback rows, 14px font and enabled accessibility. Other existing desktop/browser processes remain present; none was intentionally launched during each measured fixture. New helper PIDs are separated from the prelaunch inventory; their launchd parentage means these are attributable candidates, not a definitive ownership proof if another WebKit client starts concurrently.

`ps` samples app RSS separately from WebContent, GPU and Networking helpers at nominal one-second intervals. Native `vmmap -summary` and `footprint -p` run at selected empty/populated/closed/idle stages; capture manifests record their actual start times and any failures. Those calls lengthen some RSS intervals and consume measurement resources. They report VM/physical categories, not retained JS dominators or per-xterm allocation ownership; RSS and physical footprint are different metrics and must not be added as independent allocations. Weak references/finalizers provide selected JS collection evidence only. No heap dump, forced managed/browser collection, memory-pressure trigger, debugger enablement, privacy relaxation or allocation-stack logging was used.

### Recorded aggregate results

The sustained producer completed **about 601.1 seconds** in generation **2**, with exactly **1,606,233 generated/delivered/received bytes**, **19,200 synthetic lines** and **76,800 short combined-cell candidates**. The subsequent alternate-screen population brought acknowledged bytes to **1,607,801**. The finite ceiling was not approached, and neither allocation-failure path fired. There was no reset/replacement inside the sustained generation. The complete native runner passed in **661.5 seconds**, including stable checkpoints, post-close idle and compatibility reopening.

At every 30-second paced sample, normal history retained **224 logical lines / 225 held line objects**, including **one cached non-logical blank line**, with **216,000 bytes** of typed-array backing. Both screens populated retained **224 normal + 24 alternate lines / 249 held line objects / 239,040 backing bytes**. View-byte and backing-byte totals matched; no oversized historical backing was observed in this fixed-dimension workload. During pacing, combined maps held **1,106 entries / 3,108 UTF-16 units**, including **26 stale entries**; extended maps held **1,050 entries**, including **26 stale entries**. With both screens populated these became **1,129 / 3,154 units** and **1,487 extended entries**, still with **26 stale entries of each kind**. Maximum combined-cell length was **3 units**, far below the independent 256-unit admission budget.

Paced cache holdings were **512 entries / 2,039 units**, with lifetime key-unit peak **2,048** and empty measurement spans. At final population the cache recorded **77,194 evictions / 77,735 measurements**; render scheduling can change these activity totals on repetition. Accessible rows stayed **24**, row-copy text **910 units** while normal was active and **438** in the populated alternate screen; pending/live aggregate was **882 units**, echo entries/units **0**. The title/icon total was **21 units**, title stacks **0 entries / 0 units**, parser payload **0**, parser ground state **0**, parameter count **1**, decode backing **16,384 bytes** and paused state false. Sampled write chunks/callbacks/pending units, render callbacks and resize-cleanup tasks/callbacks were **0**. An accessibility trailing-refresh flag can remain true while output is flowing. Renderer rows stayed **24**; its sampled descendant count varied **203–233**, and the document count **256–286**, rather than growing per tick. These are sampled current holdings, not hidden browser allocations or worst-case admission-boundary evidence.

After close, both native tasks were joined, no acknowledgment/input item remained pending, attached terminal nodes were **0**, document elements **9**, and disposed width entries/units, write chunks/callbacks and render-frame pending state were **0/false**. The closed bounded channel still held **4 chunks**, replaced at the next open. All **4** weakly observed wrapper/core/buffer-set/renderer roots were initially alive; at the **30-second post-close idle** checkpoint they were **0**, with **1 wrapper finalization**. Natural collection of these selected roots is positive reclamation evidence. It neither identifies every detached browser/native allocation nor promises a collection deadline.

For this sustained run, **627** RSS samples had actual intervals **1.03–3.61 seconds** (nominal one second; native tooling causes the longer intervals). After the first five launch seconds, app RSS ranged **240.7–255.3 MiB**, WebContent **48.6–205.5 MiB**, GPU **28.9–43.4 MiB**, Networking **13.05–13.16 MiB**. There was one new PID per helper role throughout this run: app **317**, WebContent **322**, GPU **320**, Networking **321**. App and helper figures are separate; they are not an exclusive-memory sum.

At approximately 60 / 180 / 300 / 480 seconds after launch, WebContent RSS was **134.7 / 181.1 / 182.5 / 201.0 MiB**. Growth after the early near-plateau remained visible despite identical sampled payload counts. At the populated, immediately closed and 30-second-idle native-tool requests, its RSS was approximately **205.3 / 205.2 / 204.6 MiB**. Its `vmmap` physical footprint at the corresponding slightly later captures was **137.3M / 136.2M / 135.6M**, versus **31.3M** at empty baseline (reported tool units). These measures include different native/shared/allocator holdings: selected roots disappearing did not return process RSS to baseline. No RSS plateau or return-to-baseline threshold was used as acceptance.

The **12-cycle** native runner passed in **173.0 seconds**, using app **3610**, WebContent **3617**, GPU **3615** and Networking **3616** throughout that application lifetime. Every populated checkpoint delivered **33,725 bytes** and had the same two-screen/line/map/a11y/title holdings described above. Per-session cache activity varied with scheduling; bounds and actual evictions passed each time. Every close joined pump/producer, preserved the stale-write byte count, removed the terminal document and left the expected bounded four queued chunks. At eight-second idle, cycles **4 and 7** still had **3 and 2** weak roots respectively; later checkpoints cleared them. At cycle 12 idle there were **0 observed retired roots / 12 wrapper finalizations**. Collection was observed, not required by an assertion or forced.

Across this run, **152** RSS samples had actual intervals **1.03–3.63 seconds**. After five launch seconds, app RSS ranged **240.2–253.1 MiB**, WebContent **55.8–198.4 MiB**, GPU **31.5–45.0 MiB**, Networking **13.02–13.11 MiB**. The comparable tool-request checkpoints below are actual `ps` samples; VM captures followed within the held state. Source counters use a clock starting at browser readiness, while RSS/capture clocks start at process launch; the committed capture metadata preserves both origins.

| Repeated-session checkpoint | App RSS MiB | WebContent RSS MiB | WebContent `vmmap` physical footprint |
| --- | ---: | ---: | ---: |
| Empty baseline | 240.4 | 55.9 | 32.9M |
| Cycle 1 populated | 242.8 | 90.8 | 85.0M |
| Cycle 1 closed | 242.9 | 90.4 | 82.6M |
| Cycle 1 idle | 243.1 | 90.2 | 82.1M |
| Cycle 12 populated | 251.2 | 197.9 | 471.2M |
| Cycle 12 idle | 251.1 | 197.4 | 470.5M |

The final idle WebContent native footprint had a reported **685.7M peak**, despite selected JS roots having cleared. Its `footprint` category summary reported approximately **371 MB dirty owned unmapped graphics / 245 MB reclaimable graphics**, plus **89 MB dirty WebKit malloc / 69 MB reclaimable**, within a **470 MB** total. The first idle `vmmap` WebKit Malloc row had **51.4M resident / 31.6M dirty**, versus **157.5M resident / 88.7M dirty** at final idle. The JS VM Gigacage dirty column was **448K** at both; that named VM region is not the complete JS heap. Large reserved Gigacage/JIT virtual address ranges are not resident payload. Native tools use their reported M/MB units here; no cross-process or cross-metric total is asserted.

This is concrete evidence of native/browser memory remaining substantially above baseline across repeated closes. The graphics category is an attribution clue, not proof that an xterm object, Uno, the GPU helper or a particular WebKit subsystem leaked. Reclaimable classification is not a promise about when memory will return. App `GC.GetTotalMemory(false)` also includes uncollected managed allocations and excludes native holdings: cycle-idle values rose from **32.69 MiB** to **38.10 MiB**, dropped naturally to **31.54 MiB** at cycle 8, and ended **35.14 MiB** at cycle 12. No forced managed collection was used. Neither those managed values nor observed JS finalization resolve the native graphics/allocator retention.

### Reproduction, verification and remaining evidence boundary

Publish the existing development wrapper, then run the fixed named workloads:

```sh
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app --workload aggregate-sustained --workload aggregate-cycles --output artifacts/terminal-surface/aggregate-final-v2
```

The outer deadline is **840 seconds only for `aggregate-sustained`**; other named cases retain **660 seconds**. Its producer has a finite twelve-minute deadline. Ordinary fixtures still close at **1 MiB**, named synthetic diagnostics at **64 MiB**. No unrestricted mode, periodic reset, silent truncation or new production tuning policy was added.

Raw metadata, RSS samples, native category summaries and capture timing manifests are in ignored `artifacts/terminal-surface/aggregate-final-v2`. The committed [aggregate metadata summary](../tools/TerminalSurfacePrototype/Results/aggregate-2026-10-15.json) retains host/runtime conditions, binary/assembly/input hashes, renderer pins, selected counter checkpoints, process ranges, capture timings/category lines and outcomes. Exact bytes, workloads and assertions reproduce; asynchronous render/cache activity, collection timing and memory values can vary. Preliminary interrupted runs under `aggregate-v1` and `aggregate-final` refined instrumentation/checkpoint dwell time and are not acceptance evidence.

With the recorded final wrapper and unchanged pinned renderer, **all 17 selected native diagnostics passed**: `aggregate-sustained`, `aggregate-cycles`, `combined`, `combining`, `a11y-boundaries`, `lifecycle`, `cutoff`, `width-cache`, `width-cache-long`, `normal`, `alternate`, `links`, `osc`, `dcs`, `csi`, `a11y` and `attributes`. These retain byte-fragmented Unicode/ANSI, synthetic native clipboard/control-byte/resize, both-screen edits/reflow, allocation failures and delayed replacement, stale-generation fences, parser recovery and the ordinary cutoff. Raw fixture checks, fresh pinned source checks and byte-identical rebuild passed. Locked restore, Release prototype build/self-contained macOS publish and Release solution build passed with **zero warnings/errors**; JavaScript/Python/shell syntax and whitespace checks passed. The earlier 398-group simulated suite is historical evidence above, not a rerun claimed for this diagnostic-only change. Production source/dependencies/navigation and durable Intent/Engineering remain untouched.

Remaining gaps are specific:

- No retained JS heap/dominator or native allocation-backtrace graph was obtained with the existing disabled-devtools configuration. Weak-root observations cover four selected objects per session; the native graphics and WebKit malloc growth has no object/path-level cause or established reclamation bound.
- This is one ten-minute generation and one twelve-session sequence on one macOS host, with eight-/thirty-second post-close idle windows. Longer lifetimes, repeated independent baselines, other memory-pressure/load conditions and recovery of native footprint remain unmeasured. Neither a short plateau nor all selected weak roots clearing establishes sustained whole-browser memory acceptance.
- The source model is conditional on the configured fixed dimensions, handlers, no addons and default contrast policy. Worst-case simultaneous long combined cells, large completed titles, all edit/reflow/resize combinations, browser font/layout/GPU caches and an exhaustive callback/resource census are not measured by these aggregate workloads. Individual failure-boundary checks remain separate evidence.
- Windows WebView2 runtime/input/accessibility, privacy/crash/network behavior, actual screen-reader use, real Engine duplex interaction and product CPU/working-set/latency acceptance remain outstanding. No renderer promotion, ordinary-cutoff removal or component decision follows from this experiment.

## Baseline retained-state investigation, 2026-10-15 (stock xterm asset)

**Baseline result: bounded long-running terminal memory was not established. Keep the ordinary 1 MiB safety cutoff.** The following historical measurements used the original npm asset and OSC 8 hook. The follow-up experiment above replaces only the prototype JS with a source-patched build; it does not establish bounded overall memory or a production policy. CSS is unchanged.

### Exact pinned source findings

The npm 5.5.0 archive listed below contains the TypeScript sources as well as the exact bundled JavaScript. Its archive and asset SHA-256 values were rechecked. This investigation uses that source, the [5.5.0 option declarations](https://github.com/xtermjs/xterm.js/blob/5.5.0/typings/xterm.d.ts), [parser-hook contract](https://xtermjs.org/docs/guides/hooks/) and [flow-control guide](https://xtermjs.org/docs/guides/flowcontrol/). The last guide distinguishes asynchronous write buffering from processing acknowledgments; it does not promise that parsed terminal state or a browser process has a fixed memory footprint.

| Retained state | Pinned behavior and consequence |
| --- | --- |
| Bridge delivery | Four chunks of at most 4096 bytes in C#, one browser write until its parse callback, 16 input slots. These bounds constrain in-flight delivery, independently of already-parsed state. The acknowledgment no longer constructs a whole-screen string on every write; explicit metadata requests still inspect the screen. |
| Combining cells | [`BufferLine.addCodepointToCell`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/buffer/BufferLine.ts) appends to `_combined[index]` without a string limit. A single cell can grow while line count stays constant. Maps can also retain an overwritten cell's previous combined string until replaced by another combined string, the line is filled, or the entry is resized away/trimmed; the instrumentation counts stale entries in both buffers. No public 5.5.0 option caps these strings. |
| Renderer width cache | The default DOM renderer's [`WidthCache`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/browser/renderer/dom/WidthCache.ts) retains a map keyed by nontrivial cell strings and font variants without entry eviction. Overwriting short, different combining cells can grow this map even when current combined-cell length stays tiny. Measurement also places 32 repetitions of a cell string in a hidden DOM span, amplifying a pathological long cell. Font/character-size changes clear the cache, but normal output and row trimming do not provide a fixed cache budget. |
| Incomplete sequences | [`OscHandler`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/parser/OscParser.ts) and [`DcsHandler`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/parser/DcsParser.ts) accumulate registered-handler payloads until termination, with [`PAYLOAD_LIMIT = 10000000`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/parser/Constants.ts) UTF-16 units **per handler**. Overflow clears the stored string and ignores the rest of that payload. One fragment's conversion/concatenation can transiently exceed the threshold. Unsupported sequences use a fallback rather than indefinite accumulation. [`Params`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/parser/Params.ts) defaults to fixed 32-entry parameter/subparameter arrays with integer saturation; intermediate collection uses numeric shifts. An incomplete CSI is not an arbitrarily growing string. |
| OSC 8 links | [`OscLinkService`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/services/OscLinkService.ts) registers a link and row marker at each distinct opening. Records disappear when their markers are disposed, rather than whenever their visible text is overwritten. Thousands of different openings on one untrimmed row therefore grow link maps, marker arrays and listeners. The existing inert activation handler alone did not prevent this. |
| Alternate screen and attributes | [`BufferSet`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/buffer/BufferSet.ts) maintains normal and alternate buffers; returning to normal explicitly clears alternate markers and rows. Repeated extended underline/color attributes replace position-keyed entries in `BufferLine`, rather than appending a global attribute history. This is a source explanation plus the focused workload below, not proof for every resize/reflow/edit sequence. |
| Accessibility and titles | With the existing `screenReaderMode:true`, [`AccessibilityManager`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/browser/AccessibilityManager.ts) limits announcements by line feeds, not character count. Repeated printable characters plus carriage returns accumulate in pending/live-region strings with a nearly empty screen. Its key-echo suppression queue also has no explicit cap (source finding; no no-echo keyboard stress claim). Turning accessibility off is a supported option but removes that functionality and leaves combining storage unbounded. [`InputHandler`](https://github.com/xtermjs/xterm.js/blob/5.5.0/src/common/InputHandler.ts) also retains titles and two ten-entry title stacks; individual OSC payload limits bound these, potentially at a large size. |

### Narrow experimental mitigation

`surface.js` now registers `term.parser.registerOscHandler(8, () => true)`. The documented reverse-order/handled hook semantics consume completed OSC 8 metadata before the built-in hyperlink handler. Visible link text still renders, but hyperlink identity and hover metadata are deliberately discarded in this experiment; activation was already blocked. The fixed `stock-links` and `stock-alternate` baselines alone omit this hook. OSC 52 blocking, opaque frame, CSP, closed message vocabulary, explicit copy, generation fences, acknowledged writes and joined terminal cleanup remain in place. The hook does not limit an unfinished OSC payload before its upstream cap, and is not represented as a general memory fix.

A small licensed source change could reject further combining additions before append, cap accessibility pending/live/echo state, and evict renderer width-cache entries. It would need explicit failure/announcement semantics and tests across both screens, overwrites, insert/delete, reflow and resize. No supported option or complete reviewed source change was established here that preserves these behaviors and bounds the whole browser surface. Truncating combining output, disabling accessibility or periodically resetting a healthy terminal to obtain a plateau was not adopted. The follow-up experiment above now supplies the checksum-pinned combined-cell patch and focused checks. The later accessibility and width-cache experiments above add finite holding budgets; sustained native browser-memory evidence remains further work; bridge or source counters alone cannot close the question.

### Reproducible native checks

`RetentionChecks.cs` runs inside the actual Release Uno macOS development application. Every output workload traverses `ControlledTerminal` → four-slot channel → Application output read → base64 native script → trusted bridge → opaque frame → xterm `Uint8Array.write` → native parsing acknowledgment. It does not feed xterm directly in a Node/headless substitute. Bulk adversarial fragments are 4093 bytes, cutting UTF-8 scalars and escapes across writes; the historical paced width-cache case explicitly split its last combining scalar between two writes. The current width-cache fixtures replace that baseline workload with the rendered-batch checks above. The ordinary compatibility sample separately splits every byte.

Run after publishing the development wrapper:

```sh
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app
# Select one fixed workload, or repeat --workload for several:
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app --workload combining --workload links
```

The `.app` executable itself accepts `--check-retention <name>`. Named workloads are fixed source fixtures, not arbitrary byte/file/URL inputs. The native runner disables interaction, refuses non-macOS hosts and has a finite 64 MiB diagnostic ceiling; `cutoff` exercises the unchanged 1 MiB budget. Metadata-only logs and one-second RSS samples are written under ignored `artifacts/terminal-surface/retention`. No terminal content, input, selection or exception message is logged. The `lifecycle` case deliberately exercises the real clipboard with the fixed seven-character selection.

| Fixed case | Observed native result |
| --- | --- |
| `lifecycle` | Byte-fragmented Unicode and ANSI/cursor editing passed; explicit seven-character selection/copy/paste and control `0x03` reached the boundary (eight input bytes total). A 4097-byte paste was rejected. Resize reached 100×30 in browser and boundary. Holding output filled 4/4 slots and blocked the producer; replacement joined it, rejected a stale generation write, and retained only the new 189-byte sample. Repeated close/reopen passed. |
| `cutoff` | The ordinary 100,000-line producer hit the unchanged 1 MiB ceiling; the canceled producer and pump joined, then a fresh 189-byte fixture reopened. |
| `normal`, `sustained` | Exact generated/delivered/received counts matched: 14,100,000 bytes in the burst; 56,600,000 bytes / 1,200,000 lines over approximately 151 seconds in the paced case. All measured sustained output stayed in generation 2, with no reset/replacement. Final normal buffer: 224 rows, no alternate rows or links, Unicode retained. |
| `combining` | 262,145 output bytes (`a` plus 131,072 U+0301 marks) left **131,073 UTF-16 units in one cell**, plus the same number in the accessibility live region, with only 24 screen rows. |
| `width-cache` | 1200 short combining cells overwrote the same position over about 25 seconds. The last cell held only three UTF-16 units, yet **544 width-cache entries / 1622 key units** remained after recovery/idle, with 24 rows. Rendering can coalesce intermediate updates, so this count depends on scheduling; it demonstrates retention beyond the current cell rather than a one-entry-per-write contract. |
| `stock-links`, `links` | The identical 405,780-byte workload repeatedly opened 8000 unique links at one cell. Stock retained **8000 link records and 8000 markers**; the hook retained **zero** of each. Both retained 24 rows. |
| `stock-alternate`, `alternate` | 8000 enter/write/leave cycles, including a link and combining mark, ended with zero alternate rows, links or markers in both variants. These specific transitions released alternate metadata; they do not make same-row normal-buffer link churn bounded. |
| `attributes` | 50,000 changes to extended underline style/color at one cell left one extended-attribute entry, with 24 rows. |
| `osc`, `dcs` | An unfinished OSC 2 title and DCS `$q` each received 12 MiB of payload. Sampled payload storage rose toward the upstream limit, then cleared with `parserLimited:true` while still inside the sequence. CAN plus the fixed recovery line returned to ground state and displayed the recovery marker. |
| `csi` | An unfinished CSI with 12 MiB of digits retained one parameter and no payload string. CAN recovered normally. |
| `a11y` | 524,288 bytes of `X` plus carriage return left **262,144 live-region characters**, despite one overwritten visible cell and 24 rows. |

Every adversarial workload also accepted the fixed recovery marker through the same channel/bridge and returned to parser ground state. All 15 named cases passed in those baseline native runs. The baseline `combining` expectation is superseded by explicit early failure in the patched build. The final metadata-correlation/cache instrumentation was additionally checked with lifecycle, cutoff, normal, width-cache, combining and both link variants. The harness verifies generated byte totals, control/input counts, dimensions, generation fencing and selected private counters; the raw-byte fixture checks remain separate and pass. These are selected behavior checks, not exhaustive VT, Unicode, input-method, mouse-selection or accessibility acceptance.

Host for memory observations: Apple M5 Pro, Mac17,9, 48 GiB RAM, arm64 macOS 26.7.1 (25G241), .NET SDK 10.0.402, the pinned Uno/WKWebView Release self-contained development bundle, 80×24 terminal, 200 scrollback rows and existing 14-point font settings. Synthetic data only. No forced managed or browser GC was used.

In the recorded paced run, the app RSS ranged from **238.0 to 252.1 MiB**. The newly appearing WebContent process (PID 88509) had sampled RSS of **80.3–222.5 MiB**: approximately 198.4 MiB at 31 seconds, 218.9 at 62, 220.8 at 93 and 222.5 at 154 seconds. The final roughly 30 seconds were nearly flat. Its GPU and networking helpers were separately about 38 and 13 MiB, not part of the app RSS. This is a short observed plateau for this workload, not evidence of a lifetime bound or the product working-set target.

The final combining case sampled WebContent RSS at **296.2–364.1 MiB** from only 262,145 generated output bytes; the paced short-cell width-cache case sampled **50.3–70.5 MiB**. The incomplete OSC and DCS cases sampled peaks of **436.6 and 418.0 MiB** respectively, although their handler strings had cleared after reaching the upstream limit. The carriage-return accessibility case sampled **100.6–131.7 MiB**. These RSS observations include rendering, browser/runtime caches, diagnostic work and collection timing; they do not attribute a leak to one xterm object. In particular, removing link records does not imply a reproducible RSS decrease across fresh processes.

Measurement limits: `ps` samples one-second RSS, misses faster peaks and does not identify retained JS heap, native glyph/layout storage, shared pages, allocator high-water marks or GC eligibility. WKWebView XPC processes have launchd as parent; the script records PIDs appearing after each app launch as **candidates**, not authoritative ownership. One new WebContent candidate appeared in each recorded case; concurrent WebKit application launches could invalidate that attribution. The app's own RSS and `GC.GetTotalMemory(false)` exclude the browser's JS heap. Metadata inspection translates screen text and traverses private 5.5.0 maps, so instrumentation itself contributes allocations. Fresh processes per workload and a three-second idle sample do not establish hour-scale stability, repeated-session reclamation, leak attribution or an engine-independent memory ceiling.

Windows execution, Edge runtime memory/privacy behavior and real Engine interaction remain separate outstanding evidence. Nothing in this experiment establishes the workstation memory target, Windows conformance or a production terminal selection.

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

SHA-256 (the original JS is a historical baseline; the current modified asset is pinned separately above):

```text
xterm-5.5.0.tgz bd954fa721872170188cc5d7e83e88db3c83c9a18a4e8d24c2783d26491f59d2
stock xterm.js 1f991ac3b4b283ebf96e60ae23a00a52765dd3a2e46fa6fdda9f1aab032f7495
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
- Accessibility and width-cache source/rebuild checks and selected native synthetic fixtures: pass as detailed above; overall browser/native memory and screen-reader acceptance remain unavailable.
- Focused `--check-fixture` checks: pass; native retention/lifecycle/cutoff checks and RSS observations are recorded above; JavaScript, Python and macOS wrapper syntax checks: pass.
- Windows x64 self-contained cross-publish with locked restore: pass; runtime execution unavailable.
- Existing Release solution build: pass, zero warnings/errors.
- Existing independent regression suite: **398/398 groups passed on macOS 26.7.1**. This remains simulated protocol/Application evidence, not real Windows Docker evidence.
