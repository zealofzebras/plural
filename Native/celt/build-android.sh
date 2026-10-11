#!/usr/bin/env bash
# Builds stock CELT 0.7.1 as libcelt071.so for android-arm64 (phones) and android-x64 (emulator).
# Prerequisites:
#   - Tools/ext/src/celt-0.7.1   https://downloads.xiph.org/releases/celt/celt-0.7.1.tar.gz
#   - Android NDK r29 (sdkmanager "ndk;29.0.14206865"); override with ANDROID_NDK=<path>
# API level 26 matches SupportedOSPlatformVersion of Plural.App.Android.
# 16 KB page alignment is required for Android 15+ devices.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
src="${CELT_SRC:-$root/Tools/ext/src/celt-0.7.1}/libcelt"
ndk="${ANDROID_NDK:-$LOCALAPPDATA/Android/Sdk/ndk/29.0.14206865}"
case "$(uname -s)" in
  Linux*)  clang="$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/clang" ;;
  Darwin*) clang="$ndk/toolchains/llvm/prebuilt/darwin-x86_64/bin/clang" ;;
  *)       clang="$ndk/toolchains/llvm/prebuilt/windows-x86_64/bin/clang.exe" ;;
esac
api=26
files="bands.c celt.c cwrs.c entcode.c entdec.c entenc.c header.c kiss_fft.c laplace.c mdct.c modes.c pitch.c rangedec.c rangeenc.c rate.c quant_bands.c vq.c"

for pair in aarch64:android-arm64 x86_64:android-x64; do
  arch="${pair%%:*}"
  rid="${pair#*:}"
  out="$here/runtimes/$rid/native"
  mkdir -p "$out"
  (cd "$src" && "$clang" --target="$arch-linux-android$api" -shared -fPIC -O2 -DHAVE_LRINTF \
      -Wl,-z,max-page-size=16384 -Wl,-soname,libcelt071.so -Wno-everything \
      -o "$out/libcelt071.so" $files -lm)
  echo "built $out/libcelt071.so"
done
