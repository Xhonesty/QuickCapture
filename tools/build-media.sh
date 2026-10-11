#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
native="$root/.tools/media-native"
deps="$native/deps/ucrt64"
mingw="$(cygpath -u "${QUICKCAPTURE_MINGW_BIN:-C:/mingw64/bin}")"
export PATH="$deps/bin:$mingw:$PATH"
export TMPDIR="$native/temp" TEMP="$native/temp" TMP="$native/temp"
export PKG_CONFIG_LIBDIR="$deps/lib/pkgconfig"
export PKG_CONFIG_PATH="$PKG_CONFIG_LIBDIR"
mkdir -p "$native/build" "$native/output" "$native/temp"
cd "$native/build"
if [[ "${QUICKCAPTURE_MEDIA_SKIP_CONFIGURE:-0}" != 1 ]]; then
"$native/source/configure" \
  --prefix="$(cygpath -m "$native/output")" \
  --target-os=mingw32 --arch=x86_64 --cc=gcc --cxx=g++ \
  --enable-shared --disable-static --disable-ffplay --disable-doc --disable-debug \
  --disable-autodetect --enable-gpl --enable-version3 --enable-small \
  --enable-libx264 --enable-libvpx --enable-libopus --enable-libdav1d --enable-zlib \
  --disable-indevs --enable-indev=lavfi --disable-outdevs \
  --extra-cflags="-I$(cygpath -m "$deps/include") -Os" \
  --extra-ldflags="-L$(cygpath -m "$deps/lib") -static-libgcc" \
  > "$native/configure.log" 2>&1
fi
# Native Windows GNU Make does not interpret MSYS drive prefixes (/e/...).
# The source is adjacent to the build directory, so use portable relative paths.
sed -i "s|$native/source|../source|g" Makefile ffbuild/config.mak
# GNU Make from the existing MinGW toolchain; use the POSIX shell explicitly.
make_shell="$(cygpath -ms "$(command -v sh).exe")"
mingw32-make -r -j"${QUICKCAPTURE_MEDIA_JOBS:-4}" "SHELL=$make_shell" \
  > "$native/compile.log" 2>&1
mingw32-make -r install "SHELL=$make_shell" \
  >> "$native/compile.log" 2>&1
