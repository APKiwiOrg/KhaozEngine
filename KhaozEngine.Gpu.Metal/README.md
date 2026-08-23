# KhaozEngine.Gpu.Metal

The engine's own native Metal backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Carried by the
`KhaozEngine.Game2D` and `KhaozEngine.Game3D` umbrellas since 18.0.0, so a repinned game gets it without adding
anything. It was opt-in and in no umbrella while the deleted Veldrid incumbent still shipped.

**"The incumbent" below always means the Veldrid Metal backend, deleted in 18.0.0.** It is cited throughout in
the past tense, because what it did is the behaviour this backend was built to reproduce or to diverge from,
and that reasoning is what makes the code readable. Nothing selects it any more.

> **Status: it creates a device, HEADLESS OR WINDOWED, with a TIMELINE and REAL RESOURCES that RECORDS AND
> SUBMITS, IT DRAWS, and IT PRESENTS.** Rows 1 to 7 of the work breakdown are done:
> the assembly and its guard rows, the three phase-4 verification spikes, `KhaozEngineMetal.Register()` with the
> `IGpuBackendProvider` and the functional machine probe behind it, `GpuBackendKind.MetalNative = 6` with its
> `metal-native` token, and the Objective-C interop layer, a real `MTLDevice`, one `MTLCommandQueue`,
> `KE_METAL_DEVICE` selection, `KE_METAL_VALIDATION` reporting, the command-buffer error latch and the liveness
> token. Neither `IGpuDevice` nor `IGpuCommandList` has an unbuilt member left. WINDOWED creation builds a real
> `CAMetalLayer` swapchain over the Cocoa `NSWindow` as of
> [row 15](https://github.com/APKiwiOrg/KhaozEngine/issues/581), and what a windowed request can still be
> refused for is the world rather than the package: this operating system, this machine, or a handle that is not
> an `NSWindow` with a content view.
>
> **THE DEFAULT ON MACOS SINCE 17.40.0.** The OS probe answers `GpuBackendKind.MetalNative` there now, so a game that
> references this package and calls `Register()` gets it without naming anything. The flip was taken by
> DECISION on 2026-08-22, ahead of the field-evidence gates the rollout still had open, and the dated addendum
> in section 17 of the design records which of them remain open as issues. Since 18.0.0 the package rides the
> `KhaozEngine.Game2D` and `KhaozEngine.Game3D` umbrellas, and `AppWindow` and both snapshot hosts call
> `GpuBackends.RegisterResolvedIfUnregistered()`, so a repinned game needs no new call. A game that
> references `KhaozEngine.Gpu` outside the umbrellas still calls `KhaozEngineMetal.Register()` itself, and a
> default with no registered provider throws `GpuBackendProviderMissingException` rather than falling back.
> `GpuBackendKind.Metal` is a RETIRED token: `KE_GRAPHICS_BACKEND=metal` or a stored user preference naming it
> is redirected onto `MetalNative` with a WARN, and naming it in CODE throws `GpuBackendRetiredException`.
> The macOS flip is the one with the largest blast radius of the three, because macOS is the fleet's
> DEVELOPMENT platform: every windowed playtest, capture, editor session and local golden run moved with it.
> A local BAKE was the sharp edge, and it stayed sharp for one release. This backend was a GUEST in the
> `metal` golden family, so a bare `KE_UPDATE_GOLDENS=1` was REFUSED and a bake had to name the owner with
> `KE_GRAPHICS_BACKEND=metal`. Since 17.41.0 it OWNS `metal-native`, a byte-identical copy of `metal`, so a
> bare local run reads and may bake that family instead.
> Gate 4's MM1 is still open.
> Row 5 added the timeline: one `MTLSharedEvent` per device, a real `IGpuFence`, a counted
> `WaitForIdle` in 250 ms slices so a device loss can release it, and a completion handler that reads every
> command buffer's outcome and latches only failures, keyed on the command queue.
> Row 6 added the resources: `IGpuDevice.Factory` creates buffers, textures, samplers and fences, the shared
> WRAP sampler pair exists, the device-level uploads work, and `Map` waits. See
> [Resources, and the one creation this backend refuses](#resources-and-the-one-creation-this-backend-refuses).
> Row 7 added the command list and wired the timeline to it: a fresh `MTLCommandBuffer` per `Begin`, the
> one-encoder-at-a-time lifecycle, and a submit that flushes the pending setup batch, then signals, attaches
> the handler and commits under one lock.
> Row 9 added the shader path: `CreateShadersFromSpirv` and `CreateComputeShaderFromSpirv` compile GLSL to
> `MTLLibrary` and `MTLFunction` per stage, and read the per-program binding table out of the emitted MSL. See
> [The shader path, and where a binding index comes from](#the-shader-path-and-where-a-binding-index-comes-from).
> Row 12 added framebuffers and the deferred render pass: `CreateFramebuffer`, `SetFramebuffer`, both clears and
> both scissor members. A recording can bind a target and clear it, and the clear lands on the attachment it
> names. See [Render passes: a descriptor per pass, and one index the incumbent got
> wrong](#render-passes-a-descriptor-per-pass-and-one-index-the-incumbent-got-wrong).
> Row 10 added resource layouts and resource sets, neither of which touches Metal at all, and deduplicated the
> binding table so two programs that map every element the same way share one. See
> [Layouts, sets, and the table two pipelines can share](#layouts-sets-and-the-table-two-pipelines-can-share).
> Row 11 added both pipelines: `CreateGraphicsPipeline` and `CreateComputePipeline` build real Metal state
> objects, and `SetPipeline` and `SetComputePipeline` record one with an identity guard. See
> [Pipelines, and the top of the buffer space](#pipelines-and-the-top-of-the-buffer-space).
> Row 13 added the bind flush: all four `Set*ResourceSet` overloads record, and a draw emits one ARRAY call per
> kind per stage through the binding table. See [The bind flush: one array call per kind per
> stage](#the-bind-flush-one-array-call-per-kind-per-stage).
> Row 14 made it RENDER: both `Draw` overloads, `DrawIndexed`, `Dispatch`, the vertex and index binds and the
> whole transfer family, which leaves `IGpuCommandList` with no unbuilt member at all. See [Draws, dispatches
> and transfers: the backend renders](#draws-dispatches-and-transfers-the-backend-renders).
> Row 16 finished what the device says about itself: the whole `GpuCapabilities` set at ZERO permitted
> differences from the deleted Veldrid Metal backend, every `GpuDeviceCounters` channel a device with no
> swapchain has, and a frame capture that takes this backend's own queue pointer. See [What the device
> reports about itself](#what-the-device-reports-about-itself).
> Row 15 made it WINDOWED: the `CAMetalLayer`, the drawable, the present, the queued resize and a vsync toggle
> that always applies, so `IGpuDevice` has no unbuilt member left. See [The swapchain: a layer, a drawable, and
> a present that cannot be skipped silently](#the-swapchain-a-layer-a-drawable-and-a-present-that-cannot-be-skipped-silently).
> [#592](https://github.com/APKiwiOrg/KhaozEngine/issues/592) added the EMISSION cache, which is the one M-S7's
> `.metallib` was refused in favour of: a warm start reads every program's MSL and its binding table off disk
> instead of running glslang and SPIRV-Cross, 3,443 ms of cold emission against 13 ms over the shipped corpus.
> See [The shader path, and where a binding index comes
> from](#the-shader-path-and-where-a-binding-index-comes-from).

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
with its emission parse, its binding table and the disk cache in front of both, the resource layouts and sets a
pipeline binds through, the two
pipeline types and the vertex-stream numbering behind them, the framebuffer and the pass schedule behind it,
the bind records and the argument batch that flushes them, the draw and dispatch path with the
pipeline-state block and the index binding behind it, the transfer family and its copy arithmetic, the
`CAMetalLayer` and the present boundary over it with its orphan target and its queued resize, plus the
three verification spikes, which exist to answer a question rather than to run in a game.

**Call `Register()` unconditionally and on every operating system.** Registering says a provider EXISTS, which
is a fact about your app's wiring. Whether this machine can run it is the separate question
`GpuBackendSelector.IsBackendSupported` answers, and the two are kept apart on purpose: a missing registration
THROWS, while an incapable machine falls back and reports it, and a log line where those two look alike is
exactly what a soak session cannot afford. Off macOS this costs one dictionary entry and loads no Objective-C
at all, because every entry point checks the platform guard before any body that names a Metal selector, and
those bodies are `NoInlining` so the JIT never compiles one.

## What the probe asks (M-N4)

The deleted Veldrid Metal backend's own support check created a device inside a bare catch. That is still the
FLOOR of this probe rather than the whole of it. On top of it, four reads, each cheap here and expensive
anywhere later:

- **a device exists and reports a name**, which is what `GpuCapabilities.DeviceName` parity depended on under a
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
`minimumBufferOffsetAlignment`, which is why the incumbent hardcoded `MetalFeatures.IsMacOS ? 16u : 256u`
rather than asking. So the probe asks for the real property through `respondsToSelector:` first, and a future
macOS that ships one is read with no code change, then falls back to
`minimumLinearTextureAlignmentForPixelFormat:`, which IS a device-reported buffer offset alignment. It reads 16
on that machine, which is exactly what the incumbent hardcoded for macOS, so two independent statements of the
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
a shortcut: the deleted Veldrid Metal backend called that function, `GpuCapabilities.DeviceName` was compared
against it under a zero-permitted-difference bar, and taking the array's first element instead would have
swapped the GPU underneath the one gate that had to isolate the backend swap. An ordinary run therefore never
enumerates at all.

Metal has exactly one classification flag, `-isLowPower`, and no `isDiscrete` and no device-type enumeration of
the kind Vulkan has. So `discrete` means "not low-power", and `integrated` and `low-power` are the same
predicate under two names. A request that cannot be honoured WARNS with the full enumeration and falls back
rather than failing, because a name substring is machine-specific by nature and turning a stale value into a
refusal to start would make a diagnostic lever into a way of bricking a session. An ineligible device is never
chosen on any path, including an explicit index: honouring that pin would trade a warning now for a crash on
frame one. And the log line says SELECTION or SUBSTITUTED in as many words, because a soak session comparing
this backend against the incumbent had to tell those apart.

**`KE_METAL_VALIDATION`** takes `0`, `1` or `shaders`, and it REPORTS rather than arms. Metal API validation is
a process-launch mechanism: the runtime reads `MTL_DEBUG_LAYER` and `MTL_SHADER_VALIDATION` before the first
device exists and offers no way to arm validation afterwards. That was measured with a control rather than
assumed. So set the real variables on the command line:

```bash
MTL_DEBUG_LAYER=1 dotnet run --project <your game>
MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1 dotnet run --project <your game>
```

and the engine's knob then says which tier is really armed, checks that answer against the device's own
Objective-C class, and WARNS with the exact prefix above when a tier was asked for and the process cannot have
it.

The class check knows all four devices Metal has been measured handing back:

| launch environment | device class | validated |
| --- | --- | --- |
| `MTL_DEBUG_LAYER=1` (with or without the shader variable) | `MTLDebugDevice` | yes, the API layer holds it |
| `MTL_SHADER_VALIDATION=1` alone | `MTLGPUDebugDevice` on real Apple silicon, `MTLLegacySVDevice` on a hosted `macos-26` runner | yes, the shader layer holds it |
| `MTL_DEBUG_LAYER=1 MTL_CAPTURE_ENABLED=1` | `CaptureMTLDevice` | no, the capture displaced the layer |
| nothing armed, or something displaced what was | the driver's own (`AGXG14CDevice` on Apple silicon) | no |

The disambiguation WARN fires only when a variable IS armed and NOTHING is validating after all, and it names
the variable you actually set. It asks whether any validation wrapper is holding the device rather than whether
one named class came back, because shader validation alone answers with two different class names on two
machines and pinning either one puts a false warning on the other. Before
[#628](https://github.com/APKiwiOrg/KhaozEngine/issues/628) the check was a single "does the class contain
Debug" test, so it fired on every `MTL_SHADER_VALIDATION`-only run, naming `MTL_DEBUG_LAYER` and calling a
validated run unvalidated. The knob also catches the case that looks like it should work and cannot, a variable
set from managed code after launch, because on Unix that never reaches the native environment the Metal runtime
read.

## Where a Metal failure shows up now

Every command buffer's `status` and `error` are read when it finishes, in every configuration, and the first
failure latches its `MTLCommandBufferError` code and the driver's own description at the site that saw it, flips
the device's liveness token so every later release is a no-op, and lands in the telemetry session header's
`deviceLossReason` field. The incumbent read `status` in exactly one place and never read `error` at all, so
this is reporting that did not exist rather than reporting that was moved.

Every failure latches, not only the codes that sound device-level. The GPU seam has no way to resubmit a
command buffer whose work Metal discarded, so a frame that failed would otherwise be followed by one reading its
results, and stopping is the conservative direction.

Teardown drains first, then flips liveness, then releases the queue and the device, in that order. Metal has no
device-level wait, so the drain is an empty command buffer committed and waited on, which covers the whole queue
because a queue executes in enqueue order.

## What the device reports about itself

**`GpuCapabilities` was held at ZERO permitted differences from the deleted Veldrid Metal backend**, which was a
stricter bar than the native Direct3D 11 backend's and the right one here: that Metal backend had no capability
defect to correct. While both shipped, a test created both devices in one process and compared every member,
carrying a reflection check that the comparison covered the whole struct, so a member added later could not
weaken the assertion by being forgotten.

Seven of the nine members are CONSTANTS, because the incumbent answered them with constants too. Metal's clip
space already matches the engine's, so `ClipSpaceYInverted` is false with no viewport correction anywhere.
`SamplerLodBias` is false, and it is the one capability that differs from both other native backends, because
`MTLSamplerDescriptor` has no LOD bias field at all. `SupportsShadowMaps` is true unconditionally: the
incumbent's own question routed to a format switch with no `R32_Float` case, so it answered true on every Metal
device, and asking Metal a real question instead could only have produced a parity difference.

**`MaxMsaaSampleCount` asks `-supportsTextureSampleCount:` for 32, 16, 8, 4, 2 and 1 and takes the first yes.**
That reproduces the incumbent's own walk, including the fact that it ignored the pixel format, and the
format-blindness is CORRECT rather than a bug carried for parity: `-supportsTextureSampleCount:` is the only
sample-count query Metal has and it takes no format, so a sample-count limit on Metal is format-independent by
construction. Asking per format would be inventing a question the API cannot answer. Downward matters, because
the supported counts are not required to be contiguous. An Apple M2 Max answers 4.

**`GpuDeviceCounters` is filled in every channel a device has**, cumulative since creation, so a telemetry
session brackets a window by subtracting two sampled rows. `BackpressureStallCount` here counts the uniform
ring's segment acquire ALONE, where the same field on the native Vulkan backend also folds in a command list
waiting on its own oldest buffer slot: this backend has no command-buffer pool to wait on, so a non-zero
reading is unambiguously a pipeline-depth statement about `KE_METAL_FRAMES_IN_FLIGHT`. The off-timeline pair is
deliberately NOT part of that number, because a deferred write blocks nobody and usually happens at load time.
`FramesBegun` and the acquire pair belong to the present boundary, so a headless device reports zero for all
three, which is literally true rather than a placeholder.

**`GpuDeviceDiagnostics.SoftwareAdapter` is FALSE with confidence rather than null**, because Apple ships no
software Metal rasterizer at all. That is a different fact from "nobody asked", which is what null means and
what the Veldrid Metal path correctly reported, unable to answer. `DeviceLossReason` is the latch above.

**A Metal GPU frame capture takes this backend's queue directly.** `GpuFrameCapture.ArmNext(path)` writes the
next frame to an Xcode `.gputrace`, and on this backend the capture names the `MTLCommandQueue` the device
created rather than reaching into Veldrid's private field by reflection, which is what the Veldrid Metal path
had to do. `MTL_CAPTURE_ENABLED=1` must be in the environment BEFORE the process launches, the same
process-launch rule the validation variables have. Without it the capture asks Metal whether the destination is
supported, gets no, and does nothing. That guard is not decoration: starting a capture in a process where
capture was never enabled raises an Objective-C exception, which is a process abort rather than a caught error.
An arm is consumed at a PRESENT boundary, which a windowed device reaches every frame as of row 15
(https://github.com/APKiwiOrg/KhaozEngine/issues/581). A HEADLESS device consumes none, which is honest rather
than unbuilt: it presents no frames, so there is no frame to capture.

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
Polling a later fence therefore covers every earlier submission, which is what `GpuRetireQueue` relies on.

**The completion handler is not deleted along with the fence dictionary.** It survives with reporting as its
only job: reading `status` and `error` at completion, which the incumbent never did for `error` at all, so a
Metal command-buffer failure was invisible to the engine and to telemetry there. The shared event owns ordering
and the handler owns reporting, and the handler takes no lock, touches no dictionary and advances no counter.

It is one global block carrying no captures, so it finds the right latch by reading `[commandBuffer
commandQueue]` and scanning a 64-slot lock-free table. **The key is the queue rather than the device because
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

`IGpuDevice.Factory` creates buffers, textures, samplers, fences, framebuffers and both pipelines, and the
device's `PointSampler` and `LinearSampler` pair exists. A FRAMEBUFFER can be bound and cleared, which is row
12's and which the render-pass section below covers, a RESOURCE SET can be bound, which is row 13's and which
the bind-flush section covers, and a PIPELINE can be bound, which is row 11's and which the pipelines section
covers. What is missing is the DRAW itself, so nothing is rendered yet: what is usable today is creation, the
framebuffer bind and its clears, the resource-set binds, the pipeline binds, the device-level uploads, the
record-time `UpdateBuffer` described below, and readback through `Map`.

**Every buffer is `MTLStorageModeShared` and every texture is `MTLStorageModePrivate`**, reproducing the
incumbent. On unified memory a Shared buffer's `contents()` pointer is stable for its life and visible to both
sides, so a buffer write is a `memcpy` with no staging path, no flush and no invalidate. There is no allocator
and no `MTLHeap`: `newBufferWithLength:options:` IS the allocation.

**A buffer that declares BOTH `UniformBuffer` and a structured usage throws at creation, and that is a
deliberate divergence rather than a gap.** Both deleted Veldrid backends accepted it and nothing in this
engine creates it. A uniform buffer on this backend is rebased per frame by the uniform ring, and a structured
binding of the same buffer would read whichever segment that frame happened to land on. Create two buffers.

**A staging texture is not a texture.** It is a Shared `MTLBuffer` carrying the incumbent's SOFTWARE
subresource layout, byte for byte, because that is what every golden reads back through. `Map` reports the row
pitch and size for subresource 0 out of that arithmetic, which a checked-in 232-row table pins against the
numbers Veldrid's own functions produced, with no device in the room. `Unmap` is a no-op, as it was in the
incumbent, because a Shared buffer's pointer needs no unmapping.

**`Map` waits, where the incumbent did not.** `MTLGraphicsDevice.MapCore` handed back `contents()` immediately,
which was correct at the time only because every engine caller drains first, so the seam's guarantee rested on a
convention rather than on the backend. Here a read mapping drains, and it commits the pending setup batch
first, so a texture uploaded and immediately read back sees the uploaded bytes. That drain is the QUEUE
drain rather than the timeline's, because a setup batch signals no timeline value and only a completed
empty buffer covers one. A write mapping does not drain, because the caller is the producer.

**A device-level `UpdateTexture` records into a device-owned setup command buffer rather than issuing its own
queue submit.** The incumbent created a staging texture, a command list and a whole `SubmitCommands` per call.
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
at all, which is why `GpuCapabilities.SamplerLodBias` is false on this backend, as it was on the incumbent.

**`GpuSamplerAddress.Border` is a DEVICE FEATURE here, and this backend diverged from the incumbent over it.**
Sampler border colours are a `MTLGPUFamilyMac2` feature, so a Metal device that answers no to that family has
none: the debug layer asserts on any sampler descriptor carrying one, and without the layer armed the device
samples something other than a border. The incumbent wrote the border colour whenever it was on macOS and never
asked the device, which meant it armed border colours on a machine that cannot honour them. This backend asks
once, at device creation, off the family answer the probe already reads for the floor, and then does two things
the incumbent did not:

- **the property is written ONLY when an address mode is `Border`.** A Wrap, Mirror or Clamp sampler never sends
  `-setBorderColor:` at all, so the shared WRAP pair and every shipped engine sampler are untouched by
  construction rather than by a check.
- **a `Border` sampler on a device without support is REFUSED BY NAME**, with a `NotSupportedException` saying
  which family answer produced the refusal and what to use instead. Creating it anyway is not the safer option:
  it aborts the process under `MTL_DEBUG_LAYER=1` and mis-samples without it.

No sampler the engine itself builds asks for `Border` on any axis, so nothing shipped is affected either way.
What the refusal bites is a test fixture that exercises every address mode, and a future consumer asking for a
border sampler on a virtualized GPU. The hosted `macos-26` runner's Apple Paravirtual device is exactly that
device, and it is where the difference was found.

## The uniform ring: a `memcpy` where the incumbent split the encoder (M-M3 to M-M8)

**A `UniformBuffer`-usage buffer is ONE `MTLBuffer` of `align(size, 256) * KE_METAL_FRAMES_IN_FLIGHT`**, and
none of that is visible through the seam. `IGpuBuffer.SizeInBytes` still reports what you asked for, the buffer
identity never changes, and the segment base is applied at BIND.

**A segment is one RECORDING's version of the uniforms, not one frame's.** The rotation happens at
`IGpuCommandList.Begin`, so the recording that claims index N writes segment `N % framesInFlight`, captures that
segment for as long as it is recording, and its submission reads it. A segment is handed out again only after the
`MTLSharedEvent` has reached the value that submission signals. The depth therefore buys N RECORDINGS of
headroom, and a typical frame opens several command lists (the scene list, a preview capture, an ocean prime
pass, one per retire barrier), so frames of headroom is that number divided by the lists your frame opens. The
variable keeps the name it has on the other two backends, and nothing that reasons about depth should read
"frames" literally.

**The point is the ENCODER rather than the copy.** On the incumbent a record-time `UpdateBuffer` allocated an
`MTLBuffer`, copied, ENDED THE RENDER ENCODER to open a blit encoder, copied again and released, and then the
next draw paid a full graphics-state re-activation, because ending a render encoder discards the bound
pipeline, the whole argument table, the viewport, the scissor and every vertex stream. Under the ring the same
call is a `memcpy` into mapped memory and opens nothing at all. The shipped renderers write a uniform buffer
per pass and often per draw.

**It is also a CORRECTNESS change, which the same ring was not on Direct3D 11.**
`MTLGraphicsDevice.UpdateBufferCore` was an unguarded copy into `contents()` with no fence, no frame index and
no diagnostic. Direct3D 11's `MAP_WRITE_DISCARD` lets the driver rename a buffer under a write and Metal
renames nothing, so that was a plain data race with a submitted command buffer, and the segment gate is what
removes it. Automatic hazard tracking does not help: it orders GPU work against GPU work and says nothing about
a CPU write racing a GPU read.

**A record-time uniform write LANDS THE MOMENT IT IS MADE, which is the ring's one consequence for a
renderer.** It records no command, so it is not ordered against the draws in the same list: two writes to the
same range inside one frame leave the second value for every draw of that frame, including the draws recorded
between them. The deleted Veldrid Metal backend ordered the same write against the draws, so this was a real
difference between the two Metal backends, and it was measured on both in one process by the engine's
`RecordTimeUniformRewriteGpuTests`. Per-draw and per-pass uniforms are addressed by dynamic offset rather than by
rewriting one range, which is what the engine's renderers do and what makes the ring possible at all.

**A device-level `UpdateBuffer` on a uniform buffer reaches EVERY segment**, gated on the same completion read,
with a segment an earlier recording is still reading queued as a pending patch applied at its next claim rather
than waited for. So the call never blocks, from any thread, and a value written once
persists for the buffer's life exactly as it does on a backend where the buffer has one copy. Writing only the
current segment was a shipped defect elsewhere for one release: a load-time write reached one segment in three,
so two frames in three bound memory nothing had ever written, with nothing thrown and nothing logged.

**At `KE_METAL_FRAMES_IN_FLIGHT=1`, and only there, that call BLOCKS.** The never-blocks property comes from
copying the current segment ungated and deferring the others, and at a depth of one there are no others: the
ungated copy would be a CPU write into the one segment the GPU may be reading, which is the exact race the ring
exists to close. So at the floor it waits for the submission that last read the segment before copying.
Correct but slow is the right trade at a depth that exists for measuring, and it is worth knowing before reading
a capture taken there.

**Every OTHER record-time `UpdateBuffer` stages through a per-list arena and pays one blit.** Bulk payloads
genuinely need the copy command, so what the arena removes is the incumbent's allocate-and-release of a whole
`MTLBuffer` per call, which its own source carried a TODO asking for. Blocks are pooled by power-of-two size
class, sub-allocated by bumping, and handed back only once the timeline has reached the value the submission
that read them signalled. The arena never waits: a slot still in flight keeps its blocks and gets them back at
a later visit. It retains up to 8 MiB of idle blocks and releases the largest first past that.

**The rotation boundary is `IGpuCommandList.Begin` on this backend**, where both sibling backends put theirs at
`Present`. Each of those has a second per-list index that advances at `Begin` and this one has none, and
hanging the acquire off a present would leave the ring rotating never on the headless path. `Begin` is
therefore the only call in a recording that can block, and `GpuDeviceCounters.BackpressureStallCount` counts
exactly those blocks and nothing else.

**Two lists recording at once each write their own segment**, which follows from the same boundary: each
captures its segment at its own `Begin`, so a second list beginning mid-recording does not move where the first
one's writes land. What the depth bounds is how many recordings may be open or in flight at once, so a program
that keeps more than `KE_METAL_FRAMES_IN_FLIGHT` recordings open without submitting them has two of them sharing
a segment. Raise the depth if you build one.

**One creation-time refusal follows from all of it**, and it is the divergence named above: a buffer declaring
both `UniformBuffer` and a structured usage throws, because the ring rebases every bind of it and a structured
binding of the same buffer would read whichever segment the frame landed on.

**A record-time upload to a non-uniform buffer needs a four-byte-aligned destination offset.** macOS requires
that of the copy, the size half is padded up the way the incumbent padded it, and the offset half throws by
name rather than shipping the incumbent's answer to it, which was an embedded compute shader and a dedicated
pipeline for a case no shipped call site produces. Every record-time site in the engine passes 0 or a multiple
of an element stride. A ring-backed write has no such requirement, being a `memcpy`.

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
allocator to choose between. So `KE_METAL_FRAMES_IN_FLIGHT` sizes the uniform ring, each list's staging arena
and the drawable queue, and nothing else, and `GpuDeviceCounters.BackpressureStallCount` means ONE thing here
where it means two on Vulkan. All three are live as of
[row 15](https://github.com/APKiwiOrg/KhaozEngine/issues/581), so raising the variable costs a
256-aligned segment per uniform buffer per extra frame, plus one more drawable in the layer's queue on a
windowed device, and nothing more. A headless device spends only the first two, having no layer.
What the queue does have is its own maximum number of UNCOMMITTED buffers, past which `commandBuffer` blocks,
so the backend counts what it holds against that depth plus one (the separate present buffer) and warns once
rather than discovering it as a frame-loop stall with nothing attached.

**Exactly one encoder is open at a time, which is Metal's rule rather than a policy this backend invents.**
Three helpers own every transition and each ends the outgoing encoder before opening the incoming one.

**Ending an encoder discards EVERYTHING it held**, and this backend acts on all of it: the bound pipeline, the
whole argument table, the viewport, the scissor, every vertex stream and the index buffer. The incumbent
forgot the vertex streams there and was saved only by a second defect that made its stream cache permanently
cold, so it re-bound every stream on every draw. This backend keeps the cache, which means it has to keep the
invalidation, and the test for it is written behaviourally (bind, force an encoder end through a blit, bind
again, assert the second bind was re-issued) because that shape fails on the corruption rather than on the
bookkeeping.

**N lists record concurrently here, and that is still not a promise of the seam.** Each list holds its own
command buffer and its own encoders, and this backend has no shared record-time state at all: no layout
tracker, no barrier batch, no device state cache. The portable contract remains one open recording per device,
and code that relies on more does not port. The engine's own recordings all open through `GpuRecording`, which
refuses a second one whatever the backend is, so nothing engine-shipped exercises this property.

## The shader path, and where a binding index comes from

`CreateShadersFromSpirv` takes two GLSL 450 sources and gives back a shader set. On the way it compiles each
stage to SPIR-V, cross-compiles the pair to MSL under a pinned option set, reads each stage's emitted entry
point, compiles each stage's MSL into its own `MTLLibrary`, and looks up the entry-point function by the name
the emission gave it. `CreateComputeShaderFromSpirv` is the single-stage sibling and also reports the workgroup
size read out of the module, because MSL does not carry it and `dispatchThreadgroups` needs those exact numbers.

**One library per STAGE is forced, not chosen.** SPIRV-Cross emits each stage as its own translation unit and
names both entry points `main0`, so compiling the two texts together is a duplicate-symbol error. The
entry-point name is READ rather than assumed for the same family of reason: the incumbent got it from a Veldrid
layer this backend does not have, and a wrong name is not a compile error at all, it is a library that builds
and a nil function, so that is a separate refusal with its own message.

**Metal has no binding decorations, so where a resource landed is a fact about the emitted text.** There is no
`register(t3)` and no `layout(binding = 3)` on the far side: the cross-compiler assigns each resource an index
of its own, per stage, in an order that follows first reference rather than the shader's declarations. Counting
declarations on the CPU and hoping the two agree is what produced three recorded incidents in this engine (a
model pass reading the normal texture through the albedo sampler, a crease term reading depth data, and the
splat terrain reading one uniform buffer's bytes through another). The MECHANISM behind the last of those three
was measured against this backend in 2026-08, and it is the count: for a fragment function that reads set 1
alone, the emission puts the buffer at `buffer(0)` and a declaration-order count puts it at `buffer(1)`, so the
incumbent wrote it at an index the function does not read. What the measured shape then produces is an
ALL-ZERO read, because it leaves the fragment's `buffer(0)` unbound. The incident's own symptom, one buffer's
bytes arriving through another, needs the earlier buffer bound to the reading stage, so it is a sibling of the
measured shape rather than the measured shape, and it stays unreproduced rather than refuted. This backend
reads correct bytes for the program that was measured (`MetalTwoUniformBufferGpuTests`). So this backend does
not count. It reads each
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
depends on. What freezes those is the exact `Silk.NET.Shaderc` and `Silk.NET.SPIRV.Cross` versions the engine
pins, so that drift arrives on a deliberate package bump and lands as a red device-free test rather than as a
wrong frame. Those versions are in the emission cache's key as well, read off the assemblies the process loaded
rather than out of the props file, so a bump partitions the cache instead of serving the previous
cross-compiler's output
([#610](https://github.com/APKiwiOrg/KhaozEngine/issues/610)).

**The disk cache holds the EMISSION, not a `.metallib`.** macOS already caches the MSL-to-library compile across
processes (0.02 ms for a source it has seen before, against 68 to 98 ms cold, both taken with the compiler
service warmed first so neither number is startup cost), and no public API can serialize a source-compiled
`MTLLibrary` anyway, so caching one was measured, refused and written down. The cost the OS does not touch is the
engine's own half, GLSL to SPIR-V and then SPIR-V to MSL, and that is what is cached
([#592](https://github.com/APKiwiOrg/KhaozEngine/issues/592)). One file per program under
`<local-app-data>/KhaozEngine/metal-msl/<engine version>/`, keyed on the shader sources, all three pinned option
sets, the engine version, the `Silk.NET.Shaderc` and `Silk.NET.SPIRV.Cross` versions that emitted it and the
module version ids of the two assemblies that PRODUCE the payload, holding every stage's MSL, every stage's
entry-point name, the binding table read off that emission and a compute kernel's workgroup size. Over the
shipped corpus of 42 programs that is 333 KiB and turns 3,443 ms of cold emission into
13 ms.

**Old engine versions' folders are swept at cache open** ([#611](https://github.com/APKiwiOrg/KhaozEngine/issues/611)).
The engine version is a path segment so an upgrade leaves one obviously prunable folder, and nothing used to
prune one, so a machine kept a folder per engine version it had ever run. Opening the cache now deletes every
sibling version folder under `<local-app-data>/KhaozEngine/metal-msl/`, running-version folder excepted, best
effort: one that will not delete is skipped on its own and nothing propagates. Only the DEFAULT location is
swept, never a directory `KE_METAL_MSL_CACHE` names, which is taken verbatim with no version segment and whose
neighbours are the caller's own files.

**Why the producing assemblies are in the key, and what it costs you.** The pins name the toolchain, and four of
those payload fields are read OUT of the emission by engine code the pins do not cover: the entry-point name and
the argument list, the binding table, the reflected layouts and the workgroup size. Within one engine version,
editing any of them would otherwise keep serving the payload the previous build wrote, with no error anywhere. So
`KhaozEngine.Gpu.Metal` and `KhaozEngine.Gpu` are in the hash by their MVID, which the compiler rewrites on every
build and which therefore cannot be forgotten the way a hand-bumped constant can. If you are DEVELOPING the
engine, a rebuild of either assembly invalidates the cache and re-emits the corpus once, about 3.4 seconds. If
you are CONSUMING a release, its assemblies were built once, so nothing changes for you.

**`KE_METAL_MSL_CACHE` relocates it or turns it off.** Point it at a directory to move it (a CI workspace, or a
machine whose local app data is not writable), or set it to any of `off`, `0`, `false`, `no` or `none` to emit
fresh every time, which is what to do when you are chasing a binding or a shader problem and want to be sure of
what ran. Any other value is a directory path, which is why the disable words are a set rather than `off` alone.
A cache that cannot be read or written is a slower start and nothing else: every failure is a miss. A file that
is present but does not authenticate, does not restate its own key, is not this payload format, or describes a
binding table that fails its structural checks is a miss AND a delete, because a wrong table is the one thing
this backend must never accept quietly.

## Layouts, sets, and the table two pipelines can share

`CreateResourceLayout` and `CreateResourceSet` are live, and **neither makes a single native call**. Metal has no
`MTLResourceLayout` and no descriptor-set layout, because an argument table is addressed by integer per stage, so
a layout here is the engine's own bookkeeping and nothing else. Metal's answer to a descriptor set is an argument
buffer, which this backend declines by name: the engine's per-frame binding traffic is dominated by offsets-only
rebinds of ONE set, which argument buffers do not improve, and every route to them changes the emitted MSL for
every program at once, which would put the committed `metal-native` goldens in play. While the incumbent still
shipped it would also have destroyed the byte-equality parity claim in the same move.

**A layout counts nothing.** The incumbent's layout object was the same element array plus per-kind counters, and
its bind path re-walked the whole layout array to sum them on every single bind. That arithmetic is right only
where the compiler's first-reference order happens to equal declaration order, which is the mechanism behind the
three incidents the section above lists. What declaration order is for here is that an element's POSITION is its
binding number, which is the key the binding table is read through. The only refusal a layout adds is a per-draw
dynamic offset declared on a texture or a sampler, because the offset is applied with `setBufferOffset:` and that
exists only in the buffer space, so declaring one anywhere else would be dropped at every bind with nothing said.

**A set resolves everything at creation and nothing at a bind.** A set is created once at load time and bound
thousands of times a frame, so each binding comes out already carrying which argument table it goes in, the
resolved resource whose Objective-C object a bind writes, and the numbers a buffer bind composes its offset
from: the window's own offset and whether the caller's per-draw offset is added. The window is the range's size,
or the buffer's own LOGICAL size for a bare buffer, which on a ring-backed uniform buffer is emphatically not
its allocation.

**The two things a bind reads live are the handle and the uniform ring**, through the resource's own disposal
guard rather than a copy taken at creation, because those two are exactly what disposal changes: a disposed
buffer answers a nil handle and a null ring, and the ring's null is what stops a write reaching the
`contents()` pointer of an `MTLBuffer` that has been released. So disposing a resource a set still names
degrades that binding to an unbound slot rather than putting a released pointer in the argument table.

**What a set refuses at creation.** A staging texture, because it is a Shared `MTLBuffer` on this backend rather
than an `MTLTexture`, so there is nothing to write into the texture table. A resource that is already disposed,
because a binding that starts out nil has no later point at which it could come right. A texture bound for a
direction it was not created for, so a `TextureReadWrite` element needs `GpuTextureUsage.Storage` and a
`TextureReadOnly` element needs `Sampled` (or `GenerateMipmaps`): a texture's Metal usage bits are fixed at
creation and no view narrows or widens one here, so binding one without the bit it is read or written through is
a validation abort rather than something the bind could arrange. And a layout or a set that has been DISPOSED is
refused where it is handed out, since neither releases anything and the call would otherwise work.

**Which stages a set binds for is decided by the binding table and NOT by the declared visibility flags.** The
seam's `GpuShaderStages` is what the engine declared, the table is what the compiler did, and an element with no
entry for a stage is not referenced by that stage's function and must not be bound for it.

**The table is content-deduplicated per device, and that is what lets a pipeline switch keep its binds.** Metal's
argument tables are absolute and per encoder, so a bound resource survives a pipeline switch: what a switch can
invalidate is only the mapping from an element to an index. Two programs that map every element identically
therefore invalidate nothing, and with a fresh table object per program that comparison would be a reference test
that is never equal, so every switch would invalidate everything, which is exactly what the incumbent already
did. Tables are keyed on a content string rendering the layout shape and every entry, canonicalised at
shader-set creation because a table is a property of the emission, and never evicted, because a rebuilt instance
would silently start invalidating again. Measured over the shipped catalog at row 10, on 2026-08-10, **42
programs produced 17 distinct tables and 25 programs shared one with an earlier program**. That is a measurement
of the renderers as they stood rather than a property of this package, so it moves as the catalog does.

## Pipelines, and the top of the buffer space

`CreateGraphicsPipeline` and `CreateComputePipeline` build real Metal state objects, and a command list can
record one. Nothing draws yet, because the draw itself is
[row 14](https://github.com/APKiwiOrg/KhaozEngine/issues/580).

**Vertex streams are pinned at the TOP of the `[[buffer(n)]]` space, 30 downward, and resource buffers grow from
0 wherever the emission put them.** The one real collision in Metal's binding model is that both share one space
on the vertex stage, and the fork's answer made each numbering depend on the other's count: a stream landed at
`NonVertexBufferCount + i`, computed in two places that had to agree. That count is the CPU's belief about
where the resource buffers went, which is the quantity this backend's binding table replaces as the authority,
so it is not reproduced. Top pinning depends on nothing, and it moves no pixel: a stream's index is invisible to
the emitted MSL, which reaches attributes through `[[stage_in]]`, so it only has to agree between the vertex
descriptor and the bind, both of which this backend owns. Over the 34 shipped graphics programs the highest
buffer index any vertex function's emission chose is **0**, so 30 of the 31 entries are free and the largest
shipped pipeline declares two streams. A pipeline whose combined vertex-stage bindings would collide throws at
creation naming both sides, and the assertion is read out of the table's vertex-stage entries rather than out of
declared stage visibility, because a stage that never references an element has no index for it.

**A pipeline is where the declared layouts are checked against the shader's reflection.** The binding table is
keyed on `(set, binding, stage)` read out of the shader's own decorations, so a layout array of a different
shape resolves every element through a key that means something else, silently. Pipeline creation is the first
moment both arrays exist together, so that is where the check runs, for graphics and for compute.

**A graphics pipeline is two Objective-C objects and the second exists only for a depth output.** The render
pipeline state carries the functions, the vertex layout, the attachment formats and the per-attachment blend
state, which is what lets the multiple-render-target passes blend one attachment while preserving another's
destination. The depth-stencil state is its own object, created only when the pipeline declares a depth
attachment, because setting one on a pass with no depth attachment is a validation error. Everything else the
seam calls pipeline state (cull mode, winding, fill mode, depth clip, the blend colour) is ENCODER state, so it
is resolved once at creation and emitted when the pipeline changes.

**`DepthClipEnabled` is the one rasterizer member Metal has no field for, and it is honoured anyway.** Metal has
no rasterizer depth-clip enable, so the seam's `false` becomes `-setDepthClipMode:MTLDepthClipModeClamp` and its
`true` becomes `MTLDepthClipModeClip`, which is the same behaviour Direct3D 11 gets from
`RasterizerDescription.DepthClipEnable` and Vulkan from the inverse of `depthClampEnable`. This backend shipped
reproducing the incumbent's rule instead, which derived the mode from the DEPTH TEST and read the seam's flag
nowhere at all, and `17.39.0` corrected both Metal paths together
([#598](https://github.com/APKiwiOrg/KhaozEngine/issues/598)): the vendored Veldrid fork carried the identical
change as `4.9.104`. Fixing only this one would have left the native leg disagreeing with the `metal` grids the
incumbent baked. Neither moved a committed golden.

**A compute pipeline is created from the function alone**, with no `MTLComputePipelineDescriptor`. The
descriptor exists to carry per-buffer mutability, the incumbent filled it by counting buffer-kind elements in
declaration order, and that counter is the arithmetic this backend removes. Metal's default infers the same
mutability from the function's own `const device` and `constant` qualifiers.

**Binding the pipeline that is already bound does nothing.** The incumbent cleared its whole active-set array and
set a changed flag on every call including a redundant one, so a repeat bind cost a state re-emit plus a full
re-activation of every resource set. Here it costs one reference comparison. Which pipeline is bound survives an
encoder boundary, because the recorder still intends it, and whether its state has reached the current encoder
does not, because that is encoder state: the two are tracked separately rather than collapsed into one flag.

**And a real switch is what drives the other two rows.** It tells the pass schedule the incoming pipeline's
`ScissorTestEnabled`, which is the gate deciding whether a scissor rectangle is emitted at all, and it hands the
bind records the incoming program's binding table, which is what invalidates a recorded resource slot only where
the two programs map that slot's elements to different indices. Both hang off the identity guard above, so a
redundant bind touches neither, and the compute bind carries the second one for the same reason a dispatch has.

**A disposed pipeline is refused at the bind, by name.** Disposing one releases its `MTLRenderPipelineState` and
its `MTLDepthStencilState`, so binding it afterwards would leave a later draw setting a released object on a
live encoder. That is the one refusal on this path about memory rather than about a caller's belief: a disposed
resource layout or resource set releases nothing at all here, where a pipeline genuinely has.

## The bind flush: one array call per kind per stage

All four `Set*ResourceSet` overloads are live. A bind RECORDS into a per-slot `(set, offset)` array and emits
nothing, and the next draw or dispatch flushes every dirty slot, so several binds between two draws collapse to
one flush and a bind that changes nothing costs nothing.

**A flush emits ONE ARRAY CALL per (kind, stage), not one call per resource per stage.**
`setVertexBuffers:offsets:withRange:` and its five siblings take a range, so a full activation of a
model-shaped set is one buffer call, one texture call and one sampler call on the fragment stage plus one
buffer call on the vertex stage. The incumbent emitted one call per element per stage, which is a fan-out defect
this engine already paid to fix once on another API, and the vendored Metal fork's binding layer did not
declare a single array setter, so these are hand-written against the ABI a spike measured on real hardware. All
twelve selectors (eight on the render encoder, four on the compute encoder, which is a separate protocol) are
sent to a live encoder by a `[GpuFact]`, including the vertex stage's texture and sampler tables, which only a
program whose vertex function samples can reach.
Measured across the 34 shipped graphics programs, the worst full activation is **6 array calls** (`Water`, which
is the only shape whose two stages between them read all three tables), and **no shipped program produces a
non-contiguous run**, so the extra call a hole would cost is never paid today. A hole CUTS the run rather than
being padded with nil, because Metal's tables are absolute and a nil in a gap would unbind whatever another slot
legitimately put there. A nil HANDLE is the opposite case and does not cut anything: the index is being written,
and it is written with nil, which Metal reads as an unbound index. That is what a resource disposed since its
set was created degrades to, including in the middle of a run. The budget test asserts an EQUALITY rather than a ceiling: it walks each program's own
binding table, counts the contiguous index runs, and requires the flush to have emitted exactly that many array
calls. Over the whole catalog 92 array calls carry 131 arguments, and a flush that emitted one call per argument
would be red on both numbers while satisfying any per-(space, stage) ceiling that could be written.

**A slot whose only change is its dynamic offset takes a different selector entirely.**
`setVertexBufferOffset:atIndex:` writes an integer into the encoder's command stream where `setBuffers:` writes
whole argument-table entries, and it is emitted once per stage that actually reads the buffer. That is the
shadow pass's shape thousands of times a frame. It is only legal for a slot whose set is the one already in
this encoder's table, so what selects it is a comparison against what the flush last EMITTED rather than a
third state on the record: a slot bound to another set and back again with no draw between takes it correctly,
because the first set never left the table.

**And what was emitted includes each binding's LIVENESS, not just which set it was.** A binding holds the
resource wrapper and reads its handle and its ring through that wrapper's own disposal guard at every bind,
which is what makes a resource disposed after its set was built degrade to nil by construction. The bindings
array does not move when that happens, so a set whose buffer was released between two draws still compares
equal, and moving an offset on a table index holding a released buffer is something Metal accepts without a
word. Each slot therefore records, per binding, whether it was ringed and whether its handle was non-nil, and
any movement falls back to the full rebind, whose nil handle is the safe degradation.

**Every bind record carries an encoder-epoch stamp, so a boundary re-activates it.** A record-time
`UpdateBuffer` big enough to take the staging path opens a blit encoder mid-pass, and the reopened pass is a new
encoder with an empty argument table. The incumbent tracked vertex-stream binds and did NOT invalidate them
there, and was saved only by a second defect that made its cache permanently cold. Porting the tracking without
the invalidation would ship a corruption no golden reaches, because the goldens do not restart a render pass
mid-scene, so both arrive together here: two vertex streams cost one array call on the draw that binds them,
zero on every draw after, and one again after any encoder boundary.

**A pipeline switch invalidates recorded slots only where the incoming program's binding TABLE differs.** Two
programs sharing a table invalidate nothing, which the deduplication above is what makes possible. A switch
that does invalidate clears the epoch stamps rather than only marking slots dirty, because the incoming program
expects each element at a different index and an offsets-only rebind would move an offset on a binding it never
reads.

**A buffer bind's offset is `frameBase + rangeOffset + callerDynamicOffset`**, where the frame base is the ring
segment THAT RECORDING captured at its `Begin` and never the allocator's live one: shipped paths open several
lists per frame, so a bind composed against the live segment would name a version the recording never wrote. A
composed window that would leave its own segment is refused by name, and that refusal is genuinely this path's:
the same check at set creation passes a zero caller offset and cannot fire. Nothing else would report it, since
`setBufferOffset:` carries an offset and no length at all.

**And the composed offset has to be aligned the way the DEVICE says.** The segment base is a multiple of the
256-byte ring stride by construction, but the set's own range offset and the caller's per-draw offset are raw
values, and an unaligned result is a validation error under the debug layer and undefined behaviour without it.
The number checked against is the device's own reported buffer-offset alignment, read at creation and carried
down to the bind records, not the ring stride: macOS reports 16 or 32, so checking against 256 would refuse
binds every Mac accepts. The refusal names all three components.

**A refused flush is refused whole.** Any exception out of a flush drops the staged writes and invalidates every
slot's emitted state, so the next draw re-derives every arm and re-binds in full. Without that, a throw part way
through leaves staged writes for the next flush to emit into the wrong stage's table, and a throw after an
earlier stage emitted leaves the record naming the previous set while the encoder holds the new one, which is
the exact state in which an offsets-only rebind moves an offset on somebody else's buffer.

**Which has one consequence worth stating as a usage rule, because the device run found it in this package's own
test fixture.** A dynamic element bound as a BARE BUFFER takes the buffer's logical size as its window, and the
ring stride is that size rounded up to 256, so the window already fills the segment and there is no room for a
per-draw offset of any size. A dynamic element therefore has to be bound as a `GpuBufferRange` window, which is
what every shipped resource-set shape already does and what `MetalRingStrideTests` asserts over all of them. The
refusal names the numbers, so a caller who gets it wrong is told at the first draw rather than reading another
frame's uniforms.

## Render passes: a descriptor per pass, and one index the incumbent got wrong

`IGpuDevice.Factory.CreateFramebuffer` gives back a render target, and `SetFramebuffer`, `ClearColorTarget`,
`ClearDepthStencil`, `SetScissorRect` and `SetFullScissorRects` record against it. The draw is
[row 14](https://github.com/APKiwiOrg/KhaozEngine/issues/580), so what a recording can do today is bind a
target, bind a pipeline into it and clear it.

**A framebuffer creates nothing native, and that is the API rather than a simplification.** A Metal render pass
is an `MTLRenderPassDescriptor` built per pass from the attachment textures themselves, so there is no render
pass object to cache, no framebuffer object to rebuild when the window resizes, and no invalidation of either.
The framebuffer here is an aggregate of borrowed texture handles, flattened once at construction, and its
disposal releases nothing. Attachments are mip 0 slice 0 because `CreateFramebuffer` takes bare textures with no
mip and no layer parameter, which is the same reason this package declares no texture-view factory at all.

**The begin is deferred to the first draw, so a clear costs no command.** `ClearColorTarget` before a draw
stores the value, which becomes `loadAction = Clear` on that attachment when the pass opens. A clear recorded
AFTER the pass opened ends the pass and goes back on the pending array, because Metal has no clear command and
there is no cheaper shape available. A framebuffer plus a clear plus an `End` with no draw at all still clears,
through a begin and end pair with nothing between them.

**The clear lands on the attachment you NAME, and that was a deliberate difference from the deleted Veldrid
Metal backend.** That one wrote every clear into `colorAttachments[0]`, so a framebuffer with more than one
colour target cleared only its first, and the engine's own model pass clears three attachments of `ModelFB`
and shipped a comment describing the collapse. The two attachments that were never cleared loaded a freshly
created `StorageModePrivate` texture nothing had written, which was undefined rather than stable. A
`KE_METAL_CLEAR=attachment0` switch reproduced the collapse for rollout gate 1's A/B and was removed at that
gate with the losing branch, so there is nothing to set: the clear lands where you asked, always. The A/B's
answer is worth carrying, because it says what the goldens can and cannot see. The suite passed in BOTH
positions, so no committed golden distinguishes the two, and the instrument that does is a `[GpuFact]` that
clears two attachments and reads the second one back as a texel.

**Store actions are set explicitly.** The descriptor's own default DISCARDS the attachment, so every colour and
depth attachment is given `MTLStoreActionStore` rather than left alone. Depth `DontCare` is a real win on a
tile-based GPU and is deliberately not taken here: it leaves contents undefined, and undefined is not stable
across runs.

**The viewport and the scissor are emitted on a framebuffer CHANGE only.** There is no `SetViewport` on the GPU
seam at all: the engine gets one because Veldrid's own `SetFramebuffer` auto-applied a full viewport and a full
scissor, inside an identity guard. Both halves are reproduced. A backend that never emits rasterises nothing,
and one that emits on every bind silently restores the full scissor and renders the next draw outside the
rectangle the caller set. The scissor is additionally gated on the bound pipeline's `ScissorTestEnabled`: Metal
has no scissor-test enable and its rectangle is always live, so the gate is this backend honouring the seam's
own rasterizer state, which is what keeps it agreeing with Direct3D 11. A rectangle gated out by one pipeline
stays owed to the next one that wants it.

## Draws, dispatches and transfers: the backend renders

`Draw`, `DrawIndexed`, `Dispatch`, `SetVertexBuffer`, `SetIndexBuffer`, `CopyBuffer`, `CopyTexture`,
`CopyTextureSubresource`, `GenerateMipmaps` and `ResolveTexture` are live, which completes the minimal
renderable path: a recording binds a framebuffer, clears it, binds a pipeline, binds resource sets and
geometry, and draws.

**The order inside a draw is four steps and it is written once.** The pass opens (the deferred begin, which
folds the pending clears into load actions and emits the viewport and the scissor if either is owed), then the
pipeline-state block, then the resource-set flush and the vertex-stream flush, then the command. It lives in
one private helper because five entry points repeating it would be five places for a step to go missing, and a
missing step renders plausibly wrong rather than throwing.

**The index buffer is the one binding an encoder boundary does not discard.** Metal takes it, its byte offset
and its element width in the draw call ITSELF rather than binding any of them beforehand, so it never reaches
an argument table. Everything else a draw needs (the argument tables, the vertex streams, the pipeline-state
block, the viewport, the scissor) is encoder state and is re-issued after any boundary. The topology is a draw
ARGUMENT here too, where Direct3D 11 sets it on the input assembler and Vulkan bakes it into the pipeline, so
the pipeline resolves it once at creation and nothing is mapped per draw.

**Two dependent dispatches inside one recording are ordered, and that is a BACKEND PROPERTY rather than a
contract.** The compute encoder is opened with the SERIAL dispatch type, so dispatches inside it do not overlap
and a read-after-write between them needs no barrier, which is why this backend carries no barrier batch, no
layout tracker and no dependency analysis at all. **The GPU seam's compute rule 2 is unchanged**: chaining
dependent dispatches still needs `End`, `Submit` and `WaitForIdle` in portable code, because the other backends
need the drain and code that drops it because THIS one tolerates the chain breaks on them.

**`ResolveTexture` DESTROYS the source texture's contents, and that is deliberate.** The resolve is a
standalone render pass whose one colour attachment is the multisampled source at `loadAction = Load` and
`storeAction = MultisampleResolve`, with the destination as its resolve texture. Metal's resolve store action
does not also store the multisampled attachment, so the source is undefined afterwards. This diverges from
Direct3D 11's `ResolveSubresource` and Vulkan's `vkCmdResolveImage`, which both leave the source alone, and it
is reproduced from the deleted Veldrid Metal backend rather than fixed: the engine re-clears its MSAA render
targets at the start of the next frame's pass, discarding is the bandwidth-correct answer on Apple's tile-based
architecture, and the committed `metal` goldens were baked under it. **If you need the source preserved, copy it
before resolving.** A consumer relying on the source surviving a resolve is relying on behaviour this backend
does not have.

**A resolve ends the render pass you have open, and it does not flush a clear-only one.** It is the only
recorded command here that needs its own end, because it opens a render encoder of its own and every other
command that interrupts a pass opens a different encoder kind. So a resolve issued mid-pass costs a pass
boundary, which discards the pipeline state, every argument-table entry, the viewport, the scissor and every
vertex stream, and the next draw pays a full re-activation. What it does NOT do is force out a pass that
collected clears and saw no draw: those clears stay owed until a draw, a framebuffer change or `End`, so
clearing a target and resolving it with nothing drawn resolves its PRE-CLEAR contents. That is the deleted
Veldrid Metal backend's behaviour reproduced rather than smoothed.

**`CopyBuffer` requires both offsets to be multiples of four, and refuses by name when they are not.** macOS
requires that of the underlying copy. The SIZE is padded up for you, which lands inside the destination's own
allocation by construction. An unaligned OFFSET throws rather than being routed through an embedded compute
shader the way the Veldrid backend did, because no shipped call site in the engine produces one and a
device-free test over every call site is what keeps that true. Align the offset, or use the device-level
`UpdateBuffer`, which is a plain copy with no blit behind it.

Since 17.40.0 that refusal is **not this backend's alone**. The offset half of the rule moved up to the seam, so
`KhaozEngine.Gpu.Vulkan` and `KhaozEngine.Gpu.D3D11` refuse the same offsets in the same words, and a
call that works on a developer's Windows machine no longer fails on a player's Mac
([#602](https://github.com/APKiwiOrg/KhaozEngine/issues/602)). The SIZE padding stays local, because Metal is the
only backend that needs it.

**Mip generation is one call.** `-generateMipmapsForTexture:` fills the whole chain, so unlike the Vulkan
backend there is no per-level blit, no per-level barrier and no filter to choose. The texture needs more than
one mip level and must not be a staging texture, which on this backend is an `MTLBuffer` with a software
subresource layout and has no texture to generate from.

## The swapchain: a layer, a drawable, and a present that cannot be skipped silently

`GpuDeviceContext.CreateForWindow` on this backend resolves the Cocoa `NSWindow` into a `CAMetalLayer` (adopting
the host view's if it already has one, creating and attaching one if it does not), configures it, takes a
drawable, and hands back a device whose `SwapchainFramebuffer` is the same object for the rest of its life.
`Present()` presents the drawable the frame rendered into, applies anything a resize or a vsync change queued,
and acquires the drawable the next frame will use.

**The layer configuration is the deleted Veldrid Metal backend's, field for field, with one exception.** `device`,
`pixelFormat` (`BGRA8Unorm`, or its sRGB sibling if the seam ever grows a way to ask), `framebufferOnly = true`,
and `drawableSize` from the host view's frame.

**The exception is that `drawableSize` is the view frame in POINTS multiplied by
`-[NSWindow backingScaleFactor]`, because a drawable size is in PIXELS**
([#605](https://github.com/APKiwiOrg/KhaozEngine/issues/605)). The incumbent's NSView arm wrote the points
straight through, which opened a Retina window at half its real resolution until the first framebuffer-resize
callback corrected it, and the incumbent's own UIView arm multiplied by the native scale. The two arms of one
constructor disagreed and only one of them could be right, so this is a fix rather than an improvement smuggled
into a backend swap. Only the FIRST frame moves: `ResizeSwapchain` already writes the pixel size the windowing
layer forwards, so the steady state never had the defect. A degenerate scale falls back to 1.0. That arm is defensive rather than reachable: the resolve refuses a zero
window handle and a nil content view before the scale is ever read, so only a future caller that skips those
refusals could deliver the nil receiver whose message send answers 0. The arithmetic lives in `MetalSwapchainPolicy` and is asserted device-free on every leg,
which is why the scalar is read rather than the whole conversion handed to `-[NSView convertRectToBacking:]`.

**Four more things change, and each answers something the incumbent got wrong.**

**Vsync always applies.** The incumbent wrote `displaySyncEnabled` only when its `MTLFeatureSet` enumeration
landed on one of three values of an enum deprecated since macOS 10.15, so on a machine outside that set a vsync
toggle silently did nothing. `CAMetalLayer.displaySyncEnabled` is a macOS property on a macOS-only backend and
needs no capability test, so it is written unconditionally.

**A frame with no drawable renders somewhere and counts.** `-nextDrawable` returns nil when the layer has none
to give. The incumbent's framebuffer then reported itself unrenderable, every draw in that frame was silently
discarded, and nothing was logged or counted. Here the framebuffer is repointed at a device-owned ORPHAN TARGET
at the current size, the frame records, submits and completes exactly like any other, only its PRESENT is
skipped, and it counts into `GpuDeviceCounters.FramesBegun` because a skipped present is not a skipped frame.
The first one WARNs once per device. A minimised window is the ordinary cause and it recovers by itself.

**The drawable acquire is measured.** `-nextDrawable` BLOCKS when every drawable is still in flight, and Metal
offers no zero-timeout probe, no semaphore form and no readiness query, so the stall is not removable. It is
counted and timed into `GpuDeviceCounters.AcquireWaitCount` and `AcquireWaitMs` instead, one entry per boundary,
which is exactly what that pair's own documentation says a CPU-blocking acquire reports. Expect it to be
non-zero under vsync: a vsync-paced frame SHOULD wait for a drawable. `maximumDrawableCount` is set to
`KE_METAL_FRAMES_IN_FLIGHT`, so the depth of the drawable queue and the depth of the uniform ring are one
number.

**A resize is queued and applied at the next present boundary, after a drain.** `ResizeSwapchain` stores a size
and returns: no lock, no native call, nothing that can block, so a window callback arriving on any thread while
the submit thread is committing is safe. The incumbent applied it inline on the calling thread, recreating its
depth texture (releasing one in-flight frames may still be reading) with no drain anywhere. A runtime
`SyncToVerticalBlank` change queues the same way, and a burst of thirty size events between two presents costs
one apply.

**The present rides its own command buffer**, exactly as the incumbent did it. `Present()` is a separate seam
call from `Submit()`, so the frame's own buffer is already committed by the time a present runs, and
`-presentDrawable:` on a later-committed buffer runs after it by queue order anyway. One extra command buffer
per frame is a rounding error, and it is counted: the uncommitted-buffer bound this backend asserts is the
frames-in-flight depth PLUS ONE, and the one is this buffer.

**The swapchain framebuffer has no depth attachment and no MSAA**, matching the incumbent as the engine drives
it: `GpuWindowedDeviceRequest` carries a window, a size and a vsync flag, and there is no way to ask for either.
Its `Outputs` are fixed at construction, so every pipeline built against the window survives every resize.

**A headless device answers `null` for `SwapchainFramebuffer` and does nothing at a `Present()`**, which is
correct rather than unbuilt. `FramesBegun` and the acquire pair are then genuinely zero rather than absent,
which is what `GpuDeviceCounters.HasValue` being true is for.

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
`GpuPublicApiTests` walks the built IL, which is the half that binds, because until 18.0.0 Veldrid was in the
transitive closure through `KhaozEngine.Gpu` whatever the project file said.

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
