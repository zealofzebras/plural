#!/usr/bin/env bash
# Wraps a published Plural.App.Desktop folder (osx-arm64 or osx-x64) into Plural.app. Run on macOS.
#   build-app.sh <publish-dir> <rid> <version> <build-number> <iconset-dir> <out-dir>
# Signing is done separately (see .github/workflows/release.yml), so this works without certificates.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
publish="$1"; rid="$2"; version="$3"; build="$4"; iconset="$5"; out="$6"
bundle_id="${BUNDLE_ID:-net.getplural.app}"
copyright="${COPYRIGHT:-© 2026 Plural}"

app="$out/Plural.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"
# Keep only this architecture's native libraries (Windows DLLs would break code signing).
if [ -d "$app/Contents/MacOS/runtimes" ]; then
  find "$app/Contents/MacOS/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name "$rid" -exec rm -rf {} +
fi
rm -f "$app/Contents/MacOS/"*.pdb
chmod +x "$app/Contents/MacOS/Plural"

iconutil -c icns "$iconset" -o "$app/Contents/Resources/Plural.icns"
sed -e "s|{{BUNDLE_ID}}|$bundle_id|" -e "s|{{VERSION}}|$version|" -e "s|{{BUILD}}|$build|" \
    -e "s|{{COPYRIGHT}}|$copyright|" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"
echo "built $app"
