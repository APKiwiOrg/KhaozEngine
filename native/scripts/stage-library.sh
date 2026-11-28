#!/bin/sh
# Stages one preset's build for upload: the stripped library under <out>/lib/<rid>/, its debug
# information under <out>/symbols/<rid>/ and the test-only fixture libraries under <out>/fixtures/<rid>/.
# The fixtures serve the managed loader tests and never go in the package.
set -eu

[ "$#" -eq 3 ] || { echo "usage: $0 <build-dir> <rid> <out-dir>" >&2; exit 2; }
build=$1
rid=$2
out=$3
lib="$out/lib/$rid"
symbols="$out/symbols/$rid"
fixtures="$out/fixtures/$rid"
mkdir -p "$lib" "$symbols" "$fixtures"

case "$rid" in
    linux-*)
        prefix=lib
        suffix=.so
        cp "$build/libkhaoz_native.so" "$lib/"
        objcopy --only-keep-debug "$lib/libkhaoz_native.so" "$symbols/libkhaoz_native.so.debug"
        strip --strip-unneeded "$lib/libkhaoz_native.so"
        # The link records the debug file's base name and checksum, so debuggers find it beside the library.
        objcopy --add-gnu-debuglink="$symbols/libkhaoz_native.so.debug" "$lib/libkhaoz_native.so"
        ;;
    osx-*)
        prefix=lib
        suffix=.dylib
        cp "$build/libkhaoz_native.dylib" "$lib/"
        dsymutil "$build/libkhaoz_native.dylib" -o "$symbols/libkhaoz_native.dylib.dSYM"
        strip -x "$lib/libkhaoz_native.dylib"
        ;;
    win-*)
        prefix=
        suffix=.dll
        cp "$build/khaoz_native.dll" "$lib/"
        cp "$build/khaoz_native.pdb" "$symbols/"
        ;;
    *)
        echo "$0: unknown RID $rid" >&2
        exit 2
        ;;
esac

for name in khaoz_native_stale_abi_fixture khaoz_native_vulkan_startup_fixture; do
    cp "$build/$prefix$name$suffix" "$fixtures/"
done
ls -l "$lib" "$symbols" "$fixtures"
