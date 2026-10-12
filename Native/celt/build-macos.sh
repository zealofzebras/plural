#!/usr/bin/env bash
# Builds stock CELT 0.7.1 as libcelt071.dylib for osx-arm64 and osx-x64 with Apple clang (run on macOS).
# Prerequisites:
#   - Tools/ext/src/celt-0.7.1   https://downloads.xiph.org/releases/celt/celt-0.7.1.tar.gz (override with CELT_SRC)
#   - Xcode command line tools
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
src="${CELT_SRC:-$root/Tools/ext/src/celt-0.7.1}/libcelt"
min="${MACOSX_DEPLOYMENT_TARGET:-12.0}"
files="bands.c celt.c cwrs.c entcode.c entdec.c entenc.c header.c kiss_fft.c laplace.c mdct.c modes.c pitch.c rangedec.c rangeenc.c rate.c quant_bands.c vq.c"

for pair in arm64:osx-arm64 x86_64:osx-x64; do
  arch="${pair%%:*}"
  rid="${pair#*:}"
  out="$here/runtimes/$rid/native"
  mkdir -p "$out"
  (cd "$src" && clang -arch "$arch" -mmacosx-version-min="$min" -dynamiclib -O2 -DHAVE_LRINTF -DVAR_ARRAYS \
      -install_name @rpath/libcelt071.dylib -Wno-everything \
      -o "$out/libcelt071.dylib" $files)
  echo "built $out/libcelt071.dylib"
done
