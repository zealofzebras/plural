#!/usr/bin/env bash
# Builds stock CELT 0.7.1 as runtimes/ios/celt071.xcframework (dynamic framework for iPhone arm64 and the arm64/x86_64
# simulator) with Apple clang (run on macOS). Plural.App.iOS embeds it; NativeLibs loads Frameworks/celt071.framework/celt071.
# Prerequisites:
#   - Tools/ext/src/celt-0.7.1   https://downloads.xiph.org/releases/celt/celt-0.7.1.tar.gz (override with CELT_SRC)
#   - Xcode
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
src="${CELT_SRC:-$root/Tools/ext/src/celt-0.7.1}/libcelt"
files="bands.c celt.c cwrs.c entcode.c entdec.c entenc.c header.c kiss_fft.c laplace.c mdct.c modes.c pitch.c rangedec.c rangeenc.c rate.c quant_bands.c vq.c"
(cd "$src" && IOS_FRAMEWORK=celt071 IOS_VERSION=0.7.1 IOS_CFLAGS="-O2 -DHAVE_LRINTF" \
    bash "$root/Native/ios-framework.sh" "$here/runtimes/ios" $files)
