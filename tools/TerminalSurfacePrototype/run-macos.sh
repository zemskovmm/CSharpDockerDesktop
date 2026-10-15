#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
case "$(uname -m)" in
  arm64) terminal_rid=osx-arm64 ;;
  x86_64) terminal_rid=osx-x64 ;;
  *) echo 'Unsupported development host architecture.' >&2; exit 1 ;;
esac
terminal_app=artifacts/terminal-surface/macos/TerminalSurfaceResearch.app
dotnet publish tools/TerminalSurfacePrototype/TerminalSurfacePrototype.csproj -c Release -r "$terminal_rid" --self-contained true -o "$terminal_app/Contents/MacOS"
cat > "$terminal_app/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>TerminalSurfacePrototype</string>
<key>CFBundleIdentifier</key><string>org.csharpdockerdesktop.terminalresearch</string>
<key>CFBundleName</key><string>TerminalSurfaceResearch</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
open "$terminal_app"
