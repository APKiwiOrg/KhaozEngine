# KhaozEngine native core spike

The C++20 shared library exposes its C ABI in [khaoz_native.h](include/khaoz/khaoz_native.h). Check
the ABI version after loading the library. The error accessor reports the calling thread's last
recorded exception and accepts a null buffer to query the length. If recording a diagnostic runs
out of memory, the accessor reports a fallback message. POSIX builds use checked thread-specific
slots to keep diagnostic storage independent of the dynamic loader's TLS allocation. If the OS
refuses to register a slot, callers without a stored diagnostic can read the fallback.
POSIX slot registrations used to record errors stay reserved until process exit, so libc can finish
thread cleanup safely after an explicit library unload.

## Prerequisites

Use CMake 3.28 or later, Ninja, vcpkg with Git, and a C++20 toolchain. Use Apple Clang on macOS,
GCC or Clang on Linux, and MSVC from an x64 developer shell on Windows. On macOS and Linux,
put a pkg-config implementation on PATH for the vcpkg doctest port. Set VCPKG_ROOT to the
vcpkg checkout before configuring. The manifest pins the port registry baseline. Install LLVM's
clang-format and clang-tidy tools to check formatting and lint.

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
