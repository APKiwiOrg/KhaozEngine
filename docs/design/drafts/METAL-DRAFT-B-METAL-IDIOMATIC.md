# DRAFT B: KhaozEngine.Gpu.Metal, argued from Metal's own model (2026-08-09)

**This is one of two competing drafts for phase 4 of [#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420),
specified by [#566](https://github.com/APKiwiOrg/KhaozEngine/issues/566). It is not the design. An adjudicator
rules between this and the reuse-first draft, exactly as phases 2 and 3 were adjudicated.** Nothing here has run
on a device.

**The prior.** This draft argues from Metal outward. Where Metal's own model already provides what the two
prior phases had to build, the ruling is to use the platform's shape and delete the machinery. Where the
proven phase-2 or phase-3 shape is right for Metal too, the ruling says so and reuses it, and section 2.9
pulls out the four places that happens so a reader can see the prior being overridden rather than only
confirmed.

Written against engine `17.34.0` (`Directory.Build.props`). The incumbent this design replaces and must reach
parity with is **the vendored fork's Metal backend at `Veldrid 4.9.103`**. Section 2.1 establishes what that
code actually does, because two of this draft's strongest arguments turn on defects in it that neither the
program's banked inputs nor the engine's own memory records.

Every citation into the incumbent names a MEMBER rather than a line number, per V-I6, which the Vulkan phase
adopted after both of its drafts cited stale lines from two different trees.

---

## 1. Decisions

| # | Area | Decision |
|---|---|---|
| M-P1 | Package | New `KhaozEngine.Gpu.Metal`, opt-in, outside every umbrella, `net10.0` (NOT `net10.0-macos`), with `[SupportedOSPlatformGuard("macos")]` entry points and `NoInlining` bodies behind `OperatingSystem.IsMacOS()`. The CA1416 apparatus `Gpu.Vulkan` did not need DOES have an analogue here, because Metal is an OS-specific API and the assembly must still load and its device-free tests still run on the Linux and Windows legs |
| M-P2 | Binding | An ENGINE-OWNED Objective-C interop layer, `[LibraryImport]` over `objc_msgSend` with blittable-only signatures. No binding package exists to take (Silk.NET has no Metal binding of consequence) and the fork's `Veldrid.MetalBindings` is not vendored. Section 3.1 argues all four routes |
| M-P3 | Layering | References `KhaozEngine.Gpu` and NOTHING else. This is the only one of the three native backends with zero third-party dependencies, so `ArchitectureTests.ThirdPartyHomes` gains no row at all, and the no-Veldrid pair (csproj read plus IL reference walk) is extended in both forms |
| M-P4 | Shared home | #531's extraction IS TAKEN, per candidate rather than in bulk, and it takes FOUR things and refuses the rest: the liveness latch, the counter accumulators, the diagnostic rate limiter, and the shader-cache key-and-file discipline. The recorder, the emitter, the flush schedule and the ring MECHANISM stay per backend. Section 2.2 walks every candidate against three implementations |
| M-P5 | Shared home | The ring's shared SEMANTIC tests gain a Metal adapter in `KhaozEngine.TestSupport.Gpu`, which retires section 9.4 of the Vulkan design (the policy inventory) into executable form for all three backends. That is where the ring's commonality was always going to land, and it is why the ring's CODE is not extracted |
| M-P6 | Shared home | The extraction is its own row, AFTER the backend passes gate 3, never interleaved with it. Refactoring two shipped backends inside the phase whose gate is a golden family is how a golden failure stops being attributable |
| M-I1 | Identity | Append `GpuBackendKind.MetalNative = 6` with an explicit ordinal and the append-only comment. New tokens `metal-native` and `mtl-native`, added to `RecognizedTokens` |
| M-I2 | Goldens | GUEST in the committed `metal` family through `GoldenBackendToken`, bake refusal derived generically as the Vulkan phase made it. **And the asymmetry is named: the `metal` family is the FLEET's cross-backend reference**, so a metal-family disagreement is not a leg event the way a WARP or lavapipe disagreement is |
| M-I3 | Identity | `GpuBackendKinds.IsMetal()` beside `IsDirect3D11()` and `IsVulkan()`, and it is LOAD-BEARING here where its two siblings were tidiness: `FrameCap.Resolve` and `DisplaySettings` both gate on `GpuBackendKind.Metal` today, so this is the program's first append whose FrameCap row is not correct by default. Section 4.2 |
| M-I4 | Identity | A missing provider registration THROWS. An incapable machine answers false at `IsSupported`. `PreflightProvider` already fixes the order so a wiring fault cannot present as an incapable machine |
| M-N1 | Device | `MTLCreateSystemDefaultDevice()` is the DEFAULT, reproducing the incumbent, with `KE_METAL_DEVICE=<index>|<substring>|lowpower|removable` explicit selection over `MTLCopyAllDevices()` and any substitution LOGGED. The 2.9 argument from phase 3 applies unchanged: changing which GPU the engine runs on is a user-visible change unrelated to swapping the backend, and it breaks `DeviceName` parity under a zero-difference bar |
| M-N2 | Queue | ONE `MTLCommandQueue`. Command buffers execute in ENQUEUE order on a queue, `commit` enqueues if not already enqueued, and committing under the submit lock therefore makes SUBMIT ORDER the observable order by construction. A second queue buys nothing without cross-queue events |
| M-N3 | Device | New capability reads use `supportsFamily:` (`MTLGPUFamilyApple*` / `Mac2` / `Metal3`). The incumbent's `supportsFeatureSet:` enumeration is NOT reproduced for new questions, because it has been deprecated since macOS 10.15 and its answer feeds an equality test that silently disables vsync (M-W2). PARITY surfaces are the exception and reproduce the incumbent's own question (M-G1, M-C3) |
| M-N4 | Lifecycle | `NSAutoreleasePool` discipline is a stated rule with a test rather than a habit: every public entry point that can create an autoreleased object wraps its body in a pool. The incumbent does this at four sites and not at others, which is the shape that leaks under load |
| M-R1 | Recording | `MetalCommandList` encodes DIRECTLY into an `MTLCommandBuffer` through `MTLRenderCommandEncoder`, `MTLBlitCommandEncoder` and `MTLComputeCommandEncoder` at record time. No op stream, no second driver, no `KE_METAL_RECORD`, no M1 analogue. Metal's encoders ARE the deferred command buffer that phase 2 had to build in managed memory |
| M-R2 | Recording | ONE `MTLCommandBuffer` per `Begin`, taken from the queue, committed at `Submit`. There is no command-buffer pool to reset, because the queue owns that allocation, so V-R2's per-slot `VkCommandPool` ring has no occupant. The FramesInFlight gate survives, and it lives on the uniform ring's acquire ALONE |
| M-R3 | Recording | N lists record concurrently and genuinely. Different `MTLCommandBuffer`s are independent objects and each is encoded on one thread at a time, and this design has no shared record-time state at all (no layout tracker, no barrier tracker, no device state cache). The PORTABLE seam contract is unchanged at one open recording per device |
| M-R4 | Recording | Encoder state is PER ENCODER, and that is a hard property of the API rather than a design choice. Ending a render encoder discards the bound pipeline, every argument-table entry, the viewport and the scissor, so the dirty model is ENCODER-SCOPED and re-activates at the first draw after any encoder boundary. The incumbent already does this and it is the single most consequential structural fact in the backend |
| M-R5 | Recording | TWO-state per-slot dirty records, not three. `DynamicOffsetsOnly` exists on D3D11 to skip textures and samplers, and here the offsets-only path is a DIFFERENT CALL (`setVertexBufferOffset:atIndex:`) rather than a cheaper variant of the same one, so the state that would carry it is a fourth thing the flush already knows from the record |
| M-R6 | Recording | A full activation emits ONE ARRAY CALL per (kind, stage): `setVertexBuffers:offsets:withRange:`, `setFragmentTextures:withRange:`, `setFragmentSamplerStates:withRange:` and their siblings. The incumbent emits one call per element per stage, which is the exact #418 fan-out defect on a second API, and the fork's binding layer does not even declare the array setters |
| M-R7 | Recording | The offsets-only rebind is ONE `setVertexBufferOffset:atIndex:` or `setFragmentBufferOffset:atIndex:` per visible stage. No buffer rebind, no argument-table write, an integer into the encoder's stream. This is the Metal occupant of `*SetConstantBuffers1`'s first-constant and of `pDynamicOffsets`, and it is cheaper than both |
| M-R8 | Recording | `SetPipeline` gains an IDENTITY GUARD the incumbent lacks. `MTLCommandList.SetPipelineCore` unconditionally sets `_graphicsPipelineChanged` and clears the whole active-set array, so a redundant pipeline bind costs a five-call state re-emit plus a full re-activation of every set |
| M-R9 | Recording | A pipeline switch invalidates recorded slots only when the pipeline's computed BASE INDEX VECTOR differs from the outgoing one. Metal's argument tables are per encoder and per stage and indexed absolutely, so a bound resource survives a pipeline switch, but the flattened base of set N is a property of the PIPELINE's layout array. Content-deduplicating the layout objects makes that comparison a handle-array compare. This is the Metal occupant of R5's clause 5 and of V-R6, and it exists for a third reason again (absolute index arithmetic, not register numbering and not binding validity) |
| M-B1 | Binding | The `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]` index scheme is OWNED by the backend and PINNED into the MSL emission through SPIRV-Cross's MSL resource-binding API. The CPU side computes the same numbers from the seam's layouts, and a device-free table test parses the emitted MSL's attributes for every shipped program and asserts the two agree. This is S2's discipline with the emitted artefact available for the first time |
| M-B2 | Binding | `ResourceBindingModel` is NOT reproduced, in either value. It exists in the fork only to paper over vertex buffers and resource buffers colliding in one `[[buffer(n)]]` space. Vertex buffers are pinned at the TOP of the space (30 downward) and resource buffers from 0 upward, so neither numbering depends on the other's count and the collision the option manages cannot occur |
| M-B3 | Binding | The one-uniform-buffer-per-pipeline constraint is treated as a HYPOTHESIS ABOUT THE INCUMBENT'S NUMBERING, not as a property of Metal, and falsifying it is a named measurement of this phase (MM6). **The shader-shape invariant V-S6 defends STAYS until that measurement passes.** This design does not promise to fix it and does not ship a shader change on the strength of it |
| M-B4 | Binding | SPIRV-Cross's MSL habit of numbering textures by FIRST-SAMPLE ORDER rather than by `binding=` is removed by M-B1's explicit pin. The shader-side "sample all textures up front in binding order" workaround STAYS anyway, because the Veldrid Metal leg ships alongside, which is V-S5's rule applied to the Metal-specific twin |
| M-B5 | Binding | NO argument buffers, NO `MTLHeap`, NO bindless, NO `setBytes` as the uniform path. Section 8.4 argues each decline against this draft's own grain and names the trigger that reopens it |
| M-A1 | Passes | `MTLRenderPassDescriptor` per pass with NATIVE `loadAction` and `storeAction`. The begin is DEFERRED to the first draw so a clear recorded after `SetFramebuffer` folds into `loadAction = Clear`. On Vulkan that was an adaptation of a general API, and here it is the API's own model |
| M-A2 | Passes | **PER-ATTACHMENT clears. The incumbent's `colorAttachments[0]` collapse is a DEFECT and is not reproduced.** Its clear loop indexes attachment 0 for every iteration, so on the engine's three-target model framebuffer only attachment 0 is ever cleared and the other two silently keep `loadAction = Load`. `ModelRenderer.BeginModelPass` carries a shipped comment working around it. Kill switch `KE_METAL_CLEAR=attachment0`, removed at gate 1 |
| M-A3 | Passes | The clear-only case is reproduced DELIBERATELY: `SetFramebuffer` plus a clear plus `End` with no draw must still clear. The incumbent forces it at two sites and a golden depends on it |
| M-A4 | Passes | `storeAction` is set EXPLICITLY to `Store` for colour and depth rather than left to the descriptor's default, which is what the incumbent does for colour. Not optimised to `DontCare`: undefined contents are not stable across runs and the goldens require stability (V-A6's argument, unchanged) |
| M-A5 | Passes | Any command illegal inside a render encoder (blit, compute, resolve, mip generation) ENDS the encoder first. One invariant, one helper, one device-free test. On Vulkan that was a chosen discipline and here it is the API's rule |
| M-A6 | Viewport | `SetFramebuffer` emits the full viewport and the full scissor ON A FRAMEBUFFER CHANGE ONLY, reproducing W6's identity guard exactly, and the scissor flush stays gated on the pipeline's `ScissorTestEnabled` the way the incumbent gates it, because that is the seam's own rasterizer state and D3D11 honours it too |
| M-A7 | Viewport | The plural `setViewports:count:` and `setScissorRects:count:` forms are used unconditionally rather than behind the incumbent's `macOS_GPUFamily1_v3` feature-set test, and the count is always 1 because the seam has no multi-viewport concept. One code path, no deprecated enum read on the hot path |
| M-M1 | Memory | NO allocator and NO `MTLHeap`. Metal owns device memory, `newBufferWithLength:options:` is the allocation, and a heap's resources are UNTRACKED by default, which would trade the automatic hazard tracking this whole design rests on for a suballocator the workload has no use for. This is the exact inverse of V-M1 and the reason is the platform's rather than a preference |
| M-M2 | Memory | Every buffer is `MTLStorageModeShared`, reproducing the incumbent, so on unified memory every buffer is directly CPU-writable with no staging path at all. `StorageModeManaged` with `didModifyRange:` for discrete Intel Macs is a follow-up, recorded rather than built |
| M-M3 | Uniforms | Every `UniformBuffer`-usage buffer is one `MTLBuffer` of `stride * FramesInFlight`, persistently CPU-visible through `contents()`, where `stride = align(size, max(256, minUniformBufferOffsetAlignment))`. The incumbent reports 16 on macOS and 256 on iOS, so flooring at 256 makes the number device-independent, which is V-M5's own reasoning arriving at the same floor from a different direction. `FramesInFlight = 3` |
| M-M4 | Uniforms | The ring's bind-time base rides `setVertexBufferOffset:` / `setFragmentBufferOffset:` (M-R7). There is no descriptor range to overrun and no 16-constant count to round, so the Vulkan VUID invariant and the D3D11 constant-count invariant BOTH collapse here to `frameBase + rangeOffset + callerDynamicOffset + size <= (frame + 1) * stride`, asserted device-free over every shipped set shape |
| M-M5 | Uniforms | #484's every-segment off-timeline rule is adopted WHOLESALE, unmodified, including the pending-patch queue and the never-wait property. It cost a consumer defect to learn once and it is now the third implementation of it |
| M-M6 | Uniforms | U3's two creation-time invariants adopted VERBATIM, including the part that matters most: a ring-backed buffer receiving a non-uniform binding is a BACKEND-DIVERGENT CREATION FAILURE and is documented as one |
| M-M7 | Uniforms | The ring is a CORRECTNESS change on Metal and not only a cost change. The incumbent's device-level `UpdateBuffer` is an ungated `memcpy` into `contents()` of a buffer the GPU may be reading from a submitted command buffer, with no fence, no gate and no diagnostic. Metal renames nothing under a write, so that is a plain data race |
| M-M8 | Uploads | Record-time bulk payloads take a per-list Shared staging arena, sub-allocated and recycled at slot retirement, and one blit copy. The incumbent allocates a whole `MTLBuffer` per call and releases it immediately, with its own TODO saying so |
| M-M9 | Uploads | Texture CREATION issues no submit on the incumbent, so V-M10 has no occupant. Device-level `UpdateTexture` on a non-staging texture DOES create a command list and submit one, and that moves onto a device-owned setup command buffer flushed lazily at the next submit or at any device-level read |
| M-F1 | Timeline | ONE device-wide `MTLSharedEvent` as a monotonic timeline. Every submit encodes `encodeSignalEvent:value:` with `++value`, `IGpuFence` holds a target, and `Signaled` is a non-blocking `signaledValue >= target` read. This DELETES the incumbent's completion-block machinery entirely: no hand-rolled block literal, no `Marshal.GetFunctionPointerForDelegate`, no AOT static-callback special case, no process-global dictionary, and no lock taken inside a driver callback |
| M-F2 | Timeline | The timeline's monotonicity makes the seam's documented fence ordering a THEOREM rather than a convention, which is V-F2's argument reaching the same conclusion because it is the same primitive under a different name |
| M-F3 | Timeline | `SupportsCompletionFences = true`, which is what `VeldridMap` already reports for Metal, so this is PARITY and not the upgrade it was on D3D11. Nobody should look for phase 2's C5 win here |
| M-F4 | Timeline | `WaitForIdle` is `waitUntilSignaledValue:timeoutMS:` on the device timeline, counted into the existing `DrainCount` and `DrainMs`. Not `waitUntilCompleted` on a retained last command buffer, which is what the incumbent does and which needs the buffer kept alive under a lock to be read |
| M-F5 | Hazards | **NO barrier tracker, NO layout tracker, NO resting layouts, NO transition table.** Metal tracks hazards automatically for device-allocated resources, so V-F6 and V-F7 do not port, which #531 predicted by name. Stated as a decision so a reader does not go looking for the tracker and conclude it was forgotten |
| M-F6 | Hazards | Seam rule 1 (compute writes a storage texture, a later graphics pass in the same list samples it) is satisfied by the API: the compute encoder ends when the render encoder begins, and the dependency between them is the driver's. No code |
| M-F7 | Hazards | Seam rule 2 (a dispatch reading an earlier dispatch's writes) is honoured AS WRITTEN and no seam member is added, but this backend satisfies it natively, because consecutive dispatches in one serial-dispatch compute encoder are ordered and tracked. **That makes three of three engine-owned backends able to answer yes, which is the quorum that makes #461 specifiable.** It is evidence, not a contract change |
| M-F8 | Lifetimes | NO deferred-disposal retire list. An `MTLCommandBuffer` retains every resource it references until completion, so releasing a resource mid-flight is already safe. V-F9 has no occupant and its absence is a decision |
| M-F9 | Liveness | The `DeviceLiveness` latch reproduced exactly (X3 and V-F10 precedent), with a full timeline drain BEFORE teardown, which the incumbent already does |
| M-W1 | Swapchain | `CAMetalLayer` configuration reproduced from the incumbent exactly where it is visible only to a human: pixel format and sRGB pair, `framebufferOnly = true`, `drawableSize`, and the layer attach-or-adopt dance on the host view. W1's lesson applied where it actually binds |
| M-W2 | Swapchain | `displaySyncEnabled` is set UNCONDITIONALLY. The incumbent applies it only when `MaxFeatureSet` equals one of three values of a deprecated enum, so on any machine outside that set a vsync toggle silently does nothing. Reproducing an equality test on a deprecated enum is reproducing a fragility, and the failure mode is silent |
| M-W3 | Swapchain | `nextDrawable` is taken AT THE PRESENT BOUNDARY for the next frame, keeping the incumbent's timing, which is the good half of it: the drawable is known before recording starts, so nothing about record-time framebuffer resolution changes. `maximumDrawableCount` is set to `FramesInFlight` |
| M-W4 | Swapchain | **`nextDrawable` BLOCKS, and Metal offers no semaphore alternative, so the stall is not removable and is instead MEASURED.** It is taken on the submit thread at the boundary and counted into `AcquireWaitCount` and `AcquireWaitMs`, the pair the Vulkan phase already added to `GpuDeviceCounters`. No seam addition is needed at all |
| M-W5 | Swapchain | A nil drawable binds a device-owned ORPHAN TARGET, records and submits normally, and skips only its present, counting into `FramesBegun`. The incumbent instead reports the framebuffer unrenderable and every draw in that frame returns silently without encoding, which discards a frame's recording with nothing said. V-W4's shape applied to a gap that is in shipped code here rather than in a draft |
| M-W6 | Swapchain | `presentDrawable:` stays on its OWN command buffer, exactly as the incumbent does it, rather than being routed onto the frame's own buffer. **This is a place where the reuse prior wins**, and 2.6 argues why |
| M-W7 | Swapchain | `IGpuFramebuffer` wrapper identity is STABLE across resize (W2 and V-W5). Resize queues and applies at the present boundary on the submit thread (W3 and V-W6). Metal needs no swapchain recreation, only a `drawableSize` write and a depth-texture rebuild, which is why the seam's existing "no recreate" wording describes Metal and needs no change |
| M-W8 | Threading | No frame-long lock. Recording is lock-free and per list. One `_submitLock` covers commit, present and the resize apply. Two short locks sit beside it: the setup command buffer's and the ring's. Creation is otherwise free-threaded |
| M-S1 | Shaders | GLSL 450 stays the single source. `SpirvCrossCompile`'s BACK end grows an MSL target, which is exactly the split V-S3 paid for, and the front end is untouched |
| M-S2 | Shaders | **#462 IS TAKEN IN THIS PHASE, and scoped to the MSL target only.** An engine-owned P/Invoke shim over `libveldrid-spirv` seats the MSL emission, because M-B1's index pin is not expressible through the managed `Veldrid.SPIRV` surface. The HLSL emission KEEPS the managed call until the program's closing act, so D3D11's 36 committed goldens are not in play in this phase. Section 12.2 |
| M-S3 | Shaders | There is NO byte-equality parity measurement against the incumbent available here, and saying so plainly is the point. The engine has never emitted MSL, so V-S2's licence for "no rebake" has no analogue. What licenses no rebake here is the goldens themselves plus M-B1's device-free index-table test taken BEFORE the first golden run |
| M-S4 | Shaders | Row 1 carries a BINDING-SUFFICIENCY SPIKE that proves the shim can pin every index the design needs before anything depends on it. If it cannot, the fallback is the managed MSL emission with the incumbent's numbering reproduced, decided AT THE SPIKE and not by a shipped switch |
| M-S5 | Shaders | `MTLCompileOptions` are PINNED in a constant with their measured values (`MslCompilePin`), including `fastMathEnabled` and `languageVersion`. The incumbent passes a default-constructed options object, so the committed metal goldens were baked under whatever the OS default was on the runner. Fast math moves floating-point results and the language version drifts with the OS image, and the workflow already pins `macos-26` for exactly that class of reason |
| M-S6 | Shaders | A `.metallib` disk cache keyed on the pinned MSL options, the SPIR-V hash, the device's registry identity and the engine version, header-validated and best-effort. `MTLBinaryArchive` is declined for v1 as a second, newer mechanism for the same win |
| M-S7 | Shaders | S5's holed-signature sinks STAY. They are FXC-and-WARP specific, the D3D11 leg ships alongside, and a Metal seat is exactly where somebody proposes removing them. Written down because it will be proposed for the third time |
| M-C1 | Compute | Compute and graphics bindings tracked separately with separate dirty arrays and separate bound-pipeline slots, as the seam requires. `SetComputePipeline` and `Dispatch` end any pending render encoder first (M-A5) |
| M-C2 | Compute | Storage buffers are plain buffers at a `[[buffer(n)]]` index. C2's RAW byte-address forcing is an HLSL artefact and has no Metal analogue, and neither does D3D11's SRV-versus-UAV auto-unbind |
| M-C3 | MSAA | `MaxMsaaSampleCount` is READ OFF the incumbent's own `GetSampleCountLimit` and reproduced, INCLUDING the fact that it ignores both of its arguments. **Here the incumbent's shape is correct-by-API rather than a bug**, because `supportsTextureSampleCount:` is the only sample-count query Metal offers and it takes no format. C4 and V-C5's lesson lands a third time and this time exonerates the incumbent |
| M-C4 | MSAA | `ResolveTexture` is an empty render encoder with `loadAction = Load` and `storeAction = MultisampleResolve`, reproducing the incumbent INCLUDING its discard of the source. That diverges from what D3D11 and Vulkan do, the divergence is the bandwidth-correct answer on this architecture, and it is documented in the package README rather than silently inherited |
| M-C5 | Staging | Staging textures are `MTLBuffer`-backed with the incumbent's SOFTWARE subresource layout reproduced byte for byte, plus a device-free table test against a checked-in table taken from the incumbent's own arithmetic. Every golden reads back through `Map` and `MappedData.RowPitch`, so a different arithmetic garbles all 36 at once (V-C7's ruling, same evidence) |
| M-C6 | Staging | `Map(staging, Read)` WAITS on the timeline before returning the pointer, counted as a drain. The incumbent returns `contents()` with no wait, which is covered today by the engine's own readback helper draining first, so the seam's guarantee currently rests on a convention rather than on the backend |
| M-G1 | Capabilities | Field-by-field parity with ZERO permitted differences, plus the reflection-completeness check that the comparison covers every member of `GpuCapabilities`. Section 14 carries the table, and two members are worth reading before anyone "improves" them: `SamplerLodBias` is FALSE on Metal (the sampler descriptor has no LOD bias) and `MaxMsaaSampleCount` is format-independent |
| M-G2 | Diagnostics | `KE_METAL_DEVICE` selection per M-N1. `softwareAdapter` is ALWAYS false and CI pins nothing, because Metal has no software rasterizer. Said out loud because a reader will look for the `KE_D3D11_ADAPTER` and `KE_VULKAN_DEVICE` analogue and find a decision rather than a gap |
| M-G3 | Diagnostics | `KE_METAL_VALIDATION=0\|1\|strict` sets the framework's validation environment BEFORE the first device is created and LOGS whether it took. **Whether in-process environment mutation actually reaches the framework is a ROW-1 SPIKE and not an assertion**, because Metal API validation is a process-launch mechanism and no phase-3-style "install a layer" answer exists |
| M-G4 | Diagnostics | Every `MTLCommandBuffer`'s `status` and `error` are checked at completion, and an error is LATCHED at the fault site with its `MTLCommandBufferError` code and its localized description, surfacing through the existing `deviceLossReason` header field. That closes #427 for the Metal leg on the day the backend lands |
| M-G5 | Diagnostics | `MetalFrameCapture` stops reaching into Veldrid's private `_commandQueue` by REFLECTION and takes the native backend's queue pointer directly. A small concrete dividend with a named owner |
| M-G6 | Counters | `GpuDeviceCounters` is populated in full with NO seam addition. The acquire-wait pair the Vulkan phase added is exactly what `nextDrawable`'s block needs, and `BackpressureStallCount` counts the ring acquire alone here because there is no command-buffer slot wait to fold in |
| M-T1 | Tests | The 36 committed `metal` goldens run unmodified against the native backend on the same hosted `macos-26` device at the existing 0.06 absolute per-channel tolerance. No rebake |
| M-T2 | Tests | A device-free native-call budget test through a narrow `IMtlEncoderSink`, generic-constrained to a struct, covering the call classes that scale with draw count on THIS API: argument-table writes, draws and dispatches, and ENCODER BOUNDARIES. The third is the Metal-specific class and neither predecessor has it |
| M-T3 | Tests | The MSL index-table test (M-B1) is device-free, runs on every `dotnet test`, and is taken BEFORE the first golden run. It parses `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]` out of the emitted MSL for every shipped program and asserts them against what the binder computes from the seam's layouts |
| M-T4 | Tests | `NativeVsVeldridMetalCapabilityParityTests`, both structs in one process on the Metal leg, zero permitted differences |
| M-T5 | Tests | The ring's shared semantic tests gain the Metal adapter (M-P5), so section 9.4's inventory is asserted across three implementations instead of two |
| M-T6 | Tests | `SlathRepro` is a ROLLOUT GATE, not a footnote. It is the engine's committed windowed Metal regression check and the only instrument that has ever caught the class of defect the one-UBO constraint belongs to. Gate 5 runs it |
| M-T7 | Tests | A `metal-native` matrix leg on hosted `macos-26`, running the FULL suite on every trigger, matching the incumbent Metal leg's tier exactly and sitting bake dispatches out as a guest |
| M-RO1 | Rollout | Five gates, all green before any default flip (section 17) |
| M-RO2 | Rollout | `Metal` through Veldrid stays selectable by token until the program's closing act. It is the kill switch for every structural decision here, which is why most bets carry no switch of their own |
| M-RO3 | Rollout | **The three incumbent legs retire TOGETHER**, in a closing act after all three native backends have passed their own gate 4, and this design says so because "indefinitely" in RO2 and V-RO2 otherwise means Veldrid never leaves and #420's endpoint is unreachable by construction. Section 19 |
| M-RO4 | Rollout | Every kill switch carries a decision deadline and V-RO4's sort decides what the deadline means. This design ships exactly TWO switches, both branches inside one implementation, and no switch anywhere keeps a second implementation alive |

---

## 2. The contested adjudications

Nine things are genuinely contested between the two priors. One correction to the evidence base comes first,
because it moves three of them.

### 2.1 The incumbent, established from its own code

Every claim below was read in `src/Veldrid/MTL/` and `src/Veldrid.MetalBindings/` at the fork's `v4.9.103`
tag. That tree differs from the fork's `master` in three files, none of them backend code (two enum files and
the bindings csproj), so the shipped Metal backend and upstream master's are the same code and the phase-3
citation hazard does not arise here.

**The incumbent is field-proven good, and that is the honest headline.** Metal is the backend the fleet's
reference goldens are baked on, the one a developer looks at when another backend disagrees, and the one with
no filed performance defect. This phase exists for #420's endpoint. It does not exist to fix a defect, and a
draft that argues otherwise is arguing dishonestly. What follows is nonetheless a list of real defects, and
they matter for a different reason: they are what a REUSE-first ruling would inherit by construction.

**The MRT clear loop writes attachment 0 for every attachment, and a shipped renderer works around it.**
`MTLCommandList.BeginCurrentRenderPass` iterates the pending clear array and, inside the loop, takes
`rpDesc.colorAttachments[0]` rather than `colorAttachments[i]`. The engine's `RenderResources` creates
`ModelFB` with THREE colour targets (colour, normal, linear depth), and
`ModelRenderer.BeginModelPass` clears all three. So on Metal today attachment 0 receives the same clear value
three times and attachments 1 and 2 receive NO clear at all, keeping the `loadAction = Load` that
`MTLFramebuffer.CreateRenderPassDescriptor` set. `BeginModelPass` carries the comment "Metal MRT clear
collapses to one value across attachments" and clears all three to the same colour, which is a consumer-side
workaround for the defect that does not actually address it: making the values equal does nothing about two
attachments that are never cleared. This is a verified defect, reachable by shipped code, with a shipped
comment describing it incompletely, and it is not recorded in any issue or in the engine's memory.

**A record-time `UpdateBuffer` allocates a buffer, splits the encoder, and wipes every piece of encoder
state.** `MTLCommandList.UpdateBufferCore` creates a whole new `Staging` `DeviceBuffer` per call (its own TODO
says "Cache these"), memcpys into it, calls `EnsureBlitEncoder`, which calls `EnsureNoRenderPass`, which ends
the render encoder, then blits, then disposes the staging buffer. Ending the render encoder runs
`EndCurrentRenderPass`, which sets `_graphicsPipelineChanged`, clears the entire
`_graphicsResourceSetsActive` array, and marks the viewport and the scissor dirty. So the next draw re-emits
the pipeline state (five calls), re-activates every resource set element by element, and re-emits the viewport
and the scissor. **The cost of one record-time uniform write on Metal is therefore an allocation, an encoder
split, a blit, a release, and a full graphics state re-activation, and none of it is a barrier.** That is a
different mechanism from D3D11's blocking staging map and from Vulkan's render-pass-split-plus-global-barrier,
and it is the strongest single argument for the ring on this platform. The per-frame COUNT of those writes on
the #410 scene is unmeasured and MM1 is what measures it.

**Resource activation is one native call per element per stage.** `ActivateGraphicsResourceSet` walks the
set's resources and calls `setVertexBuffer` / `setFragmentBuffer` / `setVertexTexture` / `setFragmentTexture`
/ `setVertexSamplerState` / `setFragmentSamplerState` one at a time, and `GetBufferBase`, `GetTextureBase` and
`GetSamplerBase` each re-walk the preceding layouts on every single bind. That is the #418 fan-out defect on a
second API. Metal has array setters for all six of those (`setVertexBuffers:offsets:withRange:` and siblings)
and the fork's `MTLRenderCommandEncoder` binding does not declare a single one of them.

**A redundant pipeline bind costs a full re-activation.** `SetPipelineCore` has no identity guard: it clears
the active-set array and sets the changed flag every time, even for the pipeline already bound. Compare the
D3D11 backend in the same fork, which does carry a pipeline-identity guard.

**Fences are real, and their machinery is not.** `MTLFence` is a `ManualResetEvent` set from
`OnCommandBufferCompleted`, which the device registers on every submitted command buffer through a hand-rolled
global block literal built with `Marshal.AllocHGlobal` and
`Marshal.GetFunctionPointerForDelegate`. The callback takes a lock and does a dictionary lookup keyed on the
command buffer, inside a driver-owned completion thread, and there is a second static AOT path keyed on a
process-global dictionary for non-macOS targets. `VeldridMap.SupportsCompletionFences` correctly reports true
for Metal, so this is a real completion fence with a large amount of machinery behind it. `MTLSharedEvent`,
which is the primitive that replaces all of it, appears nowhere in the bindings.

**`GetSampleCountLimit` ignores both of its arguments** and returns the highest sample count the device
reports through `supportsTextureSampleCount:`. That looks like a bug and is not one: `supportsTextureSampleCount:`
is Metal's only sample-count query and it takes no format. So `MaxMsaaSampleCount` on Metal is
format-independent by construction, and a native backend that "improved" it by asking per format would be
inventing a question the API cannot answer, which is precisely the C4 and V-C5 failure in a new costume.

**Vsync depends on an equality test against a deprecated enum.** `MTLSwapchain.SetSyncToVerticalBlank` writes
`displaySyncEnabled` only when `MaxFeatureSet` is exactly `macOS_GPUFamily1_v3`, `macOS_GPUFamily1_v4` or
`macOS_GPUFamily2_v1`. `MaxFeatureSet` is computed by enumerating `MTLFeatureSet` and calling
`supportsFeatureSet:`, deprecated since macOS 10.15, keeping the last value that answered true in numeric
enum order. On any machine whose answer falls outside those three values, a vsync toggle does nothing and says
nothing.

**Every buffer is `StorageModeShared` and every non-staging texture is `StorageModePrivate`.** `MTLBuffer`'s
constructor passes options `0`, which is Shared plus the default CPU cache mode, and rounds the size up to a
multiple of 4. Device-level `UpdateBuffer` is therefore a raw `memcpy` into `contents()` with no gate of any
kind. Staging textures are a Shared `MTLBuffer` whose subresource layout is computed in SOFTWARE by
`GetSubresourceLayout` and `Util.ComputeSubresourceOffset`, which is the phase-3 parity surface arriving
again.

**Present costs a command buffer.** `SwapBuffersCore` creates a fresh `MTLCommandBuffer` purely to call
`presentDrawable:` and commit it, then calls `GetNextDrawable()`, which blocks when no drawable is free.
`MTLSwapchainFramebuffer.IsRenderable` is false when the drawable is nil, and `PreDrawCommand` then returns
false, so **every draw in that frame is silently dropped and nothing reports it.**

**Device-level `UpdateTexture` on a non-staging texture creates a staging texture, a command list and a whole
queue submit**, then disposes both.

**The Objective-C layer is hand-rolled `DllImport` on `objc_msgSend`** with about fifty typed overloads, a
cached `Selector` type, an `ObjCClass` helper, and `objc_msgSend_stret` declared behind a `UseStret<T>()` that
always returns false. It works, and it is the code the fork wrote because nothing else existed.

**The engine already has its own Objective-C interop, and it is worse than it needs to be.**
`KhaozEngine.Gpu/Internal/MetalFrameCapture.cs` P/Invokes `objc_msgSend` directly and reaches Veldrid's
PRIVATE `_commandQueue` field by reflection to hand a queue to `MTLCaptureManager`. So the "we would be taking
on Objective-C interop" objection to a native Metal backend is answered by code that already shipped.

### 2.2 The shared home: #531's trigger has fired, and the answer is four things

**#531 is explicit that this phase decides it** and equally explicit about how: "Re-assess each against three
implementations rather than assuming the list carries." Its candidate list, from the phase-3 continuity
draft, was the emitter interface, the recorder, the dirty model, the flush schedule, the counting emitter, the
ring's segment policy, the soak counters, the liveness latch, the shader blob cache and the rate limiter,
roughly 1500 to 2000 lines. Two were already excluded by name (a generic emitter interface, and the barrier
tracker).

Walking the rest against what this design actually specifies:

**The recorder and the flush schedule do not survive.** The three flushes are a `*SetConstantBuffers1` plus
per-kind array calls, one `vkCmdBindDescriptorSets` per contiguous run of dirty slots, and per-(kind, stage)
Metal array setters into absolutely-indexed argument tables. Three mechanisms, three arities, three
invalidation rules that exist for three different reasons (register numbering, binding validity, absolute
index arithmetic). What is common is a per-slot record array and the rule that repeated dirty marks between
two draws collapse to one flush. That is sixty lines and a comment, and it is not worth a shared type.

**The dirty model does not survive either, and the reason is instructive.** D3D11 has three states, Vulkan
two, and Metal two but for a DIFFERENT reason: on Vulkan the third state collapses because a descriptor bind
is one call whichever way, and on Metal it collapses because the offsets-only path is a different CALL
(`setVertexBufferOffset:`) rather than a cheaper variant of the same one. Two backends agreeing on a number
while disagreeing on why is exactly the shape that produces a shared abstraction nobody can change.

**The ring's CODE does not survive and its POLICY has already been extracted, into tests.** Three
implementations, one policy, three mechanisms: a mapped `DYNAMIC` buffer with a 256-byte constant-count
round-up, a persistently mapped host-coherent allocation with a descriptor range answering to a VUID, and a
Shared buffer with an integer offset write and no range at all. Section 9.4 of the Vulkan design already made
the policy a ten-row inventory with an Owner column, and V-P5 already made seven of those rows shared
executable tests through one test-only interface. **The right move at three implementations is to add the
third adapter, not to unify the mechanism**, and that is M-P5.

**Four things DO survive, and they survive because three implementations wrote them identically.**

- The `DeviceLiveness` latch. A volatile token flipped inside the lifecycle lock before the real device dies,
  every wrapper's `Dispose` gated on it, `IGpuFence.Signaled` reading true after death, `WaitForIdle` a no-op
  after death. Three copies, no mechanism difference at all.
- The counter accumulators. `FramesBegun`, the drain pair, the backpressure pair, the off-timeline pair and
  the acquire pair are the same arithmetic behind the same struct in three places.
- The diagnostic rate limiter. D3D11 rate-limits `ID3D11InfoQueue`, Vulkan rate-limits the debug messenger,
  Metal rate-limits command-buffer error logging. Same shape, same reason.
- The shader-cache KEY and file discipline: pin plus engine version plus device identity, header-validate
  before trusting, discard silently on mismatch, best-effort so a read or write failure is never fatal. The
  PAYLOAD differs (DXBC, `VkPipelineCache`, `.metallib`) and stays per backend.

**Decision.** M-P4 through M-P6. Extract those four into `KhaozEngine.Gpu/Internal/`, which every backend
already references and which already grants `InternalsVisibleTo`. Not a new `KhaozEngine.Gpu.Backend` package:
the phase-3 rejection table already argued that it needs public API to serve two consumers we both own, and
that argument is unchanged at three. Add the Metal ring adapter to the shared semantic tests. Refuse the rest,
in writing, per candidate, so #531 closes with a reasoned answer rather than being deferred a second time.

**And the sequencing matters more than the content.** M-P6 puts the extraction AFTER gate 3. Extracting while
the backend is being written means a golden failure has two candidate causes, and the whole value of a guest
golden family is that it has one.

### 2.3 The recording model, and why there is nothing to argue

Phase 2's largest bet was whether a managed op stream in front of the immediate context was slower than
immediate emit. It cost two complete drivers, a kill switch, an end-to-end A/B and a milestone that still gates
phase 2's rollout gate 3. Phase 3 had no analogue because a `VkCommandBuffer` is a real deferred command
buffer.

Metal is the same case and slightly stronger. An `MTLCommandBuffer` between `commandBuffer()` and `commit()`
is a driver-encoded command stream, and the encoders write into it directly. A managed op stream in front of
that would encode twice, allocate once more, and move the driver-side encode inside the submit lock, which is
the one serialised point in the frame. Phase 2's own section 16 predicted this before either phase-3 draft
existed and the prediction held there.

So M-R1, with no switch and no A/B, and the important consequence stated: **this design's largest risk is not
performance.** It is the MSL numbering (2.7), and that is a correctness question with a device-free test in
front of it.

**One Metal-specific thing does need deciding and neither prior settles it.** Command buffers execute in
ENQUEUE order on a queue, and `commit` enqueues if the buffer has not already been enqueued. With N lists
recording concurrently, the observable order is therefore whichever commits first unless something orders it.
Committing under `_submitLock` makes submit order the enqueue order by construction, which is the seam's
contract. `enqueue` at `Begin` is the alternative and it would let submits proceed without the lock, and it is
declined for v1 because nothing asks for it and it makes the order depend on `Begin` rather than on `Submit`,
which is not what the seam documents.

### 2.4 Hazards: the whole subsystem the platform deletes

The Vulkan phase spent one work-breakdown row, three decisions (V-F6, V-F7, V-F8), a canonical resting-layout
model, a per-subresource list-local tracker, an `UNDEFINED`-discard determinism rule and a device-free
barrier-shape test on synchronisation. The D3D11 phase spent an SRV-versus-UAV auto-unbind in both directions.

Metal tracks hazards automatically for resources allocated from the device. Two encoders on one command buffer
are ordered, and the driver inserts the dependency. Consecutive dispatches in one compute encoder created with
the default serial dispatch type are ordered and tracked. So:

- Seam rule 1 needs no code. The seam's own comment already says so ("Metal ends the compute encoder when the
  render encoder begins").
- Seam rule 2 needs no code either, and this backend satisfies it natively rather than by a drain.
- There is no layout tracker, no resting layout, no barrier batch, no transition table and no
  `UNDEFINED`-discard rule to get right.

**This is the reason M-M1 refuses `MTLHeap`, and it is worth stating as a linkage rather than as two separate
decisions.** A heap is Metal's suballocator, and resources allocated from a heap are `MTLHazardTrackingMode`
UNTRACKED by default, which means the application inserts `MTLFence` and `memoryBarrier` calls itself. Taking
the heap means taking back the entire subsystem this design just deleted, in exchange for a suballocator whose
workload allocates at load time and nothing in the steady-state frame. That trade is bad enough that it should
be recorded as a linkage: **if a future consumer ever needs heaps, it needs a hazard tracker in the same
change.**

**What this costs, said plainly.** Automatic tracking is conservative. The driver may serialise two encoders
that could have overlapped, and there is no instrument in this design that measures the lost overlap. That is
MM5's honest form: not a bet with an exit criterion, but a recorded observation that the design chose the
simpler and less controllable model, with `MTLResourceHazardTrackingModeUntracked` on individual resources
named as the escape hatch if a measurement ever shows the cost.

### 2.5 The uniform path, and whether `setBytes` replaces the ring

This is the question the platform prior most obviously wants to answer "yes" to, and the answer is no. Working
through it honestly matters, because "Metal has `setVertexBytes`" is exactly the kind of true statement that
produces a wrong design.

**What `setVertexBytes:length:atIndex:` would buy.** It copies caller bytes straight into the encoder's own
command stream, so there is no `MTLBuffer`, no per-frame segment, no completion gate, no backpressure counter,
no #484 every-segment rule, no stride invariant and no pending-patch queue. It would delete the entire ring
subsystem, which is the biggest single line item in both prior phases.

**Why it does not fit this seam.** The seam hands the backend a BUFFER, not bytes.
`CreateResourceSet` pins a `GpuBufferRange(buffer, offset, size)` at load time across 68 call sites, renderers
call `UpdateBuffer(buffer, offset, data)` at record time and off timeline, and then bind the set with a
dynamic offset. To reach `setBytes` the backend would have to keep a CPU shadow of every ring-backed buffer,
have `UpdateBuffer` write into the shadow, and copy `size` bytes out of the shadow at every bind, per stage.
That is a memcpy per bind per stage where the ring writes an integer, and the hot path is the shadow pass's
thousands of offsets-only rebinds of one slot per frame.

**And the 4 KB cap is a content cliff, not a design constraint.** `setBytes` is limited to 4096 bytes per
binding. The engine's shipped uniform windows are already in the four-figure range (phase 2 records 1008,
1040 and 1120 bytes at three sites), and a combined per-draw UBO carrying a bone array is exactly the shape
that crosses it. A limit whose breach is a runtime throw triggered by content is worse than a limit that does
not exist.

**Decision.** M-M3 and M-B5. Keep the ring, take `setVertexBufferOffset:atIndex:` for the offsets-only path
(M-R7), and record `setBytes` as considered and declined with a named trigger: a seam member that hands the
backend BYTES rather than a buffer range, which is the push-constant concept the seam does not have and which
V-D8 declined on Vulkan for its own reasons.

**What the ring looks like once that is settled is the best version of it in the program.** No map and unmap
dance, because Shared memory is permanently CPU-visible. No memory-type selection, no coherence question, no
flush and invalidate, because Shared is coherent on unified memory. No descriptor range and no VUID, because
`setBufferOffset:` carries the offset and nothing carries a length. No 16-constant round-up. The whole thing
is a buffer, a stride, a frame index, a completion read and an integer written at bind. **That simplicity is
the answer to anyone who reads the two prior rings and concludes the ring is inherently heavy.**

### 2.6 The present path, where the reuse prior wins

The Metal-idiomatic move is to encode `presentDrawable:` on the FRAME'S OWN command buffer rather than on a
separate one, which is the documented shape and removes a command buffer per frame. The Vulkan phase's
corrected-in-flight semaphore routing (#563) gives the mechanism for free: the present boundary asks each
submit whether its list bound the swapchain framebuffer and hands the present to the first that did.

**Take the incumbent's shape anyway (M-W6), for three reasons.**

First, W1's lesson binds here harder than anywhere. The swapchain is the one area with no automated coverage
in the whole net, and the Metal golden suite is headless. Phase 2 took the flip-model swapchain off the table
for exactly this and both of its authors said a judge wanting one risk-reducing edit should take that one.

Second, the win is negligible and the risk is not. An `MTLCommandBuffer` is a cheap object taken from a queue
pool, and one extra per frame at 144 fps is not a number anyone will find. Against that, routing the present
onto a frame's own buffer inherits the Vulkan design's own NAMED limitation (a second list binding the
swapchain framebuffer after the first has ended discards what the first drew) into a backend where nothing
measures it.

Third, the ordering is already correct. `presentDrawable:` on a later-committed command buffer runs after the
frame's, by queue order, which is the same guarantee the routed form gives.

**What is NOT reused from the present path is the silent frame drop (M-W5).** When `nextDrawable` returns nil,
`IsRenderable` goes false and `PreDrawCommand` returns false for every draw in the frame, so the recording is
built and thrown away with nothing logged and nothing counted. That is not a present-path aesthetic, it is a
frame that lies about having rendered, and V-W4's orphan-target rule fixes it for one image at one extent.

**And the blocking acquire is kept because it cannot be removed.** Phase 3 replaced the incumbent's blocking
`vkWaitForFences` acquire with a semaphore, and that option does not exist here: `nextDrawable` returns the
drawable and there is no signal-a-semaphore variant. So M-W4's ruling is to keep it, move it to the submit
thread at the boundary, and MEASURE it into the acquire-wait pair. That pair exists on the seam because the
Vulkan phase added it, which is a template paying out one phase later.

### 2.7 The MSL numbering, and the largest decision in this design

**This is where the two priors are furthest apart and where this draft takes its biggest risk.**

The reuse-first position is: emit MSL through the managed `Veldrid.SPIRV` with `CrossCompileTarget.MSL`, which
is the same call `CreateFromSpirv` makes on a Metal device, so the emitted MSL is byte-identical to the
incumbent's, the committed metal goldens carry over on V-S2's own argument, and the binder reproduces the
incumbent's index arithmetic including `ResourceBindingModel`. Minimum risk, maximum parity.

**Three things decide against it.**

**First, byte equality is not free here the way it was on Vulkan, and the reuse position understates what it
costs.** On Vulkan the incumbent hands `vkCreateShaderModule` bytes the front end produced, so byte equality
was a property of one call with one option set. Here the numbering the emitted MSL uses is SPIRV-Cross's
default MSL numbering, and the incumbent then binds against it with `GetBufferBase`, `GetTextureBase`,
`GetSamplerBase` and a `ResourceBindingModel` shift applied in two places. Reproducing the emission means
reproducing all of that arithmetic exactly, including the option whose only purpose is to manage a collision
between two index spaces. Byte-identical MSL buys parity and costs the whole shape.

**Second, that shape is where the one-uniform-buffer-per-pipeline constraint lives, and this is the last phase
that can touch it.** The engine's memory records the constraint at length: a second uniform buffer anywhere in
a pipeline reads the first buffer's bytes or reads all zero, invariant to set arrangement, reproduced from
7.64.0 through the 10.104.x GPU-skinning spike. The consequences are shipped and expensive. Skinned meshes are
CPU-skinned on EVERY backend because of a Metal-only defect. The splat terrain carries a bespoke combined UBO
per material with the frame block re-synced into it each frame. And V-S6 exists as a rule the Vulkan phase had
to defend on behalf of a backend that was not there to defend itself. **If the native Metal backend reproduces
the incumbent's numbering byte for byte, it reproduces the constraint by construction, and #420 ends with the
constraint permanent and no seat left from which to remove it.**

**Third, the numbering is precisely what a native backend exists to own, and the instrument to check it is
better here than it was on D3D11.** Phase 2 had to invent the D3D11 register scheme and prove the CPU side and
the emitted HLSL agreed, with a table test over every shipped layout. That test is the model, and it is
cheaper here: MSL carries `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]` as attributes in the emitted
text, so the assertion parses the artefact instead of inferring from it.

**The risk, stated at full strength because it is real.** The `metal` golden family is not just this leg's
family, it is the fleet's cross-backend reference. The engine's memory is explicit: when another backend's
golden regresses, check it against the Metal reference first. A metal golden that moves under a numbering
change is therefore a fleet event and not a leg event, and this draft is proposing to move the one artefact
every other backend is checked against.

**Four things bound it.**

1. The pin is asserted DEVICE-FREE and BEFORE the first golden run (M-T3). If the binder and the emission
   disagree, that is a red test on the Linux leg, not a mystery on a GPU leg.
2. The HLSL emission is UNTOUCHED (M-S2). The shim seats the MSL target only, so D3D11's 36 committed goldens
   and both WARP corruption incidents are not in play. Phase 3's own reason for declining #462 was that it
   would put them in play in a phase that could not see them, and scoping to MSL is what answers it.
3. The Metal leg is the BEST-INSTRUMENTED leg in the matrix: a real device, the full suite, on every trigger.
   That is the exact inverse of phase 3's situation and it is why this is the right phase for this change.
4. Row 1's spike (M-S4) decides feasibility before anything depends on it. If the shim cannot express the pin,
   the fallback is the managed emission with the incumbent's numbering, and the decision is taken AT THE SPIKE
   rather than carried by a shipped switch. **No second shader path ships**, which is V-RO4's rule and the
   thing phase 2's gate 3 is still paying for.

**Decision.** M-B1, M-B2, M-S2, M-S3, M-S4. Own the numbering. And M-B3 is the discipline that keeps this
honest: **this design does NOT promise the constraint is removed.** It promises to make it testable, with a
named measurement (MM6) and with V-S6 staying in force until that measurement passes. A draft that shipped a
shader change on the strength of an untested hypothesis about somebody else's cross-compiler would be doing
exactly what the memory says cost four separate diagnostic sessions.

### 2.8 The Objective-C interop layer

**Four routes.** Vendor the fork's `Veldrid.MetalBindings`. Take a third-party binding. Hand-roll fresh with
`DllImport`. Hand-roll fresh with `[LibraryImport]`.

**Vendoring is rejected on the same ground phase 3 rejected vendoring Veldrid's `Vulkan.*` namespace**, and
the ground is stronger here: Veldrid-derived code inside the backend built to remove Veldrid, invisible to
every guard that reads package ids, in a repo whose architecture tests assert exactly that no Veldrid edge
exists. It also brings shapes this design does not want: `objc_msgSend_stret` behind a `UseStret<T>()` that
always returns false, `Marshal.GetFunctionPointerForDelegate` block literals, and about fifty overloads
covering the calls the fork happened to need and not one of the array setters M-R6 requires.

**A third-party binding does not exist to take.** Silk.NET has no Metal binding of consequence, which is the
fact #566 banks and which this draft verified is still the case. That is the single largest difference from
phases 2 and 3, both of which took a maintained generated binding (Vortice, Silk.NET.Vulkan) and argued that
owning the BACKEND and owning the BINDING are different things. Here there is no binding to own separately.

**So it is hand-rolled, and the only question is the mechanism.** `[LibraryImport]` with blittable-only
signatures, source-generated, no marshalling stub, which is also what the SYSLIB1054 analyzer requires under
this repo's warnings-as-errors rule. Selectors cached in static readonly fields registered once through
`sel_registerName`. Class handles through `objc_getClass`. Completion notification through `MTLSharedEvent`
rather than through a block, which is what removes the single hardest piece of interop in the fork
(M-F1). Where a block genuinely is needed, `[UnmanagedCallersOnly]` gives a function pointer without a
delegate and without a GC handle.

**The arm64 caveats, stated because they are where hand-rolled interop dies.** `objc_msgSend` must be called
through a prototype matching the method's real signature on arm64, which is what the typed-overload approach
gives and what a single variadic declaration would break. `objc_msgSend_stret` does not exist on arm64 at all,
so no stret path is written rather than one being written and disabled. `BOOL` is one byte and `CGFloat` is a
double on 64-bit. Every one of these is a spike item in row 1 rather than an assertion in this document,
because a wrong ABI assumption in interop is a memory corruption and not a compile error.

**The scope, honestly.** This is the largest hand-written surface in the phase and the one place this design
is doing MORE work than its predecessors rather than less. Section 3.1 sizes it against what the fork needed
and what this design adds.

### 2.9 Four places the reuse prior wins outright

The rulings above go one way often enough that a reader could conclude the platform prior won everywhere. It
did not, and these four are worth pulling out of the table because three of them are parity surfaces that fail
on every golden at once.

**The MSAA limit (M-C3).** Both prior phases had drafts that invented a `MaxMsaaSampleCount` formula and then
asserted equality with the incumbent, which is the C4 failure phase 2 corrected in flight and the V-C5 ruling
phase 3 made against both of its drafts. Here the incumbent's shape looks like a bug (it ignores both of its
arguments) and is not one, because Metal's only sample-count query takes no format. Reproduce it, pin the
citation, and record that the argument-ignoring is correct so the next reader does not "fix" it.

**The staging subresource layout (M-C5).** The incumbent computes row pitch, depth pitch and subresource
offset in SOFTWARE, and every golden in the suite reads back through `Map` and `MappedData.RowPitch`. A
different arithmetic garbles all 36 at once. Reproduce it byte for byte and pin it in a device-free table test
taken from the incumbent's own computation, which is V-C7's ruling for V-C7's reason.

**The present path (M-W6, 2.6).** Take the incumbent's separate present command buffer.

**The clear-arrival semantics (M-A3).** The deferred begin, the pending-clear array, the forced begin-and-end
pair when a framebuffer is bound and cleared with no draw, and the switch to an immediate clear once encoding
has begun. All of that is subtle, a golden depends on the clear-only case, and it is reproduced rather than
re-derived. What is NOT reproduced is the attachment-0 collapse inside it (M-A2), and separating the two
halves is the whole content of this ruling.

---

## 3. Package, layering and the binding

`KhaozEngine.Gpu.Metal`, one assembly, referencing `KhaozEngine.Gpu` and nothing else. Target `net10.0`, NOT
`net10.0-macos`, so the assembly compiles and its device-free tests run on the Linux `ci.yml` leg and both
Windows legs, and so `KhaozEngine.Render.Tests` can reference it unconditionally. Every entry point carries
`[SupportedOSPlatformGuard("macos")]` and every Objective-C-touching body is
`[MethodImpl(MethodImplOptions.NoInlining)]` behind an `OperatingSystem.IsMacOS()` guard.

**That apparatus is P1's, not V-P1's, and the difference is worth one sentence.** Vulkan needed none of it
because Vulkan is not an OS-specific API. Metal is, so the D3D11 pattern applies verbatim and CA1416 makes the
compiler enforce the boundary under warnings-as-errors.

Guard work the package creates:

- `ArchitectureTests.OptInBackends` gains `Gpu.Metal`, which then enforces
  `OptInBackends_AreNotReachableFromAnyUmbrella`.
- `ArchitectureTests.ThirdPartyHomes` gains NOTHING, because there is no third-party package. That is a first
  in this program and it is worth a comment in the test so a future reader does not add a row for symmetry.
- `KhaozEngine.slnx` gains the project, which force-adds `KhaozEngine.Tests` to the selective-test set.
- `check-doc-versions.sh` requires a bolded `**KhaozEngine.Gpu.Metal**` catalog row in the root `README.md`
  and a `KhaozEngine.Gpu.Metal/README.md` shipped via `<PackageReadmeFile>`.
- `GpuPublicApiTests` extends its walk to the new assembly.
- The no-Veldrid pair (csproj read plus IL reference walk) extended in both forms. The IL walk is the
  load-bearing one, since Veldrid is in the transitive closure through `KhaozEngine.Gpu` whatever the csproj
  says.
- A new assertion: the backend names the cross-compile back end's MSL member and no other back-end member,
  which is V-S3's architecture test given a second arm.
- `docs/DEPENDENCY-SEAMS.md` gains the third instance of the out-of-package backend edge.

### 3.1 The interop layer, sized

The fork's `Veldrid.MetalBindings` is 79 files. That number is misleading in both directions: most of them are
one-enum or one-struct files that cost nothing, and the set is incomplete for this design.

What this design needs beyond what the fork declares:

- The six ARRAY setters on `MTLRenderCommandEncoder` and their compute siblings (M-R6).
- `setVertexBufferOffset:atIndex:` and `setFragmentBufferOffset:atIndex:` (M-R7).
- `MTLSharedEvent`, `newSharedEvent`, `signaledValue`, `encodeSignalEvent:value:`,
  `waitUntilSignaledValue:timeoutMS:` (M-F1).
- `supportsFamily:` and `MTLGPUFamily` (M-N3).
- `MTLCommandBuffer.error`, `MTLCommandBufferError` and `NSError.localizedDescription` (M-G4).
- `maximumDrawableCount` on `CAMetalLayer` (M-W3).
- `MTLStoreActionStoreAndMultisampleResolve` is NOT needed, since M-C4 reproduces `MultisampleResolve`.

What it does not need at all: the whole `MTLFeatureSet` enum and `supportsFeatureSet:` (replaced by
`supportsFamily:`), the block-literal machinery, `objc_msgSend_stret`, the indirect-draw encoder methods (the
seam has no indirect draw), and the specialization-constant path (the seam exposes none).

**The estimate this design is prepared to be held to** is that the interop layer is the largest single file
group in the package and is dominated by mechanical enum and struct declarations, with perhaps two hundred
lines of genuinely load-bearing marshalling. Row 1's spike is what turns that estimate into a fact, and the
spike compiles one file against every call this design names.

**The counterargument owed.** Hand-rolled interop is where memory corruption lives, and this design is
choosing it after two phases that both chose a maintained binding. The honest answer has three parts. There is
no maintained binding to choose. The engine already ships Objective-C interop in `MetalFrameCapture.cs`, so
the capability is not new. And the golden leg is a real device running the full suite on every trigger, which
is the strongest regression net any leg in this program has, so an interop defect surfaces on the next push
rather than in a field report.

---

## 4. Selection, identity and wiring

### 4.1 What the two prior phases already paid for

`GpuDeviceContext` is inverted onto `IGpuDevice`. `GpuBackendProviders` and `IGpuBackendProvider` exist.
`RequiresProvider` is stated negatively through `IsBuiltIn`, so an appended kind is provider-backed by
default and needs no edit there. `PreflightProvider` fixes the order so a missing registration throws before
the support probe can answer false. `GpuBackendProviderMissingException.BuildMessage` was corrected in phase 3
to state the naming convention rather than switch on the kind, so it degrades correctly for this backend
without an edit. The test-side seat is a static constructor on `GpuFactAttribute` in
`KhaozEngine.TestSupport.Gpu`, and a `MetalBackendRegistration` sibling goes in the same project.

So this phase adds a REGISTRATION, `KhaozEngineMetal.Register()`, and re-litigates none of the wiring. **Three
templates, now proven twice each, is the phase-3 dividend arriving.**

`IsSupported()` is a functional probe with real content: create a device through
`MTLCreateSystemDefaultDevice()`, check it is non-null, read the GPU family, and dispose. It must never throw.
A machine with no Metal device answers false and routes through `AfterFallback` as `FallbackAfterFailure`.
Phase 3's corrected-in-flight lesson applies: CREATION consults the probe BEFORE creating, so a machine-level
refusal is always a `NotSupportedException` naming what is missing.

### 4.2 The `GpuBackendKind` append audit, third time

The audit is `GpuBackendKindAppendAuditTests` plus its Vulkan sibling, so this is a diff. Fifteen sites, and
**this append is the first one whose FrameCap row is not correct by default**, which is the whole reason to
walk it again rather than assume the template carries.

| Site | `VulkanNative`'s answer | `MetalNative`'s answer |
|---|---|---|
| `GpuDeviceContext.LogThreadingCaps` | No change, gates on `IsDirect3D11()` | **No change.** No `D3D11_FEATURE_DATA_THREADING` analogue |
| `D3D11ThreadingProbe.IsApplicable` | No change | **No change**, same reason |
| `CreateWindowed` and `CreateHeadless` switch expressions | Rides the existing explicit throwing arm | Same. Verify the message names the provider registry generically |
| `GpuBackendSelector.ToVeldrid` | Explicit throwing arm | Same, one more arm |
| `GpuBackendSelector.TryParseBackend` | Two tokens added | Add `metal-native` and `mtl-native` |
| `GpuBackendSelector.RecognizedTokens` | Read by the unrecognized-override WARN, pinned by the audit test | Add both tokens. The audit test asserts every listed token parses and every kind is listed, so this cannot be missed |
| `GpuBackendSelector.IsBackendSupported` | Route to the provider's probe | Same. Veldrid cannot answer for it |
| `GpuBackendSelector.ProbeOS` | Unchanged until the flip, and the flip changes the LINUX default | Unchanged until the flip, and the flip changes the **macOS** default |
| `GpuBackendSelector._windowCandidates` | Unchanged until default-ready | Same. A player does not choose an implementation |
| **`Windowing/FrameCap.Resolve`** | Falls into the uncapped arm, correct by default | **MUST CHANGE.** It applies a real software frame cap only on Metal plus vsync. `MetalNative` falls into the uncapped arm, so the native leg silently loses the frame cap that the Metal leg has, and #380 is a pacing issue. Route through `IsMetal()` |
| **`Windowing/DisplaySettings`** | Same shape, correct by default | **MUST CHANGE**, same shape, same reason |
| `GoldenCompare`'s two filename sites | Both route through `GoldenBackendToken` | Both, mapping `MetalNative` to `metal`. The switch has no discard arm and throws, and the audit test makes a miss a device-free red |
| `VeldridMap.SupportsCompletionFences` | Not an append site, answers true for Vulkan | Not an append site, answers true for Metal, which is why M-F3 is parity |
| `VeldridGpuDevice` Metal frame capture | Unaffected | **Worth naming.** It gates on Veldrid's own `Backend == Metal`, so it is unaffected as a switch, and M-G5 replaces the reflection it uses |
| `GpuBackendProviderMissingException.BuildMessage` | Fixed generically in phase 3 | No change, and that is the fix paying out |
| `GpuDeviceContext.CreateOrFallBack` | Correct by default | Correct by default. On macOS `ProbeOS` returns `Metal` while the request is `MetalNative`, so they differ and the request routes through the functional probe |

`GpuBackendKinds.IsMetal()` is added beside its two siblings (M-I3), and the two FrameCap rows are why: a copy
of the question at each site drifts, and here there are two sites that must both change.

---

## 5. Device, queue and lifecycle

One `MTLDevice`, one `MTLCommandQueue`, both created under `GpuDeviceContext._lifecycleGate`, which stays.

**Device selection (M-N1).** `MTLCreateSystemDefaultDevice()` is the default, which is what the incumbent
does. `KE_METAL_DEVICE` accepts an index into `MTLCopyAllDevices()`, a name substring, or `lowpower` or
`removable`, with a named-but-absent device producing a WARN plus the default path. Any substitution is
LOGGED, so a soak session can tell a substitution from a selection. The phase-3 argument is unchanged:
changing which GPU the engine runs on is a user-visible change unrelated to swapping the backend, it breaks
`DeviceName` parity under a zero-difference bar, and it adds a second variable to the one gate that must
isolate the swap. Where it buys something is a Mac with both an integrated and a discrete GPU, and that is a
follow-up with its own change note.

**One queue (M-N2).** Command buffers execute in enqueue order on a queue. `commit` enqueues if the buffer was
not already enqueued. Committing under `_submitLock` therefore makes submit order the observable order, which
is the seam's contract, with no `enqueue` call and no second queue. A second queue would need
`MTLSharedEvent` cross-queue signalling to order anything against the first, which is machinery with no
consumer.

**Capability floor (M-N3).** New reads use `supportsFamily:`. The incumbent's `MTLFeatureSet` enumeration is
not reproduced, because `supportsFeatureSet:` has been deprecated since macOS 10.15 and because its result
feeds the vsync equality test M-W2 removes. **Parity surfaces are the exception**: `MaxMsaaSampleCount` and
`SupportsShadowMaps` reproduce the incumbent's own question, per M-C3 and section 14.

**Autorelease discipline (M-N4).** Metal's factory methods return autoreleased objects. The incumbent wraps
four sites in `NSAutoreleasePool` and does not wrap others, which is the shape that accumulates under a frame
loop. The rule here is that every public entry point that can create an autoreleased object wraps its body,
enforced by a device-free architecture test over the type graph rather than by review, in the shape V-D2 used
for descriptor-pool unreachability.

**Teardown (M-F9).** Drain the timeline first, then flip the liveness token inside the lifecycle lock, then
release the queue and the device. The incumbent already calls `WaitForIdle` first, which is the half phase 3
had to correct on Vulkan.

---

## 6. Command recording

### 6.1 The list, the buffer, the encoders (M-R1 to M-R4)

`MetalCommandList : IGpuCommandList`, encoding at record time.

- `Begin()` takes a command buffer from the queue, retains it, and resets the recorder's tracked state:
  framebuffer, both pipelines, both dirty arrays, the pending-clear array, the viewport and scissor marks.
- Encoders are opened lazily and exactly one is open at a time, which is the API's rule.
- `End()` closes any open encoder and seals.
- `Submit` commits under `_submitLock` and encodes the timeline signal (M-F1).

**There is no command-buffer pool to reset (M-R2).** Vulkan needed `FramesInFlight` `VkCommandPool`s per list
because a command buffer's memory is the pool's and a pool cannot be reset while its buffers are in flight.
Metal's queue owns that allocation and hands out a fresh buffer each time. So the `FramesInFlight` gate exists
here for exactly ONE reason, the uniform ring's segment recycling, and it lives on the ring's acquire alone.
`BackpressureStallCount` therefore means one thing on this backend where it means two on Vulkan, which is a
simplification worth stating because its doc comment now carries both meanings.

**Concurrent recording (M-R3).** N lists record concurrently and genuinely. Two `MTLCommandBuffer`s are
independent objects, each encoded on one thread at a time, and **this design has no shared record-time state
at all**: no layout tracker (M-F5), no device state cache, no barrier batch. The portable seam contract is
unchanged at one open recording per device, and `IGpuCommandList.Begin`'s XML doc gains a Metal sentence
saying which property makes it true here, in the shape the Vulkan sentence already has.

And the same decay warning applies for the third time: `OpenListTrackingGpuDevice` passes trivially on this
leg and is NOT evidence about this backend.

### 6.2 Encoder-scoped state, the fact everything else follows from (M-R4)

Metal's argument tables, bound pipeline state, viewport and scissor are properties of the ENCODER, not of the
command buffer. Ending a render encoder discards all of it. The incumbent already behaves this way
(`EndCurrentRenderPass` sets the pipeline-changed flag, clears the active-set array and re-marks the viewport
and scissor), and it is not a choice either implementation makes.

Three consequences the design is built around:

1. **The dirty model is encoder-scoped.** Re-activation at the first draw after any encoder boundary is
   mandatory, and a device-free test asserts it, because "we re-activated when we did not need to" and "we
   failed to re-activate when we did" are both invisible in a green suite otherwise.
2. **A record-time blit is expensive out of proportion to what it copies**, which is 2.1's finding and the
   ring's motivation (M-M4).
3. **Encoder boundaries are a first-class thing to count**, which is why M-T2's budget sink counts them
   alongside binds and draws. Neither prior phase has this call class because neither API has it.

### 6.3 The schedule (M-R5 to M-R9)

1. `SetGraphicsResourceSet(slot, set)` and its dynamic-offset overload RECORD ONLY into a per-slot array of
   `(set, engineDynamicOffset)`, marking the slot dirty when either differs from what is recorded. Two states.
2. `Draw`, `DrawIndexed` and `Dispatch` flush every dirty slot through the pre-command hook, then issue.
3. The flush assembles, per (kind, stage), a contiguous range of argument-table indices and emits ONE array
   call for it (M-R6). A full activation of the engine's model set is one buffer call, one texture call and
   one sampler call on the fragment stage plus one buffer call on the vertex stage.
4. A slot whose only change is its dynamic offset emits ONE `setVertexBufferOffset:` or
   `setFragmentBufferOffset:` per VISIBLE stage (M-R7), which is the shadow pass's shape thousands of times a
   frame.
5. `SetPipeline` on the pipeline already bound does nothing (M-R8). Otherwise it binds and then invalidates
   recorded slots only where the computed base-index vector differs (M-R9).
6. A slot whose recorded set has gone null is skipped.
7. Repeated dirty marks between two draws collapse to one flush, which falls out of an array of slots.
8. Any encoder boundary invalidates everything (M-R4).

**Clause 5 is the Metal occupant of a clause that exists in all three backends for three different reasons,
and that is worth pausing on, because it is the clearest single piece of evidence for 2.2's refusal to
extract the schedule.** On D3D11 a pipeline switch drains under the OUTGOING layout because the layout decides
register numbering. On Vulkan it invalidates from the first incompatible set because a pipeline-layout
mismatch invalidates bound descriptors. On Metal nothing is invalidated by the API at all, because argument
tables are absolute, and what changes is the BASE the backend computes for set N by summing the preceding
layouts' per-kind counts. Content-deduplicating the layout objects makes "the same set of layouts" a
handle-array compare, so the common case (two pipelines sharing the same layout array) invalidates nothing.

**And the incumbent does none of this**: `SetPipelineCore` clears the whole active-set array on every switch
and on every redundant re-bind.

### 6.4 The budget seam (M-T2)

A narrow `IMtlEncoderSink`, generic-constrained to a struct so the JIT monomorphizes it away, covering exactly
three call classes:

- **Argument-table writes**: the array setters and the offset setters.
- **Draws and dispatches.**
- **Encoder boundaries**: begin and end of each encoder kind.

Everything else (clears, which are descriptor fields rather than calls, copies, mip generation, resolves) goes
straight through with no indirection, because nothing about them scales per draw.

**Aiming this at the other backends' call classes would have been the mistake, twice over.** D3D11's fan-out
class is one call per resource per stage. Vulkan's is per-draw descriptor allocation and per-draw barriers.
Metal's is one call per resource per stage AND an encoder boundary per record-time upload, and the second one
has no analogue anywhere else in the program. A budget ported from either predecessor would pass green while
a record-time `UpdateBuffer` split the encoder a thousand times a frame.

---

## 7. Passes, clears and the viewport

### 7.1 The deferred begin (M-A1 to M-A5)

State per list: the bound framebuffer, a pending-clear value per attachment, a pending depth clear, and
whether a render encoder is open.

- `SetFramebuffer(fb)`. If an encoder is open, end it. If the outgoing framebuffer had pending clears and no
  draw happened, force a begin-and-end pair to flush them (M-A3). Record the new framebuffer, clear the
  pending array, mark viewport and scissor for emission (M-A6).
- `ClearColorTarget(i, rgba)` and `ClearDepthStencil(d)`. If no encoder is open, store the value as pending,
  which becomes `loadAction = Clear` with that clear value ON ATTACHMENT `i` (M-A2). If an encoder IS open,
  end it and begin a new one with the clear folded in, which is what the incumbent's `EnsureNoRenderPass` in
  `ClearColorTargetCore` already forces.
- First draw. Build the descriptor: per attachment, `loadAction = Clear` with the pending value if there is
  one, else `Load`, and `storeAction = Store` always (M-A4). Open the encoder. Emit viewport and scissor if
  marked. Then the draw.
- `End()`, or any command illegal inside a render encoder (M-A5): end the encoder, flushing pending clears
  through a begin-and-end pair if there were any and no draw came.

**Metal's render pass descriptor is what phase 3 had to reach dynamic rendering to get**, and it has carried
load and store actions since Metal 1. The clear-versus-load selection, the clear-only pass and the
end-before-an-illegal-command invariant are all the same shape as V-A2 through V-A5, which is #531's
prediction about Metal and Vulkan mapping onto each other holding up.

### 7.2 The per-attachment clear (M-A2)

This is the one place this design deliberately renders differently from the incumbent, so it gets its own
gate, its own switch and its own deadline.

The fix is one index. The consequence is that `ModelFB`'s normal and linear-depth attachments start being
cleared where today they load. Whether that moves a pixel depends on what those attachments contain at the
start of a golden capture, which is a freshly created `StorageModePrivate` texture that nothing has written.
**That is precisely the "undefined is not stable across runs" case V-F8 and V-A6 both legislate against**, and
it means the CURRENT behaviour is the unstable one: the committed metal goldens were baked reading two
attachments nobody had written.

`KE_METAL_CLEAR=attachment0` reproduces the incumbent exactly, for the A/B on the first golden run. By
V-RO4's sort it selects a branch inside one implementation, so it is cheap, and its deadline is GATE 1: once
the goldens have answered, the switch is removed and the losing branch deleted.

**And the renderer-side comment is a doc task with an owner.** `ModelRenderer.BeginModelPass` currently tells
the next reader that Metal collapses MRT clears, which will be false on the native leg and is an incomplete
description of the Veldrid leg. Row 19 rewords it to name the implementation it describes, which is V-C3's
precedent for exactly this kind of stale mechanism comment.

### 7.3 The viewport and the scissor (M-A6, M-A7)

There is no `SetViewport` on the seam. The engine gets a viewport because Veldrid's base `SetFramebuffer`
auto-calls `SetFullViewports()` and `SetFullScissorRects()`, wrapped in an `if (_framebuffer != fb)` identity
guard. **Both halves must be reproduced**, for the third time in this program: a backend that does not emit
rasterises nothing, and a backend that emits unconditionally silently restores the full scissor on a redundant
re-bind, which is golden-visible and which phase 2's first spec froze the wrong way into its tally test.

Two Metal specifics.

**The scissor flush stays gated on the pipeline's `ScissorTestEnabled`**, which is what the incumbent does.
Metal has no scissor-test enable (the rect is always live, defaulting to the full attachment), so the gate is
the backend honouring the seam's own rasterizer state rather than the API's. D3D11 honours the same flag
through a real enable bit. Reproducing it keeps the three backends agreeing, and NOT reproducing it would make
a scissor set before a pipeline with the test off apply on Metal and not on D3D11.

**The plural setters are used unconditionally (M-A7).** The incumbent picks between `setViewport:` and
`setViewports:count:` on `IsSupported(macOS_GPUFamily1_v3)`, which is a deprecated-enum read on the hot path
to choose between two calls that do the same thing at count 1. The seam has no multi-viewport concept, so the
count is always 1, and one code path is the answer.

`ClipSpaceYInverted` is false on Metal and `IsUvOriginTopLeft` is true, both of which the incumbent reports
and neither of which needs a viewport trick. Vulkan's negative-height viewport, the single most consequential
line in the phase-3 design, has no occupant here at all.

---

## 8. The binding model

### 8.1 Three index spaces, and the seam maps onto them cleanly

MSL gives each stage three separate argument tables: `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]`,
with at least 31 buffer entries per stage. The seam's resource kinds map one to one:

| Seam kind | Metal table |
|---|---|
| `UniformBuffer` | buffer |
| `StructuredBufferReadOnly` / `StructuredBufferReadWrite` | buffer |
| `TextureReadOnly` / `TextureReadWrite` | texture |
| `Sampler` | sampler |

Within one layout, each element takes the next index in its kind's counter, in declaration order. That is
exactly what `MTLResourceLayout` already computes and it is right. Across layouts, set N's base per kind is
the sum of the preceding layouts' counts for that kind, which is what `GetBufferBase` and its siblings
compute, correctly but by re-walking the layout array on every single bind. This design computes the base
VECTOR once at pipeline creation and stores it on the pipeline.

### 8.2 Vertex buffers get the top of the space (M-B2)

The one real collision in Metal's model is that vertex buffers and resource buffers share the `[[buffer(n)]]`
space of the vertex stage. `ResourceBindingModel` is the fork's answer: `Default` puts vertex buffers at
`0..V-1` and shifts resource buffers up by `V`, `Improved` puts resource buffers at `0..B-1` and shifts vertex
buffers up by `B`. Either way one numbering depends on the other's count, in two places (`MTLPipeline`'s
vertex descriptor and `MTLCommandList`'s `setVertexBuffer` index), and getting them out of step binds a vertex
buffer where a uniform should be.

Pin vertex buffers at the TOP instead: buffer index 30 for stream 0, 29 for stream 1, and so on. Resource
buffers grow from 0 upward. Neither depends on the other's count, the two can only collide if a pipeline
declares more than 31 combined bindings on one stage (asserted at pipeline creation with a named exception),
and `ResourceBindingModel` stops being a concept the engine has.

**That also disposes of the phase-2 correction #526 carried into this brief.** `ResourceBindingModel` is read
only by the fork's Metal backend, and it is read to solve a problem this design does not have.

### 8.3 The numbering is pinned into the MSL (M-B1, M-T3)

The CPU side computes an index for every element of every layout. The emitted MSL declares an index for every
resource. **They must agree exactly, and nothing about SPIRV-Cross's default MSL numbering guarantees they
do.** The engine's own memory records the failure mode from the other side: SPIRV-Cross assigns MSL texture
indices in the order textures are first SAMPLED in the fragment shader rather than by `binding=` decoration,
which cost the model pass its albedo texture in 7.25.0 and cost `EdgeFrag` its normal term in 7.51.2, and both
were fixed in the SHADER because the binding layer had no way to say otherwise.

SPIRV-Cross's MSL back end has a resource-binding API that takes a (stage, descriptor set, binding) triple and
a desired buffer, texture and sampler index. Pinning every element through it makes the emission agree with
the binder BY CONSTRUCTION rather than by SPIRV-Cross's heuristics, and it is the mechanism
`ResourceBindingModel` gestures at without reaching.

**The test is the point (M-T3).** Parse `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]` out of the
emitted MSL for every shipped program, pair each program with its pipeline's layout array and each layout with
its element array, and assert the emitted index equals the computed one. Device-free, on every `dotnet test`,
over all thirty-odd programs. Phase 2's S2 had to do this for HLSL registers and phase 3's V-S8 for `set` and
`binding`, and this is the third instance of the same test catching the same failure ("everything compiles and
every pixel is wrong") arriving through a third door.

### 8.4 Declining argument buffers, heaps, bindless and `setBytes` (M-B5)

The idiomatic prior wants argument buffers, so the decline needs an argument rather than an omission.

**Argument buffers.** Metal's argument buffers are the descriptor-set analogue and #531 names the mapping.
Reaching them means emitting MSL with `--msl-argument-buffers`, which changes the emission for every program,
changes the numbering scheme M-B1 just pinned, and requires an `MTLArgumentEncoder` per layout plus an
argument-buffer allocation per resource set. **There is no consumer.** This engine's per-frame binding traffic
is dominated by offsets-only rebinds of ONE set, which cost one call each under M-R7 and which argument
buffers do not improve. Phase 2's measured D3D11 shape says the same from the other side (2 calls per draw, 4
per distinct mesh). Trigger that reopens it: a consumer binding many distinct material sets per frame, which
today means a texture-array atlas the splat terrain cannot express, the same trigger V-D8 named.

**Heaps.** Declined and LINKED to M-F5, per 2.4: heap resources are hazard-untracked by default, so a heap
costs the entire barrier subsystem this design deletes. If a heap ever arrives, a tracker arrives with it in
the same change.

**Bindless.** Every route changes the shared GLSL, which puts all three backends' pixels in play at once,
which is the risk shape phase 2 refused for SPIRV-Cross direct bindings and phase 3 refused for descriptor
indexing.

**`setBytes`.** Argued in full in 2.5. Declined, trigger named.

---

## 9. Memory and the uniform ring

### 9.1 There is no allocator (M-M1, M-M2)

Metal owns device memory. `newBufferWithLength:options:` and `newTextureWithDescriptor:` ARE the allocations,
and there is no memory-type enumeration, no `bufferImageGranularity`, no chunk pooling, no free list, no
dedicated-allocation heuristic and no `maxMemoryAllocationCount` to stay under. **The entire subject of
phase 3's section 9.1, three decisions, a work-breakdown row and a conditional VMA rejection, has no occupant
here.**

Every buffer is `MTLStorageModeShared`, reproducing the incumbent, which on unified memory means the CPU and
the GPU address the same pages. So there is no transfer path for buffers at all: a write is a write.

**The Intel Mac case is recorded rather than built.** On a discrete Mac GPU, Shared memory is uncached system
memory that the GPU reads across PCIe, and `StorageModeManaged` with an explicit `didModifyRange:` is the
correct shape. The incumbent uses Shared universally and the fleet target is Apple Silicon, so parity says
Shared and the Managed path is a follow-up with a named trigger (a consumer report from an Intel Mac).

### 9.2 The ring (M-M3 to M-M7)

Every `UniformBuffer`-usage buffer is ONE `MTLBuffer` of `stride * FramesInFlight` in Shared memory, where
`stride = align(size, max(256, minUniformBufferOffsetAlignment))`. `FramesInFlight = 3`. The `IGpuBuffer`
identity NEVER changes and the frame base is applied AT BIND.

- A record-time `UpdateBuffer(buffer, offset, data)` is `memcpy(contents() + frameBase + offset, data, n)`.
  No staging buffer, no blit, no encoder split, no allocation, no release.
- Every bind of a ring-backed uniform supplies `frameBase + rangeOffset + callerDynamicOffset` through
  `setVertexBufferOffset:` or `setFragmentBufferOffset:`.
- Frame N uses segment `N % FramesInFlight`. Before handing out a segment the ring reads the timeline value
  the frame that last owned it recorded and blocks if it has not been reached, counting the stall.

**The alignment number, and why it is not the device's.** `MTLGraphicsDevice.GetUniformBufferMinOffsetAlignmentCore`
reports 16 on macOS and 256 on iOS. Flooring at 256 makes the stride device-independent and matches what the
seam already documents on `SetGraphicsResourceSet`'s dynamic-offset overload ("256 bytes is safe across
Metal/D3D11/Vulkan") and what every shipped slot size already obeys. That is V-M5's own reasoning reaching the
same floor from the other direction: on Vulkan 256 is the spec's required MAXIMUM for the device's minimum, so
flooring there removes a device-shaped number from under a golden-bearing path, and here it removes a
platform-shaped one.

**Why the ring is worth more here than on either predecessor, in the corrected form (2.1).** On the incumbent,
a record-time uniform write costs an `MTLBuffer` allocation, a `memcpy`, an ENCODER SPLIT, a blit, a release,
and then a full graphics state re-activation at the next draw (the pipeline's five calls, every resource set's
elements one by one, the viewport and the scissor). Under the ring it costs a `memcpy`. **The saved work is
not the copy, it is the encoder.** That is a third distinct mechanism for the same defect class across three
backends (CPU stalls on D3D11, render-pass splits and barriers on Vulkan, encoder splits and state wipes
here), and the convergence is the argument that the ring is a property of the seam's usage rather than of any
one API.

**It is also a CORRECTNESS change here (M-M7), which it was not on D3D11.** `MTLGraphicsDevice.UpdateBufferCore`
is an unguarded `memcpy` into `contents()`. Nothing checks whether a submitted command buffer is reading those
bytes. D3D11's `MAP_WRITE_DISCARD` gives the driver licence to rename a buffer under a write and Metal renames
nothing, so this is the same data race V-M5 identified on Vulkan, present in shipped code, and the ring's
fence gate is what removes it.

**#484 adopted wholesale (M-M5).** An off-timeline write reaches EVERY segment, gated on the same completion
read, with a non-current segment deferred as a pending patch rather than waited on, and the current segment
always written ungated. Not re-derived. The Vulkan design's note applies verbatim: it cost a consumer defect
to learn once, and the non-terminating retry loop that was drafted before it is the thing not to re-invent.

**U3's two invariants verbatim (M-M6).** Only `UniformBuffer`-usage buffers are ring-backed. A ring-backed
buffer never receives a non-uniform binding, and a buffer created `UniformBuffer | StructuredBufferReadOnly`
or with either read-write structured bit throws at CREATION. That combination is vacuous in the engine today
and legal on the seam, so it is a **backend-divergent creation failure** and is documented as one in the
package README rather than discovered by a consumer.

**The one invariant that shrinks (M-M4).** D3D11 owes a 16-constant round-up on both the first-constant and
the count, and its first version shipped the wrong one and silently dropped binds. Vulkan owes
`rangeOffset + callerDynamicOffset + range <= stride` against a VUID, because a descriptor carries a range.
Metal's `setBufferOffset:` carries no length at all, so what remains is
`frameBase + rangeOffset + callerDynamicOffset + size <= (frame + 1) * stride`, which is the same invariant
with nothing to violate it except arithmetic. It is asserted device-free over every shipped set shape anyway,
because it is the Stride row of section 9.4's inventory and this backend owns its own.

### 9.3 Bulk uploads, textures and staging (M-M8, M-M9)

Record-time `UpdateBuffer` on a non-uniform buffer, and `UpdateTexture`, write into a per-list staging arena
(one Shared `MTLBuffer`, sub-allocated, recycled at slot retirement) and record a blit copy. They are bulk and
rare relative to the uniform sites, and **they still split the encoder**, which is exactly why the ring exists
and why moving uniform writes off this path is the whole win.

The incumbent's per-call allocate-and-release is replaced (its own TODO asks for this). The disposal is safe
either way, because a command buffer retains what it references, but an allocation and a release per call on a
path that runs per caster per cascade is not.

**Texture creation issues no submit on the incumbent**, so V-M10 has no occupant here and the Vulkan phase's
two-hundred-submits-per-scene-load finding does not transfer. What DOES transfer is device-level
`UpdateTexture` on a non-staging texture, which creates a staging texture, a command list and a whole queue
submit. That moves onto a device-owned setup command buffer under a short SETUP LOCK, flushed lazily at the
next submit OR at any device-level read (`Map`, readback, an explicit drain). The read-path flush is what
makes the claim true without a hole, and it is V-M10's mechanism applied where it is genuinely needed rather
than ported wholesale.

Staging TEXTURES stay `MTLBuffer`-backed with the incumbent's software subresource layout reproduced byte for
byte (M-C5, section 13).

### 9.4 The ring policy inventory, retired into tests

Section 9.4 of the Vulkan design is a ten-row policy checklist with an Owner column, seven rows owned by
shared semantic tests and three by each backend. M-P5 adds the Metal adapter, so those seven rows become
assertions about three implementations. The three backend-owned rows stay backend-owned and their reasons hold
here:

- **Ordering.** The ring cannot recycle before the completion primitive exists. Row 5 (the timeline) is a
  prerequisite of row 8 (the ring), which is the dependency edge phase 2's first spec dropped.
- **Lock legality.** Because the off-timeline path never waits, a caller already holding `_submitLock` is
  legal. Each backend has its own lock and its own deadlock to not have.
- **Stride.** The arithmetic differs where the invariant does not, and M-M4 states this backend's form.

**That is the shape of the answer to #531 for the ring, and it is why the ring's code is not extracted
(2.2).** The policy is shared and executable. The mechanism is three different things.

---

## 10. Synchronisation

### 10.1 The timeline (M-F1 to M-F4)

One `MTLSharedEvent` created at device creation, initial value 0, owned by the device. Every `Submit` calls
`encodeSignalEvent:value:` on the command buffer with the next value before committing. `IGpuFence` holds a
target, `Signaled` is `sharedEvent.signaledValue >= target` (a non-blocking property read, which is exactly
what the seam demands), and `Reset()` clears the target.

**What this deletes is the point.** The incumbent's fence path is: a hand-built `BlockLiteral` and
`BlockDescriptor` allocated with `Marshal.AllocHGlobal`, an invoke pointer from
`Marshal.GetFunctionPointerForDelegate`, a `_NSConcreteGlobalBlock` isa loaded out of `libSystem.dylib` by
name, a completion handler registered on every submitted command buffer, a lock plus a dictionary lookup
INSIDE the driver's completion callback, a second process-global dictionary and a static callback for AOT
targets, and a `ManualResetEvent` per fence with a pooled array of them for `WaitForFences`. One
`MTLSharedEvent` replaces all of it.

**The seam's fence ordering becomes a theorem (M-F2).** The seam promises that a fence handed to a submission
made after some earlier work signals only once the queue has drained through it. A shared event's signal
operations from one queue execute in submission order and the values are monotonic, so the value reaching 6
requires the signal at 5 to have happened, which requires submission 5 to have completed. Polling a later
fence transitively covers every earlier submission, which is what `RetiredResourcePool` relies on. That is
V-F2's argument, and it is the same argument because it is the same primitive.

**`SupportsCompletionFences = true` is PARITY (M-F3).** `VeldridMap` already reports true for Metal, with a
doc comment explaining that Metal registers the fence against the command buffer and sets it from the
completion handler. Phase 2's C5 was an upgrade because D3D11's fence was a submit receipt. Nobody should look
for that win twice, and gate 2's skip criterion follows: the `RequiresCompletionFences` pair already RUNS on
this leg, so the criterion is NO NEW SKIPS rather than two fewer.

**`WaitForIdle` is `waitUntilSignaledValue:timeoutMS:` on the last submitted value (M-F4)**, counted into
`DrainCount` and `DrainMs`. The incumbent instead retains `_latestSubmittedCB` under a lock and calls
`waitUntilCompleted` on it, which needs the buffer kept alive to be read and which gives nothing to count
without extra bookkeeping. There is no C6-style bet here: the incumbent's drain is already real, and phase 2's
win was in making an empty method body exist.

### 10.2 Hazards, and the machinery that is absent (M-F5 to M-F8)

Argued in 2.4. In one table, so a reader can see the size of what is not here:

| Phase 3 decision | Metal occupant |
|---|---|
| V-F6, `vkCmdPipelineBarrier2` with explicit stage and access masks | None. Automatic |
| V-F7, canonical resting layouts and list-local tracking | None. No layouts |
| V-F8, the `UNDEFINED`-discard determinism rule | None. No layouts |
| V-F9, deferred disposal behind a timeline value | None. A command buffer retains what it references |
| V-C1, a real image barrier at the sampled bind for rule 1 | None. The encoder boundary is the dependency |
| V-C2, a read-after-write barrier between dependent dispatches | None. Serial dispatch type |
| C1, SRV-versus-UAV auto-unbind in both directions | None. Not a binding-model problem here |

**M-F7 is the one with a consequence beyond this backend.** After this phase, three of three engine-owned
backends honour seam rule 2 natively (D3D11 tracks hazards, native Vulkan emits a real barrier, Metal orders
serially), and only the two Veldrid legs need the drain. That is the quorum #461 has been waiting for, and
phase 3's VF10 already named it as advanceable. It is stated as EVIDENCE, not as a contract change: rule 2 is
cross-backend, the Veldrid legs still need the drain, and a consumer that drops the drain because the native
Metal backend tolerates it breaks on the backend its machine falls back to.

### 10.3 The seam comment is now wrong in a third way

`GpuInterfaces.cs`'s rule 1 and rule 2 comment names mechanisms per implementation, which phase 3 corrected
it to do. Its Metal sentence ("Metal ends the compute encoder when the render encoder begins") is correct for
both Metal implementations. Its rule 2 paragraph says a submit boundary plus a device drain is the only
ordering the seam can guarantee, then adds that the native Vulkan backend is more permissive. **After this
phase that paragraph needs a third arm**, and adding it is a doc task with an owner in row 19, because R4's
precedent is that an unwritten contract decays and a wrong written one decays faster.

---

## 11. Swapchain, present, resize and threading

### 11.1 What is reproduced (M-W1)

The `CAMetalLayer` configuration is reproduced from the incumbent exactly, because it is visible only to a
human and W1's lesson binds hardest where nothing in CI runs: the layer adopt-or-create dance on the host
view, `device`, `pixelFormat` from the sRGB request, `framebufferOnly = true`, and `drawableSize` from the
window's content size.

The Metal golden suite is headless and renders into offscreen textures, so **not one line of the present path
runs in CI on any leg, ever.** That is MW7, recorded as an observation so nobody reads a green golden leg as
evidence about presentation.

### 11.2 What changes (M-W2 to M-W5)

**Vsync is applied unconditionally (M-W2).** The incumbent's three-value equality against `MaxFeatureSet`
means that on any machine outside that set, `SyncToVerticalBlank` silently does nothing.
`CAMetalLayer.displaySyncEnabled` is macOS-only and needs no capability test. Reproducing a fragility whose
failure is silent is not parity, which is V-W2's ruling on the two Vulkan bugs applied to a third.

**The drawable acquire keeps its timing and gets measured (M-W3, M-W4).** `nextDrawable` is taken at the
present boundary for the NEXT frame, which is what makes the drawable known before recording starts, so
nothing about record-time framebuffer resolution changes. It BLOCKS when no drawable is free, and unlike
Vulkan's acquire there is no semaphore variant to move the wait to the GPU. **So this design does not remove
the stall, it moves it to the submit thread and counts it**, into `AcquireWaitCount` and `AcquireWaitMs`.

Phase 3 added that pair to `GpuDeviceCounters` for `vkAcquireNextImageKHR`, arguing that without it MV2's gate
had only mean frame time to read. It is exactly the right instrument here and this phase adds nothing to the
seam. `maximumDrawableCount` is set to `FramesInFlight` so the depth of the drawable queue and the depth of
the uniform ring are one number.

**A nil drawable stops discarding the frame silently (M-W5).** `nextDrawable` can return nil (the framework
gives up after about a second under pressure). Today `IsRenderable` goes false, `PreDrawCommand` returns false
for every draw, and the frame is recorded and thrown away with nothing logged and nothing counted. The rule
here is V-W4's: the wrapper is repointed at a device-owned ORPHAN TARGET (one colour texture at the current
extent, clamped to a minimum of 1 by 1, matching the swapchain framebuffer's shape), the frame records,
submits and completes exactly like any other, only its PRESENT is skipped, and it counts into `FramesBegun`
because the device opened it and the work really happened.

**The present stays on its own command buffer (M-W6),** argued in 2.6.

### 11.3 Resize (M-W7)

`ResizeSwapchain(w, h)` stores the pending size coalesced to the last requested and returns. It applies at the
next present boundary on the submit thread, where the boundary provably owns the queue and no recording is in
flight. Applying it means writing `drawableSize`, rebuilding the depth texture, and swapping the new depth
view into the EXISTING `IGpuFramebuffer` wrapper so its identity survives.

**Metal needs no swapchain recreation**, which is why the seam's existing "no recreate" wording describes
Metal and needs no Vulkan-style sentence. The colour attachment is the drawable's texture, resolved at
descriptor-build time, so a resize never invalidates a view a recording already holds. That is the property
V-W5 had to work for and gets for free here.

### 11.4 Threading (M-W8)

- Recording is lock-free and per list. Any number of lists may record concurrently on any threads.
- One `_submitLock` covers `commit`, `presentDrawable` plus its commit, and the resize apply. Microseconds,
  not a frame.
- The SETUP-BUFFER lock covers appends to the device-owned setup command buffer and its lazy flush. The flush
  takes the submit lock under it, in that order and never the reverse.
- The RING lock covers a segment acquire and an off-timeline write, scoped to the write and never to a frame.
  A caller already holding `_submitLock` is legal because the off-timeline path never waits.
- Device-level `UpdateBuffer` and `UpdateTexture` are callable from any thread behind those short locks.
- Resource creation is otherwise free-threaded. `MTLDevice` factory methods are thread-safe.
- Submit order is the observable order (M-N2).
- `GpuDeviceContext._lifecycleGate` is unchanged.

Multi-threaded recording is STRUCTURALLY SUPPORTED and is not in the shipped contract, which is W5's position
unchanged for the third time.

---

## 12. Shader path

### 12.1 The split pays out, and then asks for one more thing

`SpirvCrossCompile` is already split: `SpirvFrontEnd.ToSpirv` is glslang, and `VertexFragmentToHlsl` plus
`ComputeToHlsl` are SPIRV-Cross. **That split was the entire Metal-facing carrying cost of phase 3 and it was
one file move**, and its payout is that this phase adds `VertexFragmentToMsl` and `ComputeToMsl` beside the
HLSL pair and touches nothing else. The front end is untouched, so the SPIR-V byte-equality drift test and
`VulkanSpirvIncumbentParityTests` both keep meaning what they meant.

### 12.2 Taking #462, scoped to MSL (M-S2)

#566 banks that "the shader back end grows an MSL target via the direct SPIRV-Cross migration (#462,
deliberately deferred to this phase)". This design takes it and SCOPES it, and the scoping is the decision.

