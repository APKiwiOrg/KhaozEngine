# KhaozEngine.Gpu.Vulkan

The engine's own native Vulkan backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in NO
umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or `WorldStore.Sqlite`,
and nothing that does not want the Vulkan binding ever carries it.

> **Status: BUILT AND CONTINUOUSLY EXERCISED, NOT YET DEFAULT ANYWHERE. Registration, probe, headless and
> windowed devices, the completion timeline, the memory allocator, the command list's lifecycle, the uniform
> ring, the resource factory, the descriptors, the bind flush, dynamic rendering, the shader path, the
> pipelines, the swapchain, the barrier tracker, the capability, counter and diagnostics reads, the draw,
> dispatch and transfer paths, and a blocking CI leg carrying both validation tiers. What remains is the
> rollout.**
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
> landed with it, ahead of the pipeline row that calls them. And since
> [#522](https://github.com/APKiwiOrg/KhaozEngine/issues/522) it has FRAMEBUFFERS and DYNAMIC RENDERING:
> `CreateFramebuffer` makes no driver object at all (there is no `VkRenderPass` and no `VkFramebuffer` anywhere
> in the backend, so no cache for either and no invalidation on resize), `vkCmdBeginRendering` is deferred to the
> first draw so a clear folds into `loadOp = CLEAR` instead of costing a call, a pass that collected clears and
> saw no draw is still flushed through a begin and end pair, and the viewport a framebuffer CHANGE emits carries
> NEGATIVE height so the engine's clip space matches Direct3D's. And since
> [#526](https://github.com/APKiwiOrg/KhaozEngine/issues/526) it has a SHADER PATH: GLSL 450 through the engine's
> own front end to SPIR-V, then `vkCreateShaderModule` over the bytes verbatim, with no cross-compilation
> anywhere and the modules shared by SPIR-V hash. And since
> [#523](https://github.com/APKiwiOrg/KhaozEngine/issues/523) it has PIPELINES, which closes the resource
> factory's refusal list entirely: graphics and compute pipelines built with no `VkRenderPass` at all (the
> target's formats ride a `VkPipelineRenderingCreateInfo` off the seam's own `GpuOutputDescription`, and its
> sample count the multisample state), vertex input taken from the caller's layouts with no reflection read off
> the module,
> dynamic state held to exactly viewport and scissor, and a `VkPipelineCache` persisted to disk whose header is
> validated before the driver ever sees it. And since
> [#527](https://github.com/APKiwiOrg/KhaozEngine/issues/527) `GpuDeviceContext.CreateForWindow` reaches a real
> WINDOWED device: a platform surface chosen from `GpuWindowKind`, candidates filtered on whether their graphics
> family can present to it, `VK_KHR_swapchain` on the device, and a swapchain the boundary acquires from, resizes,
> recreates and presents. And since
> [#524](https://github.com/APKiwiOrg/KhaozEngine/issues/524) it has its BARRIER AND LAYOUT TRACKER: every
> transition is a `vkCmdPipelineBarrier2` whose masks are answered per LAYOUT rather than per layout PAIR,
> tracking is per subresource range and LIST-LOCAL against the resting layout each texture was created with,
> every attachment is transitioned as one batched barrier immediately before `vkCmdBeginRendering`, `End`
> restores everything the recording touched, and a transition out of `UNDEFINED` is refused everywhere except
> the two sites permitted to discard. And since
> [#528](https://github.com/APKiwiOrg/KhaozEngine/issues/528) its CAPABILITY READ is a device-free type held
> against the incumbent's answers with ZERO permitted differences, its `GpuDeviceCounters` fill is checked as
> nine readings rather than an absence, and both `GpuDeviceDiagnostics` fields are asserted all the way into the
> telemetry session header. And since
> [#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525) IT DRAWS: the vertex and index binds, both `Draw`
> overloads, `DrawIndexed`, `Dispatch`, both texture copies, the buffer copy, `GenerateMipmaps` and
> `ResolveTexture` are all live, so `IGpuCommandList` has no refusing member left, `MaxMsaaSampleCount` is the
> incumbent's own computation reproduced rather than a pinned 1, and a windowed run presents a frame the backend
> really rendered. The
> backend IS nameable: `GpuBackendKind.VulkanNative` and the `vulkan-native` / `vk-native` tokens
> landed with [#513](https://github.com/APKiwiOrg/KhaozEngine/issues/513). And since
> [#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529) it has a BLOCKING CI LEG, which is what turns all
> of the above from a claim into a continuous exercise: the `vulkan-native` leg verifies the committed `vulkan`
> goldens on lavapipe as a guest in that family, with `KE_VULKAN_REQUIRED=1` so a row that needs a native device
> cannot go quietly dormant, and both tiers of the validation gate ride it, `strict` on the scheduled full suite
> and `sync` on a separate golden-and-compute job.
> Nothing selects it by default.
> `KhaozEngine.Gpu`'s `Vulkan` backend, which goes through Veldrid, remains the working Vulkan path and stays
> selectable indefinitely, and the FLIP of the Linux default waits on five rollout gates, two of which are a
> field session and a human windowed pass that no CI leg can stand in for.

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

Naming the backend today reaches a real device on both paths, headless and windowed, on any machine whose probe
answers yes. A machine whose probe answers no arrives through the reported fallback rather than as a crash: the
creation path catches, WARNs with the message and boots on the incumbent, reporting
`GpuBackendSource.FallbackAfterFailure`. Nothing selects it for you. The Linux default is still
`GpuBackendKind.Vulkan` and stays there until every rollout gate is green (decision V-RO3), and
`GpuBackendSelector.SupportedBackends()` does not offer the native kind to a player at all, because a settings
screen offers an API rather than an implementation of one.

`VulkanNative` renders the same images as `Vulkan`, so it is a GUEST in the committed `vulkan` golden family
rather than owning one (decision V-I3). That is what holds it to the incumbent's already-committed
reference grids, unmodified, on the same rasterizer at the same tolerance. `KE_UPDATE_GOLDENS` is REFUSED on it
for the same reason: a bake would overwrite the very references it is being checked against, and the file it
wrote would be exactly the file it would then have compared against.

**The `vulkan-native` CI leg is where that guest sits, and it is blocking from its first run**
([#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529)). It runs the golden subset on every push and the
serialized full suite on the weekly schedule and on a dispatch, on lavapipe, with `KE_VULKAN_DEVICE=llvmpipe`
pinned at the device level as the belt to the loader-level ICD pin. Two of its settings are its own rather than
the Direct3D 11 leg's. `KE_VULKAN_REQUIRED=1`, because a row that needs a real native device goes DORMANT when
the probe refuses the machine, and a dormant row is not a skip: on the one leg built to run those rows a loader
regression would empty them into passes that assert nothing, which the zero-skipped rollout criterion cannot
see. And `KE_VULKAN_VALIDATION=strict` on the full suite, with a second job running the golden subset plus the
compute suite under `sync`. That layer is an INSTALL rather than a knob, and before this leg no validation layer
was present on any leg in this repo at all.

**The two tiers fail in two different places, and only the first one fails in the engine.** On `strict` the pump
latches an error-severity message and throws at the next controlled point, so the leg reds by itself. On `sync`
it does not: the rungs are ordered by cost rather than by containment, and `sync` deliberately reports every
hazard and carries on instead of stopping at the first, which is what makes one run's log a complete triage
artifact. So the `sync` JOB gates instead, scanning the suite log it tees for error-severity validation lines
and failing on any, with the log still uploaded on `always()` so the evidence outlives the red. That keeps
engine-side latching strict-only by design while leaving the tier something that can actually go red, which
matters because rollout gate 3 and the VMA decline above are both read off that job being green.

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
arena, record a copy, and bracket it in two barriers narrowed to the destination's actual usage rather than the
incumbent's one global `VertexAttributeRead` guess. Staging blocks are pooled by power-of-two size class with a real
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
documents exactly one open recording per device, that is what portable code is written against, and
`GpuRecording` now refuses a second engine-opened recording on every backend including this one. This backend
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

**The flush-before-read rule is DRIVEN through the device now, at all six of its call sites.** It used to be
carried by inspection, because `VulkanGpuDevice`'s constructor is private and reachable only through
`CreateHeadless` and `CreateForWindow`, both of which need a live loader, so nothing anywhere constructed the
type and `VulkanSetupBufferTests` could only call `Setup.Flush()` itself, which is the subsystem rather than the
device path ([#550](https://github.com/APKiwiOrg/KhaozEngine/issues/550)). `VulkanGpuDevice.CreateOverSeams` is
the internal hook that closes it: a real device over the same fakes every other device-free Vulkan suite uses,
with no instance lease and no disk pipeline cache, which are the only two things a machine with no loader cannot
supply. `VulkanDeviceWiringTests` drives both `Submit` overloads, `WaitForIdle` and both `Map` overloads through
it and reads the order off the TIMELINE VALUES, since every submission takes its value inside the lock that
orders `vkQueueSubmit` and a lower value is therefore an earlier submission. The same rig carries `Map(staging,
Read)`'s drain and, just as importantly, the fact that a WRITE map does not drain: a device that waited on every
map would be indistinguishable from a correct one on a suite that only checked the read path.

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
aligned entry are legal and are accepted. The alignment it is held to is the 256-byte portable floor or this
device's own limit, whichever is larger, so an offset cannot pass on a lax dev device and fail validation on one
reporting the spec's required maximum. Like the window refusal, it leaves the slot dirty, so it repeats at the
next draw instead of being spent on the first.

**The array is recomposed once per RUN, which is the incumbent's own bug not inherited.** Its
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

**Both pipeline binds drive it now** ([#523](https://github.com/APKiwiOrg/KhaozEngine/issues/523)). The prefix
computation and both of its guards landed here one row early, and `SetPipeline` and `SetComputePipeline` call
`SetPipelineLayout` with the pipeline's own layout handle and set-layout sequence. See the pipelines section
below for the identity guard that sits in front of it. The draw and dispatch members still refuse, naming
[#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525), which calls the flush hook FIRST and then issues.

## Rendering: no render pass at all, a deferred begin, and a viewport with negative height

**There is no `VkRenderPass` and no `VkFramebuffer` in this package, and that is the largest structural decision
in the design.** No cache for either, and therefore no invalidation of either on a resize. `CreateFramebuffer`
creates no driver object: every attachment view already exists on the texture, so a framebuffer is an aggregate
of borrowed handles and its disposal releases nothing. `IGpuFramebuffer.Outputs` is already
`VkPipelineRenderingCreateInfo`'s input verbatim. The incumbent's alternative is three render passes per
framebuffer with no cache, no dedup across framebuffers of identical format, one `VkFramebuffer` per swapchain
image, and all of it rebuilt on every resize, so porting it properly would have meant writing a render-pass
cache, a framebuffer cache and an invalidation problem. Dynamic rendering deletes all three. The cost is the
Vulkan 1.3 floor, which the CI rasterizer clears and which an older machine answers false at the functional probe
and routes through the existing reported fallback.

**`vkCmdBeginRendering` is DEFERRED to the first draw.** A clear recorded after `SetFramebuffer` therefore folds
into `loadOp = CLEAR` with its own clear value instead of costing a `vkCmdClearAttachments`. A clear that arrives
after the pass has already opened still issues one, which is what the incumbent does in the same situation.
`storeOp` is `STORE` unconditionally and there is no way to ask for anything else: `DONT_CARE` for depth leaves
contents undefined, undefined is not stable across runs, and the goldens require stability on the same
rasterizer.

**The clear-only pass is reproduced deliberately.** `SetFramebuffer` plus a clear plus `End` with no draw between
them still clears, through a begin and end pair with no draws, flushed at `End` and at the next framebuffer
change. It needs no "did a draw happen" flag: a begin consumes the pending clears, so a clear still pending at
the end of a pass is the proof that no draw came. Every command illegal inside a render pass instance (a
dispatch, a resolve, a copy, a mip generation) ends the pending rendering first, through one helper.

**A bulk `UpdateBuffer` is that helper's live caller today.** A staged write records a `vkCmdCopyBuffer`, which
may not appear inside a render pass instance, so it ends the pass, takes the clear-only flush with it, copies and
barriers, and the next draw begins the pass again. A ring-backed uniform write reaches none of that: it is a
memcpy into the current segment and records nothing at all, which is the whole point of the ring. The scope the
upload path ends through is the command list itself, handed to the list's own staging uploader from the list's
constructor, because the list owns both ends of that cycle and wiring it anywhere else leaves a path that can
forget the call. A forgotten call is silent rather than loud: the scope is nullable, and null reads as "there is
no pass to end".

**`SetFramebuffer` emits a viewport and a scissor ON A CHANGE ONLY, and the viewport's height is NEGATIVE.**
There is no `SetViewport` on the seam: the engine gets a viewport because Veldrid's base
`CommandList.SetFramebuffer` auto-calls `SetFullViewports` and `SetFullScissorRects`, wrapped in an identity
guard, and both halves have to be reproduced. A backend that does not emit rasterises nothing, and one that emits
unconditionally silently restores the full scissor over a live one, which is golden-visible. The negative height
(`y = y + height`, `height = -height`) is what makes Vulkan's clip space match Direct3D's, which is why
`ClipSpaceYInverted` is false here and why `GpuClip.Correct` is the identity: every matrix the engine builds
assumes the flip already happened in the viewport. Getting it wrong does not throw and does not fail to render.
It renders every golden upside down.

**A non-zero scissor index is refused by name.** The seam carries an output index because Veldrid models one
scissor per colour target, nothing in the engine passes a non-zero one, and honouring it would mean enabling
`multiViewport` and matching every pipeline's viewport count to its attachment count for a shape no shipped
renderer has. The native Direct3D 11 backend refuses the same index for the same reason.

**What is not built yet, and where it lands.** The attachment layout transitions a begin owes are the barrier
row's ([#524](https://github.com/APKiwiOrg/KhaozEngine/issues/524)), and the bound framebuffer already carries
each attachment's `VkImage` for it. The pre-draw hook is called by nothing yet, and the
end-before-illegal-command helper is called by the staged upload path above and by the compute pipeline bind:
the draws, dispatches, copies and resolves are
[#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525).

## Shaders: there is no cross-compilation, and that is the headline

**Vulkan consumes SPIR-V, so the whole shader path is three steps.** The engine's GLSL 450 source goes through
the same front end every backend already uses, `vkCreateShaderModule` takes the resulting bytes verbatim, and
the handles are held on the shader set for the pipeline row to name. No HLSL, no FXC, no register numbering to
invent, no `local_size` hand-parse over a cross-compiler's output, no emitted-intermediate hash pin and no
signature workarounds. The Direct3D 11 package's shader section is seventy lines of hazard and this one is
eight, which is not luck: that phase confined the cross-compile edge to one file in `KhaozEngine.Gpu` precisely
so a later backend could take the half it needs.

**So this backend takes the FRONT END only, and that split is the whole Metal-facing carrying cost of the
phase.** `SpirvCrossCompile` was split along the seam it already had: `SpirvFrontEnd` is glslang (GLSL to
SPIR-V) and `SpirvCrossCompile` keeps SPIRV-Cross (SPIR-V to HLSL). This package reaches the front end across
`InternalsVisibleTo` and names no member of the back end at all, asserted over the built IL rather than over the
source text, because a `using` alias would walk past a grep. When Metal arrives it changes the BACK end to add
an MSL target, so the eventual direct-SPIRV-Cross migration
([#462](https://github.com/APKiwiOrg/KhaozEngine/issues/462)) becomes a change to one half of one file with one
consumer family, evaluated against Metal's own fresh goldens rather than against Direct3D 11's committed ones.
**SPIRV-Cross is deliberately not touched in this phase**: swapping it here would put those 36 goldens and both
documented WARP corruption incidents in play at once, for a backend that consumes none of its output, in the
phase whose CI leg cannot see any of it.

**The parity claim is TWO artefacts and neither substitutes for the other.** `SpirvFrontEndPin` states the
front-end options as constants with an identity derived from them, so a pin change moves every derived cache key
by construction. Separately, a ONE-OFF in-process measurement compiled every shipped program through both this
path and a faithful replication of the incumbent's own SPIR-V production and compared the modules byte for byte:
**76 of 76 stages identical, 0 mismatches, taken 2026-08-08 before the first golden run** and recorded in
section 12.1 of the design. THAT is what licenses the committed `vulkan` goldens carrying over unmodified.
`VulkanSpirvByteEqualityTests` is a different thing and its header says so: its table is baked from this path's
own emission, so what it detects is DRIFT, and a wrong emission baked once would pass forever. Reading a green
run there as parity evidence reads it backwards.

**`VkShaderModule`s are deduplicated by SPIR-V hash within a device.** The shipped set is 76 stage emissions and
59 distinct modules, because one fullscreen vertex source backs eleven post-processing programs on its own and
the model, skinned-model, shadow-depth, water and billboard families each share a stage across two. A handle is
therefore shared, so `IGpuShaderSet.Dispose` releases nothing and the cache ends every module in the device's
teardown window, the same rule a shared `VkDescriptorSetLayout` already follows. The key is a hash of the bytes
and nothing else, with no options token in it, because these bytes ARE the emission: two equal SPIR-V modules are
the same module to the driver whatever produced them.

**The `set` and `binding` numbers are INHERITED rather than invented, and asserted anyway.** The shared GLSL
already declares `layout(set = N, binding = M)`, and the backend's whole job is to make N the layout's index in
the pipeline's layout array and M the element's index in that layout. Get it wrong and everything compiles,
every descriptor writes, every draw issues and every pixel is wrong. So a device-free test parses every
declaration out of the real shipped sources, pairs each program with its pipeline's layout array, and asserts
both indices plus the resource KIND at each, plus that no layout element goes undeclared. Names are deliberately
not compared: they disagree in shipped code and Vulkan binds by number. **`SpriteBatch` declares its uniform
block at `set = 1` with its texture and sampler at `set = 0`**, so "the UBO set comes first" is false in shipped
code and this test is the only thing that would catch a layout array reordered by a well-meaning refactor.

**Two things this backend must NOT "fix", both stated because they will be proposed.** The Direct3D 11 backend's
holed-signature sinks stay: SPIRV-Cross drops an unread vertex input and a holed `TEXCOORD` sequence miscompiles
under FXC on WARP, both incidents were tolerated by Metal and Vulkan, and that leg ships indefinitely, so
removing a sink because Vulkan tolerates it corrupts WARP. And the Metal-driven shader-shape invariant stays,
one uniform buffer per pipeline at set 0 binding 0 with per-mesh textures at set 1 and up: Vulkan has no such
limit and a Vulkan-only author would naturally spread uniforms across sets, which breaks a phase-4 backend that
is not here to defend itself. The Metal-only shader validation check is in the same category.

## Pipelines: no render pass, two dynamic states, and a disk cache that validates its own header

**A pipeline create-info here names no `VkRenderPass` and no subpass that means anything.** The target's colour
format array and depth format come straight off the seam's `GpuOutputDescription` as a
`VkPipelineRenderingCreateInfo` chained onto the create info, which under dynamic rendering is the whole of what
a render pass would have carried. That is the same absence the rendering section above is about, seen from the
other end: no pass to cache, nothing to invalidate on a resize, and no "created against pass A, bound inside pass
B" mismatch class at all. The stencil plane is named separately from the depth one, because dynamic rendering
splits them and both of the seam's depth formats are combined ones. The sample count is off the same
`GpuOutputDescription` and lands somewhere else, on
`VkPipelineMultisampleStateCreateInfo.rasterizationSamples`, because the rendering create-info has no
sample-count field.

**Vertex input comes from the caller's own layouts and nothing is reflected off the module.** The Direct3D 11
backend has to reflect its compiled vertex signature, because SPIRV-Cross invents a `TEXCOORD<location>` semantic
per location and drops any input the shader never reads. Here the `GpuVertexLayoutDescription` list IS the input
state, so there is one source of truth. Two rules are worth knowing:

- **Locations count across ALL slots, not within one.** Slot 1's first element continues where slot 0's last one
  left off, because a GLSL `location` is a flat sequence over every vertex input the shader declares and knows
  nothing about which buffer an attribute arrives in. Restarting per slot reads the instance stream's first
  attribute as the vertex buffer's second, which renders plausible garbage rather than throwing.
- **Offsets pack within their own slot, and a declared stride wins.** The seam has no per-element offset, so an
  element sits immediately after the one before it in the same slot. A non-zero `Stride` is kept, which is how an
  interleaved buffer with padding survives, and a zero one is the sum of the slot's element sizes.

**An instance step rate above 1 is refused by name.** Vulkan's core vertex input rate is two-valued with no
divisor, and `VK_EXT_vertex_attribute_divisor` is not enabled here, so a rate of 2 cannot be honoured. Flattening
it to 1 would draw every instance from the same element, silently. Every shipped instance stream declares 1.

**Dynamic state is exactly viewport and scissor, and it is a VALUE rather than a line inside the seam.** A
two-element array a plain `[Fact]` reads, translated verbatim at the driver boundary, because a claim buried in a
`VkPipelineDynamicStateCreateInfo` built under a real driver is a claim no headless test can check. Everything
else is baked into the pipeline object, including the constant blend colour, which is the incumbent's shape kept
deliberately.

**A blend state count that is not the colour output count is refused by name, in both directions.** Vulkan
requires the colour blend state's attachment count to EQUAL the rendering create-info's colour attachment count,
and the seam lets the two differ with nothing checking either way, so a mismatch is a validation error at
creation on a shipped renderer, which is a failure a player sees. Repairing it was the earlier answer and is
worse: padding an undeclared output with a disabled blend that writes every channel invents a per-backend
semantic for a state the caller never gave, and the Direct3D 11 native backend answers the same description with
its own struct defaults, so the two would quietly disagree about the same undeclared attachment. Dropping a
declared state past the last colour output throws away a state the caller wrote and meant.
`GpuPipelineDescription.BlendAttachments` is already documented as one per colour output and every shipped call
site declares exactly that, so enforcing the contract costs nothing and only fires on a description that was
already wrong.

**The `VkPipelineCache` is persisted, and a corrupt file cannot crash a launch.** The incumbent passes
`VkPipelineCache.Null` at both of its creation sites, so every launch recompiles every pipeline from SPIR-V,
across considerably more permutations than programs because everything except viewport and scissor is baked in.
Here one cache is created with the device, seeded from a file under
`<local-app-data>/KhaozEngine/vulkan-pipeline-cache/<engine version>` and written back at teardown. The mechanics
are all defensive:

- **The key is `(pipelineCacheUUID, driverVersion, engine version)`.** The UUID is the vendor's OWN validity key,
  which is the answer to "a disk cache needs one" being a reason to defer. The driver version rides the file NAME
  because no header restates it, so a driver update that keeps its UUID never opens the old file at all.
- **The header is VALIDATED before `pCacheData` is passed**: header size, header version, vendor id, device id
  and the UUID. Any mismatch is a silent discard. The driver is required to check the same things, and "required
  to" is not "every driver on every machine does", and the file is one a user, a sync tool or a half-finished
  write can have mangled.
- **The write is a process-unique temp-and-move**, so a reader sees a whole entry or no entry. A plain write
  leaves a truncated file when the process dies mid-write, and a truncated cache is exactly the shape this path
  exists to keep away from a driver.
- **Every failure is a colder start and nothing else.** No file, an unreadable one, a full disk, a directory that
  cannot be created: all of them fall back to compiling. That best-effort construction is the measurement's own
  kill switch, so there is nothing to turn off.
- **A driver that refuses an accepted blob gets ONE retry with no seed, and the file it refused is deleted.** The
  retry rescues the run, because without it a single file a driver dislikes would leave the process with no cache
  at all for its whole life. The delete rescues the launches after it: the entry is otherwise only replaced by a
  clean teardown, and a refused seed is exactly where a launch is likely to end without one, so the same blob
  would be read, seeded and refused once per launch for as long as it survived.

`KE_VULKAN_PIPELINE_CACHE=<directory>` relocates it and `KE_VULKAN_PIPELINE_CACHE=off` turns it off, so a session
chasing a pipeline miscompile can prove it is compiling fresh rather than believing it.

**Old engine versions' folders are swept at cache open** ([#611](https://github.com/APKiwiOrg/KhaozEngine/issues/611)).
The engine version is a path segment so an upgrade leaves one obviously prunable folder, and nothing used to
prune one, so a machine kept a folder per engine version it had ever run. Opening the cache now deletes every
sibling version folder under `<local-app-data>/KhaozEngine/vulkan-pipeline-cache/`, running-version folder
excepted, best effort: one that will not delete is skipped on its own and nothing propagates. Only the DEFAULT
location is swept, never a directory `KE_VULKAN_PIPELINE_CACHE` names, which is taken verbatim with no version
segment and whose neighbours are the caller's own files. This folder is the smallest of the three, one blob per
GPU in the machine rather than one file per program.

**Binding a pipeline invalidates descriptor slots from the first INCOMPATIBLE set onward.** `SetPipeline` emits
`vkCmdBindPipeline` and then hands the pipeline's own `VkPipelineLayout` and set-layout sequence to the matching
bind records, whose compatibility-prefix computation landed a row early with the bind flush. A rebind of the
pipeline already current does nothing at all, and that is a stronger skip than the layout guard underneath it:
two different programs sharing a layout each emit their bind and invalidate nothing, and the same pipeline twice
emits neither. A `Begin` forgets both bound pipelines, because a fresh `VkCommandBuffer` has neither. The compute
arm ends any pending render pass instance first.

**Creating a pipeline is UNREACHABLE from a command list, and that is why there are two pipeline seams.** A
pipeline creation is a shader compile, and one inside a frame is the classic hitch, so the creation seam and the
subsystem that calls it sit where the descriptor pool sits: on the device and on the resource factory, and
nowhere a recorder's field graph reaches, asserted over the type graph. A list holds a separate one-call binder
that can bind a pipeline that already exists and can make nothing.

## Barriers: masks per layout, tracked per list, and a resting layout restored at `End`

Every image layout transition is a `vkCmdPipelineBarrier2` with `srcStageMask`, `srcAccessMask`, `dstStageMask`
and `dstAccessMask` all named. There is no `vkCmdPipelineBarrier` on any path and no table of layout PAIRS.

**The masks are answered per LAYOUT, and that is what makes the pair space total.** The source masks are the old
layout's own stages and accesses, the destination masks are the new layout's, and eight layouts are covered. A
layout outside those eight throws by name. The incumbent instead runs a 25-arm if/else over the PAIR, ends it in
a debug assertion, and in Release emits `NONE` on both stage masks for a pair it does not handle, which is a
barrier that synchronises nothing on a path whose only signal compiles away. The shape here turns "no pair
produces an empty mask on both sides" into a device-free test PER LAYOUT, eight of them, plus one loop over the
pair space that the eight already imply. Under a per-pair shape that loop would be the only way to know, and it
would be 49 separate facts to keep total by hand.

**Tracking is per subresource RANGE and per COMMAND LIST.** Every texture carries a canonical resting layout
assigned at creation from its usage bits, and the device's setup command buffer puts it there before any list can
record against it. So a list ASSUMES every texture is at rest when it starts, tracks what it moved locally, and
restores everything it touched before `End`. Nothing shared is read or written during recording, which is what
lets N lists record concurrently on this backend and lets their submissions compose in any order. A texture
written in list 1 and sampled in list 2 pays a restore in list 1 and a re-transition in list 2, which is
redundant and harmless.

The incumbent keeps the layout ON the texture as recording-time mutable state. Two recordings touching one
texture read and write the same array, and the loser records either a redundant barrier, which is harmless, or NO
barrier for a transition it needed, which is a corruption no golden on a software rasterizer will show.

**Per-level tracking has to meet a whole-chain bind, and here is how the two get on.** The standard streaming
path produces both shapes in one list: a copy seeds mip 0, `GenerateMipmaps` walks the chain a level at a time,
and then a draw samples the WHOLE texture, because the seam has no texture-view type and the sampled view is
full-chain by construction. So a wider range that CONTAINS narrower tracked ones is answered rather than refused,
with ONE barrier per piece, each from its own layout, which is why levels left disagreeing by a blit chain
(0 to N-2 in `TRANSFER_SRC_OPTIMAL`, N-1 in `TRANSFER_DST_OPTIMAL`) are not an ambiguity. The pieces then COLLAPSE
into one entry, so the restore at `End` owes one barrier rather than one per level and a second bind of the same
chain owes none. Two shapes are refused by name. A range that PARTIALLY overlaps a tracked one, because
transitioning it would move part of that range and leave its entry claiming a layout the rest no longer has. And
a wider range whose untouched levels would themselves need a barrier, which only happens when the target is not
the resting layout, since untouched means at rest. That second one is unreachable through a sampled bind, because
`Sampled` wins the resting ladder, so a full-chain texture rests exactly where a sampled bind wants it.

**What that ruling LOSES is worth knowing before you read a green test as evidence.**
`OpenListTrackingGpuDevice` passes trivially here, exactly as it does on the native Direct3D 11 backend, because
this backend genuinely does not need the one-open-recording rule. It is the PORTABLE guard and it says nothing
about this backend either way.

**Where transitions happen:**

| Point | Transition |
|---|---|
| Before a draw or a dispatch | each image the bound sets name to `SHADER_READ_ONLY_OPTIMAL` or `GENERAL` |
| Begin rendering | each attachment to `COLOR_ATTACHMENT_OPTIMAL` or `DEPTH_STENCIL_ATTACHMENT_OPTIMAL` |
| Copy source or destination | to `TRANSFER_SRC_OPTIMAL` or `TRANSFER_DST_OPTIMAL` |
| Mip chain, per level | level N-1 to `TRANSFER_SRC_OPTIMAL` and level N to `TRANSFER_DST_OPTIMAL` |
| Resolve | source to `TRANSFER_SRC_OPTIMAL`, destination to `TRANSFER_DST_OPTIMAL` |
| `End` | everything touched back to its resting layout |

The attachment transitions ride the DEFERRED begin, so they are emitted immediately before
`vkCmdBeginRendering` and never inside the instance, where the same call would mean something much narrower. The
bound-image row is emitted BEFORE that begin, for the same reason.

**There is no PRESENT row, and its absence is a ruling rather than a gap**
([#557](https://github.com/APKiwiOrg/KhaozEngine/issues/557)). `PRESENT_SRC_KHR` is the swapchain image's
canonical RESTING layout, so the `End` row above IS the present transition: the frame's own list puts it there
under the rule every other texture already follows, inside the submit that already signals the render-finished
semaphore the present waits on. A present transition the boundary owned would have needed a command pool on the
boundary, a second `vkQueueSubmit` per frame, a third for the post-acquire discard, and a rearrangement of which
submit signals that semaphore, all on the one path with zero automated coverage anywhere. The acquire half falls
out of the discard rule above: a transition OUT of `PRESENT_SRC_KHR` names `UNDEFINED` as its old layout, which
is the second permitted discard site and which also covers a freshly created generation whose images really are
in `UNDEFINED`. The one shape it excludes, a second command list per frame binding the swapchain framebuffer, is
[#562](https://github.com/APKiwiOrg/KhaozEngine/issues/562).

**A transition to the layout an image is already in emits nothing**, and a whole boundary is ONE barrier call
carrying one barrier per image that actually moved. That is what keeps the barrier count proportional to passes
times touched textures rather than to draws. A plain render target rests in `COLOR_ATTACHMENT_OPTIMAL`, so an
ordinary pass pays nothing at either end. A post-chain target, which is a render target that is also `Sampled`
and therefore rests in `SHADER_READ_ONLY_OPTIMAL`, pays one barrier in and one back.

**Both numbers are counted through the same budget seam the binds and draws go through**, because the tracker's
emitter is SUBSTITUTABLE: the two implementations differ only in which command sink they drive, one over a real
command buffer and one over the device-free counting sink, and both reach it through the same batching function.
That substitution is what makes "no pipeline barriers on the per-draw path" an assertion that can fail. An
emitter that built its concrete sink inside its own body would leave the barrier tallies at zero whatever the
tracker did, and a budget that cannot fail is worse than no budget, because it reads as evidence.

**`VK_IMAGE_LAYOUT_UNDEFINED` as an OLD layout discards the image's contents, so it is refused.** It is the cheap
transition and the tempting one, and using it on contents that are still wanted produces output that varies by
driver and by run, which the goldens cannot tolerate and which does not throw when it happens. Two sites in the
whole backend may do it, each through its own named entry point: a texture's first-ever transition, on the setup
command buffer, and a swapchain image reacquired for a frame that will fully overwrite it. Every other caller
goes through an entry point that refuses `UNDEFINED` outright.

**As a NEW layout it is refused everywhere, including on the reacquire**, which is a different rule with a
different reason. `VUID-VkImageMemoryBarrier2-newLayout-01198` forbids it: `UNDEFINED` is the state an image is
in before anything has happened to it rather than one anything can be moved into, and a barrier naming it leaves
the image unusable to every later command in the recording. So that refusal sits in the one barrier constructor
both entry points pass through, and it has no legitimate site at all rather than two.

**Buffer uploads are not part of this.** A staged `UpdateBuffer` emits a PAIR of buffer memory barriers over the
written range with their masks narrowed to the destination's real usage, which involves no image and no layout
at all, so the layout tracker neither subsumes nor duplicates them. The pair is
[#618](https://github.com/APKiwiOrg/KhaozEngine/issues/618): the barrier after the copy makes the transfer write
visible to the reads that follow, and the barrier before it orders that write against the reads and writes that
came earlier, INCLUDING the ones in earlier submissions. Without the second one a consumer re-uploading a vertex
buffer per frame records the copy at the head of the next command buffer while the previous submission's
`vkCmdDrawIndexed` is still fetching those bytes, and nothing in the backend orders the two: submission order
alone is not an execution dependency, and the pool ring's fence waits on the submission FRAMES-IN-FLIGHT back
rather than on the immediately preceding one. Both the record-time path and the device-level setup path bracket
the copy the same way.

## Drawing: one order for five members, and the compute barriers that fall out of it

Every member of `IGpuCommandList` is live. A draw does four things before its `vkCmd*` and they are written ONCE
rather than at each of the five members, because the order is what can be wrong and a missing step renders
plausibly wrong rather than throwing:

1. **Every image the bound sets name goes into the layout that binding needs**, through the layout tracker, and
   this happens BEFORE the render pass instance opens. A barrier recorded inside a dynamic-rendering instance is
   a different and much narrower call.
2. **The deferred begin opens the pass**, folding every pending clear into a `loadOp` and emitting the viewport
   and the scissor if a framebuffer change marked them.
3. **The vertex and index binds flush**, one `vkCmdBindVertexBuffers` per contiguous run of dirty slots.
4. **The descriptor binds flush and the command issues**, as one pair with nothing between them.

**The vertex and index binds RECORD and the draw flushes them**, with a rebind of what is already recorded
emitting nothing at all. The incumbent issues `vkCmdBindVertexBuffers` inside its own `SetVertexBufferCore` with
no comparison, so a renderer that rebinds one mesh's buffer before each of its draws pays a native call per draw
for a state change that did not happen. A run is cut by a clean slot and by an unbound one alike, because
`vkCmdBindVertexBuffers` takes a dense array from `firstBinding` and a binding nothing bound cannot be skipped
inside one call.

**Compute rule 1 is a REAL image barrier where the sampled bind is assembled.** A storage texture a dispatch
wrote is left in `GENERAL`, and the next draw whose set samples it moves it to `SHADER_READ_ONLY_OPTIMAL`, which
is step 1 above rather than the incumbent's queued layout restore armed by a usage flag. A resource set carries
its images as plain data resolved at CREATION, with the range each binding's own view covers: the full chain for
a sampled bind, mip 0 for a storage one. That is what hands the tracker its contains-then-collapse shape rather
than a partial overlap it would refuse. The walk covers every RECORDED slot rather than the dirty ones, because a
set bound before a dispatch is still bound at the draw after it, and it costs no native call at all in the common
frame: a texture already in the layout it is asked for emits nothing.

**Compute rule 2 is honoured AS WRITTEN and the backend additionally orders a chain.** A dispatch that binds a
resource an earlier dispatch in the same list WROTE gets one read-after-write barrier before it, driven by a set
of written resources rather than by a barrier per dispatch, so a run of independent dispatches is not serialised.
**That is not a contract change.** The seam's rule 2 still says a portable consumer separates dependent dispatches
with `End`, `Submit` and `WaitForIdle`, because the Veldrid legs need the drain and a consumer that drops it here
breaks on Metal. It is evidence for the automatic-hazard seam capability
([#461](https://github.com/APKiwiOrg/KhaozEngine/issues/461)), which after this row has two of three backends able
to answer yes.

**A dispatch, a copy, a mip generation and a resolve all end the pending render pass instance first**, through the
one helper that rule has, because every one of them is illegal inside one.

## Copies, the mip chain and the resolve

A texture copy is one of FOUR shapes decided by the two textures' staging flags and by nothing else, because a
staging texture is a `VkBuffer` here and each side is therefore either an image or a buffer:
`vkCmdCopyImage`, `vkCmdCopyImageToBuffer`, `vkCmdCopyBufferToImage` or `vkCmdCopyBuffer`.

**The readback direction is the one every golden takes and it is the highest-risk parity surface in the backend.**
Its region's buffer offset and row terms come from the STAGING side's software subresource layout, which
reproduces the incumbent's own arithmetic byte for byte, while the level and layer named in `imageSubresource`
come from the IMAGE side's, which need not be the same numbers. A whole-texture copy names every mip level and
every array layer at its own offset, so reading back a chain does not silently return its base level with the
rest as whatever the staging buffer held.

**The mip chain is one halving blit per level**, floored at one so a 1024 by 1 texture ends at 1 by 1 rather than
at an extent the driver refuses, with every array layer in one blit. Level N-1 goes to `TRANSFER_SRC_OPTIMAL` and
level N to `TRANSFER_DST_OPTIMAL` at each step, so the two ranges are DISJOINT every time, which is exactly the
shape the layout tracker answers per level and then collapses when the whole chain is sampled.

**`ResolveTexture` is `vkCmdResolveImage` at mip 0 layer 0, outside a render pass instance**, with both images
transitioned to the transfer layouts and left for `End` to restore. An out-of-range sample count is refused at
TEXTURE CREATION rather than here and rather than clamped, because the engine clamps upstream against
`MaxMsaaSampleCount` so nothing legitimate reaches the throw, and a silent MSAA downgrade presents as a golden
mismatch that reads like a rendering bug.

**`MaxMsaaSampleCount` is the incumbent's own computation reproduced**, not a formula invented here: the minimum
over the engine's three MRT targets of the highest sample count each supports, read through
`vkGetPhysicalDeviceImageFormatProperties` exactly as `VkGraphicsDevice.GetSampleCountLimit` does, with the
citation pinned in a constant so the two sources can be diffed. That is what makes the capability parity test's
asserted identical satisfiable by construction rather than by luck.

**A buffer copy gets a global memory barrier on EITHER side**, which is a deliberate departure. A `VkBuffer` has
no layout, so nothing the tracker does orders a copy out of a buffer a dispatch just wrote or into one a draw is
about to read. The incumbent emits one barrier, after the copy, naming `VERTEX_INPUT` and
`VERTEX_ATTRIBUTE_READ` and nothing else, which orders exactly one consumer and nothing on the source side at
all. Two calls on a path that runs once per readback is the right price for closing a hazard class a golden on a
software rasterizer cannot show.

**The staged upload path is bracketed too, and it took the sync validation tier to notice it was not.** It
carries the narrow per-range pair described under the barriers section rather than this global one, and it
originally shipped with the after half alone, on the reasoning that a barrier before the copy would order
nothing. That reasoning holds only if nothing read the destination first, which is false for every buffer a
consumer updates once per frame ([#618](https://github.com/APKiwiOrg/KhaozEngine/issues/618)). The tier reported
it as `SYNC-HAZARD-WRITE-AFTER-READ` at `vkQueueSubmit`, 138 instances across the golden family, which is
precisely the shape a golden on lavapipe passes anyway because a software rasterizer orders the same command
stream correctly regardless.

## `KE_VULKAN_DEVICE`, `KE_VULKAN_VALIDATION`, `KE_VULKAN_FRAMES_IN_FLIGHT` and `KE_VULKAN_PIPELINE_CACHE`

## The swapchain: reproduced where a human can see it, changed where the specification forces it

`GpuDeviceContext.CreateForWindow` builds a real windowed device: one platform surface chosen from
`GpuWindowKind` (Win32, Xlib or Wayland, never all three, and never a Cocoa one because phase 4 brings a real
Metal backend rather than MoltenVK), candidates filtered on whether their graphics family can present to that
surface, `VK_KHR_swapchain` on the device, and a swapchain the present boundary acquires from, resizes,
recreates and presents. `IGpuDevice.SwapchainFramebuffer` hands back the SAME object for the whole life of the
device and everything underneath it moves.

**Nothing in this section runs in CI, on any leg, ever.** A headless Vulkan device enables no surface extension
at all, which is what lets the golden suite run on a machine with no display server, so a green golden leg is
not evidence about anything here. What IS asserted device-free is the ordering: when the recreate runs, how many
retries follow it, what an imageless frame binds, what is destroyed after what, and when the acquire-wait
counter ticks. A human at a window is the only instrument for the rest, which is why the create-info is decided
in a pure function that a plain test can read value by value.

**The create-info is reproduced from the incumbent exactly**, because it is visible only to a human eye and
changing it buys nothing this phase is measuring: `B8G8R8A8_UNORM` in `SRGB_NONLINEAR`,
`min(maxImageCount, minImageCount + 1)` images, `COLOR_ATTACHMENT | TRANSFER_DST`, `OPAQUE` composite alpha,
`clipped`, no MSAA and no depth. Present mode is `FIFO_RELAXED` then `FIFO` under a vsync request and `MAILBOX`
then `IMMEDIATE` then `FIFO` without one. `FIFO_RELAXED` under a vsync request permits tearing on a late frame
and is arguably the wrong answer, and it is reproduced anyway: the pacing work
([#380](https://github.com/APKiwiOrg/KhaozEngine/issues/380)) is where that gets decided with a measurement, and
moving the variable underneath it would make every pacing capture taken here incomparable with every one taken
against the incumbent.

**Two departures, both bugs rather than behaviours.** `preTransform` reads the surface's own `currentTransform`
rather than being hardcoded to `IDENTITY`, which is wrong on any device reporting a rotation. And the
incumbent's sRGB fallback compares a variable it has already set to `VK_FORMAT_UNDEFINED` against an sRGB
format, so its intended throw is dead code. The refusal here is real. Reproducing a bug a different device WOULD
reach is not parity.

**The acquire keeps the incumbent's TIMING and replaces its SYNCHRONISATION.** Acquiring at present time for the
next frame is a genuinely good property, because it makes the image index known before recording starts. What
the incumbent does on top of that is block the CPU on a fence with an infinite timeout, submit with no
image-availability wait semaphore, and present with no wait semaphore either. That last part is a specification
violation a validation layer flags, and a design that gates on validation cannot deliberately reproduce a
configuration validation rejects. So the acquire signals a binary semaphore the frame's FIRST submit waits on at
`COLOR_ATTACHMENT_OUTPUT`, that submit signals the acquired image's render-finished semaphore, and the present
waits on it. The pair is taken exactly once per frame, because a binary semaphore may be waited once per signal
and a second wait is a hang rather than an error.

**The acquire semaphores are a ring indexed by a monotonic acquire counter, NEVER by image index.** The
semaphore is handed to `vkAcquireNextImageKHR` before the index is known, so indexing by image index reuses one
that may still be pending from an acquire that returned a different image. It is the most common Vulkan
swapchain bug and it manifests as a validation error and an intermittent hang rather than as a clean failure.
The ring is `max(FramesInFlight, imageCount) + 1` entries, sized on the maximum because acquires are paced by
the presentation engine while recording is paced by the frame loop. Render-finished semaphores are per IMAGE
rather than per frame, because a present of image 2 must not wait on a semaphore the submit for image 0
signalled.

**The `OUT_OF_DATE` boundary is four questions and all four are answered in one method.** The recreate runs at
that same boundary, so the semaphore handed to the failed acquire is retired by the recreate's unconditional
drain rather than reused while pending or destroyed while pending. ONE fresh acquire follows it before the
boundary returns, so an ordinary boundary and a recreating one leave the device in the same state. The retry is
ONE, so a surface mid-resize cannot spin the boundary. And an imageless frame binds a device-owned ORPHAN TARGET
at the current extent clamped to a minimum of 1 by 1, then records, submits and completes exactly like any other
frame with only its present skipped. A skipped present is not a skipped frame, so `FramesBegun` counts it.

**A minimised window is survivable by arithmetic rather than by a special case.** Its surface reports every
extent as zero, the clamp against those bounds produces zero, and the resulting spec reads as not creatable, so
`vkCreateSwapchainKHR` is never called at a size the specification forbids. That is the whole guard, and it
lives in the same pure function every other extent goes through.

**A resize, a runtime `SyncToVerticalBlank` change and a checked `vkQueuePresentKHR` result all queue the SAME
recreate**, coalesced and applied at the next present boundary on the submit thread, where the recreation
provably owns the queue and no recording is in flight. `ResizeSwapchain` stores a number and returns: no lock, no
native call, nothing that can block, so a window callback on any thread is safe. Vulkan cannot change a
swapchain's present mode in place, so vsync is a full recreate here rather than the argument of a present call
it is on Direct3D 11. The incumbent ignores `vkQueuePresentKHR`'s result entirely.

**Recreation drains the timeline first, unconditionally**, which is what makes retiring a possibly pending
binary semaphore safe: there is no way to ask one whether it is pending, and destroying a pending semaphore is
undefined behaviour drivers mostly tolerate until they do not. The new views are published onto the existing
framebuffer wrapper BEFORE the old ones are destroyed, every time, which is the ordering that makes a
use-after-free unreachable rather than merely unlikely.

**A creation that fails at a creatable extent takes the old generation with it, and keeping it would be the
bug.** `vkCreateSwapchainKHR` retires the swapchain handed to it as `oldSwapchain` as an effect of the CALL
rather than of the call succeeding, and a retired swapchain may already have had the images nothing had acquired
freed underneath it. So a boundary that kept its old generation would not be holding live views, it would be
naming images the driver may have taken back, and it would hand the same retired handle to the next attempt,
which the specification forbids outright. The failure retires the old generation, binds the orphan target and
retries with no old swapchain to pass, exactly as a zero-extent surface does.

**Nothing the recreate reads can throw out of `Present`.** The surface capability query REPORTS its result the
way the acquire and the present do rather than throwing, which matters most for `VK_ERROR_SURFACE_LOST_KHR`,
because the capability re-read is the first thing a recreate does and is therefore the first place a window that
died under a running frame loop shows up. A surface reporting no formats at all is read as a failed format query
rather than as a surface with none, and a surface format `GpuPixelFormat` cannot name is caught rather than left
to escape, since the format ladder's last arm takes the surface's first format when it offers no BGRA8 at all.
All three bind the orphan target and say so once. The device CONSTRUCTOR still refuses on all three, because a
windowed device that cannot describe its own surface has nothing to hand back.

**One process cannot hold a headless device and a windowed one at the same time.** A live `VkInstance`'s
extension list is fixed at creation and Vulkan offers no way to add one afterwards, so the second configuration
is refused by name. Create the WINDOWED device first, or run them in separate processes. Serving the case would
mean either a second instance, which abandons the single-instance decision quietly, or creating every instance
with the surface extensions, which takes the golden leg down on a machine with no display server.

## `KE_VULKAN_DEVICE`, `KE_VULKAN_VALIDATION`, `KE_VULKAN_FRAMES_IN_FLIGHT` and `KE_VULKAN_ACQUIRE`

**`KE_VULKAN_VALIDATION`'s messages go to YOUR logging, so a session that configured none sees none.** The pump
writes through the ambient `Log` facade in `KhaozEngine.Diagnostics`, and that facade discards everything until
something calls `Log.Configure`. Arm the lever in an app that never configured logging and the layer still runs,
the rate limiter still counts, `strict` still latches and throws at the next controlled point, and not one
message is printed anywhere. Configure a `LogManager` with a `ConsoleSink` (or any other sink) before creating
the device if you want to read them. The Khronos layer's own stdout is independent of that and arrives either
way, which is why such a run can look half instrumented: layer output present, engine-formatted validation lines
absent. The engine's own CI hit exactly this, and its test host now configures a sink whenever the lever arms a
rung ([#565](https://github.com/APKiwiOrg/KhaozEngine/issues/565)).

**`KE_VULKAN_PIPELINE_CACHE` controls the persisted `VkPipelineCache`.** Point it at a directory to relocate the
blob (a CI workspace, or a machine whose local app data is not writable), or set it to any of `off`, `0`,
`false`, `no` or `none` to compile every pipeline fresh, which is what to do when you are chasing a pipeline
miscompile and want to be sure of what ran. Any other value is a directory path taken verbatim, which is why the
disable words are a set rather than `off` alone: `KE_VULKAN_PIPELINE_CACHE=0` naming a cache directory called `0`
beside the working directory is the failure that set exists to prevent. Turning it off does not turn off the
in-process cache, which is worth having on its own because several shipped programs differ only in blend or depth
state and their pipelines share compiled stages within one run.

**`KE_VULKAN_ACQUIRE=stall` restores the incumbent's acquire exactly**, for the frame-pacing A/B and for nothing
else: a blocking `vkWaitForFences` on the acquire, a submit carrying no image-availability wait semaphore, and a
present carrying no wait semaphore. Unset, or `semaphore`, is the shipped path. An unrecognised value warns and
keeps the default, because this variable exists to settle a measurement and a mistyped value that silently left
the default in place would produce a capture that reads as evidence about the other side.

It is **NOT usable with `KE_VULKAN_VALIDATION`**, and that is a documented limitation rather than a defect:
presenting with no wait semaphore is the specification violation the mode exists to reproduce, so the layer
reports it on every present and buries whatever else it found. Both variables set together WARN and both stay
on, because turning a diagnostic session into a startup failure is the wrong trade.

Unlike the frames-in-flight knob this one keeps a SECOND IMPLEMENTATION alive, so it is REMOVED at rollout gate
4 and the losing path deleted with it, whichever way the measurement goes. A switch that outlives its bet is how
phase 2 ended up with a gate blocked behind an unresolved A/B and two drivers still shipping. The measurement is
read off `AcquireWaitCount` and `AcquireWaitMs` rather than off mean frame time, with the frame cap and vsync
both OFF, because a machine pinned at its refresh rate produces the same mean in both positions by construction.

## Capabilities: zero permitted differences, the counter fill, and the two header fields

`VulkanCapabilityRead` assembles `GpuCapabilities` with **no device in it**. Five of the nine members are
constants of the configuration this backend creates rather than answers a device gives: `ClipSpaceYInverted` is
false because the viewport carries negative height, `DepthRangeZeroToOne` and `SamplerLodBias` and
`SupportsCompute` are core Vulkan, and `SupportsCompletionFences` is true because a fence here is a value on the
device timeline that `vkQueueSubmit` itself signals. Three arrive as plain data off the physical-device read: the
reported name, the `samplerAnisotropy` bit the feature chain settled, and the `R32_SFLOAT` format-properties read
behind `SupportsShadowMaps`. So every rule that decides what the engine believes about the device is a plain
`[Fact]` on a machine with no Vulkan loader.

**The parity bar is ZERO permitted differences, and it is stricter than the Direct3D 11 backend's for a reason
rather than by preference.** That backend exempts `SupportsCompletionFences`, because Veldrid's Direct3D 11 fence
is a CPU-side submit receipt and the native one is real, so the incumbent's answer is a defect the native backend
corrects. Nothing here is in that position: `VeldridMap.SupportsCompletionFences` already answers true for
`GraphicsBackend.Vulkan`. A difference `NativeVsVeldridVulkanCapabilityParityTests` finds is therefore a bug in
this backend until proven otherwise, and that test carries the reflection check that holds its comparer against
every public member of `GpuCapabilities`, so a member appended later cannot make the assertion quietly weaker.

**The device name the seam carries is the driver's own, and it is not the one the log prints.**
`VulkanDeviceFacts.DeviceName` substitutes `unnamed device 0x…` when a driver reports nothing readable, because a
rejection line naming an empty string is a line nobody can act on. The incumbent makes no such substitution and
`GpuCapabilities.DeviceName` is compared string for string, so the capability read takes
`VulkanPhysicalDeviceRead.ReportedDeviceName` instead: verbatim, empty when the driver reported nothing, which is
exactly what the seam's own doc says empty means. There is **no whitespace trim on either path**, because the
incumbent does not trim and trimming one side alone fails parity on every machine whose vendor pads its name.

**`SupportsShadowMaps` asks `R32_SFLOAT` for COLOUR attachment plus sampled image**, held as
`VulkanPhysicalDeviceReader.ShadowMapFormatFeatures` with the decision split off the driver call so the bits are
assertable device-free. Asking for the depth-stencil attachment bit instead, which the capability's name
suggests, is not a stricter question but a structurally false one: `R32_SFLOAT` is a colour format and no driver
reports that bit for it. The pass wants this pair anyway, since `ShadowMapRenderer` creates the atlas as
`R32Float` with `RenderTarget | Sampled` and hangs a separate depth-stencil off it, and it is also the parity
answer, since `VeldridMap.SupportsShadowMaps` asks `GetPixelFormatSupport` for the same two.

**`MaxMsaaSampleCount` is pinned to one sample until [#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525)
reproduces the incumbent's own `GetSampleCountLimit`.** That is a ruling rather than an omission: two drafts of
the design each invented a formula, the two differ, and both then asserted equality with the incumbent as a test,
so at most one of them could have been measuring anything. Pinning under-promises, and `AntiAliasing.ResolveFor`
clamps a request rather than throwing on one.

**The counter fill is nine READINGS.** The drain pair comes off the timeline, the backpressure pair off the one
accumulator both the command lists and the uniform ring stall into, the off-timeline pair off the ring's pending
patches, and `FramesBegun` with the acquire pair off the present boundary. A headless device reports zero for
those last three because it has no swapchain and opens no frame at this seam, which is literally true of such a
device rather than a placeholder, and `HasValue` is true throughout so a capture carries columns rather than
nothing. The row against a real device asserts the channel NAMES the projection carries and not their count,
since once `HasValue` is true the count is a property of `GpuTelemetryChannels.For` rather than of this
backend's fill. **`DrainCount` is not comparable against the Direct3D 11 native backend's**: that one counts every
`WaitForIdle`, because its drain must signal and flush to know it is idle, and this one counts only the drains
with outstanding submissions, because comparing the timeline counter against the last submitted value genuinely
means idle here. `DrainMs` is comparable, since the drains this one skips cost about nothing.

**Both `GpuDeviceDiagnostics` fields are live members and both reach the session header.** `softwareAdapter` is
`deviceType == Cpu || driverID == MesaLlvmpipe`, landing in the EXISTING telemetry field rather than a new one,
and `deviceLossReason` carries the latch the device-loss row sets at the fault site. Live rather than
creation-time arguments because a loss happens at an arbitrary moment long after creation.

**The two-device rows RUN on the `vulkan-native` leg** ([#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529)),
against lavapipe, and a green run anywhere else is still not evidence about this backend. Their early returns
read the backend's own functional probe rather than an operating system, since Vulkan is not a Windows API, and
that early return is exactly what `KE_VULKAN_REQUIRED=1` turns into a hard failure on the leg that declares a
native device mandatory. Without it, a loader regression there would empty those rows into passing tests with no
assertions in them, and a dormant row does not skip, so nothing downstream could notice.

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

So the native Vulkan CI leg needs no symlink step of its own. The existing one stays where it is, and it stays
scoped to the OS rather than to the incumbent leg, which is a distinction worth stating because the step now
looks dead on the native leg and is not: the capability-parity test creates a VELDRID Vulkan device beside the
native one on whichever leg it runs, so a native leg without the symlink fails that test at device creation.
The step retires with the Veldrid Vulkan leg itself, in phase 4 and not before
([#540](https://github.com/APKiwiOrg/KhaozEngine/issues/540)).

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
`Veldrid` package, and that is decision V-P3 rather than an accident of ordering: the shader path needs
glslang, which arrives as `Veldrid.SPIRV`, and referencing it from a backend whose entire premise is
being Veldrid-free is a bad signal no guard reading package ids would catch. The edge stays in
`KhaozEngine.Gpu` behind its internal, Veldrid-free `SpirvFrontEnd` helper, which this package reaches across
`InternalsVisibleTo`.

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

`VulkanShaderFrontEndOnlyTests` is the fifth, and it guards decision V-S3's split. Both halves of the shader
toolchain live in `KhaozEngine.Gpu`, so every scan above reads identically whether this package calls
`SpirvFrontEnd` or `SpirvCrossCompile`, and the tempting shortcut is one line that would compile. It reads this
assembly's `TypeRef` table off disk and asserts it names the front end and no back-end type, plus the mirror
assertion that the walk really does find the front end, so a metadata read that quietly found nothing cannot
pass forever.

## Layering

```
KhaozEngine.Gpu.Vulkan -> KhaozEngine.Gpu                     (the only direction. Gpu never references a backend package)
KhaozEngine.Gpu.Vulkan -> KhaozEngine.Diagnostics             (the probe's one log line, same as the D3D11 instance)
KhaozEngine.Gpu.Vulkan -> Silk.NET.Vulkan(+.Extensions.KHR/.EXT)   (its subject matter)
KhaozEngine.Gpu.Vulkan -> Veldrid*                            (never, asserted two ways)
```

See [docs/DEPENDENCY-SEAMS.md](../docs/DEPENDENCY-SEAMS.md), "Out-of-package graphics backends", for the
inverted-edge pattern this package is the second instance of.
