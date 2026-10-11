#!/usr/bin/env bash
# Builds LAME 3.100 (libmp3lame only) as mp3lame.dll for win-x64 and win-arm64 using llvm-mingw.
# Prerequisites (downloaded into Tools/ext, not committed):
#   - Tools/ext/src/lame-3.100   https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz
#   - Tools/ext/llvm-mingw-20240619-ucrt-aarch64   https://github.com/mstorsjo/llvm-mingw/releases
# Uses the hand-written Native/lame/config.h; no frontend, no mpglib, no SIMD intrinsics.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
# LAME_SRC / LLVM_MINGW override the defaults (used by CI on Linux).
lame="${LAME_SRC:-$root/Tools/ext/src/lame-3.100}"
tc="${LLVM_MINGW:-$root/Tools/ext/llvm-mingw-20240619-ucrt-aarch64}/bin"
files="VbrTag.c bitstream.c encoder.c fft.c gain_analysis.c id3tag.c lame.c newmdct.c presets.c psymodel.c quantize.c quantize_pvt.c reservoir.c set_get.c tables.c takehiro.c util.c vbrquantize.c version.c"

for pair in x86_64:win-x64 aarch64:win-arm64; do
  arch="${pair%%:*}"
  rid="${pair#*:}"
  out="$here/runtimes/$rid/native"
  mkdir -p "$out"
  (cd "$lame/libmp3lame" && "$tc/$arch-w64-mingw32-clang" -shared -O2 -static -DHAVE_CONFIG_H -DWIN32 \
      -I"$here" -I"$lame/include" -I"$lame/libmp3lame" -Wno-everything \
      -o "$out/mp3lame.dll" $files)
  echo "built $out/mp3lame.dll"
done
