#!/bin/sh
set -eu

cd "$(dirname "$0")/../.."

# Freestanding C uses Clang's stddef.h and stdint.h without host SDK headers.
# Use the Windows consumer target where KHAOZ_API has no export annotation.
# A fixed target keeps the checked-in source independent of the host.
resource_dir=$("${CLANG:-clang}" -print-resource-dir)
dotnet tool run ClangSharpPInvokeGenerator -- \
    --file native/include/khaoz/khaoz_native.h \
    --output KhaozEngine.Gpu.Core/Interop/KhaozNative.g.cs \
    --language c --std c11 --resource-directory "$resource_dir" \
    --additional=--target=x86_64-pc-windows-msvc --additional=-ffreestanding \
    --namespace KhaozEngine.Gpu.Core.Interop --method-class-name KhaozNative \
    --library-path khaoz_native --config types=windows \
    --include 'khaoz_*' KHAOZ_NATIVE_ABI_VERSION \
    --with-access-specifier '*=Internal' --with-type khaoz_result=int \
    --generate macro-bindings file-scoped-namespaces helper-types generated-code=type
