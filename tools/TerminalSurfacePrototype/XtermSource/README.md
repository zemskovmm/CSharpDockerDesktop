# Modified xterm source experiment

Only the isolated prototype consumes this modified MIT xterm.js 5.5.0 build.
`checksums.json` pins the npm source archive, source patch and rebuilt asset.
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

This callback extension is deliberately outside stock xterm's public contract.
It is research failure behavior, not an accepted production terminal policy.
Preserve `ThirdParty/xterm-LICENSE` and the modification notice in distributions.
See the main research document for native observations and remaining gaps.
