# KhaozEngine native core spike

The C++20 shared library exposes its C ABI in [khaoz_native.h](include/khaoz/khaoz_native.h). Check
the ABI version after loading the library. The error accessor reports the calling thread's last
recorded exception and accepts a null buffer to query the length. If recording a diagnostic runs
out of memory, the accessor reports a fallback message to the calling thread. A thread that has yet
to record an error reads an empty message.

POSIX builds store full diagnostics in checked pthread slots. Linux places its one-byte fallback
flag in the module's static TLS block at library load. A loader with exhausted static TLS can
refuse that load.
On macOS, a preallocated table tracks fallback owners by native thread ID if pthread refuses the
fallback slot. It reuses records of exited threads and holds up to 1,024 simultaneous owners. If it
fills, the guard still returns its result code.

Published POSIX keys stay reserved until process exit, so libc can finish thread cleanup safely
after an explicit library unload.

## Prerequisites

Use CMake 3.28 or later, Ninja and a toolchain with C and C++20 support. Use Apple Clang on macOS, GCC or Clang
on Linux, and MSVC from an x64 developer shell on Windows. The macOS preset targets macOS 15.0
or later. All dependencies are in [third_party](third_party/README.md), so configure, build and
test work offline. If you previously configured the preset with another toolchain, configure
once with `cmake --fresh --preset <name>` to clear the cached toolchain.

Run the commands for your platform from this directory. On the shared development Mac, run
configure, build and CTest through build-slot with a label for each command.

### macOS arm64

    cmake --preset macos-arm64
    cmake --build --preset macos-arm64
    ctest --preset macos-arm64

### Linux x64

    cmake --preset linux-x64
    cmake --build --preset linux-x64
    ctest --preset linux-x64

### Linux x64 with address and undefined behavior sanitizers

    cmake --preset linux-x64-asan
    cmake --build --preset linux-x64-asan
    ctest --preset linux-x64-asan

### Windows x64

    cmake --preset windows-x64
    cmake --build --preset windows-x64
    ctest --preset windows-x64

Each preset writes the build and compilation database under out/build in this directory.

## Source checks

Use LLVM's clang-format and run-clang-tidy from this directory. The formatter checks tracked
files and new files that Git does not ignore, excluding `third_party/`. The linter checks
first-party translation units from the configured preset's compilation database and reports
diagnostics in their non-system headers. Include vendored headers by name in angle brackets
through the `khaoz::*` targets, which supply their SYSTEM include paths. The include check
rejects direct paths into the vendored tree and quoted vendored header names. Substitute
your configured preset in the lint command.

```sh
git ls-files -z --cached --others --exclude-standard -- \
  '*.[cChH]' '*.cc' '*.cp' '*.cpp' '*.cxx' '*.c++' '*.hh' '*.hpp' '*.hxx' '*.h++' \
  '*.inc' '*.inl' '*.ipp' '*.tcc' '*.tpp' '*.ccm' '*.cppm' '*.cxxm' '*.c++m' '*.ixx' \
  ':(exclude)third_party/**' | xargs -0 clang-format --dry-run --Werror --

python3 scripts/check-vendored-includes.py

native_root=$(python3 -c 'import os, re; print(re.escape(os.getcwd()))')
run-clang-tidy -quiet -j 1 -config-file="$PWD/.clang-tidy" -p out/build/macos-arm64 \
  -source-filter "^${native_root}"'[/\\](?!third_party[/\\])'

scripts/refresh-third-party.sh --check
```