**Scope: the MSL target moves onto an engine-owned P/Invoke shim over `libveldrid-spirv`. The HLSL target
keeps the managed `Veldrid.SPIRV` call.**

- The shim is needed because M-B1's index pin is not expressible through the managed surface, which exposes
  `CrossCompileOptions` and nothing about MSL resource bindings.
- The HLSL target is left alone because moving it changes the emitted HLSL, which changes register numbering
  and drop-unused behaviour, which puts D3D11's 36 committed goldens and both documented WARP corruption
  incidents in play. Phase 3 refused that for a phase that could not see it, and this phase can see it no
  better: a metal-native leg does not run the D3D11 goldens.
- `Veldrid.SPIRV` therefore STAYS in the graph after this phase, for glslang and for HLSL. What changes is the
  arity again: after this phase, two of three back-end targets run through engine-owned code.
- Moving the front end and the HLSL back end onto the shim is the program's CLOSING ACT (section 19), and the
  two instruments that make it checkable already exist: the SPIR-V byte-equality drift test and the HLSL
  byte-equality hash table, built by phases 3 and 2 respectively.

**`libveldrid-spirv` is still a bundled native per RID after this**, which is the packaging burden #420 partly
exists to reduce. This design does not reduce it and says so.

### 12.3 There is no parity measurement, and that is the honest position (M-S3)

