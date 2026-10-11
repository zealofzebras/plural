#!/usr/bin/env bash
# Builds LAME 3.100 (libmp3lame only) as libmp3lame.dylib for osx-arm64 and osx-x64 with Apple clang (run on macOS).
# Prerequisites:
#   - Tools/ext/src/lame-3.100   https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz (override with LAME_SRC)
#   - Xcode command line tools
# Uses the hand-written Native/lame/config.h; no frontend, no mpglib, no SIMD intrinsics.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
lame="${LAME_SRC:-$root/Tools/ext/src/lame-3.100}"
min="${MACOSX_DEPLOYMENT_TARGET:-12.0}"
files="VbrTag.c bitstream.c encoder.c fft.c gain_analysis.c id3tag.c lame.c newmdct.c presets.c psymodel.c quantize.c quantize_pvt.c reservoir.c set_get.c tables.c takehiro.c util.c vbrquantize.c version.c"

for pair in arm64:osx-arm64 x86_64:osx-x64; do
  arch="${pair%%:*}"
  rid="${pair#*:}"
  out="$here/runtimes/$rid/native"
  mkdir -p "$out"
  (cd "$lame/libmp3lame" && clang -arch "$arch" -mmacosx-version-min="$min" -dynamiclib -O2 -DHAVE_CONFIG_H \
      -I"$here" -I"$lame/include" -I"$lame/libmp3lame" -install_name @rpath/libmp3lame.dylib -Wno-everything \
      -o "$out/libmp3lame.dylib" $files)
  echo "built $out/libmp3lame.dylib"
done
