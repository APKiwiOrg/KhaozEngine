# KhaozEngine.Gpu.Core

This project holds the managed interop boundary for the native core spike. It builds without a native
library. It is used by the spike harness and does not participate in normal GPU backend selection.

## Bindings and load gate

The checked-in bindings come from [the public C header](../native/include/khaoz/khaoz_native.h). After
changing the header, restore the pinned ClangSharp tool with `dotnet tool restore` and run
`native/scripts/generate-bindings.sh`. The tool needs .NET 11 and Clang's builtin headers. Set `CLANG`
to the compiler path if `clang` is not on your PATH.

`NativeLibraryGate.Probe()` reports whether the library can be loaded and advertises the expected
ABI. The generated imports bind through the same check and retain the verified handle for the
lifetime of their entry points. Failed native operations carry the calling thread's UTF-8 diagnostic
into the managed exception.

## Tests

Build the native library using the CMake preset for your platform. Pass its absolute path as
`-p:KhaozNativeLibPath=<path>` when building the managed test project. MSBuild copies the library next
to the test assembly under its original filename. Run the tests with `KE_NATIVE_TESTS=1`. GPU facts
also use the shared `KE_GPU_TESTS` gate.

Keep `khaoz_native_stale_abi_fixture` from the native test build beside the library passed to
`KhaozNativeLibPath`. The managed test copies the fixture into a fresh process's application
directory under the platform's production library filename. Native test jobs need both files.
The fixture is for the managed loader test and stays out of the runtime package.
