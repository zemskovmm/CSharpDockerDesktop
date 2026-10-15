# Isolated terminal surface prototype

Development-only Uno Skia executable, outside the production solution and navigation. Uses `IContainerTerminal` with a controlled bounded echo fixture; no Docker endpoint, shell, command default, localhost API, transcript storage or reporting service.

Read [the comparison, licenses, evidence and limitations](../../docs/terminal-surface-research.md) before running. The optional `WindowsHost` source patch compiles crash-report suppression for both Windows backends, but Windows runtime privacy is unverified. Ordinary Windows builds fail closed before browser startup; use the documented patched-host procedure for synthetic fixtures only. This is not a product terminal or component selection.

```sh
dotnet restore tools/TerminalSurfacePrototype/TerminalSurfacePrototype.csproj -p:Configuration=Release --locked-mode
dotnet build tools/TerminalSurfacePrototype/TerminalSurfacePrototype.csproj -c Release --no-restore
dotnet tools/TerminalSurfacePrototype/bin/Release/net10.0-desktop/TerminalSurfacePrototype.dll --check-fixture
dotnet run --project tools/TerminalSurfacePrototype/TerminalSurfacePrototype.csproj -c Release --no-build
```

On macOS, `sh tools/TerminalSurfacePrototype/run-macos.sh` creates and opens a development app wrapper. Dependencies and notices live under this folder only; production projects have no terminal-surface dependency.

For the reproducible native retention investigation, publish that wrapper first, then run:

```sh
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app
```

This runs fixed synthetic workloads through the actual Application fixture, four-slot channel, sandbox bridge and acknowledged xterm byte writes. It writes metadata-only logs and one-second RSS samples under ignored `artifacts/terminal-surface/retention`. Use `--workload lifecycle`, `--workload cutoff`, or another named workload to select checks; the historical `sustained` case lasts about three minutes; `aggregate-sustained` lasts at least ten minutes in one generation. Each native runner also accepts `--check-retention <name>`. The runner refuses non-macOS hosts and disables interaction while checking. It exercises the real native clipboard with the fixed seven-character fixture selection.

Ordinary interactive fixtures retain the **1 MiB safety cutoff**. Only these finite, named synthetic diagnostics receive a **64 MiB total-output ceiling** to measure behavior beyond 1 MiB; there is no unrestricted session mode. The experimental OSC 8 hook now consumes inactive link metadata as well as preventing activation. The `stock-links` and `stock-alternate` fixed diagnostics temporarily omit that hook to reproduce the pinned upstream baseline. The checksum-pinned source experiment now rejects combined cells above 256 UTF-16 units before append, with explicit session failure and joined cleanup. The ordered accessibility source patch also caps pending/live announcements at 16,384 UTF-16 units in aggregate, and echo retention at 256 entries / 1,024 units / 16 units per entry. Over-budget parsing, render callbacks and keyboard handling close the fixture with the fixed `accessibility-limit` failure and serialized cleanup; accessibility stays enabled. These are research budgets, not production policy. The third source patch bounds DOM width-cache retention at 512 entries / 32,768 key UTF-16 units, evicting before insertion and remeasuring on misses. Hidden measurement text clears after each measurement. Overall browser memory and real screen-reader acceptance remain unresolved. Source rebuild/check instructions are in [XtermSource/README.md](XtermSource/README.md); the `combined` native workload covers both screens, edits/reflow and failure/replacement, and `combining` now checks early failure of the former 131,073-unit reproducer. The `a11y` carriage-return and `attributes` workloads now expect an early accessibility failure, joined producer/pump and successful reopening; their historical unbounded-retention counts remain baseline evidence only. `a11y-boundaries` covers mixed pending/live exact limits, UTF-8 fragments, tabs, no-echo synthetic DOM keys, echo consumption, blur, row navigation, a keyboard failure with a parser write outstanding, delayed-render fault injection, disposal and failure/replacement. All accessibility holding counts are metadata only. Run it with `--workload a11y-boundaries`. Neither source counters nor RSS samples prove bounded browser memory; see the research document for results, attribution limits and next evidence.

The optional Windows experiment is built with `sh tools/TerminalSurfacePrototype/WindowsHost/build-windows-host.sh /path/to/pinned-uno-source.tar.gz`. Read the research document for the archive checksum, preparation/build steps, source notices, strict initialization, fresh-profile retention and joined-cleanup limitations. No Edge runtime is bundled, and no Windows privacy or terminal conformance result is implied by cross-publishing.

The native `width-cache` and `width-cache-long` diagnostics render batches of 64
distinct cells across regular, bold, italic and bold-italic: 3,072 three-unit cells
and 1,024 exact 256-unit cells respectively. They require rendered-cell, numeric
eviction, entry/unit high-water, measured-width and accounting evidence; writes
may coalesce into a render. Deliberate blur/refocus between batches clears prior
announcements while accessibility stays enabled (a long batch reaches its 16,384
unit limit). Both check repeated hits, evicted-cell remeasurement, font changes,
resize, recovery, close and replacement. Run with `--workload width-cache
--workload width-cache-long`; the research document records observed counts.
Cache counts do not establish overall browser/native-memory or sustained-session
conformance; production component selection, Windows privacy/runtime behavior and
real Engine interaction remain unresolved.

