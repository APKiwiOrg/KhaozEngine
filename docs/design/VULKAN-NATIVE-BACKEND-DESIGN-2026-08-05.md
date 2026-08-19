# KhaozEngine.Gpu.Vulkan: native Vulkan backend design (2026-08-05)

**Status: spec complete, implementation not started.** Phase 3 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), following the shipped phase 2
(`docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md`). This document is the phase 3 deliverable.
Implementation is a numbered issue list in section 18 and none of it has been written. Nothing here has run on
a device. Section 16 lists every decision that rests on reasoning rather than measurement, each with the
measurement that settles it, the switch that turns it off, the criterion that retires the switch, and a
deadline: the gate at which a switch keeping a second implementation alive is removed, or by which a bet
carrying no such switch must be resolved.

Written against engine `17.32.0` (`Directory.Build.props`). The incumbent this design replaces and must reach
parity with is **`Veldrid 4.9.103`**, the vendored fork package `Directory.Packages.props` pins, whose
`src/Veldrid/Vk/` tree is byte-identical to upstream `v4.9.0` (verified: `git diff v4.9.0 v4.9.103 --
src/Veldrid/Vk/` is empty, and no fork-authored commit has ever touched that directory). That distinction is
load-bearing and section 2.1 explains why: the fork repository also carries a master-based branch whose Vulkan
tree does differ, one draft cited it, and the difference lands squarely on the strongest argument either draft
made.

