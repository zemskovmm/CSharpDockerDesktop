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

The optional Windows experiment is built with `sh tools/TerminalSurfacePrototype/WindowsHost/build-windows-host.sh /path/to/pinned-uno-source.tar.gz`. Read the research document for the archive checksum, preparation/build steps, source notices, strict initialization, fresh-profile retention and joined-cleanup limitations. No Edge runtime is bundled, and no Windows privacy or terminal conformance result is implied by cross-publishing.