The aggregate retention baseline uses `--workload aggregate-sustained --workload
aggregate-cycles`. The first emits 1,200 fixed half-second ticks, with 16
Unicode/ANSI lines and 64 short combined cells per tick, without resets or focus
clearing. The second runs 12 open/populate/close/idle cycles in one native
application and browser lifetime. Each populates normal history and the alternate
screen, churns bounded cache entries, blocks a producer on the four-slot channel,
then cancels and joins producer/pump and probes a stale write. Both reopen the
ordinary compatibility fixture afterward. No workload accepts external data.

Metadata now includes circular-list backing slots (including non-visible held
lines), the cached blank line, deduplicated cell array backing bytes, stale
combined/extended map entries, titles/parser state, renderer/accessibility rows,
write/render/cleanup callbacks, and closed-surface checkpoints. Diagnostic weak
references and finalization counts observe natural collection without retaining
terminal roots; absence of finalization is not proof of a leak. Snapshots allocate
temporary inspection arrays and strings, without logging their contents.

The runner records host conditions, assembly hash, app RSS separately from newly
observed WebContent/GPU/Networking helper PIDs, and `vmmap -summary` / `footprint`
at selected aggregate checkpoints. Stable checkpoint dwells allow native capture;
capture manifests record the actual delay from the requested stage. The nominal
RSS interval is one second, with longer gaps during native tooling. These helpers
are PID candidates because XPC uses launchd parentage; concurrent WebKit launches
would confound attribution. Native categories do not identify retained JS paths.
There are no heap dumps, forced collections or debugger/privacy changes. Only
`aggregate-sustained` receives an outer 840-second deadline (other cases retain
660 seconds); its producer has a finite twelve-minute deadline. The ordinary
1 MiB cutoff and named 64 MiB diagnostic ceiling are unchanged. See the research
document and committed metadata summary for observed results and remaining gaps.

## Matched native-memory controls

Publish the development bundle above, then run the three variants in separate
fresh actual Uno macOS applications (the runner launches them sequentially):

```sh
python3 tools/TerminalSurfacePrototype/run-retention-checks.py artifacts/terminal-surface/macos/TerminalSurfaceResearch.app --workload aggregate-empty-cycles --workload aggregate-idle-cycles --workload aggregate-cycles --output artifacts/terminal-surface/matched-controls-2026-10-15
python3 tools/TerminalSurfacePrototype/summarize-controls.py artifacts/terminal-surface/matched-controls-2026-10-15 tools/TerminalSurfacePrototype/Results/matched-controls-2026-10-15.json
```

All three retain one native host, WKWebView and opaque sandbox frame throughout
12 cycles, with the same embedded assets/CSP/bridge, native fixture/channel,
80×24 dimensions and window geometry. `aggregate-empty-cycles` starts/closes
bridge sessions without constructing xterm; it still loads/parses the bundled
library and CSS. It does **not** recreate the iframe, navigate WKWebView, unload
the library or emulate a different browser. `aggregate-idle-cycles` constructs,
opens, focuses and disposes xterm with accessibility enabled but no output.
`aggregate-cycles` remains the fixed populated reference: 24 paced ticks,
normal+alternate screens and width-cache churn, with no renderer change.

Measurements begin before any terminal has been constructed. Controls wait the
same 24×60 ms output window plus 1,200 ms render-settle period. The populated
reference also includes actual delivery/parse overhead. The shared `populated`
stage name denotes the active-phase checkpoint for controls, which receive zero
terminal output. Every variant then
holds native delivery, blocks the same finite producer on four channel slots,
and cancels/joins producer and pump; those control bytes never reach xterm.
Every close tests stale-generation and current-closed-generation writes and
requires unchanged received bytes and no attached terminal. Selected first/final
populated and idle stages dwell eight seconds for native tools. First/final
closed stages have an eight-second capture dwell followed by the ordinary
eight-second idle; other cycles idle eight seconds. Corresponding stages have
identical cadence across variants; captures remain sequential observations.

Counters record starts/closes, xterm construction/open/disposal/render events,
accepted/rejected writes, current DOM geometry/counts and existing bounded
retention metadata. After the measurement window, each variant explicitly
replaces an idle terminal with the empty control, proves no terminal remains,
then runs ordinary compatibility/replacement and fixed security probes. Thus the
empty **measured** sequence constructs zero terminals, while its later checks
intentionally construct terminals. No forced collection, sensitive reporting or
production alternative mode is introduced. Read the research report and the
metadata summary for observations, attribution limits and remaining evidence.
