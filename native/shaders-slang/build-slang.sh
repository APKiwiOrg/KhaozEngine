#!/bin/sh
# Compiles sprite.slang with the pinned slangc into the checked-in SPIR-V, validates it, and emits MSL that
# macOS compiles through the Metal framework. --check regenerates into a temporary folder and fails when the
# checked-in SPIR-V differs. Usage: SLANGC=<path to slangc> sh native/shaders-slang/build-slang.sh [--check]
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
checked_in="$root/KhaozEngine.Gpu.Core/SlangTrial"
msl_dir="${SLANG_MSL_DIR:-/tmp/khaozengine-native-core/slang}"
pinned_version=2026.23

check=0
case "${1:-}" in
    '') ;;
    --check) check=1 ;;
    *) echo "usage: SLANGC=<path to slangc> sh $0 [--check]" >&2; exit 2 ;;
esac

if [ -z "${SLANGC:-}" ]; then
    echo "error: set SLANGC to the slangc $pinned_version binary" >&2
    exit 2
fi
if [ ! -x "$SLANGC" ]; then
    echo "error: SLANGC=$SLANGC is not an executable file" >&2
    exit 2
fi
version=$("$SLANGC" -version 2>&1)
if [ "$version" != "$pinned_version" ]; then
    echo "error: $SLANGC reports version $version, the pin is $pinned_version" >&2
    exit 2
fi
if ! command -v spirv-val >/dev/null 2>&1; then
    echo "error: spirv-val (SPIRV-Tools) is not on PATH" >&2
    exit 2
fi

if [ "$check" = 1 ]; then
    spv_dir=$(mktemp -d)
    trap 'rm -rf "$spv_dir"' EXIT
else
    spv_dir="$checked_in"
fi
mkdir -p "$spv_dir"
(umask 077 && mkdir -p "$msl_dir")
chmod 700 "$msl_dir"

# Run from this folder so the source path slangc sees is the same on every machine.
cd "$here"
for pair in vert:vertex:vertexMain frag:fragment:fragmentMain; do
    suffix=${pair%%:*}
    rest=${pair#*:}
    stage=${rest%%:*}
    entry=${rest#*:}
    spv="$spv_dir/sprite.$suffix.spv"
    "$SLANGC" sprite.slang -target spirv -profile spirv_1_3 -matrix-layout-column-major \
        -entry "$entry" -stage "$stage" -o "$spv"
    spirv-val --target-env vulkan1.3 "$spv"
    "$SLANGC" sprite.slang -target metal -matrix-layout-column-major -line-directive-mode none \
        -entry "$entry" -stage "$stage" -o "$msl_dir/sprite.$suffix.metal"
done

if [ "$(uname -s)" = Darwin ]; then
    xcrun swift "$here/compile-msl.swift" "$msl_dir/sprite.vert.metal" "$msl_dir/sprite.frag.metal"
else
    echo "MSL emitted to $msl_dir, the Metal compile runs on macOS only"
fi

if [ "$check" = 1 ]; then
    for suffix in vert frag; do
        if ! cmp -s "$spv_dir/sprite.$suffix.spv" "$checked_in/sprite.$suffix.spv"; then
            echo "error: KhaozEngine.Gpu.Core/SlangTrial/sprite.$suffix.spv differs from a fresh compile," \
                "run build-slang.sh and commit the result" >&2
            exit 1
        fi
    done
    echo "checked-in SPIR-V matches"
fi
