#!/usr/bin/env bash
# Builds stock CELT 0.7.1 as celt071.dll for win-x64 and win-arm64 using llvm-mingw.
# Prerequisites (downloaded into Tools/ext, not committed):
#   - Tools/ext/src/celt-0.7.1          https://downloads.xiph.org/releases/celt/celt-0.7.1.tar.gz
#   - Tools/ext/llvm-mingw-20240619-ucrt-aarch64   https://github.com/mstorsjo/llvm-mingw/releases
# (Zig 0.13's aarch64-windows output does not load on Windows ARM64 — do not use it.)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
src="$root/Tools/ext/src/celt-0.7.1/libcelt"
tc="$root/Tools/ext/llvm-mingw-20240619-ucrt-aarch64/bin"
files="bands.c celt.c cwrs.c entcode.c entdec.c entenc.c header.c kiss_fft.c laplace.c mdct.c modes.c pitch.c rangedec.c rangeenc.c rate.c quant_bands.c vq.c"

for pair in x86_64:win-x64 aarch64:win-arm64; do
  arch="${pair%%:*}"
  rid="${pair#*:}"
  out="$here/runtimes/$rid/native"
  mkdir -p "$out"
  (cd "$src" && "$tc/$arch-w64-mingw32-clang" -shared -O2 -static -DWIN32 -DHAVE_LRINTF \
      -Wno-everything -o "$out/celt071.dll" $files)
  echo "built $out/celt071.dll"
done