Phase 3's V-S2 licensed "no rebake" with a one-off in-process measurement: every shipped program compiled
through both paths, 76 of 76 stages byte-identical. **No analogue exists here.** The engine has never emitted
MSL. The incumbent's MSL is produced inside `Veldrid.SPIRV` on a Metal device and never surfaces. And under
M-B1 the emission is deliberately DIFFERENT, because the numbering is pinned.

So what licenses the committed `metal` goldens carrying over is:

1. **M-T3's index-table test, taken BEFORE the first golden run.** If the binder and the emission agree on
   every index of every program, the remaining difference between the two backends is the backend, which is
   what a golden should be testing.
2. **The goldens themselves**, on a real device, at 0.06 absolute per channel.
3. **`KE_METAL_CLEAR`'s A/B (M-A2)**, which isolates the one other deliberate rendering change so a golden
   delta has one candidate cause rather than two.

**And the risk is named at full strength in 2.7 rather than buried here:** the `metal` family is the fleet's
cross-backend reference, and this design is proposing to move the artefact every other backend is checked
against. Gate 1 is where that is answered.

### 12.4 The compile options are pinned (M-S5)

`MTLShader` passes `MTLCompileOptions.New()`, a default-constructed object. Two defaults matter.

**`fastMathEnabled` defaults to YES**, and fast math changes floating-point results. The committed metal
goldens were baked with it on. Pinning it to on is a no-op today and a guard forever, and flipping it is the
kind of change that moves every pixel with no other symptom.

