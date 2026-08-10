# KhaozEngine.Gpu.Metal

The engine's own native Metal backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Objective-C interop ever carries it.

> **Status: it creates a HEADLESS device with a TIMELINE and REAL RESOURCES that RECORDS AND SUBMITS, and it
> cannot present.** Rows 1 to 7 of the work breakdown are done:
> the assembly and its guard rows, the three phase-4 verification spikes, `KhaozEngineMetal.Register()` with the
> `IGpuBackendProvider` and the functional machine probe behind it, `GpuBackendKind.MetalNative = 6` with its
> `metal-native` token, and the Objective-C interop layer, a real `MTLDevice`, one `MTLCommandQueue`,
> `KE_METAL_DEVICE` selection, `KE_METAL_VALIDATION` reporting, the command-buffer error latch and the liveness
> token. Pipelines and the swapchain are not built, and every member that needs one
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
> Row 7 added the command list and wired the timeline to it: a fresh `MTLCommandBuffer` per `Begin`, the
> one-encoder-at-a-time lifecycle, and a submit that flushes the pending setup batch, then signals, attaches
> the handler and commits under one lock. The list can be begun, recorded against and submitted today, and
> every member that records CONTENT into it still names the row that builds it.
> Row 9 added the shader path: `CreateShadersFromSpirv` and `CreateComputeShaderFromSpirv` compile GLSL to
> `MTLLibrary` and `MTLFunction` per stage, and read the per-program binding table out of the emitted MSL. See
> [The shader path, and where a binding index comes from](#the-shader-path-and-where-a-binding-index-comes-from).

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
timeline and the fence on it, the command list with its encoder lifecycle and the submit path, the shader path
with its emission parse and binding table, plus the three verification spikes, which exist to answer a question
rather than to run in a game.

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
`LinearSampler` pair exists. Nothing can BIND any of it yet, because the members that record content into
a list belong to later rows, so what is usable today is creation, the device-level uploads, and readback
through `Map`.

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
first, so a texture uploaded and immediately read back sees the uploaded bytes. That drain is the QUEUE
drain rather than the timeline's, because a setup batch signals no timeline value and only a completed
empty buffer covers one. A write mapping does not drain, because the caller is the producer.

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

## The command list: a buffer per `Begin`, and no pool at all (M-R1 to M-R4)

`Begin` takes a fresh `MTLCommandBuffer` from the queue and retains it, `End` closes any open encoder and seals
the record, and the device's `Submit` encodes the timeline signal, attaches the completion handler and commits,
all under one lock. There is no engine-owned op stream: an `MTLCommandBuffer` between `commandBuffer` and
`commit` IS a driver-encoded command stream, so recording into a managed array first would encode twice and
move the driver-side encode inside the submit lock.

**A list and a fence are refused by IDENTITY rather than by type**, because a process can hold up to four live
native Metal devices and a type check passes another one's. A cross-device submit would commit another queue's
buffer while holding this device's lock, with this device's shared event encoded into it, and a cross-device
fence is worse for being silent: it polls the wrong counter and reads signalled for work this device never ran.
So a list carries the device that created it, a fence carries the timeline it names a value on, and both are
compared by reference at the submit.

**There is no command-buffer pool either, and that is where this backend diverges from the Vulkan one.** A
Vulkan list owns `FramesInFlight` command pools because a pool cannot be reset while its buffers are in flight.
A Metal command buffer is single-use, the queue owns its memory, and there is no reset, no pool object and no
allocator to choose between. So `KE_METAL_FRAMES_IN_FLIGHT` sizes the uniform ring and the drawable queue and
nothing else, and `GpuDeviceCounters.BackpressureStallCount` means ONE thing here where it means two on Vulkan.
Neither consumer is in the package yet, so today the variable is resolved, validated and named in the session
log and sizes nothing: the ring is [row 8](https://github.com/APKiwiOrg/KhaozEngine/issues/574) and
`maximumDrawableCount` is [row 15](https://github.com/APKiwiOrg/KhaozEngine/issues/581).
What the queue does have is its own maximum number of UNCOMMITTED buffers, past which `commandBuffer` blocks,
so the backend counts what it holds against that depth plus one (the separate present buffer) and warns once
rather than discovering it as a frame-loop stall with nothing attached.

**Exactly one encoder is open at a time, which is Metal's rule rather than a policy this backend invents.**
Three helpers own every transition and each ends the outgoing encoder before opening the incoming one.

**Ending an encoder discards EVERYTHING it held**, and this backend acts on all of it: the bound pipeline, the
whole argument table, the viewport, the scissor, every vertex stream and the index buffer. The incumbent
forgets the vertex streams there and is saved only by a second defect that makes its stream cache permanently
cold, so it re-binds every stream on every draw. This backend keeps the cache, which means it has to keep the
invalidation, and the test for it is written behaviourally (bind, force an encoder end through a blit, bind
again, assert the second bind was re-issued) because that shape fails on the corruption rather than on the
bookkeeping.

**N lists record concurrently here, and that is still not a promise of the seam.** Each list holds its own
command buffer and its own encoders, and this backend has no shared record-time state at all: no layout
tracker, no barrier batch, no device state cache. The portable contract remains one open recording per device,
and code that relies on more does not port.

## The shader path, and where a binding index comes from

`CreateShadersFromSpirv` takes two GLSL 450 sources and gives back a shader set. On the way it compiles each
stage to SPIR-V, cross-compiles the pair to MSL under a pinned option set, reads each stage's emitted entry
point, compiles each stage's MSL into its own `MTLLibrary`, and looks up the entry-point function by the name
the emission gave it. `CreateComputeShaderFromSpirv` is the single-stage sibling and also reports the workgroup
size read out of the module, because MSL does not carry it and `dispatchThreadgroups` needs those exact numbers.

**One library per STAGE is forced, not chosen.** SPIRV-Cross emits each stage as its own translation unit and
names both entry points `main0`, so compiling the two texts together is a duplicate-symbol error. The
entry-point name is READ rather than assumed for the same family of reason: the incumbent gets it from a Veldrid
layer this backend does not have, and a wrong name is not a compile error at all, it is a library that builds
and a nil function, so that is a separate refusal with its own message.

**Metal has no binding decorations, so where a resource landed is a fact about the emitted text.** There is no
`register(t3)` and no `layout(binding = 3)` on the far side: the cross-compiler assigns each resource an index
of its own, per stage, in an order that follows first reference rather than the shader's declarations. Counting
declarations on the CPU and hoping the two agree is what produced three recorded incidents in this engine (a
model pass reading the normal texture through the albedo sampler, a crease term reading depth data, and the
splat terrain reading one uniform buffer's bytes through another). So this backend does not count. It reads each
stage's emitted entry point, takes the SPIR-V id each argument's name spells, and resolves that id to a
`(set, binding)` through that stage's own `DescriptorSet` and `Binding` decorations. Decorations survive the
debug-info stripping that removes names, and each stage's ids are read from that stage's own module, which is
why this works where a name-keyed join does not.

**An element with no entry for a stage is NOT bound for that stage**, and that is correct rather than a gap: the
cross-compiler omits an argument a stage does not reference, and binding one anyway is the off-by-one.

**The parse never falls back to a count.** An argument name that is not the expected shape, an id with no
decorations in that stage's module, a `(set, binding)` outside the declared layout array, a kind that does not
match its index space, or two arguments landing on one element: each throws at shader-set creation, naming the
program, the stage and the argument. Two more throw earlier, where the arguments are read off the emitted text:
an index attribute that never closes, and an index that is not a number. Neither is reachable from anything the
cross-compiler emits today, and they throw rather than skip the argument because a dropped argument is one the
five refusals above can never see, so its element would read as unreferenced by that stage and simply not be
bound. This all happens with no device involved, so a shader whose emission cannot be read fails on a CI leg
that has no GPU rather than as a wrong pixel on one that does.

**The emission is pinned twice and neither pin covers the numbering.** One pin freezes the cross-compile options
and one freezes `MTLCompileOptions` (`languageVersion` 3.2, fast math on, `preserveInvariance` off, all measured
rather than assumed). Neither reaches the cross-compiler's naming or index assignment, which the binding table
depends on. What freezes those is the exact `Veldrid.SPIRV` version the engine pins, so that drift arrives on a
deliberate package bump and lands as a red device-free test rather than as a wrong frame.

**There is no compiled-shader disk cache, deliberately.** macOS already caches the MSL-to-library compile across
processes (0.02 ms for a source it has seen before, against 68 to 98 ms cold, both taken with the compiler
service warmed first so neither number is startup cost), and no public API can serialize a source-compiled
`MTLLibrary` anyway. The cost worth caching is the engine's own GLSL-to-MSL half, which is
tracked as [#592](https://github.com/APKiwiOrg/KhaozEngine/issues/592).

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
