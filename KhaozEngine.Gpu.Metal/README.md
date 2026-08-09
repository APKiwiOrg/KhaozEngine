# KhaozEngine.Gpu.Metal

The engine's own native Metal backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Objective-C interop ever carries it.

> **Status: SKELETON.** Row 1 of the work breakdown creates the assembly, its guard rows and the three phase-4
> verification spikes, and nothing else. `KhaozEngineMetal` has exactly one member, the platform guard.
> There is no `Register()`, no provider, no probe and no device, `GpuBackendKind` has no native Metal member,
> and no consumer can reach a Metal device through this package. Registration and the functional probe are row
> 2 and the `GpuBackendKind.MetalNative` append is row 3. `GpuBackendKind.Metal`, which goes through Veldrid,
> is the working Metal backend and stays selectable indefinitely.

Spec, decisions and the nineteen-row work breakdown:
[docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md](../docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md).
This is phase 4 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), and the last one.

## What is in the package today

```csharp
using KhaozEngine.Gpu.Metal;

if (KhaozEngineMetal.IsPlatformSupported) { /* macOS */ }
```

`IsPlatformSupported` is the whole public surface, and a test pins it member by member so the next row has to
mean it. Everything else in the assembly is internal and exists to answer a question rather than to run in a
game: the interop spike, which is the one file that touches every Objective-C call the design names.

## Three decisions worth knowing before reading the code

**It targets `net10.0`, not `net10.0-macos`, and it carries the platform guard the Vulkan package does not
(M-P1).** A macOS-only target framework would stop the assembly compiling on the Linux and Windows legs and
stop `KhaozEngine.Render.Tests` referencing it unconditionally, which is where the device-free tests live. The
macOS boundary is carried instead by `[SupportedOSPlatformGuard("macos")]` over `NoInlining` bodies, which is
the Direct3D 11 package's apparatus rather than the Vulkan package's absence of one. That difference is
deliberate both ways. Vulkan is not an OS-specific API, so `KhaozEngine.Gpu.Vulkan` needs no guard and copying
one in by analogy would add a boundary it does not have. Metal is an OS-specific API, so this package needs
exactly what Direct3D 11 needs, and CA1416 enforces it at compile time under warnings as errors.

**The Objective-C interop is engine-owned, and there was no alternative to weigh (M-P2).** Phase 2 took
`Vortice.Direct3D11` and phase 3 took `Silk.NET.Vulkan`, both on the reasoning that owning the BACKEND and
owning the BINDING are different things. That reasoning is unchanged here and it has nothing to point at:
Silk.NET ships no Metal, Vortice ships no Metal, and Apple ships no managed binding of any kind. The
candidates were a hand-rolled layer or vendoring `Veldrid.MetalBindings`, and vendoring is rejected by name,
because Veldrid-derived code inside the backend built to remove Veldrid would be invisible to every guard that
reads package ids. So the interop is `[LibraryImport]` with blittable-only signatures over `objc_msgSend`,
source-generated with no marshalling stub, which is also what the SYSLIB1054 analyzer requires here. Reading
the fork as the reference implementation is a different act and the design does exactly that throughout.

**It carries no third-party package at all (M-P3).** This is the only backend in the program whose
`ArchitectureTests.ThirdPartyHomes` row is empty, and the emptiness is asserted rather than implied. It also
carries no `Veldrid` edge of its own, checked twice: `ArchitectureTests` reads the project file, and
`GpuPublicApiTests` walks the built IL, which is the half that binds, because Veldrid is in the transitive
closure through `KhaozEngine.Gpu` whatever the project file says.

## The interop, and where an error shows up

An ABI mistake in hand-rolled Objective-C interop is a memory corruption rather than a compile error, which is
why the design spends a whole verification task on it before any row depends on the layer. Three things about
arm64 drive the shape of the code and each of them is measured rather than assumed: `objc_msgSend` must be
called through a prototype matching the real method signature, so every call is a typed `[LibraryImport]`
overload rather than one variadic declaration, `objc_msgSend_stret` does not exist at all, so no stret path is
written rather than one being written and disabled, and `BOOL` is one byte while `CGFloat` is a double.

The one comforting property of this risk is that an interop defect presents as a crash rather than as a wrong
pixel, and the leg that will exercise it runs the full suite on a real GPU on every trigger.