**`languageVersion` defaults to the newest the OS supports**, which DRIFTS with the runner image. The workflow
already pins `macos-26` by number rather than to `macos-latest` "so an image promotion cannot move the GPU
under a golden gate", and the language version is the same class of hazard one level up. Row 1 MEASURES what
macos-26 reports and pins that value, so the pin is a no-op on the day it lands.

`MslCompilePin` holds both as constants with the citation, and derives an `Identity` string from them for the
cache key, in the exact shape `HlslCrossCompilePin` and `SpirvFrontEndPin` already use, so a pin change moves
every cache key by construction rather than by remembering.

**`preserveInvariance` is left at its default (off)**, matching the incumbent. It forces position computation
to be invariant across pipelines, which matters for multi-pass depth equality, and turning it on is a
follow-up with a trigger (Z-fighting between the depth prepass and a later pass) rather than a speculative
change to a golden-bearing knob.

### 12.5 The `.metallib` cache (M-S6)

A per-program `.metallib` written to disk and loaded through `newLibraryWithData:`, keyed on the MSL pin
identity, the SPIR-V hash, the device's registry identity and the engine version. Header-validated before
trusting, discarded silently on any mismatch, best-effort so a read or write failure is never fatal. This is
S4's disk cache and V-S7's `VkPipelineCache` with a third noun, and M-P4 extracts the KEY discipline the three
share.

