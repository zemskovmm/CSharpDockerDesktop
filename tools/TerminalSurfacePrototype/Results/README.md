# Aggregate retention observations

`aggregate-2026-10-15.json` is metadata from the actual macOS Release Uno/WKWebView
application, using only the fixed `RetentionChecks.cs` fixtures. The research
report in `docs/terminal-surface-research.md` explains source bounds, native tool
attribution, measured growth, verification and remaining gaps.

The JSON records run conditions and input/assembly hashes; renderer input pins;
per-stage metadata for the two aggregate workloads; separate app/helper RSS
ranges; capture timing and selected VM/footprint category lines; and outcomes
for the selected regression workloads. It contains no terminal text, title text,
commands, input or selection. Raw local logs/captures remain in ignored
`artifacts/terminal-surface/aggregate-final-v2`.

Native checkpoint seconds start at browser readiness. RSS and native capture
seconds start at process launch. A capture includes its requested native-stage
clock and actual outer-runner clock; the nearest RSS sample uses the latter.
Stable checkpoint dwell periods make those native snapshots comparable, while
not making them simultaneous or removing measurement overhead. RSS ranges omit
the first five launch seconds. Raw RSS values are KiB; summarized ranges use MiB.
VM/footprint strings retain the tools' own reported units and column ordering.

Reproduce with the README's development-bundle publish and the named
`aggregate-sustained` / `aggregate-cycles` runner options. Workload bytes and
assertions are fixed. Render counts, collection timing, binary identities and
process memory are observations of this environment, not expected constants or
acceptance thresholds. No forced GC was used. Selected root collection does not
prove native reclamation, a leak cause or whole-browser bounded memory.

## Matched controls

`matched-controls-2026-10-15.json` summarizes three fresh actual Uno macOS app
runs: `aggregate-empty-cycles`, `aggregate-idle-cycles` and the populated
`aggregate-cycles` reference. Every baseline precedes xterm construction. One
opaque frame, the embedded library/CSS and WKWebView stay alive in every run;
the empty control omits construction rather than replacing the browser or frame.
Post-measurement checks intentionally construct compatibility terminals.

Reproduce and summarize with the prototype README commands. The checked-in
`summarize-controls.py` only copies selected numeric/boolean counters, hashes,
process roles, RSS, native timing/footprint and selected category rows. Native
array columns and footprint category columns are declared in the JSON. All
native captures must succeed before a summary is emitted. Role-to-PID mappings
are in `processRssRangesAfterFiveSeconds`; helper attribution remains provisional.
Ranges include the post-measurement compatibility checks; corresponding measured
checkpoints are separate. Native categories retain the tools' B/KB/MB units;
RSS uses MiB, and these figures must not be added as exclusive allocations.

Raw logs, one-second RSS observations and all app/helper `vmmap`/`footprint`
category reports remain under ignored
`artifacts/terminal-surface/matched-controls-2026-10-15`. The result summary
retains all corresponding native capture start times and WebContent category
rows; it is not a heap/allocation-backtrace report. No forced collection,
reporting/debugger change, renderer repair, lifetime bound or component choice
follows from this comparison. See the research report for the measured
comparison and remaining evidence boundary.
