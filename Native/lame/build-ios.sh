#!/usr/bin/env bash
# Builds LAME 3.100 (libmp3lame only) as runtimes/ios/mp3lame.xcframework (dynamic framework for iPhone arm64 and the
# arm64/x86_64 simulator) with Apple clang (run on macOS). Plural.App.iOS embeds it.
# Prerequisites:
#   - Tools/ext/src/lame-3.100   https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz (override with LAME_SRC)
#   - Xcode
# Uses the hand-written Native/lame/config.h; no frontend, no mpglib, no SIMD intrinsics.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
lame="${LAME_SRC:-$root/Tools/ext/src/lame-3.100}"
files="VbrTag.c bitstream.c encoder.c fft.c gain_analysis.c id3tag.c lame.c newmdct.c presets.c psymodel.c quantize.c quantize_pvt.c reservoir.c set_get.c tables.c takehiro.c util.c vbrquantize.c version.c"
(cd "$lame/libmp3lame" && IOS_FRAMEWORK=mp3lame IOS_VERSION=3.100 \
    IOS_CFLAGS="-O2 -DHAVE_CONFIG_H -I$here -I$lame/include -I$lame/libmp3lame" \
    bash "$root/Native/ios-framework.sh" "$here/runtimes/ios" $files)