The incumbent compiles MSL from SOURCE at every launch through `newLibraryWithSource:`, for every shipped
program, exactly as the incumbent Vulkan backend compiles every pipeline from SPIR-V at every launch.

`MTLBinaryArchive` (a compiled pipeline-state cache, one level further down) is DECLINED for v1: it is a
second, newer mechanism for the same win, it needs its own serialisation discipline, and the library cache is
where the compile time actually is.

### 12.6 Two things this backend must not "fix" (M-S7, M-B4)

**S5's holed-signature sinks stay.** They are FXC-and-WARP specific, the D3D11 leg ships until the closing
act, and removing one corrupts WARP. This is the third design that has to write that sentence, and a Metal
seat is where it looks most pointless, because Metal tolerates the holes.

**The "sample all textures up front in binding order" shader discipline stays**, even though M-B1's index pin
removes the SPIRV-Cross behaviour that made it necessary. Same reason: the Veldrid Metal leg is still
selectable and still numbers by sample order. It comes out with that leg, in the closing act, not before.

**And M-B3's discipline covers the third one.** The one-uniform-buffer-per-pipeline invariant STAYS in force
until MM6's measurement says otherwise, and a shader change on the strength of an untested hypothesis is
exactly what this design refuses.

---

## 13. Compute, MSAA, staging and readback

