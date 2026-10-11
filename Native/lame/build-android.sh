#!/usr/bin/env bash
# Builds LAME 3.100 (libmp3lame only) as libmp3lame.so for android-arm64 (phones) and android-x64 (emulator).
# Prerequisites:
#   - Tools/ext/src/lame-3.100   https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz
#   - Android NDK r29 (sdkmanager "ndk;29.0.14206865"); override with ANDROID_NDK=<path>
# API level 26; 16 KB page alignment is required for Android 15+ devices.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
lame="${LAME_SRC:-$root/Tools/ext/src/lame-3.100}"
ndk="${ANDROID_NDK:-$LOCALAPPDATA/Android/Sdk/ndk/29.0.14206865}"
case "$(uname -s)" in
  Linux*)  clang="$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/clang" ;;
  Darwin*) clang="$ndk/toolchains/llvm/prebuilt/darwin-x86_64/bin/clang" ;;
  *)       clang="$ndk/toolchains/llvm/prebuilt/windows-x86_64/bin/clang.exe" ;;
esac
api=26
files="VbrTag.c bitstream.c encoder.c fft.c gain_analysis.c id3tag.c lame.c newmdct.c presets.c psymodel.c quantize.c quantize_pvt.c reservoir.c set_get.c tables.c takehiro.c util.c vbrquantize.c version.c"

for pair in aarch64:android-arm64 x86_64:android-x64; do
  arch="${pair%%:*}"
  rid="${pair#*:}"
  out="$here/runtimes/$rid/native"
  mkdir -p "$out"
  (cd "$lame/libmp3lame" && "$clang" --target="$arch-linux-android$api" -shared -fPIC -O2 -DHAVE_CONFIG_H \
      -I"$here" -I"$lame/include" -I"$lame/libmp3lame" \
      -Wl,-z,max-page-size=16384 -Wl,-soname,libmp3lame.so -Wno-everything \
      -o "$out/libmp3lame.so" $files -lm)
  echo "built $out/libmp3lame.so"
done
