# KhaozEngine.Gpu.Vulkan

The engine's own native Vulkan backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Vulkan binding ever carries it.

> **Status: SKELETON.** This package currently contains its entry point, the binding-sufficiency spike, and
> nothing else. It registers no provider, it creates no device, `GpuBackendKind` has no native Vulkan member
> yet, and there is no way for a consumer to reach a Vulkan device through it. What HAS landed is the package
> itself, its guard rows, the two verification tasks row 1 owed, and the Silk.NET pin. `KhaozEngine.Gpu`'s
> `Vulkan` backend, which goes through Veldrid, remains the working Vulkan path and stays selectable
> indefinitely.

## The spec

Everything this package will become is specified in
[docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md](../docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md),
phase 3 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), following the shipped phase 2
([KhaozEngine.Gpu.D3D11](../KhaozEngine.Gpu.D3D11)). Section 18 is the nineteen-row work breakdown each
implementation issue comes from, section 2 carries the eight contested decisions with their arguments and
rulings, and section 16 is the honesty ledger of what nobody can currently measure. Read the design before
adding anything here: several of the shapes below look like oversights and are decisions.

## Why there are no platform guards, and why nobody should add them

`KhaozEngine.Gpu.D3D11` targets `net10.0` deliberately rather than `net10.0-windows`, and carries a
`[SupportedOSPlatformGuard("windows")]` entry point over every `NoInlining` Vortice body so that the Direct3D
interop stays off the load path on macOS and Linux, with CA1416 turning the boundary into a compile-time rule.
That whole apparatus exists because Direct3D is a Windows API being shipped in an assembly that must load
everywhere.

**Vulkan is not a Windows API, so none of it has an analogue here (decision V-P1).** The same managed code runs
on Windows and Linux, the loader is resolved at runtime rather than being a platform-pinned import, and a
machine without one fails the functional probe and routes through the engine's existing fallback. The assembly
loads harmlessly on macOS, where phase 4 ships a real Metal backend and this one is never selected.

So the absence of `[SupportedOSPlatformGuard]`, of `NoInlining` bodies, and of an OS-suffixed TFM is a
simplification rather than an omission. It is written down here because the failure mode is somebody reading
the D3D11 package as a template, noticing the guards are missing, and adding them back by analogy. That would
add a boundary this backend does not have, and every member behind it would then be a member the tests have to
reach through.

## The binding, and the spike that proves it

The Vulkan binding is **`Silk.NET.Vulkan`** plus `Silk.NET.Vulkan.Extensions.KHR` and
`Silk.NET.Vulkan.Extensions.EXT`, pinned to the **`2.23.0` line the windowing, input and audio stacks already
pin** (decision V-P2, argued in full in section 3.1 of the design). The short version: the Silk.NET family is
already trusted and already load-bearing in this engine, a Vulkan binding off the same generated line adds a
package rather than a vendor, shares `Silk.NET.Core` with what is already in the graph, and rides an upgrade
the engine already performs in lockstep. It also carries the per-instance and per-device function-pointer
acquisition (`Vk.GetApi`, `TryGetInstanceExtension`, `TryGetDeviceExtension`) that a hand-rolled binding has to
invent, and loader discipline is exactly what the lavapipe race punished this engine for. `Vortice.Vulkan`,
hand-rolled P/Invoke, TerraFX and vendoring Veldrid's own `Vulkan.*` namespace are each argued and rejected
there.

`Internal/VulkanBindingSpike.cs` is what holds that decision honest. It is one file of never-called static
methods, one per area, covering the API surfaces the design spends:

- The loader entry points above, plus `VK_EXT_validation_features` chained into instance creation for the
  synchronization-validation rung.
- The device feature chain: `VkPhysicalDeviceFeatures2` with the 1.2 and 1.3 feature structures on its `pNext`,
  which is both how the device probe reads support and how device creation enables features by name.
- Timeline semaphore creation, host signal, host wait, the non-blocking counter read, and the submit-side value
  structure.
- `vkCmdBeginRendering` and `vkCmdEndRendering` with `VkRenderingInfo` and its attachment structure, plus the
  pipeline-side `VkPipelineRenderingCreateInfo`.
- `vkCmdPipelineBarrier2` with `VkDependencyInfo` and all three barrier2 structures.
- Dedicated allocations in both directions: `VkMemoryDedicatedRequirements` on the requirement query's `pNext`,
  and `VkMemoryDedicatedAllocateInfo` on the allocation that acts on the answer.
- The three platform surface extensions (Win32, Xlib, Wayland), and the four `VK_KHR_surface` queries the
  swapchain is sized and the presenting queue family is chosen from.
- The swapchain's whole cycle: create, image enumeration, acquire, present, destroy.
- The `VK_EXT_debug_utils` messenger with its callback signature, plus object naming.

It exists to fail at COMPILE time if a binding regression, a downgrade or a rename lands, so the failure
surfaces in the change that caused it rather than in whichever row first needed the member. The swapchain and
surface entries are the ones that most earn their place: every GPU CI leg in this repo is headless, so those
design sections have no automated coverage at all, and a binding surprise there would otherwise land in a
hand-run windowed session many rows later.