**Compute (M-C1, M-C2).** Compute and graphics bindings are tracked separately with separate dirty arrays and
separate bound-pipeline slots, as the seam requires. `SetComputePipeline` and `Dispatch` end any open render
encoder first (M-A5). The compute encoder is created with the default SERIAL dispatch type, which is what
makes M-F7 true. Storage buffers are plain buffers at a `[[buffer(n)]]` index, with the pipeline's buffer
mutability declared at creation the way the incumbent declares it. `SpirvLocalSize`'s hand-parse is still
needed, because `IGpuComputeShader.ThreadGroupSize*` must report the workgroup size and MSL takes the
threadgroup size at dispatch.

**MSAA (M-C3, M-C4).** `MaxMsaaSampleCount` reproduces the incumbent's computation, which asks
`supportsTextureSampleCount:` per count and ignores both of its parameters, and the pin carries the citation
plus the note that the argument-ignoring is CORRECT because Metal's only sample-count query takes no format.
The engine's `VeldridMap.MaxMsaaSampleCount` takes a MIN over three formats and on Metal all three answer the
same, so the min is a no-op and reproducing it means reproducing the same single answer.

An out-of-range requested sample count THROWS at texture creation rather than silently falling to 1, which is
C4's departure inherited for C4's reason.

`ResolveTexture` opens an empty render encoder with the MSAA source as `colorAttachments[0]`,
`loadAction = Load`, `storeAction = MultisampleResolve` and the destination as `resolveTexture`, then ends it.
That is the incumbent's shape and it DESTROYS the source's contents, which the incumbent's own TODO says and
which diverges from `ResolveSubresource` and `vkCmdResolveImage`. Reproduced anyway (M-C4): the engine's MSAA
sources are re-cleared at the start of the next frame's pass, discarding is the bandwidth-correct answer on
this architecture, and it is what the goldens were baked against. **The divergence goes in the package README**
so a consumer that ever needs the source preserved finds a documented property rather than a surprise.

**Staging and readback (M-C5, M-C6). This is the highest-risk parity surface in the design and it earns its
own paragraph, for the same reason it did in phase 3.** Every golden reads back through
`IGpuDevice.Map(staging, ...)` and consumes `MappedData.RowPitch`. The incumbent backs a staging texture with
a Shared `MTLBuffer` rather than a linear texture, and computes row pitch, depth pitch and the subresource
offset IN SOFTWARE (`MTLTexture.GetSubresourceLayout`, `MTLTexture.GetSubresourceSize`,
`Util.ComputeSubresourceOffset`). A different arithmetic garbles all 36 goldens at once. Reproduce it byte for
byte and pin it in a device-free table test over a spread of formats, sizes, mip levels and array layers,
asserted against a checked-in table taken from the incumbent's own computation.

`Map(staging, Read)` WAITS on the timeline's last submitted value before returning the pointer, counted as a
drain (M-C6). The incumbent returns `contents()` immediately, which works today because `GpuReadback` submits
and drains before mapping, so the seam's guarantee currently rests on a caller convention rather than on the
backend. Closing it is cheap and it is V-C8's ruling for V-C8's reason.

---

## 14. Capabilities and diagnostics

`ReadCapabilities` stays the single source both `GpuDeviceContext.Capabilities` and `IGpuDevice.Capabilities`
come from. The native device implements one and the context reads it from the device.

| Member | Native source | Parity |
|---|---|---|
| `ClipSpaceYInverted` | false | identical, and no viewport trick is needed to make it so |
| `DepthRangeZeroToOne` | true | identical |
| `DeviceName` | `MTLDevice.name`, verbatim | identical by construction, given M-N1's default selection |
| `SamplerAnisotropy` | **true, hardcoded**, as the incumbent hardcodes it | identical |
| `SamplerLodBias` | **false**, because `MTLSamplerDescriptor` has no LOD bias at all | identical, and it is the one capability that differs from both other native backends |
| `MaxMsaaSampleCount` | the incumbent's own computation reproduced, format-independent (M-C3) | asserted identical, and satisfiable by construction rather than by luck |
| `SupportsShadowMaps` | the incumbent's own question: is `R32_Float` usable as BOTH render target and sampled | asserted identical |
| `SupportsCompute` | true | identical |
| `SupportsCompletionFences` | **true** (M-F3) | identical, and it was already true |

**ZERO permitted differences (M-G1)**, which is phase 3's bar rather than phase 2's, and it is right for the
same reason: the incumbent Metal backend has no capability defect to correct. The test carries the
reflection-completeness check, so a member appended later cannot silently weaken the assertion.

**Two carried warnings, because the mistakes are available here too.** Phase 3's `SupportsShadowMaps` shipped
asking a structurally-false question because the implementer wrote what the member's NAME suggested instead of
what the incumbent asks, and the failure was silent (the shadow path degrading to blob shadows on one backend
with nothing red). And `DeviceName` must not be tidied: whatever `MTLDevice.name` returns is what the
incumbent reports, padding included.

**The shared point and linear samplers WRAP on all three axes**, built from wrap-addressed descriptions and
NOT from the identically named `GpuSamplerDescription.Point` and `.Linear` statics, which default every axis
to CLAMP. The seam says so in writing on `IGpuDevice.PointSampler`, and reading the address mode off the
statics because the names matched cost two goldens on the D3D11 leg. This paragraph exists because the same
mistake is available a third time. The incumbent's other sampler mappings are reproduced exactly, including
`borderColor` being set only on macOS and `maxAnisotropy` being clamped to a minimum of 1.

**Device selection and the software-rasterizer field (M-G2).** `KE_METAL_DEVICE` per M-N1. `softwareAdapter`
is ALWAYS false, because there is no software Metal device, and CI pins nothing. Said out loud because a
reader will look for `KE_D3D11_ADAPTER`'s and `KE_VULKAN_DEVICE`'s CI pin and should find a decision. The
integrity hole those pins closed (a runner image growing an adapter and silently changing the rasterizer under
a golden gate) is closed differently here, by the workflow pinning `macos-26` by number.

**Validation (M-G3), and the honest part.** Metal API validation is a FRAMEWORK-level facility enabled by the
process environment at device creation, not by an API call, and there is no
"install a layer" answer of the kind phase 3 had to price. `KE_METAL_VALIDATION=1` sets the framework's
validation environment before the first `MTLCreateSystemDefaultDevice` and LOGS whether the device came back
wrapped. `strict` additionally latches and throws at a controlled point on a command-buffer error.
**Whether in-process environment mutation actually reaches the framework is a ROW-1 SPIKE**, and if it does
not, the answer is that the CI leg sets the variable in the job environment and the local answer is a
documented `KE_` prefix on the run command, which is worse ergonomics and no less correct.

**Command-buffer errors and device loss (M-G4).** Every command buffer's `status` and `error` are read at
completion. A `status` of `Error` latches the `MTLCommandBufferError` code and the localized description AT
the fault site, flips the liveness token so subsequent disposals are no-ops, and surfaces through the existing
`deviceLossReason` header field. That closes #427 for the Metal leg on the day the backend lands, which is the
correct time, because retrofitting the reporting after the first field crash wastes the crash. The incumbent
reads neither `status` nor `error` on the completion path at all: it looks at `status` in one place, inside
`WaitForIdleCore`, to decide whether to wait.

**Frame capture (M-G5).** `MetalFrameCapture` currently reaches Veldrid's PRIVATE `_commandQueue` field by
reflection and gives up silently if the layout differs. The native backend exposes its queue pointer, so the
reflection goes away for this backend and the capture stops being one Veldrid refactor away from silently not
working. Small, concrete, and it has an owner in row 18.

**Counters (M-G6).** Every member of `GpuDeviceCounters` is populated from the struct as it stands, and **this
phase adds nothing to the seam.** The acquire-wait pair phase 3 appended for `vkAcquireNextImageKHR` is what
`nextDrawable`'s block needs. `BackpressureStallCount` counts the ring acquire alone here, where on Vulkan it
folds in the command-buffer slot wait, so its doc comment gains a sentence saying the second meaning is
Vulkan's rather than universal.

---

## 15. Test plan

| Layer | What it covers | Runs where |
|---|---|---|
| The 36 committed `metal` goldens, shared family (M-T1) | Pixel equivalence against the SHIPPED Metal backend on the same hosted `macos-26` device at 0.06 absolute per channel. No rebake | New `metal-native` leg, full suite on every trigger |
| `CrossBackendGoldenTests` | Unchanged. Still three families, still the 0.20 ceiling. **It is also the thing that would catch a bad `metal` bake, and the metal family is the reference the other two are read against** | Every `dotnet test` |
| **The MSL index-table test (M-T3)** | "Everything compiles and every pixel is wrong", arriving through the MSL door. Taken BEFORE the first golden run | Every `dotnet test` |
| Native-call budget, device-free `[Fact]` (M-T2) | The Metal fan-out class through `IMtlEncoderSink`: argument-table writes, draws and dispatches, and ENCODER BOUNDARIES | Every `dotnet test`, every PR, every cheap leg |
| `NativeVsVeldridMetalCapabilityParityTests` (M-T4) | Silent capability drift, ZERO permitted differences, plus the reflection-completeness check | Metal leg |
| Shared uniform-ring semantic tests plus the Metal adapter (M-T5) | Section 9.4's seven shared rows, now asserted across THREE implementations | Every `dotnet test` |
| Ring stride and bind-window invariant, device-free (M-M4) | `frameBase + rangeOffset + callerDynamicOffset + size <= (frame + 1) * stride` for every shipped set shape | Every `dotnet test` |
| Staging subresource layout table test (M-C5) | A garbled readback on all 36 goldens at once | Every `dotnet test` |
| Encoder-scope re-activation test, device-free (M-R4) | That every encoder boundary is followed by a full re-activation at the next draw, and that a redundant pipeline bind is not (M-R8) | Every `dotnet test` |
| Base-vector invalidation test, device-free (M-R9) | That a pipeline switch between two pipelines sharing a layout array invalidates nothing, and that one that does not share invalidates from the first differing base | Every `dotnet test` |
| Autorelease-pool architecture test (M-N4) | That every public entry point that can create an autoreleased object wraps its body, over the type graph rather than by review | Every `dotnet test` |
| Recording-contract test, device-free | N lists open concurrently, interleaved records, submitted out of record order, per-list order asserted and concatenated in SUBMIT order | Every `dotnet test` |
| Clear-folding test, device-free (M-A2, M-A3) | Per-attachment `loadAction` for a three-target framebuffer, and the clear-only pass still clearing | Every `dotnet test` |
| Viewport and scissor identity-guard test (M-A6) | Exactly one viewport and one scissor emission per framebuffer CHANGE and zero for a redundant re-bind, which phase 2's first spec froze the wrong way | Every `dotnet test` |
| Timeline and fence unit tests (M-F1) | Monotonic signal, non-blocking `Signaled`, `Reset` re-arming, the drain counting into `DrainCount` and `DrainMs` | Every `dotnet test` |
| Drawable-boundary test, device-free (M-W5) | The nil-drawable frame: orphan target bound, recorded, submitted, present skipped, `FramesBegun` incremented | Every `dotnet test` |
| Full `macos-26` suite | 0 failed, 0 skipped, passed at or above the incumbent's on the same commit | Metal leg, every trigger |
| **`SlathRepro` windowed run (M-T6)** | The windowed Metal defect class that headless testing structurally cannot reproduce, which is the class the one-UBO constraint belongs to. **A rollout gate, not a test** | Gate 5, by hand |
| `OpenListTrackingGpuDevice` | Nested `Begin`. Stays the PORTABLE guard, passes trivially here, and is NOT evidence about this backend | Every `dotnet test` |
| `GpuBackendKindAppendAuditTests` | The fifteen sites, extended for `MetalNative`, INCLUDING the two FrameCap rows that are not correct by default | Every `dotnet test` |
| `ArchitectureTests`, `VeldridLockdownTests`, `GpuPublicApiTests`, the no-Veldrid pair, the back-end-member edge | Zero renderer changes, no Veldrid leakage, opt-in isolation, and that the backend names the MSL back-end member and no other | Every `dotnet test` |

**The budget test's gate, stated the way T2 and V-T2 state it.** The gate is (a) structural invariants: one
array call per (kind, stage) per full activation and never one per element, exactly one viewport and one
scissor emission per framebuffer CHANGE, zero encoder boundaries between two draws in one pass, and zero
record-time buffer allocations. (b) Marginal per-draw deltas: 5 distinct meshes against 1, and 18 draws
against 6, must move the total by an exact per-draw delta, and an offsets-only rebind must be exactly ONE call
per visible stage. (c) Trace identity for 8 instances of one mesh against 1. (d) An upper bound on encoder
boundaries per frame. Absolute totals are documentation and may be updated freely, because a test that is
routinely edited to match reality stops being a gate.

