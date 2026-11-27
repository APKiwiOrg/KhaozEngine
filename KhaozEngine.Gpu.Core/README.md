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

## Frame submission

`FrameSubmitBoundary` owns a fixed-capacity frame. Pass a span of 64-byte `khaoz_draw_record`
values to submit the whole frame in one call. Each record carries a 3x4 row-major affine transform,
mesh and material IDs, and an unsigned 64-bit sort key. The native core stable-sorts complete
records by key into host storage allocated at creation. The host storage stands in for a mapped
upload buffer so the path runs without a GPU. A successful submit allocates no memory on either
side, and a rejected submit leaves the previous frame intact. Serialize operations on each
instance. `CopySortedKeys` copies into a caller-provided span with room for the submitted frame.

## Tests

Build the native library using the CMake preset for your platform. Pass its absolute path as
`-p:KhaozNativeLibPath=<path>` when building the managed test project. MSBuild copies the library next
to the test assembly under its original filename. Run the tests with `KE_NATIVE_TESTS=1`. GPU facts
also use the shared `KE_GPU_TESTS` gate.

Keep `khaoz_native_stale_abi_fixture` from the native test build beside the library passed to
`KhaozNativeLibPath`. The managed test copies the fixture into a fresh process's application
directory under the platform's production library filename. Native test jobs need both files.
The fixture is for the managed loader test. Keep the `khaoz_native_vulkan_startup_fixture`
beside it for the managed startup refusal tests. Both fixtures stay out of the runtime
package.

The report-only frame probe requires `KE_NATIVE_FRAME_TIMING=1` in addition to
`KE_NATIVE_TESTS=1`. It times submit for the 3,335-record random and frame-coherent workloads and
can write a JSON receipt at `KE_NATIVE_FRAME_RECEIPT`.

## Headless Vulkan transfers

The spike tests explicitly seat `CoreVulkanBackendProvider` as `VulkanNative`. The provider
checks the native library ABI and probes for an eligible Vulkan 1.3 adapter. It uses the
managed Vulkan assembly's structured creation refusal so both providers expose the same
exception and reason for an unavailable machine.

The core supports buffers, textures, samplers, transfer command lists and completion fences.
`GpuReadback` can copy data into staging memory and map it through the usual GPU interfaces.
Drawing, shader and pipeline creation, and presentation are outside the transfer subset.
`KE_VULKAN_DEVICE` uses the managed Vulkan selection policy, including its warning and
fallback when the request cannot be honored.
`KHAOZ_NATIVE_VULKAN_LOADER` selects a loader file and `KHAOZ_NATIVE_VALIDATION=1` enables
Vulkan validation with an error count that includes device teardown.

On macOS, use the MoltenVK ICD. If the Homebrew validation manifest names its library without
a path, set `DYLD_LIBRARY_PATH` to the Homebrew library directory in the launched test command.
Set it inside `build-slot` using `env`, since macOS can strip it when starting the slot wrapper.
