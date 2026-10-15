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
