# KhaozEngine.Gpu.Metal

The engine's own native Metal backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Objective-C interop ever carries it.

> **Status: SKELETON.** Row 1 of the work breakdown creates the assembly, its guard rows and the three phase-4
> verification spikes, and nothing else. `KhaozEngineMetal` has exactly one member, the platform guard.
> There is no `Register()`, no provider, no probe and no device, and no consumer can reach a Metal device
> through this package. Registration and the functional probe are row 2. `GpuBackendKind.MetalNative` and its
> `metal-native` token DO exist as of `17.35.0` (row 3), so the kind is nameable ahead of the backend behind it,
> and naming it with no provider registered throws rather than falling back. `GpuBackendKind.Metal`, which goes
> through Veldrid, is the working Metal backend and stays selectable indefinitely.

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
game: the interop spike, which covers every ABI shape the design names (one representative per distinct
`objc_msgSend` prototype, rather than every selector row 4 will need) across two files, plus the
compile-options probe beside it.

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

## What the three spikes answered

Row 1 exists as much to MEASURE as to build, because three of this design's decisions rest on facts nobody had
checked. All three answers were taken on an Apple M2 Max under macOS 26, and all three are committed as tests
rather than written down, so each is a tripwire on its own premise rather than a number in a document nobody
re-runs.

**The interop spike is clean.** Every Objective-C call the design names was compiled and run against a real
device, in one command buffer that completed with a nil error. `BOOL` is one byte, `CGFloat` is a double, all
three by-value struct shapes cross correctly, the array setters and the offset setters record on both a render
and a compute encoder, an `[UnmanagedCallersOnly]` completion handler fires from a global block literal with no
delegate and no GC handle anywhere on the path, `MTLSharedEvent`'s four members work end to end, and
`supportsFamily:` and `maximumDrawableCount` both answer. One question came back NO: in-process
`setenv("MTL_DEBUG_LAYER", "1")` does not reach the validation layer, verified against a control run where the
same variable set at launch does. So validation is armed at job level in CI and by a documented prefix locally,
which is the fallback the design already named.

**`MTLCompileOptions` defaults are measured**, so the pin that lands later is a no-op rather than a guess:
`languageVersion` 3.2, `fastMathEnabled` on, `preserveInvariance` off, and the newer `mathMode` property exists
on this OS and agrees, reading fast. Those two defaults are what the committed metal goldens were baked under,
and fast math is the kind of knob that moves every pixel with no other symptom.

**The MSL name-join spike refuted the design's biggest bet, and that is the most valuable thing row 1
produced.** The plan was to read binding indices out of the emitted MSL and join them to the shader reflection
by name. Over all 42 shipped programs, zero of 159 emitted arguments join to any of the 141 reflected elements:
every texture and sampler element reflects with an empty name, and the buffer elements that do carry a name
carry a different one per stage. The design's own named fallback applies instead, and the numbering fix is
filed as [#586](https://github.com/APKiwiOrg/KhaozEngine/issues/586). Finding that at a spike rather than at
the first golden run is the whole reason the spike was scheduled ahead of everything that depends on it.

**Then the same join was tried on the other key, and it reaches everything.** Argument names are SPIRV-Cross's
spelling of the SPIR-V id, and the descriptor decorations behind that id survive the debug-info stripping that
removes the names, so a join keyed on the id rather than on the name matches 159 of 159 with no failure class.
It changes no binding today, because the id join and the incumbent's arithmetic agree on every argument in the
shipped set, and the test records WHY: the condition that makes them disagree (a stage skipping a same-kind
element ahead of a referenced one) does not occur once, so the fallback is safe on evidence rather than on the
absence of an alternative. That test is the tripwire on the first shader to change it.

## The interop, and where an error shows up

An ABI mistake in hand-rolled Objective-C interop is a memory corruption rather than a compile error, which is
why the design spends a whole verification task on it before any row depends on the layer. Three things about
arm64 drive the shape of the code and each of them is measured rather than assumed: `objc_msgSend` must be
called through a prototype matching the real method signature, so every call is a typed `[LibraryImport]`
overload rather than one variadic declaration, `objc_msgSend_stret` does not exist at all, so no stret path is
written rather than one being written and disabled, and `BOOL` is one byte while `CGFloat` is a double.

The one comforting property of this risk is that an interop defect presents as a crash rather than as a wrong
pixel, and the leg that will exercise it runs the full suite on a real GPU on every trigger.