**CI (M-T7).** A `metal-native` matrix leg on hosted `macos-26` with `KE_GRAPHICS_BACKEND=metal-native` and
`KE_GPU_TESTS=1`, running the FULL suite on every trigger, matching the incumbent Metal leg's tier exactly,
and sitting bake dispatches out entirely because it is a guest in the incumbent's family.

**This leg is the strongest regression net in the program and that changes what the design can lean on.** The
D3D11 native leg runs golden-only on push. The Vulkan native leg runs golden-only on push, on a software
rasterizer, with no swapchain coverage at all. This one runs everything, on a real GPU, every time. It is why
2.7 argues that this is the right phase for the numbering change, and it is why the interop risk in 2.8 is
bounded.

The `NativeDeviceLifecycle` collection definition is copied into every test assembly carrying `[GpuFact]`,
because collection definitions are per assembly and phase 2 measured that adding a second live-device backend
without one took a leg from 17 minutes to 49.

**The naming contract is unchanged.** `cross-platform-gpu.yml` selects golden-only tiers with
`--filter FullyQualifiedName~Golden`. The Metal leg runs the full suite so the filter does not bind here, and
the device-free tests deliberately do not carry the substring so they run on the cheap legs.

---

## 16. Unproven bets: gates, kill switches, exit criteria, deadlines

Every decision below rests on reasoning rather than measurement. Each names the measurement that settles it,
the switch that turns it off, the criterion that retires the switch, and the deadline (V-RO4's sort: a switch
keeping a SECOND IMPLEMENTATION alive is REMOVED at its gate with the losing path deleted, a tuning knob or an
observation flag may survive).

**Two switches ship in this whole design, and both are branches inside one implementation.** That is
deliberate. Phase 2's gate 3 is still blocked behind an unresolved A/B with two drivers shipping, and this
design does not repeat it: the MSL emission question is decided at a SPIKE (M-S4) rather than carried by a
switch, and the recording model has nothing to A/B.

| # | Bet | Measurement gate | Kill switch | Exit criterion | Deadline |
|---|---|---|---|---|---|
| MM1 | The ring is worth as much on Metal as on the other two, because a record-time `UpdateBuffer` costs an allocation plus an encoder split plus a full state re-activation (2.1). **The magnitude is unmeasured**: nobody has counted how many record-time `UpdateBuffer` calls per frame the #410 scene makes on Metal, and the renderers may already have hoisted most of them out of the pass | Count record-time `UpdateBuffer` calls, encoder boundaries and record-time buffer allocations per frame on the #410 scene ON THE INCUMBENT, before the ring exists. Then the same three on the native backend | None. The ring is not optional here either (M-M7 makes it a correctness change) | Native encoder boundaries per frame at or below the framebuffer-change count plus the compute and blit passes the frame genuinely needs, native record-time allocations at zero, and frame time no worse than the incumbent's gate-4 baseline. If the incumbent's counts turn out near zero already, the ring is still taken for M-M7 and this bet is **RECORDED AS NOT PAYING** rather than quietly forgotten | Gate 4 |
| MM2 | Per-attachment clears (M-A2) do not move a committed metal golden, and if they do, the movement is attributable to exactly that clause | All 36 goldens with `KE_METAL_CLEAR` in both positions on the same build, first green run | `KE_METAL_CLEAR=attachment0` reproduces the incumbent exactly | Both positions green, or exactly the scenes whose framebuffer has more than one colour target differ and the difference is explained by two attachments going from Load to Clear. **A difference anywhere else means something other than this clause moved** | **Gate 1. A branch by 2.7's sort but a rendering-behaviour one, so it is removed there and the losing branch deleted whichever way it goes** |
| MM3 | The array-batched flush (M-R6) collapses a full activation to one call per (kind, stage) and an offsets-only rebind to one per visible stage | The device-free budget test (M-T2), confirmed on the first green run and then frozen as marginals | None needed. A call-count property with no runtime risk | The first green run's measured marginals are recorded in this document's history and become the frozen numbers | Gate 3 |
| MM4 | `FramesInFlight = 3` is enough that ring segment backpressure never blocks the CPU, and `maximumDrawableCount = 3` is enough that the drawable acquire does not become the frame's pacing | `BackpressureStallCount` and `BackpressureStallMs` for the ring, `AcquireWaitCount` and `AcquireWaitMs` for the drawable. **The second pair is expected to be non-zero under vsync and that is not a failure**: a vsync-paced frame SHOULD wait for a drawable, and what the gate reads is the UNCAPPED capture | `KE_METAL_FRAMES_IN_FLIGHT=<n>` | Ring stall count zero across a full capture window. Acquire wait per frame near zero on the uncapped capture. A non-zero ring stall means 3 is wrong, not that the design is | Gate 4. A TUNING KNOB by 2.7's sort, so it may survive as a knob, but only if the exit criterion was met at its DEFAULT |
| MM5 | (observation, not a bet) **Automatic hazard tracking (M-F5) costs conservatism that nothing in this design measures.** The driver may serialise two encoders that could have overlapped, and there is no counter for lost overlap | None available in v1. A GPU trace in Xcode's frame debugger would show it and is not a CI instrument | n/a. `MTLResourceHazardTrackingModeUntracked` on an individual resource is the escape hatch, and taking it means writing the barriers for that resource | Recorded so a reader does not mistake "no barriers in the code" for "no serialisation on the device". If a measurement ever shows the cost, the heap decision (8.4) is re-argued in the same change | n/a |
| MM6 | **The one-uniform-buffer-per-pipeline constraint is a property of the incumbent's numbering and not of Metal**, so a pipeline with a second uniform buffer reads correct bytes under M-B1's pin | A `[GpuFact]` reproducing the shape `GpuSkinningReproGpuTests` established: a pipeline whose vertex stage reads two resource buffers, and a pipeline with a fragment-only second UBO at set 1, each with a pixel READBACK assertion rather than a no-throw assertion. **Plus the windowed `SlathRepro` run, because the 7.18.0-era finding was windowed-only and the memory says a serialized readback test cannot reproduce a windowed multi-submit bug** | None. This is a measurement, not a shipped behaviour. **V-S6's shader-shape invariant STAYS in force regardless of the result** | Both probes read correct values headless AND the windowed run is clean. **A pass does NOT authorise a shader change**: it authorises filing the shader-shape invariant's removal as its own work with its own gates. A fail is recorded here as the constraint being real on Metal rather than on Veldrid, which is worth just as much and closes four sessions' worth of open question | Gate 3 |
| MM7 | (observation, not a bet) **The swapchain has ZERO CI coverage**, because the Metal golden suite is headless and renders into offscreen textures. Every decision in section 11 is validated by a human at a window, or not at all | None available | n/a | Recorded so a reader does not mistake a green full-suite leg for evidence about presentation. Gate 5's manual pass is the only instrument, and `SlathRepro` is the only automated part of it | n/a |
| MM8 | The `.metallib` cache key plus header validation is enough that a stale or corrupt cache can never crash a launch (M-S6) | Startup time cold and warm, plus a deliberate corruption test that truncates and mutates the file and asserts a clean discard | The cache path is best-effort by construction: any read or write failure is a silent discard, which IS the fallback | Corruption test green and no launch failure attributable to the cache across the soak | Gate 4 |
| MM9 | The engine-owned interop layer is ABI-correct on arm64 (2.8) | Row 1's spike compiles one file against every call this design names and runs it against a real device, and then the full suite on the leg is the standing answer | None. An ABI error is a crash, not a tunable | The spike runs clean and the full suite is green on the leg. **An interop defect is expected to present as a crash rather than as a wrong pixel**, which is the one comforting property of this risk | Gate 2 |

---

## 17. Rollout

Opt-in first, then the CI leg as the continuous exercise, then a field soak on a Mac through a game's normal
update flow, then the default. Five gates, all green before any flip.

1. **All 36 goldens green** against the shared `metal` family on hosted `macos-26` at 0.06, with the observed
   worst-cell delta RECORDED here. **MM2 resolved and `KE_METAL_CLEAR` removed.** The
   `golden-deltas.<family>.txt` evidence file appends on a PASS, so the number comes off a green run rather
   than needing one to break first. **This gate is worth more than its siblings and carries more risk**: the
   `metal` family is the fleet's cross-backend reference, so a green here is the strongest evidence any leg in
   this program produces and a red here is a fleet event, not a leg event.
2. **Full `macos-26` suite at 0 failed and 0 skipped**, with the passed count at or above the incumbent's on
   the same commit. **The skip criterion is NO NEW SKIPS**, matching phase 3 rather than phase 2, because
   Veldrid Metal already signals completion fences so the `RequiresCompletionFences` pair already runs on this
   leg. MM9 met.
3. **Budget test green** with the marginals recorded here, MM3 met, the MSL index-table test green, and
   **MM6's measurement TAKEN and recorded whichever way it went.** No M1-equivalent hangs over this gate,
   because there is one recording driver and nothing to A/B.
4. **A field session on a Mac at or above the incumbent Metal's numbers** across a full capture window, with
   zero command-buffer errors, the session header naming `MetalNative`, and MM1, MM4 and MM8's exit criteria
   met. **And the honest problem with this gate is that the incumbent's numbers do not exist yet**: #410's
   published figures are a Windows machine's (125 fps on D3D11, 144 on Vulkan) and there is no published Metal
   field baseline anywhere. So gate 4 has a prerequisite the other two phases did not: **take the incumbent's
   baseline first**, on the same Mac, the same scene and the same capture window, before the native session.
   A gate stated against a number nobody has measured cannot be read.
5. **A human windowed pass**: resize by drag, maximise, fullscreen toggle, alt-tab, and a vsync toggle
   mid-session. **Plus a `SlathRepro` run**, which is the engine's committed windowed Metal regression check
   and the only instrument that has ever caught the windowed-only defect class. `deviceLossReason` present in
   the session header.

**Gate 4 is harder here than it was in either predecessor, and the reason is worth naming.** On D3D11 the
incumbent WAS the problem and parity was already a win. On Vulkan the incumbent was the engine's best backend
on the only field evidence there was. On Metal the incumbent is the backend the fleet's REFERENCE IMAGES are
baked on and the one with no filed defect at all. So the pass bar is "no worse over a week" against a baseline
that has to be measured first, and anyone weighing whether this phase should happen should weigh that
honestly: the case for it is #420's endpoint, the ring's correctness argument (M-M7), and the numbering
question (2.7), not a promised speedup.

**What a flip means here.** `ProbeOS` maps macOS to `Metal`, so flipping changes the macOS desktop default,
which for this fleet is the DEVELOPMENT platform: every developer, every local playtest, every locally baked
golden. That is a smaller population than phase 2's Windows flip and a more consequential one per person, and
it is a reason to hold gate 5 strictly rather than to relax anything.

The flip is one line in `ProbeOS` plus adding the kind to `_windowCandidates` plus the two FrameCap and
DisplaySettings rows (4.2), which are the reason `IsMetal()` exists. `Metal` through Veldrid stays selectable
by token (M-RO2), so a regression is one environment variable away from an A/B on the same build.

The headless default stays on Veldrid until gate 4. An early headless flip would silently reduce the
incumbent's coverage during exactly the window when both legs must stay green, which is RO3's ruling for the
third time.

Before the field capture, pin the session log's build line and the capture-window stamps. A number attributed
to the wrong build is the expensive failure here, and M-I4's throw-on-missing-provider exists specifically to
make it impossible.

---

## 18. Work breakdown

Each row becomes one implementation issue, `kind/backlog` unless noted, `confidence/authored`, linked to #566.

