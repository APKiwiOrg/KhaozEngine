# KhaozEngine.Gpu.Metal

The engine's own native Metal backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Objective-C interop ever carries it.

> **Status: it creates a HEADLESS device with a TIMELINE and REAL RESOURCES, and it cannot record or present.**
> Rows 1 to 6 of the work breakdown are done:
> the assembly and its guard rows, the three phase-4 verification spikes, `KhaozEngineMetal.Register()` with the
> `IGpuBackendProvider` and the functional machine probe behind it, `GpuBackendKind.MetalNative = 6` with its
> `metal-native` token, and the Objective-C interop layer, a real `MTLDevice`, one `MTLCommandQueue`,
> `KE_METAL_DEVICE` selection, `KE_METAL_VALIDATION` reporting, the command-buffer error latch and the liveness
> token. Command lists, pipelines and the swapchain are not built, and every member that needs one
> throws a message naming the row that builds it. WINDOWED creation refuses by naming
> [row 15](https://github.com/APKiwiOrg/KhaozEngine/issues/581), because a windowed device that cannot present
> is worse than one that says so at creation. `GpuBackendKind.Metal`, which goes through Veldrid, is the
> working Metal backend and stays selectable indefinitely.
> Row 5 added the timeline: one `MTLSharedEvent` per device, a real `IGpuFence`, a counted
> `WaitForIdle` in 250 ms slices so a device loss can release it, and a completion handler that reads every
> command buffer's outcome and latches only failures, keyed on the command queue.
> Row 6 added the resources: `IGpuDevice.Factory` creates buffers, textures, samplers and fences, the shared
> WRAP sampler pair exists, the device-level uploads work, and `Map` waits. See
> [Resources, and the one creation this backend refuses](#resources-and-the-one-creation-this-backend-refuses).

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
next row has to mean it. Everything else in the assembly is internal: the Objective-C interop layer, the
device and its queue, the provider, the machine probe and its device-free decision half, the completion
timeline and the fence on it, plus the three verification spikes, which exist to answer a question rather
than to run in a game.

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

## The device, the queue, and the two variables that steer them

`MTLCreateSystemDefaultDevice()` and one `MTLCommandQueue`, created under the seam's existing lifecycle gate.
There is no process-wide instance object on Metal, so the Vulkan package's refcounted-instance machinery has no
analogue here and none was invented, and there is no second queue and no async compute: a queue is documented
thread-safe, command buffers execute in ENQUEUE order, and `commit` enqueues, so committing under one lock makes
submit order the observable order by construction.

**`KE_METAL_DEVICE`** picks a different one. It takes a zero-based index into `MTLCopyAllDevices()`, a
case-insensitive substring of a device name, or `discrete`, `integrated` or `low-power`. Unset, the DEFAULT is
`MTLCreateSystemDefaultDevice()` rather than element zero of the enumeration, and that is a decision rather than
a shortcut: the incumbent Veldrid Metal backend calls that function, `GpuCapabilities.DeviceName` is compared
against it under a zero-permitted-difference bar, and taking the array's first element instead would swap the
GPU underneath the one gate that has to isolate the backend swap. An ordinary run therefore never enumerates at
all.

Metal has exactly one classification flag, `-isLowPower`, and no `isDiscrete` and no device-type enumeration of
the kind Vulkan has. So `discrete` means "not low-power", and `integrated` and `low-power` are the same
predicate under two names. A request that cannot be honoured WARNS with the full enumeration and falls back
rather than failing, because a name substring is machine-specific by nature and turning a stale value into a
refusal to start would make a diagnostic lever into a way of bricking a session. An ineligible device is never
chosen on any path, including an explicit index: honouring that pin would trade a warning now for a crash on
frame one. And the log line says SELECTION or SUBSTITUTED in as many words, because a soak session comparing
this backend against the incumbent has to tell those apart.

**`KE_METAL_VALIDATION`** takes `0`, `1` or `shaders`, and it REPORTS rather than arms. Metal API validation is
a process-launch mechanism: the runtime reads `MTL_DEBUG_LAYER` and `MTL_SHADER_VALIDATION` before the first
device exists and offers no way to arm validation afterwards. That was measured with a control rather than
assumed. So set the real variables on the command line:

```bash
MTL_DEBUG_LAYER=1 dotnet run --project <your game>
MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1 dotnet run --project <your game>
```

and the engine's knob then says which tier is really armed, checks that answer against the device's own
Objective-C class (a validated device is an `MTLDebugDevice`), and WARNS with the exact prefix above when a tier
was asked for and the process cannot have it. It also catches the case that looks like it should work and
cannot, a variable set from managed code after launch, because on Unix that never reaches the native
environment the Metal runtime read.

## Where a Metal failure shows up now

Every command buffer's `status` and `error` are read when it finishes, in every configuration, and the first
failure latches its `MTLCommandBufferError` code and the driver's own description at the site that saw it, flips
the device's liveness token so every later release is a no-op, and lands in the telemetry session header's
`deviceLossReason` field. The incumbent reads `status` in exactly one place and never reads `error` at all, so
this is reporting that did not exist rather than reporting that was moved.

Every failure latches, not only the codes that sound device-level. The GPU seam has no way to resubmit a
command buffer whose work Metal discarded, so a frame that failed would otherwise be followed by one reading its
results, and stopping is the conservative direction.

Teardown drains first, then flips liveness, then releases the queue and the device, in that order. Metal has no
device-level wait, so the drain is an empty command buffer committed and waited on, which covers the whole queue
because a queue executes in enqueue order.

## The timeline, and why a fence here is two fields (M-F1 to M-F5)

One device-wide `MTLSharedEvent` is the whole synchronisation story. Every submission encodes
`encodeSignalEvent:value:` with the next value before committing, a fence is a remembered value on that
counter, and `IGpuFence.Signaled` is one non-blocking `signaledValue` read. There is no Metal object behind a
fence at all.

**That turns the seam's fence ordering into a theorem.** The seam promises that a fence handed to a submission
made after some earlier work signals only once the queue has drained through it. With a completion callback per
submission that is a convention, because callback B firing says nothing about submission A, and Metal delivers
handlers on an arbitrary internal thread in no guaranteed order. A queue's signals on one event execute in
submission order with monotonic values, so the counter reaching 6 requires the signal at 5 to have happened.
Polling a later fence therefore covers every earlier submission, which is what `RetiredResourcePool` relies on.

**The completion handler is not deleted along with the fence dictionary.** It survives with reporting as its
only job: reading `status` and `error` at completion, which the incumbent never does for `error` at all, so a
Metal command-buffer failure is invisible to the engine and to telemetry today. The shared event owns ordering
and the handler owns reporting, and the handler takes no lock, touches no dictionary and advances no counter.

It is one global block carrying no captures, so it finds the right latch by reading `[commandBuffer
commandQueue]` and scanning a four-slot lock-free table. **The key is the queue rather than the device because
of a measurement:** `MTLCreateSystemDefaultDevice` called twice in one process hands back the same pointer, so
two engine devices on one GPU are indistinguishable by `MTLDevice` and a device-keyed table would both refuse
the second device's registration and route a torn-down device's late completions into its successor's latch. A
queue is a fresh object per `newCommandQueue` and each device has exactly one.

**`WaitForIdle` waits on the last submitted value in slices.** It blocks for as long as the GPU takes, and the
slice exists for the failure shape rather than for the timeout: a Metal command-buffer failure is asynchronous,
so a failed buffer's signal never arrives and the only notification is the error latch flipping the device's
liveness token from Metal's own completion thread. A single unbounded wait cannot observe that flip. The drain
counts one entry into `GpuDeviceCounters.DrainCount` and `DrainMs` per call that actually blocked. The slice has
one observable cost, which is up to 250ms of extra teardown latency after a device loss, since a waiter already
blocked inside the native call keeps blocking until its current slice expires.

## Resources, and the one creation this backend refuses

`IGpuDevice.Factory` creates buffers, textures, samplers and fences, and the device's `PointSampler` and
`LinearSampler` pair exists. Nothing can bind any of it yet, because there is no command list
([row 7](https://github.com/APKiwiOrg/KhaozEngine/issues/573)), so what is usable today is creation, the
device-level uploads, and readback through `Map`.

**Every buffer is `MTLStorageModeShared` and every texture is `MTLStorageModePrivate`**, reproducing the
incumbent. On unified memory a Shared buffer's `contents()` pointer is stable for its life and visible to both
sides, so a buffer write is a `memcpy` with no staging path, no flush and no invalidate. There is no allocator
and no `MTLHeap`: `newBufferWithLength:options:` IS the allocation.

**A buffer that declares BOTH `UniformBuffer` and a structured usage throws at creation, and that is a
deliberate divergence rather than a gap.** Both Veldrid backends accept the combination and nothing in this
engine creates it. A uniform buffer on this backend is rebased per frame by the uniform ring, and a structured
binding of the same buffer would read whichever segment that frame happened to land on. Create two buffers.

**A staging texture is not a texture.** It is a Shared `MTLBuffer` carrying the incumbent's SOFTWARE
subresource layout, byte for byte, because that is what every golden reads back through. `Map` reports the row
pitch and size for subresource 0 out of that arithmetic, which a checked-in 232-row table pins against
Veldrid's own functions with no device in the room. `Unmap` is a no-op, as it is in the incumbent, because a
Shared buffer's pointer needs no unmapping.

**`Map` waits, where the incumbent does not.** `MTLGraphicsDevice.MapCore` hands back `contents()` immediately,
which is correct today only because every engine caller drains first, so the seam's guarantee rests on a
convention rather than on the backend. Here a read mapping drains, and it commits the pending setup batch
first, so a texture uploaded and immediately read back sees the uploaded bytes. A write mapping does not drain,
because the caller is the producer.

**A device-level `UpdateTexture` records into a device-owned setup command buffer rather than issuing its own
queue submit.** The incumbent creates a staging texture, a command list and a whole `SubmitCommands` per call.
Here they accumulate into one buffer, flushed at the next device-level read. That trades the incumbent's one
live staging allocation for holding every payload since the last flush, so the open batch carries a **64 MB
staging budget**: an upload that would cross it commits the batch first. A five-layer 1024-square splat set
(ten uploads, 40 MB, no drain between them) still shares one batch, and a 2048-square one splits rather than
holding 160 MB.

**An upload region is checked against the destination subresource, and a resource is checked against the device
that created it.** Both refusals are `ArgumentException` shaped and both close a hole a caller could not
otherwise see: a payload of exactly the right length aimed one texel past the mip's edge, and a resource from
another `IGpuDevice`. The second matters more here than it reads: Apple silicon reports one `MTLDevice` for the
process, so two devices share a handle and a cross-device use SUCCEEDS, leaving their teardowns to disagree
about who releases what.

The `MipLodBias` a sampler description carries is dropped, because `MTLSamplerDescriptor` has no LOD bias field
at all, which is why `GpuCapabilities.SamplerLodBias` is false on this backend and on the incumbent alike.

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