**Provenance.** Two complete competing drafts were written independently from opposite priors: a reuse-first
draft (63 decisions, carry phase 2's machinery and the incumbent's shapes, argue every departure) and a
Vulkan-idiomatic draft (78 decisions, design from Vulkan's own primitives outward, argue every inheritance).
This document adjudicates them. Where a draft won outright it is recorded in the Origin column. Where the
adjudication produced something neither proposed, the column says so and section 2 argues it. Section 20 lists
what was rejected from both.

---

## 1. Decisions

| # | Area | Decision | Origin |
|---|---|---|---|
| V-P1 | Package | New `KhaozEngine.Gpu.Vulkan`, opt-in, outside every umbrella, `net10.0` with NO OS platform guard. Vulkan is not a Windows API, so the `[SupportedOSPlatformGuard]` plus `NoInlining` apparatus `Gpu.D3D11` needs has no analogue. The assembly loads harmlessly on macOS, where it is never selected | Both, converged |
| V-P2 | Binding | `Silk.NET.Vulkan` plus `Silk.NET.Vulkan.Extensions.KHR` and `.EXT`, pinned to the `2.23.0` line the windowing, input and audio stacks already pin. Hand-rolled P/Invoke, TerraFX and vendoring Veldrid's `Vulkan.*` namespace all rejected (section 3.1) | Both, converged |
| V-P3 | Layering | References `KhaozEngine.Gpu` and Silk.NET only. The no-Veldrid-edge assertion is extended in BOTH its forms: the csproj read and the IL reference walk. The walk is the load-bearing one, because Veldrid is in the transitive closure through `KhaozEngine.Gpu` whatever the csproj says | A's two-way form |
| V-P4 | Shared home | NOTHING is extracted from `KhaozEngine.Gpu.D3D11` into a shared home in this phase. The rule of three is not satisfied by two, and section 2.2 names each candidate and what fails it | B |<br>**Answered 2026-08-11, and the wait paid.** The rule of three is satisfied now, and `METAL-NATIVE-BACKEND-DESIGN-2026-08-09` section 2.8 decides #531 per candidate: five things move into `KhaozEngine.Gpu/Internal/` (the `DeviceLiveness` latch, the counter accumulators, the diagnostic rate limiter, the shader-cache key and file discipline, and the completion timeline's bookkeeping) and four stay put with a written refusal (the ring's CODE, the record-then-flush schedule, the dirty MODEL, the generic emitter interface). **This row's own prediction was right for a reason it did not name**: it expected D3D11 to be the outlier, and what actually happened is that the two lists BOTH prior phases would have extracted from were wrong. Phase 2 named three-state dirty tracking as general and Vulkan collapsed it to two, phase 2 named the flush schedule as general and Metal's has a call class neither neighbour has, and the timeline bookkeeping that turned out to be genuinely common appears on neither phase's list. It is also only extractable because M-F1 chose a shared event, so a ruling in one section decided a candidate in another. (**Corrected in place the same day, once row 18 EXECUTED that ruling: it is TWO things, not five, and this row's decline was righter than the answer above.** The `DeviceLiveness` latch moved whole and absorbed a fourth copy in the shared namespace nobody had counted. The counters moved at the CARRIERS only and were refused at the accumulation sites, `VulkanWaitTotals`'s own header having argued this row's case correctly at the time. The rate limiter and the shader-cache key were refused because Metal has neither and cannot have either, and the timeline bookkeeping was refused because D3D11's peer is not the type it looks like. So the decline this row made at two was not merely cautious: two of the five things the list at three named were things a third implementation does not have. See the row 18 addendum in section 2.8 of `METAL-NATIVE-BACKEND-DESIGN-2026-08-09`.) |
| V-P5 | Shared home | What IS shared now is the uniform ring's SEMANTIC tests, run against both backends' rings through one internal test-only interface. Share the tests at two implementations, share the code at three. The interface and BOTH adapters live in `KhaozEngine.TestSupport.Gpu`, which already references `Gpu.D3D11`, is `IsPackable=false` and ships nothing, so V-P4's no-shared-PRODUCTION-home rule is untouched. `Gpu.D3D11` grants it `InternalsVisibleTo` beside the one grant it has today | B, home named by the judge |
| V-P6 | Shared home | Section 9.4 carries the explicit POLICY INVENTORY the Vulkan ring must satisfy, so the decision not to share code does not become a decision to re-derive the policy from memory. The extraction issue is filed now, triggered by phase 4 | Judge, new |
| V-I1 | Identity | Append `GpuBackendKind.VulkanNative = 5` with an explicit ordinal and the existing append-only comment. New tokens `vulkan-native` and `vk-native` | Both, converged |
| V-I2 | Identity | The append is CHEAPER than D3D11's by three rows, and the thirteen-site audit is a TEST now (`GpuBackendKindAppendAuditTests`) rather than a discovery. Section 4.2 walks all thirteen anyway | B |
| V-I3 | Goldens | GUEST in the committed `vulkan` family through `GoldenBackendToken`. The switch has no discard arm and throws with a message naming the decision, so the audit test turns a missed mapping into a device-free red. Bake refusal derives guest-ness generically, from the token not matching the kind's own name under the `OrdinalIgnoreCase` compare `BakeRefusal` already uses, which is what makes `vulkan` an OWNER token for `Vulkan` and a GUEST token for `VulkanNative` | B |
| V-I4 | Identity | A missing provider registration THROWS and never falls back. An incapable machine is the different case, answered by `IsBackendSupported`'s functional probe and reported through `AfterFallback`. `PreflightProvider` already fixes the order so a wiring fault cannot present as an incapable machine | Both, converged |
| V-I5 | Identity | Add `GpuBackendKinds.IsVulkan()` beside the existing `IsDirect3D11()`, for the same reason that one exists: a copy of the question at each site drifts | A |
| V-I6 | Citation | Every citation into the incumbent in this document and in the implementation issues names a MEMBER, not a line number. Both drafts cite lines, both cite lines from different trees, and `GpuBackendKindAppendAuditTests` already records that phase 2's cited line numbers went stale | Judge, against both |
| V-N1 | Instance | ONE process-wide `VkInstance`, refcounted, created and destroyed under `GpuDeviceContext`'s existing `_lifecycleGate`. The gate STAYS: it also covers disposal and it is not this backend's to remove | Both, converged |
| V-N2 | Device | Request `min(VK_API_VERSION_1_3, vkEnumerateInstanceVersion())`. Four hard version-and-feature requirements: device apiVersion at or above 1.3, `dynamicRendering`, `synchronization2`, `timelineSemaphore`. All three features are mandatory on a 1.3 device, so the check fails loudly on a 1.2 machine instead of crashing on frame one. The probe checks these plus the three further reads 4.1 lists. The incumbent hardcodes `1.0.0` at two sites and never calls `vkEnumerateInstanceVersion` at all (verified) | B, reason corrected |
| V-N3 | Device | The DEFAULT physical device reproduces the incumbent's `physicalDevices[0]`, filtered by V-N2's hard requirements, with any substitution LOGGED. `KE_VULKAN_DEVICE=<index>\|<substring>\|llvmpipe\|discrete\|integrated\|cpu` is explicit selection. Preferring a discrete device by default is a follow-up, not this phase | A's default, B's filter |
| V-N4 | Device | Features are enabled SELECTIVELY, by name, through the `pNext` chain. The incumbent hands `vkCreateDevice` the entire supported feature struct (verified), which makes the engine's real dependencies unknowable and moves a missing-feature failure to an unrelated call site | B |
| V-N5 | Queue | ONE graphics queue that also presents, required to be one family. A device whose graphics family cannot present is REJECTED by the probe with a named reason and routed through the reported fallback. No transfer queue, no async compute | B |
| V-N6 | Extensions | Instance: `VK_KHR_surface` plus exactly one platform surface extension, windowed only, plus `VK_EXT_debug_utils` under the validation knob. Device: `VK_KHR_swapchain`, windowed only. That is the entire list. The headless path enables NO surface extension, which is why the golden suite runs with no display server and also why the swapchain has zero CI coverage | B |
| V-R1 | Recording | `VulkanCommandList` implements `IGpuCommandList` and calls `vkCmd*` at RECORD TIME into a `VkCommandBuffer`. No op stream, no second driver, no `KE_VULKAN_RECORD` switch and no M1-analog A/B. Vulkan's deferral IS the deferral the D3D11 recorder had to build | Both, converged |
| V-R2 | Recording | Each list owns `FramesInFlight` `VkCommandPool`s, one primary buffer each, reset with `vkResetCommandPool` at `Begin`. Not one pool with `RESET_COMMAND_BUFFER`, which tells the driver every buffer must be individually resettable and pushes it onto the slower per-buffer allocator | B |
| V-R3 | Recording | `Begin` advances to the next slot and waits on that slot's recorded timeline value, which returns immediately in the steady state at `FramesInFlight = 3` and is counted as backpressure when it does not | Both, converged |
| V-R4 | Recording | N lists record concurrently and genuinely, because per-list pools are what Vulkan's own threading model asks for and because layout tracking is LIST-LOCAL (V-F6). The PORTABLE seam contract is unchanged: exactly one open recording per device. This backend's permissiveness is a backend property | B |
| V-R5 | Recording | Two-state per-slot dirty records, not three. `DynamicOffsetsOnly` exists on D3D11 to skip textures and samplers, and a Vulkan descriptor bind is ONE call whether one offset moved or every image in the set changed. The flush emits ONE `vkCmdBindDescriptorSets` per CONTIGUOUS RUN of dirty slots | B |
| V-R6 | Recording | A pipeline switch invalidates recorded slots only from the first INCOMPATIBLE set onward, decided by a set-layout handle-prefix compare that V-D5's content dedup makes a pointer compare. This is the Vulkan occupant of R5's clause-5 slot and it exists for a different reason (binding validity, not register numbering) | B |
| V-R7 | Recording | That prefix is GUARDED, not trusted. A device-free test asserts the computed compatible prefix never exceeds the true identical-handle prefix for any pair of shipped pipelines, and the validation build asserts at draw that every bound set's layout matches the current pipeline layout at that index | Judge, new |
| V-A1 | Rendering | DYNAMIC RENDERING. No `VkRenderPass`, no `VkFramebuffer`, no cache for either, no invalidation on resize. `IGpuFramebuffer.Outputs` is already `VkPipelineRenderingCreateInfo`'s input verbatim | B |
| V-A2 | Rendering | `vkCmdBeginRendering` is DEFERRED to the first draw, so a clear recorded after `SetFramebuffer` folds into `loadOp = CLEAR` instead of `vkCmdClearAttachments`. A clear arriving after rendering has begun still uses `vkCmdClearAttachments`, which is what the incumbent does in the same situation | B |
| V-A3 | Rendering | The clear-only case is REPRODUCED deliberately: `SetFramebuffer` plus clear plus `End` with no draw must still clear, because the incumbent forces it and a golden depends on it. Here it is a begin/end pair with no draws, flushed at `End` and at the next `SetFramebuffer` | B |
| V-A4 | Rendering | Any command illegal inside a render pass instance (dispatch, resolve, copy, mip generation) ENDS the pending rendering first. One invariant, one helper, one device-free test | B |
| V-A5 | Viewport | `SetFramebuffer` emits `vkCmdSetViewport` plus `vkCmdSetScissor` ON A FRAMEBUFFER CHANGE ONLY, replicating W6's identity guard exactly, and the viewport carries NEGATIVE HEIGHT so `ClipSpaceYInverted` stays false and `GpuClip.Correct` stays the identity. Core in 1.1, no extension, no conditional at the 1.3 floor | Both, converged |
| V-A6 | Rendering | `storeOp = STORE` unconditionally, matching the incumbent, and deliberately not optimised to `DONT_CARE` for depth. Undefined contents are not stable across runs and the goldens require stability | B |
| V-D1 | Descriptors | One `VkDescriptorSet` per `IGpuResourceSet`, allocated and written ONCE at `CreateResourceSet`, immutable for its life. This is what the incumbent already does, so it is a port rather than an invention, and it is stated as a decision because the naive Vulkan renderer does the opposite | Both, converged |
| V-D2 | Descriptors | "Zero `vkAllocateDescriptorSets` and zero `vkUpdateDescriptorSets` during recording" is enforced STRUCTURALLY, not by the budget test: the descriptor pool is unreachable from the recording type, asserted by an architecture test over the type graph, plus a zero-count assertion against a fake pool in the device-free harness. Both drafts claimed a counting seam gates this and neither seam can see those calls | Judge, against both |
| V-D3 | Descriptors | Pools are sized from ACTUAL demand per allocation, not the incumbent's fixed `maxSets = 1000` with 100 descriptors of each of seven types, and freeing restores EVERY counted type including `UniformBufferDynamicCount` and `StorageBufferDynamicCount`, which the incumbent forgets (verified, present in `v4.9.0` and unchanged upstream) | Both, converged |
| V-D4 | Descriptors | EVERY ring-backed uniform buffer binds as `UNIFORM_BUFFER_DYNAMIC`, not only the element the layout declared dynamic, and the per-frame ring base is a bind-time entry in `pDynamicOffsets`. The seam's "at most one declared-dynamic element per set" is a statement about the ENGINE's dynamic-offset API, not about the Vulkan descriptor type | Both, converged |
| V-D5 | Descriptors | `VkDescriptorSetLayout` and `VkPipelineLayout` are CONTENT-DEDUPLICATED. Not a micro-optimisation: identity-shared set layouts are what make bound descriptors survive a pipeline switch (V-R6). The incumbent creates one per `ResourceLayout` object with no dedup, so nothing is ever compatible and every switch forces a full rebind | B |
| V-D6 | Descriptors | The dynamic uniform descriptor count is checked at pipeline-layout creation with a named exception, AND a device-free test asserts every shipped `CreateResourceLayout` shape keeps the pipeline total at or under the Vulkan required minimum of 8. The test is the one that matters, because it fails on the free Linux leg rather than on a minimum-spec machine | Both, converged |
| V-D7 | Descriptors | That limit stops being a knowledge claim: the CI `vulkaninfo` step drops `--summary` so the full `VkPhysicalDeviceLimits` block is dumped on every Vulkan run. No Vulkan device limit is observable in CI today (verified) and the incumbent reads exactly three limits, none of them this one | Judge, new |
| V-D8 | Descriptors | NO descriptor indexing, NO bindless, NO push constants, NO descriptor buffers. Section 8.4 argues the decline against the idiomatic prior's own grain and names the trigger that reopens it | B |
| V-M1 | Memory | NO VMA. An engine-owned block suballocator. **The decline is CONDITIONAL on V-T4's synchronisation-validation gate existing**, because that gate is the only instrument in the net that sees an aliasing or hazard defect a golden on a software rasterizer cannot | Both on the decline, judge on the condition |
| V-M2 | Memory | Chunks pooled by `(memoryTypeIndex, linear\|optimal)` so `bufferImageGranularity` is satisfied by SEPARATION rather than by the incumbent's per-allocation rounding. First-fit over a sorted free list with alignment correction, split on allocate, merge on free, dedicated allocation on driver preference or above a threshold | B |
| V-M3 | Memory | Host-visible chunks are `vkMapMemory`'d once at creation and NEVER unmapped, so every host-visible allocation has a stable pointer for the chunk's life. This is the thing D3D11 could not do and had to emulate with a record-phase map | Both, converged |
| V-M4 | Memory | The uniform ring is PINNED to a host-visible `HOST_COHERENT` type as a hard requirement, and the probe fails a device that reports none. The spec requires at least one such type, so the check is a formality that fails loudly rather than a gate anything real trips. Everywhere else the allocator PREFERS coherent and, when the chosen type lacks it (readback staging prefers cached), emits `vkFlushMappedMemoryRanges` and `vkInvalidateMappedMemoryRanges`. The incumbent emits neither anywhere and rests entirely on coherence being available | B, ring pinned by the judge |
| V-M5 | Uniforms | Every `UniformBuffer`-usage buffer is one `VkBuffer` of `stride * FramesInFlight` in host-visible persistently mapped memory, where `stride = align(size, max(256, minUniformBufferOffsetAlignment))`. `IGpuBuffer` identity NEVER changes and the base is applied at BIND. `FramesInFlight = 3` | Both, converged, A's stride |
| V-M6 | Uniforms | The descriptor is written once at set creation with `offset = 0` and `range` equal to the BIND WINDOW: `GpuBufferRange.Size` where the set was created from a range, the buffer's own logical size otherwise. Never `VK_WHOLE_SIZE`, and never the stride, which is the shape that overruns. `VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979` requires effective offset plus range to stay inside the buffer, and five shipped renderers pass a non-zero caller offset, so the invariant the ring owes is `rangeOffset + callerDynamicOffset + range <= stride` (9.2) | A's shape, corrected against the VUID |
| V-M7 | Uniforms | U3's two creation-time invariants are adopted VERBATIM: only `UniformBuffer` usage is ring-backed, and a ring-backed buffer never receives a non-uniform binding. Including the part that matters most, that the throw is a BACKEND-DIVERGENT CREATION FAILURE and must be documented as one rather than discovered by a consumer | Both, converged |
| V-M8 | Uniforms | #484's correction is adopted WHOLESALE without re-deriving it: an off-timeline `UpdateBuffer` reaches EVERY segment, gated on the same completion read, with a non-current segment deferred as a pending patch rather than waited on, and the current segment always written ungated. It cost a consumer defect to learn once | Both, converged |
| V-M9 | Uploads | Record-time bulk payloads take a per-list persistently mapped staging arena and replay as `vkCmdCopyBuffer` or `vkCmdCopyBufferToImage`. Staging buffers are pooled BY SIZE with a real retention cap. The incumbent destroys any returned staging buffer over 512 bytes, so every real upload allocates and destroys a buffer plus a memory block per call | Both, converged |
| V-M10 | Textures | Texture creation issues NO queue submit. The incumbent's clear-if-render-target plus transition-if-sampled cost a whole `vkQueueSubmit` EACH. Here both are appended to ONE device-owned setup command buffer under a short SETUP-BUFFER LOCK (a `VkCommandPool` and its buffers are externally synchronised, 6.1, so free-threaded creation may not append unsynchronised), flushed lazily at the next submit OR at any device-level read (`Map`, readback, drain), which is what makes "no submit per texture" true without leaving a hole. The clear is preserved deliberately, because undefined is not stable across runs | A's mechanism, B's motivation |
| V-M11 | Views | Every `VkImageView` is created at RESOURCE creation from the declared usage bits. None at bind, none at draw. This is X1's analogue and X1's evidence is why: all 25 `DEVICE_REMOVED` stacks in #423 surfaced inside the lazy view constructor on the draw path, so lazy creation put an allocation on the hot path and put it on the exact path a broken device makes fail. Enforced the way V-D2 enforces the descriptor pool, by unreachability: no view factory is reachable from the recording type | Judge, new (X1 precedent) |
| V-F1 | Fences | ONE device-wide monotonic TIMELINE semaphore. Every submit signals `++value`, `IGpuFence` holds a target, and `Signaled` is a non-blocking `vkGetSemaphoreCounterValue` comparison. `SupportsCompletionFences = true`, which is what the incumbent already reports, so this is PARITY rather than the upgrade it was on D3D11 | Both, converged |
| V-F2 | Fences | The timeline's monotonicity makes the seam's documented fence ordering a theorem rather than a convention, which is the argument for one device timeline over per-submit `VkFence` objects. Section 10.1 | B |
| V-F3 | Fences | `Submit(cl, fence)` is ONE `vkQueueSubmit`. The incumbent's second empty submit signalling an internal tracking fence is not inherited | A |
| V-F4 | Fences | `WaitForIdle` is `vkWaitSemaphores` on the last submitted value, counted into the existing `DrainCount` and `DrainMs`. Not `vkQueueWaitIdle`: it does not hold the queue lock and it gives a number to time. Real from day one, so there is no C6-style bet here | B |
| V-F5 | Sync | Swapchain acquire and present use BINARY semaphores, because `VK_KHR_swapchain` accepts no timeline. The acquire ring is `max(FramesInFlight, imageCount) + 1` entries indexed by a monotonic acquire counter, NEVER by image index, which is the classic reuse bug | Both, judge-sized |
| V-F6 | Barriers | `vkCmdPipelineBarrier2` with explicit stage and access masks, tracked per subresource range and LIST-LOCAL. Not the incumbent's 25-arm if/else over layout pairs that ends in `Debug.Fail` and silently emits `NONE` on both sides in Release for an unhandled pair | B |
| V-F7 | Barriers | Every texture has a CANONICAL RESTING LAYOUT derived from its usage bits, and every command list restores it before `End`. That is what makes lists composable in any submit order, which is what the seam promises and what record-time layout tracking on the texture object cannot deliver | B |
| V-F8 | Barriers | A transition FROM `VK_IMAGE_LAYOUT_UNDEFINED` DISCARDS contents, so it appears as an old layout in exactly two places: a texture's first-ever transition, and a swapchain image being reacquired for a frame that will fully overwrite it. Both asserted at the point of use. This is a determinism rule, not a nicety | B |
| V-F9 | Barriers | Resource disposal is DEFERRED behind the timeline value of the last submit that referenced the resource, which converts the mid-life-disposal-racing-queued-work defect class from convention-safe to structurally safe | B |
| V-F10 | Liveness | The `DeviceLiveness` latch is reproduced exactly (X3 precedent). Device destruction calls `vkDeviceWaitIdle` FIRST, unlike the incumbent, which destroys the memory manager and pools and then waits | Both, converged |
| V-W1 | Swapchain | Present-path semantics are reproduced from the incumbent exactly where they are visible only to a human: surface format and colour space, present-mode preference order including `FIFO_RELAXED` under a vsync request, image count, usage, composite alpha, `clipped`. W1's lesson applied where it actually binds | Both, converged |
| V-W2 | Swapchain | Two deliberate departures from that reproduction, both bugs rather than behaviours: `preTransform` reads `currentTransform` rather than being hardcoded to `IDENTITY`, and the sRGB fallback that compares an already-`Undefined` format is not reproduced | Both, converged |
| V-W3 | Swapchain | The every-frame CPU acquire stall is NOT reproduced. Acquire signals a binary semaphore the frame's submit waits on at `COLOR_ATTACHMENT_OUTPUT`, and the submit signals a render-finished semaphore the present waits on. Behind `KE_VULKAN_ACQUIRE=stall`. Section 2.6 shows this is FORCED by V-T4 rather than merely preferred | Both, judge's argument |
| V-W4 | Swapchain | An acquire returning `OUT_OF_DATE` recreates at that same present boundary, retires the failed acquire's semaphore through the recreate's drain rather than reusing it, and takes ONE fresh acquire on the new swapchain before the boundary returns, so the next frame starts with an image index exactly as an ordinary boundary leaves it. Only if that retry also fails does a frame go imageless, and then it binds the device's ORPHAN TARGET, submits normally and skips its present. `FramesBegun` counts it, because a skipped present is not a skipped frame. Neither draft says what happens to that frame | Judge, new |
| V-W5 | Swapchain | `IGpuFramebuffer` wrapper identity is STABLE across resize and across a present-mode change (W2 precedent), which matters more here than on D3D11 because every image view object is replaced | Both, converged |
| V-W6 | Swapchain | `ResizeSwapchain`, `OUT_OF_DATE` and `SUBOPTIMAL` from either call, and a runtime `SyncToVerticalBlank` change all queue and apply at the next present boundary on the submit thread. Recreation drains the timeline first, which is what makes retiring pending acquire semaphores safe | Both, converged, B's present-mode row |
| V-W7 | Swapchain | `vkQueuePresentKHR`'s result is CHECKED. The incumbent ignores it entirely | B |
| V-W8 | Threading | No frame-long lock. Recording is lock-free and per-list. One `_submitLock` covers `vkQueueSubmit`, present and the resize apply. Three short locks sit beside it, each scoped to one operation: the allocator's around a suballocation, the descriptor pool manager's, and the SETUP-BUFFER lock V-M10's device-owned command buffer needs. Creation is otherwise free-threaded, with no `DriverConcurrentCreates` analogue to ask about | Both, converged, setup lock named by the judge |
| V-S1 | Shaders | GLSL 450 stays the single source. The backend calls the existing `SpirvCrossCompile.ToSpirv` across `InternalsVisibleTo` and hands the bytes to `vkCreateShaderModule`. Zero cross-compilation, zero new shader machinery, zero reflection | Both, converged |
| V-S2 | Shaders | The byte-identical-SPIR-V parity claim is TWO artefacts, not one: a `SpirvFrontEndPin` constant set with its citation, and a ONE-OFF in-process parity measurement against the incumbent, taken and RECORDED here before the first golden run. The per-program hash table detects DRIFT only, and phase 2's own test header says so | B |
| V-S3 | Shaders | `SpirvCrossCompile` is SPLIT along the seam it already has: front end (`ToSpirv`) and back end (`VertexFragmentToHlsl`, `ComputeToHlsl`). Vulkan depends on the front end only, asserted by an architecture test that it names no back-end member | B |
| V-S4 | Shaders | SPIRV-Cross is NOT touched in this phase. Vulkan consumes no cross-compiled output, so swapping it here would put D3D11's 36 goldens and both WARP corruption workarounds in play for a backend whose CI leg cannot see any of it. F2 stays a Metal-phase change | Both, converged |
| V-S5 | Shaders | S5's holed-signature workarounds STAY, and this backend is not an argument against them. They are FXC-and-WARP specific and the D3D11 leg ships indefinitely. Stated because it WILL be re-raised from a Vulkan seat where the sinks look pointless | Both, converged |
| V-S6 | Shaders | The Metal-driven shader-shape invariant (one uniform buffer per pipeline at set 0 binding 0, per-mesh textures at set 1 and up) STAYS. Vulkan has no such limit and a Vulkan-only author would naturally spread uniforms across sets, which breaks a phase-4 backend that is not here to defend itself | B |
| V-S7 | Shaders | A `VkPipelineCache` persisted to disk, keyed on `(pipelineCacheUUID, driverVersion, engine version)`, HEADER-VALIDATED before `pCacheData` is passed, discarded silently on any mismatch, and best-effort so a read or write failure is never fatal. Modules deduplicated by SPIR-V hash. The incumbent passes `VkPipelineCache.Null` at both creation sites | B, with A's caution as a requirement |
| V-S8 | Shaders | The `set=` and `binding=` numbering gets S2's table test: parse every shipped GLSL `layout(set = N, binding = M)` declaration and assert N and M against the pipeline's layout-array and element-array indices. `SpriteBatch` declares its UBO at `set = 1`, so "the UBO set comes first" is false in shipped code | A |
| V-C1 | Compute | Rule 1 is satisfied by a REAL image barrier at the sampled bind, `GENERAL` to `SHADER_READ_ONLY_OPTIMAL`, `COMPUTE_SHADER` to `FRAGMENT_SHADER`, rather than the incumbent's queued layout restore armed by a usage flag | B |
| V-C2 | Compute | Rule 2 is honoured AS WRITTEN and no seam member is added. The backend ADDITIONALLY emits a real read-after-write barrier between dependent dispatches inside one list, driven by a written-resource set. That is EVIDENCE for F1 and explicitly NOT a contract change | B |
| V-C3 | Compute | The seam's rule 1 and rule 2 comment describes VELDRID's Vulkan behaviour BY NAME and becomes false the day this backend ships. Rewording it to say which implementation it describes is a doc task with an owner, not a nice-to-have | B |
| V-C4 | Compute | Storage buffers are plain SSBOs. C2's RAW byte-address forcing was an artefact of what SPIRV-Cross emits for HLSL and has no Vulkan analogue | Both, converged |
| V-C5 | MSAA | `MaxMsaaSampleCount` is READ OFF the incumbent's own `GetSampleCountLimit` implementation and reproduced, with the citation pinned in a constant. NEITHER draft's invented formula is taken, because they differ and at most one could have equalled the incumbent's | Judge, against both |
| V-C6 | MSAA | `ResolveTexture` is `vkCmdResolveImage` at mip 0 layer 0 outside a render pass instance. An out-of-range requested sample count THROWS at texture creation (C4 precedent). No MSAA on the swapchain, matching the incumbent | Both, converged |
| V-C7 | Staging | Staging textures are `VkBuffer`-backed with the incumbent's SOFTWARE-computed subresource layout reproduced byte for byte, asserted by a device-free table test against a checked-in table taken from the incumbent's own arithmetic. Every golden reads back through `Map` and `MappedData.RowPitch`, so a different arithmetic garbles all 36 at once | A |
| V-C8 | Staging | `Map(staging, Read)` WAITS on the timeline's last submitted value before returning the pointer, counted as a drain. D3D11's `Map(READ)` blocks by definition, so this is where Vulkan must be explicit about something the other API did implicitly | B |
| V-G1 | Capabilities | Field-by-field parity with the incumbent and ZERO permitted differences, not the one D3D11 had to permit, plus the reflection-completeness check that the comparison covers every member of `GpuCapabilities` | Both, converged |
| V-G2 | Diagnostics | `KE_VULKAN_DEVICE` per V-N3, CI pins `llvmpipe`, and `SoftwareRasterizer` is `deviceType == Cpu \|\| driverID == MesaLlvmpipe`, landing in the EXISTING `softwareAdapter` telemetry field | Both, converged |
| V-G3 | Diagnostics | `KE_VULKAN_VALIDATION=0\|1\|strict\|sync`. `1` enables `VK_LAYER_KHRONOS_validation` plus a `VK_EXT_debug_utils` messenger pumping through a rate limiter, `strict` latches and throws on error severity at a controlled point, `sync` adds the synchronisation-validation feature | B's ladder, A's sync rung |
| V-G4 | Diagnostics | `VK_ERROR_DEVICE_LOST` is checked in EVERY configuration and latched AT THE FAULT SITE with the call name and result. The incumbent's `VulkanUtil.CheckResult` is `[Conditional("DEBUG")]`, so a latch built on its shape would never fire in Release, and #427 asks for exactly that latch | A |
| V-G5 | Diagnostics | The debug messenger LOGS and never throws. The incumbent's callback throws a managed exception and calls `Debugger.Break()` from inside a native driver callback, and unwinding through native frames is not a diagnostic. `VK_EXT_debug_utils` only, never the deprecated `VK_EXT_debug_report`. Object names are set so a validation message names a buffer instead of a handle | A's rule, B's mechanism |
| V-G6 | Diagnostics | `GpuDeviceCounters` is populated in full with exactly ONE seam addition, named rather than assumed away: an acquire-wait pair (`AcquireWaitCount` and `AcquireWaitMs`, the shape every other reading on the struct already has) plus its two `GpuTelemetryChannels` names, without which MV2 cannot read its own result. Everything else is already there, already documents that absent is not zero, and is answered by exactly one backend today. `BackpressureStallCount` keeps its member and gains a sentence of doc comment, because here it also counts the command-buffer slot wait | B, one addition named by the judge |
| V-T1 | Tests | The 36 committed `vulkan` goldens run unmodified against the native backend on the same lavapipe rasterizer at the existing 0.06 absolute per-channel tolerance, where zero of the 1728 values IN A GOLDEN GRID may exceed it. No rebake, ever | Both, converged |
| V-T2 | Tests | A device-free native-call budget test aimed at the VULKAN fan-out class through a narrow `IVkCmdSink`, generic-constrained to a struct, covering ONLY the three call classes that scale with draw count: descriptor binds, draws and dispatches, and barriers. Everything else goes straight to `vkCmd*` | B |
| V-T3 | Tests | `NativeVsVeldridVulkanCapabilityParityTests`, both structs in one process on the Linux leg, zero permitted differences | Both, converged |
| V-T4 | Tests | Validation is a CI GATE, in two tiers: `strict` core validation on the scheduled full suite, and `sync` on a SEPARATE, smaller golden-plus-compute job on the schedule only. Installing `vulkan-validationlayers` and enabling the layer is NET-NEW CI WORK, not an env-var flip: no validation layer is present in CI today (verified) | A's gate, judge split and costed |
| V-T5 | Tests | SPIR-V byte equality per program against a checked-in hash, device-free, on every `dotnet test`, understood as a DRIFT detector per V-S2 | Both, converged |
| V-T6 | Tests | The uniform ring's semantic tests run against BOTH backends' rings through one internal test-only interface (V-P5), covering SEVEN of section 9.4's ten policy rows: segment selection, fence gating under pressure, backpressure counting, #484's every-segment reach, its gating, the never-blocks pending-patch queue, and record-time writes staying current-segment. The other three (Ordering, Lock legality, Stride) are each backend's OWN, because their mechanisms differ where the policy does not, and 9.4 names the owner per row. The ring-backed-view invariant is V-M7's creation-time throw rather than a ring semantic, so it is not on either list | B, partitioned by the judge |
| V-T7 | Tests | Device-free barrier-shape, resting-layout, recording-contract and descriptor-invariant tests. The descriptor invariant is the structural one from V-D2 rather than a counter the sink cannot see | B, plus V-D2 |
| V-T8 | Tests | A `vulkan-native` matrix leg on `ubuntu-latest`, golden-only on push, full suite on schedule and dispatch. At 1x billing this is the cheapest leg the program has added, roughly a quarter of the `direct3d11-native` leg per run at the same tier | Both, converged |
| V-T9 | Tests | The `NativeDeviceLifecycle` collection definition is copied into BOTH test assemblies (definitions are per assembly), and the full-suite leg is budgeted at the incumbent's serialised order or worse before anyone reads a first schedule run as a hang | B |
| V-RO1 | Rollout | Five gates, all green before any default flip (section 17) | Both, converged |
| V-RO2 | Rollout | `Vulkan` through Veldrid stays selectable by token INDEFINITELY. It is the kill switch for every STRUCTURAL decision in this design, which is why most bets carry no switch of their own | B |
| V-RO3 | Rollout | The Linux headless default stays on Veldrid until gate 4. The `vulkan-native` CI leg is the continuous exercise | Both, converged |
| V-RO4 | Rollout | EVERY kill switch carries a decision deadline in its Deadline cell, and 2.7's taxonomy decides what the deadline MEANS. A switch that keeps a SECOND IMPLEMENTATION shipping is removed at its named gate and the losing path deleted with it, whichever way the bet went, because a switch that outlives its bet is phase 2's M1 failure, where gate 3 is still blocked behind an unresolved A/B with two drivers shipping. A TUNING KNOB or an OBSERVATION FLAG selects a value or a mode inside one implementation, keeps no second path alive, and MAY survive its gate, in which case its Deadline cell says so and says on what condition | Judge, new |

---

## 2. The contested adjudications

Eight things were genuinely contested, and one thing had to be established before any of them could be decided.
That one comes first, because it is a correction to the evidence base both drafts argued from and it moves two
of the arguments below.

### 2.1 The incumbent, established

Four load-bearing factual claims were checked in the sources before they were allowed to decide anything.
Three survived, two of them with a correction that changes what the design may claim.

**The incumbent is `4.9.103`, and its Vulkan tree is stock upstream `v4.9.0`.** `Directory.Packages.props`
pins `Veldrid 4.9.103`, served from the vendored nupkg. `git diff v4.9.0 v4.9.103 -- src/Veldrid/Vk/` produces
zero lines, and no fork-authored commit has ever touched that directory. So the reference implementation is
2023-era stock Veldrid, warts included, and every citation below is into that tree.

**One draft cited the wrong ref, and it matters.** The idiomatic draft headed itself "the Veldrid fork at
`v4.9.0-30-g485384b`, whose `src/Veldrid/Vk/` tree is byte-identical to upstream `4.9.0`", and verified it with
`git diff origin/master -- src/Veldrid/Vk/`. That diff proves the branch matches upstream MASTER, which is a
different statement. `v4.9.0-30-g485384b` is the master-based branch, and its Vulkan tree carries six
upstream-authored commits since the `v4.9.0` tag. The conclusion is right for the package the engine ships and
wrong for the ref the draft named, which is exactly the shape of error V-I6 exists to stop.

**Record-time `UpdateBuffer` splits the render pass. Verified, and worse than the draft that raised it said.**
The continuity draft's strongest argument was that on Vulkan a record-time uniform write is not a memcpy but a
render-pass split plus a full-device six-stage memory barrier. The split is verified on the shipped line:
`UpdateBufferCore` routes to `CopyBufferCore`, whose FIRST statement is `EnsureNoRenderPass()`, which ends the
pass, transitions the attachments, and emits its own `BOTTOM_OF_PIPE` to `TOP_OF_PIPE` pipeline barrier with
zero memory barriers. The pass then restarts lazily at the next draw through the LOAD variant. **The six-stage
barrier is not on the shipped line.** On `v4.9.0` and therefore on `4.9.103`, `CopyBufferCore` emits a global
`VkMemoryBarrier` with `srcAccessMask = TransferWrite`, `dstAccessMask = VertexAttributeRead`, and `Transfer`
to `VertexInput`. One destination stage. The six-stage form the draft quoted, gated on
`BufferUsage.UniformBuffer`, is an upstream master fix.

That correction makes the incumbent look worse rather than better, and this is the finding neither draft has.
On the shipped package a record-time write into a UNIFORM buffer is followed by a barrier whose destination
access mask is `VertexAttributeRead`, which does not cover `UniformRead` at all. So the write is both
heavyweight and, for the exact usage the engine's per-frame uniform buffers have, under-synchronised. The
argument for the ring therefore stands on stronger ground than the draft claimed, and the claim has to be
restated in the corrected form or the first reader to open `4.9.103` will find the quoted code missing and
distrust the section. Section 9.2 carries the corrected version.

**The instance is 1.0.0 and there is no physical-device selection. Verified.** `VkApplicationInfo.apiVersion`
is `new VkVersion(1, 0, 0)` at two hardcoded sites, one for the real instance and one for the support probe,
and `vkEnumerateInstanceVersion` is never called. `CreatePhysicalDevice` reads `physicalDevices[0]` under the
comment "just use the first one", with no scoring, no device-type preference and no environment override.
Device features are enabled by handing `vkCreateDevice` the entire supported feature struct with no `pNext`
chain. Rendering is classic `VkRenderPass` throughout, which is forced by the 1.0 instance.

**The lavapipe support matrix. Verified, with a ceiling nuance and a drift warning.** On
`cross-platform-gpu` run `30972295121` the `vulkaninfo --summary` step reports instance version `1.3.275`,
device `apiVersion = 1.4.318`, `deviceName = llvmpipe (LLVM 20.1.2, 256 bits)`,
`driverID = DRIVER_ID_MESA_LLVMPIPE`, `driverInfo = Mesa 25.2.8`, `conformanceVersion = 1.3.1.1`. The device
advertises 1.4 and the loader is a 1.3-era `libvulkan1`, and lavapipe's own conformance claim is 1.3. So the
**1.3 core floor is the correct and correctly conservative thing to rely on**, which is what the idiomatic
draft concluded. Its stated reason (an instance cannot be created above the loader's version) is a
simplification of a looser rule, and V-N2's `min(1.3, vkEnumerateInstanceVersion())` cap is the safe form
under any reading, so the mechanism is adopted and the reasoning is corrected. Nothing is pinned: Mesa arrived
through a stock archive update and `ubuntu-latest` will eventually roll off 24.04 and take the loader with it.
These are cited as observed on that run, never as a contract.

**`maxDescriptorSetUniformBuffersDynamic` is a spec claim, not a measurement, and both drafts overreached.**
The Vulkan required minimum is 8 and required minimums are never lowered across core versions, so the floor is
sound. Everything past it is not: one draft asserted AMD reports exactly 8, the other that lavapipe and NVIDIA
both report far higher, and **neither is observable anywhere in this repo**. `vulkaninfo --summary` prints no
limits block, the job log has zero hits for any `maxDescriptorSet*` string, and the incumbent reads exactly
three device limits, none of which is this one. Both assertions are demoted to what they are. V-D7 makes the
question measurable instead, by dropping `--summary` from a step that is already `continue-on-error`, which
costs log volume and nothing else.

**And a fifth thing, checked because both drafts assumed it.** Validation layers are absent from CI entirely.
The repo has zero hits for `VK_LAYER`, `VK_INSTANCE_LAYERS` or `vulkan-validationlayers` across every workflow,
script and source file, and the run's own layer enumeration lists three Mesa and Intel layers with no Khronos
validation among them. `VK_EXT_debug_utils` is available as an instance extension, so a messenger can be wired
with no install, but the LAYER cannot. Both drafts write about turning validation on as though it were a knob.
Section 2.8 prices it as the CI work it is.

### 2.2 The shared home: extract nothing, share the tests

**The two poles.** The continuity draft makes extraction row 1 of the program: move the emitter interface, the
recorder, the dirty model, the flush schedule, the counting emitter, the ring's segment policy, the soak
counters, the liveness latch, the shader blob cache and the rate limiter out of `Gpu.D3D11` into
`KhaozEngine.Gpu/Internal/`, as a pure move plus rename, with phase 2's frozen native-call marginals
(26 fixed head, 4 per distinct mesh, 2 per draw, 1 per visible stage) byte-identical across the commit as the
regression proof. Its case is not line count, which it honestly puts at 1500 to 2000 lines against a Vulkan
backend four to five times that. Its case is that every one of those lines carries a defect a shipped run
already found, and it lists six.

The idiomatic draft extracts nothing and applies the rule of three, answering each candidate: the emitter
interface is already a near-copy of `IGpuCommandList` in engine types, so promoting it makes a SECOND copy of
the seam that both backends must keep in sync, and its purpose in the D3D11 backend is to drive two drivers, a
problem Vulkan does not have. The third dirty state has no observable meaning where a set bind is one call
either way. The op stream does not travel, which phase 2's own section 16 already conceded. The ring's policy
is genuinely identical and its mechanism is not.

**What decides it is the continuity draft's own list.** Take the six lessons one at a time against the design
each draft actually specifies.

The 256-byte constant-count round-up is a D3D11 mechanism whose Vulkan sibling is
`minUniformBufferOffsetAlignment` on a byte offset. Different arithmetic, different failure (a validation error
rather than a silent dropped bind), and no shared IMPLEMENTATION. One constant is shared, the 256 in V-M5's
stride, and even that is derived independently on this side rather than borrowed: 256 is the spec's required
MAXIMUM for `minUniformBufferOffsetAlignment`, so flooring the stride there makes it device-independent instead
of leaving a device-shaped number under a golden-bearing path. A constant with two independent derivations is
not shared code. The framebuffer-change viewport guard is a one-line
emit rule plus a test assertion, and BOTH drafts specify it independently without any extraction. The
WRAP-on-all-three-axes shared sampler pair is already written as a contract on `IGpuDevice.PointSampler`, so it
is carried by the seam rather than by any extracted type. **The pipeline-switch record wipe does not port at
all, and the continuity draft's own schedule says so**: its clause 5 INVERTS the D3D11 rule, and the idiomatic
draft replaces it with layout compatibility. A draft cannot list a lesson as portable and then invert it four
pages later.

That leaves two of six, and they are the same lesson: #484's every-segment off-timeline rule, and the
non-terminating retry loop that was drafted before it. Both are POLICY, both are exactly what the idiomatic
draft proposes to share as TESTS, and a shared semantic test asserts the policy more directly than shared code
does. Shared code proves one implementation exists. A shared test proves both implementations behave.

**The phase-4 argument is what turns a close call into a clear one, and only one draft makes it.** Metal has
real deferred command buffers, real completion callbacks, render pass descriptors with load and store actions
that map onto `VkRenderingAttachmentInfo` nearly one to one, and argument buffers that map onto descriptor
sets. The backend most likely to be the OUTLIER of the eventual three is D3D11. Extracting from D3D11 and
Vulkan today produces an abstraction shaped by the outlier and then asks Metal to fit it. Waiting for Metal
means the common shape is observed rather than predicted.

**Decision.** V-P4 and V-P5. Extract nothing. Share the ring's semantic tests now, through a minimal
test-only interface, so the Vulkan ring must pass the tests #484 was fixed by before it renders a golden.

**Two amendments, because the winning position has a cost it underprices.** First, that shared test interface
is itself an abstraction derived from one implementation, which is the sin the decision is avoiding, just
moved into the test layer. It is a much smaller one (acquire a segment, write off-timeline, read a segment
base, read the stall count) and it is worth paying, but it should be named rather than presented as free. It
also has to be given a HOME, and neither draft gives it one. The home is `KhaozEngine.TestSupport.Gpu`: it
already references `KhaozEngine.Gpu` and `KhaozEngine.Gpu.D3D11`, it gains a `Gpu.Vulkan` reference in row 2
anyway for the `VulkanBackendRegistration` seat, and it is `IsPackable=false` and `IsTestProject=false`, so it
ships nothing and V-P4's rule against a shared PRODUCTION home is untouched. The interface and both adapters
live there, the semantic tests live in the shared ring-test project that references it, and the visibility
this costs is one line: `KhaozEngine.Gpu.D3D11` grants `InternalsVisibleTo` to `KhaozEngine.TestSupport.Gpu`
beside the single `KhaozEngine.Render.Tests` grant it carries today. The Vulkan package carries the same grant
from its first commit. **That is real D3D11-side work and row 8 owns it explicitly**, because "share the
tests" with no adapter on the other side is a decision that quietly becomes one backend's tests. Second, and
this is V-P6:
a decision not to share code is not a decision to re-derive the policy from memory. Section 9.4 writes the
policy inventory out as a checklist the Vulkan ring is implemented against, because the continuity draft is
right that the expensive thing here is the lessons, and the answer to that has to be written down somewhere.
The extraction issue is filed now with phase 4 as its trigger, so it is scheduled debt rather than forgotten
debt.

**What this costs, stated plainly.** Two backends will contain two similar ring allocators and two similar
record-then-flush loops, and a reader will notice. That duplication is cheap and legible. A shared abstraction
whose shape was guessed from one implementation is expensive and illegible.

### 2.3 Dynamic rendering versus the render-pass port

**The two poles.** The continuity draft ports classic `VkRenderPass` plus `VkFramebuffer`, with each
framebuffer owning three render passes exactly as the incumbent does, and rejects dynamic rendering on four
grounds: it does not remove the hard part, it changes pipeline creation to a different compatibility rule, it
raises the machine floor from 1.1 to 1.3, and porting a known-correct thing beats writing a simpler unknown
thing. The idiomatic draft writes no render-pass path at all.

**Ground one is a wash and both drafts know it.** The lazy-begin, queued-clear, restart-on-clear dance is a
consequence of the SEAM's shape, since `SetFramebuffer` and `ClearColorTarget` are separate calls and a clear
can arrive after the bind. It is identical under both models, and the idiomatic draft's V-A2 and V-A3 exist
precisely to pay it. Nobody gains here.

**Ground two is backwards.** Dynamic rendering's compatibility rule is SIMPLER, not different-and-therefore-
riskier: a pipeline created against a set of attachment formats is compatible with any rendering carrying the
same formats, and the classic "pipeline created against render pass A, bound inside render pass B" mismatch
class stops existing. `IGpuFramebuffer.Outputs` is a `GpuOutputDescription` of attachment formats, a depth
format and a sample count, whose formats are `VkPipelineRenderingCreateInfo`'s input verbatim and whose sample
count is the multisample state's. The seam's shape and dynamic rendering's shape are the same shape.

**Ground four collapses under its own premise, and this is what decides it.** The thing being ported is not
known-correct. The incumbent creates THREE `VkRenderPass` objects per `VkFramebuffer` with no cache and no
dedup across framebuffers of identical format, plus one `VkFramebuffer` per framebuffer and one per swapchain
image, all rebuilt on every resize. Doing render passes properly means writing a render-pass cache keyed on
formats, sample count and load and store ops, AND a framebuffer cache keyed on the pass plus the views plus the
size, AND invalidating the second on every resize and every render-target recreation. The continuity draft
ports the UNCACHED shape, which is a per-framebuffer object explosion it then files a follow-up to measure. So
the choice is not "port a correct thing versus write a new thing". It is "port a shape everyone agrees needs
redesigning, or delete both caches and the invalidation problem with them". Porting only wins when there is
something correct to port.

**Ground three is the one real cost and it is small.** The floor moves from 1.1 to 1.3. The CI rasterizer
clears it (2.1), and the field GPU has cleared it since 2022. A Linux desktop machine on an older driver
answers false at V-I4's functional probe and routes through the existing reported fallback to the Veldrid leg,
which is the designed behaviour rather than a regression. What the cost buys is that 1.3 is also where
`synchronization2` is core, and `synchronization2` and `dynamicRendering` together are what the floor is
actually spent on. `timelineSemaphore` is core at **1.2**, so it rides the floor rather than justifying it,
and saying otherwise would credit the move with something it does not buy.

**Decision.** V-A1 through V-A6. No render-pass path is written and no in-backend fallback is built, per
2.7. The incumbent's clear-arrival semantics are ported deliberately, which is the continuity draft's instinct
applied where it is actually right: the clear-versus-load selection is subtle, a golden depends on the
clear-only case, and that behaviour is reproduced rather than re-derived.

**The counterargument that survives.** On a tile-based deferred renderer a render pass carries attachment
lifetime information the driver genuinely uses. Neither Vulkan target is a tiler. A future mobile head would
want that story and would want its own, which is recorded as a follow-up rather than pre-built.

### 2.4 The schedule: two states, and the clause that replaces clause 5

**Three states or two.** The continuity draft keeps phase 2's three-state model deliberately, conceding in the
same clause that "on Vulkan the three states collapse to two at the emitter", and justifying the third state
purely by shared code and shared tests with D3D11. Once 2.2 rules against extraction that justification is
gone, and no independent one survives: `pDynamicOffsets` is POSITIONAL and covers every dynamic descriptor in
the bound run, and every ring-backed uniform's base moves every frame, so the array is recomposed on any bind
regardless. A state that changes no call and skips no work is bookkeeping. Two states, per V-R5.

**Clause 5 is the real fight, and it is the sharpest technical disagreement in the two drafts.** On D3D11,
`SetPipeline` drains pending sets under the OUTGOING layout and then forgets the records, because the layout
decides register numbering. On Vulkan the analogous rule runs the other way: binding a pipeline whose layout is
incompatible with the previous one invalidates bound descriptor sets from the first incompatible set onward.

The continuity draft reproduces the incumbent's BLUNT version, clearing the bound-set arrays wholesale on every
pipeline switch, on the grounds that computing set-layout compatibility exactly is a correctness cliff, and
files the refinement as a follow-up. The idiomatic draft computes it, and pairs it with V-D5: content-dedupe
the `VkDescriptorSetLayout` and `VkPipelineLayout` objects so two pipelines declaring the same layout shapes
SHARE handles, which makes their pipeline layouts compatible and makes the compatibility test a pointer
compare.

V-D5 is load-bearing and the continuity draft has no answer to it. Without dedup, every pipeline gets fresh
layout objects, nothing is ever compatible, and every pipeline switch forces a full rebind of every set. That
is a real per-frame cost the incumbent pays and that the blunt version reproduces by construction rather than
by choice.

**Decision.** V-R6, with the continuity draft's caution converted from a deferral into a guard rather than
discarded. The rule is stated precisely so it can be tested: two pipeline layouts are compatible for set N when
they were created with identically defined set layouts for sets 0 through N and identical push-constant ranges.
Content dedup makes "identically defined" into handle identity, and V-D8 declines push constants, so the
computation is the longest common prefix of the two pipeline layouts' set-layout handle sequences, and every
set at or past that prefix is marked dirty.

**V-R7 is what makes that safe.** A device-free test walks every ordered pair of shipped pipelines and asserts
the computed prefix never exceeds the true prefix of identical handles, so the invalidation can only ever be
conservative. Under `KE_VULKAN_VALIDATION` the draw path additionally asserts that every bound set's layout is
the current pipeline layout's set layout at that index. Neither draft has either check: one deferred the
mechanism to avoid the cliff, the other took the mechanism and left the cliff unguarded.

And the guard earned its keep on the one case neither draft nor this decision states: a switch to a pipeline
declaring FEWER sets. The correction is in 6.2 beside the clause it belongs to (#625). Worth reading here too,
because it is the shape of the miss rather than the miss itself that generalises: both halves of V-R7 check
whether a set SATISFIES the layout it is bound under, and neither draft asked whether the layout has that set
number at all.

### 2.5 One open recording, and why the layout model decides it

**The continuity draft is STRICTER than the D3D11 native backend and gives a concrete reason.** Vulkan image
layout tracking in the incumbent is recording-time mutable state on the TEXTURE: `VkTexture`'s layout array is
read to decide whether a barrier is needed and written to record what the barrier did. Two recordings touching
the same texture read and write the same array, and the loser records either a redundant barrier, which is
harmless, or NO barrier for a transition it needed, which is a corruption no golden on a software rasterizer
will show. So that draft makes exactly one open recording per device the Vulkan backend's own contract, and
notes with some satisfaction that `OpenListTrackingGpuDevice` then becomes real evidence on this leg rather
than a trivially passing guard.

**The idiomatic draft eliminates the hazard instead of constraining around it.** Every texture is assigned a
CANONICAL RESTING LAYOUT at creation from its usage bits. A list assumes every texture is at rest when it
starts, tracks transitions LIST-LOCALLY, and restores the resting layout before `End`. Nothing shared is read
or written during recording, so two lists cannot disagree, and lists become composable in any submit order,
which is what the seam already promises.

This is phase 2's section 2.1 ruling arriving again in different clothes, and it goes the same way: hazard
elimination beats a claim about absence, and beats a constraint adopted to avoid a hazard that a different
model does not have. The continuity draft's restriction is self-inflicted, because it ports the mechanism that
creates the problem it then designs around.

**The costs of the winning model, which the draft that proposed it does not price.** Restoring to rest costs a
handful of extra barriers per list at pass boundaries, bounded by touched textures and independent of draw
count. A texture written in list 1 and sampled in list 2 pays a restore in list 1 and a re-transition in list
2, which is redundant and harmless. A `Storage | Sampled` texture rests at `SHADER_READ_ONLY_OPTIMAL`, so a
dispatch writing it transitions to `GENERAL` and restores, which is exactly the rule 1 case and falls out
correctly. The barrier count is gated (V-T7, MV5) rather than assumed.

**Decision.** V-F7 and V-R4. Layouts are list-local against a canonical resting layout, N lists may record
concurrently on this backend, and the PORTABLE seam contract is unchanged at one open recording per device.
`IGpuCommandList.Begin`'s existing "a backend may be more permissive" paragraph gains a Vulkan sentence.

**And the thing that is lost, recorded so it is not misread.** Under this ruling
`OpenListTrackingGpuDevice` passes trivially on the Vulkan native leg, exactly as it does on the D3D11 native
leg. The continuity draft's version would have made that test evidence, and this one does not. A future reader
seeing it green on the Vulkan leg must not read it as evidence about this backend. That is R4's decay mode and
naming it here is the whole defence against it.

### 2.6 The present path, forced rather than preferred

**Both drafts change the same behaviour, both behind the same shape of switch, and both admit the same zero
coverage.** The incumbent presents, immediately acquires the next image with a FENCE, and blocks the CPU on
`vkWaitForFences` with an infinite timeout inside `SwapBuffers`, while the following submit carries no
image-availability wait semaphore at all and the present carries no wait semaphore either. Both drafts replace
the fence-and-block with a binary semaphore the frame's submit waits on, keep the acquire-at-present timing,
add a render-finished semaphore for the present, and put it behind an environment switch restoring the
incumbent's exact shape.

**Two authors with opposite priors converging is evidence and not proof, and phase 2's W1 is the precedent
that should make a judge suspicious.** There, both drafts proposed a flip-model swapchain, both named the
same counterargument against their own decision (the swapchain is the one area with zero automated coverage
anywhere in the net), and the judge took the edit neither author took. The same conditions hold here with more
force, because the Vulkan golden legs are headless and a headless Vulkan device enables no surface extension at
all, so not one line of the swapchain path runs in CI on any leg ever.

So the question is whether to reject the change for v1 the way W1 rejected the flip model.

**The answer is no, and the reason is not the one either draft gives.** One draft argues the failure modes
differ, that a wrong semaphore produces a validation error and a hang or visible corruption that a five-minute
windowed run finds, where the flip model's failure was subtle presentation behaviour reproducible only on one
machine with one window operation. That is true as far as it goes, and it goes less far than claimed, because
the same draft describes the acquire-reuse bug as manifesting as "an intermittent hang, never as a clean
failure", and intermittent is precisely the class that survives a five-minute run.

The decisive argument is a contradiction neither draft resolves in this direction. **V-T4 makes validation a
blocking CI gate. Presenting with no wait semaphore is not a tuning choice, it is presenting without the
synchronisation the specification requires, and validation flags it.** A design cannot both gate on validation
and deliberately reproduce a configuration validation rejects. One draft states the contradiction and files it
under its own departure list, the other never connects the two decisions. Once the validation gate is adopted
(2.8), the spec-correct present path is FORCED, not preferred, and the kill switch exists to A/B frame pacing
rather than to hedge correctness.

**Decision.** V-W3, with three additions.

The switch is `KE_VULKAN_ACQUIRE=stall` and it carries V-RO4's deadline: it is removed at gate 4 and the
blocking path deleted with it. Note explicitly that `stall` restores a configuration validation rejects, so the
switch and `KE_VULKAN_VALIDATION` are not usable together, which is a documented limitation rather than a bug.

The acquire ring is sized `max(FramesInFlight, imageCount) + 1` rather than either draft's number. One draft
sized it on frames in flight and the other on image count, and acquires are paced by the presentation engine
while recording is paced by the frame loop, so the ring has to clear both.

**V-W4 fills a gap both drafts leave.** Neither says what happens to a frame whose acquire returned
`OUT_OF_DATE`. That frame has no image, so it cannot render. The rule, written out in 11.2: the swapchain
recreate runs at that same present boundary rather than being queued to a later one, the semaphore handed to
the failed acquire is retired by the recreate's unconditional drain rather than being reused, and ONE fresh
acquire follows the recreate before the boundary returns. Without the same-boundary rule the semaphore is
either reused while pending, which is the reuse bug, or destroyed while pending, which is undefined behaviour.
Without the fresh acquire the recording path needs a second "no image yet" state, which is the state the rule
exists to have exactly one of.

### 2.7 Kill switches, deadlines, and the phase-2 lesson

**The two postures.** The continuity draft carries a switch per contested decision: the present path, the
instance model, the uniform ring's routing, the frames-in-flight depth. The idiomatic draft spends per-decision
switches only where the incumbent's shape is reachable cheaply INSIDE the backend, and points at phase 2: "adding
an in-backend fallback path for a structural decision is how phase 2's gate 3 ended up stuck behind an
unresolved A/B with two drivers still shipping".

That citation is correct and checkable against phase 2's own rollout record, where gates 1, 2 and 5 are met,
gate 3 is not, and what it waits on is not a number but the deletion of a driver that a still-shipping switch
keeps alive.

**Sorting the switches by that criterion settles it.** A switch that selects a BRANCH inside one
implementation is cheap. A switch that keeps a SECOND IMPLEMENTATION shipping is the M1 failure.

`KE_VULKAN_ACQUIRE` selects between a semaphore wait and a fence wait in one acquire path. `FramesInFlight` is
a number. Both are branches, both drafts want them, both are kept. The instance-model switch keeps a second
instance-lifetime model alive to hedge a bet about CI flakiness that is better answered by simply leaving
serialisation on and running one experiment, so it buys nothing its bet does not already have and is rejected.
The uniform-routing switch keeps the incumbent's staging-copy upload path shipping beside the ring, which is a
second structural implementation, and the draft proposing it concedes its own exit criterion removes it
regardless. Rejected.

On the largest structural decision there is no two-driver risk either way, because neither draft proposes both
a render-pass path and a dynamic-rendering path. That is the correct posture and it is worth naming as the
model rather than the exception.

**Decision.** V-RO2 as the rule (Veldrid Vulkan by token is the kill switch for every structural decision),
the two branch switches kept, the two implementation switches rejected. **And the amendment neither draft
applies universally, V-RO4: every switch in this design names a gate in its Deadline cell, and the SORT above
decides what the deadline means.** A switch keeping a second implementation shipping is removed at that gate
and its losing path deleted, whichever way the bet went, because a bet without a deadline is not a bet, it is
a permanent fork with optimistic documentation, and that is exactly what phase 2 is currently paying for. A
tuning knob or an observation flag keeps no second path alive, so it may survive its gate, and its Deadline
cell says so and says on what condition. `KE_VULKAN_ACQUIRE` is the first kind and dies at gate 4.
`KE_VULKAN_FRAMES_IN_FLIGHT` is the second kind and may live on as a knob, but only if MV3's exit criterion
was met at its default, which is the condition that stops "it is only a knob" from becoming a way to keep a
failed default.

### 2.8 The validation gate, split and costed

**Where the drafts actually differ is narrower than it looks, and the narrow part is the important part.**
Both make validation a blocking gate on the scheduled full suite: one as a named test decision with an argument
from hazard classes, the other as an environment mode whose `strict` rung throws on error severity and which
the weekly run uses. They converge on the gate. They diverge on SYNCHRONISATION VALIDATION, which one draft
makes central and the other never mentions.

**That difference decides on the hazard class, and the winning argument is the one draft's best paragraph.** A
hand-written Vulkan backend's characteristic failure is a missing barrier or a wrong layout, and both are
invisible on lavapipe, because a software rasterizer executes with far stronger implicit ordering than a real
GPU. The golden passes and the same command stream corrupts on the field GPU, on the one machine that is not in
CI. That is the shape of defect this whole program exists to stop shipping and it is the shape the golden net
structurally cannot catch. Core validation does not catch it either. Synchronisation validation is the only
instrument in the net that does.

**It also pays for two other decisions, which is why it is load-bearing rather than a nicety.** V-M1 declines
VMA partly on the grounds that a hand-rolled allocator's aliasing failures are catchable, and this is what
catches them, so V-M1 is written as CONDITIONAL on this gate. And V-W3's replacement of the incumbent's
semaphore-free present is proved right rather than merely different by the same instrument (2.6).

**Two things neither draft priced, and both change the shape of the decision.**

Synchronisation validation tracks every access and is slow. The Vulkan full suite already runs with xUnit
collection serialisation because of residual lavapipe instability under parallel load, at roughly the
incumbent's serialised order of twenty-odd minutes, and phase 2 measured that adding a second live-device
backend took a leg from 17 minutes to 49 before the lifecycle collection fixed it. Running sync validation over
that whole suite is an unbounded multiplier on a job that is already the slowest in the matrix.

And turning validation on is NOT a toggle. There is no validation layer installed on any CI leg today
(verified in 2.1), so the gate is net-new CI work: install `vulkan-validationlayers`, enable
`VK_LAYER_KHRONOS_validation`, and separately enable the synchronisation-validation feature through
`VkValidationFeaturesEXT`. Both drafts write as though an environment variable does it.

**Decision, against both.** Two tiers, V-T4. `KE_VULKAN_VALIDATION=strict` (core validation, error severity
fails the leg) on the scheduled full suite, which is where a broad sweep is worth its cost. `sync` (core plus
synchronisation validation) on a SEPARATE, smaller job that runs the golden subset plus the compute suite, on
the schedule only, which is where the barrier machinery actually lives and where the instrument earns its
runtime. Warning and performance severities are logged and uploaded as an artifact, never failed, because
performance warnings are opinions. The install work is a named line in the CI row of the work breakdown rather
than an assumed toggle.

### 2.9 Three places the continuity prior wins outright

The rulings above go one way often enough that a reader could conclude the reuse-first prior lost everywhere.
It did not, and these three are worth pulling out of the table, because two of them are the kind of parity
surface that fails on every golden at once.

**Physical device selection.** One draft scores devices (presentation support, then discrete over integrated
over CPU). The other reproduces the incumbent's `physicalDevices[0]` as the DEFAULT and provides explicit
selection by environment variable, on the grounds that changing which physical device the engine runs on is a
user-visible change unrelated to swapping the backend, that it breaks `DeviceName` parity in a design demanding
zero capability differences, and that it puts a second variable into the one gate that must isolate the backend
swap. The scoring buys nothing in CI, where both drafts pin the software device anyway, and nothing on the
reporting machine, which has one GPU. Where it buys something is a laptop with an integrated device at index 0,
which is a real user benefit and a real second variable in the soak. **The default reproduces `[0]`**, filtered
by V-N2's hard requirements with any substitution logged, and preferring discrete becomes a follow-up with its
own change note. V-N3.

**The staging subresource layout.** One draft asserts the incumbent's staging rows are tightly packed and moves
on. The other calls this the highest-risk parity surface in the design, declines to assert what the arithmetic
IS, and specifies reproducing it byte for byte plus a device-free table test over a spread of formats, sizes,
mip levels and array layers, asserted against a checked-in table taken from the incumbent's own computation.
Every golden in the suite reads back through `Map` and `MappedData.RowPitch`, the incumbent computes the whole
subresource layout in software rather than asking `vkGetImageSubresourceLayout`, and a different arithmetic
garbles all 36 at once. Converting "should be identical" into a checked fact before a golden runs is exactly
what S3 did for the emitted HLSL, and it is the better posture whichever assertion turns out to be true. V-C7.

**Result checking in Release.** `VulkanUtil.CheckResult` is `[Conditional("DEBUG")]`, so every `VkResult` check
in the shipped Vulkan backend compiles away. A device-loss latch cannot be built on top of that, and #427 asks
for exactly that latch. Every result is checked in every configuration and the latch records the call name at
the fault site. V-G4.

A fourth, smaller: `Submit(cl, fence)` on the incumbent costs TWO `vkQueueSubmit` calls, one signalling the
caller's fence and a second empty one signalling an internal tracking fence. Collapsing that to one is a
measurable per-frame cost with no defender. V-F3.

---

## 3. Package, layering and the binding

`KhaozEngine.Gpu.Vulkan`, one assembly, referencing `KhaozEngine.Gpu`, `Silk.NET.Vulkan`,
`Silk.NET.Vulkan.Extensions.KHR` and `Silk.NET.Vulkan.Extensions.EXT`. Target `net10.0` with no OS suffix, no
`[SupportedOSPlatformGuard]` and no `NoInlining` bodies. Vulkan runs on Windows and Linux from the same managed
code, the loader is found at runtime, and a machine without one fails the functional probe and routes through
the existing fallback. The whole CA1416 apparatus the D3D11 package needs has no analogue here, and its absence
is a simplification rather than an omission.

Guard work the package creates, all mechanical and all precedented:

- `ArchitectureTests.OptInBackends` gains `Gpu.Vulkan`, which then enforces
  `OptInBackends_AreNotReachableFromAnyUmbrella`.
- `ArchitectureTests.ThirdPartyHomes` gains three `Silk.NET.Vulkan*` keys mapped to `Gpu.Vulkan`, or
  `EveryThirdPartyPackage_IsDeliberatelyMapped` fails.
- `KhaozEngine.slnx` gains the project, which force-adds `KhaozEngine.Tests` to the selective-test set, so the
  architecture guards run on the landing PR.
- `check-doc-versions.sh` requires a bolded `**KhaozEngine.Gpu.Vulkan**` catalog row in the root `README.md`
  and a `KhaozEngine.Gpu.Vulkan/README.md` shipped via `<PackageReadmeFile>`.
- `GpuPublicApiTests` extends its walk to the new assembly, for `Veldrid` and for `Silk` in the public surface.
- The no-Veldrid-edge pair, generalised from the D3D11 originals: a csproj read and an IL reference walk. The
  walk is the load-bearing one, since Veldrid is in the transitive closure through `KhaozEngine.Gpu` whatever
  the csproj declares.
- A new assertion for V-S3: the backend names no cross-compile BACK-END member, only the front end.
- `docs/DEPENDENCY-SEAMS.md` gains the second instance of the out-of-package backend edge.

### 3.1 The binding library (V-P2)

**`Silk.NET.Vulkan`, on the `2.23.0` line the engine already runs.** Both drafts reach this and the reasons
compose.

The dependency FAMILY is already trusted and already load-bearing. `Directory.Packages.props` pins
`Silk.NET.Windowing`, `.Windowing.Glfw`, `.Input`, `.Input.Glfw`, `.GLFW` and `.OpenAL` at `2.23.0`, with a
comment recording that 9.0.0 unified them after Silk.NET drifted apart. A Vulkan binding from the same
generated line adds a package, not a vendor, shares `Silk.NET.Core`, and drifts in lockstep with packages the
engine already upgrades together. It is complete, maintained, and carries the per-instance and per-device
function-pointer loading (`Vk.GetApi`, `TryGetInstanceExtension`, `TryGetDeviceExtension`) that a hand-rolled
binding has to invent, and loader discipline is exactly what the lavapipe race punished the engine for.

The phase-2 precedent is directly on point: that phase took `Vortice.Direct3D11` rather than hand-rolling COM
interop, for a backend whose premise was owning the implementation. Owning the BACKEND and owning the BINDING
are different things, and #420's endpoint is "no Veldrid in the graph", not "no dependencies".

**One claim here is a claim and not a fact, and it is checked first.** `cross-platform-gpu.yml` carries a step
titled "Symlink libdl / libvulkan for Veldrid (Linux)", because Veldrid's Vulkan binding P/Invokes the bare
names `libdl` and `libvulkan` while modern Ubuntu ships only the versioned sonames. Silk.NET resolves through
`Silk.NET.Core`'s native-context search, which includes the versioned soname, so the native leg should need no
symlink step. That is asserted by one draft and it would be expensive to discover in the swapchain row, so it
is the first thing work-breakdown row 1's binding spike verifies, alongside compiling one file against every
API this design needs.

**Rejected: `Vortice.Vulkan`.** A coherent competing bundle with leaner bindings, newer headers and sibling
shaderc and SPIRV-Cross packages that would one day serve the Veldrid.SPIRV exit from one vendor. It loses on
the pin: the two Vortice packages already in the graph are pinned deliberately to what Veldrid depends on so
there is one D3D11 binding in the graph, and `Vortice.Vulkan` is a separate versioning line that would not ride
that pin, so it adds an independent version axis rather than joining an existing one. It loses again on the
shader path, because V-S4 declines to touch SPIRV-Cross here, so the sibling-package advantage buys nothing
now. Recorded as a follow-up rather than dismissed: if row 1's spike finds Silk.NET `2.23.0` missing something
core-1.3 this design needs, `Vortice.Vulkan` is the named replacement and the spike is the decision point.

**Rejected: hand-rolled P/Invoke, which is what Veldrid does.** Thousands of lines of struct definitions where
every mistake is a memory corruption rather than a compile error, the single largest line-count item in a phase
whose bar is parity, buying control with no use.

**Rejected: TerraFX.Interop.Vulkan.** A good thin source-generated binding, and a second unfamiliar vendor for
a smaller assembly with no loader helpers, against a family the engine already ships.

**Rejected: vendoring Veldrid's `Vulkan.*` namespace.** Veldrid-derived code inside the backend built to
remove Veldrid, invisible to every guard that reads package ids.

**The counterargument owed.** Silk.NET.Vulkan is a large assembly, and a backend whose premise is fewer
dependencies taking a big one reads badly. The premise is Veldrid leaving, not dependency count falling, and
the package is opt-in and outside every umbrella, so a consumer that never names Vulkan never loads it. The
same is already true of `Gpu.D3D11` and Vortice.

---

## 4. Selection, identity and wiring

### 4.1 What phase 2 already paid for

`GpuDeviceContext` is already inverted onto `IGpuDevice`. `GpuBackendProviders` and `IGpuBackendProvider`
already exist, with the second constructor, the disposal hook and the capability read off the device. So this
phase adds a REGISTRATION and re-litigates none of the wiring: `KhaozEngineVulkan.Register()`, one public entry
point, called once at consumer startup, no `[ModuleInitializer]`, no reflection.

Three inherited properties, each of which was a decision somebody had to make:

- **`RequiresProvider` is stated NEGATIVELY.** `GpuBackendProviders.IsBuiltIn` lists the four Veldrid-backed
  kinds, so any APPENDED kind is provider-backed by default. `VulkanNative` needs no edit there, and a future
  backend that forgot one fails loudly rather than being treated as built in.
- **`PreflightProvider` fixes the ORDER.** A missing registration throws before the support probe can answer
  false, so a wiring fault can never present as an incapable machine. That is I2's whole content and it is
  already enforced.
- **The test-side seat exists**, as a static constructor on `GpuFactAttribute` in `KhaozEngine.TestSupport.Gpu`,
  fired at xUnit discovery in ANY assembly carrying `[GpuFact]`. A `VulkanBackendRegistration` sibling goes in
  the SAME project. The regression evidence is recorded in that file: when registration lived only in
  `Render.Tests`, all four `MapEditor.Tests` GPU tests threw on the native leg.

`IsSupported()` is a functional probe with real content on this backend: create a throwaway instance, enumerate
physical devices, and check V-N2's four hard requirements, then three more reads, each cheap at the probe and
expensive anywhere later:

- a host-visible `HOST_COHERENT` memory type, which the uniform ring is pinned to (V-M4). The spec requires one
  to exist, so this fails loudly on a device that somehow has none rather than being a gate anything real trips.
- `maxDescriptorSetUniformBuffersDynamic` at or above the count the engine's layouts need, which is 8.3's fourth
  defence and the one of the four that answers for the machine, at runtime, so a machine below the count
  falls back through the reported path instead of throwing partway into a run.
- on the windowed path, a graphics family that presents.

It must never throw. A machine with no loader, no ICD or a pre-1.3 driver answers false and routes
through `AfterFallback` as `FallbackAfterFailure`, exactly as today.

**Corrected in flight (row 4, CI run 31062315211): CREATION consults this probe, and the machine has THREE states
rather than two.** The prose above says "no loader, no ICD or a pre-1.3 driver" as though those were one case,
and row 4's creation path treated them as one: it checked only whether the loader RESOLVED, renaming that single
failure to a `NotSupportedException` about the machine, and let everything after it fall through the ordinary
`VkResult` check. On a plain `ubuntu-latest` runner, which has `libvulkan` and no ICD, the loader resolves and
answers `vkEnumerateInstanceVersion` out of its own version, so nothing knew anything was wrong until
`vkCreateInstance` returned `VK_ERROR_INCOMPATIBLE_DRIVER` and the check raised an `InvalidOperationException`
whose message asserted the probe had answered yes. It had never been asked. The fix is the ordering V-I4 already
implied and nothing enforced: `CreateHeadless` refuses on the probe BEFORE creating, so a machine-level refusal is
always a `NotSupportedException` naming what is missing (the loader, or the driver plus the package that installs
one), a missing REGISTRATION still throws its own exception, and the creation-time `InvalidOperationException`
narrows to the genuinely surprising case its message describes. The probe pays for one throwaway instance per
provider instance rather than per device, memoized on the provider, whose lifetime is the registration's and is
therefore the same lifetime `GpuBackendSelector` invalidates its own cached boolean on.

### 4.2 The `GpuBackendKind` append audit, second time

The audit is a TEST now (`GpuBackendKindAppendAuditTests`), which is the phase-2 dividend and makes this a diff
rather than a re-derivation. Appending `VulkanNative = 5` touches the same thirteen sites and four answer
differently than they did for `Direct3D11Native`.

| Site | `Direct3D11Native`'s answer | `VulkanNative`'s answer |
|---|---|---|
| `GpuDeviceContext.LogThreadingCaps` | Must change, include the native kind | **No change.** It gates on `GpuBackendKinds.IsDirect3D11()`, which correctly excludes Vulkan. There is no `D3D11_FEATURE_DATA_THREADING` analogue to log |
| `D3D11ThreadingProbe.IsApplicable` | Must change | **No change**, same reason. `ThreadingCaps` and `ThreadingProbeFailure` are both null, which the record already documents as "there was nothing to ask" |
| `CreateWindowed` and `CreateHeadless` switch expressions | Must change, the discard arm asked Veldrid for a Metal device | **Rides the existing explicit arm.** Phase 2 replaced the discard with a named throw. Verify the message names the provider registry generically and not Direct3D11 specifically, or the Vulkan failure reads as a D3D11 one |
| `GpuBackendSelector.ToVeldrid` | Explicit throwing arm | Same, one more arm |
| `GpuBackendSelector.TryParseBackend` | Two tokens added | Add `vulkan-native` and `vk-native` |
| `GpuBackendSelector.IsBackendSupported` | Route to the provider's probe | Same. Veldrid cannot answer for it |
| `GpuBackendSelector.ProbeOS` | Unchanged until the flip | Unchanged until the flip. **The flip means something different here**: `ProbeOS` maps Linux to `Vulkan`, so flipping changes the LINUX default, not the Windows one |
| `GpuBackendSelector._windowCandidates` | Unchanged until default-ready | Same. A player does not choose an implementation |
| `FrameCap.Resolve` and `DisplaySettings` | Falls into the uncapped arm, correct by default | Same. Both gate on Metal. Recorded because it is #380's arm |
| `GoldenCompare`'s two filename sites | Both route through `GoldenBackendToken` | Both, mapping `VulkanNative` to `vulkan`. **This site cannot be missed any more**: the switch has NO discard arm and throws with a message naming the decision, and the audit test turns that into a device-free red |
| `VeldridMap.SupportsCompletionFences` | Not an append site | Not an append site, and worth naming: it answers `true` for `GraphicsBackend.Vulkan` already, which is why V-G1 can demand ZERO capability differences where D3D11 had to permit one |
| `VeldridGpuDevice` Metal frame capture | Unaffected | Unaffected |
| `GpuDeviceContext.CreateOrFallBack` | Correct by default | Correct by default, and the reasoning differs. On Linux `ProbeOS` returns `Vulkan` while the request is `VulkanNative`, so they differ and the request routes through the functional probe. A Linux machine whose native creation fails therefore falls back to Veldrid Vulkan and reports `FallbackAfterFailure`, while a missing REGISTRATION still throws. Recorded because the two cases look alike in a log line and the soak depends on telling them apart |

Beyond the table: `GpuBackendKinds.IsVulkan()` is added beside `IsDirect3D11()` (V-I5), because a copy of the
question at each site drifts, and that is the reason the D3D11 predicate exists.

(Corrected 2026-08-06. The enumeration is thirteen rows above and fifteen in fact. Both extras were found by
landing this append rather than by reading the table, which is the point: a site nobody listed is a site nobody
checks. The fourteenth is `GpuBackendProviderMissingException.BuildMessage`, which named `KhaozEngineD3D11.Register()`
unconditionally, so the second provider-backed backend turned it into a message telling a Vulkan tester to
register Direct3D 11. It was fixed by stating the naming convention (`KhaozEngine.Gpu.<Backend>` exposes
`KhaozEngine<Backend>.Register()`) rather than by switching on the kind, so it stops being an append site at all
and degrades correctly for a backend added later. The fifteenth is the token list inside
`GpuDeviceContext.LogSelection`'s unrecognized-override WARN, carried as a literal and missed by BOTH native
appends: it named five tokens while the parser accepted six, and that warning is the only clue a tester gets that
their typo'd `KE_GRAPHICS_BACKEND` did nothing, since the run boots on the OS probe and looks entirely normal. It
reads `GpuBackendSelector.RecognizedTokens` now, one screen from the switch that parses them, with rows in
`GpuBackendKindAppendAuditTests` asserting every listed token parses and every `GpuBackendKind` is listed. Row 19's
audit and the Metal program's append inherit both.)

---

## 5. Instance, device, queue

### 5.1 The instance (V-N1, V-N2, V-N6)

One `VkInstance` for the process, refcounted, created under `GpuDeviceContext._lifecycleGate` and destroyed
when the last device goes, with a lifecycle test that creates and destroys many devices and asserts the
instance is gone. `VkApplicationInfo.apiVersion = min(VK_API_VERSION_1_3, vkEnumerateInstanceVersion())`. The
incumbent hardcodes `1.0.0` at two sites and never calls `vkEnumerateInstanceVersion` at all, which is why
everything past 1.0 has to arrive there as an extension.

Instance extensions: `VK_KHR_surface` plus exactly one of the Win32, Xlib or Wayland surface extensions chosen
from `GpuWindowHandle.Kind`, and only on the windowed path, plus `VK_EXT_debug_utils` under
`KE_VULKAN_VALIDATION`. **The headless path enables NO surface extension**, which is why the whole golden suite
runs on a machine with no display server, and is also why the swapchain has zero CI coverage (section 11).
Layers: `VK_LAYER_KHRONOS_validation` only, only under the validation knob. The incumbent additionally requests
the long-removed `VK_LAYER_LUNARG_standard_validation` and passes layers to `vkCreateDevice`, which modern
loaders ignore.

**Why a single instance is more than tidiness, stated as the hypothesis it is.** The workflow header records
that concurrent device creation raced the Vulkan loader and was fixed by serialising creation process-wide. The
racing operation was `vkCreateInstance` and the loader's ICD enumeration underneath it, and with one refcounted
instance the golden suite's repeated device creation stops touching that path after the first device. That is a
hypothesis about a fixed defect and not a claim to have fixed anything. The lifecycle gate stays regardless, it
also covers disposal, and it is not this backend's to remove. Bet MV7 is how the hypothesis gets tested, at
zero cost, because serialisation stays on by default.

### 5.2 The device and the queue (V-N3, V-N4, V-N5)

**Four hard requirements**, checked by the probe and by device creation: device apiVersion at or above 1.3,
`dynamicRendering`, `synchronization2`, `timelineSemaphore`. All three features are mandatory on a 1.3 device,
so the feature checks are formalities that fail loudly on a 1.2 machine rather than crashing on frame one. The
probe checks three more things beyond these four (a coherent host-visible memory type, the dynamic-uniform
descriptor limit, and a presenting graphics family on the windowed path), and 4.1 lists them, because they are
probe content rather than device-version floor.

**Physical device selection reproduces `physicalDevices[0]` as the default** (2.9), filtered by those
requirements, with any substitution LOGGED so a soak session can tell a substitution from a selection.
`KE_VULKAN_DEVICE` accepts an index, a name substring, or one of `llvmpipe`, `discrete`, `integrated`, `cpu`. A
named device that is not present is a WARN plus the default path, never a hard failure. CI pins `llvmpipe`,
which closes a real integrity hole: the leg relies today on `VK_ICD_FILENAMES` and `VK_DRIVER_FILES` pointing
at lavapipe, a loader-level pin the workflow has already had to repair once when an image moved the ICD
manifest, and the incumbent then takes device zero unconditionally. A device-level pin is the belt to that
brace.

**Features are enabled selectively by name** through the `pNext` chain: `samplerAnisotropy`, `fillModeNonSolid`,
`depthClamp`, `independentBlend`, plus `timelineSemaphore` from the Vulkan 1.2 features struct and
`dynamicRendering` and `synchronization2` from the 1.3 one. `geometryShader`, `tessellationShader`,
`multiViewport`, `drawIndirectFirstInstance` and `shaderFloat64` are READ for capability reporting and not
enabled. The incumbent hands `vkCreateDevice` the entire supported feature struct, which makes the engine's real
dependencies unknowable from the code, so a future device missing one fails at an unrelated call site instead of
at device creation with the feature's name in the message.

**One graphics queue that also presents**, required to be the same family. On the headless path there is no
presentation requirement and any graphics-capable family serves.

Rejecting a separate present family is a real decision, so here is the argument. The incumbent supports
graphics not equal to present and its support is broken: the queue-create loop writes the graphics family index
for every entry instead of the loop variable, so a device that actually needed two families gets two identical
create-infos, which is a spec violation validation flags. The configuration has never worked in this fork and
nobody noticed, which is evidence about how often it is reached. Every desktop driver the fleet targets exposes
a graphics family that presents. A device that does not is rejected by the probe with a named reason and routed
through the reported fallback to Veldrid, whose support for that case does not work either but which is at
least the incumbent behaviour. Building and testing a cross-family ownership-transfer path for a configuration
nobody can produce is unbounded work with no consumer, and getting it wrong silently corrupts presentation.

No transfer queue and no async compute. Both are real Vulkan wins on paper and both need queue-family ownership
transfers, a second submit lock and cross-queue semaphores, for a renderer whose uploads are a few megabytes at
load time and whose compute is one FFT chain already gated by rule 2. Filed as a follow-up with the FFT ocean
named as the consumer that would justify it first.

---

## 6. Command recording

### 6.1 The list, the pools, the buffers (V-R1 to V-R4)

`VulkanCommandList : IGpuCommandList`, calling `vkCmd*` at record time. **There is no op stream, no second
driver, no `KE_VULKAN_RECORD` and no M1-analog A/B**, and that is worth stating as a decision rather than an
omission. Phase 2's section 16 predicted it exactly: the CPU op stream was a D3D11-specific adapter that
existed because D3D11's immediate context has no usable deferred recording, and a `VkCommandBuffer` between
`vkBeginCommandBuffer` and `vkEndCommandBuffer` IS an engine-invisible op stream the driver encodes into its own
format. Recording into a managed array first means encoding twice, allocating once more, and moving the
driver-side encode inside the submit lock, which is the one serialised point in the frame.

**The largest unproven bet in phase 2 is simply absent here.** M1 needed two complete drivers, a kill switch,
an end-to-end A/B and a milestone that still gates phase 2's rollout gate 3. Phase 3 has none of it.

Each list owns `FramesInFlight` `VkCommandPool`s, each with one primary `VkCommandBuffer` allocated at
construction, and a parallel array of the timeline value each was last submitted at.

- `Begin()` advances to the next slot, waits on that slot's recorded timeline value (a `vkWaitSemaphores` that
  returns immediately in the steady state and is counted as backpressure when it does not), calls
  `vkResetCommandPool`, then `vkBeginCommandBuffer` with `ONE_TIME_SUBMIT`, and resets the recorder's tracked
  state: framebuffer, both pipelines, both dirty arrays, scissor, and the list-local layout map.
- `End()` calls `vkEndCommandBuffer` and seals, after restoring every touched texture to its resting layout
  (V-F7).
- `Submit` is the device's, and records the signalled timeline value back into the slot.

**Pool per slot, not one pool with `RESET_COMMAND_BUFFER`.** The incumbent creates one pool per list with the
reset-command-buffer flag, which tells the driver every buffer must be individually resettable and pushes it
onto a slower per-buffer allocator. Resetting the whole pool is the documented fast path and returns memory to
the pool's arena in one operation. The cost is three pool objects per list instead of one. The depth is
`FramesInFlight` and not `FramesInFlight + 1`, deliberately: the uniform ring gates on the same depth, so a
deeper command-buffer ring is dead capacity behind a shallower gate, and one number governing both is one
number to move if MV3 says 3 is wrong.

**The shared number is not a shared index, and conflating them is the mistake available here.** The pool slot
is PER LIST and advances on every `Begin`. The ring segment is PER FRAME and advances at the frame boundary. A
list begun twice in one frame therefore takes two different pool slots while both of its records write the
SAME ring segment, which is correct in both directions: two records must not share a command buffer that is
still in flight, and two records in one frame must see one frame's uniform values. A list begun more times per
frame than `FramesInFlight` wraps onto its own oldest slot and waits on that slot's recorded timeline value,
which is real backpressure and is counted as such.

**Why this gives real N-concurrent recording.** A `VkCommandPool` and every buffer allocated from it are
externally synchronised, one thread at a time. Per-list pools mean two lists recording on two threads never
touch the same pool. Combined with list-local layout tracking (V-F7), nothing shared is read or written during
recording at all. This is the property the D3D11 stream buys by touching no device state, obtained here from the
API's own threading model plus the barrier design. It holds for a reason a reader has to know rather than one
they can see, which is why V-R4 carries a documentation obligation into `Begin`'s XML docs and the package
README.

**Disposal while in flight.** A list disposed with submissions outstanding cannot destroy its pools. It records
its highest submitted timeline value and hands its pools to the device's retire list (V-F9), destroyed when the
timeline passes it. The incumbent uses a refcount, which also works and which this design does not need because
the retire list exists for resources anyway.

### 6.2 The schedule (V-R5, V-R6, V-R7)

1. `SetGraphicsResourceSet(slot, set)` and its dynamic-offset overload RECORD ONLY, into a per-slot array of
   `(set, engineDynamicOffset)`, marking the slot dirty when either differs from what is recorded. Two states,
   not three (2.4).
2. `Draw`, `DrawIndexed` and `Dispatch` flush every dirty slot through the pre-command hook, then issue.
3. The flush emits ONE `vkCmdBindDescriptorSets` per CONTIGUOUS RUN of dirty slots, with `firstSet` at the run's
   start, carrying `pDynamicOffsets` for every dynamic descriptor in those sets in set-then-binding order. A
   full activation of the engine's four-set shapes is one call and an offsets-only rebind of one set is one
   call.
4. `SetPipeline` binds the pipeline and invalidates recorded slots from the first INCOMPATIBLE set onward
   (V-R6). Two pipeline layouts are compatible for set N when they were created with identically defined set
   layouts for sets 0 through N and identical push-constant ranges. V-D5's content dedup makes "identically
   defined" into handle identity and V-D8 declines push constants, so the computation is the longest common
   prefix of the two layouts' set-layout handle sequences, and every set at or past it is marked dirty. A rebind
   of the pipeline already current does nothing, which is the fork's pipeline-identity guard and is kept.
5. A slot whose recorded set has gone null is skipped.
6. Repeated dirty marks between two draws collapse to one flush, which falls out of an array of slots rather
   than a list of binds. Phase 2's rule 8 is the same requirement for the same reason: the shadow pass does
   thousands of offsets-only rebinds of one set per frame, and an O(rebinds) record is an O(n squared) frame.

**Corrected after the fact (#625, caught by V-R7's own draw-time assertion on the vulkan-native leg): clause 4
says what a switch INVALIDATES and clause 3 needed to say how far a flush REACHES.** The prefix is the longest
common prefix of the two handle sequences, so it is bounded by the SHORTER of the two, and a switch to a pipeline
declaring FEWER sets therefore answers the shorter length and marks every set past it dirty. Those marks are
right, because those sets really are disturbed. What was missing is that they are also unbindable: a set number
the current pipeline layout has no entry for cannot be named at all, since `vkCmdBindDescriptorSets` requires
`firstSet` plus the set count to stay inside the layout's `setLayoutCount`. The flush walked every recorded slot,
so the shipped transition from the two-set GPU-skinned model pipeline to a one-set `PixelPostProcess` pipeline
put the stale material set into set 0's run and emitted a call the layout could not carry. Nine tests on the
vulkan-native leg ended at the assertion, which read the state correctly and named the wrong cause: its message
offers "the prefix was too long and left a set marked CLEAN", and the set was dirty. The rule now reads "the
flush emits one call per contiguous run of dirty slots, up to the set count the bound pipeline layout declares",
and the slots past that count KEEP their marks rather than going clean, so the next layout that declares them
rebinds a set the caller never re-recorded. Clearing them instead would leave a draw after the post pass reading
a descriptor slot the post pass disturbed, which is the same defect with nothing to catch it.

**And the same limit answers the OTHER walk over these records (#626), which is where the correction above was
half applied.** Row 15's per-command bound-image walk reads the same per-slot array to put each bound set's
images into the layout its binding needs (V-C1), and it kept walking every recorded slot after the flush stopped
walking them. That is not an invalid call, so nothing catches it: it is work for an image no shader on the bound
pipeline can read. Where the image was already resting where the binding wanted it the tracker emitted nothing,
which is why the shipped post chain showed no extra barrier and why this cost nothing observable. Where it was
not, the draw moved the image out of the layout its real consumer wants and the consumer moved it back, and in
the sharp shape, a dropped set naming a `RenderTarget | Sampled` image the pass begin itself moves, the walk was
owed a transition the instant the pass reopened, so the draw ended the pass, transitioned, reopened, and the
begin put the attachment straight back, at EVERY draw of that pass rather than once. Both the emitting walk and
the ask that decides whether the pass is ended now stop at the declared count, identically and on purpose: a
question reaching further than the walk it gates would end passes for nothing, and one reaching less far would
leave a barrier to be recorded INSIDE an open render pass instance. The bound is DECLARED and emphatically not
dirty, which the surviving clause here is about from the other side: dirty is what owes a bind, declared is what
can be bound at all, and a set bound before a dispatch still owes its rule 1 transition at the next draw without
owing a bind.

**The dynamic offset array is where a subtle mistake lives.** `pDynamicOffsets` covers every dynamic descriptor
in every set being bound by that call, in set order then binding order, and it is POSITIONAL. Bind a run of
three sets and the array must carry an entry for each dynamic descriptor in all three, in order, including ring
bases for uniform buffers the caller never named. Each entry composes as
`ringBase(buffer, currentFrame) + rangeOffset + (isTheDeclaredDynamicElement ? engineDynamicOffset : 0)`. Only
the `ringBase` term is guaranteed a multiple of `minUniformBufferOffsetAlignment` by V-M5's stride. The
`rangeOffset` and `engineDynamicOffset` terms hold that alignment in practice because every shipped slot size is
itself 256-aligned, an invariant the renderers already obey rather than a consequence of the stride. A
device-free test asserts the composed array for every shipped layout shape, because an off-by-one here reads
the wrong slice of the right buffer, which renders plausible garbage rather than throwing.

**The incumbent's own bug here is not inherited.** Its batching flush resets the batch count and first-set but
NOT the accumulated dynamic-offset count, so a second batch within one flush passes a too-large count built from
stale entries. The budget test's invariant, that the dynamic-offset count passed equals the sum of the batch's
sets' dynamic descriptors, is what pins that.

### 6.3 The interposition point (V-T2)

The device-free native-call budget test needs a seam, and Silk.NET's generated bindings are non-virtual. The
seam is a narrow `IVkCmdSink`, generic-constrained to a struct so the JIT monomorphizes it away exactly as the
D3D11 emitter is, covering ONLY the three call classes that scale with draw count: descriptor binds, draws and
dispatches, and barriers. Clears, copies, mip generation, resolves and the rendering begin and end pair go
straight to `vkCmd*` with no indirection, because nothing about them scales per draw and freezing numbers over
them would gate on figures nobody should gate on.

**Corrected in flight by row 12: the rendering class does get a line, and it is a DIFFERENT one.** "No
indirection" above is a statement about the BUDGET, and taken literally it collides with 7.2's own requirement
that the negative viewport height be asserted by a device-free test: Silk.NET's bindings are non-virtual, so an
emission is observable only where there is a line to interpose on, and asserting the pure function instead tests
the arithmetic rather than the emission, which is exactly the failure mode 7.2 describes (the arithmetic being
right and the call site being wrong look identical from a green suite). So the begin, the end, the two
dynamic-state setters and the two clear shapes sit on their own `IVulkanRenderApi`, a plain `ulong`-handle
interface in the shape of `IVulkanCommandApi` rather than a generic-constrained counting sink. `IVkCmdSink` is
untouched, no marginal is frozen over anything on the new seam, and row 11's pin that the budget seam names no
viewport, no scissor and no begin still passes unchanged. The budget means exactly what it meant before.

**Aiming it at D3D11's call classes would have been the mistake.** D3D11's #418 defect was one native call per
resource per stage, because that API binds resources. Vulkan binds SETS and the resources went into the set at
creation, so a full activation is one call. The Vulkan fan-out class is a completely different animal: per-draw
descriptor set ALLOCATION, per-draw `vkUpdateDescriptorSets`, and per-draw barrier emission. A budget test
ported from D3D11 would pass green while a Vulkan backend allocated a descriptor set per draw.

**And the sink cannot gate the invariant that matters most, which is why V-D2 exists.** "Zero
`vkAllocateDescriptorSets` and zero `vkUpdateDescriptorSets` between `Begin` and `End`" is the Vulkan #418
protection, and neither of those is a sink call, so no counting seam sees them. The enforcement is structural
instead, in the shape X1 used on D3D11 where the absence of a `Create*` member made draw-time creation a compile
error: the descriptor pool is not reachable from the recording type, asserted by an architecture test over the
type graph, and the device-free harness additionally runs every shipped scene shape against a fake pool whose
allocate and write counters must both read zero. **V-M11 applies the same shape to image views** for the same
reason and on X1's own evidence, so the one architecture test covers both unreachability claims (9.3).

### 6.4 What is not here

No secondary command buffers. The seam has no sub-list concept and multi-threaded recording is not a shipped
feature. No `vkCmdDrawIndirect`: the seam has no indirect draw and adding one has no consumer. No pipeline
barriers on the per-draw path, and their absence is a gated invariant (V-T2) rather than an aspiration.

---

## 7. Rendering, clears and the viewport

### 7.1 The deferred begin (V-A1 to V-A4)

State per list: the bound framebuffer, a pending-clear value per attachment, and whether rendering is currently
begun.

- `SetFramebuffer(fb)`. If rendering is begun, end it. If the outgoing framebuffer had pending clears and no
  draw happened, force a begin and end pair to flush them (V-A3). Record the new framebuffer, clear the pending
  array, mark viewport and scissor for emission (V-A5).
- `ClearColorTarget(i, rgba)` and `ClearDepthStencil(d)`. If rendering has not begun, store the value as
  pending, which becomes `loadOp = CLEAR` with that clear value. If rendering HAS begun, emit
  `vkCmdClearAttachments` immediately, which is what the incumbent does in the same situation.
- First draw. Begin rendering: per attachment, `loadOp = CLEAR` with the pending value if there is one, else
  `loadOp = LOAD`, and `storeOp = STORE` always (V-A6). Transition every attachment to its attachment layout
  first (section 10.3). Emit viewport and scissor if marked. Then the draw.
- `End()`, or any command illegal inside a render pass instance (V-A4): end rendering, flushing pending clears
  through a begin and end pair if there were any and no draw came.

**The clear-only case is reproduced deliberately, not inherited by accident.** `SetFramebuffer` plus a clear
plus `End` with no draw between them must still clear, because the incumbent forces it at two sites and a golden
depends on it. Under a deferred begin that is a begin and end pair with no draws, which is the one place the
deferral needs an explicit flush rather than falling out of the schedule.

`storeOp = DONT_CARE` for depth is rejected. It leaves contents undefined, undefined is not stable across runs,
and the goldens require stability on the same rasterizer (V-F8's rule applied to a store). If a measurement ever
justifies it, it needs its own change with its own determinism argument.

**Pipelines carry `VkPipelineRenderingCreateInfo` built from `GpuOutputDescription`**: the colour format array
and the depth format. The same description's sample count rides
`VkPipelineMultisampleStateCreateInfo.rasterizationSamples` instead, because the rendering create-info has no
sample-count field. Everything except viewport and scissor is baked into the pipeline object,
which is the incumbent's shape and is kept. Dynamic state is exactly viewport and scissor.

### 7.2 The viewport, and the single most consequential line in the design (V-A5)

There is no `SetViewport` on the seam. The engine gets a viewport because Veldrid's base
`CommandList.SetFramebuffer` auto-calls `SetFullViewports()` and `SetFullScissorRects()`, wrapped in an
`if (_framebuffer != fb)` identity guard. **Both halves must be reproduced.** A backend that does not emit
rasterises nothing. A backend that emits UNCONDITIONALLY diverges on the shipped sequence `SetFramebuffer(fb)`,
`SetScissorRect(...)`, draw, `SetFramebuffer(fb)`, draw, where the second bind silently restores the full
scissor and the second draw renders outside the intended rectangle. That is golden-visible, and phase 2's first
spec froze the wrong behaviour into its tally test, which would have made the test certify the bug.

**And the viewport carries the clip-space flip.** `VkViewport { y = y + height, height = -height }` is what
makes Vulkan's clip space match D3D's, which is why the incumbent reports `ClipSpaceYInverted = false` when
`VK_KHR_maintenance1` is enabled, and `GpuClip.Correct` negates clip-space Y only when that flag is set, so the
engine's matrices assume the flip. Negative viewport height is core in Vulkan 1.1, so at the 1.3 floor it needs
no extension and no conditional. **Getting this wrong does not throw. It renders every golden upside down**, and
it is asserted three ways: by the capability parity test, by all 36 goldens, and by a device-free test that the
emitted viewport height is negative.

No `SetViewport` member is added to the seam. Phase 2 counted 48 `SetFramebuffer` sites and zero viewport sites,
and that has not changed. It remains a reasonable phase-4 addition when the seam is being revisited anyway.

---

## 8. Descriptors

### 8.1 The mapping is already one to one

The seam was designed against a Vulkan-shaped API and it shows:

| Seam | Vulkan |
|---|---|
| `IGpuResourceLayout` | `VkDescriptorSetLayout` |
| `IGpuResourceSet` | `VkDescriptorSet`, allocated and written at creation |
| the pipeline's layout array | `VkPipelineLayout` |
| `SetGraphicsResourceSet(slot, set)` | `vkCmdBindDescriptorSets(firstSet: slot, ...)` |
| `SetGraphicsResourceSet(slot, set, dynamicOffset)` | the same call's `pDynamicOffsets` |
| `GpuResourceLayoutElement.Dynamic` | `UNIFORM_BUFFER_DYNAMIC` |

`CreateResourceSet` allocates one set and issues one `vkUpdateDescriptorSets` covering every binding, then never
touches it again. Binding index equals element index, `descriptorCount` is always 1, sampled images bind
`SHADER_READ_ONLY_OPTIMAL` and storage images bind `GENERAL`. Separate `SAMPLED_IMAGE` and `SAMPLER`, never
`COMBINED_IMAGE_SAMPLER`, which the GLSL sources already assume by declaring `texture2D` and `sampler`
separately. Structured read-only and read-write both map to `STORAGE_BUFFER`.

**That is what the incumbent already does, and saying so matters.** Both drafts present the immutable
write-once set as their invariant. It is a port. What is new is the enforcement (V-D2) and the fact that it now
holds by construction rather than by the incumbent happening to be written that way.

### 8.2 Pools (V-D3)

A list of pools created with `FREE_DESCRIPTOR_SET`, sized from the demand seen so far rather than the
incumbent's fixed `maxSets = 1000` with 100 descriptors of each of seven types, whose per-type ceiling is
reached long before its set ceiling. Allocation walks pools by remaining per-type budget and appends a pool
sized to at least the failing request when none fits.

**Freeing restores EVERY counted type.** The incumbent's free path restores five and forgets
`UniformBufferDynamicCount` and `StorageBufferDynamicCount` (verified, present in `v4.9.0` and unchanged
upstream), so an application that churns dynamic-offset resource sets leaks pool budget until a new pool spawns.
This engine's resource sets are overwhelmingly dynamic-offset ones and the map editor churns them on every
document load, so the leak is aimed squarely at this consumer. **It binds harder here than there**, because V-D4
makes far more descriptors dynamic than the incumbent does. A unit test allocates and frees in a loop and
asserts the pool count does not grow.

### 8.3 Dynamic offsets carry the ring base (V-D4, V-D6, V-D7)

Every `UniformBuffer` element in every layout becomes `UNIFORM_BUFFER_DYNAMIC`, not only the one the engine
declared dynamic, because the per-frame ring base has to be applied at bind and the only bind-time knob Vulkan
offers on a uniform buffer is the dynamic offset. The declared flag then decides exactly one thing: whether the
caller's own offset is added on top for that element. The seam's "at most one declared-dynamic element per set"
rule is unchanged and unaffected. This is what preserves `CreateResourceSet`'s pinning across its 68 call sites,
for the same reason U3 holds on D3D11: the `IGpuBuffer` identity never changes and the base is never baked into
a set.

**The limit this spends.** `maxDescriptorSetUniformBuffersDynamic` has a Vulkan required minimum of 8 across a
whole pipeline layout, and required minimums are never lowered across core versions. Beyond that floor, nothing
about real device values is verifiable from this repo (2.1), so no claim is made about what lavapipe, NVIDIA or
AMD report. Four defences, in order of how early they fire:

1. Only `UniformBuffer`-usage buffers are ring-backed, so a storage buffer never becomes dynamic.
2. A DEVICE-FREE test computes the dynamic uniform descriptor count for every pipeline the renderers declare and
   asserts it is at most 8, so a layout that would break a minimum-spec device fails on the free Linux leg
   rather than on a player's machine.
3. Pipeline-layout creation counts them and throws a named exception above the device's actual limit.
4. `IsSupported()` reads the limit, so a device below the count the engine needs answers false rather than
   crashing.

**And the limit becomes measurable (V-D7).** The CI `vulkaninfo` step drops `--summary`, which makes it dump the
full `VkPhysicalDeviceLimits` block on every Vulkan run. The step is already `continue-on-error`, so the cost is
log volume, and the gain is that the next design resting on a device limit rests on a number somebody can read
rather than on a spec floor plus an assumption.

### 8.4 Declining descriptor indexing, against the idiomatic grain (V-D8)

Descriptor indexing is core in 1.2 and the CI rasterizer clears it, so the support matrix does not decide this
and the decline needs an argument rather than an omission.

**There is no consumer.** Bindless exists to remove per-material descriptor set switching from renderers that
bind hundreds of distinct material sets per frame. This engine's per-frame binding traffic is dominated by
OFFSETS-ONLY rebinds of ONE set, which already cost one call each and which bindless does not improve. Phase 2's
measured D3D11 shape says the same thing from the other side: 2 calls per draw, 4 per distinct mesh.

**Every route to it changes the GLSL**, which is the SHARED source for D3D11 and Metal too, so it puts all three
backends' pixels in play at once. That is the exact risk shape phase 2 refused for SPIRV-Cross direct bindings
and it is refused here for the same reason. It would also weaken V-S1's byte-identical-SPIR-V claim, which is
the strongest parity argument in this design and survives only while the shaders do not change.

**Push constants** are declined on the same grounds plus one more: the seam has no push-constant concept, so
using them means either inventing seam API with one backend behind it or having the backend silently promote
some uniform buffer to push constants and diverge from what the other two do. Both are worse than the dynamic
offset that already works. Their absence is also what makes V-R6's compatibility computation a pure set-layout
prefix compare.

**The trigger that reopens it**, so this is a decision and not a permanent no: a consumer needing per-draw
material variety beyond one dynamic offset, which today means a texture-array atlas the splat terrain cannot
express. Filed with that trigger named.

---

## 9. Memory and the uniform ring

### 9.1 The allocator (V-M1 to V-M4)

An engine-owned block suballocator. Chunks of a fixed size, one `vkAllocateMemory` each, pooled by
`(memoryTypeIndex, linear|optimal)`. First-fit over a sorted free list with alignment correction, split on
allocate, merge with neighbours on free, one short lock around allocate and free because allocation is not on
the hot path. Dedicated allocations when the driver reports that it prefers or requires one, or above a size
threshold. Host-visible chunks are mapped once at creation and never unmapped, so every host-visible allocation
has a stable pointer for the chunk's life.

**Separate linear and optimal pools instead of granularity rounding.** Buffers and images may not share a
`bufferImageGranularity` page. The incumbent rounds every non-dedicated request up to a multiple of that
granularity and shares chunks, which is correct and wasteful, and its rounding adds a granule even when the size
is already aligned. Separating the pools by tiling makes the constraint structural and removes the arithmetic.

**Flush and invalidate when coherence is absent.** The incumbent has no `vkFlushMappedMemoryRanges` or
`vkInvalidateMappedMemoryRanges` anywhere and rests entirely on a `HOST_COHERENT` type existing. Every desktop
driver provides one, so this has never bitten, and it is a few lines to be correct rather than lucky. Coherent
types are preferred, and cached types are preferred for readback staging, which is where a non-coherent type is
actually reachable and where the invalidate is therefore real code rather than a defensive branch.

**The ring is the one place this is a requirement rather than a preference (V-M4).** 9.2's whole no-barrier
argument rests on the uniform ring's memory being coherent, so the ring asks for a host-visible `HOST_COHERENT`
type and nothing else, and `IsSupported()` answers false on a device that reports none. The spec requires such
a type to exist, so this fails loudly on a device that cannot happen rather than gating one that can, and the
alternative (a per-frame flush over every written segment range before every submit) would put back exactly the
per-frame work the ring exists to remove. Preference elsewhere, hard requirement here, and 9.2 may then say
"no flush is required" as a fact.

**Rejecting VMA, and the condition attached to the rejection.** VMA is a C++ library with no maintained managed
binding, so the real proposal is a native binary per RID in the package, which is the exact bundling burden
`libveldrid-spirv` already imposes and that #420 exists partly to reduce, added to a backend whose premise is
reducing native surface. The workload has no allocation problem to solve: meshes and textures allocate at load,
uniform rings allocate once at creation, and V-D1 and V-M5 together mean the steady-state frame allocates
NOTHING. The Vortice analogy does not carry either, because Vortice is a BINDING (mechanical, generated, no
policy) while VMA is a POLICY ENGINE, so taking a maintained binding and declining a policy engine for a
workload with no policy problem are consistent positions rather than opposite ones.

**The counterargument owed is that hand-rolled allocators are where memory corruption lives, and the failure
mode is an aliasing bug no test on lavapipe will show.** The answer is the port target's readability (the
incumbent's allocator is a few hundred diffable lines and this is a corrected version of the same shape) plus
V-T4's synchronisation-validation gate, which is the instrument that sees aliasing and hazard errors a golden
cannot. **That linkage is a decision, not a remark: if the sync gate is ever dropped, the VMA decline must be
re-argued.** MV6 is the falsifying measurement.

### 9.2 The ring, and the corrected argument for it (V-M5 to V-M8)

Every `UniformBuffer`-usage buffer is one `VkBuffer` of `stride * FramesInFlight` in host-visible, coherent,
persistently mapped memory (V-M4 pins the type and the probe fails a device reporting none), where
`stride = align(size, max(256, minUniformBufferOffsetAlignment))`. `FramesInFlight = 3`.

- A record-time `UpdateBuffer(buffer, offset, data)` is `memcpy(mapped + frameBase + offset, data, n)`. No
  staging buffer, no `vkCmdCopyBuffer`, no memory barrier, and **no render-pass split**.
- Every bind of a ring-backed uniform descriptor supplies `frameBase + rangeOffset + callerDynamicOffset` as its
  `pDynamicOffsets` entry, composed in the flush's array (6.2).
- The descriptor is written ONCE at set creation with `offset = 0` and `range` equal to the BIND WINDOW:
  `GpuBufferRange.Size` where the set was created from a range, and the buffer's own logical size where it was
  created from a bare buffer. `VK_WHOLE_SIZE` is deliberately not used, because a whole-size range combined
  with a dynamic offset addresses past the end of the buffer.
- **And the range is NOT the stride**, which is the shape that looks safe and is not.
  `VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979` requires the effective offset plus the range to stay
  within the buffer, and the effective offset here is `frameBase + rangeOffset + callerDynamicOffset`. At the
  last frame slot `frameBase` is `(FramesInFlight - 1) * stride`, so a range of `stride` overruns the buffer by
  exactly the caller's own offset the moment that offset is non-zero. It is non-zero in five shipped
  renderers: `ShadowMapRenderer` passes `cascade * CascadeSlotBytes` (and `slot * SkinnedDepthSlotBytes` on the
  skinned path), `ModelRenderer` `slot * SkinnedMainSlotBytes`, `WaterRenderer` and `OverlayMeshRenderer` a
  per-plane and per-draw slot, and `SpriteBatch` its view-projection slot. Every one of those sets is created
  from a `GpuBufferRange(buffer, 0, slotBytes)`, so the window is already on the seam and the descriptor takes
  it verbatim. The invariant the ring owes is then
  `rangeOffset + callerDynamicOffset + range <= stride`, which keeps the whole sum inside the buffer at every
  frame slot by construction. **This is the same invariant an unringed buffer already obeys**, since a windowed
  bind with a dynamic offset must stay inside the buffer on any backend. The ring adds `frameBase` to the
  offset and `stride` to the ceiling, and the arithmetic is otherwise untouched.
- Frame N uses segment `N % FramesInFlight`. Before handing out a segment the allocator checks the completion
  value the frame that last owned it recorded and blocks if it has not been reached, counting the stall.

**Why the ring is worth as much here as on D3D11, in the corrected form (2.1).** The obvious reading is that
Vulkan has no D3D11-shaped problem, because its `UpdateBuffer` records a copy instead of blocking the CPU. That
reading is wrong. On the shipped incumbent, `UpdateBufferCore` takes a staging buffer from a per-list pool,
memcpys into it, and calls the copy path, whose FIRST statement ends the active render pass. Ending it
transitions the attachments and emits a `BOTTOM_OF_PIPE` to `TOP_OF_PIPE` pipeline barrier with zero memory
barriers, which is a full pipeline flush. Then the copy. Then a GLOBAL `VkMemoryBarrier`. Then the next draw
lazily re-begins the pass through the LOAD variant.

So a record-time uniform write on Vulkan is a **render-pass split plus a full pipeline flush plus a global
memory barrier**, not a memcpy. Structurally it is the same defect class as the D3D11 one, a per-frame uniform
write that is a heavyweight operation instead of a copy into mapped memory, presenting differently: on D3D11 as
CPU stalls, on Vulkan as lost GPU overlap and lost render-pass state. It presents as "still the engine's fastest
backend" rather than as a field defect precisely because two releases of renderer-side engineering already
hoisted most of these writes out of the frame. #408 enumerated the residue (one partial write per water plane,
per overlay-mesh draw, per SpriteBatch slot, and one per splat material) and 17.38.0 packed it away, so what is
left on this backend is one whole-buffer write per block per frame rather than a run of them.

**And the global barrier on the shipped line does not cover uniform reads at all.** Its `dstAccessMask` is
`VertexAttributeRead` and its destination stage is `VertexInput`. A record-time write into a UNIFORM buffer is
therefore not merely expensive, it is under-synchronised for the usage the engine's per-frame uniform buffers
have. The six-stage `UniformRead` form quoted by one draft is an upstream master fix that the shipped `4.9.103`
does not carry. Under V-M5 through V-M8 all of it becomes a memcpy into a persistently mapped segment: zero
recorded commands, zero barriers, zero render-pass splits, and the bind picks the segment up as a field in an
array the flush was already building.

**On Vulkan the ring is not only a fix, it is the only correct design.** D3D11's `MAP_WRITE_DISCARD` gives the
driver licence to rename the buffer under a write. Vulkan renames nothing, so writing bytes the GPU may still be
reading from a previous frame's submission is a straightforward data race with no diagnostic. Per-frame segments
are the requirement here rather than an optimisation, and the fact that D3D11 arrived at the same shape from a
different direction is a convergence rather than an inheritance.

**What replaces `MAP_WRITE_NO_OVERWRITE`: nothing needs to.** D3D11 has no persistent mapping and forbids a
mapped resource being bound to the pipeline, so the whole map-for-the-record-phase and unmap-at-submit dance
existed to work around an API restriction Vulkan does not have. The discipline that replaces it is that the
memory is `HOST_COHERENT` BY REQUIREMENT rather than by luck (V-M4), so no explicit flush is required, that
`vkQueueSubmit` performs an implicit host-write availability operation for coherent memory so writes made
before the submit are visible with no barrier, and that the ONLY remaining invariant is that the CPU never writes into a segment the GPU is still
reading, which is exactly the fence gate. The Vulkan ring is strictly simpler than the D3D11 ring while running
the same policy, and that asymmetry is the answer to anyone who reads "persistent mapping IS available on
Vulkan" as an invitation to design something new.

**Two creation-time invariants, adopted verbatim (V-M7).** Only `UniformBuffer`-usage buffers are ring-backed,
so a storage buffer's full-range view stays correct. And a ring-backed buffer never receives a non-uniform
binding: a buffer created `UniformBuffer | StructuredBufferReadOnly` or with either read-write structured bit
throws at creation. That combination is vacuous in the engine today, so nothing legitimate reaches the throw,
but it is legal on the seam and the Veldrid backend accepts it, so this is a **backend-divergent creation
failure** and must be documented as one rather than discovered by a consumer.

### 9.3 Bulk uploads, texture creation and views (V-M9 to V-M11)

Record-time `UpdateBuffer` on a non-uniform buffer, and `UpdateTexture`, write into a per-list staging arena
(host-visible, persistently mapped, sub-allocated, recycled on the list's slot retirement) and record
`vkCmdCopyBuffer` or `vkCmdCopyBufferToImage`, with a barrier narrowed to the destination's actual usage rather
than the incumbent's uniform-or-vertex-attribute guess, and with the render-pass split those copies unavoidably
cause. They are bulk and rare relative to the uniform sites and they are exactly the traffic the ring is not
for. Device-level `UpdateTexture` uses a device-owned staging pool, which is what off-timeline means.

**CORRECTED IN PLACE (17.36.1): "a barrier" is TWO barriers, and the singular was the defect.** The row as
written meant the one after the copy, which carries read-after-write. Nothing carried write-after-read, and a
buffer updated once per frame is read by the previous submission's draws while the next submission's copy
overwrites it. The pool ring's fence does not cover that (it waits on the submission frames-in-flight back) and
neither does submission order, which is not an execution dependency. The shipped shape brackets the copy: the
pre-copy barrier's source stages are the reading stages the usage implies plus transfer, its source access is
the transfer write alone. Found by the `sync` validation tier and not by any golden, which is the case that tier
was installed for ([#618](https://github.com/APKiwiOrg/KhaozEngine/issues/618)).

Staging buffers are pooled BY SIZE with a real retention cap. The incumbent destroys any returned staging buffer
over 512 bytes, so every real-sized upload creates and destroys a buffer AND a device memory block per call.
Raising that to a real cap is not an optimisation, it is removing an allocation storm from every load.

**No queue submit at texture creation (V-M10).** The incumbent's texture constructor clears render targets and
transitions sampled textures, and each of those grabs a shared pool, records one command and issues a WHOLE
`vkQueueSubmit`. Loading a scene with two hundred textures is two hundred queue submissions before a frame is
drawn. Here both are appended to ONE device-owned setup command buffer, flushed lazily at the next submit OR at
any device-level read (`Map`, readback, an explicit drain). **The read-path flush is what makes the claim true
without a hole**: a render target created and immediately read back must still see cleared contents, and a
design that only flushes at the next submit leaves that case reading memory nothing wrote. The clear itself is
preserved deliberately, because dropping it would change what a render target reads before anything writes it,
and undefined contents are not stable across runs while the goldens require stability.

**That buffer needs a lock, and V-W8 names it rather than leaving creation "free-threaded" unqualified.** A
`VkCommandPool` and every buffer allocated from it are externally synchronised (6.1), so two threads creating
two textures may not append to one setup buffer at once. Creation stays free-threaded everywhere else and takes
a SETUP-BUFFER LOCK for the append and for the lazy flush, held for the record of one or two commands and
released before creation returns. It is the third short lock beside the allocator's and the descriptor pool
manager's, and the flush path takes the submit lock under it in that order, never the reverse.

**Every image view is created eagerly, at resource creation (V-M11).** From the declared usage bits: a
full-chain sampled view if `Sampled` or `GenerateMipmaps`, an attachment view at mip 0 layer 0 if `RenderTarget`
or `DepthStencil`, a storage view at mip 0 if `Storage`. The bound is real rather than optimistic because the
seam cannot express anything else: `CreateFramebuffer` carries no mip or layer parameter, `ResolveTexture` is
subresource 0 only, and per-face cubemap rendering is not expressible. This is X1's decision on X1's evidence,
which is blunt and worth restating in a Vulkan seat where `vkCreateImageView` looks cheap enough to do at a
bind: all 25 `DEVICE_REMOVED` stacks in #423 surfaced inside the lazy view constructor on the draw path, so
lazy creation put an allocation on the hot path and put it on the exact path a broken device makes fail. The
enforcement is V-D2's, not a counter: no view factory is reachable from the recording type, so a draw-time view
is a compile error. A `GpuBufferRange` inside a `CreateResourceSet` description likewise resolves at SET
creation and never at draw time.

### 9.4 The ring policy inventory (V-P6)

2.2 decided not to share the ring's code. This is the checklist that decision owes, so the Vulkan ring is
implemented against the policy rather than against somebody's memory of it. **The Owner column says who checks
each row, because "covered by shared tests" was too broad a claim to leave standing.** Seven rows are the
shared semantic tests of V-P5 and V-T6, run against both backends' rings through the one test-only interface.
Three are each backend's OWN, because the POLICY is identical and the MECHANISM is not, which is 2.2's whole
ruling applied one row at a time rather than in bulk.

| Policy | What it means here | Owner |
|---|---|---|
| Segment selection | Frame N uses segment `N % FramesInFlight`. The base is applied at BIND and never baked into a descriptor or a resource set | Shared (V-T6) |
| Fence gating | A segment is acquired only after the completion value the frame that last owned it recorded has been reached. On this backend that read is `vkGetSemaphoreCounterValue` on the device timeline | Shared (V-T6) |
| Ordering | The ring cannot recycle safely before the completion primitive exists. This is the dependency edge phase 2's first draft dropped, and a ring built against submit receipts corrupts a frame silently | Backend's own. It is a BUILD-ORDER fact, not a runtime semantic: nothing a test can observe, and what enforces it is row 8 depending on row 5 |
| Backpressure | A blocked acquire increments the stall count and accumulates stall time, into the existing seam counters | Shared (V-T6). The interface exposes the stall count for exactly this row |
| Off-timeline reach (#484) | A device-level write reaches EVERY segment, not only the current one. A value written once must persist for the buffer's life, or two frames in three bind memory nothing has ever written | Shared (V-T6) |
| Off-timeline gating | The added segments are gated on the same completion read. The CURRENT segment is ungated and always written, because gating it would change the documented semantic | Shared (V-T6) |
| Off-timeline never blocks | A segment failing the gate is queued as a PENDING PATCH applied at that segment's next acquire. It is never waited for. A retry loop that waits for every non-current segment at once NEVER TERMINATES in the GPU-bound steady state, because the frame thread submits again for every frame the GPU retires | Shared (V-T6) |
| Lock legality | Because it never waits, a caller already holding the submit lock is legal. That is the case the waiting draft would have deadlocked on | Backend's own. Each backend has its own lock and its own deadlock to not have, and a shared test would assert against a lock the interface cannot see |
| Record-time writes | Stay current-segment only. Every shipped one is unconditional per frame, so replicating them would be N memcpys for a value the next frame overwrites, on the hot path | Shared (V-T6) |
| Stride | The stride is the SPACING of the segments, and it is not what the descriptor's `range` is set to. The range is the bind window (V-M6), and the invariant the stride carries is `rangeOffset + callerDynamicOffset + range <= stride`, so a frame's window lands inside its own segment and never in a neighbour's | Backend's own. The arithmetic differs (`align256` and a 16-constant count on D3D11, `max(256, minUniformBufferOffsetAlignment)` and a descriptor range here) where the invariant does not, and the Vulkan half additionally answers to a VUID (9.2) |

---

## 10. Synchronisation, fences and barriers

### 10.1 The timeline, and why one is enough (V-F1 to V-F4)

One `VkSemaphore` of type TIMELINE, initial value 0, owned by the device. Every `vkQueueSubmit` signals the next
value. `IGpuFence` holds a target, `Signaled` is `vkGetSemaphoreCounterValue() >= target` (a non-blocking read,
which is exactly what the seam demands), and `Reset()` clears the target so the fence can be handed to a later
submit.

**The seam's documented fence guarantee becomes true by construction, and that is the argument for one device
timeline over per-submit `VkFence` objects.** The seam promises that a fence handed to a submission made after
some earlier work signals only once the queue has drained through it. With per-submit fences that is a
convention, because fence B signalling says nothing about submission A. With one monotonic timeline it is a
theorem: a timeline semaphore's signal operations must strictly increase, and a queue's signal operations on one
semaphore execute in submission order, so the value reaching 6 requires the signal at 5 to have happened, which
requires submission 5's commands to have completed. Polling a later fence therefore transitively covers every
earlier submission, which is what `GpuRetireQueue` relies on.

`Submit(cl, fence)` is ONE `vkQueueSubmit` (V-F3). The incumbent's second empty submit signalling an internal
tracking fence is not inherited, and one timeline collapses three separate completion mechanisms (user fences,
tracking fences, staging recycling) into one primitive that the ring's segment gate and the `GpuRetireQueue` both
read.

`WaitForIdle` is `vkWaitSemaphores` on the last submitted value with an infinite timeout, counted into
`DrainCount` and `DrainMs`. Not `vkQueueWaitIdle`, for two reasons: it does not need the queue lock, so a drain
from one thread does not block a submit from another until it finishes, and it gives a value to time. **There is
no C6-style bet here**, because the incumbent's Vulkan drain is already real. Phase 2's `WaitForIdleCore` was an
empty method body and the win was in making it exist. Nobody should look for that win twice.

### 10.2 The swapchain's binary semaphores (V-F5)

`VK_KHR_swapchain` accepts no timeline semaphores at acquire or present, so:

- An ACQUIRE RING of `max(FramesInFlight, imageCount) + 1` binary semaphores, indexed by a monotonic acquire
  counter. Acquires are paced by the presentation engine and recording is paced by the frame loop, so the ring
  has to clear both, which is why it is sized on the maximum rather than on either alone.
- One RENDER-FINISHED binary semaphore per swapchain IMAGE, signalled by the submit that renders to it and
  waited by the present of that image.

**The acquire semaphore must not be indexed by the image index.** It is handed to `vkAcquireNextImageKHR`
BEFORE the image index is known, so indexing by image index means reusing a semaphore that may still be pending
from an acquire that returned a different image. It is the most common Vulkan swapchain bug and it manifests as
a validation error and an intermittent hang, never as a clean failure. The counter-based indexing is asserted in
a device-free unit test over a simulated acquire sequence that includes `OUT_OF_DATE` returns.

The incumbent sidesteps all of this by having no semaphores at all (`vkCreateSemaphore` appears zero times in
its Vulkan tree), acquiring with a fence and blocking the CPU on it. Section 11.2 is where that is changed and
2.6 is the argument.

### 10.3 Barriers and layouts (V-F6 to V-F9)

`vkCmdPipelineBarrier2` with explicit `srcStageMask`, `srcAccessMask`, `dstStageMask` and `dstAccessMask` per
barrier, rather than the incumbent's long if/else over layout pairs that ends in a debug assertion and silently
produces `NONE` on both sides in Release for an unhandled pair.

**Tracking is per command list, relative to a canonical resting layout (V-F7).** Every texture is assigned a
resting layout at creation from its usage bits: `SHADER_READ_ONLY_OPTIMAL` if `Sampled`, else `GENERAL` if
`Storage`, else its attachment layout. A command list assumes every texture is at rest when it starts,
transitions as it needs, and RESTORES the resting layout before `End`. That is what makes lists composable in
any submit order, which is what the seam promises and what record-time global layout tracking on the texture
object cannot deliver (2.5). It costs a handful of extra barriers per list at pass boundaries, bounded by
touched textures and independent of draw count, and V-T7 gates exactly that.

**CORRECTED AFTER SHIPPING (#623): "per subresource range" needs FOUR shapes, and the implementation enumerated
three.** V-F6 says tracking is per subresource range and leaves the shapes to the implementation, which named the
same range, a range CONTAINING tracked narrower ones, and a partial overlap it refused. The fourth is a range
CONTAINED IN one tracked wider entry, and it is not exotic: it is what the third shape's own collapse produces on
the next request. `OceanFftProducer.BuildMipChain` seeds each layer of the cascade map with a per-layer copy and
then generates mips over every layer, which collapses those per-layer entries into one, so the next recording of
the same composition asks for one layer of a mip the tracker now holds whole. It got the partial-overlap refusal,
whose message asserts neither range contains the other while one plainly does, and that took nine water tests
down on the `vulkan-native` leg. The shape is answered by transitioning the ENTRY rather than the request, which
keeps the entry uniform and keeps it one entry. Splitting it down to the request is the rectangle subtraction
this tracker declines to do, and it would trade one entry for up to four to restore at `End`.

**And it is the guest-leg design paying for itself, which is worth recording where the leg was argued for.** The
refusal is this backend's own bookkeeping, so no driver was involved and no validation layer would have reported
it. The incumbent Veldrid Vulkan leg passes those same nine rows on the same lavapipe, and that contrast is what
located the defect: a shape the engine's own backend refuses and every other backend accepts.

| Point | Transition |
|---|---|
| Begin rendering | each attachment to `COLOR_ATTACHMENT_OPTIMAL` or `DEPTH_STENCIL_ATTACHMENT_OPTIMAL` |
| Sampled bind of a texture written earlier in this list | to `SHADER_READ_ONLY_OPTIMAL`, with the writing stage as source |
| Storage bind | to `GENERAL` |
| Copy source or destination | to `TRANSFER_SRC_OPTIMAL` or `TRANSFER_DST_OPTIMAL` |
| Resolve | source to `TRANSFER_SRC_OPTIMAL`, destination to `TRANSFER_DST_OPTIMAL` |
| `End` | everything touched back to its resting layout |
| Present | the swapchain image to `PRESENT_SRC_KHR` |

**CORRECTED IN FLIGHT (row 15): the present row IS the `End` row, because `PRESENT_SRC_KHR` is the swapchain
image's resting layout.** The table above reads as though the present were a transition of its own, and row 17
left a notice in `VulkanPresentBoundary` saying the boundary would record one. It does not. A layout transition
is a RECORDED command, so a boundary-owned one needs a command pool on the boundary, a second `vkQueueSubmit`
per frame, and a rearrangement of which submit signals the render-finished semaphore, plus a THIRD submit for
the post-acquire discard, which has to run after the acquire semaphore and before the frame's first list. All of
that lands on the one path with zero automated coverage anywhere (MV9). Assigning the swapchain image a resting
layout of `PRESENT_SRC_KHR` instead costs nothing at all: the frame's own list restores it there at `End` under
the rule every other texture already follows, inside the submit that signals the semaphore the present waits on,
and the acquire half falls out of V-F8's second permitted `UNDEFINED` site, because a transition OUT of
`PRESENT_SRC_KHR` discards. That same discard is what covers a freshly created generation, whose images really
are in `UNDEFINED` and for which no first-use transition is recorded anywhere.

**BOTH HALVES REQUIRE THE FRAME'S LIST TO BIND THE SWAPCHAIN FRAMEBUFFER, and neither is a property of the frame
merely having submitted something.** The discard and the restore are both recorded by the recording that bound
it, so a frame whose submits never bound it transitions the image not at all: it presents an image nothing wrote,
which on a freshly created generation is an image in `UNDEFINED` that no barrier has taken to `PRESENT_SRC_KHR`.
That is why the semaphore pair is routed by the same question rather than by arrival order (#563): the boundary
asks each submit whether its list bound the framebuffer, hands the pair to the first that did, and presents
nothing at all when none did. Before that routing, an ocean priming frame's own list took the pair by arriving
first and the scene list that rendered the image submitted with no semaphores.

The limitation is named rather than hidden: a SECOND list in one frame that binds the swapchain framebuffer
after another one has already ended discards what the first drew. Every shipped renderer draws the backbuffer
from one list and the seam's portable contract is one open recording per device, so no shipped shape reaches it,
and the boundary-epilogue shape above is the fix if one ever does (https://github.com/APKiwiOrg/KhaozEngine/issues/562).

**The undefined-layout determinism rule (V-F8).** A transition whose `oldLayout` is `VK_IMAGE_LAYOUT_UNDEFINED`
is permitted to DISCARD the image's contents. It is the cheap transition and the tempting one, and using it on a
texture whose contents are still wanted produces output that varies by driver and by run. The goldens require
stability on the same rasterizer, so `UNDEFINED` appears as an old layout in exactly two places: a texture's
first-ever transition, and a swapchain image being reacquired for a frame that will fully overwrite it. Both are
asserted at the point of use.

**Deferred disposal (V-F9).** `Dispose` on any resource records the device's current timeline value and moves
the handle to a retire list drained at frame boundaries and at device teardown. That turns "mid-life resource
disposal racing queued async work", one of the four defects the CI workflow header records as fixed
engine-side, from convention-safe into a structural property of the backend. The engine's own `WaitForIdle`
calls stay, because they are the seam's contract and the Veldrid leg still needs them.

**Liveness (V-F10).** The `DeviceLiveness` latch is reproduced exactly: a shared volatile token flipped inside
the lifecycle lock before the real device is destroyed, every wrapper's `Dispose` gated on it,
`IGpuFence.Signaled` reading true after death, `WaitForIdle` a no-op after death. Device destruction calls
`vkDeviceWaitIdle` FIRST, unlike the incumbent, which destroys the memory manager and pools and then waits.

---

## 11. Swapchain, present, resize, threading

### 11.1 What is reproduced, and the W1 lesson restated (V-W1, V-W2)

Phase 2's W1 took the flip-model swapchain off the table for v1 because the swapchain is the one area with ZERO
automated coverage anywhere in the net. **That reasoning applies here with more force, not less**, because the
Vulkan golden legs are headless and a headless Vulkan device enables no surface extension at all (5.1). Not one
line of the swapchain path runs in CI, on any leg, ever.

So the following are reproduced from the incumbent exactly, because they are visible only to a human eye and
changing them buys nothing this phase is measuring. Rendering goes DIRECTLY into the swapchain image, so W1's
actual subject (the blit versus flip model) has no Vulkan analogue at all.

- Surface format `B8G8R8A8_UNORM` with `SRGB_NONLINEAR` colour space, and the sRGB pair when the device asks
  for it.
- Present mode: `FIFO` by default, `FIFO_RELAXED` when vsync is on and available, `MAILBOX` then `IMMEDIATE`
  when vsync is off. **`FIFO_RELAXED` under a vsync request permits tearing on a late frame and is arguably
  the wrong answer**, and it is reproduced anyway, because #380's pacing work is where that gets decided with a
  measurement and this phase must not move the variable underneath it.
- Image count `min(maxImageCount, minImageCount + 1)`, `COLOR_ATTACHMENT | TRANSFER_DST` usage, `OPAQUE`
  composite alpha, `clipped = true`, extent clamped to the surface's reported min and max.
- No MSAA on the swapchain, and no depth attachment, both matching the incumbent.

**Two deliberate departures (V-W2), and both are bugs rather than behaviours.** `preTransform` reads
`currentTransform` rather than being hardcoded to `IDENTITY`, because hardcoding identity on a surface reporting
a rotation is wrong on a device that would reach it. And the incumbent's sRGB fallback compares an
already-undefined format against an sRGB one, making the intended throw unreachable, so that shape is not
copied. Reproducing a bug that a different device WOULD reach is not parity.

### 11.2 The one present-path behaviour that changes (V-W3, V-W4)

The incumbent presents, immediately acquires the next image with a FENCE, and blocks the CPU on
`vkWaitForFences` with an infinite timeout inside `SwapBuffers`, while the subsequent submit carries no
image-availability wait semaphore at all. So the CPU stalls on the presentation engine once per frame, and the
GPU-side ordering between the acquire and the rendering that targets the acquired image rests on that CPU stall
rather than on a semaphore.

This design keeps the TIMING, which is a genuinely good property (acquiring at present time for the NEXT frame
is what makes the image index known before recording starts, so nothing about record-time framebuffer
resolution changes), and replaces the synchronisation. The acquire signals a BINARY semaphore that the frame's
submit waits on at `COLOR_ATTACHMENT_OUTPUT`, the submit signals a render-finished semaphore, and the present
waits on it. The CPU does not block. `vkAcquireNextImageKHR` returns the index synchronously even when it
signals a semaphore.

**2.6 is the argument and it is worth restating in one line: this is FORCED by V-T4 rather than preferred.**
Presenting with no wait semaphore is a spec violation validation flags, and a design cannot gate on validation
while deliberately reproducing a configuration validation rejects.

`KE_VULKAN_ACQUIRE=stall` restores the incumbent's shape exactly, for the frame-pacing A/B (MV2). It is
therefore NOT usable together with `KE_VULKAN_VALIDATION`, which is a documented limitation rather than a bug,
and per V-RO4 it is removed at gate 4 with the blocking path deleted.

**A frame whose acquire returns `OUT_OF_DATE` (V-W4).** Neither draft says what happens to it, and the gap is
four questions, not one. All four are answered inside `SwapBuffers`, which is the only place any of this
happens: it presents the frame just submitted IF an image is held, then applies any pending recreation, then
acquires for the next frame. It never throws, never reports failure upward, and the frame loop above it is
unchanged.

- **The recreate runs at that same boundary**, not queued to a later one, and the semaphore handed to the
  failed acquire is retired by the recreate's unconditional drain rather than being reused. Without the
  same-boundary rule that semaphore is either reused while pending, which is the reuse bug, or destroyed while
  pending, which is undefined behaviour.
- **ONE fresh acquire follows the recreate, at that same boundary.** This is the half neither draft reaches and
  it is the one that decides the shape of everything else. The reason the incumbent's acquire TIMING is kept at
  all is that acquiring for the next frame at present time makes the image index known before recording starts
  (above), and a recreate that returns without re-acquiring throws that away for one frame, which means the
  record path needs a second "no image yet" state. Re-acquiring immediately means an ordinary boundary and a
  recreating boundary leave the device in the same state, and the imageless state exists in exactly one place
  instead of two. The retry is ONE. If the fresh acquire also fails, the boundary returns with the pending flag
  still set and tries again next time, so a surface mid-resize cannot spin the boundary.
- **What an imageless frame binds.** The V-W5 wrapper's identity is stable across all of this, so the question
  is what its views POINT AT, and recording against destroyed views is a use-after-free CI cannot see (MV9).
  Two rules make it unreachable. The old views are destroyed and the wrapper repointed inside one operation on
  the submit thread, after the drain and with no recording in flight (11.3), so there is no window in which the
  wrapper names a dead view. And when the double failure does leave the device with no image, the wrapper is
  repointed at a device-owned ORPHAN TARGET rather than at nothing: one colour image at the current extent
  clamped to a minimum of 1 by 1, matching the swapchain framebuffer's shape, which carries no depth (11.1).
  It is created lazily the first time this path is reached and destroyed at the next successful acquire. The
  frame then records, submits and completes exactly like any other frame, and only its present is skipped. That
  costs one image in a state a minimised or zero-extent window reaches and nothing else does, and it buys
  `SetFramebuffer` being legal at every instant without a seam change and without a use-after-free.
- **The skipped frame counts into `FramesBegun`.** A skipped PRESENT is not a skipped frame: the device opened
  it, the recording and the submit really happened, and `FramesBegun` is the denominator every per-frame figure
  is divided by. Leaving it out would understate per-frame costs on exactly the frames that were unusual.

### 11.3 Resize, present-mode change, and the retirement hazard (V-W5 to V-W7)

`ResizeSwapchain(w, h)` stores the pending size coalesced to the last requested and returns. `OUT_OF_DATE` or
`SUBOPTIMAL` from either `vkAcquireNextImageKHR` or `vkQueuePresentKHR` sets the same pending flag. Setting
`SyncToVerticalBlank` at runtime sets a pending present mode, and Vulkan cannot change present mode in place, so
that too is a recreation and the seam's "no recreate" wording (which describes Metal) gains a Vulkan sentence.
All three apply at the next present boundary on the submit thread, where the recreation provably owns the queue
and no recording is in flight.

Recreation: drain the timeline to the last submitted value, create the new swapchain passing the old as
`oldSwapchain`, take the new images and views, rebuild the per-image render-finished semaphores, destroy the old
swapchain, views and semaphores, and swap the new views into the EXISTING `IGpuFramebuffer` wrapper so its
identity survives. Identity stability matters more here than on D3D11 because every image view object is
replaced, and a foreign-thread resize during recording becomes structurally impossible rather than
contractually forbidden, which also matters more here because recreating a swapchain invalidates every
attachment a recording may already have bound.

**The retirement hazard, named because it is the one that bites.** A binary semaphore an acquire signalled but
nothing ever waited on is left pending, and destroying a pending semaphore is undefined behaviour that
validation catches and drivers mostly tolerate until they do not. **The drain before recreation is what makes
retirement safe, which is why it is unconditional rather than only on resize.**

`vkQueuePresentKHR`'s result is CHECKED (V-W7). The incumbent ignores it entirely.

### 11.4 Threading (V-W8)

- Recording is lock-free and per-list. Any number of lists may record concurrently on any threads, because each
  owns its pools and its layout map.
- One `_submitLock` covers `vkQueueSubmit`, `vkQueuePresentKHR` and the swapchain recreation. Held for
  microseconds, not a frame.
- `Map` and `Unmap` on staging take it for the map call only.
- Device-level `UpdateBuffer` and `UpdateTexture` are callable from any thread behind the same short lock scoped
  to the write, and legal from a caller already holding it, because the ring policy never waits (9.4).
- Resource creation is free-threaded. Vulkan has no `DriverConcurrentCreates` analogue to ask about, and three
  short locks cover what is genuinely shared: the allocator's, the descriptor pool manager's, and the
  SETUP-BUFFER lock around the device-owned command buffer texture creation appends to (V-M10, 9.3), which is
  needed because a `VkCommandPool` is externally synchronised and creation is otherwise unsynchronised. Held
  for an append or a flush, never across a creation call. The flush takes the submit lock under the setup lock,
  in that order and never the reverse.
- Submit order is the observable order, with the caveat the seam already documents in rule 2.
- The process-wide `GpuDeviceContext._lifecycleGate` is unchanged and still serialises device creation and
  disposal across backends. **That gate exists because concurrent device creation raced the Vulkan loader on
  lavapipe**, so this is the backend it was built for and nothing here weakens it.

Multi-threaded recording is STRUCTURALLY SUPPORTED and is not in the shipped contract (W5's position,
unchanged). Nothing in the engine asks for it and no test exercises it.

---

## 12. Shader path

### 12.1 There is no cross-compilation, and that is the headline (V-S1, V-S2)

Vulkan consumes SPIR-V. The engine already has a Veldrid-free, device-free, internal entry point that turns a
GLSL 450 source into SPIR-V: `SpirvCrossCompile.ToSpirv(glsl, stage, label)`, whose signature names no Veldrid
type and which the backend reaches across `InternalsVisibleTo`. `vkCreateShaderModule` takes the bytes verbatim.

So the entire shader path is: call the existing helper, create the modules, hold them on the shader-set handle.
No HLSL, no FXC, no register numbering to invent, no `SpirvLocalSize` hand-parse, no emitted-intermediate hash
pins over a cross-compiler, no signature workarounds. Phase 2's section 8, which is seventy lines of hazard, has
an eight-line counterpart here. That is not luck, it is P2 paying out: the edge was confined to one file in
`KhaozEngine.Gpu` precisely so a later backend could take the half it needs.

Every ENGINE-OWNED seat that produces SPIR-V goes through `SpirvFrontEnd` and therefore under the same pinned
options, and there is no debug or optimisation knob on that leg anywhere in the repo, which is worth stating
because the D3D11 path has one on ITS leg and a reader may go looking for the equivalent. The incumbent
`VeldridGpuDevice` is NOT one of those seats and is not meant to be: it hands GLSL to `CreateFromSpirv`, which
runs the front end internally, and on its compute path it calls `CompileGlslToSpirv` itself with
`GlslCompileOptions.Default` so it can read the workgroup size back. That wrapper leaves the graph only when
Veldrid itself does, so it keeps the library's defaults on purpose. **So the native backend hands
`vkCreateShaderModule` the same bytes the incumbent hands it today**, which makes the 36 goldens test the
BACKEND and nothing else. That sentence is a MEASURED and ASSERTED fact rather than one held by construction,
and the difference is the whole subject of the rest of this section.

**And here is the caveat, taken from phase 2's own correction rather than rediscovered.** The D3D11
byte-equality test carries a warning in its header that must be transplanted with the pattern: its hash table is
baked from that path's own emission, so what it detects is DRIFT, and a wrong emission baked once passes
forever. It compares nothing against the incumbent. Parity with the incumbent was a SEPARATE, HISTORICAL,
one-off measurement taken at review time, and that measurement is what let the D3D11 goldens carry over without
a rebake.

So V-S2 is TWO artefacts, not one:

1. **`SpirvFrontEndPin`**, the analogue of `HlslCrossCompilePin`: the front-end options stated as constants with
   a citation, plus an `Identity` string built FROM those constants so a pin change moves every derived cache
   key by construction.
2. **A parity check against the incumbent's own path**, compiling every shipped program through both paths in
   one process and asserting byte equality. It was taken first as a ONE-OFF measurement, RECORDED in this
   document before the first golden run, and that recording is the fact that licenses "no rebake". V-T5's
   per-program hash table guards against drift from that point on. Recording the measurement's date and result
   here is the whole discipline: a reader who mistakes the drift test for the parity test will believe a proof
   that was never taken.

**The measurement is now also a standing test, added in review.** `VulkanSpirvIncumbentParityTests` makes the
same comparison over the same 76 stages on every leg, so parity stops being a fact about one afternoon and
becomes a fact about the current tree. The one-off measurement below is unchanged and stands as the historical
record of what licensed the goldens carrying over. The reason to have both is that the equality is NOT true by
construction: the pin governs the engine's own front-end seat and the incumbent wrapper keeps
`GlslCompileOptions.Default`, so the two sets are maintained independently and a deliberate change to either one
silently moves one side of an equality nothing else in the net is watching. A red run there means they have
diverged, which means the committed `vulkan` goldens are baked on one emission and asserted against another, and
the response is to decide which side moved rather than to re-bake the hash table.

**THE MEASUREMENT, TAKEN 2026-08-08, BEFORE THE FIRST GOLDEN RUN. 76 of 76 stages byte-identical, 0
mismatches.** Every shipped program was compiled twice in one process and the two SPIR-V modules compared byte
for byte: 34 graphics programs at two stages each plus 8 compute kernels, which is 76 stage emissions and the
whole shipped set. The native side is `SpirvFrontEnd.ToSpirv(source, stage, programName)`. The incumbent side is
`SpirvCompilation.CompileGlslToSpirv(source, fileName: null, stage, GlslCompileOptions.Default)`, which is the
call `Veldrid.SPIRV`'s `CreateFromSpirv` makes on a Vulkan device, where it takes the short path and hands the
compiled SPIR-V straight to `vkCreateShaderModule` with no cross-compilation at all. The two call shapes differ
in exactly one argument, the diagnostic FILE NAME, and the measurement is what establishes that it never reaches
the module while `SpirvFrontEndPin.Debug` is false. So the native backend really does hand
`vkCreateShaderModule` the same bytes the incumbent hands it, and the committed `vulkan` golden family carries
over unmodified.

Two consequences worth stating rather than leaving to be re-derived. The compile LABEL not reaching the bytes is
also what makes V-S7's module dedup work across programs: the same measurement pass counted 59 DISTINCT modules
behind those 76 emissions, so a third of them are shared, and a label that reached the bytes would have made all
76 distinct while looking identical from the outside. And the recorded measurement above is a historical
artefact that is never edited or re-taken by hand: the standing test is what re-checks the claim now, and it is
also what turns `VulkanSpirvByteEqualityTests` moving EVERY program at once into a two-test failure rather than
a one-test puzzle. Re-baking that table on its own would leave a proof nobody has taken standing behind 36
goldens, which is exactly what the standing test refuses to let happen quietly.

Even with the caveat this is materially stronger than phase 2's position, where the pin sat over an
INTERMEDIATE (emitted HLSL) that then went through FXC. Here there is no intermediate and no cross-compile.

**One phase-2 correction carried forward so it is not re-derived.** Phase 2's section 8.2 assumed
`CreateFromSpirv` derives something from `ResourceBindingModel.Improved` that a direct call does not get.
`HlslCrossCompilePin` records that this is FALSE: options are forwarded verbatim, `ResourceBindingModel` is not
a member of `CrossCompileOptions` at all, and in the vendored fork only the METAL backend reads it. Two
consequences. Vulkan is unaffected, which is why V-S1's claim survives. And **phase 4 IS affected**:
`ResourceBindingModel` is a Metal-only concept and it is where Metal's argument-buffer layout gets decided, so it
belongs in the phase-4 brief and not in this one.

### 12.2 The numbering, inherited and asserted (V-S8)

The D3D11 backend had to INVENT a register scheme and then prove the CPU side and the emitted HLSL agreed.
Vulkan has no such freedom and no such risk: the GLSL sources already declare `layout(set = N, binding = M)` and
the backend must make N the layout's index in the pipeline array and M the element's index in the layout. Get it
wrong and everything compiles and every pixel is wrong, which is the same failure S2 exists to prevent arriving
through a different door.

So it gets S2's test, and it is cheap because both sides are already in the repo: parse every declaration out of
the shader source constants, pair each program with its pipeline's layout array and each layout with its element
array, and assert N and M against the indices. Device-free, on every `dotnet test`, over all thirty-odd programs
rather than a hand-picked few. One shipped case makes it worth doing rather than assuming: `SpriteBatch`
declares its UBO at `set = 1` with texture and sampler at `set = 0`, so "the UBO set comes first" is false in
shipped code and the test is the only thing that would catch a layout array reordered by a well-meaning
refactor.

### 12.3 The SPIRV-Cross exit is NOT taken here, and declining is the pro-Metal choice (V-S3 to V-S6)

#420 says cross-compilation eventually moves to direct SPIRV-Cross bindings. Phase 2 filed that as F2. This
design leaves it filed and argues that actively.

**Vulkan needs no cross-compiler at all**, only the glslang FRONT end. SPIRV-Cross, the back end, exists in this
engine to produce HLSL for D3D11 and will exist to produce MSL for Metal. Swapping it here would put D3D11's 36
goldens and both documented WARP corruption incidents in play at once, for a backend that does not consume its
output, in the phase whose CI leg cannot see ANY of it: Vulkan consumes SPIR-V, so a change to the HLSL emitter
is invisible to every vulkan golden. Landing a change in the phase least able to detect its own regressions is
the wrong sequencing whatever the endpoint is.

**So separate the halves instead (V-S3).** `SpirvCrossCompile` already has the seam internally: `ToSpirv` is the
front end, `VertexFragmentToHlsl` and `ComputeToHlsl` are the back end. Split the file along it. The Vulkan
backend takes an `InternalsVisibleTo` dependency on the front end only, and a device-free architecture test
asserts it never names a back-end member. When Metal arrives it changes the BACK end to add an MSL target, and
F2 becomes a change to one half of one file with exactly one consumer family, evaluated against Metal's own
fresh goldens rather than against D3D11's committed ones. That split is the entire Metal-facing carrying cost of
this phase and it is one file move.

**What stays in the graph, stated so nobody thinks Vulkan retires anything.** `Veldrid` stays (Metal, and both
incumbent legs). `Veldrid.SPIRV` stays (glslang for everyone, SPIRV-Cross for D3D11). What changes is the
ARITY: after this phase, one of three shipping native backends consumes only the front end, which is the fact
that makes the split obvious rather than speculative.

**Two things this backend must not "fix", both stated because they WILL be proposed.** S5's holed-signature
sinks stay (V-S5): SPIRV-Cross drops unread vertex inputs and a holed `TEXCOORD` sequence miscompiles under FXC
on WARP, both incidents were tolerated by Metal and Vulkan, and the D3D11 leg ships indefinitely, so removing a
sink because Vulkan tolerates it corrupts WARP. And the Metal-driven shader-shape invariant stays (V-S6): the
engine's shaders carry exactly one uniform buffer per pipeline at set 0 binding 0 with per-mesh textures at set
1 and up, Vulkan has no such limit, a Vulkan-only author would naturally spread uniforms across sets, and doing
so breaks a phase-4 backend that is not here to defend itself. The Metal-only shader validation check is in the
same category: a Vulkan backend neither needs it nor may remove it.

### 12.4 Pipeline and module caching (V-S7)

A `VkPipelineCache` created at device creation, seeded from a file keyed on
`(pipelineCacheUUID, driverVersion, engine version)` and written back at disposal. The incumbent passes a null
cache at both pipeline creation sites, so every pipeline is compiled from SPIR-V on every launch, across the
shipped graphics programs and compute kernels and considerably more pipeline permutations, because everything
except viewport and scissor is baked into the pipeline object.

**The caution one draft raised is adopted as a requirement rather than as a reason to defer.** A corrupt cache is
a crash class, so the file's header is VALIDATED before `pCacheData` is passed (magic, header size, vendor and
device ID, `pipelineCacheUUID`), any mismatch discards silently, and the whole path is best-effort so a failure
to read or write is never fatal. The "needs a validity key" objection is answered by the API itself:
`pipelineCacheUUID` is the vendor-supplied key. `VkShaderModule` objects are deduplicated by SPIR-V hash within a
device, because several programs share stages. This is S4's disk-cache decision with a different noun, and the
D3D11 side already ships the pattern.

---

## 13. Compute, MSAA, staging and readback

**Compute (V-C1 to V-C4).** Compute and graphics bindings are tracked separately with separate dirty arrays and
separate bound-pipeline slots, as the seam requires, so a compute bind never disturbs a graphics one.
`SetComputePipeline` and `Dispatch` end any pending rendering first (V-A4).

Rule 1 (compute writes a storage texture, a graphics pass in the same list samples it) is satisfied by a REAL
image barrier where the sampled bind is assembled: `GENERAL` to `SHADER_READ_ONLY_OPTIMAL`, `COMPUTE_SHADER`
and `SHADER_WRITE` as source, `FRAGMENT_SHADER` and `SHADER_READ` as destination. The incumbent achieves the
same observable result with a queued layout restore armed by the `Sampled` usage flag and drained before the
next draw, which is why the seam's comment says splitting across two command lists is unsafe there. Here it is
unsafe for a different and better-scoped reason: the second list assumes the resting layout, which the first
list restored, so the handoff simply does not carry. The seam's rule 1 GUARANTEE needs no change.

Rule 2 is honoured AS WRITTEN and no seam member is added. The backend ADDITIONALLY emits a real read-after-write
barrier between dependent dispatches inside one list, driven by a set of resources written by earlier dispatches
rather than by a barrier per dispatch, so chaining is safe here. **That is not a contract change and must not be
written as one**: rule 2 is cross-backend, the Veldrid legs still need the drain, and a consumer that drops the
drain because Vulkan-native tolerates it breaks on Metal. It is EVIDENCE for F1, the automatic-hazard seam
capability, which after this phase has two of three backends able to answer yes.

**And the seam's comment becomes false the day this ships (V-C3).** It describes VELDRID's Vulkan behaviour BY
NAME, saying that Vulkan queues a layout restore at dispatch time and drains it before the next draw, and that
on Vulkan no memory barrier is emitted between dependent dispatches at all. Both sentences stop being true.
Rewording it to name the implementation it describes is a doc task with an owner in the work breakdown, because
R4's precedent is that an unwritten contract decays and a wrong written one decays faster.

Storage buffers are plain SSBOs (V-C4). The RAW byte-address forcing is an HLSL artifact of what SPIRV-Cross
emits for a GLSL storage block and has no Vulkan analogue. Nor is there an SRV-versus-UAV auto-unbind, which is
a D3D11 binding-model artefact whose Vulkan occupant is the rule 1 barrier above.

**MSAA (V-C5, V-C6), and this is the row ruled against both drafts.** One draft computes `MaxMsaaSampleCount`
as the minimum over framebuffer colour and depth sample-count limits intersected with per-format image
properties across three MRT formats. The other computes it as the AND of framebuffer colour, framebuffer depth
and sampled-image colour sample counts. These are DIFFERENT computations and at most one can equal the
incumbent's, yet both then assert equality with the incumbent as a test. That is exactly the C4 failure phase 2
had to correct in flight, where the first draft asked the driver a different question than the incumbent did and
"asserted equal" rested on them happening to answer the same.

So: **the computation is READ OFF the incumbent's own `GetSampleCountLimit` and reproduced**, with the citation
pinned in a constant, and the implementation issue re-reads it before writing. Then "asserted identical" is
satisfiable by construction rather than by luck. The incumbent's shape is a per-format read reduced to the
highest supported bit, so reproducing it is cheap, and neither draft's invented formula is taken.

**CORRECTED IN FLIGHT (row 15).** This paragraph named the wrong call, and re-reading the source before writing
is what caught it, which is the whole reason that obligation was put on the row. The sentence above said
`vkGetPhysicalDeviceFormatProperties`. `VkGraphicsDevice.GetSampleCountLimit` calls
`vkGetPhysicalDeviceImageFormatProperties` and reduces the `sampleCounts` field of the `VkImageFormatProperties`
it returns. Those are different queries with different answers: the image-format one takes the image type, the
tiling and the USAGE, and a format's supported sample counts genuinely differ by usage, so a backend that had
taken this paragraph at its word would have asked a different question and then asserted equality with the
incumbent, which is precisely the failure the paragraph exists to prevent. `VulkanMsaaLimit` carries the real
shape with its citation, and it also pins the clause that reads like a bug: `GetSampleCountLimit` hands its own
`depthFormat` argument to the USAGE bits alone and maps the format with `VdToVkPixelFormat`'s default, so the
linear-depth target is queried as `R32_SFLOAT` with a COLOUR attachment usage.

`ResolveTexture` is `vkCmdResolveImage` at mip 0 layer 0 outside a render pass instance, with both images
transitioned to the transfer layouts and restored afterwards per V-F7. An out-of-range requested sample count
THROWS at texture creation rather than silently falling to 1, which is C4's departure inherited for the same
reason: the engine clamps upstream so nothing legitimate reaches the throw, and a silent MSAA downgrade presents
as a golden mismatch that reads like a rendering bug. No MSAA on the swapchain, matching the incumbent.

**Staging and readback (V-C7, V-C8). This is the highest-risk parity surface in the design and it earns its own
paragraph.** Every golden in the suite reads back through `IGpuDevice.Map(staging, ...)` and consumes
`MappedData.RowPitch`. The incumbent backs a staging texture with a `VkBuffer` rather than a linear-tiled image,
and computes the subresource layout IN SOFTWARE: row pitch, depth pitch, array pitch and the subresource offset
are all engine arithmetic, not `vkGetImageSubresourceLayout`. A different arithmetic produces a garbled grid on
every scene at once.

So the buffer backing and the software layout computation are reproduced byte for byte, and a device-free test
computes the layout for a spread of formats, sizes, mip levels and array layers and asserts it against a
checked-in table taken from the incumbent's own arithmetic. That converts "should be identical" into a checked
fact before a single golden is run, which is exactly what phase 2's S3 did for the emitted HLSL. One draft
asserted the rows are tightly packed and moved on, which may well be right and is the wrong posture either way,
because the assertion is not what the goldens depend on.

Memory type for readback staging prefers host-visible, coherent and cached, falling back to coherent alone,
which is the incumbent's preference. `CopyTexture` from a render target becomes `vkCmdCopyImageToBuffer` with
the same row stride.

**`Map(staging, Read)` WAITS on the timeline's last submitted value before returning the pointer (V-C8)**, and
the wait is counted as a drain. D3D11's `Map(READ)` on the immediate context blocks until the resource is ready
by definition, so this is where Vulkan has to be explicit about something the other API did implicitly. Getting
it wrong returns a pointer to bytes the copy has not written yet, which reads as an intermittently wrong golden.

---

## 14. Capabilities and diagnostics

`ReadCapabilities` stays the single source both `GpuDeviceContext.Capabilities` and `IGpuDevice.Capabilities`
come from, after they drifted before 15.2.0. The native device implements one and the context reads it from the
device.

| Member | Native source | Parity |
|---|---|---|
| `ClipSpaceYInverted` | **false**, from the negative-height viewport path (7.2) | identical, and it is the one that flips every image |
| `DepthRangeZeroToOne` | true | identical |
| `DeviceName` | `VkPhysicalDeviceProperties.deviceName`, NUL cut only, never trimmed | identical by construction, given V-N3's default selection |
| `SamplerAnisotropy` | `VkPhysicalDeviceFeatures.samplerAnisotropy` | asserted identical |
| `SamplerLodBias` | true | identical |
| `MaxMsaaSampleCount` | the incumbent's own computation, reproduced (V-C5) | asserted identical, and satisfiable by construction rather than by luck |
| `SupportsShadowMaps` | `vkGetPhysicalDeviceFormatProperties(R32_SFLOAT)` for COLOUR attachment and sampled | asserted identical |
| `SupportsCompute` | true | identical |
| `SupportsCompletionFences` | true | **identical**, unlike D3D11 |

**ZERO permitted differences (V-G1)**, which is a stricter bar than phase 2's and is the correct bar, because
the incumbent Vulkan backend has no capability defect to correct: `VeldridMap.SupportsCompletionFences` already
answers true for Vulkan (verified). If the parity test finds a difference it is a bug in the native backend
until proven otherwise. The test carries the reflection-completeness check phase 2 called the guard that matters
most, that the comparison covers every member of `GpuCapabilities`, so a member appended later cannot silently
weaken the assertion. It runs in one process on the Linux leg at 1x billing, which makes it the cheapest strong
test in the program.

**CORRECTED IN FLIGHT (row 18): the `SupportsShadowMaps` cell above said "attachment" and the first
implementation read that as `VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT`.** That question is not stricter,
it is structurally false: `R32_SFLOAT` is a colour format, no driver reports the depth-stencil bit for it, so
the capability would have answered false on every Vulkan device in existence. The shadow pass never wanted that
bit either, since `ShadowMapRenderer` creates the atlas as `R32Float` with `RenderTarget | Sampled` and hangs a
separate depth-stencil off it. `VeldridMap.SupportsShadowMaps` asks `GetPixelFormatSupport` for
`RenderTarget | Sampled`, so the correct pair is COLOUR attachment plus sampled image, which is what
`VulkanPhysicalDeviceReader.ShadowMapFormatFeatures` now holds, with the decision split off the driver call so
the bits are pinnable device-free. Carry the D3D11 sibling's warning with it: this capability answering false
where the incumbent answers true is not a loud failure, it is the shadow path degrading to blob shadows on one
backend only, with nothing red anywhere. The lesson generalises under a zero-difference bar, which is why it is
written here rather than only in the code: writing down what a member's NAME suggests instead of what the
incumbent asks is a parity failure by construction.

**CORRECTED IN FLIGHT (row 18): the `DeviceName` cell said "trimmed", and the implementation deliberately does
not trim.** It cuts at the first NUL and returns everything else verbatim, padding included. Trimming reads like
the tidier answer and is the wrong one under this bar for the same reason as above: the incumbent does not trim,
so a trim on the native path alone fails `DeviceName` parity on every machine whose vendor pads its reported
name, which is an ordinary hardware habit rather than a hypothetical. The parity test pins the padded case on
purpose. The NUL cut stays because it is free and defensive, and it expects to find nothing, since the
marshaller on both paths already stops at the first terminator.

**The device's shared point and linear samplers WRAP on all three axes**, built from wrap-addressed
descriptions and NOT from the identically named `GpuSamplerDescription.Point` and `.Linear` statics, which
default every axis to CLAMP. The seam says so in writing on `IGpuDevice.PointSampler`, and reading the address
mode off the statics because the names matched cost two goldens on the D3D11 leg. This paragraph exists because
the same mistake is available here. The incumbent's other sampler mappings are reproduced exactly.

**Physical device selection (V-G2).** `KE_VULKAN_DEVICE` accepts an index, a name substring, or one of
`llvmpipe`, `discrete`, `integrated`, `cpu`, with a named-but-absent device producing a WARN plus the default
path rather than a hard failure. CI pins `llvmpipe`. The hole this closes is worse than D3D11's: there,
`KE_D3D11_ADAPTER` guards against a runner image growing a paravirtual adapter, while here the incumbent takes
`physicalDevices[0]` unconditionally, so a runner image that enumerates anything before lavapipe silently
changes the rasterizer under the golden gate. `SoftwareRasterizer` is `deviceType == Cpu || driverID ==
MesaLlvmpipe` and lands in the EXISTING `softwareAdapter` telemetry field.

**Device loss (V-G4).** `VK_ERROR_DEVICE_LOST` can come back from `vkQueueSubmit`, `vkQueuePresentKHR`,
`vkAcquireNextImageKHR`, `vkWaitSemaphores`, `vkGetSemaphoreCounterValue`, `vkMapMemory`, `vkDeviceWaitIdle` and
every creation call. Every result is checked in EVERY configuration, because the incumbent's `CheckResult` is
`[Conditional("DEBUG")]` and a latch built on that shape would never fire in Release. On loss the call name and
result are latched IMMEDIATELY at the fault site, the liveness token flips so all subsequent disposals are
no-ops, and the reason surfaces through the existing `deviceLossReason` header field. Where `VK_EXT_device_fault`
is present its address and vendor information are appended. That closes #427 for the Vulkan leg on the day the
backend lands, which is the correct time, because retrofitting the reporting after the first field crash wastes
the crash.

**Validation and debug utils (V-G3, V-G5).** `KE_VULKAN_VALIDATION` takes `0` (default), `1`
(`VK_LAYER_KHRONOS_validation` plus a `VK_EXT_debug_utils` messenger pumping into the engine logger through a
rate limiter, with error and warning severities promoted), `strict` (the pump latches and THROWS on error
severity at a controlled point), and `sync` (adds the synchronisation-validation feature through
`VkValidationFeaturesEXT`). Object names are set through the debug-utils naming call when the messenger is on,
which is what makes a validation message name a buffer instead of a handle.

Three departures from the incumbent's shape, all of them bugs: `VK_EXT_debug_utils` rather than the
`VK_EXT_debug_report` it uses, which has been deprecated for six years, no request for the long-removed
`VK_LAYER_LUNARG_standard_validation`, and the callback LOGS rather than throwing a managed exception and
calling `Debugger.Break()` from inside a native driver callback. Unwinding a managed exception through native
driver frames is not a diagnostic. `strict`'s throw happens at a controlled point after the latch, never inside
the callback.

No frame-capture integration. RenderDoc attaches externally and needs no engine support.

**Counters (V-G6).** `FramesBegun`, `DrainCount`, `DrainMs`, `BackpressureStallCount`, `BackpressureStallMs`,
`OffTimelineDeferred` and `OffTimelineOutstanding` are all populated from the struct as it stands: it exists,
already documents that absent is not zero, and is answered by exactly one backend today. They leave through
`IGpuDevice.Counters`, are forwarded by `GpuDeviceContext` and `AppWindow`, and reach a capture as sample-row
channels, which is the path gate 4 reads.

**One member pair is ADDED, and calling that "no seam change" would have cost MV2 its result.** The acquire
wait is the whole subject of V-W3 and there is nothing on the struct that counts it, so `AcquireWaitCount` and
`AcquireWaitMs` are appended, with the two matching `GpuTelemetryChannels` names, in the count-and-milliseconds
shape every other reading on the struct already uses and for the reason its own doc comment gives: a count with
no cost attached cannot be weighed, and a duration with no count cannot separate one long wait from many short
ones. Row 17 owns it, and the pair goes on the populating constructor too, so the D3D11 native device's single
call site is updated with it and passes zero, which is the honest reading on a backend with no acquire to wait
on rather than a gap. Without the pair MV2's A/B
has only mean frame time to read, and section 16 explains why that number cannot answer the question on the
reporting machine.

**And one shipped member's doc comment changes meaning here, which is a smaller thing said out loud rather than
a silent one.** `BackpressureStallCount` is documented as the ring-segment stall, and on this backend it also
counts the command-buffer slot wait at `Begin` (6.1). The two are folded onto one accumulator deliberately,
because they are the same statement about pipeline depth with the same lever (`FramesInFlight`), which is the
exact criterion #499 used when it ruled that the off-timeline reading must stay SEPARATE. So the member stays
one member and its XML doc gains a sentence naming the second thing it counts, as an owned doc task in row 19
rather than as a fact a reader has to infer from a design document.

---

## 15. Test plan

| Layer | What it covers | Runs where |
|---|---|---|
| The 36 committed `vulkan` goldens, shared family (V-T1) | Pixel equivalence against the SHIPPED Vulkan backend on the same lavapipe rasterizer, at the one global tolerance of 0.06 absolute per channel where zero of the 1728 values in a golden grid may exceed it. No rebake | New `vulkan-native` leg, golden-only on push, full on schedule |
| `CrossBackendGoldenTests` | Unchanged. Still three families, still the 0.20 ceiling. It is the thing that would catch a bad `vulkan` bake | Every `dotnet test` |
| Native-call budget, device-free `[Fact]` (V-T2) | The VULKAN fan-out class through `IVkCmdSink`: binds, draws and dispatches, barriers | Every `dotnet test`, every PR, both cheap legs |
| Descriptor invariant, structural plus device-free (V-D2) | Zero `vkAllocateDescriptorSets` and zero `vkUpdateDescriptorSets` between `Begin` and `End`, enforced by unreachability from the recording type AND by a fake-pool counter over every shipped scene shape | Every `dotnet test` |
| `NativeVsVeldridVulkanCapabilityParityTests` (V-T3) | Silent capability drift, ZERO permitted differences, plus the reflection-completeness check | Linux leg, 1x |
| **Validation, two tiers (V-T4)** | The hazard class goldens on a software rasterizer cannot see. `strict` core on the full suite, `sync` on a golden-plus-compute subset | Scheduled runs only |
| Layout dynamic-UBO limit, device-free (V-T4 sibling, V-D6) | A layout that exceeds the Vulkan required minimum of 8 and works on lavapipe but not on a minimum-spec device | Every `dotnet test` |
| SPIR-V byte equality per program (V-T5) | Front-end DRIFT, understood as drift and not as parity (12.1) | Every `dotnet test` |
| The one-off SPIR-V parity measurement against the incumbent (V-S2) | What actually licenses "no rebake". Taken once, recorded here | Once, in-process, before the first golden run |
| Shared uniform-ring semantic tests (V-P5, V-T6) | Section 9.4's SEVEN shared rows, run against BOTH backends' rings through the test-only interface in `KhaozEngine.TestSupport.Gpu`. The other three rows are each backend's own and 9.4's Owner column says which | Every `dotnet test` |
| Ring stride and bind-window invariant, device-free (9.4's Stride row, V-M6) | `rangeOffset + callerDynamicOffset + range <= stride` for every shipped resource-set shape, which is what keeps the effective offset plus range inside the buffer at the last frame slot. The Vulkan half of a row whose D3D11 half is the 16-constant count | Every `dotnet test` |
| `set` and `binding` table test (V-S8) | "Everything compiles and every pixel is wrong" | Every `dotnet test` |
| Staging subresource layout table test (V-C7) | A garbled readback on all 36 goldens at once | Every `dotnet test` |
| Pipeline-layout compatibility guard (V-R7) | That the computed compatible prefix is never longer than the true identical-handle prefix, for every ordered pair of shipped pipelines | Every `dotnet test` |
| Composed `pDynamicOffsets` test (6.2) | An off-by-one in a positional array, which renders plausible garbage rather than throwing | Every `dotnet test` |
| Barrier-shape and resting-layout tests, device-free (V-T7) | Restoration at `End`, no transition from `UNDEFINED` on live contents, one barrier per touched texture per pass rather than per draw | Every `dotnet test` |
| Recording-contract test, device-free | N lists open concurrently, interleaved records, submitted out of record order, per-list order asserted and concatenated in submit order | Every `dotnet test` |
| Acquire-ring index test, device-free (10.2) | Semaphore reuse across a simulated acquire sequence including `OUT_OF_DATE` returns, plus V-W4's boundary: the recreate, the retired semaphore, the one fresh acquire, and the double-failure frame that binds the orphan target and skips its present | Every `dotnet test` |
| Negative viewport height, device-free (7.2) | Every golden upside down | Every `dotnet test` |
| Full lavapipe suite | 0 failed, 0 skipped, passed at or above the incumbent's on the same commit | Linux leg, schedule and dispatch |
| `OpenListTrackingGpuDevice` | Nested `Begin`. Stays the PORTABLE guard, passes trivially here, and is NOT evidence about this backend (2.5) | Every `dotnet test` |
| `GpuFactSkipReasonTests` extension | That `KE_GPU_TESTS=probe` still answers correctly once a second provider registers | Every `dotnet test` |
| `GpuDeviceLifecycleTests` | Concurrent create, use and dispose against the native provider, plus the instance refcount reaching zero | Linux leg |
| `GpuBackendKindAppendAuditTests` | The thirteen sites, extended for `VulkanNative` | Every `dotnet test` |
| `ArchitectureTests`, `VeldridLockdownTests`, `GpuPublicApiTests`, the no-Veldrid pair, the front-end-only edge | Zero renderer changes, no Veldrid leakage, opt-in isolation, that the backend names no cross-compile back-end member, and that neither the descriptor pool (V-D2) nor an image-view factory (V-M11) is reachable from the recording type | Every `dotnet test` |

**The budget test's gate, stated the way T2 states it.** The gate is (a) structural invariants: zero descriptor
allocations and zero descriptor writes during recording, exactly one `vkCmdSetViewport` and one
`vkCmdSetScissor` per framebuffer CHANGE and zero for a redundant rebind, zero barriers between two draws in one
pass that touch no new texture. (b) Marginal per-draw deltas: 5 distinct meshes against 1, and 18 draws against
6, must move the total by an exact per-draw delta, and an offsets-only rebind must be exactly ONE
`vkCmdBindDescriptorSets`. (c) Trace identity for 8 instances of one mesh against 1. (d) Upper bounds on the
per-pass barrier count. Absolute totals are documentation and may be updated freely, because a test that is
routinely edited to match reality stops being a gate.

**CI (V-T8, V-T9), and the cost conversation is different from phase 2's.** A `vulkan-native` matrix leg on
`ubuntu-latest` with `KE_GRAPHICS_BACKEND=vulkan-native`, `KE_VULKAN_DEVICE=llvmpipe`, `KE_GPU_TESTS=1`,
golden-only on push and full suite on schedule and dispatch, sitting out bake dispatches entirely because it is
a guest in the incumbent's family. Linux bills at 1x on a private repo against Windows at 2x, so this leg costs
roughly a quarter of what the `direct3d11-native` leg costs per run at the same tier, which makes it the
cheapest leg the program has added.

Three things that are net-new work rather than configuration:

- **The validation layers are not installed anywhere today** (2.1). The leg installs `vulkan-validationlayers`
  and enables `VK_LAYER_KHRONOS_validation`, and the `sync` job additionally enables the synchronisation feature.
- **The `vulkaninfo` step drops `--summary`** (V-D7), so device limits become observable.
- **The `NativeDeviceLifecycle` collection definition is copied into BOTH test assemblies.** Collection
  definitions are per assembly, and phase 2 measured that adding a second live-device backend without one took a
  leg from 17 minutes to 49 through device build and teardown contention. On lavapipe that contention meets a
  full suite already serialised at roughly twenty-odd minutes, so the Vulkan native full-suite leg is budgeted
  at that order or worse before anyone reads a first schedule run as a hang. Golden-only runs stay parallel,
  which is where the push path lives and where cost actually matters.

The scheduled full suite inherits the incumbent's xUnit collection serialisation on day one, because the
residual lavapipe parallel instability is not this backend's to claim it fixed. MV7 is how that gets found out.

**No FXC-equivalent CI step is needed.** The D3D11 leg has one because nothing else ever ran a compiler over the
shipped programs. The SPIR-V front end already runs device-free on every leg through `ShaderValidation`, so a
program that does not compile fails everywhere already.

**The naming contract is load-bearing and unchanged.** `cross-platform-gpu.yml` selects with
`--filter FullyQualifiedName~Golden`, so any new `[GpuFact]` that must run cross-backend carries that substring
or is added to the full-suite filter explicitly. The device-free tests here deliberately do NOT carry it, so
they run on the cheap legs instead of inside the golden filter.

---

## 16. Unproven bets: gates, kill switches, exit criteria, deadlines

Every decision below rests on reasoning rather than measurement. Each names the measurement that settles it, the
switch that turns it off, the criterion that retires the switch, and **a deadline, which is the gate at which a
second-implementation switch is REMOVED, or the gate by which a bet carrying no such switch must be RESOLVED
(V-RO4, sorted by 2.7)**. A bet without all four is not shipped.

**One switch covers most of them, and that is deliberate.** V-RO2 keeps Veldrid Vulkan selectable by token
indefinitely, so every STRUCTURAL decision here (dynamic rendering, direct recording, the descriptor model, the
allocator, the barrier model) has a working, shipping, one-environment-variable escape. Per-decision switches
are spent only where the incumbent's shape is reachable cheaply INSIDE the native backend, which is two places.
Adding an in-backend fallback path for a structural decision is how phase 2's gate 3 ended up blocked behind an
unresolved A/B with two drivers still shipping, and repeating that would be learning nothing (2.7).

**What is NOT on this list is the headline.** Phase 2's largest bet was whether the deferred recording model was
slower than immediate emit. It needed two complete drivers, a kill switch, an end-to-end A/B and a milestone
that still gates its rollout. There is no M1 analogue here, because Vulkan has a real deferred command buffer
and the list writes into it. Every bet below is a counter reading or a switch flip, and none of them gates an
implementation row.

| # | Bet | Measurement gate | Kill switch | Exit criterion | Deadline: switch removed, or bet resolved |
|---|---|---|---|---|---|
| MV1 | The ring is worth as much on Vulkan as on D3D11, because a record-time `UpdateBuffer` costs a render-pass split plus a full pipeline flush plus a global barrier (9.2). The magnitude is unmeasured: nobody has counted how many record-time `UpdateBuffer` calls per frame the #410 scene makes on Vulkan today | Count record-time `UpdateBuffer` calls, render-pass begins and global barriers per frame on the #410 scene ON THE INCUMBENT, before the ring exists. Then the same three counters on the native backend | None. The ring is not optional on Vulkan (9.2), so there is no in-backend fallback to hold | Native render-pass begins per frame at or below the framebuffer-change count, native record-time global barriers at zero, and frame time no worse than 6.9 ms. If the incumbent's counts turn out near zero already, the ring is still taken because it is the only correct design, and this bet is RECORDED AS NOT PAYING rather than quietly forgotten | Gate 4 |
| MV2 | The semaphore acquire (V-W3) removes a per-frame CPU stall with no presentation regression | Frame time and frame-time variance on the reporting machine with the switch in both positions, same build, same scene, same capture window, **and the frame cap and vsync BOTH OFF**, because the reporting machine otherwise runs pinned at 144 fps and 6.9 ms and the two positions produce the same mean by construction, which is a gate that cannot read its own result. Plus `AcquireWaitCount` and `AcquireWaitMs` (V-G6), which are what actually see the stall, and a human windowed pass | `KE_VULKAN_ACQUIRE=stall`. NOT usable with `KE_VULKAN_VALIDATION`, because `stall` restores a configuration validation rejects (2.6) | On the uncapped capture, semaphore acquire at or better than `stall` on mean and 99th percentile, `AcquireWaitMs` per frame near zero on the semaphore side against a substantial fraction of the frame interval on the `stall` side, and no visual anomaly across two consecutive soak builds | **Gate 4. A second-implementation switch by 2.7's sort, so it is REMOVED there and the blocking path deleted, whichever way it goes** |
| MV3 | `FramesInFlight = 3` is enough that ring segment backpressure and command-buffer slot waits never block the CPU | `BackpressureStallCount` and `BackpressureStallMs`, which the seam already carries, covering BOTH the ring and the command-buffer slot wait on one accumulator. The fold is deliberate (same statement, same lever) and it CHANGES what the shipped member documents, so `BackpressureStallCount`'s doc comment gains the second meaning explicitly in row 19 rather than being widened in silence (section 14) | `KE_VULKAN_FRAMES_IN_FLIGHT=<n>`, owned by row 7 | Stall count zero across a full capture window. A non-zero count means 3 is wrong, not that the design is | Gate 4. A TUNING KNOB by 2.7's sort, keeping no second path alive, so it may survive the gate as a knob, but only if the exit criterion was met at its DEFAULT. A knob is not a way to ship a failed default |
| MV4 | The descriptor model collapses a full activation to ONE `vkCmdBindDescriptorSets` and an offsets-only rebind to ONE, with zero descriptor writes during recording | The device-free budget test (V-T2) plus V-D2's structural check, confirmed on the first green run and then frozen as marginals | None needed. It is a call-count property with no runtime risk | The first green run's measured marginals are recorded in this document's history and become the frozen numbers | Gate 3 |
| MV5 | The resting-layout barrier model (V-F7) costs a bounded number of barriers per frame and does not scale with draws | The device-free barrier-shape test, plus field frame time | None. A wrong barrier model is a correctness failure caught by goldens and validation, not a tuning knob | Barrier count per frame proportional to passes times touched textures, asserted, AND `KE_VULKAN_VALIDATION=sync` clean on the barrier-and-compute job | Gate 3 |
| MV6 | An engine-owned allocator (V-M1) is sufficient without VMA. **This bet is conditional on MV5's sync-validation job existing**, because that is the instrument that sees an aliasing defect (9.1) | `vkAllocateMemory` call count against `maxMemoryAllocationCount` with margin, zero allocation failures across a soak, and peak resident device memory against the incumbent on the same scene | None in-backend. V-RO2 is the escape | Allocation count under a quarter of the device limit and resident memory within 10 per cent of the incumbent | Gate 4 |
| MV7 | The single-instance model (V-N1) does not make the residual lavapipe parallel instability worse, and may improve it | Four consecutive green weekly full-suite runs on the native leg with serialisation ON, then ONE dispatch with `KE_TEST_EXTRA_ARGS` empty | Serialisation stays on by default, so this bet costs nothing if it loses | The unserialised dispatch is green twice, after which serialisation is removed from the NATIVE leg only. The incumbent leg keeps it, because this bet says nothing about the incumbent. Failing changes nothing | Open past gate 5, deliberately. An OBSERVATION FLAG by 2.7's sort, keeping no second path alive and gating no rollout gate, and its cost is one dispatch. **Filed as [#564](https://github.com/APKiwiOrg/KhaozEngine/issues/564)** when row 19 closed, because a bet that outlives the whole program cannot live only in this table |
| MV8 | The `pipelineCacheUUID` key plus header validation is enough that a stale or corrupt `VkPipelineCache` file can never crash a launch (V-S7) | Startup time with a cold and a warm cache, plus a deliberate corruption test that truncates and mutates the file and asserts a clean discard | The cache path is best-effort by construction: any read or write failure is a silent discard, which IS the fallback | Corruption test green and no launch failure attributable to the cache across the soak | Gate 4 |
| MV9 | (observation, not a bet) **The swapchain has ZERO CI coverage on this backend**, because a headless Vulkan device enables no surface extension at all. Every present-path decision in section 11 is validated by a human at a window, or not at all | None available. A native soak that reproduces a presentation defect is consistent with several mechanisms | n/a | Recorded so a reader does not mistake a green golden leg for evidence about presentation. Rollout gate 5's manual pass is the only instrument | n/a |
| MV10 | (observation, not a bet) **No Vulkan device limit is observable in CI today** (2.1), so `maxDescriptorSetUniformBuffersDynamic` and every other limit this design touches currently rest on spec minimums plus assumption. Both drafts asserted vendor-specific values and neither is checkable | V-D7 makes it measurable, which is the entire remedy. Until that lands, no claim about a real device's value appears in this document | n/a | Once V-D7 ships, the observed lavapipe values are recorded here and the spec floor stops being the only fact. **V-D7 has shipped and the recording has not**: the CI `vulkaninfo` step dropped `--summary` in row 1 and row 19 additionally uploads the dump as `vulkan-device-limits-<leg>` on `always()`, so the numbers are quotable verbatim off the first green run instead of scraped out of an expiring job log. Recording them into 2.1 is [#541](https://github.com/APKiwiOrg/KhaozEngine/issues/541), still open | Row 1 of the work breakdown |

---

## 17. Rollout

Opt-in first, then the CI leg as the continuous exercise, then a field soak on the #410 reporting machine
through Ruinborne's established update-feed flow, then the default. Five gates, all green before any flip.

1. **All 36 goldens green** against the shared `vulkan` family on lavapipe at the existing tolerance, with the
   observed worst-cell delta RECORDED here. Two implementations feeding the same rasterizer the same SPIR-V
   bytes should agree far inside the tolerance, and the recorded number is what a future reader compares
   against. The `golden-deltas.<family>.txt` evidence file phase 2 added already appends on a PASS, so the
   number comes off a green run rather than needing one to break first.
2. **Full lavapipe suite at 0 failed and 0 skipped**, with the passed count at or above the incumbent's on the
   same commit, and `KE_VULKAN_VALIDATION=strict` clean. **The skip criterion differs from phase 2's and the
   difference matters.** D3D11's gate turned on two `RequiresCompletionFences` skips becoming runs, because
   the incumbent could not signal on completion. Veldrid Vulkan already can, so those tests already run on this
   leg and the criterion here is NO NEW SKIPS rather than two fewer. A skip is a failed implementation of
   something, whatever else is green.
3. **Budget test green** with the marginals recorded here, MV4 and MV5 met, and the `sync` validation job clean
   on the golden-and-compute subset. **No M1-equivalent hangs over this gate**, because there is one recording
   driver and nothing to A/B, which is the single biggest difference from phase 2's rollout.
4. **A field session on the reporting machine at or above the incumbent Vulkan's numbers** (144 fps, 6.9 ms)
   across a full capture window, with zero device loss, the session header naming `VulkanNative`, and MV1, MV2,
   MV3, MV6 and MV8's exit criteria met. That session is the CAPPED one, because 144 and 6.9 are what it is
   being held to. **MV2's A/B is a SEPARATE capture with the frame cap and vsync both off**, taken with
   `KE_VULKAN_ACQUIRE` in both positions on the same build, and it is read off `AcquireWaitMs` rather than off
   mean frame time. At a pinned 144 fps the two positions have the same mean by construction, so a gate stated
   against the capped session alone could not tell them apart. The switch is removed here.
5. **A human windowed pass**: resize by drag, maximise, fullscreen toggle, alt-tab, and a vsync toggle
   mid-session, on both Windows and Linux. This is gate 5's whole content and it exists because section 11 is
   invisible to CI (MV9). `deviceLossReason` and `softwareAdapter` present in the session header.

**Gate 4 is harder here than it was in phase 2, and the difference is worth naming.** On D3D11 the incumbent
WAS the problem: 125 fps against Vulkan's 144, a field defect with a filed issue, and parity was already a win
because it came with attribution and a debug layer. On Vulkan the incumbent is the engine's BEST backend on the
only field evidence there is. So the pass bar is "no worse over a week", and "no worse" is a harder bar when
there is a real number to lose. Anyone weighing whether this phase should happen at all should weigh that
honestly: the case for it is #420's endpoint plus the pathology in 9.2, not a promised speedup.

**What a flip means here, and it is not what it meant in phase 2.** `ProbeOS` maps Linux to `Vulkan`, so
flipping changes the LINUX desktop default. Windows keeps its D3D11 default, untouched by this phase, and the
headless CI legs pick their backend explicitly. So the blast radius is Linux desktop players plus the Linux
golden leg, and the golden leg is already the continuous exercise. That is a materially smaller population than
phase 2's Windows flip, which is a reason to be confident about the flip and NOT a reason to relax gate 4,
because gate 4 is measured on Windows against the best backend the fleet has.

The flip is one line in `ProbeOS` plus adding the kind to `_windowCandidates`. `Vulkan` through Veldrid stays
selectable by token INDEFINITELY (V-RO2), so a field regression is one environment variable away from an A/B on
the same build. That escape hatch is worth more than the code it costs and it is the primary diagnostic
instrument for a defect that only reproduces on one machine that is not in CI.

The headless default stays on Veldrid until gate 4 (V-RO3). An early headless flip would silently reduce the
incumbent's coverage during exactly the window when both must stay green.

**Veldrid cannot leave the graph when this lands.** Metal is phase 4, and `Veldrid` plus `Veldrid.SPIRV` stay
referenced by `KhaozEngine.Gpu` for the Metal path and for the shader front end regardless. So the endpoint
this package reaches is "the Vulkan leg is engine-owned", not "no Veldrid", and the two Vulkan implementations
coexist for at least one more phase.

Before the field capture, pin the session log's build line and the capture-window stamps. A number attributed to
the wrong build is the expensive failure here, and V-I4's throw-on-missing-provider exists specifically to make
it impossible.

### Rollout record (2026-08-09)

Where the five gates stand as row 19 lands the CI leg, both validation tiers and the doc sweep. **Every gate is
PENDING, and that is the honest reading rather than a hedge**: the leg is committed but has never run, because a
workflow leg cannot be exercised from a developer machine. What this record establishes is that each gate now
has an instrument pointed at it and a place its answer goes, which is the part that was missing.

**Gate 1 is pending its first green run, and its instrument is wired.** The `golden-deltas.<family>.txt`
evidence file appends every compare's worst-cell delta on a PASS as well as a fail, and the leg uploads it as
`golden-deltas-vulkan-native` on `always()`, so the observed number comes off a green run rather than needing
one to break first. Note the naming, which is the same trap the Windows pair has: the ARTIFACT is named for the
leg and the FILE inside it for the family, so `golden-deltas-vulkan-native` contains `golden-deltas.vulkan.txt`,
and downloading both Linux artifacts gives two same-named files that are two implementations measured against
one set of references. Record the worst-cell delta here when the first green run has one.

**Gate 2 is pending, and its skip criterion has a new mechanism behind it.** The full lavapipe suite runs on the
schedule and on a dispatch, serialized, under `KE_VULKAN_VALIDATION=strict`. The criterion is NO NEW SKIPS
rather than phase 2's two-fewer, because Veldrid Vulkan can already signal on completion so the
`RequiresCompletionFences` pair already runs on this leg. What row 19 added for it is
`KE_VULKAN_REQUIRED=1`: the rows that need a native device return early when the probe refuses the machine, and
a dormant row is NOT a skip, so a zero-skipped gate could have been satisfied by rows that asserted nothing. On
this leg that refusal now throws and names what the probe objected to.

**Gate 3 is pending the two halves it does not already have.** MV4's marginals were frozen device-free by row
15 and are asserted on every `dotnet test`, so that half needs no leg. MV5's second half is
`KE_VULKAN_VALIDATION=sync` clean on the golden-and-compute job, which is what row 19 built and which has never
run. **The `sync` job is also the condition MV6's VMA decline was written against**, so a job that does not
exist would have retroactively unpicked a decision, and that is the load-bearing reason it is a gate rather
than a nice-to-have.

**What "clean" MEANS on that job is the job's own scan, not the engine's latch, and the distinction is
load-bearing for this gate.** `VulkanValidationMode.Sync` does not latch and does not throw:
`VulkanValidation.ThrowsOnError` is true for the `strict` rung alone, deliberately, because `strict` exists to
stop at the first error while `sync` exists to finish the sweep and report every hazard in one run. Taken
alone that would make tier two a job that cannot fail, and gate 3 plus MV6's decline would then both be read
off a green that error-severity hazards sail straight through. So the job's last step scans the suite log it
tees and exits non-zero on any error-severity validation line, matching the engine pump's
`Vulkan validation [Error]` format and the Khronos layer's `Validation Error:` prefix, and printing the count
of validation lines at any severity so a scan that has stopped seeing its producer cannot pass as a clean
sweep. Engine-side latching stays strict-only. Gate 3's `sync` half is satisfied by that job going green with
the scan armed, and by nothing weaker.

**Gate 4 is pending the field soak, and nothing in row 19 moves it.** It needs a session on the reporting
machine at or above 144 fps and 6.9 ms with the header naming `VulkanNative`, plus MV1, MV2, MV3, MV6 and MV8's
exit criteria. The counters it reads all reach a capture already. `KE_VULKAN_ACQUIRE` is NOT removed here:
V-RO4 removes it AT gate 4, after MV2's uncapped A/B has been taken with the switch in both positions, and
removing it now would delete the second implementation the measurement compares against. The soak build is
gate 4's, not this row's, for the same reason.

**Gate 5 is pending a human at a window**, on both Windows and Linux, and it is the one gate no amount of CI
can move (MV9). `deviceLossReason` and `softwareAdapter` both ship in the telemetry session header from row 18,
so the header half of gate 5 is met and the windowed pass is the whole of what remains.

**The `ProbeOS` flip is NOT in this row's commits, and that is deliberate.** Section 17 makes the flip
conditional on all five gates, three of which cannot be green before the leg has ever run and two of which need
a human. The flip is one line plus a `_windowCandidates` entry when the gates allow it.

**Two departures from the sections above, corrected in place.** First, the `sync` job runs on
`workflow_dispatch` as well as on the schedule, where 2.8 and section 15 say schedule only. The cost argument
for the subset is unchanged, and this is about aiming the instrument: a job reachable only by the weekly cron
takes its first run unattended on a Sunday and cannot be re-run against a fix inside a week, which for the job
MV5 and MV6 are both read off is a real cost against a saved Linux minute. A bake dispatch is excluded, for the
guest-family reason. Second, 2.8 describes installing the validation layer without saying where, and the
install is scoped to the legs that USE it rather than to the OS. The incumbent Vulkan leg is V-RO2's
indefinitely-selectable escape hatch, and giving it a new apt package would let a package rename on a future
runner image redden the leg the native backend escapes to.

---

## 18. Work breakdown

Each row becomes one implementation issue, `kind/backlog` unless noted, `confidence/authored`, linked to the
phase 3 spec issue.

| # | Scope | Regression evidence |
|---|---|---|
| 1 | Project skeleton, Silk.NET references, architecture rows, `OptInBackends`, README catalog row, package README, slnx, `GpuPublicApiTests` extension, the no-Veldrid pair, AND two verification tasks: the **binding-sufficiency spike** (one file touching every API this design needs, timeline semaphores, `vkCmdBeginRendering`, `vkCmdPipelineBarrier2`, the three surface extensions, `VK_EXT_debug_utils`, compiling against Silk.NET `2.23.0`, plus a Linux smoke run proving the loader resolves without the CI symlink step), and **dropping `--summary` from the CI `vulkaninfo` step** so device limits become observable (V-D7, MV10's deadline) | `check-doc-versions.sh` fails on a packable project without a catalog row. The symlink step exists because Veldrid P/Invokes bare `libvulkan`, and assuming Silk.NET does better without checking would surface in row 17. No Vulkan device limit is observable in CI today |
| 2 | `KhaozEngineVulkan.Register()`, the provider, `IsSupported` functional probe checking apiVersion at or above 1.3, the three mandatory features, **a host-visible `HOST_COHERENT` memory type** (V-M4, which the ring is pinned to), **`maxDescriptorSetUniformBuffersDynamic` at or above what the shipped layouts need** (8.3's fourth defence) and, on the windowed path, a presenting graphics family. The `VulkanBackendRegistration` seat in `KhaozEngine.TestSupport.Gpu`, not in a single test assembly | A silent fallback would let a soak session measure the incumbent and report it as the native backend. Registration living only in `Render.Tests` threw in all four `MapEditor.Tests` GPU tests on the D3D11 leg's first run. The limit read is the only one of 8.3's four defences that answers before a device exists, so a machine below the count falls back instead of throwing partway into a run, and the coherent-type read is what lets 9.2 claim no flush is required |
| 3 | Append `GpuBackendKind.VulkanNative = 5` with the explicit ordinal, tokens, `GoldenBackendToken` mapping at BOTH sites, the generic bake refusal, `GpuBackendKinds.IsVulkan()`, and the thirteen-site audit test extension per 4.2 | `GoldenCompare` lower-cases the kind into the filename at two sites, so a new kind silently orphans 36 goldens. The switch's throwing arm plus the audit test is what makes this a device-free red rather than a GPU-leg mystery |
| 4 | Single refcounted `VkInstance` under the lifecycle gate, device creation with selective feature enable through the `pNext` chain, `KE_VULKAN_DEVICE` selection defaulting to device 0 with substitutions logged, `KE_VULKAN_VALIDATION` with the debug-utils pump, the device-loss latch with Release-mode result checking, `DeviceLiveness`, and `vkDeviceWaitIdle` BEFORE teardown | The lifecycle gate exists because concurrent device creation raced the Vulkan loader on lavapipe. The incumbent takes `physicalDevices[0]` unconditionally, hardcodes apiVersion 1.0.0 at two sites, enables every supported feature wholesale, and destroys the memory manager and pools before it waits. `CheckResult` is `[Conditional("DEBUG")]`, so a latch built on its shape would never fire |
| 5 | **The timeline subsystem, an early prerequisite of 7 and 8.** The device timeline semaphore, `IGpuFence`, `SupportsCompletionFences`, `WaitForIdle` as `vkWaitSemaphores` with `DrainCount` and `DrainMs`, and the deferred-disposal retire list | The ring's segment recycling reads a completion value, so a ring built before the timeline exists is a silent corruption. That dependency edge is the one phase 2's first spec dropped. `RetireFenceGpuTests` and `Scene3DUnloadDrainTests` must RUN and pass |
| 6 | Memory allocator: chunks pooled by `(memoryTypeIndex, tiling)`, first-fit with alignment correction and coalescing free, persistent whole-chunk mapping, dedicated allocations on driver preference, flush and invalidate when the type is not coherent, plus the allocation-count counter (MV6) | The incumbent shares chunks between linear and optimal with per-allocation granularity rounding and has no flush or invalidate anywhere. A memory aliasing corruption is invisible on lavapipe and is what the `sync` validation job exists to catch |
| 7 | Command list: per-slot `VkCommandPool`s reset with `vkResetCommandPool`, buffer slots retired by the timeline, `Begin`, `End` and submit, the narrow `IVkCmdSink` over binds, draws and barriers, the device-free recording-contract test, AND **the `FramesInFlight` depth constant with its `KE_VULKAN_FRAMES_IN_FLIGHT=<n>` override** (MV3's knob, which had no owning row), which row 8's ring READS rather than defining a second one | A `VkCommandPool` is externally synchronised, so a shared pool makes concurrent recording a data race that mostly works. `RESET_COMMAND_BUFFER` forces the driver's slower per-buffer allocator. The knob lands here because this row creates the number and row 8 consumes it, and because the slot index and the ring's frame index are different indexes off one depth (6.1) |
| 8 | Uniform ring on coherent persistently mapped memory: segments, bind-time base, **the bind-window range and its `rangeOffset + callerDynamicOffset + range <= stride` invariant** (V-M6) with the device-free test over every shipped set shape, the ring-backed-view invariant, the per-list staging arena for bulk payloads, `UpdateBuffer` routing at both levels including #484's every-segment rule and its pending-patch queue, the backpressure counters, AND the **shared ring-test project** (V-P5): the test-only interface and BOTH adapters in `KhaozEngine.TestSupport.Gpu`, the `InternalsVisibleTo` grant that costs `KhaozEngine.Gpu.D3D11` one csproj line, **the D3D11-side adapter itself**, and section 9.4's seven shared rows as the checklist. **Depends on 5** | 22 blocking staging maps per frame on the other API. #484's silent two-frames-in-three-read-nothing defect, which this ring must not reintroduce. A ring built against submit receipts recycles a segment the GPU is still reading. A range of `stride` overruns the buffer at the last frame slot for any non-zero caller offset, and five shipped renderers pass one. "Share the tests" with no adapter on the other side quietly becomes one backend's tests |
| 9 | Resources: formats, buffers, textures with resting layouts assigned at creation, **eager `VkImageView` creation from the declared usage bits with NO view factory reachable from the recording type** (V-M11, asserted by the same architecture test that holds V-D2's pool), samplers with the WRAP shared pair, staging as `VkBuffer` with the incumbent's software subresource layout reproduced plus its device-free table test, `Map` and `Unmap` with the read drain, and the device-owned setup command buffer **under its own short lock** (V-W8, 9.3) flushed at the next submit OR at any device-level read | Every golden reads back through `Map` and `RowPitch`, so a different arithmetic garbles all 36 at once. Reading the shared samplers' address mode off the engine statics cost two goldens on the D3D11 leg. The incumbent issues a whole `vkQueueSubmit` per render-target or sampled texture created. All 25 `DEVICE_REMOVED` stacks in #423 surfaced inside the lazy view constructor on the draw path. Free-threaded creation appending to one externally synchronised pool is a data race that mostly works |
| 10 | Descriptors: content-deduplicated set layouts and pipeline layouts, pools with correct per-type accounting including both dynamic types, sets allocated and written at creation **with `offset = 0` and `range` taken from the bind window, `GpuBufferRange.Size` or the buffer's own size, never `VK_WHOLE_SIZE` and never the stride** (V-M6), the dynamic-UBO limit check plus its device-free layout test, and **V-D2's structural enforcement** that the pool is unreachable from the recording type | The incumbent's pool free forgets both dynamic counters, and no layout dedup means every pipeline switch invalidates every set. `SpriteBatch` puts its UBO at `set = 1`, so "set 0 first" is false in shipped code. A stride-sized range is the shape that violates `VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979` at the last frame slot, and this row is where the range is written |
| 11 | Bind flush: two-state per-slot records, contiguous-run `vkCmdBindDescriptorSets`, the composed positional `pDynamicOffsets` of `frameBase + rangeOffset + callerDynamicOffset` **asserted against the descriptor's own range so the effective offset plus range stays inside the buffer at every frame slot** (V-M6), pipeline-layout compatibility-prefix invalidation with **V-R7's conservativeness guard and its validation-build draw assertion**, plus the device-free budget test | An off-by-one in the positional dynamic-offset array reads the wrong slice of the right buffer, which renders plausible garbage rather than throwing. The incumbent fails to reset its accumulated dynamic-offset count between batches in one flush. The offset composed here is the one the VUID measures, so this row and row 10 have to agree or validation fails on the last frame slot only |
| 12 | Dynamic rendering: deferred begin, clear folding into `loadOp`, the clear-only-pass flush, the end-before-illegal-command invariant, and the framebuffer-change-guarded viewport and scissor with NEGATIVE HEIGHT | An unguarded viewport emit silently resets a live scissor, which is golden-visible and which phase 2's first spec froze the wrong way. A positive-height viewport renders every golden upside down |
| 13 | Pipelines: graphics and compute, `VkPipelineRenderingCreateInfo` built from `GpuOutputDescription`, vertex input from the seam's layouts, dynamic state limited to viewport and scissor, the `VkPipelineCache` persisted to disk with header validation and best-effort discard. **Depends on 16**, which creates and dedups the modules | The incumbent passes a null cache at both creation sites and compiles every pipeline from SPIR-V on every launch. A corrupt cache is a crash class, which is what the header validation and the truncation test (MV8) exist for. A pipeline needs `VkShaderModule` handles, which is why 16 is inside the renderable path and not beside it |
| 14 | Barrier and layout tracker: `vkCmdPipelineBarrier2`, per-subresource tracking, LIST-LOCAL state, resting-layout restore at `End`, the `UNDEFINED`-discard rule, plus the device-free barrier-shape tests | The incumbent's transition helper fails a debug assertion on an unhandled layout pair and silently emits `NONE` stage masks in Release. Record-time layout tracking on the texture object is what makes two concurrent lists disagree |
| 15 | Draw and dispatch paths, vertex and index binding, compute rule 1 barriers, dependent-dispatch barriers, MSAA resolve with `MaxMsaaSampleCount` **READ OFF the incumbent's own computation and pinned**, mip generation as a blit chain, copies | The 36 goldens, and the compute `[GpuFact]` suite that proves rule 1 on all three backends. Both drafts invented a different MSAA formula and both then asserted equality with the incumbent, which is the C4 failure phase 2 had to correct in flight |
| 16 | **Shader path, a prerequisite of 13 rather than a parallel row**: the front-end and back-end split of `SpirvCrossCompile`, `vkCreateShaderModule`, module dedup by SPIR-V hash, `SpirvFrontEndPin`, **the one-off in-process SPIR-V parity measurement against the incumbent, taken and RECORDED in this document**, the per-program byte-equality drift test, the architecture test that the backend names no back-end member, and the `set` and `binding` table test | Byte-identical modules are the parity claim the whole golden gate rests on, and the drift test alone does not establish it. Phase 2's own test header records that a wrong emission baked once passes forever. A pipeline cannot be created without module handles, so scheduling this outside the renderable path would block 13 on a row nothing said 13 needed |
| 17 | Swapchain: surface creation per `GpuWindowKind`, present-mode and format reproduction with the two deliberate departures, the acquire ring and per-image render-finished semaphores, `KE_VULKAN_ACQUIRE`, the checked present result, **the `OUT_OF_DATE`-boundary rule in full (V-W4): the same-boundary recreate, the retired semaphore, the one fresh acquire, the orphan target an imageless frame binds, and the skipped present that still counts into `FramesBegun`**, queued resize and present-mode change applied at the present boundary, stable framebuffer identity, the drain-before-retire rule, AND **the `AcquireWaitCount` and `AcquireWaitMs` addition to `GpuDeviceCounters` with its two `GpuTelemetryChannels` names** (V-G6) | The incumbent presents with no wait semaphore, blocks the CPU on acquire every frame, and ignores `vkQueuePresentKHR`'s result entirely. Destroying a pending acquire semaphore is undefined behaviour. Recording against views a recreate destroyed is a use-after-free no leg can see. Zero automated coverage anywhere in the net (MV9). Without the acquire-wait counter MV2's gate reads mean frame time on a machine pinned at 144 fps, where both switch positions are identical by construction |
| 18 | Capability read and the ZERO-difference parity test with its reflection-completeness check, the `GpuDeviceCounters` fill, `GpuDeviceDiagnostics` with `softwareAdapter` and `deviceLossReason` | Capability drift is silent and golden-visible through `AntiAliasing.ResolveFor`. `ClipSpaceYInverted` is the one capability that flips every image |
| 19 | The `vulkan-native` CI leg, **installing `vulkan-validationlayers` and wiring both validation tiers** (`strict` on the full suite, `sync` on the golden-and-compute job), the `NativeDeviceLifecycle` collection in BOTH test assemblies, the seam rule 1 and rule 2 comment rewording (V-C3), the `Begin` XML doc's Vulkan sentence (V-R4), **`BackpressureStallCount`'s doc comment gaining the command-buffer slot wait as its second meaning** (section 14, MV3), the doc sweep below, the soak build, the five rollout gates, and the `ProbeOS` flip | #423 records the push-triggered D3D11 golden gate degraded for weeks without anyone noticing. No validation layer is installed on any leg today, so the gate is net-new work rather than a toggle. A second live-device backend without the collection took one leg from 17 minutes to 49 |

**Order.**

- **1 to 4 are prerequisites** and land first. Row 1's two verification tasks land before anything depends on
  their answers.
- **5 (the timeline) is pulled early**, because 7 and 8 both read it, for the same reason 13a was pulled early
  in phase 2.
- **6 follows 4.**
- **7, 9, 10, 11, 12, 13, 14 and 16 are the minimal renderable path** and follow their own prerequisites. **8
  follows 5** and parallelises with them.
- **16 lands before 13** inside that path, for the same reason 5 lands before 8: a graphics pipeline is created
  from `VkShaderModule` handles, so the shader row is a prerequisite rather than a parallel one. Row 16's later
  half (the parity measurement, the drift test, the two device-free table tests) can follow 13 freely. What may
  not follow it is module creation.
- **15 and 18 parallelise** after theirs.
- **17 can start any time after 4 and lands late**, because CI cannot test it and it should not be the thing
  blocking rows that CI can.
- **19 is last.**

**KESIZE.** The incumbent's `VkGraphicsDevice.cs` is 1667 lines and `VkCommandList.cs` 1361 (counted on
`4.9.103`, whose Vulkan tree is `v4.9.0`, not on the master-based branch, which is V-I6's rule applied to a
line count), against an 800-line cap, which is a warning about what happens without a file plan. The device, instance, allocator, ring,
command list, bind flush, barrier tracker, descriptor pool, pipeline factory, resource factory, swapchain and
shader path are twelve types by construction, so the ratchet is satisfied by design rather than by a late split.
**The precedent says this is achievable rather than hopeful**: `KhaozEngine.Gpu.D3D11` is 17,713 lines across
110 files with its largest at 776, and it has ZERO entries in `.filesize-baseline` (verified). No baseline edit
should be needed here either, and if one is, that is the user's call.

**Doc sweep this phase owes beyond the guard-checked set.** The root `README.md` catalog row and the package
README are guard-checked and land in row 1. These are not, and land in row 19: `docs/DEPENDENCY-SEAMS.md`'s
out-of-package graphics backends section gains the second instance of the inverted edge, and its
"where to look in the code" GPU row still names only `VeldridGpuDevice` and has never been updated for
`Gpu.D3D11` either. `docs/USING-KHAOZENGINE.md` gains the backend-selection token and the `KE_VULKAN_*`
variables. `docs/CROSS-PLATFORM.md`'s platform-to-backend mapping gains the native Vulkan leg.
`GpuInterfaces.cs`'s `Begin` XML doc gains its Vulkan sentence, and its rule 1 and rule 2 comment must be
reworded to name the implementation it describes, or it becomes false the day this backend ships.
`GpuDeviceCounters.cs` gains the acquire-wait pair's own doc and the second meaning
`BackpressureStallCount` acquires here (section 14). And
`docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md` is on this list, which a design doc usually is not: its
section 16 promises a phase 3 that extracts the recorder and the emitter into a shared home, which 2.2 declines,
so it carries a corrected-in-place note in that doc's own established style rather than being left to read as a
plan somebody is still executing.

---

## 19. Relationship to #420 phase 4 (Metal)

**What this design deliberately leaves ready.**

- The provider registry, the golden-guest pattern, the capability-parity pattern, the opt-in package shape, the
  append audit test, the counters plumbing, the telemetry header fields and the CI matrix leg are all templates,
  now proven TWICE. Two is the point at which a template is worth trusting.
- **The front-end and back-end split of `SpirvCrossCompile` (V-S3) is the whole shader story for Metal.** Metal
  adds an MSL target to the BACK end, and F2's swap to direct SPIRV-Cross bindings becomes a change to one half
  of one file with exactly one consumer family, evaluated against Metal's own fresh goldens rather than against
  D3D11's committed ones. That split is the entire Metal-facing carrying cost this phase pays, and it is one
  file move.
- **A correction phase 2 shipped that phase 4 must read.** `ResourceBindingModel` is not a member of
  `CrossCompileOptions` and reaches neither the D3D11 backend nor SPIRV-Cross. In the vendored fork the only
  backend that reads it is METAL, and it is where Metal's argument-buffer layout gets decided. It belongs in the
  phase-4 brief and nowhere else (12.1).
- Metal's model is closer to Vulkan's than to D3D11's in every respect that mattered here: real deferred command
  buffers, real completion callbacks, explicit render pass descriptors with load and store actions that map
  onto `VkRenderingAttachmentInfo` nearly one to one, and argument buffers that map onto descriptor sets. The
  resting-layout composability model, the timeline-as-theorem argument, the record-then-flush schedule and the
  deferred-begin clear folding should all survive contact with Metal largely intact.
  (**Confirmed 2026-08-11, with the score.** `METAL-NATIVE-BACKEND-DESIGN-2026-08-09` section 7.1 records the
  central claim holding: `MTLRenderPassDescriptor`'s per-attachment `texture`, `loadAction`, `clearColor` and
  `storeAction` map onto `VkRenderingAttachmentInfo`'s `imageView`, `loadOp`, `clearValue` and `storeOp` almost
  member for member, so V-A1 through V-A6 ported with Metal nouns and no argument, and Metal has carried those
  actions since Metal 1 where Vulkan needed dynamic rendering to get them. **Three of the four named here
  survived and one did not, in a way worth reading.** The resting-layout model has NO Metal occupant at all,
  because automatic hazard tracking means there are no layouts to rest in. The timeline argument survived and
  paid twice, since the shared event is what made the timeline bookkeeping extractable at all (2.8). The clear
  folding survived and found a defect in the incumbent on the way through (2.4). The record-then-flush schedule
  survived as a SHAPE and not as code: Metal's flush is per (kind, stage) array calls with an encoder-boundary
  call class neither neighbour has, which is exactly why 2.8 refuses to extract it. What the prediction got
  wrong is not any of the four, it is the assumption that "closer to Vulkan" implies shareable, and section 2.8
  is where that distinction is finally written down.)
- The shader-shape invariant Metal depends on is written down and defended here (V-S6) rather than being an
  unwritten property a Vulkan-only author would have broken.

**What this design deliberately refuses to pre-build.**

- **The shared home (2.2).** Nothing is extracted. The reason is a phase-4 reason: the backend most likely to be
  the OUTLIER of the eventual three is D3D11, so extracting from D3D11 and Vulkan today produces an abstraction
  shaped by the outlier and then asks Metal to fit it. **So the code most likely to be genuinely common across
  the fleet is the code in THIS package, not the code in `Gpu.D3D11`**, and waiting for Metal means the common
  shape is observed rather than predicted. The extraction issue is filed now with phase 4 as its trigger, which
  makes it scheduled debt rather than forgotten debt, and section 9.4's inventory is what carries the policy
  across in the meantime.
- **A generic emitter interface.** The D3D11 emitter exists to drive two D3D11 drivers, which is a problem only
  D3D11 has. Promoting it would produce a second copy of `IGpuCommandList` in engine types that every backend
  must then keep in sync with the seam. If phase 4 wants one it should be shaped by Vulkan and Metal.

**What this design makes harder, stated plainly.**

- Two more backends' worth of ring allocator and bind-flush code exist without a shared home. That is the
  deliberate deferral above, and a reader will notice the duplication.
- The barrier and layout tracker is the exception in the other direction: Metal tracks hazards automatically by
  default, so V-F6's tracker is Vulkan-specific and does NOT port. Said here so a phase-4 reader does not go
  looking for it.
- The `KE_VULKAN_*` variable family is a third dialect beside `KE_D3D11_*`. A follow-up proposes the common
  spelling once three backends exist and the shared subset is observable, which is the same rule V-P4 applies to
  code.
- `Veldrid` and `Veldrid.SPIRV` both stay in the graph after this phase. Vulkan retires neither. What it retires
  is the last reason to believe the front end and the back end cannot be separated.
- **Three engine-owned backends now honour rule 2 by paying a drain**, so F1's automatic-hazard seam capability
  stops being a Vulkan-shaped nuisance and becomes a fleet-wide one with three known answers. That is a better
  position to file from and a worse position to keep ignoring.

---

## 20. Rejected from both drafts

| Rejected | Why |
|---|---|
| **Extracting a shared home from `Gpu.D3D11` in this phase** (the reuse-first draft's row 1) | Its own list of six portable lessons does not survive inspection: two are D3D11 mechanisms, two are rules both drafts specify independently with no extraction, one is already a written seam contract, and one the draft itself INVERTS four pages later. The two that remain are one policy, which is better carried by shared TESTS than by shared code (2.2) |
| **The three-state dirty schedule** | Its own author concedes the third state collapses to two at the emitter and justifies keeping it purely by code sharing with D3D11, which 2.2 removed. `pDynamicOffsets` is positional and every ring base moves every frame, so the array is recomposed on any bind regardless (2.4) |
| **The classic `VkRenderPass` port** | The thing being ported is not known-correct: three passes per framebuffer, no cache, no dedup, rebuilt on every resize. Doing it properly means writing two caches and an invalidation problem, so "port a correct thing" was never the choice on offer. Dynamic rendering deletes both caches and the seam already hands it its input verbatim (2.3) |
| **The blunt clear-all on every pipeline switch** | Reproduces the incumbent's full rebind by construction rather than by choice, and its own draft files the refinement as a follow-up. With content-deduplicated set layouts the compatibility test is a pointer compare (2.4) |
| **Per-decision kill switches for STRUCTURAL decisions** (the uniform-routing switch, the instance-model switch) | Each keeps a second implementation shipping, which is exactly how phase 2's gate 3 ended up blocked behind an unresolved A/B. The two BRANCH switches both drafts wanted are kept. Every surviving switch now carries a decision deadline (2.7, V-RO4) |
| **A first-principles `MaxMsaaSampleCount` formula** (both drafts invented one, and they differ) | At most one could have equalled the incumbent's, and both then asserted equality with the incumbent as a test. That is precisely the C4 failure phase 2 corrected in flight. The computation is read off the incumbent and pinned (V-C5, section 13) |
| **Gating the descriptor invariant on a counting seam** (both drafts claim it) | Neither seam can see `vkAllocateDescriptorSets` or `vkUpdateDescriptorSets`, because neither is a bind, a draw or a barrier. The enforcement is structural: the pool is unreachable from the recording type (V-D2, 6.3) |
| **Treating validation as an environment-variable toggle** (both drafts) | No validation layer is installed on any CI leg today (verified). Enabling the gate is net-new CI work: install the package, enable the layer, and separately enable the synchronisation feature (2.8) |
| **A single undifferentiated validation gate** (one draft ran sync validation over the whole scheduled suite, the other never mentioned sync validation) | Sync validation is the only instrument that sees the missing-barrier and wrong-layout class, so it must exist. It is also slow, on a job already serialised at roughly twenty-odd minutes, so it runs on a separate golden-and-compute subset while core validation sweeps the full suite (2.8) |
| **Asserting real device values for `maxDescriptorSetUniformBuffersDynamic`** (one draft said AMD reports exactly 8, the other that lavapipe and NVIDIA report far higher) | Neither is observable anywhere in this repo. The spec floor of 8 is sound and everything past it is demoted. V-D7 makes the question measurable instead (2.1) |
| **Citing the incumbent by line number** (both drafts, from two different trees) | One draft's cited lines come from the master-based branch and describe a barrier the shipped package does not carry. The repo already records that phase 2's cited line numbers went stale. Cite members (V-I6, 2.1) |
| **Scoring the physical device by default** | A user-visible device change unrelated to swapping the backend, breaking `DeviceName` parity in a design demanding zero capability differences, and adding a second variable to the one gate that must isolate the swap. `KE_VULKAN_DEVICE` provides it explicitly and the default change is a follow-up (2.9) |
| **Asserting the incumbent's staging rows are tightly packed and moving on** | It may well be right and it is the wrong posture, because the goldens depend on the arithmetic and not on the assertion. Reproduce it and pin it in a table test taken from the incumbent's own computation (V-C7) |
| **An engine-owned CPU op stream in front of `VkCommandBuffer`** | A second deferral on top of the driver's own, doubling the encode, adding an allocation, and moving driver-side encode inside the submit lock. Phase 2's section 16 said so before either draft existed |
| **VMA or any native allocator dependency** | No maintained managed binding, so the real proposal is a per-RID native library added to a backend built to reduce native surface. The steady-state frame allocates nothing. Vortice is a binding, VMA is a policy engine, and the analogy does not carry (9.1) |
| **Direct SPIRV-Cross bindings in this phase** | Vulkan consumes no cross-compiled output, so the swap would put D3D11's 36 goldens and both WARP corruption workarounds in play in the phase whose CI leg cannot see any of it (12.3) |
| **Descriptor indexing, bindless, push constants and descriptor buffers** | No consumer (the hot path is offsets-only rebinds of ONE set), every route changes the SHARED GLSL and puts all three backends' pixels in play at once, and it would weaken the byte-identical-SPIR-V parity claim. Reopening trigger named (8.4) |
| **A dedicated transfer queue or async compute** | Queue-family ownership transfers, a second submit lock and cross-queue semaphores, for uploads measured in megabytes at load time and one compute chain already gated by rule 2 |
| **Supporting a separate present queue family** | The configuration has never worked in this fork, nobody noticed, and no fleet target produces it. Rejected at the probe with a named reason (5.2) |
| **Automatic dispatch-to-dispatch barriers presented as making rule 2 unnecessary** | The barrier IS emitted, and it is EVIDENCE for F1 rather than a contract change. Rewriting rule 2 would make the three backends disagree invisibly until somebody drops the drain on Vulkan's strength and it breaks on Metal (section 13) |
| **Reproducing the incumbent's semaphore-free present** | Presenting without the specified synchronisation is a tolerated spec violation, not a field-proven configuration, and it directly contradicts the validation gate (2.6) |
| **Reproducing `[Conditional("DEBUG")]` result checking** | A device-loss latch cannot be built on checks that compile away, and #427 asks for exactly that latch (2.9) |
| **Reproducing the debug callback that throws and breaks into the debugger from inside a driver callback** | Unwinding a managed exception through native frames is not a diagnostic |
| **Reproducing the hardcoded `preTransform = IDENTITY` and the always-true sRGB format fallback** | Both are bugs rather than behaviours, unreachable on the fleet's targets, and reproducing a bug a different device WOULD reach is not parity |
| **Changing the present-mode preference order, including `FIFO_RELAXED` under a vsync request** | It is arguably wrong and it is #380's variable. Moving it here would corrupt that measurement (11.1) |
| **`storeOp = DONT_CARE` for depth** | Leaves contents undefined, and undefined is not stable across runs, which the goldens require (7.1) |
| **`vkQueueWaitIdle` for `WaitForIdle`** | Holds the queue lock and gives nothing to count. `vkWaitSemaphores` on the device timeline is the same guarantee, countable and lock-free |
| **Per-submit `VkFence` objects instead of one device timeline** | The seam's documented fence ordering becomes a convention rather than a theorem, and the `GpuRetireQueue` depends on it (10.1) |
| **`vkCmdUpdateBuffer` for per-frame uniform writes** | Capped at 65536 bytes, must be outside a render pass, and is therefore the same render-pass split the ring exists to remove |
| **N descriptor sets per resource set, one per frame** | Multiplies descriptor sets by `FramesInFlight`, breaks the write-once immutable-set invariant, and reintroduces the per-frame descriptor bookkeeping this design most wants none of |
| **Hand-rolled Vulkan P/Invoke, TerraFX, and vendoring Veldrid's `Vulkan.*` bindings** | Thousands of lines where every mistake is a memory corruption, a second unfamiliar vendor with no loader helpers, and Veldrid-derived code inside the backend built to remove Veldrid (3.1) |
| **A new `KhaozEngine.Gpu.Backend` package for extracted machinery** | Needs a PUBLIC emitter and recorder to serve two consumers we both own, which is the "new public API with no external consumer" pattern phase 2 already rejected once, plus a catalog row, a package README and a third link in every chain |
| **Owning a `vulkan-native` golden family** | A guest verifies the incumbent's committed references on the same rasterizer, which is a second implementation checking the first. A family of its own would check nothing |
| **Persisting `VkPipelineCache` without header validation, and deferring it entirely** | The vendor-supplied `pipelineCacheUUID` IS the validity key, so deferring over "it needs one" is answered by the API. A corrupt file is still a crash class, so the header is validated and the path is best-effort (12.4) |
| **Secondary command buffers, `vkCmdDrawIndirect`, and specialization constants** | The seam has no sub-list concept, no indirect draw and no specialization constants. New machinery with no consumer, and the incumbent's specialization path hands the driver a pointer to uninitialized stack memory |
| **Removing the D3D11 holed-signature sinks because Vulkan tolerates them** | FXC-and-WARP specific, and the D3D11 leg ships indefinitely. Removing one corrupts WARP. Written down because it will be proposed (12.3) |
| **Adding `SetViewport` to the seam** | 48 `SetFramebuffer` sites and zero viewport sites, unchanged since phase 2 rejected it. A reasonable phase-4 addition when the seam is being revisited anyway |
| **Claiming this backend fixes the residual lavapipe parallel instability** | It might, and MV7 is how that is found out at zero cost. Serialisation stays on by default until it is |

---

## 21. Follow-ups this design knowingly leaves open

Filed as issues when this spec lands, not discovered later.

- **VF1.** Extract the shared home for the uniform ring, the record-then-flush schedule and the counting seam,
  TRIGGERED by phase 4 landing, when three implementations make the common shape observable rather than
  predicted (2.2, section 19). Section 9.4's inventory is the interim carrier.
- **VF2.** Unify the `KE_D3D11_*` and `KE_VULKAN_*` variable families into a `KE_GPU_*` core plus per-backend
  extras, on the same trigger and for the same reason.
- **VF3.** Prefer a discrete physical device by default, with its own change note, once the soak's A/B no longer
  needs one variable (2.9, V-N3).
- **VF4.** A dedicated transfer queue and async compute, with the FFT ocean named as the first consumer that
  would justify the queue-ownership machinery.
- **VF5.** Descriptor indexing, reopened by a consumer needing per-draw material variety beyond one dynamic
  offset, which today means a texture-array atlas the splat terrain cannot express (8.4).
- **VF6.** Offline SPIR-V baking at build time, which needs the shader splicing (the GLSL is assembled from
  shared blocks in C# at runtime) to move into the build.
- **VF7.** The `Vortice.Vulkan` reconsideration, closed as not planned unless row 1's binding spike finds
  Silk.NET `2.23.0` missing something core-1.3 this design needs. Written down so the alternative is findable
  rather than re-argued (3.1).
- **VF8.** A Vulkan mobile or tiler head would want the attachment-lifetime information a render pass carries
  and dynamic rendering does not. No consumer today, recorded so the trade is not rediscovered (2.3).
- **VF9.** Vulkan on macOS through MoltenVK and `VK_KHR_portability_subset`. Not needed, because Metal ships,
  and recorded here so it is not re-raised as a gap.
- **VF10.** F1 (the automatic-hazard seam capability) now has two of three backends able to answer yes and one
  that cannot, which is the quorum that makes it specifiable. Advance it rather than leaving it as a D3D11 note
  (section 13, section 19).
- **VF11.** Retire the Veldrid Vulkan leg, its extension list and the CI `libvulkan` symlink step TOGETHER, and
  only once phase 4 removes Veldrid entirely. Not before (section 17).
- **VF12.** Record the observed lavapipe device limits here once V-D7's `vulkaninfo` change has run, so
  MV10 stops being an observation and becomes a measurement.