| # | Scope | Regression evidence |
|---|---|---|
| 1 | Project skeleton, macOS platform guards, architecture rows, `OptInBackends`, README catalog row, package README, slnx, `GpuPublicApiTests` extension, the no-Veldrid pair, AND **three verification tasks**: the **interop spike** (one file touching every Objective-C call this design names, compiled and run against a real device, covering the arm64 caveats in 2.8), the **binding-sufficiency spike** (M-S4: prove the SPIRV-Cross shim can pin every `[[buffer]]`, `[[texture]]` and `[[sampler]]` index), and **measuring and pinning `MTLCompileOptions`** (M-S5: the `languageVersion` macos-26 reports, and `fastMathEnabled`) | `check-doc-versions.sh` fails on a packable project without a catalog row. A wrong ABI assumption in interop is a memory corruption, not a compile error. M-B1 is the design's largest risk and everything downstream of it depends on the spike's answer. `fastMathEnabled` moves every pixel and the language version drifts with the runner image, which is why `macos-26` is already pinned by number |
| 2 | `KhaozEngineMetal.Register()`, the provider, the `IsSupported` functional probe, the `MetalBackendRegistration` seat in `KhaozEngine.TestSupport.Gpu` (not in one test assembly) | A silent fallback would let a soak session measure the incumbent and report it as the native backend. Registration living only in `Render.Tests` threw in all four `MapEditor.Tests` GPU tests on the D3D11 leg's first run |
| 3 | Append `GpuBackendKind.MetalNative = 6`, tokens, `RecognizedTokens`, `GoldenBackendToken` mapping at BOTH sites, the generic bake refusal, `GpuBackendKinds.IsMetal()`, **the two `FrameCap.Resolve` and `DisplaySettings` rows**, and the audit-test extension per 4.2 | `GoldenCompare` lower-cases the kind into the filename at two sites. **This is the program's first append whose FrameCap row is not correct by default**: the native leg would silently lose the software frame cap the Metal leg has, and #380 is a pacing issue |
| 4 | Device and queue creation, `KE_METAL_DEVICE` selection defaulting to the system default with substitutions logged, `KE_METAL_VALIDATION` with the environment-mutation answer row 1's spike gives, the command-buffer error latch with Release-mode checking, `DeviceLiveness`, and the autorelease-pool rule with its architecture test | The incumbent reads neither `status` nor `error` on the completion path. Phase 3's `CheckResult` lesson: a latch built on checks that compile away never fires. Autorelease discipline that is a habit rather than a rule accumulates under a frame loop |
| 5 | **The timeline subsystem, an early prerequisite of 8.** The device `MTLSharedEvent`, `IGpuFence`, `SupportsCompletionFences`, `WaitForIdle` as `waitUntilSignaledValue:` with `DrainCount` and `DrainMs` | The ring's segment recycling reads a completion value, so a ring built before the timeline exists is a silent corruption. That dependency edge is the one phase 2's first spec dropped and phase 3 pulled early for the same reason. `RetireFenceGpuTests` and `Scene3DUnloadDrainTests` must RUN and pass |
| 6 | Resources: formats, buffers (Shared, size rounding reproduced), textures (Private), samplers with the WRAP shared pair, texture views, staging as a Shared `MTLBuffer` with the incumbent's software subresource layout reproduced plus its device-free table test, `Map` and `Unmap` with the read drain, and the device-owned setup command buffer under its own short lock | Every golden reads back through `Map` and `RowPitch`, so a different arithmetic garbles all 36 at once. Reading the shared samplers' address mode off the engine statics cost two goldens on the D3D11 leg. Device-level `UpdateTexture` currently issues a whole queue submit per call |
| 7 | Command list: the command buffer, encoder lifecycle and the one-encoder-at-a-time invariant, `Begin`, `End`, submit under the lock, the narrow `IMtlEncoderSink` over argument-table writes, draws and **encoder boundaries**, the device-free recording-contract test, AND **the `FramesInFlight` constant with its `KE_METAL_FRAMES_IN_FLIGHT` override** (MM4's knob), which row 8's ring READS | Encoder state is per encoder, so a missed re-activation renders with the previous pass's bindings. The knob lands here because this row creates the number and row 8 consumes it. `commit` enqueues, so submit order is only the observable order if commits are serialised |
| 8 | Uniform ring on Shared memory: segments, bind-time base through `setBufferOffset:`, the stride invariant with its device-free test over every shipped set shape, the ring-backed-view invariant, the per-list staging arena for bulk payloads, `UpdateBuffer` routing at both levels including #484's every-segment rule and its pending-patch queue, the backpressure counters, AND **the Metal adapter for the shared ring-test project** (M-P5). **Depends on 5** | The incumbent's device-level `UpdateBuffer` is an ungated memcpy into a buffer the GPU may be reading. #484's silent two-frames-in-three-read-nothing defect, which this ring must not reintroduce. A ring built against a submit receipt recycles a segment the GPU is still reading. "Share the tests" with no adapter on the third side quietly becomes two backends' tests |
| 9 | **Shader path, a prerequisite of 11 rather than a parallel row**: the MSL back-end member on `SpirvCrossCompile`, the SPIRV-Cross shim scoped to MSL, `MslCompilePin`, the resource-binding pin (M-B1), `newLibraryWithData:` plus the `.metallib` cache with header validation, `SpirvLocalSize` unchanged, **the MSL index-table test taken BEFORE the first golden run**, and the architecture test that the backend names the MSL back-end member and no other | Get the numbering wrong and everything compiles and every pixel is wrong. A pipeline cannot be created without a `MTLFunction`, so scheduling this outside the renderable path would block row 11 on a row nothing said row 11 needed. The HLSL emission must NOT move, or D3D11's 36 goldens are in play in a phase that cannot see them |
| 10 | Resource layouts with per-kind declaration-order indices, content-deduplicated layout objects, resource sets, and the pipeline's per-set base-index VECTOR computed once at creation | `GetBufferBase` re-walks the layout array on every single bind today. Without content dedup, M-R9's base comparison is never equal and every pipeline switch invalidates everything |
| 11 | Pipelines: graphics and compute, the render pipeline descriptor from `GpuOutputDescription`, the vertex descriptor with vertex buffers pinned at the TOP of the buffer space (M-B2), depth-stencil state, the over-31-bindings throw, and the `SetPipeline` identity guard. **Depends on 9** | `ResourceBindingModel` exists to manage a collision this row's numbering removes, and getting the pipeline's vertex descriptor and the command list's bind index out of step binds a vertex buffer where a uniform should be. A redundant pipeline bind costs a full re-activation today |
| 12 | Render pass descriptors: deferred begin, **per-attachment clear folding into `loadAction` with `KE_METAL_CLEAR`** (M-A2), explicit `storeAction`, the clear-only-pass flush, the end-before-illegal-command invariant, and the framebuffer-change-guarded viewport and scissor with the unconditional plural setters | The incumbent writes every clear into attachment 0, so `ModelFB`'s normal and linear-depth attachments are never cleared and `ModelRenderer` carries a shipped comment working around it. An unguarded viewport emit silently resets a live scissor, which phase 2's first spec froze the wrong way |
| 13 | Bind flush: two-state per-slot records, **per-(kind, stage) ARRAY calls**, the offsets-only `setBufferOffset:` path, base-vector invalidation on a pipeline switch, and the device-free budget test | One native call per resource per stage is the #418 defect arriving on a second API, and the fork's binding layer does not declare a single array setter |
| 14 | Draw and dispatch paths, vertex and index binding, compute pipelines and dispatch with the serial dispatch type, MSAA resolve with `MaxMsaaSampleCount` **READ OFF the incumbent's own computation and pinned**, mip generation, copies | The 36 goldens, and the compute `[GpuFact]` suite that proves rules 1 and 2 on every backend. Both prior phases had drafts that invented an MSAA formula and then asserted equality with the incumbent, which is the C4 failure phase 2 corrected in flight |
| 15 | Swapchain: `CAMetalLayer` configuration reproduced, unconditional `displaySyncEnabled`, drawable acquire at the present boundary counted into the acquire-wait pair, `maximumDrawableCount`, **the nil-drawable orphan target and the skipped present that still counts into `FramesBegun`**, the separate present command buffer (M-W6), queued resize applied at the boundary, stable framebuffer identity | A nil drawable silently discards a whole frame's recording today. A vsync toggle silently does nothing outside three deprecated-enum values. Zero automated coverage anywhere in the net (MM7) |
| 16 | Capability read and the ZERO-difference parity test with its reflection-completeness check, the `GpuDeviceCounters` fill, `GpuDeviceDiagnostics` with `deviceLossReason`, and **`MetalFrameCapture` taking the native queue pointer instead of reflecting into Veldrid's private field** | Capability drift is silent and golden-visible through `AntiAliasing.ResolveFor`. Phase 3's `SupportsShadowMaps` shipped asking a structurally-false question because the implementer wrote what the member's name suggested. The frame-capture reflection is one Veldrid refactor away from silently not working |
| 17 | **MM6's measurement**: the two-uniform-buffer `[GpuFact]` probes with pixel READBACK assertions, the windowed `SlathRepro` run, and the RESULT recorded in the adjudicated design doc whichever way it goes. **No shader change lands in this row** | The memory records four sessions' worth of open question and one shipped consequence (CPU skinning on every backend for a Metal-only defect). A `GpuFact` that only asserts no-throw is how the all-black splat terrain shipped |
| 18 | **The shared home (#531)**: extract the liveness latch, the counter accumulators, the rate limiter and the shader-cache key discipline into `KhaozEngine.Gpu/Internal/`, refuse the rest in writing, and close #531 with the per-candidate reasoning. **Lands AFTER gate 3** | Extracting while the backend is being written gives a golden failure two candidate causes. #531's own instruction is to re-assess each candidate against three implementations rather than assume the list carries |
| 19 | The `metal-native` CI leg, the `NativeDeviceLifecycle` collection in every `[GpuFact]` assembly, **the seam rule 1 and rule 2 comment's third arm** (10.3), the `Begin` XML doc's Metal sentence, **`ModelRenderer.BeginModelPass`'s stale MRT-clear comment reworded to name the implementation it describes** (7.2), `BackpressureStallCount`'s doc comment, the doc sweep below, the soak build, the five rollout gates, and the `ProbeOS` flip | #423 records the push-triggered D3D11 golden gate degraded for weeks without anyone noticing. A comment that describes a mechanism the native backend does not have decays faster than an unwritten one |

**Order.**

- **1 to 4 are prerequisites** and land first. Row 1's three verification tasks land before anything depends
  on their answers, and **row 1's binding-sufficiency spike is the gate on M-B1**, which is the design's
  largest risk.
- **5 (the timeline) is pulled early**, because 8 reads it.
- **6 follows 4.**
- **7, 9, 10, 11, 12, 13 and 14 are the minimal renderable path.** **8 follows 5** and parallelises with them.
- **9 lands before 11** inside that path, for the same reason 5 lands before 8: a pipeline is created from
  `MTLFunction` handles. Row 9's later half (the index-table test, the cache, the architecture test) can
  follow 11 freely. What may not follow it is library and function creation.
- **10 lands before 11 and 13**, because both read the base vector it computes.
- **15 can start any time after 4 and lands late**, because CI cannot test it and it should not block rows CI
  can.
- **16 and 17 parallelise** after theirs. **17 needs a device and a window**, so it is scheduled where a human
  is available rather than by dependency.
- **18 follows gate 3**, deliberately, and is the only row with a gate rather than a row as its prerequisite.
- **19 is last.**

**KESIZE.** The incumbent's `MTLCommandList.cs` is 1163 lines and `MTLGraphicsDevice.cs` 604, against an
800-line cap, which is a warning about what happens without a file plan. The device, command list, encoder
lifecycle, bind flush, ring, resource factory, pipeline factory, layout cache, swapchain, shader path,
capability read and the interop layer are twelve type groups by construction. **The precedent says this is
achievable rather than hopeful**: `KhaozEngine.Gpu.D3D11` is 17,713 lines across 110 files with its largest at
776 and ZERO entries in `.filesize-baseline`. The interop layer is the one place to watch, and the answer is
one file per Objective-C class rather than one file per API surface, which is what the fork already does. No
baseline edit should be needed, and if one is, that is the user's call.

**Doc sweep this phase owes beyond the guard-checked set.** The root `README.md` catalog row and the package
README are guard-checked and land in row 1. These are not, and land in row 19: `docs/DEPENDENCY-SEAMS.md`'s
out-of-package graphics backends section gains the third instance of the inverted edge, and the per-pipeline
uniform-buffer invariant it carries gains MM6's result. `docs/USING-KHAOZENGINE.md` gains the backend-selection
token and the `KE_METAL_*` variables. `docs/CROSS-PLATFORM.md`'s platform-to-backend mapping gains the native
Metal leg. `GpuInterfaces.cs`'s `Begin` XML doc and its rule 1 and rule 2 comment both gain their Metal arms.
`GpuDeviceCounters.cs`'s `BackpressureStallCount` doc gains the note that its second meaning is Vulkan's.
`ModelRenderer.BeginModelPass`'s comment is reworded. And both prior design docs get a corrected-in-place note
in their own established style: the D3D11 doc's F2 is now scoped rather than pending, and the Vulkan doc's VF1
is answered rather than open.

---

## 19. #420's endpoint, and the closing act

**This is the last backend, so this design owes an answer the other two could defer.**

Phase 2's RO2 says `Direct3D11` stays selectable by token INDEFINITELY. Phase 3's V-RO2 says the same for
`Vulkan`. M-RO2 says the same for `Metal`. **Taken together those three sentences mean Veldrid never leaves
the graph and #420's endpoint is unreachable by construction**, and nobody has written that down.

**M-RO3 resolves it.** The three incumbent legs retire TOGETHER, as a closing act, after all three native
backends have passed their own gate 4 field soak. Not one at a time, because retiring one leg at a time
removes the A/B instrument for that backend while the other two still have theirs, and the whole value of
keeping the token was that a field regression is one environment variable away from a comparison on the same
build.

**What the closing act contains**, so it is sized rather than gestured at:

- Delete `VeldridGpuDevice`, `VeldridGpuCommandList`, `VeldridMap`, `VeldridResources` and the four built-in
  kinds' creation paths.
- Move the SPIR-V front end and the HLSL back end onto the shim this phase builds for MSL (12.2). **The two
  instruments that make that checkable already exist**: `VulkanSpirvByteEqualityTests` plus
  `VulkanSpirvIncumbentParityTests` for the front end, and `D3D11HlslByteEqualityTests` for the back end. That
  is phases 2 and 3 having built the regression net for a change neither of them made.
- Drop `Veldrid` and `Veldrid.SPIRV` from `Directory.Packages.props`, and with them the `Newtonsoft.Json`
  9.0.1 CVE override, which arrives through `Veldrid -> NativeLibraryLoader ->
  Microsoft.Extensions.DependencyModel` and NOT through `Veldrid.SPIRV`, a fact phase 2 had to correct at
  three comment sites.
- Retire together: #424's nested-`Begin` site list, #428's second-recorder guardrail, #429's pre-record
  phase, the CI `libvulkan` symlink step, the Veldrid Vulkan extension list, the D3D11 holed-signature sinks
  (M-S7), the Metal sample-order shader discipline (M-B4), `OpenListTrackingGpuDevice`'s reason for existing,
  and V-S6's shader-shape invariant IF MM6 says it may go.
- Retire the three GUEST golden families into OWNER families, which is the one thing that genuinely changes
  meaning: today each native leg verifies the incumbent's committed references, and afterwards each family has
  exactly one implementation behind it and `CrossBackendGoldenTests` becomes the only cross-check left.

**That last bullet is the one to think hardest about before the closing act**, and it is filed rather than
decided here.

**What this phase leaves ready, and what it deliberately does not.**

- The provider registry, the golden-guest pattern, the capability-parity pattern, the opt-in package shape,
  the append audit test, the counters plumbing and the CI matrix leg are templates proven three times. There
  is no fourth backend, so they stop being templates and start being history.
- The shared home is DECIDED rather than deferred (2.2, M-P4 to M-P6). #531 closes with four extractions and a
  written refusal for the rest.
- `libveldrid-spirv` is still a bundled native per RID. This phase does not reduce the native packaging
  burden, and #420 partly existed to.
- The `KE_D3D11_*`, `KE_VULKAN_*` and `KE_METAL_*` variable families are now three dialects. Phase 3's VF2
  proposed unifying them once three backends exist and the shared subset is observable. It exists now, and
  the subset is small (`FRAMES_IN_FLIGHT`, the device selector, the validation ladder). That is a follow-up,
  not this phase's work.
- **#461's quorum exists** (M-F7). Three of three engine-owned backends honour rule 2 natively and only the
  two Veldrid legs need the drain, which is a better position to specify from and a worse one to keep
  ignoring.

---

## 20. Rejected

| Rejected | Why |
|---|---|
| **An engine-owned CPU op stream in front of `MTLCommandBuffer`** | A second deferral on top of the driver's own, doubling the encode, adding an allocation, and moving driver-side encode inside the submit lock. Phase 2's section 16 said so before either phase-3 draft existed and phase 3 confirmed it (2.3) |
| **Vendoring `Veldrid.MetalBindings`** | Veldrid-derived code inside the backend built to remove Veldrid, invisible to every guard that reads package ids, in a repo whose architecture tests assert no Veldrid edge. It also brings an arm64-dead stret path, block-literal machinery this design replaces with `MTLSharedEvent`, and not one of the array setters M-R6 needs (2.8) |
| **`setVertexBytes` as the uniform path** | The seam hands the backend a buffer range, not bytes, so reaching `setBytes` needs a CPU shadow plus a memcpy per bind per stage where the ring writes an integer. And the 4 KB cap is a content cliff the engine's shipped uniform windows already approach (2.5) |
| **Argument buffers** | No consumer: the hot path is offsets-only rebinds of ONE set, which cost one call each. Reaching them changes the MSL emission for every program and the numbering M-B1 just pinned, and needs an `MTLArgumentEncoder` per layout. Trigger named (8.4) |
| **`MTLHeap` as a suballocator** | Heap resources are hazard-UNTRACKED by default, so a heap costs the entire barrier subsystem this design deletes, in exchange for suballocation a workload that allocates at load time does not need. Linked to M-F5 rather than decided separately (2.4, 8.4) |
| **Reproducing `ResourceBindingModel`** | It exists to manage a collision between vertex buffers and resource buffers in one index space, and pinning vertex buffers at the top of that space removes the collision. Reproducing it means reproducing the arithmetic the one-UBO constraint lives inside (2.7, 8.2) |
| **Reproducing the `colorAttachments[0]` clear collapse** | A shipped renderer reaches it with a three-target framebuffer and carries a comment working around it, and the current behaviour reads two attachments nobody has written, which is the "undefined is not stable" case both prior designs legislate against. Kill switch for the A/B, deadline gate 1 (2.1, 7.2) |
| **Reproducing the vsync feature-set equality test** | An equality test on an enum deprecated since macOS 10.15, whose failure is a vsync toggle that silently does nothing. Reproducing a bug a different machine WOULD reach is not parity, which is V-W2's ruling (M-W2) |
| **Reproducing the silent frame drop on a nil drawable** | It builds a frame's recording and throws it away with nothing logged and nothing counted. V-W4's orphan target costs one image at one extent and buys `SetFramebuffer` being legal at every instant (M-W5, 2.6) |
| **Encoding `presentDrawable:` on the frame's own command buffer** | The idiomatic shape, and it inherits the Vulkan design's own named limitation into the one area with zero automated coverage, to save a cheap object per frame. W1's lesson (2.6, M-W6) |
| **Building a barrier or layout tracker** | Metal tracks hazards automatically for device-allocated resources. #531 predicted this by name. Recorded as a decision so a reader does not conclude it was forgotten (2.4, M-F5) |
| **A deferred-disposal retire list** | An `MTLCommandBuffer` retains what it references until completion, so mid-flight release is already safe. V-F9 has no occupant (M-F8) |
| **Keeping the completion-block fence machinery** | A hand-built block literal, a `libSystem` symbol lookup, a lock plus a dictionary inside a driver callback, a second static AOT path and a `ManualResetEvent` per fence, all replaced by one `MTLSharedEvent` and a non-blocking property read (M-F1, 2.1) |
| **Inventing a `MaxMsaaSampleCount` formula** | Both prior phases had drafts that did, and they differed, and both then asserted equality with the incumbent. Here the incumbent's argument-ignoring shape is correct-by-API, because Metal's only sample-count query takes no format. Read it off and pin it (M-C3, 2.9) |
| **"Fixing" `ResolveTexture` to preserve the MSAA source** | It would match D3D11 and Vulkan and cost bandwidth on an architecture where discarding is the right answer, for a source the engine re-clears next frame. Reproduce and document the divergence (M-C4) |
| **Asserting the staging rows are tightly packed and moving on** | It may well be right and it is the wrong posture, because the goldens depend on the arithmetic and not on the assertion. V-C7's ruling, same evidence (M-C5) |
| **A second `MTLCommandQueue`, a transfer queue, or async compute** | Ordering anything between two queues needs cross-queue event signalling, for a renderer whose uploads are load-time and whose compute is one FFT chain already gated by rule 2. #534's Vulkan-side argument, unchanged by the change of API |
| **Taking #462 for the HLSL target in this phase** | Changing the emitted HLSL changes register numbering and drop-unused behaviour, putting D3D11's 36 goldens and both WARP corruption incidents in play in a phase whose leg does not run them. Phase 3's reason, still binding (12.2) |
| **Shipping a second MSL emission behind a switch** | It would keep a second implementation alive, which is exactly how phase 2's gate 3 ended up blocked behind an unresolved A/B with two drivers shipping. The question is decided at row 1's spike instead (M-S4, 2.7) |
| **Shipping a shader change on MM6's strength** | The one-UBO constraint has cost four separate diagnostic sessions and one fleet-wide CPU-skinning fallback. MM6 authorises FILING the invariant's removal with its own gates, and nothing more (M-B3) |
| **Removing the D3D11 holed-signature sinks, or the sample-order shader discipline** | Both are specific to a leg that ships until the closing act, and a Metal seat is exactly where each looks pointless. Third and second time written down respectively (M-S7, M-B4) |
| **`MTLBinaryArchive` in v1** | A second, newer mechanism for the same win as the `.metallib` cache, with its own serialisation discipline, when the compile time is in the library (12.5) |
| **`storeAction = DontCare` for depth** | Leaves contents undefined, and undefined is not stable across runs, which the goldens require. V-A6's ruling (M-A4) |
| **Adding `SetViewport` to the seam** | 48 `SetFramebuffer` sites and zero viewport sites, unchanged since phase 2 rejected it and phase 3 re-rejected it. If the seam is ever revisited it is a reasonable addition, and this is the last backend, so "when the seam is being revisited anyway" no longer has a scheduled occasion |
| **Extracting the recorder, the emitter, the flush schedule or the ring's mechanism into a shared home** | Three flushes with three arities and three invalidation rules that exist for three different reasons. Two backends agreeing on a dirty-state COUNT while disagreeing on why is the shape that produces an abstraction nobody can change. Four things ARE extracted and the refusal is written per candidate (2.2) |
| **Deferring #531 a second time** | Its trigger is this phase landing, and a follow-up that is deferred at its own trigger is a follow-up nobody will ever action. It closes here with an answer, either way |
| **A new `KhaozEngine.Gpu.Backend` package for the extracted machinery** | Needs public API to serve consumers we all own, plus a catalog row, a package README and a third link in every chain. Phase 3 rejected it at two implementations and the argument is unchanged at three (2.2) |
| **Owning a `metal-native` golden family** | A guest verifies the incumbent's committed references on the same device, which is a second implementation checking the first. A family of its own would check nothing, and on THIS family it would also fork the fleet's cross-backend reference in two (M-I2) |
| **Flipping the headless default before gate 4** | Would silently reduce the incumbent's coverage during exactly the window when both legs must stay green. RO3's ruling for the third time |

---

## 21. Follow-ups this design knowingly leaves open

Filed as issues when the adjudicated spec lands, not discovered later.

- **MF1.** The program's CLOSING ACT: retire all three Veldrid legs together, move the SPIR-V front end and the
  HLSL back end onto the shim, drop `Veldrid` and `Veldrid.SPIRV` and the `Newtonsoft.Json` override, and
  decide what the three golden families mean once each has one implementation behind it (section 19).
  Triggered by all three native backends passing gate 4.
- **MF2.** The shader-shape invariant's removal, IF MM6 passes. Its own work with its own gates, never a side
  effect of this phase (M-B3, row 17).
- **MF3.** `StorageModeManaged` with `didModifyRange:` for discrete Intel Mac GPUs, triggered by a consumer
  report from one (M-M2).
- **MF4.** Prefer a discrete GPU by default on a dual-GPU Mac, with its own change note, once the soak's A/B
  no longer needs one variable (M-N1, phase 3's VF3 with a different noun).
- **MF5.** Argument buffers, reopened by a consumer binding many distinct material sets per frame, which today
  means a texture-array atlas the splat terrain cannot express (8.4). Same trigger as phase 3's VF5, which is
  itself evidence the trigger is about the renderer rather than about the API.
- **MF6.** `MTLBinaryArchive` for compiled pipeline states, once the `.metallib` cache has a measurement
  showing where the remaining launch cost is (12.5).
- **MF7.** `preserveInvariance` on the MSL compile options, triggered by Z-fighting between the depth prepass
  and a later pass (12.4).
- **MF8.** Unify the three `KE_*` variable families into a `KE_GPU_*` core plus per-backend extras. Phase 3's
  VF2, whose "once three backends exist" trigger has now fired (section 19).
- **MF9.** #461's automatic-hazard seam capability, which now has three of three engine-owned backends able to
  answer yes and only the Veldrid legs unable (M-F7). Phase 3's VF10 with the quorum it was waiting for.
- **MF10.** Measure what automatic hazard tracking costs in lost encoder overlap, and re-argue the heap
  decision in the same change if it is material (MM5, 8.4).
- **MF11.** Record the Metal field baseline (frame time, encode times, drawable wait) on a Mac BEFORE gate
  4's native session, because no published Metal field numbers exist anywhere and a gate stated against a
  number nobody has measured cannot be read (section 17).
- **MF12.** #538 (a tiler head wanting attachment lifetimes) is CLOSED by this phase rather than carried:
  Metal's render pass descriptor carries exactly the per-attachment load and store actions the Vulkan issue
  says dynamic rendering does not, and Apple Silicon is a tile-based architecture, so the engine now has a
  shipped tiler backend that expresses them. The issue's Vulkan-side question (whether a Vulkan MOBILE head
  would want a render-pass path) is untouched and stays open on its own terms.
- **MF13.** #539 (Vulkan on macOS through MoltenVK) closes as not planned once this backend passes gate 4,
  with the reason its own body already states: Metal is the macOS story and MoltenVK would be a second, worse
  macOS path maintained beside the one the fleet actually built.
- **MF14.** #534 (a transfer queue and async compute) gains a Metal-side note rather than a Metal-side issue:
  the argument is the same on both APIs and the consumer that would justify it is the same FFT ocean, so the
  Vulkan issue carries it and this design records that it was considered and declined for the same reasons
  (section 20).
