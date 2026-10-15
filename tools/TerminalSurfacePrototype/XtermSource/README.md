# Modified xterm source experiment

Only the isolated prototype consumes this modified MIT xterm.js 5.5.0 build.
`checksums.json` pins the npm source archive, three ordered source patches and rebuilt asset.
`package-lock.json` pins the isolated build tool and platform binaries by npm
integrity; Node 22.23.1/npm 10.9.8/esbuild 0.25.0 were used for the recorded build.
No installed package, NuGet cache assembly or upstream checkout is modified.

From the repository root (Python 3, Git, Node/npm required):

```sh
curl --fail --location https://registry.npmjs.org/@xterm/xterm/-/xterm-5.5.0.tgz -o /tmp/xterm-5.5.0.tgz
python3 tools/TerminalSurfacePrototype/XtermSource/prepare.py /tmp/xterm-5.5.0.tgz artifacts/terminal-surface/xterm-build/reproduce
mkdir -p artifacts/terminal-surface/xterm-build/reproduce/build-tools
cp tools/TerminalSurfacePrototype/XtermSource/package*.json artifacts/terminal-surface/xterm-build/reproduce/build-tools/
npm ci --ignore-scripts --no-audit --no-fund --prefix artifacts/terminal-surface/xterm-build/reproduce/build-tools
node tools/TerminalSurfacePrototype/XtermSource/build.mjs artifacts/terminal-surface/xterm-build/reproduce
node artifacts/terminal-surface/xterm-build/reproduce/checks.cjs
cmp artifacts/terminal-surface/xterm-build/reproduce/xterm.js tools/TerminalSurfacePrototype/Web/xterm.js
```

Use a fresh generated directory each time. Preparation rejects checksum mismatch
before writing sources. Build verifies the output hash before writing the asset.
Only after checks, an intentional asset refresh can copy that verified `xterm.js`
to `Web/xterm.js`. Normal .NET builds use the committed asset without npm/network.
Acquire source and locked build dependencies ahead of an offline reproduction.

The patch caps each combined cell at 256 UTF-16 units, checks lengths before
concatenation/scalar conversion, and reports a dedicated limit exception through
a fixed experimental write callback argument. The writer permanently stops and
clears queued writes before notification. `surface.js` accepts that one code,
disposes the failed terminal and sends a content-free generation/sequence-bound
failure; C# revokes delivery and joins session/pump/producer cleanup. The failing
write is not acknowledged as successful. Previously parsed bytes in that write
may have changed the failed surface before disposal; there is no atomic-write or
resumable-session promise. No suffix is parsed after the offending scalar.

`accessibility-limit.patch` applies after the combined-cell patch. It bounds
`AccessibilityManager` pending plus live announcements to **16,384 UTF-16 units
in aggregate** (therefore neither holding can exceed that value). The keyboard
echo queue permits **256 entries, 1,024 units total and 16 units per entry**.
These are finite experimental choices, not production policy. Supplementary
characters count as two units. The preflight includes expanded tab spaces and
the upstream excessive-output message; pending-to-live transfer counts each
retained unit once. Key handling retains upstream live-region clearing and echo
suppression. Blur clears pending/live/echo state, and disposal clears these
holdings and cancels the debouncer. Row navigation remains enabled.

Parsing throws a dedicated fixed failure to the revoked writer. Keyboard and
render callbacks catch that failure locally, revoke the writer, and emit a
research-only core event even when no write is outstanding. The wrapper fences
input/acks immediately, disposes after the callback stack unwinds, and reports
`accessibility-limit` with the terminal generation and most recent write
sequence (zero before the first write). Native cleanup validates both identities,
cancels the outstanding acknowledgment, closes the fixture and joins its pump
and producer. No failed write is acknowledged; a fresh fixture requires deliberate
opening. There is no silent output truncation or periodic reset.

`accessibility-checks.ts` uses a minimal simulated DOM plus the actual source;
its pass is **not native browser or screen-reader acceptance**. It covers exact
and over-budget units/entries, mixed pending/live retention and transfer, Unicode,
tabs, excessive-output text, echo consumption, clear/blur, disposal, deferred
callbacks and revoked/reentrant/async writes. The native `a11y-boundaries` runner
separately covers the real Uno/WKWebView path, synthetic DOM keys, a held parser
write, delayed callback fault injection, row navigation and close/replacement.
See the research document for measured results and the explicit injected cases.

These callback/core-event extensions are deliberately outside stock xterm's public contract.
It is research failure behavior, not an accepted production terminal policy.
Preserve `ThirdParty/xterm-LICENSE` and the modification notice in distributions.
See the main research document for native observations and remaining gaps.

`width-cache-limit.patch` applies third. The DOM width cache evicts oldest keys
**before insertion** to retain at most **512 entries / 32,768 key UTF-16 units**
across all four font variants. A one-unit variant prefix avoids suffix collisions.
Cache misses remeasure; hits preserve FIFO order without new key retention.
The regular one-unit U+0000..U+00FF flat cache stays at 256 Float32 values (1 KiB).
Clear/font invalidation resets accounting, while numeric research counters retain
lifetime evidence; disposal releases the map, accounting, flat storage and spans.
The existing 256-unit cell limit bounds one key to 257 units and one 32-repeat
measurement to 8,192 units. Measurement spans clear in `finally`, also on layout
failure; no repeated measurement text remains between calls. Logical key payload
is at most 64 KiB, independently of map/DOM/string-copy/GC overhead. These are
experimental budgets, not a browser/native-memory bound or production policy.

`width-cache-checks.ts` tests entry and aggregate exact boundaries, eviction and
remeasurement, repeated hits, four variants, literal suffix separation, flat
storage, supplementary/combined text, zero measurements, exception cleanup,
clear/font invalidation and disposal using actual source with simulated layout.
Run native `--workload width-cache --workload width-cache-long` after publishing:
short cells churn 3,072 distinct cells; long cells churn 1,024 exact 256-unit
cells, across regular/bold/italic/bold-italic. Each 64-cell batch must actually
appear in the cache and increase measurement counts; parsed write count is not a
render count. Fixed redraw/font probes exercise ordinary rendering, without cache
injection. Explicit blur/refocus between batches clears prior announcements while
screen reader mode and row accessibility stay enabled; long batches announce
exactly 16,384 units. Repeated redraws hit the cache, evicted text remeasures to the
same numeric width, and font changes/resize/recovery/close/replacement are checked.
These finite diagnostics retain the 64 MiB ceiling; ordinary fixtures keep 1 MiB.
See the research document for numeric native results and limitations.