Be clear about what it is and is not. It is a compile tripwire over an API inventory, and that is the entire
claim: nothing in the list is ever called, so nothing here says a single one of those calls behaves correctly at
runtime. The one runtime observation anywhere near this package is the loader smoke below, which is a single
local run and covers the loader alone.

**Verdict, taken once and recorded here: the binding is sufficient.** Every API listed above exists on the
`2.23.0` line with the shape the design assumes, so decision V-P2 stands and the named replacement
(`Vortice.Vulkan`) is not taken. One constraint the design's prose did not state came out of the spike: Silk.NET
types the debug-utils callback as a CDECL function pointer, so the messenger callback must be
`[UnmanagedCallersOnly]` and therefore cannot capture. That is a compile error rather than a wrong ABI, which
is the right failure direction, and it constrains the row that wires the validation pump.

## The Linux loader, measured locally rather than assumed

`.github/workflows/cross-platform-gpu.yml` carries a step titled "Symlink libdl / libvulkan for Veldrid
(Linux)". It exists because Veldrid's Vulkan binding P/Invokes the bare names `libdl` and `libvulkan`, while
modern Ubuntu ships only the versioned `libdl.so.2` and `libvulkan.so.1` with no unversioned development
symlink, so the device init throws `DllNotFoundException` before anything renders. Silk.NET is supposed to
resolve through `Silk.NET.Core`'s native-context search, which includes the versioned soname, and therefore to
need no such step. That was asserted by a design draft and would have been expensive to discover in the
swapchain row, so it was checked here first.

**It resolves.** Measured on a developer machine, in a LOCAL `amd64` Ubuntu 25.10 Apple container running under
Rosetta, with `mesa-vulkan-drivers` 25.2.8 and lavapipe pinned through `VK_ICD_FILENAMES`, with **no unversioned
`libvulkan.so` present** and the bare name confirmed unresolvable in the same process as a control:

```
precondition: NO unversioned /usr/lib/x86_64-linux-gnu/libvulkan.so
CONTROL dlopen("libvulkan"): FAILED as expected -> Unable to load shared library 'libvulkan' ...
Vk.GetApi(): OK
vkEnumerateInstanceVersion: Success -> 1.4.321
vkCreateInstance(apiVersion=1.3): Success
physical devices: 1
vkDestroyInstance: OK
```

So the native Vulkan CI leg needs no symlink step of its own. The existing one stays where it is, because the
Veldrid leg it was written for still needs it.

Two caveats on how far that carries, because the run above did NOT happen in CI. It was one local container,
not a workflow leg, so what it establishes is that Silk.NET's native-context search finds a versioned soname
with no unversioned symlink present, which is a property of the binding rather than of any particular runner.
And the Mesa version is not CI's version: `cross-platform-gpu.yml` installs `mesa-vulkan-drivers` unpinned on
`ubuntu-latest`, and nothing has yet recorded what that resolves to, so 25.2.8 is EXPECTED to match CI and is
unverified until the `vulkaninfo` record in
[#541](https://github.com/APKiwiOrg/KhaozEngine/issues/541) lands. Neither caveat changes the conclusion about
the symlink step, and both are worth stating before somebody cites this section as a CI result.

## What the package may reference, and the one edge it may not

`KhaozEngine.Gpu.Vulkan` references `KhaozEngine.Gpu` and the three Silk.NET Vulkan packages. It declares NO
`Veldrid` package, and that is decision V-P3 rather than an accident of ordering: the shader path eventually
needs SPIRV-Cross, which arrives as `Veldrid.SPIRV`, and referencing it from a backend whose entire premise is
being Veldrid-free is a bad signal no guard reading package ids would catch. The edge stays in
`KhaozEngine.Gpu` behind its internal, Veldrid-free cross-compile helper.

That is asserted TWO ways, and the second one is the load-bearing half:

- `ArchitectureTests.NativeGpuBackend_DeclaresNoVeldridPackage` reads the project file, which catches the
  deliberate edit.
- `GpuPublicApiTests.NativeGpuBackend_ReferencesNoVeldridAssembly` reflects over the BUILT assembly's
  references. Veldrid is in this package's transitive closure through `KhaozEngine.Gpu` whatever the project
  file says, so an internal helper signature naming a Veldrid type would compile, would put a Veldrid assembly
  reference in this assembly's IL, and would be invisible to every public-surface scan there is.

`GpuPublicApiTests.GpuVulkanPublicApi_DoesNotLeakBackendTypes` adds the third rule: no `Silk` type on the
externally visible surface either. Not for load-path reasons, which V-P1 removes, but for the seam's own: a
`Silk.NET.Vulkan` type in a public signature makes a consumer that merely reads that signature compile against
the Vulkan binding, which turns an opt-in backend package into a second GPU vocabulary the engine would then
owe stability to.

## Layering

```
KhaozEngine.Gpu.Vulkan -> KhaozEngine.Gpu                     (the only direction. Gpu never references a backend package)
KhaozEngine.Gpu.Vulkan -> Silk.NET.Vulkan(+.Extensions.KHR/.EXT)   (its subject matter)
KhaozEngine.Gpu.Vulkan -> Veldrid*                            (never, asserted two ways)
```

See [docs/DEPENDENCY-SEAMS.md](../docs/DEPENDENCY-SEAMS.md), "Out-of-package graphics backends", for the
inverted-edge pattern this package is the second instance of.
