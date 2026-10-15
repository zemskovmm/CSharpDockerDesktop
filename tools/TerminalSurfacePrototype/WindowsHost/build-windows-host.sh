#!/bin/sh
# Build only the experimental host; download the exact archive separately (see research documentation).
set -eu
prototype=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
repository=$(CDPATH= cd -- "$prototype/../.." && pwd)
dotnet_command=${DOTNET:-dotnet}
archive=${1:?Pass the checksum-pinned Uno source tar.gz path}
mkdir -p "$repository/artifacts/terminal-surface/windows-privacy"
output=$(mktemp -d "$repository/artifacts/terminal-surface/windows-privacy/build-XXXXXXXX")
python3 "$prototype/WindowsHost/prepare-host.py" "$archive" "$output/source"
python3 "$prototype/WindowsHost/check-source.py" "$output/source"
"$dotnet_command" restore "$prototype/TerminalSurfacePrototype.csproj" -p:Configuration=Release --locked-mode
"$dotnet_command" publish "$prototype/TerminalSurfacePrototype.csproj" -c Release -r win-x64 --self-contained true --no-restore -o "$output/app"
"$dotnet_command" restore "$prototype/WindowsHost/Host.csproj" --locked-mode
"$dotnet_command" build "$prototype/WindowsHost/Host.csproj" -c Release --no-restore \
    -p:UnoSourceRoot="$output/source" -p:PrototypeReferences="$output/app" -o "$output/host"
"$dotnet_command" build "$prototype/WindowsHost/Checks/HostChecks.csproj" -c Release -p:PrototypeReferences="$output/app"
checks="$prototype/WindowsHost/Checks/bin/Release/net10.0/HostChecks.dll"
"$dotnet_command" "$checks" stock-host "$output/app/Uno.UI.Runtime.Skia.Win32.dll"
for scenario in options runtime unprepared unknown-backend override sso browser-arguments profile unused-profile cleanup; do
    "$dotnet_command" "$checks" "$scenario"
done
# Replace only this fresh, isolated publish output. Never write a NuGet cache or production output.
cp "$output/host/Uno.UI.Runtime.Skia.Win32.dll" "$output/app/Uno.UI.Runtime.Skia.Win32.dll"
cp "$output/host/Uno.UI.Runtime.Skia.Win32.pdb" "$output/app/Uno.UI.Runtime.Skia.Win32.pdb"
# Do not copy the host builder's older Skia compile dependencies over the locked prototype runtime.
"$dotnet_command" "$checks" patched-host "$output/app/Uno.UI.Runtime.Skia.Win32.dll"
printf 'Synthetic-only Windows research output: %s\n' "$output/app"
printf 'Stock builds refuse Windows startup. This output is build/configuration evidence, not a Windows runtime/privacy result.\n'
