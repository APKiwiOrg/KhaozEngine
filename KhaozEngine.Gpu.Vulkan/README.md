# KhaozEngine.Gpu.Vulkan

The engine's own native Vulkan backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Vulkan binding ever carries it.

> **Status: REGISTRATION, PROBE, A HEADLESS DEVICE, ITS COMPLETION TIMELINE, ITS MEMORY ALLOCATOR, THE COMMAND
> LIST'S LIFECYCLE, THE UNIFORM RING, THE RESOURCE FACTORY, THE DESCRIPTORS AND THE BIND FLUSH.**
> `KhaozEngineVulkan.Register()`
> is real, so is the machine-capability probe behind it, and since
> [#514](https://github.com/APKiwiOrg/KhaozEngine/issues/514) so is headless device creation:
> `GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative)` builds a real `VkDevice` and a graphics queue on
> the one refcounted process `VkInstance`, with its features enabled by name, its device-loss latch armed and
> `KE_VULKAN_VALIDATION` wired. Since [#515](https://github.com/APKiwiOrg/KhaozEngine/issues/515) that device
> also owns its ONE timeline semaphore, a real `IGpuFence` over it, the deferred-disposal retire list, and a
> `WaitForIdle` that is a counted `vkWaitSemaphores` rather than a device-wide wait. Since
> [#516](https://github.com/APKiwiOrg/KhaozEngine/issues/516) it owns its block suballocator too, complete and
> with nothing yet allocating out of it, because the first resource is
> [#519](https://github.com/APKiwiOrg/KhaozEngine/issues/519). And since
> [#517](https://github.com/APKiwiOrg/KhaozEngine/issues/517) it hands out real command lists with their own
> per-slot `VkCommandPool`s, and `Submit` is one `vkQueueSubmit` on the queue. Since
> [#518](https://github.com/APKiwiOrg/KhaozEngine/issues/518) it also owns the UNIFORM RING and the per-list
> staging arena, and both `UpdateBuffer` levels route through them. And since
> [#519](https://github.com/APKiwiOrg/KhaozEngine/issues/519) it has a REAL RESOURCE FACTORY: buffers, textures
> with every image view they will ever need already made and a canonical resting layout assigned, samplers with
> the wrap-addressed shared pair, staging textures backed by a `VkBuffer` with the incumbent's software
> subresource layout, `Map` and `Unmap` with the read drain, and a device-owned setup command buffer under its
> own short lock that means NO texture creation submits anything to the queue. And since
> [#520](https://github.com/APKiwiOrg/KhaozEngine/issues/520) it has DESCRIPTORS: content-deduplicated
> `VkDescriptorSetLayout`s and `VkPipelineLayout`s, pools sized from actual demand whose free path restores every
> counted type, and `CreateResourceLayout` and `CreateResourceSet` handing out one `VkDescriptorSet` allocated
> and written once at creation with the bind window as its range. And since
> [#521](https://github.com/APKiwiOrg/KhaozEngine/issues/521) all four resource-set binds are LIVE: they record
> into a two-state per-slot array and issue nothing, and the flush behind them emits one
> `vkCmdBindDescriptorSets` per contiguous run of dirty slots with a positional `pDynamicOffsets` composed over
> every dynamic descriptor in the run. The pipeline-layout compatibility prefix and both of decision V-R7's guards
> landed with it, ahead of the pipeline row that calls them. That device cannot yet
> RENDER: the rest of the recording content is
> [#522](https://github.com/APKiwiOrg/KhaozEngine/issues/522),
> [#524](https://github.com/APKiwiOrg/KhaozEngine/issues/524) and
> [#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525), framebuffers are
> [#522](https://github.com/APKiwiOrg/KhaozEngine/issues/522), shaders are
> [#526](https://github.com/APKiwiOrg/KhaozEngine/issues/526) and pipelines are
> [#523](https://github.com/APKiwiOrg/KhaozEngine/issues/523), and each unbuilt member throws a message naming
> its own row. Creating a WINDOWED device refuses, naming the swapchain row
> ([#527](https://github.com/APKiwiOrg/KhaozEngine/issues/527)), rather than handing back a device that cannot
> present. The backend IS nameable: `GpuBackendKind.VulkanNative` and the `vulkan-native` / `vk-native` tokens
> landed with [#513](https://github.com/APKiwiOrg/KhaozEngine/issues/513). Nothing selects it by default.
> `KhaozEngine.Gpu`'s `Vulkan` backend, which goes through Veldrid, remains the working Vulkan path and stays
> selectable indefinitely.

## The spec

Everything this package will become is specified in
[docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md](../docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md),
phase 3 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), following the shipped phase 2
([KhaozEngine.Gpu.D3D11](../KhaozEngine.Gpu.D3D11)). Section 18 is the nineteen-row work breakdown each
implementation issue comes from, section 2 carries the eight contested decisions with their arguments and
rulings, and section 16 is the honesty ledger of what nobody can currently measure. Read the design before
adding anything here: several of the shapes below look like oversights and are decisions.

## Registering it, and the one machine question it can answer

```csharp
using KhaozEngine.Gpu.Vulkan;

KhaozEngineVulkan.Register();   // once, at startup, on every OS
```

That is the package's entire public surface, and the pin test in `KhaozEngine.Render.Tests` holds it to one
member. Everything else (the provider, the probe, the requirement check, the binding spike) is internal, because
the seam speaks engine types in both directions and a `Silk.NET.Vulkan` type on a public signature would make a
consumer that merely reads it compile against the Vulkan binding.

Registering is a fact about your app's WIRING and says nothing about the hardware. Whether the machine can run
the backend is the separate question `GpuBackendSelector.IsBackendSupported` answers, through this package's own
functional probe: a Vulkan loader, a throwaway instance at the 1.3 floor, then every physical device read
against section 5.2's four hard requirements (apiVersion at or above 1.3, `dynamicRendering`,
`synchronization2`, `timelineSemaphore`) and section 4.1's three further reads (a host-visible `HOST_COHERENT`
memory type, `maxDescriptorSetUniformBuffersDynamic` at or above what the shipped pipeline layouts spend, and,
on the windowed path, a graphics family that presents). The instance is destroyed before the answer comes back,
on every path, which is why the decision is taken over a copied snapshot rather than over live handles.

The probe NEVER throws. A machine with no loader, no ICD or a pre-1.3 driver answers false, because "we could
not even ask" and "no" are the same answer to a settings screen and to the fallback that consume it. On the
macOS developer machines this is written on there is no Vulkan loader at all, so the first read answers and the
rest is never reached.

**Three machine states, not two, and creation asks about them before it creates anything.** A machine with no
loader is one state and a machine with a loader and a driver is another, and between them sits a machine with a
**loader and no ICD**: the ordinary state of a bare CI image and of most servers. There the loader resolves and
answers `vkEnumerateInstanceVersion` out of its own version, and the first call that can know there is nothing
behind it is `vkCreateInstance`, which fails `VK_ERROR_INCOMPATIBLE_DRIVER`. The probe reports that as the
machine fact it is, with the same sentence it gives an instance that creates and then enumerates zero devices,
and it names the fix (`mesa-vulkan-drivers` on Debian and Ubuntu, which brings lavapipe). `CreateHeadless`
consults the probe FIRST, so all three states behave: no loader and no driver both refuse with a
`NotSupportedException` about the MACHINE, and a loader with a driver gets a real device. A creation-time
`InvalidOperationException` is therefore what it says it is, a failure on a machine whose probe really did
answer yes.

Keeping those two questions apart is decision V-I4, and here it bites harder than it did on Direct3D 11. On
Linux the OS probe already returns `GpuBackendKind.Vulkan`, so a native request that fails falls back to the
incumbent Vulkan backend and reports `FallbackAfterFailure`, which in a log line looks a great deal like a
forgotten registration. A forgotten registration THROWS instead, and telling those two apart is what the whole
soak measurement rests on.

## Naming it

`GpuBackendKind.VulkanNative` is the kind this package registers under, and two `KE_GRAPHICS_BACKEND` tokens
reach it:

```
KE_GRAPHICS_BACKEND=vulkan-native   # or the shorter vk-native
```

The whole token is matched, so a typo'd suffix is an unrecognized override with its own loud diagnostic rather
than a silent run on the incumbent implementation under the new name. **`vulkan` still means Veldrid's Vulkan
and keeps meaning it indefinitely**, which is not a transitional state: it is the kill switch every structural
decision in the design leans on, so an A/B between the two implementations is one environment variable away.

Naming the backend today reaches a real HEADLESS device and the windowed refusal above, and the refusal arrives
through the reported fallback rather than as a crash: the creation path catches, WARNs with the message and
boots on the incumbent, reporting
`GpuBackendSource.FallbackAfterFailure`. Nothing selects it for you. The Linux default is still
`GpuBackendKind.Vulkan` and stays there until every rollout gate is green (decision V-RO3), and
`GpuBackendSelector.SupportedBackends()` does not offer the native kind to a player at all, because a settings
screen offers an API rather than an implementation of one.

`VulkanNative` renders the same images as `Vulkan`, so it is a GUEST in the committed `vulkan` golden family
rather than owning one (decision V-I3). That is what will hold it to the incumbent's already-committed
reference grids, unmodified, on the same rasterizer at the same tolerance. `KE_UPDATE_GOLDENS` is REFUSED on it
for the same reason: a bake would overwrite the very references it is being checked against, and the file it
wrote would be exactly the file it would then have compared against.

Code that cares about the API rather than the implementation asks `kind.IsVulkan()`, true for both members.
Plain equality against `GpuBackendKind.Vulkan` is the right question only when Veldrid's implementation
specifically is meant.

## The instance, the device and the queue

**One `VkInstance` for the process, refcounted.** The first device creates it, the last one destroys it, and
every device in between shares it. `VkApplicationInfo.apiVersion` is `min(1.3, vkEnumerateInstanceVersion())`,
asked rather than assumed. The instance extension list is the whole list: nothing at all on the headless path,
which is why the golden suite runs on a machine with no display server, plus `VK_EXT_debug_utils` under the
validation knob. The support probe is deliberately NOT a holder: it creates and destroys its own throwaway
instance, because it has to answer before any device exists.

Why one instance is more than tidiness, stated as the hypothesis it is: concurrent device creation once raced
the Vulkan loader on lavapipe and was fixed by serialising creation process-wide. The racing operation was
`vkCreateInstance` and the ICD enumeration under it, so with one refcounted instance the golden suite's repeated
device creation stops touching that path after the first device. Bet MV7 tests that on the CI leg, and the
process-wide creation gate stays on regardless, so the bet costs nothing if it loses.

**One graphics queue.** No transfer queue and no async compute. The graphics family must also present on the
windowed path, and a device whose graphics family cannot present is refused with a named reason and routed
through the reported fallback.

**Features are enabled by name.** `dynamicRendering`, `synchronization2` and `timelineSemaphore` are REQUIRED,
and a device missing one is refused with that feature's name and what depends on it in the message.
`samplerAnisotropy`, `fillModeNonSolid`, `depthClamp` and `independentBlend` are enabled when the device offers
them and degrade quietly when it does not. `geometryShader`, `tessellationShader`, `multiViewport`,
`drawIndirectFirstInstance` and `shaderFloat64` are READ for capability reporting and never enabled, because
nothing in this engine uses them.

**A device loss reports why, at the site that noticed.** Every `VkResult` is checked in EVERY configuration,
which is the one thing this backend could not inherit: the incumbent's `CheckResult` is
`[Conditional("DEBUG")]`, so a latch built on its shape would never fire in a Release build. On
`VK_ERROR_DEVICE_LOST` the call's name and the result are latched immediately, the liveness token flips so every
later destroy is a no-op, and the reason reaches you two ways: an ERROR line in the session log and the
`deviceLossReason` field of the telemetry session header. There is no recovery path, which is what the liveness
token makes safe. Teardown calls `vkDeviceWaitIdle` FIRST, before anything is destroyed.

## One timeline, and why one is enough

**The device owns ONE `VkSemaphore` of type TIMELINE, created at 0.** Every submission takes its next value,
every `IGpuFence` holds a target on it, and `WaitForIdle` waits for the last value handed out. There is no
`VkFence` anywhere in this backend, and there is no second submit signalling an internal tracking fence.

**That is a correctness argument, not a tidiness one.** The seam promises that a fence handed to a submission
made after some earlier work signals only once the queue has drained through it. With per-submit fences that is
a convention, because fence B signalling says nothing about submission A. With one monotonic timeline it is a
theorem: a timeline semaphore's signal operations must strictly increase, and a queue's signal operations on one
semaphore execute in submission order, so the counter reaching 6 requires the signal at 5 to have happened,
which requires submission 5's commands to have completed. Polling a later fence therefore transitively covers
every earlier submission, which is what `RetiredResourcePool` relies on.

**A fence poll never waits and never takes a lock.** `IGpuFence.Signaled` is one `vkGetSemaphoreCounterValue`
compared against the fence's target, and `Reset` unarms it for a later submit. After the device is destroyed
every fence reads SIGNALLED, because a dead device has no outstanding work and answering otherwise strands a
retire pool on a batch it can never free.

**`WaitForIdle` is `vkWaitSemaphores` on the last submitted value, with no timeout, counted into `DrainCount`
and `DrainMs`.** Not `vkQueueWaitIdle`: a semaphore wait holds no queue lock, so a drain on one thread does not
block a submit on another, and it names a value, which is what gives a drain a number. A drain that found the
GPU already caught up is not counted, because the seam's own counter documents that it should not be.

**Disposal is DEFERRED behind the timeline.** Disposing a resource records the device's current timeline value
and holds the native destroy until the counter passes it. Today that list drains only at device teardown, the
frame-boundary drain arrives with row 17's `Present` path, which is the only thing that can call it. That makes
"mid-life resource disposal racing queued async work" structurally impossible on this backend rather than merely
conventionally avoided. The engine's own `WaitForIdle` calls stay where they are, because they are the seam's
contract and the Veldrid leg still needs them.

## The memory allocator, and the condition attached to declining VMA

**An engine-owned block suballocator.** Chunks of 64 MiB, one `vkAllocateMemory` each, pooled by
`(memoryTypeIndex, linear|optimal)`. First-fit over a sorted free list with alignment correction, split on
allocate, merge with both neighbours on free, one short lock around allocate and free because allocation is not
on the hot path. A request at or above 16 MiB, or one the driver says it prefers or requires a dedicated
allocation for, gets its own `vkAllocateMemory` outside the pools.

**Linear and optimal never share a chunk, and that is the whole `bufferImageGranularity` implementation.**
Buffers and optimal-tiling images may not share a granularity page. The incumbent rounds every non-dedicated
request up to a multiple of that granularity and shares chunks, which is correct and wasteful, and its rounding
adds a granule even when the size is already aligned. Separating the pools by tiling makes the constraint
structural, so there is no granularity arithmetic anywhere in this package and none to get wrong.

**Host-visible chunks are `vkMapMemory`'d once at creation and never unmapped.** Every host-visible allocation
therefore has a stable pointer for its chunk's life, with no map call on any path. This is the thing Direct3D 11
could not do and had to emulate with a record-phase map, and it is what makes the uniform ring
([#518](https://github.com/APKiwiOrg/KhaozEngine/issues/518)) strictly simpler here while running the same
policy. Anyone porting the D3D11 ring's map-and-unmap dance across is porting a workaround for a restriction
Vulkan does not have, and the native seam has no unmap member so the alternative is not expressible.

**Coherent memory is preferred everywhere and cached is preferred for readback.** The incumbent has no
`vkFlushMappedMemoryRanges` and no `vkInvalidateMappedMemoryRanges` anywhere and rests entirely on a
`HOST_COHERENT` type existing. Here both are real: a coherent chunk skips them entirely, and a cached
non-coherent chunk, which is what readback staging deliberately prefers, emits them with the range widened to
`nonCoherentAtomSize`. Widening is why every suballocation in such a chunk is also aligned and sized to that
atom: a widened invalidate that reached a neighbour would discard the host's cached view of writes that
neighbour has not flushed.

**The uniform ring is the one place coherence is a requirement rather than a preference.** It asks for a
host-visible `HOST_COHERENT` type and nothing else, with no fallback rung, and `IsSupported()` already answers
false on a device reporting none. The spec requires such a type to exist, so this fails loudly on a device that
cannot happen rather than gating one that can.

**A chunk's memory goes back behind the timeline.** When the last allocation in a chunk is freed the chunk is
retired rather than destroyed, and its `vkFreeMemory` runs only once the completion timeline has passed the
value recorded at that moment. A pool keeps its last chunk, so a load-unload cycle does not become one
`vkAllocateMemory` per iteration.

**VMA is declined, and the decline is CONDITIONAL.** VMA is a C++ library with no maintained managed binding, so
the real proposal is a native binary per RID in a backend whose premise is reducing native surface. The workload
has no allocation problem to solve: meshes and textures allocate at load, uniform rings allocate once at
creation, and the steady-state frame allocates nothing. The counterargument owed is that hand-rolled allocators
are where memory corruption lives and the failure mode is an aliasing bug no test on a software rasterizer will
show. The answer is this code's readability and device-free testability plus the synchronisation-validation job
of [#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529), which is the only instrument in the net that
sees aliasing and hazard errors. **That linkage is a decision rather than a remark: if the sync-validation gate
is ever dropped, the VMA decline must be re-argued.** The live and lifetime `vkAllocateMemory` counts the
allocator keeps are what measurement gate MV6 is settled on, and they are deliberately not on
`GpuDeviceCounters`, which has no field for them and which the other backends would have nothing to put in.

## Per-frame memory: the uniform ring

**Every `UniformBuffer`-usage buffer is ONE `VkBuffer` of `stride * FramesInFlight`**, in host-visible coherent
persistently mapped memory, where `stride = align(size, max(256, minUniformBufferOffsetAlignment))` and
`FramesInFlight` is 3. The `IGpuBuffer` identity never changes and the per-frame base is applied at BIND, as the
dynamic uniform descriptor's `pDynamicOffsets` entry, which is what keeps a resource set's pinned
`GpuBufferRange` valid across all 68 sites that build one at load time.

**A record-time `UpdateBuffer` on a uniform buffer is a `memcpy` and NOTHING else**: no staging buffer, no
`vkCmdCopyBuffer`, no memory barrier and no render-pass split. That is the whole of what the ring buys, and the
obvious reading of why it is not worth much here is wrong. On the shipped incumbent the same call takes a
staging buffer from a per-list pool, memcpys into it, and calls a copy path whose FIRST statement ends the
active render pass. Ending it transitions the attachments and emits a full pipeline flush. Then the copy. Then a
GLOBAL `VkMemoryBarrier`. Then the next draw lazily re-begins the pass. So a record-time uniform write on the
incumbent is a render-pass split plus a pipeline flush plus a global barrier, not a memcpy. And that barrier's
destination is `VertexAttributeRead` at `VertexInput`, so it does not cover a uniform read at all: the write is
both expensive AND under-synchronised for the usage every per-frame uniform buffer in the engine has.

**On Vulkan the segments are a REQUIREMENT rather than an optimisation.** Direct3D 11's `MAP_WRITE_DISCARD`
gives the driver licence to rename the buffer under a write. Vulkan renames nothing, so writing bytes the GPU
may still be reading from a previous frame's submission is a plain data race with no diagnostic. Nothing needs
to replace `MAP_WRITE_NO_OVERWRITE` either: that dance worked around an API restriction Vulkan does not have.
What makes the writes visible with no flush is that the memory is `HOST_COHERENT` by requirement and that
`vkQueueSubmit` performs an implicit host-write availability operation for coherent memory. The only invariant
left is the fence gate below.

**A segment is recycled against a COMPLETION value, never a submit receipt.** Frame N writes segment
`N % FramesInFlight`, and before handing that segment out the allocator waits until the timeline's counter has
reached the value that segment's frame was closed at, counting the wait as backpressure. The owner value is the
timeline's REGISTERED submit high-water read under the submit lock, which is exact because a submission
allocates and registers its value inside that same lock. It is deliberately not the ALLOCATION high-water: a
submit that failed with a non-loss result took a value nothing will ever signal, and gating a segment on it
would block that segment for good. The deferred-disposal retire list gates on the allocation high-water instead,
for the opposite reason.

**The descriptor's range is the BIND WINDOW, and it is never `VK_WHOLE_SIZE` and never the stride.**
`VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979` requires the effective offset plus the range to stay inside
the buffer, and the effective offset here is `frameBase + rangeOffset + callerDynamicOffset`. At the last frame
slot `frameBase` is `(FramesInFlight - 1) * stride`, so a range of the STRIDE overruns the buffer by exactly the
caller's own offset the moment that offset is non-zero, which it is in five shipped renderers. The invariant the
ring owes is therefore `rangeOffset + callerDynamicOffset + range <= stride`, asserted device-free over every
shipped resource-set shape. It is the same invariant an unringed buffer already obeys: the ring adds `frameBase`
to the offset and `stride` to the ceiling and leaves the arithmetic otherwise untouched.

**BACKEND-DIVERGENT CREATION FAILURE: a uniform buffer combined with any other bindable usage throws here.**
`UniformBuffer | StructuredBufferReadOnly` (or either read-write structured bit, or the vertex, index or
indirect bits) is legal on the seam and is ACCEPTED by `GpuBackendKind.Vulkan`, the Veldrid leg. It is refused at
creation on this backend, because only the dynamic uniform descriptor carries the ring's per-frame base: a
vertex bind, an index bind, an indirect argument read and a storage descriptor all address byte zero, so they
would read the first segment while the uniform bind read the current one. Nothing about that is an error at run
time, it is one frame's data being read as another's. The combination is vacuous in the engine today, and the
divergence is written down here rather than left for a consumer to meet as a surprise. Create two buffers.

**A DEVICE-LEVEL `UpdateBuffer` WRITES EVERY SEGMENT, A RECORD-TIME ONE WRITES THE CURRENT SEGMENT.** That split
is adopted wholesale from [#484](https://github.com/APKiwiOrg/KhaozEngine/issues/484) rather than re-derived,
because it cost a consumer defect to learn once on the other backend: a load-time write that reached the current
segment alone held only until the frame index wrapped, so two frames out of every three bound memory nothing had
ever written, intermittently, with nothing thrown and nothing logged. The off-timeline write reaches all
`FramesInFlight` segments, so a value written once persists for the buffer's life exactly as it does on the
Veldrid leg where the buffer has one copy.

**And that write NEVER BLOCKS.** A segment an earlier frame is still reading does not receive the copy: the byte
range and a private copy of the data are queued as a PENDING PATCH, and the frame boundary that next opens that
segment applies them right after the gate has proved the GPU is done with it. So the call returns immediately,
on every thread, at any pipeline depth, which is what makes it legal from a caller already holding the submit
lock. Waiting instead does not merely cost time, it does not terminate: a loop waiting for every non-current
segment to be free AT ONCE is unsatisfiable in the GPU-bound steady state, because the frame thread submits
again for every frame the GPU retires. The deferral ledger is reported through `GpuDeviceCounters`'
`OffTimelineDeferred` and `OffTimelineOutstanding`, separately from the backpressure count, because a deferred
patch is not a stall at all.

**Bulk payloads take a per-list staging arena instead, and the render-pass split is theirs.** A record-time
write to a NON-uniform buffer, and a texture upload, sub-allocate out of a host-visible persistently mapped
arena, record a copy, and take a barrier narrowed to the destination's actual usage rather than the incumbent's
one global `VertexAttributeRead` guess. Staging blocks are pooled by power-of-two size class with a real
retention cap of 8 MiB. The incumbent destroys any returned staging buffer over 512 bytes, so every real-sized
upload creates and destroys a `VkBuffer` AND a device memory block per call, and raising that to a real cap is
removing an allocation storm from every load rather than an optimisation. The arena recycles PER SLOT, in the
window where `Begin` has already waited for the slot it is advancing onto, because the blocks the previous
record filled belong to a submission that may still be in flight.

**Where measurement gate MV1's NATIVE reading comes from, stated because no counter here answers it.** MV1 bets
that the ring is worth as much on Vulkan as on Direct3D 11, and its magnitude is unmeasured because nobody has
counted how many record-time `UpdateBuffer` calls per frame the [#410] scene makes on the INCUMBENT. That first
half is an incumbent measurement and is not this backend's code at all. The native half is three counters, and
each already has a home that is not a new field on the ring:

- **Record-time `UpdateBuffer` calls per frame** are counted by the CALLER, on the renderer side, and are the
  same number on both legs by construction, because the call sites are shared engine code rather than backend
  code. The ring's own write path counts NOTHING, deliberately: it is a memcpy on the hot path this design
  exists to make cheap, and a counter on it would be the one piece of per-write work the ring removed.
- **Render-pass begins per frame** come off the dynamic-rendering row's own `vkCmdBeginRendering` accounting
  ([#522](https://github.com/APKiwiOrg/KhaozEngine/issues/522)). The exit criterion is that this lands at or
  below the framebuffer-change count, which is a statement about that row's deferred begin rather than about the
  ring, and the ring's contribution to it is a NEGATIVE one: it removes the pass splits the incumbent's uniform
  writes cause.
- **Record-time global barriers** are countable through `IVkCmdSink`'s barrier class, which is already the
  budget seam. The exit criterion is ZERO, and the ring is why: a uniform write records no barrier at all, and
  the staging arena's barriers are per-buffer rather than global and are not on the per-draw path.

No speculative counter is added here for any of the three. **And if the incumbent's counts turn out near zero
already, the ring is still taken because it is the only correct design on this API, and the bet is RECORDED AS
NOT PAYING rather than quietly forgotten.**

[#410]: https://github.com/APKiwiOrg/KhaozEngine/issues/410

**The ring's SEMANTIC tests are shared with the Direct3D 11 backend.** Neither ring's code is shared, on the
rule of three and because the policy is identical where the mechanism is not, but section 9.4 of the design
writes the policy out as a ten-row inventory and seven of those rows run against BOTH backends' rings through
one internal test-only interface in `KhaozEngine.TestSupport.Gpu`. The other three (ordering, lock legality and
the stride arithmetic) are each backend's own, because their mechanisms differ where the policy does not.

## Recording: a pool per slot, and no op stream at all

**There is NO op stream, and that is a DECISION rather than an omission.** `VulkanCommandList` calls `vkCmd*` at
RECORD TIME into a `VkCommandBuffer`. There is no second driver, no `KE_VULKAN_RECORD` and no A/B, and phase 2's
own section 16 predicted exactly this: the CPU op stream in
[KhaozEngine.Gpu.D3D11](../KhaozEngine.Gpu.D3D11) is a Direct3D 11 adapter that exists because that API's
immediate context has no usable deferred recording. A `VkCommandBuffer` between `vkBeginCommandBuffer` and
`vkEndCommandBuffer` IS an engine-invisible op stream that the driver encodes into its own format, so recording
into a managed array first would encode twice, allocate once more, and move the driver-side encode inside the
submit lock, which is the one serialised point in the frame. **The largest unproven bet in phase 2 is simply
absent here.** Do not port the stream across as a "missing" feature.

**Each list owns `FramesInFlight` `VkCommandPool`s with one primary buffer each, reset with
`vkResetCommandPool`.** Not one pool with `VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT`, which is what the
incumbent creates: that flag tells the driver every buffer must be individually resettable and pushes it onto
the slower per-buffer allocator, while resetting the whole pool is the documented fast path and returns the last
record's memory to the pool's arena in one operation. The cost is three pool objects per list instead of one.
The flag is not merely unused, it is unreachable: the package's internal command seam has no parameter through
which it could be asked for.

`Begin` advances to the next slot, waits on that slot's recorded timeline value, resets the pool and begins the
buffer with `ONE_TIME_SUBMIT`. `End` calls `vkEndCommandBuffer` and seals. `Submit` is the device's, and records
the value it signalled back into the slot the record came from.

**The DEPTH is shared with the uniform ring and the INDEX is not, and conflating them is the mistake available
here.** The pool slot is PER LIST and advances on every `Begin`. The ring segment is PER FRAME and advances at
the frame boundary. A list begun twice in one frame therefore takes two different pool slots while both of its
records write the SAME ring segment, which is correct in both directions: two records must not share a command
buffer that is still in flight, and two records in one frame must see one frame's uniform values. A list begun
more times per frame than `FramesInFlight` wraps onto its own oldest slot and waits on that slot's recorded
value, which is real backpressure and is counted as such.

**N lists record CONCURRENTLY on this backend, and the portable seam contract is unchanged.** `IGpuCommandList`
documents exactly one open recording per device, and that is what portable code is written against. This backend
is more permissive as a BACKEND PROPERTY: a `VkCommandPool` and every buffer allocated from it are externally
synchronised one thread at a time, per-list pools mean two lists on two threads never touch the same pool, and
layout tracking is list-local ([#524](https://github.com/APKiwiOrg/KhaozEngine/issues/524)), so nothing shared is
read or written during recording at all. That is the property the Direct3D 11 stream buys by touching no device
state, obtained here from the API's own threading model plus the barrier design. It holds for a reason a reader
has to know rather than one they can see, which is why it is written down twice. One list is still ONE thread at
a time.

**A list disposed with submissions outstanding hands its pools to the retire list** at the highest timeline value
any of its slots was submitted at, and they are destroyed once the counter passes it. The incumbent uses a
refcount, which also works and which this design does not need because the retire list exists for resources
anyway.

**One `vkQueueSubmit` per submission, with the timeline value allocated inside the lock that orders it.** The
value the submission signals rides in the `VkTimelineSemaphoreSubmitInfo` chained onto the submit info, and there
is no `VkFence` anywhere in this backend. Allocating inside the lock is load-bearing rather than tidy: a timeline
semaphore's signals must strictly increase, and allocating outside it would let two threads take 5 and 6 and then
reach the queue in the other order. **A submit that FAILS with a non-loss result registers nothing**, so the
value it took becomes a hole nothing waits on and `WaitForIdle` keeps a target the GPU can still reach. The
failure is still thrown. Host-signalling the taken value to close the hole was weighed and declined: a host
signal has to respect the same strictly-increasing rule against signals still pending on the queue, so doing it
correctly means blocking inside the submit lock on the path where the machine is out of memory.

**What the recording members do TODAY is refuse by naming their own row.** The lifecycle is complete and the
content is not: binds are [#521](https://github.com/APKiwiOrg/KhaozEngine/issues/521), the rendering and clear
path is [#522](https://github.com/APKiwiOrg/KhaozEngine/issues/522), barriers are
[#524](https://github.com/APKiwiOrg/KhaozEngine/issues/524), and draws, dispatches and copies are
[#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525).

**`IVkCmdSink` is the seam a device-free native-call BUDGET is counted through**, struct-constrained so the JIT
monomorphizes it away, covering only the three call classes that scale with draw count: descriptor binds, draws
and dispatches, and barriers. Everything else goes straight to `vkCmd*`. Aiming it at Direct3D 11's call classes
would have been the mistake: that API binds RESOURCES and its fan-out defect was one call per resource per stage,
while Vulkan binds SETS and the Vulkan fan-out class is per-draw descriptor set ALLOCATION, per-draw
`vkUpdateDescriptorSets` and per-draw barrier emission. Neither of those two calls is a member of the sink and
neither should become one: their absence is enforced structurally by the descriptor pool being unreachable from
the recording type, and a call that cannot be made is a stronger guarantee than a call counted and found to be
zero.

## Resources: eager views, resting layouts, a setup buffer that never submits, and the staging layout

**Every `VkImageView` is created at RESOURCE creation and none at a bind or a draw.** A full-chain sampled view
when the texture is sampled or generates mips, an attachment view at mip 0 layer 0 when it is a render target or
a depth target, and a storage view at mip 0 when it is a storage image. The bound is real rather than optimistic
because the seam cannot express anything else: `CreateFramebuffer` carries no mip or layer parameter,
`ResolveTexture` is subresource 0 only, and per-face cubemap rendering is not expressible. Widening any of those
is a seam change, and a seam change is where the extra view would be added.

It is worth restating in a Vulkan seat, where `vkCreateImageView` looks cheap enough to do at a bind: all 25
`DEVICE_REMOVED` stacks in [#423](https://github.com/APKiwiOrg/KhaozEngine/issues/423) surfaced inside a LAZY
VIEW CONSTRUCTOR on the draw path, so lazy creation put an allocation on the hot path and put it on the exact
path a broken device makes fail. The enforcement is STRUCTURAL rather than a counter, and it has to be: neither
`vkCreateImageView` nor `vkAllocateDescriptorSets` is a bind, a draw or a barrier, so the budget sink cannot see
either. `VulkanRecordingUnreachabilityTests` walks the type graph from `VulkanCommandList` and asserts it reaches
no view factory. The descriptor pool is on the same list, for the same reason.

**Every texture is assigned a canonical RESTING LAYOUT at creation** from its usage bits:
`SHADER_READ_ONLY_OPTIMAL` if sampled, else `GENERAL` if storage, else its attachment layout. Sampled wins
outright, so a post-chain intermediate that is both a render target and sampled rests as sampled and a list that
renders into it transitions and restores. It is a property of the RESOURCE rather than of a recording, which is
what makes lists composable in any submit order.

**No texture creation submits anything to the queue.** The incumbent's texture constructor clears render targets
and transitions sampled textures, and each of those grabs a shared pool, records one command and issues a WHOLE
`vkQueueSubmit`: two hundred textures is two hundred submissions before a frame is drawn. Here both are appended
to ONE device-owned setup command buffer, flushed lazily at the next submit **or at any device-level read**
(`Map`, a readback, `WaitForIdle`). The read-path half is what removes the hole rather than moving it: a render
target created and immediately read back must still see cleared contents. The clear itself is preserved
deliberately, because undefined contents are not stable across runs and the goldens require stability on the same
rasterizer.

**That buffer takes its own short lock, the third one.** A `VkCommandPool` and every buffer allocated from it are
externally synchronised, so two threads creating two textures may not append to one setup buffer at once.
Creation stays free-threaded everywhere else and takes the SETUP lock for the append and for the flush, held for
the record of one or two commands. The flush takes the SUBMIT lock **under** it, in that order and never the
reverse, and the one path that touches both (a device `Submit`, which flushes the setup buffer and then queues
the frame's list) takes them sequentially rather than nested. `VulkanSetupBufferTests` pins the nesting by
asserting from inside the submit that the setup lock is held.

**A staging texture is a `VkBuffer` and its subresource layout is computed in SOFTWARE, reproduced from the
incumbent byte for byte.** This is the highest-risk parity surface in the backend. Every golden reads back
through `IGpuDevice.Map(staging, ...)` and consumes `MappedData.RowPitch`, so a different arithmetic garbles all
36 at once and does it silently. `VulkanStagingLayoutTableTests` carries a checked-in table of 63 rows across
formats, sizes, mip levels and array layers, produced by a throwaway generator that transcribed the incumbent's
nine functions independently of the code under test, with each formula's source line cited. Two numbers in it
look wrong and are not: `D32FloatS8UInt` is FIVE bytes per texel in that layout, and the `arrayPitch` field is
set equal to the `depthPitch` rather than to the distance between array layers.

**`Map(staging, Read)` waits on the timeline's last submitted value before returning the pointer**, counted as a
drain. Direct3D 11's `Map(READ)` blocks by definition, so this is where Vulkan has to be explicit about something
the other API did implicitly. A WRITE map does not wait, matching the incumbent. There is no `vkMapMemory` and no
`vkUnmapMemory` anywhere on this path: host-visible chunks are mapped once at chunk creation and never unmapped,
so a map is a pointer plus an offset and an unmap is bookkeeping plus, on a non-coherent memory type, a flush.

**Disposal is one TERMINAL retire per resource.** The single held entry destroys a texture's views inline, then
its image, then its memory, and never re-retires a child. A destroy that retired another destroy that then freed
an allocation would append a third generation of retirement after the teardown drain had taken its snapshot, and
that chunk would never be freed. Destroying children inline bounds the depth at the one generation the device's
two teardown drains already cover. The staging source obeys the same rule from the other side: its `Destroy`
defers the native free through the retire list rather than making it, because the staging arena's own disposal is
ungated, and it ABANDONS rather than frees on a dead device.

**Four departures from the incumbent, all of them its defects.** An image is created with
`VK_IMAGE_LAYOUT_UNDEFINED` rather than `PREINITIALIZED`, which describes a host-written linear image. The
memory-requirements call is the `2` form unconditionally, because this backend requires Vulkan 1.3 where it and
`VkMemoryDedicatedRequirements` are core. An out-of-range sample count is refused rather than rounded down. And
`GpuPixelFormat.R16G16Float` maps to `VK_FORMAT_R16G16_SFLOAT`: the incumbent maps it to the FOUR-channel
`VK_FORMAT_R16G16B16A16_SFLOAT`, which is invisible there because the only texture using that format is the
distortion offset target and it is written and sampled through red and green alone, and would not be invisible
here because the reproduced staging arithmetic sizes that format at four bytes per texel.

**And one more consumer-visible divergence beside the ring's.** `GpuBufferUsage.Dynamic` does NOT make a buffer
CPU-mappable here, where it does on the Veldrid leg. The only dynamic buffers this engine creates are uniform
buffers, which are ring-backed and host-visible for a better reason, so a dynamic vertex buffer lives in
device-local memory and is written through the staging path like any other. Read back by copying into a
`GpuBufferUsage.Staging` buffer, which is what `GpuReadback.ReadBuffer` already does.

## Descriptors: shared layouts, honest pools, and a range that is the bind window

**The seam was designed against a Vulkan-shaped API and it shows.** `IGpuResourceLayout` IS a
`VkDescriptorSetLayout`, `IGpuResourceSet` IS a `VkDescriptorSet` allocated and written at creation, the
pipeline's layout array IS a `VkPipelineLayout`, `SetGraphicsResourceSet(slot, set)` IS
`vkCmdBindDescriptorSets(firstSet: slot, ...)`, and `GpuResourceLayoutElement.Dynamic` IS the dynamic uniform
buffer. Binding index equals element index, `descriptorCount` is always 1, sampled images bind
`SHADER_READ_ONLY_OPTIMAL` and storage images bind `GENERAL`, and `SAMPLED_IMAGE` and `SAMPLER` are separate and
never `COMBINED_IMAGE_SAMPLER`, which the shared GLSL sources already assume by declaring `texture2D` and
`sampler` separately. Structured read-only and read-write both map to `STORAGE_BUFFER`. **The write-once
immutable set is a PORT rather than an invention** and the incumbent already does it. What is new is the
enforcement below, and that it now holds by construction.

**`VkDescriptorSetLayout` and `VkPipelineLayout` are content-deduplicated, and that is load-bearing.**
Identity-shared set layouts are what make bound descriptors SURVIVE a pipeline switch: Vulkan decides
pipeline-layout compatibility by comparing set layouts slot by slot, so one handle per distinct CONTENT turns
that into a pointer compare that always answers correctly, which is exactly what the bind-flush row computes its
compatible prefix with. The incumbent creates one per `ResourceLayout` object with no dedup, so nothing there is
ever compatible with anything and every switch forces a full rebind of every set.

The key is exactly what `vkCreateDescriptorSetLayout` reads, in order: binding number, descriptor type,
descriptor count, stage flags. **Two omissions are deliberate.** Element NAMES are not in it, because Vulkan
binds by number and splitting on names would make a genuinely compatible pipeline pair compare as incompatible
for the rest of the run. And `GpuResourceLayoutElement.Dynamic` is not in it either: the dynamic-ness the key
carries is the DESCRIPTOR TYPE's, which is the only kind the create-info has. Layouts share a handle and never
destroy one, so `IGpuResourceLayout.Dispose` releases nothing and the caches retire every handle at device
teardown.

**Every uniform buffer binds as `UNIFORM_BUFFER_DYNAMIC`, not only the element the layout declared dynamic.**
The per-frame ring base has to be applied at bind and the dynamic offset is the only bind-time knob Vulkan
offers on a uniform buffer, so the type comes from the KIND alone and the declared flag decides exactly one
thing: whether the caller's own per-draw offset is added on top for that element. A declared-dynamic element
that is not a uniform buffer is REFUSED at layout creation, one case wider than the Direct3D 11 backend's
identical refusal for structured buffers, because a texture or a sampler has no dynamic form at all and either
would leave the positional `pDynamicOffsets` array misaligned against the set's real dynamic descriptors.
Vacuous in the engine today: all six shipped dynamic elements are uniform buffers.

**Pools are sized from actual demand and freeing restores EVERY counted type.** The incumbent creates every pool
with `maxSets = 1000` and 100 descriptors of each of seven types, whose per-type ceiling is reached long before
its set ceiling, and its free path restores five of the seven it spends: it forgets `UniformBufferDynamicCount`
and `StorageBufferDynamicCount`, both of which its own allocate does spend. An application that churns
dynamic-offset resource sets leaks pool budget until a fresh pool spawns, and every fresh pool leaks the same
way. This engine's sets are overwhelmingly dynamic-offset ones, the map editor churns them on every document
load, and the rule above makes far more descriptors dynamic here than there, so the leak is aimed squarely at
this consumer. Here take and restore are one pair of methods over one value with structural equality, so there
is no second list of field names to fall out of step, and a churn test allocates and frees in a loop and asserts
the pool count does not grow. A new pool holds as many sets as the most that have ever been live at once
(floored at 8, capped at 1024) and for each type that many sets of the heaviest single shape seen so far, never
below the request that just failed. Freeing is deferred behind the completion timeline like every other resource
destroy, because a descriptor set freed under a submission that binds it is undefined behaviour of the quiet
kind.

**The range is the BIND WINDOW.** `GpuBufferRange.Size` where the set was created from a range, the buffer's own
logical size where it was created from a bare buffer. **Never `VK_WHOLE_SIZE`**, because a whole-size range
combined with a dynamic offset addresses past the end of the buffer. **And never the stride**, which is the shape
that looks safe and is not: at the last frame slot a range of `stride` overruns the buffer by exactly the
caller's own offset, and five shipped renderers pass a non-zero one, so it violates
`VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979` on one frame in three rather than on every frame. A dynamic
uniform descriptor is written with `offset = 0` and its window offset travels at bind time, because Vulkan ADDS
the two. A non-dynamic buffer has no bind-time term, so its window offset goes into the descriptor. Every
`GpuBufferRange` is resolved at set creation and none at a draw.

**The limit this spends has four defences.** `maxDescriptorSetUniformBuffersDynamic` has a Vulkan required
minimum of 8 across a whole pipeline layout, and required minimums are never lowered across core versions.
Beyond that floor nothing about real device values is verifiable from this repository, so no claim is made here
about what lavapipe, NVIDIA or AMD report. In the order they fire: only `UniformBuffer`-usage buffers are
ring-backed, so a storage buffer never becomes dynamic. A device-free test computes the count for all 33 shipped
`CreateResourceLayout` sites grouped into all 33 shipped pipelines and asserts every one is at or under 8, so a
breaking combination fails on the free Linux leg rather than on a player's machine (the heaviest shipped
pipeline spends exactly ONE, which is the engine's own one-uniform-buffer-per-pipeline convention arriving as
seven descriptors of headroom). Pipeline-layout creation counts them and refuses above the device's actual limit
by name. And `IsSupported()` reads the limit, so a machine below what the engine needs falls back rather than
throwing partway into a run.

**Zero descriptor allocations and zero descriptor writes during recording, enforced structurally.** Neither
`vkAllocateDescriptorSets` nor `vkUpdateDescriptorSets` is a bind, a draw or a barrier, so the native-call budget
sink cannot see either and no counting seam ever will. The guarantee is instead that a recorder's field graph
cannot reach the descriptor pool or its seam at all, asserted by `VulkanRecordingUnreachabilityTests` over the
type graph alongside the image-view claim, plus a zero-count assertion against a fake pool with every shipped
layout shape built into a real set first. The subsystem therefore gets its OWN owner record rather than hanging
off the resource owner, because a recorder legitimately reaches that one through the staging block lifetime edge
and a pool behind it would be invisible to the walk.

**Descriptor indexing, bindless, push constants and descriptor buffers are declined, and the decline is argued
rather than omitted** because descriptor indexing is core in 1.2 and the CI rasterizer clears it. There is no
consumer: bindless exists to remove per-material set switching from renderers binding hundreds of distinct
material sets per frame, and this engine's per-frame traffic is dominated by offsets-only rebinds of ONE set,
which already cost one call each. Every route to it changes the SHARED GLSL, which puts all three backends'
pixels in play at once and weakens the byte-identical-SPIR-V parity claim the whole golden gate rests on. Push
constants additionally have no seam concept, so using them means inventing seam API with one backend behind it
or silently promoting some uniform buffer and diverging from what the other two do. Their absence is also what
keeps the pipeline-layout compatibility computation a pure set-layout prefix compare. **The trigger that reopens
it is named**: a consumer needing per-draw material variety beyond one dynamic offset, which today means a
texture-array atlas the splat terrain cannot express.

## Binding: two states, one call per contiguous run, and a compatibility prefix with a guard

**A resource-set bind RECORDS ONLY.** `SetGraphicsResourceSet` and `SetComputeResourceSet`, both overloads of
each, write into a per-slot array of `(set, engineDynamicOffset)` and issue nothing. A slot goes dirty when
either the set or the offset differs from what is already recorded, several marks between two draws collapse to
one flush, and a rebind that changes nothing leaves the slot clean. The record is one struct per SLOT, replaced
in place, so its size follows the highest slot ever used and never the number of rebinds: the shadow pass does
thousands of offsets-only rebinds of one set per frame and an O(rebinds) record would make that an O(n squared)
frame. `Draw`, `DrawIndexed` and `Dispatch` flush every dirty slot through a pre-command hook and then issue.

**TWO STATES, NOT THREE, AND THE THIRD IS NOT MISSING.** The Direct3D 11 backend carries a `DynamicOffsetsOnly`
state so an offsets-only rebind can push the constant buffers and skip the textures and samplers, which on that
API is a real saving in native calls. A Vulkan descriptor bind is ONE call whether one offset moved or every
image in the set changed, `pDynamicOffsets` is positional over every dynamic descriptor in the run, and every
ring-backed uniform's base moves every frame, so the array is recomposed on any bind regardless. A third state
would change no call and skip no work. The "was there an offset overload" flag the other backend keeps is gone
for the same reason: with no second activation path to choose, a bind with an offset of zero and a bind without
one are the same call. The one thing that distinction still buys is a refusal, so a NON-ZERO offset passed to a
set whose layout declares no dynamic element is rejected by name rather than silently dropped.

**One `vkCmdBindDescriptorSets` per CONTIGUOUS RUN of dirty slots**, with `firstSet` at the run's start. A full
activation of the engine's shapes is ONE call carrying every set and an offsets-only rebind of one set is ONE
call carrying one. A clean slot cuts the run because rebinding it would be a call bought for nothing, and a slot
whose recorded set has gone null cuts it because there is no handle to name: neither can be a hole in the middle
of an array that starts at one index. A null slot is SKIPPED rather than unbound, and it goes clean on the way
past so the skip happens once. `SpriteBatch` puts its uniform buffer at SET 1, so "set 0 first" is false in
shipped code and a run's own `firstSet` is load-bearing rather than decorative.

**`pDynamicOffsets` is POSITIONAL and covers sets the caller never named.** One entry for every dynamic
descriptor in every set of the run, in SET ORDER then BINDING ORDER, including ring bases for uniform buffers
nobody passed an offset for. Each entry is `ringBase(buffer, currentFrame) + rangeOffset + (declaredDynamic ?
engineOffset : 0)`, and the declared flag is the only thing that decides that last term. There is no key and no
name anywhere in the array: position is the only thing that says which entry belongs to which descriptor, which
is why an off-by-one here reads the wrong slice of the RIGHT buffer and renders plausible garbage rather than
throwing. A device-free test composes it for all 33 shipped layout shapes at every frame slot and asserts each
entry plus the descriptor's own range stays inside the buffer, which is
`VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979` measured against the range the set wrote at creation and the
stride the ring owns. Those three have to agree or validation fails on the LAST FRAME SLOT ONLY, which is the
hardest version of this bug to find, so the composition re-asserts the bind-window invariant with the caller's
real offset rather than the zero set creation had to assume.

**An entry that is not a multiple of the dynamic-offset alignment is refused too, and that one is checked rather
than assumed.** `VUID-vkCmdBindDescriptorSets-pDynamicOffsets-01971` requires every entry to be a multiple of
`minUniformBufferOffsetAlignment`, and only the ring base carries that by construction. The `rangeOffset` and
caller terms hold it because every shipped slot size is itself 256-aligned, which is an invariant the renderers
obey rather than anything the ring guarantees, so the composition states it instead of relying on it. The check
is on the COMPOSED entry, which is what the VUID measures: two terms that are each misaligned and sum to an
aligned entry are legal and are accepted. The alignment it is held to is the engine's portable 256-byte floor
rather than this device's own limit, so an offset cannot pass on a lax dev device and fail validation on one
reporting the spec's required maximum. Like the window refusal, it leaves the slot dirty, so it repeats at the
next draw instead of being spent on the first.

**The array is recomposed at the head of every RUN, which is the incumbent's own bug not inherited.** Its
batching flush resets the batch count and the first set but NOT the accumulated dynamic-offset count, so a second
batch inside one flush passes a too-large count built from stale entries. The invariant here is that the count
passed equals the sum of that call's sets' dynamic descriptors, and the device-free budget test asserts it over a
whole frame rather than leaving it to a reading of the code.

**A pipeline switch invalidates recorded slots from the first INCOMPATIBLE set onward, and the rule runs the
opposite way to Direct3D 11's.** There a switch drains the pending sets under the OUTGOING layouts and forgets
the records, because the layout array decides register numbering. Here nothing is renumbered: Vulkan itself
invalidates bound descriptor sets from the first incompatible set, and two pipeline layouts are compatible for
set N when they were created with identically defined set layouts for sets 0 through N and identical
push-constant ranges. Content dedup makes "identically defined" into HANDLE IDENTITY and push constants are
declined, so the computation is the longest common prefix of the two layouts' set-layout handle sequences. A
rebind of the layout already current does nothing. Without dedup this would answer zero every time, which is
exactly what the incumbent pays and what a blunt clear-everything version reproduces by construction rather than
by choice.

**That prefix is GUARDED rather than trusted, and the asymmetry is why.** A prefix shorter than the truth costs a
redundant bind. A prefix LONGER than the truth leaves a set the driver has already invalidated marked clean, so
the next draw reads whatever that descriptor slot now holds, which renders wrong and throws nothing. Two checks:
a device-free test walks all 1089 ordered pairs of the 33 shipped pipelines and asserts the computed prefix never
exceeds the true prefix of identically DEFINED set layouts, computed from the binding tables rather than from the
handles so the guard is not a restatement of the thing it guards. And under `KE_VULKAN_VALIDATION` the flush
additionally asserts that every bound set's layout IS the current pipeline layout's set layout at that index,
which is the half that runs where a draw would consume the answer.

**What is not built yet, and where it lands.** `SetPipeline` and `SetComputePipeline` still refuse, naming the
pipeline row ([#523](https://github.com/APKiwiOrg/KhaozEngine/issues/523)): the prefix computation and both of
its guards are already here, and that row calls `SetPipelineLayout` with the pipeline's own layout handle and
set-layout sequence. The draw and dispatch members still refuse, naming
[#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525), which calls the flush hook FIRST and then issues.

## `KE_VULKAN_DEVICE`, `KE_VULKAN_VALIDATION` and `KE_VULKAN_FRAMES_IN_FLIGHT`

**`KE_VULKAN_DEVICE` pins which physical device the backend runs on.** Six forms, case-insensitive and
whitespace-trimmed:

```
KE_VULKAN_DEVICE=llvmpipe     # the Mesa software rasterizer, by driver id or name. The value CI pins
KE_VULKAN_DEVICE=discrete     # the first discrete GPU
KE_VULKAN_DEVICE=integrated   # the first integrated GPU
KE_VULKAN_DEVICE=cpu          # the first CPU device
KE_VULKAN_DEVICE=1            # a zero-based index into the vkEnumeratePhysicalDevices order
KE_VULKAN_DEVICE=GeForce      # a case-insensitive substring of a device name
```

Unset takes the first device that meets the requirements, which reproduces the incumbent's
`physicalDevices[0]` on every machine where device zero qualifies. **A request that cannot be honoured WARNs and
falls back to that default, and never fails the run**, and the warning lists what was actually enumerated. A
device that cannot run the backend is never chosen even by an explicit index, because honouring the pin would
trade a warning now for a crash on frame one. When the default has to skip past an ineligible device zero, the
INFO line says SUBSTITUTED in as many words, so a soak session can tell a substitution from a selection.

**`KE_VULKAN_VALIDATION` is a four-rung ladder**, `0` (the default) / `1` / `strict` / `sync`:

```
KE_VULKAN_VALIDATION=1        # VK_LAYER_KHRONOS_validation plus a VK_EXT_debug_utils messenger
KE_VULKAN_VALIDATION=strict   # 1, and an error-severity message throws at a controlled point
KE_VULKAN_VALIDATION=sync     # 1, plus synchronisation validation through VkValidationFeaturesEXT
```

Messages are pumped into the engine log at a rate limit, with warning severity at WARN and error severity at
ERROR. The limiter has two caps, and a cap that suppresses says so exactly once rather than going quiet: per
repeated message (the one that does the real work, since validation's characteristic failure is one mistake
reported once per draw call) and per session (the soak backstop). Objects this backend creates are NAMED, so a
message names the device or the queue instead of a bare handle. A machine with no layer installed gets a WARN
naming what to install and a device created without it, rather than an app that refuses to start on somebody who
is mid-diagnosis. An unrecognized value is off plus a warning listing what works, because a session that
believes it is running `strict` and is running nothing produces a clean run that proves nothing.

**The callback LOGS and never throws.** The incumbent's throws a managed exception and calls `Debugger.Break()`
from inside a native driver callback, which is undefined behaviour that destroys the stack the diagnostic was
about. `strict`'s throw is what that behaviour is for, and it happens at a controlled point after the latch.
RenderDoc attaches externally and needs nothing from the engine.

**`KE_VULKAN_FRAMES_IN_FLIGHT=<n>` moves the ONE depth this backend pipelines at** (2 to 16, default 3, an
unparseable or out-of-range value warns and keeps 3). It sizes both rings at once: how many `VkCommandPool`s each
command list cuts, and how many per-frame segments each uniform ring is cut into. One number, because a deeper
command-buffer ring behind a shallower uniform gate is dead capacity, and one number to move if the measurement
says 3 is wrong.

The floor is 2 rather than the Direct3D 11 lever's 1, and that difference is deliberate. There the number sizes
constant-buffer rings only, so 1 is an honest degenerate case: one frame of latency, and the shape that proves
the backpressure counter counts something real. Here 1 would give every list ONE pool, so every `Begin` would
advance onto the slot it just used and wait for that record's own submission to complete: a synchronous round
trip per RECORD, which on a frame recording several lists is several full GPU drains, and a capture taken there
measures the drain rather than the pipeline.

The variable exists to settle measurement gate MV3, whose exit criterion is `BackpressureStallCount` reading zero
across a full capture window AT THE DEFAULT. That counter is ONE accumulator covering both meanings, a command
list wrapping onto its own oldest pool slot and a frame boundary finding its uniform segment still in flight,
because they are the same statement about the same lever. Raising the depth is the response to a non-zero count.
**The knob may outlive its gate only if the exit criterion was met at 3**, which is the condition that stops "it
is only a knob" from becoming a way to keep a failed default.

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

`GpuPublicApiTests.GpuVulkanPublicSurface_IsExactlyTheApprovedMembers` is the fourth, and it catches what none
of the three above can. They ask what the surface exposes and say nothing about how much of it there is, so a
new public member that happens to name no forbidden type is invisible to every one of them. That row pins the
surface member by member at one exported type carrying one method, so widening it is a deliberate edit somebody
had to read the reasoning to make.

## Layering

```
KhaozEngine.Gpu.Vulkan -> KhaozEngine.Gpu                     (the only direction. Gpu never references a backend package)
KhaozEngine.Gpu.Vulkan -> KhaozEngine.Diagnostics             (the probe's one log line, same as the D3D11 instance)
KhaozEngine.Gpu.Vulkan -> Silk.NET.Vulkan(+.Extensions.KHR/.EXT)   (its subject matter)
KhaozEngine.Gpu.Vulkan -> Veldrid*                            (never, asserted two ways)
```

See [docs/DEPENDENCY-SEAMS.md](../docs/DEPENDENCY-SEAMS.md), "Out-of-package graphics backends", for the
inverted-edge pattern this package is the second instance of.
