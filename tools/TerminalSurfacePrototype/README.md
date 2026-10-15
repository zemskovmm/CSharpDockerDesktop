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

This runs fixed synthetic workloads through the actual Application fixture, four-slot channel, sandbox bridge and acknowledged xterm byte writes. It writes metadata-only logs and one-second RSS samples under ignored `artifacts/terminal-surface/retention`. Use `--workload lifecycle`, `--workload cutoff`, or another named workload to select checks; the sustained case lasts about three minutes. Each native runner also accepts `--check-retention <name>`. The runner refuses non-macOS hosts and disables interaction while checking. It exercises the real native clipboard with the fixed seven-character fixture selection.

Ordinary interactive fixtures retain the **1 MiB safety cutoff**. Only these finite, named synthetic diagnostics receive a **64 MiB total-output ceiling** to measure behavior beyond 1 MiB; there is no unrestricted session mode. The experimental OSC 8 hook now consumes inactive link metadata as well as preventing activation. The `stock-links` and `stock-alternate` fixed diagnostics temporarily omit that hook to reproduce the pinned upstream baseline. The checksum-pinned source experiment now rejects combined cells above 256 UTF-16 units before append, with explicit session failure and joined cleanup. The ordered accessibility source patch also caps pending/live announcements at 16,384 UTF-16 units in aggregate, and echo retention at 256 entries / 1,024 units / 16 units per entry. Over-budget parsing, render callbacks and keyboard handling close the fixture with the fixed `accessibility-limit` failure and serialized cleanup; accessibility stays enabled. These are research budgets, not production policy. Renderer caches, overall browser memory and real screen-reader acceptance remain unresolved. Source rebuild/check instructions are in [XtermSource/README.md](XtermSource/README.md); the `combined` native workload covers both screens, edits/reflow and failure/replacement, and `combining` now checks early failure of the former 131,073-unit reproducer. The `a11y` carriage-return and `attributes` workloads now expect an early accessibility failure, joined producer/pump and successful reopening; their historical unbounded-retention counts remain baseline evidence only. `a11y-boundaries` covers mixed pending/live exact limits, UTF-8 fragments, tabs, no-echo synthetic DOM keys, echo consumption, blur, row navigation, a keyboard failure with a parser write outstanding, delayed-render fault injection, disposal and failure/replacement. All accessibility holding counts are metadata only. Run it with `--workload a11y-boundaries`. Neither source counters nor RSS samples prove bounded browser memory; see the research document for results, attribution limits and next evidence.

The optional Windows experiment is built with `sh tools/TerminalSurfacePrototype/WindowsHost/build-windows-host.sh /path/to/pinned-uno-source.tar.gz`. Read the research document for the archive checksum, preparation/build steps, source notices, strict initialization, fresh-profile retention and joined-cleanup limitations. No Edge runtime is bundled, and no Windows privacy or terminal conformance result is implied by cross-publishing.
