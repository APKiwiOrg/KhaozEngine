# KhaozEngine.Gpu.Metal

The engine's own native Metal backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Objective-C interop ever carries it.

> **Status: it REGISTERS and it PROBES, and it cannot yet create a device.** Rows 1 and 2 of the work
> breakdown are done: the assembly, its guard rows, the three phase-4 verification spikes, then
> `KhaozEngineMetal.Register()`, the `IGpuBackendProvider` behind it, and a functional probe that answers for
> this machine. Creating a device refuses with a message naming
> [row 4](https://github.com/APKiwiOrg/KhaozEngine/issues/570), which builds the `MTLDevice` and the
> `MTLCommandQueue`. `GpuBackendKind` has no native Metal member yet, so registration keys on a pinned ordinal
> until [row 3](https://github.com/APKiwiOrg/KhaozEngine/issues/569) appends `MetalNative = 6`, and no
> `KE_GRAPHICS_BACKEND` token reaches this backend before that. `GpuBackendKind.Metal`, which goes through
> Veldrid, is the working Metal backend and stays selectable indefinitely.

Spec, decisions and the nineteen-row work breakdown:
[docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md](../docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md).
This is phase 4 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), and the last one.

## What is in the package today

```csharp
using KhaozEngine.Gpu.Metal;

KhaozEngineMetal.Register();   // unconditionally, on every OS, once at startup
```

`Register` and `IsPlatformSupported` are the whole public surface, and a test pins it member by member so the
next row has to mean it. Everything else in the assembly is internal: the provider, the machine probe and its
device-free decision half, plus the three verification spikes, which exist to answer a question rather than to
run in a game.

**Call `Register()` unconditionally and on every operating system.** Registering says a provider EXISTS, which
is a fact about your app's wiring. Whether this machine can run it is the separate question
`GpuBackendSelector.IsBackendSupported` answers, and the two are kept apart on purpose: a missing registration
THROWS, while an incapable machine falls back and reports it, and a log line where those two look alike is
exactly what a soak session cannot afford. Off macOS this costs one dictionary entry and loads no Objective-C
at all, because every entry point checks the platform guard before any body that names a Metal selector, and
those bodies are `NoInlining` so the JIT never compiles one.

## What the probe asks (M-N4)

The incumbent Veldrid Metal backend's own support check creates a device inside a bare catch. That is the FLOOR
of this probe rather than the whole of it. On top of it, four reads, each cheap here and expensive anywhere
later:

- **a device exists and reports a name**, which is what `GpuCapabilities.DeviceName` parity depends on under a
  zero-permitted-difference bar.
- **`supportsFamily:` answers at or above the floor**, meaning any `MTLGPUFamilyApple` generation or
  `MTLGPUFamilyMac2`. An Apple silicon Mac answers both arms and an Intel Mac on a supported macOS answers the
  second, so a device answering neither is below anything this engine ships on and is refused rather than
  crashing on frame one.
- **the device's own buffer-offset alignment divides the uniform ring's 256-byte stride.** This is the one read
  that would silently corrupt every ring bind on a future device.
- **`supportsTextureSampleCount:` answers for 1**, which is where the `MaxMsaaSampleCount` walk starts.

It never throws. A probe that blows up and a probe that answers no are the same answer to the settings screen
and the fallback that consume them, so anything the interop layer can raise is caught and reported as a no with
the exception named.

**One of those four asks for a property Metal does not have, and the probe says so.** Measured on an Apple M2
Max under macOS 26.6: `MTLDevice` responds to neither `minimumConstantBufferOffsetAlignment` nor
`minimumBufferOffsetAlignment`, which is why the incumbent hardcodes `MetalFeatures.IsMacOS ? 16u : 256u`
rather than asking. So the probe asks for the real property through `respondsToSelector:` first, and a future
macOS that ships one is read with no code change, then falls back to
`minimumLinearTextureAlignmentForPixelFormat:`, which IS a device-reported buffer offset alignment. It reads 16
on that machine, which is exactly what the incumbent hardcodes for macOS, so two independent statements of the
number agree.

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
