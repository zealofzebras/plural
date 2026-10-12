#!/usr/bin/env bash
# Compiles C sources in the current directory into <out>/<name>.xcframework: a dynamic <name>.framework for iPhone (arm64)
# and one for the simulator (arm64 + x86_64), each with the Info.plist the App Store requires. Used by */build-ios.sh.
#   IOS_FRAMEWORK=<name> IOS_VERSION=<x.y> IOS_CFLAGS="..." ios-framework.sh <out-dir> <sources...>
# IPHONEOS_DEPLOYMENT_TARGET (default 15.0) matches Plural.App.iOS's SupportedOSPlatformVersion.
set -euo pipefail
name="$IOS_FRAMEWORK"; version="$IOS_VERSION"; cflags="${IOS_CFLAGS:-}"
min="${IPHONEOS_DEPLOYMENT_TARGET:-15.0}"
out="$1"; shift
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

framework() {  # framework <sdk> <platform name> <targets...>
  local sdk="$1" platform="$2"; shift 2
  local dir="$work/$sdk/$name.framework" sysroot slices=()
  sysroot="$(xcrun --sdk "$sdk" --show-sdk-path)"
  mkdir -p "$dir"
  for target in "$@"; do
    # shellcheck disable=SC2086
    xcrun --sdk "$sdk" clang -target "$target" -isysroot "$sysroot" -dynamiclib $cflags -Wno-everything \
        -install_name "@rpath/$name.framework/$name" -o "$work/$sdk/$target.dylib" "${sources[@]}"
    slices+=("$work/$sdk/$target.dylib")
  done
  lipo -create "${slices[@]}" -output "$dir/$name"
  cat > "$dir/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>$name</string>
  <key>CFBundleIdentifier</key><string>net.getplural.$name</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>$name</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleSupportedPlatforms</key><array><string>$platform</string></array>
  <key>MinimumOSVersion</key><string>$min</string>
</dict>
</plist>
PLIST
  plutil -lint "$dir/Info.plist" > /dev/null
}

sources=("$@")
framework iphoneos iPhoneOS "arm64-apple-ios$min"
framework iphonesimulator iPhoneSimulator "arm64-apple-ios$min-simulator" "x86_64-apple-ios$min-simulator"
mkdir -p "$out"
rm -rf "$out/$name.xcframework"
xcodebuild -create-xcframework \
  -framework "$work/iphoneos/$name.framework" \
  -framework "$work/iphonesimulator/$name.framework" \
  -output "$out/$name.xcframework"
echo "built $out/$name.xcframework"
