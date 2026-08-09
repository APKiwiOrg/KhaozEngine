# KhaozEngine.Gpu.Metal: native Metal backend design (2026-08-09)

**Status: spec complete, implementation not started.** Phase 4 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), specified by
[#566](https://github.com/APKiwiOrg/KhaozEngine/issues/566), following the shipped phase 2
(`docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md`) and phase 3
(`docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md`). This document is the phase 4 deliverable.
Implementation is a numbered issue list in section 18 and none of it has been written. Nothing here has run on
a device. Section 16 lists every decision that rests on reasoning rather than measurement, each with the
measurement that settles it, the switch that turns it off, the criterion that retires the switch, and a
deadline.

Written against engine `17.34.0` (`Directory.Build.props`). The incumbent this design replaces and must reach
parity with is **`Veldrid 4.9.103`**, the vendored fork package `Directory.Packages.props` pins.

**Provenance.** Two complete competing drafts were written independently from opposite priors: a reuse-first
draft (the shapes phases 2 and 3 proved are the default, every departure earned) and a Metal-idiomatic draft
(design from Metal's own model outward, every inheritance argued). This document adjudicates them. Where a
draft won outright it is recorded in the Origin column. Where the adjudication produced something neither
proposed, the column says so and section 2 argues it. Section 20 lists what was rejected from both.

**Section 2 is the one to read**, and 2.1 first, because it corrects the evidence base. Eleven things were
contested. Four are ruled AGAINST BOTH drafts, and three of those four are corrections of a factual claim a
draft made about code in this repository or in the fork. Two of the biggest rulings turn on facts neither
draft checked: the reuse-first draft's central claim that the MRT clear defect is unreachable by shipped code
is FALSE (`KhaozEngine.Render3D/Rendering/ModelRenderer.cs` reaches it and carries a comment about it), and
the Metal-idiomatic draft's central mechanism for owning the MSL numbering does not exist
(`libveldrid-spirv` exports three C entry points and none of them can pin a resource index).

**Every citation into the incumbent names a MEMBER, never a line number** (V-I6). Phase 2's cited line numbers
went stale inside one release and `GpuBackendKindAppendAuditTests` records it.

---

## 1. Decisions

| # | Area | Decision | Origin |
|---|---|---|---|
| M-P1 | Package | New `KhaozEngine.Gpu.Metal`, opt-in, outside every umbrella, `net10.0` (NOT `net10.0-macos`) with `[SupportedOSPlatformGuard("macos")]` entry points and `NoInlining` bodies behind `OperatingSystem.IsMacOS()`. That is P1's apparatus, which V-P1 did not need because Vulkan is not an OS-specific API and Metal is. The assembly compiles and its device-free tests run on the Linux `ci.yml` leg and both Windows legs | Both, converged |
| M-P2 | Binding | An ENGINE-OWNED Objective-C interop layer, `[LibraryImport]` with blittable-only signatures over `objc_msgSend`, source-generated with no marshalling stub (which is also what the SYSLIB1054 analyzer requires under this repo's warnings-as-errors rule). No maintained managed Metal binding exists to take, so the Vortice and Silk.NET precedents have nothing to point at, and vendoring `Veldrid.MetalBindings` is rejected by name. Reading it as the reference implementation is a different act and is what this document does throughout | Both, B's mechanism |
| M-P3 | Layering | References `KhaozEngine.Gpu` and `KhaozEngine.Diagnostics` only, with NO third-party package at all, which makes it the first backend whose `ArchitectureTests.ThirdPartyHomes` row is empty. The no-Veldrid pair (csproj read plus IL reference walk) is extended in both forms, and the walk is the load-bearing one | Both, converged |
| M-P4 | Shared home | **#531's extraction is TAKEN and it is FIVE things**: the `DeviceLiveness` latch, the counter accumulators, the diagnostic rate limiter, the shader-cache KEY and file discipline, and the completion TIMELINE's bookkeeping. Each is three implementations writing the same thing, and the fifth is only extractable because M-F1 makes Metal's timeline the same primitive the other two have. Section 2.8 walks every candidate | Judge, merged from A's list and B's |
| M-P5 | Shared home | The ring's CODE does not extract and its POLICY already did, into tests. `KhaozEngine.TestSupport.Gpu`'s shared ring semantic tests gain a THIRD adapter, so section 9.4's inventory becomes an assertion about three implementations. The record-then-flush SCHEDULE, the dirty MODEL and the generic EMITTER interface are all refused in writing, per candidate | B, A's reasons on the schedule |
| M-P6 | Shared home | The extraction lands AFTER rollout gate 3, never interleaved with the backend, and every candidate carries a written exit: a candidate the third implementation does not fit closes as NOT PLANNED with that reason. Refactoring two shipped backends inside the phase whose gate is a golden family is how a golden failure stops being attributable | B's trigger, A's exit |
| M-I1 | Identity | Append `GpuBackendKind.MetalNative = 6` with an explicit ordinal and the append-only comment. New tokens `metal-native` and `mtl-native`, added to `RecognizedTokens` | Both, converged |
| M-I2 | Identity | The append audit is a TEST (`GpuBackendKindAppendAuditTests` and its Vulkan sibling), walked a third time in section 4.2. **Three sites answer differently from the Vulkan append and all three degrade SILENTLY**: `FrameCap.Resolve`, `DisplaySettings.RequiresFrameCapWarning`, and `VeldridGpuDevice`'s Metal frame-capture gate | Both, A's third site |
| M-I3 | Goldens | GUEST in the committed `metal` family through `GoldenBackendToken`, which throws on an unmapped kind and is pinned by the audit test. `BakeRefusal` derives guest-ness generically already. **And the asymmetry is named: the `metal` family is the FLEET's cross-backend reference**, so a metal-family disagreement is a fleet event rather than a leg event | B's asymmetry |
| M-I4 | Identity | A missing provider registration THROWS and never falls back. An incapable machine is answered by `IsSupported()`'s functional probe and reported through `AfterFallback`. `PreflightProvider` already fixes the order | Both, converged |
| M-I5 | Identity | Add `GpuBackendKinds.IsMetal()` beside `IsDirect3D11()` and `IsVulkan()`. It has real readers on day one, unlike `IsVulkan()`, and two of them are the frame-cap pair whose arm this phase cannot decide by reasoning (2.9) | Both, reason corrected |
| M-N1 | Device | `MTLCreateSystemDefaultDevice()` as the DEFAULT, reproducing the incumbent, with `KE_METAL_DEVICE=<index>|<substring>|discrete|integrated|low-power` explicit selection over `MTLCopyAllDevices()` and any substitution LOGGED. Phase 3's 2.9 argument is unchanged: changing which GPU the engine runs on is a user-visible change unrelated to swapping the backend, and it breaks `DeviceName` parity under a zero-difference bar | Both, converged |
| M-N2 | Queue | ONE `MTLCommandQueue`, created once, documented thread-safe. Command buffers execute in ENQUEUE order and `commit` enqueues if the buffer was not already enqueued, so committing under the submit lock makes SUBMIT ORDER the observable order by construction. No second queue and no async compute: #534's argument transfers with the FFT ocean as the same named consumer | Both, B's enqueue reasoning |
| M-N3 | Device | New capability questions use `supportsFamily:` (`MTLGPUFamilyApple*`, `Mac2`, `Metal3`). The incumbent's `supportsFeatureSet:` enumeration is not reproduced for new questions: it has been deprecated since macOS 10.15 and `MTLFeatureSupport.MaxFeatureSet` feeds two fragile reads. **PARITY surfaces are the exception** and reproduce the incumbent's own question (M-C3, section 14) | B |
| M-N4 | Device | `IsSupported()` is a functional probe with real content: a device exists and reports a name, `supportsFamily:` answers at or above the floor, the device's minimum constant-buffer offset alignment is at or below 256 (which M-M3's stride depends on), and `supportsTextureSampleCount:` answers for at least 1. It must never throw. The incumbent's `MTLGraphicsDevice.GetIsSupported` is the FLOOR of that probe rather than the whole of it | A |
| M-N5 | Lifecycle | `NSAutoreleasePool` discipline is a stated rule with an architecture test rather than a habit: every public entry point that can create an autoreleased object wraps its body. The incumbent wraps four sites and not others, which is the shape that accumulates under a frame loop | B |
| M-R1 | Recording | `MetalCommandList` implements `IGpuCommandList` and encodes DIRECTLY into a per-list `MTLCommandBuffer` through the three encoder kinds at RECORD TIME. No engine-owned op stream, no second driver, no `KE_METAL_RECORD`, no M1 analogue. Metal's encoders ARE the deferred command buffer phase 2 had to build in managed memory | Both, converged |
| M-R2 | Recording | **NO command-buffer ring, and V-R2 does not port.** An `MTLCommandBuffer` is single-use: there is no reset, no pool object and no allocator to choose between. `Begin()` takes a fresh buffer from the queue. The `FramesInFlight` depth survives and lives on the uniform ring's acquire ALONE, so `BackpressureStallCount` means one thing here where it means two on Vulkan | Both, converged |
| M-R3 | Recording | N lists record concurrently and genuinely, because each owns its command buffer and its encoders and this design has no shared record-time state at all (no layout tracker, no barrier tracker, no device state cache). The PORTABLE seam contract is unchanged at one open recording per device, and `IGpuCommandList.Begin`'s XML doc gains a Metal sentence naming the property that makes it true | Both, converged |
| M-R4 | Recording | **Encoder state is PER ENCODER, and that is the API's rule rather than a design choice.** Ending a render encoder discards the bound pipeline, every argument-table entry, the viewport, the scissor, AND every vertex stream. The incumbent's `EndCurrentRenderPass` forgets the last of those and is saved only by a second defect, so this is stated explicitly and tested (2.1) | Both, A's second-order finding |
| M-R5 | Recording | TWO-state per-slot dirty records, not three. The reason differs from both neighbours: `DynamicOffsetsOnly` exists on D3D11 to skip textures and samplers, and here the offsets-only path is a DIFFERENT CALL (`setVertexBufferOffset:atIndex:`) rather than a cheaper variant of the same one | Both, B's reason |
| M-R6 | Recording | **A full activation emits ONE ARRAY CALL per (kind, stage)**: `setVertexBuffers:offsets:withRange:`, `setFragmentTextures:withRange:`, `setFragmentSamplerStates:withRange:` and their siblings. The incumbent emits one call per element per stage, which is the #418 fan-out defect on a second API, and the fork's `MTLRenderCommandEncoder` binding does not declare a single array setter | B |
| M-R7 | Recording | The offsets-only rebind is ONE `setVertexBufferOffset:atIndex:` or `setFragmentBufferOffset:atIndex:` per visible stage. No buffer rebind and no argument-table write, an integer into the encoder's stream. This is the Metal occupant of `*SetConstantBuffers1`'s first-constant and of `pDynamicOffsets`, and it is cheaper than both | B |
| M-R8 | Recording | `SetPipeline` gains an IDENTITY GUARD the incumbent lacks. `MTLCommandList.SetPipelineCore` unconditionally sets the changed flag and clears the whole active-set array, so a redundant pipeline bind costs a five-call state re-emit plus a full re-activation of every set. **Verified**, and it is one of two places a draft asserted the incumbent already had the guard | B, A's claim corrected |
| M-R9 | Recording | A pipeline switch invalidates a recorded slot only where the incoming program's INDEX TABLE maps that slot's elements to different indices than the outgoing one did. Metal's argument tables are absolute and per encoder, so a bound resource survives a pipeline switch. Content-deduplicating the per-program index table makes the comparison a handle compare, so two pipelines sharing a table invalidate nothing. This is the Metal occupant of R5's clause 5 and V-R6, existing for a third reason again (absolute index arithmetic, not register numbering and not binding validity) | Judge, B's shape over A's index source |
| M-A1 | Passes | `MTLRenderPassDescriptor` per pass with NATIVE `loadAction` and `storeAction`, and the begin DEFERRED to the first draw so a clear recorded after `SetFramebuffer` folds into `loadAction = Clear`. On Vulkan that was an adaptation of a general API. Here it is the API's own model | Both, converged |
| M-A2 | Passes | **PER-ATTACHMENT clears. The incumbent's `colorAttachments[0]` collapse is a DEFECT, it is REACHED by shipped engine code, and it is not reproduced.** `KhaozEngine.Render3D/Rendering/ModelRenderer.BeginModelPass` clears three attachments of `ModelFB` and carries a comment describing the collapse. Kill switch `KE_METAL_CLEAR=attachment0` for the A/B, removed at gate 1 | B, A's premise refuted |
| M-A3 | Passes | The clear-only case is reproduced DELIBERATELY: framebuffer plus clear plus `End` with no draw must still clear. The incumbent forces it at two sites (`SetFramebufferCore` and `End`) and a golden depends on it | Both, converged |
| M-A4 | Passes | `storeAction = Store` set EXPLICITLY for colour and depth rather than left to the descriptor default. NOT `DontCare`: undefined contents are not stable across runs and the goldens require stability. **The tiler upside is larger here than the Vulkan equivalent and is RECORDED with named consumers rather than taken** (2.5) | Both, A's magnitude |
| M-A5 | Passes | Any command illegal inside a render encoder (dispatch, blit, copy, mip generation, resolve) ENDS the encoder first. One invariant, one helper, one device-free test. On Vulkan that was a chosen discipline and here it is the API's rule | Both, converged |
| M-A6 | Viewport | `SetFramebuffer` emits the full viewport and the full scissor ON A FRAMEBUFFER CHANGE ONLY, reproducing W6's identity guard exactly, and the scissor flush stays gated on the bound pipeline's `ScissorTestEnabled` the way the incumbent gates it, because that is the seam's own rasterizer state and D3D11 honours it too. `ClipSpaceYInverted` is false with no viewport trick at all | Both, converged |
| M-A7 | Viewport | The plural `setViewports:count:` and `setScissorRects:count:` forms are used unconditionally rather than behind the incumbent's `macOS_GPUFamily1_v3` feature-set test, at a count of 1, because the seam has no multi-viewport concept. One code path and no deprecated-enum read on the hot path | B |
| M-B1 | Binding | **THE BINDING TABLE IS READ OFF THE EMITTED MSL, per program and per stage, and NOT counted on the CPU.** At shader-set creation the backend parses each stage's entry-point signature for its `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]` attributes and its entry-point NAME. Resource-set activation binds through that table. Owning the numbering by pinning it into the emission is the better design and is NOT AVAILABLE: section 2.2 establishes that `libveldrid-spirv` exports no entry point that can pin a resource index | A, B's route refuted on a fact |
| M-B2 | Binding | **Vertex STREAM buffers are pinned at the TOP of the buffer space (30 downward), not at `NonVertexBufferCount + i`.** `ResourceBindingModel.Improved`'s arithmetic assumes the CPU knows where the resource buffers landed, which is exactly the assumption M-B1 removes, so reproducing it would be unsound. Top-pinning cannot collide with any MSL index growing from 0, is invisible to the emitted MSL and therefore to every golden, and a pipeline declaring more than 31 combined bindings on one stage throws at creation with a named exception | Judge, B's scheme forced by A's M-B1 |
| M-B3 | Binding | `ResourceBindingModel` leaves the engine's vocabulary entirely. Its only reader in the vendored fork is the Metal backend, it exists to manage a collision M-B2 removes, and nothing in `KhaozEngine.Gpu.Metal` names the concept | Both, converged |
| M-B4 | Binding | The one-uniform-buffer-per-pipeline shader invariant **STAYS IN FORCE**, and this phase does not lift it, weaken it, or ship a shader change on the strength of a hypothesis about it. What this phase adds is a MEASUREMENT (MM6) whose result is recorded either way, and a named seat from which a later change could lift it. Section 2.3 | Judge, B's discipline, A's over-claim rejected |
| M-B5 | Binding | The "sample all textures up front in binding order" shader discipline STAYS, and so do S5's holed-signature sinks, because the Veldrid Metal and Veldrid D3D11 legs ship alongside until the closing act. Both are written down because a Metal seat is exactly where each looks pointless | Both, converged |
| M-B6 | Binding | NO argument buffers, NO `MTLHeap`, NO indirect command buffers, NO bindless, NO tile shading, and NO `setBytes` as the uniform path. Section 8.4 argues each decline against the idiomatic grain and names the trigger that reopens it | Both, converged |
| M-M1 | Memory | NO allocator and NO `MTLHeap`. Metal owns device memory, `newBufferWithLength:options:` IS the allocation, and a heap's resources are hazard-UNTRACKED by default, which would trade away the automatic tracking this design rests on for a suballocator the workload has no use for. V-M1 through V-M4 have no analogue | Both, converged |
| M-M2 | Memory | Every buffer is `MTLStorageModeShared`, reproducing the incumbent, so on unified memory every buffer is directly CPU-writable with no staging path. Textures are `Private` and staging textures are a `Shared` buffer. `StorageModeManaged` with `didModifyRange:` for a discrete Intel Mac, and `Private` plus a blit for load-time static buffers, are follow-ups with named triggers | Both, converged |
| M-M3 | Uniforms | Every `UniformBuffer`-usage buffer is ONE `MTLBuffer` of `stride * FramesInFlight` in Shared memory, persistently CPU-visible through `contents()`, where `stride = align(size, 256)`. The device's own minimum is READ and ASSERTED at or below 256 by M-N4's probe. The incumbent reports 16 on macOS (`GetUniformBufferMinOffsetAlignmentCore`), and flooring at 256 is what makes one number govern all three rings. `FramesInFlight = 3` | Both, converged |
| M-M4 | Uniforms | The bind-time base rides `setVertexBufferOffset:` / `setFragmentBufferOffset:` (M-R7). There is no descriptor range to overrun and no 16-constant count to round, so V-M6's VUID invariant and D3D11's constant-count invariant both collapse to `frameBase + rangeOffset + callerDynamicOffset + size <= (frame + 1) * stride`, asserted device-free over every shipped set shape | B |
| M-M5 | Uniforms | #484's every-segment off-timeline rule is adopted WHOLESALE and unmodified, including the pending-patch queue and the never-wait property. It cost a consumer defect to learn once and this is its third implementation | Both, converged |
| M-M6 | Uniforms | U3's two creation-time invariants adopted VERBATIM: only `UniformBuffer` usage is ring-backed, and a ring-backed buffer that also declares a structured binding throws at CREATION as a documented **backend-divergent creation failure**, recorded in the package README rather than discovered by a consumer | Both, converged |
| M-M7 | Uniforms | **The ring is a CORRECTNESS change here and not only a cost change.** `MTLGraphicsDevice.UpdateBufferCore` is an ungated `memcpy` into `contents()` of a buffer a submitted command buffer may be reading, with no fence, no frame index and no diagnostic. Metal renames nothing under a write, so that is a plain data race, and the ring's completion gate is what closes it | Both, converged |
| M-M8 | Uploads | Record-time bulk payloads (vertex, index, texture) take a per-list Shared STAGING ARENA, sub-allocated and recycled at slot retirement, and one blit copy. The incumbent allocates a whole `MTLBuffer` per call and releases it immediately, and its own TODO says the buffers should be pooled | Both, converged |
| M-M9 | Uploads | Texture CREATION issues no command buffer, so V-M10 has no occupant. Device-level `UpdateTexture` on a NON-staging texture does create a command list and a whole queue submit today, and that moves onto a device-owned setup command buffer under a short setup lock, flushed lazily at the next submit OR at any device-level read | B |
| M-M10 | Views | Every texture view a resource set can name is created at RESOURCE creation from the declared usage bits, following the incumbent's rule that a view object exists only when the description narrows the target. NO view factory is reachable from the recording type, asserted by an architecture test over the type graph, so a draw-time view is a compile error | Both, converged |
| M-F1 | Timeline | ONE device-wide `MTLSharedEvent` as the monotonic timeline. Every submit encodes `encodeSignalEvent:value:` with the next value before committing, `IGpuFence` holds a target, and `Signaled` is a non-blocking `signaledValue >= target` read. That is the same primitive the other two backends have, which is what makes M-P4's fifth extraction possible | B |
| M-F2 | Timeline | **The completion HANDLER survives, and its only job is M-G4's error latch.** A shared event replaces the fence dictionary, the `ManualResetEvent` per fence, the lock inside a driver callback and the AOT static-callback path, but it does NOT remove the need to read `MTLCommandBuffer.status` and `.error` at completion, which M-G4 requires. The handler therefore carries no ordering responsibility at all, which is the answer to the observation that completion callbacks arrive on an arbitrary internal thread in no guaranteed order | Judge, against both |
| M-F3 | Timeline | The block is `[UnmanagedCallersOnly]`, with no delegate, no `Marshal.GetFunctionPointerForDelegate` and no process-global dictionary. If row 1's spike finds the block layout does not work that way, the named fallback is the incumbent's delegate-and-dictionary shape, and the design loses AOT-cleanliness on the completion path and says so | A |
| M-F4 | Timeline | The monotonicity makes the seam's documented fence ordering a THEOREM rather than a convention, which is V-F2's argument reaching the same conclusion because it is the same primitive under a different name. `SupportsCompletionFences = true` is PARITY, which `VeldridMap` already reports for Metal, so nobody should look for phase 2's C5 win here | Both, converged |
| M-F5 | Timeline | `WaitForIdle` is `waitUntilSignaledValue:timeoutMS:` on the last submitted value, counted into the existing `DrainCount` and `DrainMs`. Not `waitUntilCompleted` on a retained last command buffer, which is what the incumbent does and which needs the buffer kept alive under a lock to be read. There is no C6-style bet: the incumbent's drain is already real | B |
| M-H1 | Hazards | **AUTOMATIC HAZARD TRACKING IS KEPT.** Every resource is allocated tracked, which is Metal's default and the incumbent's configuration. Untracked resources plus explicit `MTLFence` and `memoryBarrierWithScope` are DECLINED, and so is every route that would import untracked resources through a side door (`MTLHeap` is the one that matters) | Both, converged |
| M-H2 | Hazards | **The decline is CONDITIONAL and the condition is the inverse of V-M1's.** V-M1 declined VMA on the condition that a synchronisation validator EXISTS to catch what it gives up. Metal has no synchronisation validator: `MTL_DEBUG_LAYER` is API validation and `MTL_SHADER_VALIDATION` is in-shader bounds checking, and neither tracks read-after-write hazards across encoders. So an untracked backend would carry that hazard class with no detector anywhere in the net, on the ONE leg whose real hardware really reorders. Reopened by a named instrument existing, not by a preference | A, condition sharpened |
| M-H3 | Hazards | **NO barrier tracker, NO layout tracker, NO resting layouts, NO transition table, NO retire list.** V-F6 through V-F9 and C1's SRV-versus-UAV auto-unbind all have no occupant, which #531 predicted by name. Stated as decisions so a reader does not go looking for a tracker and conclude it was forgotten. An `MTLCommandBuffer` retains every resource it references until completion, which is what makes mid-flight release safe, and `commandBufferWithUnretainedReferences` is never used, so a future reader reaching for it has to bring a retire list with it | Both, converged |
| M-H4 | Hazards | Seam rule 1 needs no code (the encoder boundary is the dependency, which the seam's own comment already names for Metal). Seam rule 2 is honoured AS WRITTEN with no seam member added, and this backend additionally satisfies it natively through the serial dispatch type. **That makes three of three engine-owned backends able to answer yes, which is #461's quorum.** Evidence, not a contract change | Both, converged |
| M-W1 | Swapchain | `CAMetalLayer` configuration reproduced from the incumbent field for field, because it is visible only to a human: the layer adopt-or-create dance on the host view, `device`, `pixelFormat` from the sRGB request, `framebufferOnly = true`, and `drawableSize`. W1's lesson binds hardest where nothing in CI runs, and the Metal golden suite is headless | Both, converged |
| M-W2 | Swapchain | **`displaySyncEnabled` is set UNCONDITIONALLY.** The incumbent's `MTLSwapchain.SetSyncToVerticalBlank` writes it only when `MetalFeatures.MaxFeatureSet` equals one of three values of an enum deprecated since macOS 10.15, so on a machine outside that set a vsync toggle silently does nothing. Reproducing a fragility whose failure is silent is not parity | B |
| M-W3 | Swapchain | **The two frame-cap sites route through `IsMetal()` and their arm is decided by MEASUREMENT at gate 5, not by assumption.** `FrameCap.Resolve` and `DisplaySettings.RequiresFrameCapWarning` both apply a software cap only on Metal plus vsync, and both carry a comment saying the equality is deliberate because the question is whether the backend's present throttles the CPU. M-W2 plus `maximumDrawableCount` may change that answer for the native backend. Defaulting into the Metal arm preserves today's behaviour in both directions, and gate 5 reads which arm is right | Judge, against both |
| M-W4 | Swapchain | `nextDrawable` is taken AT THE PRESENT BOUNDARY for the next frame, keeping the incumbent's timing, which is the good half of it. It BLOCKS and Metal offers no semaphore alternative, so the stall is not removable and is instead MEASURED into `AcquireWaitCount` and `AcquireWaitMs`, the pair phase 3 already appended to `GpuDeviceCounters`. `maximumDrawableCount` is set to `FramesInFlight`. No seam addition at all | Both, converged |
| M-W5 | Swapchain | A nil drawable binds a device-owned ORPHAN TARGET, records and submits normally, and skips only its present, counting into `FramesBegun`. The incumbent's `MTLSwapchainFramebuffer.IsRenderable` goes false and `PreDrawCommand` returns false for every draw, so a whole frame's recording is built and thrown away with nothing logged and nothing counted | Both, converged |
| M-W6 | Swapchain | `presentDrawable:` stays on its OWN command buffer, exactly as the incumbent does it. **This is a place where the reuse prior wins**, and 2.11 argues it: the seam's `Present()` is a separate call from `Submit()`, the win is one cheap object per frame, and the alternative inherits the Vulkan design's own named limitation into the one area with zero automated coverage | Both, converged |
| M-W7 | Swapchain | `IGpuFramebuffer` wrapper identity is STABLE across resize BY CONSTRUCTION, because the colour attachment is the drawable's texture resolved at descriptor-build time. Resize queues and applies at the present boundary on the submit thread after a drain, where the boundary provably owns the queue. Metal needs no swapchain recreation, only a `drawableSize` write and a depth rebuild, so the seam's existing no-recreate wording already describes it | Both, converged |
| M-W8 | Threading | No frame-long lock. Recording is lock-free and per list. One `_submitLock` covers commit, present and the resize apply, with two short locks beside it (the setup command buffer's and the ring's), taken in that order and never the reverse. Creation is otherwise free-threaded. `GpuDeviceContext._lifecycleGate` is unchanged | Both, converged |
| M-S1 | Shaders | GLSL 450 stays the single source. `SpirvCrossCompile`'s BACK end grows `VertexFragmentToMsl` and `ComputeToMsl` beside the HLSL pair, which is exactly the split V-S3 paid for, and the front end is untouched. The signatures stay Veldrid-free and the architecture test gains its third arm | Both, converged |
| M-S2 | Shaders | **#462 is NOT taken in this phase, in either target.** A shim over `libveldrid-spirv` cannot pin an MSL resource index, because the library exports exactly `CompileGlslToSpirv`, `CrossCompile` and `FreeResult` and none of them carries a binding table (2.2). So the MSL target runs through the managed `Veldrid.SPIRV` call under an `MslCrossCompilePin`, exactly as the HLSL target does, and `Veldrid.SPIRV` stays in the graph until the closing act | Judge, against B on a verified fact |
| M-S3 | Shaders | `MslCrossCompilePin` beside `HlslCrossCompilePin`, values stated as constants with their citation and an `Identity` derived FROM them, plus a per-program byte-equality DRIFT test AND `MetalMslIncumbentParityTests`, which asserts on every leg that the two independently maintained option sets still emit the same bytes. Byte-identical MSL is what licenses "no rebake", and the drift test alone does not establish it: a wrong emission baked once passes forever | A |
| M-S4 | Shaders | The one-off in-process MSL parity measurement against the incumbent's own path is TAKEN AND RECORDED in this document before the first golden run. The incumbent hands GLSL to `CreateFromSpirv` with `new CrossCompileOptions()`, so `MslCrossCompilePin` must state the library defaults or the measurement fails and the pin is what moves | A |
| M-S5 | Shaders | The entry-point NAME is read out of the emitted MSL rather than assumed, because SPIRV-Cross renames `main` and the incumbent looks a function up by a name Veldrid supplies from a layer this backend does not have. It is the same parse M-B1 already runs | A |
| M-S6 | Shaders | `MTLCompileOptions` are PINNED in the same constant family, including `fastMathEnabled` and `languageVersion`, both MEASURED on `macos-26` by row 1 so the pin is a no-op on the day it lands. The incumbent passes a default-constructed options object, so the committed metal goldens were baked under whatever the OS default was. Fast math moves floating-point results and the language version drifts with the runner image, which is the class of hazard `macos-26` is already pinned by number for | B |
| M-S7 | Shaders | A `.metallib` disk cache through `newLibraryWithData:`, keyed on the pin identity, the SPIR-V hash, the device's registry identity and the engine version, header-validated and best-effort so any read or write failure is a silent discard. `MTLBinaryArchive` is DECLINED for v1 as a second, newer mechanism for the same win, when the compile time is in the library | B |
| M-C1 | Compute | Compute and graphics bindings tracked separately with separate dirty arrays and separate bound-pipeline slots, as the seam requires. `SetComputePipeline` and `Dispatch` end any open render encoder first. The compute encoder is created with the default SERIAL dispatch type, which is what makes M-H4 true, and `SpirvLocalSize`'s hand-parse is unchanged | Both, converged |
| M-C2 | Compute | Storage buffers are plain buffers at a `[[buffer(n)]]` index. C2's RAW byte-address forcing was an HLSL artefact with no Metal analogue, and neither does D3D11's SRV-versus-UAV auto-unbind | Both, converged |
| M-C3 | MSAA | `MaxMsaaSampleCount` is READ OFF the incumbent's own `GetSampleCountLimit` and reproduced, INCLUDING the fact that it ignores both of its arguments, and **the reproduction is recorded as CORRECT rather than as a bug carried for parity**: `supportsTextureSampleCount:` is Metal's only sample-count query and it takes no format. A native backend that "improved" it by asking per format would be inventing a question the API cannot answer | B, A's follow-up refused |
| M-C4 | MSAA | `ResolveTexture` is an empty render encoder with `loadAction = Load` and `storeAction = MultisampleResolve`, reproducing the incumbent INCLUDING its documented discard of the source. The divergence from `ResolveSubresource` and `vkCmdResolveImage` goes in the package README rather than being silently inherited. An out-of-range requested sample count THROWS at texture creation rather than silently falling to 1, which is C4's departure for C4's reason | Both, B's README task |
| M-C5 | Staging | Staging textures are `MTLBuffer`-backed with the incumbent's SOFTWARE subresource layout reproduced byte for byte, plus a device-free table test against a checked-in table taken from the incumbent's own arithmetic. Every golden reads back through `Map` and `MappedData.RowPitch`, so a different arithmetic garbles all 36 at once | Both, converged |
| M-C6 | Staging | `Map(staging, Read)` WAITS on the timeline before returning the pointer, counted as a drain. The incumbent's `MapCore` returns `contents()` with no wait, which is correct today only because every engine caller drains first, so the seam's guarantee currently rests on a convention rather than on the backend | Both, converged |
| M-G1 | Capabilities | Field-by-field parity with the incumbent and ZERO permitted differences, plus the reflection-completeness check that the comparison covers every member of `GpuCapabilities`. The incumbent Metal backend has no capability defect to correct, which is why the bar is phase 3's rather than phase 2's | Both, converged |
| M-G2 | Diagnostics | `KE_METAL_DEVICE` per M-N1. `softwareAdapter` is ALWAYS false, because Apple ships no software Metal rasterizer, and CI pins NOTHING. Both Windows legs pin an adapter and both Linux legs pin a device because each guards against an accident, and a hosted `macos-26` runner has one device and no accident available. The integrity hole those pins close is closed here by the workflow pinning `macos-26` by number | Both, converged |
| M-G3 | Diagnostics | `KE_METAL_VALIDATION=0|1|shaders` maps onto the process-level `MTL_DEBUG_LAYER` and `MTL_SHADER_VALIDATION` variables the Metal runtime reads at device creation, plus a log line recording which tier was armed and a WARN when the variable was set after the runtime had already read it. **Whether in-process environment mutation reaches the framework is a ROW-1 SPIKE, not an assertion**, and if it does not, the answer is a job-level variable in CI and a documented prefix locally | Both, B's spike |
| M-G4 | Diagnostics | Every command buffer's `status` and `error` are read at completion, in EVERY configuration, and an error LATCHES the `MTLCommandBufferError` code and the localized description AT THE FAULT SITE, flips the liveness token, and surfaces through the existing `deviceLossReason` header field. The incumbent reads `status` in exactly one place (`WaitForIdleCore`, to decide whether to wait) and never reads `error`, so a Metal device loss is invisible to the engine and to telemetry today. Closes #427 for the Metal leg | Both, converged |
| M-G5 | Diagnostics | `MetalFrameCapture` stops reaching Veldrid's PRIVATE `_commandQueue` field by reflection and takes the native backend's queue pointer directly. The reflection version stays for the Veldrid Metal leg for as long as that leg ships | Both, converged |
| M-G6 | Counters | `GpuDeviceCounters` is populated in full with NO seam addition. The acquire-wait pair phase 3 appended is exactly what `nextDrawable`'s block needs, and `BackpressureStallCount` counts the ring acquire alone here, so its doc comment gains a sentence saying the second meaning is Vulkan's rather than universal. **No encoder-boundary counter is added to the seam**: it is the number MM1 is really about and it lives in the budget test as a frozen marginal | Both, A's refusal |
| M-F6 | Liveness | The `DeviceLiveness` latch reproduced exactly (X3, V-F10), with a full timeline drain BEFORE teardown, which the incumbent already does. That is about the ENGINE's teardown order rather than about Objective-C refcounting, so M-H3's absence of a retire list does not touch it | Both, converged |
| M-T1 | Tests | The 36 committed `metal` goldens run unmodified against the native backend on the same hosted `macos-26` device at the existing 0.06 absolute per-channel tolerance. No rebake | Both, converged |
| M-T2 | Tests | A device-free native-call budget test through a narrow `IMetalEncoderSink`, generic-constrained to a struct, covering the call classes that scale with draw count on THIS API: argument-table writes (the array setters and the offset setters), draws and dispatches, and **ENCODER BOUNDARIES**. The third is the Metal-specific class and neither predecessor has it | Both, converged |
| M-T3 | Tests | The MSL index-table test is device-free, runs on every `dotnet test`, and is taken BEFORE the first golden run: parse every emitted entry point across every shipped program, pair it against the pipeline's layout array and each layout's element array, and assert every element resolves to exactly one index in exactly the spaces its declared stages need. Plus M-B2's no-collision assertion. This is S2's and V-S8's test arriving through a third door | Both, converged |
| M-T4 | Tests | `NativeVsVeldridMetalCapabilityParityTests`, both structs in one process on the Metal leg, zero permitted differences | Both, converged |
| M-T5 | Tests | The shared uniform-ring semantic tests gain a THIRD adapter in `KhaozEngine.TestSupport.Gpu`, covering section 9.4's seven shared rows, and the Metal ring must pass them before it renders a golden | Both, converged |
| M-T6 | Tests | A `metal-native` matrix leg on hosted `macos-26`, running the FULL suite on every trigger, matching the incumbent Metal leg's `fullSuite: always` tier exactly, guest in the `metal` family, sitting bake dispatches out, with `KE_METAL_REQUIRED=1` so a row that needs a native device fails rather than going dormant. **This is the strongest regression net in the program** and it changes what the design can lean on | Both, A's required flag |
| M-T7 | Tests | Validation is a CI GATE in two tiers: `MTL_DEBUG_LAYER=1` on EVERY native-leg run, because it is an environment variable with no install and no measurable cost at this scale, and `MTL_SHADER_VALIDATION=1` on the scheduled run. Neither is a synchronisation validator and section 16 says so | A |
| M-RO1 | Rollout | Five gates, all green before any default flip (section 17) | Both, converged |
| M-RO2 | Rollout | `Metal` through Veldrid stays selectable by token until the program's CLOSING ACT. It is the kill switch for every structural decision here, which is why most bets carry no switch of their own | B's bound on "indefinitely" |
| M-RO3 | Rollout | **The three incumbent legs retire TOGETHER**, in a closing act after all three native backends have passed their own gate 4, because three separate "indefinitely" sentences otherwise mean Veldrid never leaves and #420's endpoint is unreachable by construction. It is its own release with its own risk budget and it is NOT a gate of this phase | B, A's sizing |
| M-RO4 | Rollout | Every kill switch carries a decision deadline and V-RO4's sort decides what the deadline means. This design ships exactly TWO switches, both branches inside one implementation, and no switch anywhere keeps a second implementation alive. The MSL emission question is decided at a row-1 spike rather than carried by a switch | B |
| M-RO5 | Rollout | **The flip changes the macOS default, which is the fleet's DEVELOPMENT platform**, not a player population. That is a smaller population than phase 2's Windows flip and a more consequential one per person, and it is a reason to hold gate 5 strictly rather than to relax anything. The headless default stays on Veldrid until gate 4 | Both, converged |
| M-RO6 | Rollout | **`SlathRepro` is NOT a gate, because it does not exist.** Both drafts made it one. It was deleted in `9.0.0` as a stale one-commit Metal bone-buffer repro whose fix had long shipped. Gate 5's windowed pass carries the windowed-only defect class as an explicit checklist instead, and rebuilding a windowed repro harness is a follow-up rather than a silent prerequisite | Judge, against both |

---

## 2. The contested adjudications

Eleven things were genuinely contested, and one had to be established before any of them could be decided.
That one comes first, because it is a correction to the evidence base both drafts argued from and it moves
three of the arguments below.

### 2.1 The incumbent, established

Both drafts checked the incumbent before deciding anything, and both were right about most of it. What
follows is the merged picture with every claim re-verified in the sources, plus the two places a draft's
factual claim did not survive.

**The incumbent is `4.9.103`, and its `src/Veldrid/MTL/` tree is stock upstream `v4.9.0`.**
`Directory.Packages.props` pins `Veldrid 4.9.103`, served from the vendored nupkg.
`git diff v4.9.0 v4.9.103 -- src/Veldrid/MTL/ src/Veldrid.MetalBindings/` produces zero lines, and
`git diff v4.9.103 master -- src/Veldrid/MTL/` also produces zero lines, so reading the backend on a `master`
checkout is safe. Phase 3 had to make exactly this check and one of its drafts got it wrong, so it is made
here rather than assumed. `src/Veldrid.MetalBindings/` is NOT identical to master: three files differ, and two
of them are enum widths (`MTLResourceOptions` and `MTLStorageMode` are `: uint` on the shipped package and
were widened to `: ulong` upstream). The real Metal types are `NSUInteger`, which is 64-bit on every Mac in
the fleet, so the shipped bindings answer an ABI question by luck rather than by construction. That is a
small, concrete, checkable instance of the general reason to own the interop layer, and it is worth more than
the general argument because it is a fact rather than a worry.

**The MRT clear loop writes attachment 0 for every attachment, and SHIPPED ENGINE CODE REACHES IT.**
`MTLCommandList.BeginCurrentRenderPass` iterates the pending clear array and, inside the loop, takes
`rpDesc.colorAttachments[0]` rather than `colorAttachments[i]`. `KhaozEngine.Render3D`'s `RenderResources`
creates `ModelFB` with THREE colour targets, and `ModelRenderer.BeginModelPass` clears all three, carrying the
comment "Metal MRT clear collapses to one value across attachments" above a clear of all three. So on
Metal today attachment 0 receives the same clear value three times and attachments 1 and 2 receive NO clear at
all, keeping the `loadAction = Load` that `MTLFramebuffer.CreateRenderPassDescriptor` set. **The reuse-first
draft's claim that no shipped scene clears a second colour attachment is false**, and the consequence runs
through its whole ruling on this area: it classified the fix as invisible to every golden, which it is not.
Section 2.4 re-decides it.

**A record-time `UpdateBuffer` allocates a buffer, splits the encoder, and wipes every piece of encoder
state.** `MTLCommandList.UpdateBufferCore` creates a whole new `Staging` `DeviceBuffer` per call (its own TODO
says to cache them), memcpys into it, calls `EnsureBlitEncoder`, which calls `EnsureNoRenderPass`, which ends
the render encoder, then blits, then disposes the staging buffer. `EndCurrentRenderPass` sets
`_graphicsPipelineChanged`, clears the entire `_graphicsResourceSetsActive` array, and marks the viewport and
the scissor dirty, so the next draw re-emits the pipeline state (five calls plus three more when the
framebuffer has depth), re-activates every resource set element by element, and re-emits the viewport and the
scissor. **The cost of one record-time uniform write on Metal is therefore an allocation, an encoder split, a
blit, a release, and a full graphics state re-activation.** On a tile-based deferred GPU, which is every Apple
Silicon Mac and therefore the hosted runner and the fleet's own machines, the encoder split additionally
resolves tile memory out to device memory and the next begin loads it back, unconditionally, because
`CreateRenderPassDescriptor` sets `loadAction = Load` on every attachment. That is a third distinct mechanism
for the same defect class across three backends (CPU stalls on D3D11, render-pass splits plus a global barrier
on Vulkan, encoder splits plus state wipes here), and the convergence is the argument that the ring is a
property of the seam's usage rather than of any one API. The per-frame COUNT on the #410 scene is unmeasured
and MM1 is what measures it.

**Device-level `UpdateBuffer` is an ungated memcpy into memory the GPU may be reading.**
`MTLGraphicsDevice.UpdateBufferCore` takes `DeviceBuffer.contents()` and copies straight into it. Every
`MTLBuffer` the incumbent creates is `MTLStorageModeShared` (the constructor passes options `0`), so that
pointer is CPU-visible and GPU-visible at once with no synchronisation of any kind. There is no fence, no
frame index and no wait. D3D11's `MAP_WRITE_DISCARD` gives the driver licence to rename a buffer under a write
and Metal renames nothing, so this is a plain data race in shipped code. It has never produced a reported
defect, which is a fact about the engine's call sites rather than about the code.

**Resource activation is one native call per element per stage.** `ActivateGraphicsResourceSet` walks the
set's resources and calls `setVertexBuffer` / `setFragmentBuffer` / `setVertexTexture` / `setFragmentTexture`
/ `setVertexSamplerState` / `setFragmentSamplerState` one at a time, and `GetBufferBase`, `GetTextureBase` and
`GetSamplerBase` each re-walk the preceding layouts on every single bind. That is the #418 fan-out defect on a
second API. Metal has array setters for all six, and the fork's `MTLRenderCommandEncoder` binding does not
declare a single one of them.

**A redundant pipeline bind costs a full re-activation, and the incumbent has NO identity guard.**
`SetPipelineCore` calls `Util.ClearArray` on the active-set array and sets the changed flag every time,
including for the pipeline already bound. **The reuse-first draft asserted the incumbent had an identity guard
and that reproducing it was the ruling.** It does not, and M-R8 adds one.

**Ending a render encoder does not invalidate the vertex-stream binds, and a second defect hides it.**
`EndCurrentRenderPass` clears `_graphicsResourceSetsActive` and does NOT clear `_vertexBuffersActive`. Vertex
buffer bindings are encoder state on Metal, so on the face of it every render-pass restart leaves the next
draw reading garbage. It does not, because `PreDrawCommand`'s vertex-buffer loop issues `setVertexBuffer` when
the flag is false and never sets it to true, unlike the resource-set loop directly above it, which does. So
the flag is permanently false, every stream is re-bound on every draw, and the missing invalidation can never
be observed. Two consequences, both decisions rather than trivia. The incumbent pays one `setVertexBuffer` per
stream per draw unconditionally, which belongs in the budget test's marginals as a number to beat rather than
match. And a native backend that ports the redundancy tracking without porting the invalidation ships a
corruption no golden would catch, because the goldens do not restart a render pass mid-scene. This is the
reuse-first draft's best finding and it is verified.

**Fences are real and their machinery is not.** `MTLFence` is a `ManualResetEvent` set from
`OnCommandBufferCompleted`, registered on every submitted command buffer through a hand-rolled global block
literal built with `Marshal.AllocHGlobal` and `Marshal.GetFunctionPointerForDelegate`, with a
`_NSConcreteGlobalBlock` isa loaded out of `libSystem.dylib` by name, a lock plus a dictionary lookup inside a
driver-owned completion thread, and a second static AOT path keyed on a process-global dictionary.
`VeldridMap.SupportsCompletionFences` correctly reports true for Metal, so this is a real completion fence
with a large amount of machinery behind it. `MTLSharedEvent` appears nowhere in the bindings.

**`GetSampleCountLimit` ignores both of its arguments, and that is CORRECT.** It walks
`_supportedSampleCounts` from the top and returns the first supported count, never reading either parameter,
and `_supportedSampleCounts` is filled at device creation from `supportsTextureSampleCount:`, which is
format-blind. That looks like a bug and is not one: `supportsTextureSampleCount:` is Metal's only sample-count
query and it takes no format. So `MaxMsaaSampleCount` on Metal is format-independent by construction, and a
native backend that "improved" it by asking per format would be inventing a question the API cannot answer.
The Metal-idiomatic draft is right and the reuse-first draft's follow-up to fix the format-blindness is
refused with that reason.

**Vsync depends on an equality test against a deprecated enum.** `MTLSwapchain.SetSyncToVerticalBlank` writes
`displaySyncEnabled` only when `MetalFeatures.MaxFeatureSet` is exactly `macOS_GPUFamily1_v3`,
`macOS_GPUFamily1_v4` or `macOS_GPUFamily2_v1`. `MTLFeatureSupport`'s constructor computes `MaxFeatureSet` by
enumerating `MTLFeatureSet` and keeping the last value that answered `supportsFeatureSet:`, which is
deprecated since macOS 10.15. `Enum.GetValues` returns numeric order, and the enum's macOS members are
10000 to 10005 while tvOS members are 30000 to 30003, so a machine that answered true for any tvOS member
would land outside the three and lose vsync silently. Section 2.9 rules on it and finds a consequence neither
draft connected.

**A frame with no drawable silently discards every draw.** `MTLSwapchainFramebuffer.IsRenderable` is
`!CurrentDrawable.IsNull`, `BeginCurrentRenderPass` returns false when the framebuffer is not renderable,
`EnsureRenderPass` propagates it, and `PreDrawCommand` returns false, so `DrawCore` and `DrawIndexedCore`
issue nothing. `MTLSwapchain.GetNextDrawable` sets `_drawable` from `CAMetalLayer.nextDrawable`, which returns
nil when the layer has none to give. The whole frame's rendering is discarded with nothing logged and nothing
counted.

**The viewport flush is behind a deprecated feature-set test.** `FlushViewports` picks between
`setViewports:count:` and the singular form on `IsSupported(macOS_GPUFamily1_v3)`, which is a deprecated-enum
read on the hot path to choose between two calls that do the same thing at count 1. The scissor flush is
separately gated on `_graphicsPipeline.ScissorTestEnabled`, which is the seam's own rasterizer state and stays
(M-A6).

**Present costs a command buffer, resize has no drain, and the MSL numbering is managed from the C# side.**
`SwapBuffersCore` creates a fresh `MTLCommandBuffer` purely to call `presentDrawable:` and commit it, then
calls `GetNextDrawable`. `MTLSwapchain.Resize` recreates the depth texture and takes a new drawable inline
with no drain, while in-flight frames may still be reading the texture it just released. And `MTLPipeline`
builds its `MTLVertexDescriptor` layout indices and `MTLCommandList` builds its `setVertexBuffer` bind indices
from the same `ResourceBindingModel.Improved` expression (`_nonVertexBufferCount + i`), which
`GpuDeviceContext` passes at the one windowed site and through `DefaultHeadlessOptions` at the headless one.
So `Improved` is what every shipped `metal` golden was baked under and `Default` is dead in this engine.

### 2.2 The binding-index derivation, and the route that does not exist

**This is the largest decision in the phase and the two drafts are furthest apart here.**

**What the incumbent does.** `MTLResourceLayout`'s constructor assigns each element a per-kind,
declaration-order slot within its layout, with uniform and both structured kinds SHARING one buffer counter,
both texture kinds sharing a texture counter, and samplers their own. `MTLCommandList.GetBufferBase`,
`GetTextureBase` and `GetSamplerBase` then sum the preceding layouts' counts, so a resource's Metal index is
its declaration position flattened across the pipeline's layout array, per kind.

**What the shader actually got.** Metal has no binding decorations. SPIRV-Cross assigns each resource an
index of its own, and this repository has three independently recorded production incidents saying that index
follows FIRST-REFERENCE order rather than the `set` and `binding` decorations. `7.25.0` records the model
pass's albedo sampler reading the normal texture, fixed by making the shader sample all three maps up front in
binding order. `7.51.2` records `EdgeFrag` sampling Color, Depth, Normal while binding Color, Normal, Depth,
so the crease term read depth data. And the terrain splat pass, the engine's first pipeline to bind two
uniform buffers, read the frame UBO's bytes through the params UBO. The engine already SHIPS a check for the
same mechanism: `ShaderValidation.CheckMslBufferSlots` cross-compiles every compute source to MSL, parses the
`kernel void` entry point's `[[buffer(n)]]` arguments with a depth-matched parenthesis walk, and compares the
KIND ORDER against the reflected layout order, with a message that says in as many words that Metal buffer
indices are assigned in first-reference order while the resource layout is counted in binding order. It exists
because a kernel read its cascade tile size out of the spectrum buffer, got zero, and produced a NaN surface.

**So the CPU-side count has to go, and both drafts agree on that.** They disagree on which direction the
information flows.

**The Metal-idiomatic position: OWN the numbering.** Pin every element's index into the emission through
SPIRV-Cross's MSL resource-binding API, so the CPU side and the emitted MSL agree by construction rather than
by SPIRV-Cross's heuristics. Take #462 scoped to the MSL target only, seating the emission on an engine-owned
P/Invoke shim over `libveldrid-spirv`, because the pin is not expressible through the managed `Veldrid.SPIRV`
surface. That is the better design in the abstract, it is what a native backend exists to own, and it is what
would eventually make the one-UBO constraint removable.

**It is refused, and on a fact rather than on a preference.** `libveldrid-spirv` exports exactly three
non-incidental C entry points: `CompileGlslToSpirv`, `CrossCompile` and `FreeResult`. The rest of its exported
symbol table is glslang's `Sh*` surface and SPIRV-Tools' `spv*` surface, neither of which is a cross-compiler
option. `spirv_cross::CompilerMSL::add_msl_resource_binding` IS present in the binary as a C++ symbol, because
the library exports everything it statically links, and it is not reachable: calling it would mean P/Invoking
a mangled C++ member function with a C++ ABI, on an object `CrossCompile` constructs internally and never
hands back. **So a shim over `libveldrid-spirv` gets exactly what the managed wrapper already gets, because
the managed wrapper is a thin P/Invoke over those same three exports.** The Metal-idiomatic draft's central
mechanism does not exist at the seat it names it at.

Owning the numbering therefore means linking SPIRV-Cross directly: a new native dependency built per RID, with
its own packaging story, inside the phase that is also introducing a hand-written Objective-C interop layer and
must isolate a backend swap. That is not a scoped #462, it is a bigger change than #462 was ever costed as, and
it is not this phase's.

**The ruling: read the emitted MSL (M-B1).** At `CreateShadersFromSpirv` the backend already has the MSL text
for both stages, because it produced it. It parses each stage's entry-point signature for `[[buffer(n)]]`,
`[[texture(n)]]` and `[[sampler(n)]]`, and its entry-point name, and builds a per-stage table mapping the
declared resource to the index the compiler actually chose. Resource-set activation binds through that table.
An element with no entry for a given stage is NOT bound for that stage, which is correct by construction:
SPIRV-Cross omits an argument a stage does not reference, and binding one anyway is what an index-counting
backend does that produces the off-by-one.

Four things make this the right answer rather than a consolation.

It changes NO shader and NO emission, so M-S3's byte-equality drift test and M-S4's one-off parity measurement
can establish that the MSL this backend consumes is the MSL the incumbent consumes, which is the entire
licence for "no rebake" on the fleet's reference golden family. The Metal-idiomatic route deliberately gives
that up and has to license the goldens on the index-table test alone.

The parse ALREADY SHIPS, including the depth-matched parenthesis walk that a naive parser gets wrong because
every argument carries an attribute of its own. This promotes a shipped, incident-driven diagnostic into the
binding path rather than writing a parser.

It is checkable device-free over the whole shipped set (M-T3), on the free Linux leg, on every `dotnet test`.
The failure mode this area has is "everything compiles and every pixel is wrong", and S2 and V-S8 both
answered it with exactly this test.

And it strictly DOMINATES the incumbent. Where first-reference order happens to equal declaration order the
table is the same table. Where it does not, the incumbent is wrong and this is right.

**And the ruling goes AGAINST the reuse-first draft on its fallback.** That draft proposes joining layout
elements to emitted arguments by NAME, with a fallback to ORDINAL within each index space if the names do not
match. The ordinal fallback is unsound: within a space, ordinal position is the index, so falling back to it
asserts that the emitted order matches the layout's declaration order for that kind, which is exactly the
assumption that produced three incidents. It reproduces the incumbent's defect per space instead of across
spaces. Nor is flipping `NormalizeResourceNames` available as a lever, because the incumbent emits under the
library defaults and any pin that differs breaks the byte-equality claim in the same move.

**So the fallback is named honestly instead.** Row 1's spike compiles every shipped program to MSL in process
and checks whether the emitted argument names join to `ShaderReflection`'s element names. If they do, M-B1
lands as specified. If they do not, the fallback is to reproduce the incumbent's arithmetic exactly, known
defect included, ship M-T3 as a DETECTOR rather than as an assertion, and file the numbering fix behind a real
SPIRV-Cross binding with this section as its argument. That is a worse outcome and it is a truthful one, and
it is decided at the spike rather than carried by a shipped switch.

**The reopening trigger.** A direct SPIRV-Cross binding, whether it arrives through #462 or through the
closing act's move of the front end and the HLSL back end. At that point `add_msl_resource_binding` becomes
reachable, M-B1's parse is deleted, and the table is authored rather than read. That is the right shape and
this is the wrong phase for it.

### 2.3 The one-UBO constraint: its fate, its measurement, and its lifting seat

**What the constraint is.** `docs/DEPENDENCY-SEAMS.md` carries it as a GPU-backend invariant: on Metal
through Veldrid and SPIRV-Cross, any pipeline that reads more than ONE uniform buffer mis-binds. A second UBO
read only by the fragment stage, whether in the same set or in a separate set 1, reads all zero. Textures and
samplers in a second set map fine. The read is silent, so it surfaces as garbage geometry or unlit shading
rather than as a validation failure. **And the doc records that it holds OFFSCREEN as well as windowed**,
which matters in a moment. The consequences are shipped and expensive: the splat terrain carries a bespoke
combined UBO per material with the frame block re-synced into it each frame, and the engine-wide rule for any
new render path is that a pipeline reads exactly one uniform buffer at set 0 binding 0.

**The reuse-first position: M-B1 turns it from a constraint into a convention.** If the backend binds at the
index the compiler chose, a second uniform buffer binds correctly, so the rule survives only because the
shaders are unchanged and the Veldrid leg ships. Lifting it becomes a shader change with its own goldens.

**The Metal-idiomatic position: treat it as a HYPOTHESIS about the incumbent's numbering and falsify it with
a named measurement**, keeping V-S6's shader-shape invariant in force regardless of the result, and refusing
to ship any shader change on the strength of it.

**The ruling is the second position, and the first position is rejected as an over-claim.** M-B1's table is
read out of an emission this design does not control, and until row 1's spike says the join holds, calling the
constraint "a convention now" is a prediction stated as a fact about a mechanism that has cost this
repository four separate diagnostic sessions. The reuse-first draft is probably right about the mechanism.
Probably right is not the standard for a rule whose violation is silent.

**So MM6 is a real measurement with a real gate.** Two `[GpuFact]` probes in the shape
`GpuSkinningReproGpuTests` established: a pipeline whose vertex stage reads two resource buffers, and a
pipeline with a fragment-only second UBO at set 1, each with a pixel READBACK assertion rather than a
no-throw assertion, because a `GpuFact` that only asserts no-throw is how the all-black splat terrain shipped.
Deadline gate 3. The result is recorded here whichever way it goes, because a fail is worth as much as a pass:
it says the constraint is real on Metal rather than on Veldrid, and it closes four sessions' worth of open
question.

**And the ruling goes AGAINST the Metal-idiomatic draft on MM6's second half.** That draft additionally
requires a windowed `SlathRepro` run, on the reasoning that the finding was windowed-only. Two corrections.
`SlathRepro` was deleted in `9.0.0` and cannot be run (2.10). And the windowed-only finding is a DIFFERENT
defect: `7.18.0` records the bone-palette ARRAY read corrupting past element 0 in the windowed swapchain
context, with headless rendering always clean, and that is what `SlathRepro` guarded. The one-UBO constraint's
own record says it holds offscreen as well as windowed. So a headless readback probe is the right instrument
for MM6, and the windowed class gets gate 5's manual pass rather than a deleted tool.

**The lifting seat, named so it is not lost.** A pass of MM6 authorises FILING the invariant's removal as its
own work with its own gates on all three backends. It does not authorise a shader change in this phase, and it
does not authorise one afterwards without those gates. The seat exists because M-B1 makes the binding side
correct. Whether the shader side can move is a separate question with 36 goldens on three families behind it.

### 2.4 The MRT clear, and the shipped workaround one draft missed

**The reuse-first draft ruled the collapse a correction that no golden can see**, on the stated ground that no
shipped scene clears a second colour attachment. 2.1 shows that is false:
`KhaozEngine.Render3D/Rendering/ModelRenderer.BeginModelPass` clears attachments 0, 1 and 2 of `ModelFB` and
carries a comment describing the collapse. The workaround in that comment (make all three clear values equal)
does not address the defect, because equal values do nothing about two attachments that are never cleared at
all.

**So this is not an invisible correction. It is a deliberate rendering change on the fleet's reference golden
family**, and the Metal-idiomatic draft's handling is the right one. M-A2 fixes the index. The consequence is
that `ModelFB`'s normal and linear-depth attachments start being CLEARED where today they LOAD, and what they
load today is a freshly created `StorageModePrivate` texture that nothing has written. That is precisely the
"undefined is not stable across runs" case V-F8 and V-A6 both legislate against, which means **the current
behaviour is the unstable one**: the committed metal goldens were baked reading two attachments nobody had
written.

`KE_METAL_CLEAR=attachment0` reproduces the incumbent exactly, for the A/B on the first golden run. By
V-RO4's sort it selects a branch inside one implementation, so it is cheap, and its deadline is GATE 1: once
the goldens have answered, the switch is removed and the losing branch deleted. MM2 states the exit criterion
precisely, because "some goldens moved" is not a result: either both positions are green, or exactly the
scenes whose framebuffer has more than one colour target differ and the difference is explained by two
attachments going from Load to Clear. A difference anywhere else means something other than this clause moved.

And `ModelRenderer.BeginModelPass`'s comment is a doc task with an owner, because it will be false on the
native leg and is an incomplete description of the Veldrid one.

### 2.5 The tiler store actions: depth DontCare and the folded resolve

**Both drafts landed conservative and their reasoning differs, so this reconciles rather than decides.** The
reuse-first draft defers both on W1's sequencing (a v1 that also changes what a pass writes out cannot
attribute a regression to the recording model and the memory model) and records the magnitude as larger than
the Vulkan equivalent. The Metal-idiomatic draft defers depth `DontCare` on V-A6's determinism argument alone
and does not raise the folded resolve as a candidate at all, reproducing the incumbent's standalone resolve
encoder without arguing it.

**The ruling keeps both conservative, and takes BOTH reasons rather than one.** Determinism is the reason
depth `DontCare` cannot land: it leaves contents undefined, undefined is not stable across runs, and the
goldens require stability on the same device. Attribution is the reason the folded resolve cannot land:
`MTLStoreActionStoreAndMultisampleResolve` changes what the producing pass writes out, and `scene3d_hdr_msaa`
is a committed golden in the family this phase is being measured against.

**And the magnitude is recorded rather than left implicit**, because it is what sizes the follow-ups. On a
tile-based deferred GPU a depth `DontCare` means the depth tile is never written to device memory at all,
which is a larger win than the Vulkan equivalent and a real one for the shadow atlas and the depth prepass.
The folded resolve removes an entire encoder, which under M-T2's counting is a first-class cost class on this
API. Both are filed with named consumers and with the argument each one owes: a determinism argument for the
first, a golden argument for the second.

Note also that 2.4's ruling makes the second half of this section land differently than it reads: this phase
is already spending one deliberate rendering change on the reference family, and that is the budget. A second
one would make MM2's A/B unreadable.

### 2.6 Hazards: automatic tracking, and the condition that decides it

**Both drafts keep automatic tracking, and they are right**, so this section records why the agreement is
correct rather than re-arguing it, and then improves the recording.

Metal tracks hazards automatically for resources allocated from the device. Two encoders on one command buffer
are ordered and the driver inserts the dependency. The whole of phase 3's synchronisation subsystem (V-F6's
explicit barriers, V-F7's canonical resting layouts and list-local tracker, V-F8's `UNDEFINED`-discard
determinism rule, V-F9's deferred disposal) and phase 2's SRV-versus-UAV auto-unbind have no occupant here,
which #531 predicted by name. That deletion is the single largest simplification in the design and M-H3 states
it as a decision so a reader does not conclude a tracker was forgotten.

**The reuse-first draft's conditional recording is the stronger one and it is adopted with a sharpening.**
V-M1 declined VMA on the condition that a synchronisation validator EXISTS to catch what it gives up, and said
outright that if the sync gate were ever dropped the decline must be re-argued. Metal has no synchronisation
validator: `MTL_DEBUG_LAYER` checks API usage and `MTL_SHADER_VALIDATION` checks in-shader memory access, and
neither tracks read-after-write hazards across encoders. So an untracked Metal backend would carry the exact
hazard class phase 3 spent a whole new CI job on, with no detector anywhere in the net, and it would carry it
on the ONE backend whose CI has a real device. Phase 3's missing-barrier class was invisible on lavapipe
because a software rasterizer executes with far stronger implicit ordering than real hardware. Real hardware
really reorders, so a missing fence on this leg is a FLAKY golden rather than a consistently green one, and a
flaky golden on a five-legged blocking matrix is the worst failure shape the program has.

**The improvement is the Metal-idiomatic draft's linkage, folded into the same clause.** `MTLHeap` is not a
separate decision. Heap-allocated resources are hazard-untracked by default, so taking heaps imports this
hazard class through a side door without anyone deciding to. M-H1 and M-M1 are therefore one decision with two
names: **if a future consumer ever needs heaps, it needs a hazard tracker in the same change.**

**Three things the decision does not buy, said out loud.** Automatic tracking orders GPU against GPU and says
nothing about a CPU write racing a GPU read, so the ring's completion gate is load-bearing and is not made
redundant (M-M7). Automatic tracking is conservative, so the driver may serialise two encoders that could have
overlapped, and nothing in this design measures the lost overlap. And the cost cannot be priced at all,
because there is no safe untracked build to A/B against unless somebody writes one. MM5 carries all three as
an observation rather than a bet.

### 2.7 The bind flush: array setters, and where the platform prior wins

**The reuse-first draft emits one call per element per stage**, reproducing the incumbent's shape with a
two-state dirty model in front of it. **The Metal-idiomatic draft emits one ARRAY call per (kind, stage)**,
using `setVertexBuffers:offsets:withRange:` and its five siblings, and one `setBufferOffset:atIndex:` per
visible stage for the offsets-only case.

**The ruling is the Metal-idiomatic one, on both halves, and it is not close.** 2.1 establishes that
per-element activation is the #418 fan-out defect arriving on a second API, and #418 is a defect this
program already paid to fix once. The array setters exist, and the only reason the reuse-first draft does not
reach for them is that the fork's bindings do not declare them, which is a fact about the fork rather than
about Metal. The offsets-only path is the sharper of the two: the engine's hot path is the shadow pass doing
thousands of offsets-only rebinds of one slot per frame, and `setBufferOffset:` writes an integer into the
encoder's stream where re-issuing `setBuffer` writes a whole argument-table entry.

**What survives from the reuse-first draft is the dirty model underneath.** Both drafts land on two states
rather than three, for different reasons, and the two-state per-slot record is what feeds the array assembly:
the flush walks the dirty records and assembles a contiguous range per (kind, stage), so repeated dirty marks
between two draws collapse to one flush and a slot whose recorded set went null is skipped. Two states with an
array flush is strictly better than either draft's version taken alone.

**And clause 5 is where all three backends differ for three different reasons, which is the clearest evidence
for 2.8's refusal to extract the schedule.** On D3D11 a pipeline switch drains under the OUTGOING layout
because the layout decides register numbering. On Vulkan it invalidates from the first incompatible set
because a pipeline-layout mismatch invalidates bound descriptors. On Metal nothing is invalidated by the API,
because argument tables are absolute, and what changes is where the incoming program expects each element to
be.

**Here the two drafts each have half of it and neither has the whole.** The Metal-idiomatic draft compares a
per-set BASE INDEX VECTOR computed by summing the preceding layouts' per-kind counts, which is the arithmetic
2.2 just removed as the authority. The reuse-first draft invalidates WHOLESALE on every switch, reproducing
the incumbent, and argues that Metal has nothing finer to compute. Both are wrong in the same way: under M-B1
the authority is the per-program INDEX TABLE read out of the MSL, so M-R9 compares THAT, content-deduplicated
so two programs with identical tables invalidate nothing. That is the fine comparison the reuse-first draft
said did not exist, computed off the source the Metal-idiomatic draft did not use.

### 2.8 The #531 extraction: scope, list and trigger

**#531 is explicit that this phase decides it**, and equally explicit about how: re-assess each candidate
against three implementations rather than assuming the list carries. Both drafts did that and produced
different lists, so the ruling is a merge with a per-candidate reason.

**Extract, five things.**

- **The `DeviceLiveness` latch.** A volatile token flipped inside the lifecycle lock before the real device
  dies, every wrapper's `Dispose` gated on it, `IGpuFence.Signaled` reading true after death, `WaitForIdle` a
  no-op after death. Three copies with no mechanism difference at all.
- **The counter accumulators.** `FramesBegun`, the drain pair, the backpressure pair, the off-timeline pair and
  the acquire pair are the same arithmetic behind the same struct in three places.
- **The diagnostic rate limiter.** D3D11 rate-limits `ID3D11InfoQueue`, Vulkan rate-limits the debug
  messenger, Metal rate-limits command-buffer error logging. Same shape, same reason.
- **The shader-cache KEY and file discipline**: pin plus engine version plus device identity, header-validate
  before trusting, discard silently on mismatch, best-effort so a read or write failure is never fatal. The
  PAYLOAD differs (DXBC, `VkPipelineCache`, `.metallib`) and stays per backend.
- **The completion TIMELINE's bookkeeping.** `D3D11MonotonicFenceTimeline`, `VulkanTimeline` and Metal's
  `MTLSharedEvent` are the same object: a monotone unsigned value, advanced on submit, read non-blocking,
  waited on blocking, with `WaitTotals` counting drains. **This one is only extractable because M-F1 chose the
  shared event**, and that is worth saying: had the timeline been a counter advanced from a completion
  callback, the primitive underneath would have been different enough that the bookkeeping above it would have
  had to grow a second shape. A ruling in one section decided a candidate in another.

**Refuse, four things, each in writing so #531 closes with an answer rather than a deferral.**

- **The ring's CODE.** Three implementations, one policy, three mechanisms: a mapped `DYNAMIC` buffer with a
  256-byte constant-count round-up, a persistently mapped host-coherent allocation with a descriptor range
  answering to a VUID, and a Shared buffer with an integer offset write and no range at all. The POLICY has
  already been extracted, into the shared semantic tests V-P5 built, and the right move at three
  implementations is to add the third adapter (M-P5), not to unify the mechanism.
- **The record-then-flush SCHEDULE.** Three flushes, three arities, three invalidation rules that exist for
  three different reasons (2.7). What is common is a per-slot record array and the rule that repeated dirty
  marks collapse to one flush, which is sixty lines and a comment, and whose extraction would need a generic
  activation callback, a generic slot record and a generic invalidation policy.
- **The dirty MODEL.** D3D11 has three states, Vulkan two, and Metal two for a DIFFERENT reason than Vulkan's.
  Two backends agreeing on a number while disagreeing on why is exactly the shape that produces a shared
  abstraction nobody can change.
- **The generic EMITTER interface.** V-P4 excluded it by name at two implementations, and Metal's sink is a
  third shape again, with a call class (encoder boundaries) neither neighbour has. The exclusion is confirmed
  rather than reopened.

**The home is `KhaozEngine.Gpu/Internal/`**, which every backend already references and which already grants
`InternalsVisibleTo`. Not a new `KhaozEngine.Gpu.Backend` package: phase 3's rejection table already argued
that it needs public API to serve consumers we all own, and that argument is unchanged at three.

**The trigger is the Metal-idiomatic draft's and the exit is the reuse-first draft's.** The extraction lands
AFTER rollout gate 3, not after the Metal rows go green, because extracting while the backend is being written
gives a golden failure two candidate causes and the whole value of a guest golden family is that it has one.
And every candidate carries a written exit: a candidate the third implementation does not fit closes as NOT
PLANNED with that reason. A decision to extract that cannot fail is not a decision.

**What it costs, stated plainly.** Production types move packages, which is a real diff across `Gpu.D3D11` and
`Gpu.Vulkan` in a release that is otherwise additive. Phase 2's frozen native-call marginals and phase 3's are
the regression proof that the move changed nothing, and that proof is cheap because both are device-free tests
that already run on every `dotnet test`.

### 2.9 Vsync, the deprecated enum, and the two frame-cap sites

**The first half is settled and goes to the Metal-idiomatic draft.** The incumbent writes `displaySyncEnabled`
only under an equality test against three values of a deprecated enum, and on a machine outside that set a
vsync toggle silently does nothing and says nothing. The reuse-first draft reproduces the condition, deriving
its answer from `supportsFamily:` instead of `supportsFeatureSet:`, which removes the deprecated read and
keeps the conditional. **Keeping the conditional is the part that has no defence.** `displaySyncEnabled` is a
`CAMetalLayer` property on a macOS-only backend, so there is nothing to be conditional about. M-W2 sets it
unconditionally, which is V-W2's ruling on the two Vulkan bugs applied to a third.

**The second half is a consequence neither draft connected, and it is ruled against both.** Both drafts walk
the append audit and mark `FrameCap.Resolve` and `DisplaySettings.RequiresFrameCapWarning` as sites that MUST
CHANGE, routing `MetalNative` through `IsMetal()` so the native leg does not silently lose the software frame
cap. Read the sites: both carry an explicit comment saying the equality against `Metal` is DELIBERATE, that an
appended backend falling into the uncapped arm is the RIGHT answer for `Direct3D11Native` because its present
throttles the CPU from vsync exactly as the incumbent's does, and that the question is therefore **whether
this backend's present throttles the CPU**, not which API it is.

That question is open for `MetalNative` in a way it was not for `Direct3D11Native`. `DisplaySettings`'s own
doc says the Veldrid Metal present does not throttle the CPU from vsync alone, which is why the software cap
exists. M-W2 makes `displaySyncEnabled` actually take effect where the incumbent's conditional may not have,
and M-W4 sets `maximumDrawableCount` to `FramesInFlight` with a blocking `nextDrawable` at the boundary. Those
two together may well throttle the CPU, in which case the software cap is redundant on this backend. They may
not, in which case removing it would regress a Mac client to a free run.

**So M-W3 routes both sites through `IsMetal()` and defers the ARM to measurement.** Defaulting `MetalNative`
into the Metal arm is conservative in both directions: if the native present throttles, a software cap at the
display refresh does not bind and costs nothing, and if it does not, the cap is required. Gate 5's windowed
pass reads which it is, with the vsync toggle mid-session as its instrument, and the disposition is recorded
either way. Both sites' doc comments are rewritten in the same row, because they currently assert a rationale
that this append makes incomplete.

### 2.10 The rollout conditions, and a gate that cannot be run

**Both drafts make `SlathRepro` a rollout condition.** The reuse-first draft adds it as a sixth condition on
the flip, calling it the repo's only windowed Metal regression guard. The Metal-idiomatic draft promotes it to
a decision row and puts it in gate 5, calling it the engine's committed windowed Metal regression check and
part of MM6's measurement.

**`SlathRepro` does not exist.** `CHANGELOG.md` records it deleted in `9.0.0`: "SlathRepro (stale one-commit
Metal bone-buffer repro, fix long shipped) deleted." It was introduced in `7.18.0` as a manual windowed repro
kept deliberately out of the solution, guarding the bone-palette array-read corruption that could not be
reproduced headless. The fix for that defect (CPU skinning on every backend) shipped in the same release and
still ships.

**So the ruling is against both, and M-RO6 says what replaces it.** A gate stated against a tool nobody can
run is worse than no gate, because it reads as coverage. Gate 5's windowed pass carries the windowed-only
defect class as an explicit checklist item instead: skinned meshes and any pipeline reading a second uniform
buffer, exercised at a window, with the tester told what the failure looks like. Rebuilding a windowed Metal
repro harness is a follow-up with a named trigger (MM6 wanting a windowed half, or a windowed-only defect
recurring), not a silent prerequisite of this phase.

**Two other rollout conditions are worth recording as settled rather than contested.** Both drafts put the
`metal-native` leg on hosted `macos-26` at the incumbent Metal leg's `fullSuite: always` tier, guest in the
`metal` family, sitting bake dispatches out. That is verified correct against `cross-platform-gpu.yml`, where
the `metal` row already carries `"fullSuite":"always"` and the workflow header records why `macos-26` is
pinned by number. And both drafts require gate 4 to TAKE its own incumbent baseline first, because #410's
reporting machine is Windows and no published Metal field number exists anywhere in this program's record. A
gate stated against a number nobody has measured cannot be read.

### 2.11 Where each prior wins outright

The rulings above do not sweep in either direction, and naming where each prior is right is worth more than
pretending otherwise.

**The Metal-idiomatic prior wins the per-draw cost argument outright (2.7).** Array setters and
`setBufferOffset:` are the whole per-draw story on this API, and the reuse-first draft missed them because the
fork's bindings do not declare them. It also wins the pipeline identity guard, the vsync fix, the MSAA
exoneration, the autorelease rule, the shader-cache mechanism, the retirement contradiction, and the MRT clear,
where its ruling was right for a reason its own evidence found and the other draft's evidence denied.

**The reuse-first prior wins the parity surfaces**, which is where a mistake fails on every golden at once.
The staging subresource layout reproduced byte for byte with a table test taken from the incumbent's own
arithmetic. The present path kept on its own command buffer, because the seam's `Present()` is a separate call
from `Submit()`, the win is one cheap object per frame, and the alternative inherits the Vulkan design's own
named limitation into the one area with zero automated coverage. The clear-arrival semantics reproduced rather
than re-derived, since a golden depends on the clear-only case. And the byte-equality parity discipline, which
2.2 makes available and which the Metal-idiomatic draft's route would have given up.

**And it wins the two findings that decide the recording model.** The vertex-stream invalidation defect and
the second defect that hides it is the sharpest reading either draft produced, and the conditional form of the
hazard-tracking decline is the recording that a future reader needs. Both are adopted verbatim.

---

## 3. Package, layering and the interop

`KhaozEngine.Gpu.Metal`, one assembly, referencing `KhaozEngine.Gpu` and `KhaozEngine.Diagnostics` and nothing
else. Target `net10.0`, NOT `net10.0-macos`, so the assembly compiles and its device-free tests run on the
Linux `ci.yml` leg and on both Windows legs, and so `KhaozEngine.Render.Tests` can reference it
unconditionally. Every entry point carries `[SupportedOSPlatformGuard("macos")]` and every
Objective-C-touching body is `[MethodImpl(MethodImplOptions.NoInlining)]` behind an `OperatingSystem.IsMacOS()`
guard.

**That apparatus is P1's rather than V-P1's, and the difference is worth one sentence.** Vulkan needed none of
it because Vulkan is not an OS-specific API. Metal is, so the D3D11 pattern applies verbatim and CA1416 makes
the compiler enforce the boundary under warnings-as-errors. The pattern is already proven in this repository
twice over: `D3D11ThreadingProbe` on the Windows side, and `MetalFrameCapture`, which lives in
`KhaozEngine.Gpu` today with `objc_msgSend` declarations and loads harmlessly on Linux and Windows because
nothing calls it there.

Guard work the package creates, all mechanical and all precedented:

- `ArchitectureTests.OptInBackends` gains `Gpu.Metal`, which then enforces
  `OptInBackends_AreNotReachableFromAnyUmbrella`.
- `ArchitectureTests.ThirdPartyHomes` gains NOTHING, which is a first in this program: this is the only backend
  with no third-party package at all. `EveryThirdPartyPackage_IsDeliberatelyMapped` is unaffected, and the
  absence is worth a comment in the test so a later reader does not add a row for symmetry.
- `KhaozEngine.slnx` gains the project, which force-adds `KhaozEngine.Tests` to the selective-test set, so the
  architecture guards run on the landing PR.
- `check-doc-versions.sh` requires a bolded `**KhaozEngine.Gpu.Metal**` catalog row in the root `README.md` and
  a `KhaozEngine.Gpu.Metal/README.md` shipped via `<PackageReadmeFile>`.
- `GpuPublicApiTests` extends its walk to the new assembly.
- The no-Veldrid pair in BOTH forms, the csproj read and the IL reference walk. The walk is the load-bearing
  one, because Veldrid is in the transitive closure through `KhaozEngine.Gpu` whatever the csproj declares.
- A new assertion, the Metal-specific member of that family: the backend names the cross-compile back end's MSL
  members and no HLSL member, which is the third arm of the architecture test V-S3 created.
- `docs/DEPENDENCY-SEAMS.md` gains the third instance of the out-of-package backend edge.

### 3.1 The interop layer (M-P2)

**There is no maintained managed Metal binding, and that is the whole argument.** Phase 2 took
`Vortice.Direct3D11` and phase 3 took `Silk.NET.Vulkan`, both on the reasoning that owning the BACKEND and
owning the BINDING are different things and that #420's endpoint is "no Veldrid in the graph" rather than "no
dependencies". That reasoning is unchanged and it has nothing to point at. Silk.NET ships Vulkan, OpenGL,
OpenCL, OpenAL, GLFW and SDL and no Metal. Vortice ships Direct3D, Vulkan and audio and no Metal. Apple ships
no managed binding of any kind. The candidates are a hand-rolled layer or vendoring `Veldrid.MetalBindings`.

**Vendoring is rejected by name**, on V-P2's own wording: Veldrid-derived code inside the backend built to
remove Veldrid, invisible to every guard that reads package ids, in a repository whose architecture tests
assert exactly that no Veldrid edge exists. It also brings shapes this design does not want: an
`objc_msgSend_stret` path behind a `UseStret<T>()` that always returns false, block-literal machinery M-F1
replaces, the deprecated `MTLFeatureSet` enumeration M-N3 stops asking, and not one of the array setters M-R6
requires. Reading it as the reference implementation is a different act and it is the right one, and this
document does exactly that throughout.

**So it is hand-rolled, and the mechanism is decided rather than left open.** One internal file family under
`Internal/ObjC/`, one file per Objective-C class rather than one per API surface, which is what the fork
already does and what keeps the KESIZE ratchet satisfied by construction. Three parts. A runtime shim
(`objc_getClass`, `sel_registerName`, `objc_msgSend` in the overload set the calls need, `objc_retain` and
`objc_release`) declared with `[LibraryImport]` and blittable-only signatures, source-generated with no
marshalling stub, which is also what the SYSLIB1054 analyzer requires under warnings-as-errors. A set of
readonly-struct handle types over `IntPtr`, one per Metal protocol the backend touches, so a device is not
interchangeable with a queue at compile time. And the enums, declared with the RIGHT underlying width, which
2.1 shows the vendored bindings get wrong for `MTLResourceOptions` and `MTLStorageMode`.

**What this layer needs beyond what the fork declares**, so the size is on the record: the six array setters on
`MTLRenderCommandEncoder` and their compute siblings (M-R6), `setVertexBufferOffset:atIndex:` and
`setFragmentBufferOffset:atIndex:` (M-R7), `MTLSharedEvent` with `newSharedEvent`, `signaledValue`,
`encodeSignalEvent:value:` and `waitUntilSignaledValue:timeoutMS:` (M-F1), `supportsFamily:` and
`MTLGPUFamily` (M-N3), `MTLCommandBuffer.error` with `MTLCommandBufferError` and `NSError.localizedDescription`
(M-G4), and `maximumDrawableCount` on `CAMetalLayer` (M-W4). What it does not need at all: the whole
`MTLFeatureSet` enum and `supportsFeatureSet:`, `objc_msgSend_stret`, the indirect-draw encoder methods (the
seam has no indirect draw, and the incumbent's `DrawIndirectCore` loops issuing one draw per element anyway),
and the specialization-constant path (the seam exposes none).

**The arm64 caveats, stated because they are where hand-rolled interop dies.** `objc_msgSend` must be called
through a prototype matching the method's real signature on arm64, which is what a typed-overload approach
gives and what a single variadic declaration would break. `objc_msgSend_stret` does not exist on arm64 at all,
so no stret path is written rather than one being written and disabled. `BOOL` is one byte and `CGFloat` is a
double on 64-bit. Every one of these is a row-1 spike item rather than an assertion in this document, because a
wrong ABI assumption in interop is a memory corruption rather than a compile error.

**Row 1's spike, and what fails if each answer is no.** It compiles ONE file touching every Objective-C call
this design names and runs it against a real device: the `objc_msgSend` return classes, an
`[UnmanagedCallersOnly]` completion handler firing on a real command buffer, `MTLSharedEvent`'s four members,
the array setters and the offset setters, `supportsFamily:`, and whether in-process environment mutation
reaches the validation layer (M-G3). The named fallbacks are the incumbent's delegate-and-dictionary block
shape (losing AOT-cleanliness on the completion path), a completion-counter timeline instead of a shared event
(which also removes M-P4's fifth extraction, per 2.8), per-element binds instead of array setters (losing
M-R6's whole argument and the budget test's headline marginal), and a job-level environment variable in CI plus
a documented local prefix for validation.

**The counterargument owed.** A hand-rolled interop layer is the single largest line-count item in a phase
whose bar is parity, and phase 3 rejected hand-rolled P/Invoke for Vulkan in as many words: thousands of lines
of struct definitions where every mistake is a memory corruption rather than a compile error. That rejection is
right and it does not transfer, because the two are not the same size. Vulkan's surface is a C API with
hundreds of structs that must be laid out byte-exactly. Metal's is an Objective-C API reached through one
dispatch function, where the surface this backend needs is roughly sixty selectors and a dozen enums, and the
struct layouts involved are `MTLSize`, `MTLOrigin`, `MTLRegion`, `MTLViewport`, `MTLScissorRect` and
`MTLClearColor`. The engine already ships Objective-C interop in `MetalFrameCapture`, so the capability is not
new. And the golden leg is a real device running the full suite on every trigger, which is the strongest
regression net any leg in this program has, so an interop defect surfaces on the next push rather than in a
field report. **An ABI error presents as a crash rather than as a wrong pixel, which is the one comforting
property of this risk.**

---

## 4. Selection, identity and wiring

### 4.1 What the two previous phases already paid for

`GpuDeviceContext` is already inverted onto `IGpuDevice`. `GpuBackendProviders` and `IGpuBackendProvider`
exist, with the second constructor, the disposal hook and the capability read off the device.
`GpuBackendProviders.IsBuiltIn` lists the four Veldrid-backed kinds, so an APPENDED kind is provider-backed by
default and `MetalNative` needs no edit there. `PreflightProvider` fixes the order so a missing registration
throws before the probe can answer false. `GpuBackendProviderMissingException.BuildMessage` was corrected in
phase 3 to state the naming convention rather than switch on the kind, so it degrades correctly for this
backend with no change at all. And the test-side seat is a static constructor on `GpuFactAttribute` in
`KhaozEngine.TestSupport.Gpu`, fired at xUnit discovery in ANY assembly carrying `[GpuFact]`, so a
`MetalBackendRegistration` sibling goes in the SAME project beside the D3D11 and Vulkan ones.

So this phase adds a REGISTRATION and re-litigates none of the wiring: `KhaozEngineMetal.Register()`, one
public entry point, called once at consumer startup, no `[ModuleInitializer]`, no reflection. **Three
templates, now proven twice each, is the phase-3 dividend arriving.**

**`IsSupported()` is a functional probe with real content (M-N4).** The incumbent's
`MTLGraphicsDevice.GetIsSupported` checks the OS platform, then either counts `MTLCopyAllDevices` or creates
the system default device, wrapped in a bare `catch` that answers false. That is the FLOOR. On top of it the
probe reads four things, each cheap here and expensive anywhere later:

- a device exists and reports a name, which is what `GpuCapabilities.DeviceName` parity depends on.
- `supportsFamily:` reports at least the Apple or Mac family floor section 5 pins, so a machine below it
  answers false rather than crashing on frame one.
- the device's minimum constant-buffer offset alignment is at or below 256, which M-M3's stride depends on and
  which is the one number that would silently corrupt every ring bind if a future device raised it.
- `supportsTextureSampleCount:` answers for at least 1, which is what M-C3's limit read walks.

It must never throw. A machine with no Metal device answers false and routes through `AfterFallback` as
`FallbackAfterFailure`. Phase 3's corrected-in-flight lesson is inherited without re-deriving it: CREATION
consults this probe BEFORE creating, so a machine-level refusal is always a `NotSupportedException` naming what
is missing, a missing REGISTRATION still throws its own exception, and the creation-time
`InvalidOperationException` narrows to the genuinely surprising case its message describes. The probe is
memoized on the provider instance, whose lifetime is the registration's.

### 4.2 The `GpuBackendKind` append audit, third time

The audit is a TEST (`GpuBackendKindAppendAuditTests` and its Vulkan sibling), which is what made the second
append a diff rather than a re-derivation and makes this one a diff again. Appending `MetalNative = 6` touches
the sites the corrected phase-3 record enumerates. **Three answer differently from BOTH previous appends and
all three degrade SILENTLY**, which is the highest silent-degradation count of the three phases and is the
reason this section is not a formality.

| Site | `VulkanNative`'s answer | `MetalNative`'s answer |
|---|---|---|
| `GpuDeviceContext.LogThreadingCaps` | No change, it gates on `IsDirect3D11()` | No change, same reason. No `D3D11_FEATURE_DATA_THREADING` analogue exists |
| `D3D11ThreadingProbe.IsApplicable` | No change | No change. `ThreadingCaps` and `ThreadingProbeFailure` are both null, which the record documents as "there was nothing to ask" |
| `CreateWindowed` and `CreateHeadless` switch expressions | Rides the existing explicit throwing arm | Same. Verify the message still names the provider registry generically, which phase 3 already made it do |
| `GpuBackendSelector.ToVeldrid` | Explicit throwing arm | Same, one more arm |
| `GpuBackendSelector.TryParseBackend` | Two tokens added | Add `metal-native` and `mtl-native`, in the whole-token style both previous appends used so a typo'd suffix gets the `UnrecognizedOverride` diagnostic rather than a silent run on the incumbent |
| `GpuBackendSelector.RecognizedTokens` | Read by the unrecognized-override WARN, pinned by the audit test | Add both tokens. The audit test asserts every listed token parses and every kind is listed, so this cannot be missed |
| `GpuBackendSelector.IsBackendSupported` | Route to the provider's probe | Same. Veldrid cannot answer for it |
| `GpuBackendSelector.ProbeOS` | Unchanged until the flip, and the flip means LINUX | Unchanged until the flip, and **the flip means macOS**, which is the fleet's development platform (section 17) |
| `GpuBackendSelector._windowCandidates` | Unchanged until default-ready | Same. A player does not choose an implementation |
| **`Windowing/FrameCap.Resolve`** | Falls into the uncapped arm, correct by default, recorded because it is #380's arm | **MUST CHANGE, and it is silent.** It applies a real software frame cap only on Metal plus vsync, so `MetalNative` falls into the uncapped arm and a native windowed run loses the cap the incumbent run has. Route through `IsMetal()`, and **2.9 rules that the ARM is a gate-5 measurement rather than an assumption**, with the Metal arm as the conservative default |
| **`Windowing/DisplaySettings.RequiresFrameCapWarning`** | Same shape, same arm | **MUST CHANGE, same shape, same silence, same 2.9 ruling.** Both sites' doc comments assert the equality against `Metal` is deliberate, and both are rewritten in the same row |
| `GoldenCompare`'s two filename sites | Both route through `GoldenBackendToken` | Both, mapping `MetalNative` to `metal`. The switch has no discard arm and throws, and the audit test turns a missed mapping into a device-free red |
| `VeldridMap.SupportsCompletionFences` | Not an append site, answers true for Vulkan | Not an append site, and worth naming: it answers true for `GraphicsBackend.Metal` already, which is why M-F4 is parity rather than the upgrade it was on D3D11 |
| **`VeldridGpuDevice`'s Metal frame-capture gate** | Unaffected | **MUST CHANGE, and it is the third silent one.** It gates a `MTLCaptureManager` capture on `Backend == GpuBackendKind.Metal`, so a native run arms nothing and a diagnostic capture silently produces no trace. M-G5 gives the native backend its own capture path, which is better than widening the gate: it owns the queue, so the reflection into Veldrid's private `_commandQueue` field is unnecessary there |
| `GpuBackendProviderMissingException.BuildMessage` | Fixed generically in phase 3 | No change, and that is the fix paying out |
| `GpuDeviceContext.LogSelection`'s token list | Reads `GpuBackendSelector.RecognizedTokens` | No change, and that is the second phase-3 fix paying out |
| `GpuDeviceContext.CreateOrFallBack` | Correct by default | **Correct by default and the reasoning differs again, so it is recorded.** On macOS `ProbeOS` returns `Metal` while the request is `MetalNative`, so they differ and the request routes through the functional probe. A Mac whose native creation fails falls back to Veldrid Metal and reports `FallbackAfterFailure`, while a missing REGISTRATION still throws. The soak depends on telling those apart in a log line |

`GpuBackendKinds.IsMetal()` is added beside its two siblings (M-I5), and unlike `IsVulkan()` it has readers on
day one. Three of them, and all three are in the table above.

---

## 5. Device, queue and lifecycle

One `MTLDevice` and one `MTLCommandQueue`, both created under `GpuDeviceContext._lifecycleGate`, which stays.
No process-wide instance object exists on Metal, so V-N1 has no analogue, and the gate is not this backend's to
remove: it also covers disposal.

**Device selection (M-N1).** `MTLCreateSystemDefaultDevice()` is the default, which is what the incumbent does
and what keeps `GpuCapabilities.DeviceName` parity satisfiable by construction. `KE_METAL_DEVICE` accepts an
index into `MTLCopyAllDevices()`, a name substring, or one of `discrete`, `integrated` and `low-power`, with a
named-but-absent device producing a WARN plus the default path rather than a hard failure. Any substitution is
LOGGED, so a soak session can tell a substitution from a selection. Phase 3's 2.9 argument is unchanged:
changing which GPU the engine runs on is a user-visible change unrelated to swapping the backend, it breaks
`DeviceName` parity in a design demanding zero capability differences, and it puts a second variable into the
one gate that must isolate the swap. Preferring a discrete GPU on a dual-GPU Mac is a follow-up with its own
change note.

**CI pins nothing (M-G2), and that is a deliberate difference from both other backends.** Both Windows legs
pin `KE_D3D11_ADAPTER=warp` and both Linux legs pin `KE_VULKAN_DEVICE=llvmpipe` because each guards AGAINST an
accident: a paravirtual adapter appearing, an ICD manifest moving. A hosted `macos-26` runner has exactly one
device and no accident available, so a pin could only produce false failures. The integrity hole those pins
close is closed here by the workflow pinning `macos-26` by number rather than to `macos-latest`, which its own
header already records as being so an image promotion cannot silently move the GPU under a golden gate.

**One queue (M-N2).** `MTLCommandQueue` is documented thread-safe, which is what makes M-W8's lock-free
recording true. Command buffers execute in ENQUEUE order on a queue, and `commit` enqueues if the buffer was
not already enqueued, so committing under `_submitLock` makes SUBMIT ORDER the observable order by
construction, which is the seam's contract, with no `enqueue` call at `Begin` and no second queue. `enqueue` at
`Begin` is the alternative and it would let submits proceed without the lock, and it is declined for v1 because
nothing asks for it and it makes the order depend on `Begin` rather than on `Submit`, which is not what the
seam documents.

No second queue and no async compute. #534's argument transfers with no modification and the FFT ocean
(`OceanFftProducer`) is the same named consumer: a second queue needs `MTLSharedEvent` cross-queue signalling
and its own submit lock, for a renderer whose uploads are megabytes at load time and whose compute is one chain
already gated by the seam's rule 2. The Metal-specific note worth adding to that issue is that Metal's
cross-queue story is cheaper than Vulkan's queue-family ownership transfers, so the follow-up is smaller here,
and it still has no consumer.

**Capability floor (M-N3).** New reads use `supportsFamily:` for `MTLGPUFamilyApple*`, `Mac2` and `Metal3`.
The incumbent's `MTLFeatureSet` enumeration is not reproduced for new questions, because `supportsFeatureSet:`
has been deprecated since macOS 10.15 and because `MTLFeatureSupport.MaxFeatureSet` feeds two fragile reads:
the vsync equality test M-W2 removes, and `IsMacOS`, off which the incumbent derives its uniform-buffer
alignment (`MetalFeatures.IsMacOS ? 16u : 256u`) and its sampler border colour. **PARITY surfaces are the
exception** and reproduce the incumbent's own question, which is M-C3 and section 14, because a parity surface
that asks a different question is a parity failure by construction whatever the new question's merits.

**Autorelease discipline (M-N5).** Metal's factory methods return autoreleased objects. The incumbent wraps
four sites in `NSAutoreleasePool` and does not wrap others, which is the shape that accumulates under a frame
loop. The rule here is that every public entry point which can create an autoreleased object wraps its body,
enforced by a device-free architecture test over the type graph rather than by review, in the shape V-D2 used
for descriptor-pool unreachability.

**Teardown (M-F6).** Drain the timeline first, then flip the liveness token inside the lifecycle lock, then
release the queue and the device. The incumbent already calls `WaitForIdle` first, which is the half phase 3
had to correct on Vulkan, so this is reproduction rather than repair. The `DeviceLiveness` latch is X3 and
V-F10's, reproduced exactly: that is about the ENGINE's teardown order rather than about Objective-C
refcounting, so M-H3's absence of a retire list does not touch it.

---

## 6. Command recording

### 6.1 The list, the buffer and the encoders (M-R1 to M-R3)

`MetalCommandList : IGpuCommandList`, encoding at record time. There is no op stream, no second driver, no
`KE_METAL_RECORD` and no M1-analog A/B. An `MTLCommandBuffer` between `commandBuffer()` and `commit()` is a
driver-encoded command stream and the encoders write into it directly, so a managed op stream in front of it
would encode twice, allocate once more, and move the driver-side encode inside the submit lock, which is the
one serialised point in the frame. Phase 2's section 16 predicted this before either phase-3 draft existed,
phase 3 confirmed it, and Metal gives no new reason to revisit it.

- `Begin()` takes a command buffer from the queue, retains it, and resets the recorder's tracked state:
  framebuffer, both pipelines, both dirty arrays, the pending-clear array, the vertex-stream records, the
  index-buffer record, and the viewport and scissor marks. It additionally waits on the ring's frame slot.
- Encoders are opened lazily and exactly one is open at a time, which is Metal's own rule rather than a policy
  this design invents. Three helpers own the transitions and every command routes through one of them
  (`EnsureRenderEncoder`, which may return false only on a genuine framebuffer failure per M-W5's orphan-target
  rule, `EnsureBlitEncoder` and `EnsureComputeEncoder`), plus their three `EnsureNo*` counterparts.
- `End()` closes any open encoder, flushing pending clears through a begin-and-end pair if there were any and
  no draw came (M-A3).
- `Submit` encodes the timeline signal (M-F1), adds the completion handler that reads `status` and `error`
  (M-F2), and commits, all under `_submitLock`.

**There is no command-buffer pool to reset (M-R2).** Vulkan needed `FramesInFlight` `VkCommandPool`s per list
because a command buffer's memory is the pool's and a pool cannot be reset while its buffers are in flight.
Metal's queue owns that allocation and hands out a fresh buffer each time, and there is no reset, no pool
object and no allocator to choose between. So the `FramesInFlight` gate exists here for exactly ONE reason, the
uniform ring's segment recycling, and it lives on the ring's acquire alone. `BackpressureStallCount` therefore
means one thing on this backend where it means two on Vulkan, which is a simplification worth stating because
that member's doc comment now carries both meanings.

**The queue's own bound is named rather than assumed away.** `MTLCommandQueue` has a maximum number of
UNCOMMITTED command buffers and `commandBuffer` BLOCKS when it is reached. That is a real bound with a real
block and it is not the ring's. Two things keep it out of reach rather than relying on it: `Begin` waits on the
ring's frame slot first, which bounds how far ahead the frame loop can get, and a device-free test asserts the
backend never holds more uncommitted buffers than `FramesInFlight` plus one, the one being the present buffer
M-W6 keeps. A blocked `commandBuffer` would present as a frame-loop stall with no counter attached, which is
the shape section 16 exists to keep off the list.

**Concurrent recording (M-R3).** N lists record concurrently and genuinely, because each holds its own command
buffer and its own encoders, and **this design has no shared record-time state at all**: no layout tracker
(M-H3), no barrier batch, no device state cache. That is V-R4's property obtained from Metal's own object model
rather than from a barrier design. The PORTABLE seam contract stays at one open recording per device, and
`IGpuCommandList.Begin`'s XML doc gains a Metal sentence naming the property that makes it true here, in the
shape the Vulkan sentence already has. That doc currently says the same code "on Metal" is a half-recorded
frame or a corrupted one, which is true of the Veldrid Metal leg and becomes false for this backend on the day
it ships, so the edit is owed rather than optional. And the same decay warning applies for the third time:
`OpenListTrackingGpuDevice` passes trivially on this leg and is NOT evidence about this backend.

### 6.2 Encoder-scoped state, the fact everything else follows from (M-R4)

Metal's argument tables, bound pipeline state, viewport and scissor are properties of the ENCODER rather than
of the command buffer. Ending a render encoder discards all of it. The incumbent already behaves this way and
it is not a choice either implementation makes.

**And the incumbent's version of it is incomplete, which is 2.1's finding.** `EndCurrentRenderPass` sets the
pipeline-changed flag, clears the active-set array and re-marks the viewport and scissor, and does NOT clear
`_vertexBuffersActive`. It is saved only by a second defect: `PreDrawCommand`'s vertex-buffer loop issues
`setVertexBuffer` when the flag is false and never sets it true, so the cache is permanently cold and every
stream is re-bound on every draw. **Porting the redundancy tracking without porting the invalidation ships a
corruption no golden would catch**, because the goldens do not restart a render pass mid-scene.

So M-R4 invalidates EVERYTHING at an encoder boundary: pipeline state, cull mode, front face, fill mode, blend
colour, depth-stencil state, depth clip mode, stencil reference, every argument-table entry, the viewport, the
scissor, every vertex stream and the index buffer. The device-free test is written BEHAVIOURALLY rather than as
a state assertion: record a draw, force an encoder end through a blit, record a second draw, and assert the
second draw re-issued its vertex-stream binds. It fails on the corruption rather than on the bookkeeping.

Three consequences the design is built around.

1. **The dirty model is encoder-scoped**, so re-activation at the first draw after any encoder boundary is
   mandatory, and a device-free test asserts it, because "we re-activated when we did not need to" and "we
   failed to re-activate when we did" are both invisible in a green suite otherwise.
2. **A record-time blit is expensive out of proportion to what it copies** (2.1), which is the ring's whole
   motivation.
3. **Encoder boundaries are a first-class thing to count**, which is why M-T2's budget sink counts them
   alongside argument-table writes and draws. Neither prior phase has this call class because neither API has
   it.

### 6.3 The schedule (M-R5 to M-R9)

1. `SetGraphicsResourceSet(slot, set)` and its dynamic-offset overload RECORD ONLY, into a per-slot record of
   `(set, engineDynamicOffset)`, marking the slot dirty when either differs from what is recorded. Two states.
2. `Draw`, `DrawIndexed` and `Dispatch` flush every dirty slot through the pre-command hook, then issue.
3. The flush assembles, per (kind, stage), a contiguous range of argument-table indices from the dirty records
   and emits ONE array call for it (M-R6). A full activation of the engine's model set is one buffer call, one
   texture call and one sampler call on the fragment stage plus one buffer call on the vertex stage.
4. A slot whose only change is its dynamic offset emits ONE `setVertexBufferOffset:` or
   `setFragmentBufferOffset:` per VISIBLE stage (M-R7), which is the shadow pass's shape thousands of times a
   frame.
5. `SetPipeline` on the pipeline already bound does nothing (M-R8). Otherwise it binds and then invalidates
   recorded slots only where the incoming program's INDEX TABLE differs from the outgoing one's (M-R9).
6. A slot whose recorded set has gone null is skipped.
7. Repeated dirty marks between two draws collapse to one flush, which falls out of an array of slots rather
   than a list of binds. Phase 2's rule 8 is the same requirement for the same reason.
8. Any encoder boundary invalidates everything (M-R4).

**The pipeline-state block.** A pipeline change drives one block of calls (`setRenderPipelineState`,
`setCullMode`, `setFrontFacing`, `setTriangleFillMode`, `setBlendColor`, and when the framebuffer has a depth
target `setDepthStencilState`, `setDepthClipMode`, `setStencilReferenceValue`), reproducing the incumbent's
`PreDrawCommand` INCLUDING the depth-target guard. The guard is not cosmetic: `setDepthStencilState` with a
null state on a pass with no depth attachment is a validation error under `MTL_DEBUG_LAYER`, which M-T7 arms on
every run.

**Vertex streams get a cache that is actually maintained.** `SetVertexBuffer(slot, buffer, offset)` marks the
stream dirty when the buffer or the offset differs, and the flush issues one `setVertexBuffer` per dirty
stream, invalidated wholesale by M-R4. The incumbent pays one per stream per draw unconditionally (2.1), so the
native per-draw marginal is strictly LOWER and the budget test freezes the lower number. **That marginal is a
REGRESSION target rather than a parity target**, and it is worth naming: a future change reintroducing the
unconditional bind is a red test rather than an invisible cost.

### 6.4 The budget seam (M-T2)

The device-free budget test needs a seam, and the interop layer's calls are static P/Invoke. The seam is a
narrow `IMetalEncoderSink`, generic-constrained to a struct so the JIT monomorphizes it away exactly as the
D3D11 emitter and the Vulkan `IVkCmdSink` are, covering exactly three call classes:

- **Argument-table writes**: the array setters and the offset setters, split by stage.
- **Draws and dispatches.**
- **Encoder boundaries**: the begin and end of each encoder kind.

Everything else goes straight to the interop layer with no indirection, because none of it scales per draw:
clears (which are descriptor fields rather than calls), copies, mip generation, resolves, and the
pipeline-state block. Phase 3's row-12 correction is inherited rather than rediscovered: a device-free
assertion about an EMISSION needs a line to interpose on, so the render-encoder begin and end pair and the
viewport and scissor setters sit on their own plain-handle `IMetalRenderApi`, and nothing on that seam is
frozen as a marginal.

**Aiming this at either neighbour's call classes would have been the mistake, twice over.** D3D11's fan-out
class is one call per resource per stage through an array setter. Vulkan's is per-draw descriptor set
allocation and per-draw `vkUpdateDescriptorSets`, and Metal allocates no descriptor of any kind. Metal's is
argument-table writes AND an encoder boundary per record-time upload, and the second has no analogue anywhere
else in the program. A budget ported from either predecessor would pass green while a record-time
`UpdateBuffer` split the encoder a thousand times a frame.

### 6.5 What is not here

No parallel render command encoders: the seam has no sub-list concept and multi-threaded recording is not a
shipped feature (W5's position, unchanged for the third time). No indirect draws: the seam has no indirect
draw and the incumbent's `DrawIndirectCore` loops issuing one draw per indirect element anyway, which is not
what the API is for. No `commandBufferWithUnretainedReferences`: it removes exactly the retain M-H3 depends on
for safe mid-flight disposal, in exchange for a retain-release pair per referenced resource, and taking it
would put back the retire list this design does not need.

---

## 7. Passes, clears and the viewport

### 7.1 The deferred begin (M-A1 to M-A5)

State per list: the bound framebuffer, a pending clear value per colour attachment plus one for depth and
stencil, and whether a render encoder is open.

- `SetFramebuffer(fb)`. If an encoder is open, end it. If the OUTGOING framebuffer had pending clears and no
  draw happened, force a begin-and-end pair to flush them (M-A3, and the incumbent forces exactly this in
  `SetFramebufferCore`). Record the new framebuffer, clear the pending array, mark the viewport and scissor for
  emission (M-A6).
- `ClearColorTarget(i, rgba)` and `ClearDepthStencil(d)`. If no encoder is open, store the value as pending,
  which becomes `loadAction = Clear` with that clear value on **attachment `i`** (M-A2). If an encoder IS open,
  end it and store the value as pending, which is what the incumbent forces through its `EnsureNoRenderPass`
  call in `ClearColorTargetCore` and is the behaviour a golden may depend on.
- First draw. Build the `MTLRenderPassDescriptor` from the framebuffer: per colour attachment,
  `loadAction = Clear` with the pending value if there is one and `loadAction = Load` otherwise, and
  `storeAction = Store` set EXPLICITLY rather than left to the descriptor default (M-A4). Depth and stencil the
  same, with the stencil attachment populated only when the depth format carries stencil, which is the
  incumbent's `FormatHelpers.IsStencilFormat` guard. Open the encoder, emit the viewport and scissor if marked,
  then the draw.
- `End()`, or any command illegal inside a render encoder (M-A5): end the encoder, flushing pending clears
  through a begin-and-end pair if there were any and no draw came.

**Metal's render pass descriptor is what phase 3 had to reach dynamic rendering to get**, and it has carried
load and store actions since Metal 1. `MTLRenderPassDescriptor`'s per-attachment `texture`, `loadAction`,
`clearColor` / `clearDepth` and `storeAction` map onto `VkRenderingAttachmentInfo`'s `imageView`, `loadOp`,
`clearValue` and `storeOp` almost member for member, so V-A1 through V-A6 port with Metal nouns and no
argument. That is #531's prediction about Metal and Vulkan mapping onto each other holding up.

### 7.2 The per-attachment clear (M-A2)

This is the one place this design deliberately renders differently from the incumbent, so it gets its own gate,
its own switch and its own deadline. 2.4 is the argument and 2.1 is the evidence.

The fix is one index. The consequence is that `ModelFB`'s normal and linear-depth attachments start being
CLEARED where today they LOAD. Whether that moves a pixel depends on what those attachments contain at the
start of a golden capture, which is a freshly created `StorageModePrivate` texture that nothing has written.
That is precisely the "undefined is not stable across runs" case V-F8 and V-A6 both legislate against, and it
means the CURRENT behaviour is the unstable one.

`KE_METAL_CLEAR=attachment0` reproduces the incumbent exactly, for the A/B on the first golden run. By V-RO4's
sort it selects a branch inside one implementation, so it is cheap, and its deadline is GATE 1: once the
goldens have answered, the switch is removed and the losing branch deleted whichever way it goes.

**And the renderer-side comment is a doc task with an owner.** `ModelRenderer.BeginModelPass` currently tells
the next reader that Metal collapses MRT clears, which will be false on the native leg and is an incomplete
description of the Veldrid one. It is reworded to name the implementation it describes, which is V-C3's
precedent for exactly this kind of stale mechanism comment.

### 7.3 The viewport and the scissor (M-A6, M-A7)

There is no `SetViewport` on the seam. The engine gets a viewport because Veldrid's base
`CommandList.SetFramebuffer` auto-calls `SetFullViewports()` and `SetFullScissorRects()`, wrapped in an
`if (_framebuffer != fb)` identity guard. **Both halves must be reproduced**, for the third time in this
program. A backend that does not emit rasterises nothing. A backend that emits UNCONDITIONALLY diverges on the
shipped sequence `SetFramebuffer(fb)`, `SetScissorRect(...)`, draw, `SetFramebuffer(fb)`, draw, where the
second bind silently restores the full scissor and the second draw renders outside the intended rectangle.
That is golden-visible, and phase 2's first spec froze the wrong behaviour into its tally test.

**Metal adds a third half, and it is the incumbent's own.** `PreDrawCommand` flushes the scissor only when
`_graphicsPipeline.ScissorTestEnabled`, so a pipeline with scissor test off never receives a scissor rect at
all. Metal has no scissor-test enable (the rect is always live, defaulting to the full attachment), so the gate
is the backend honouring the SEAM's own rasterizer state rather than the API's, and D3D11 honours the same flag
through a real enable bit. Reproducing it keeps the three backends agreeing, and NOT reproducing it would make
a scissor set before a pipeline with the test off apply on Metal and not on D3D11. It is reproduced, with a
device-free assertion.

**The plural setters are used unconditionally (M-A7).** The incumbent's `FlushViewports` picks between
`setViewports:count:` and the singular form on `IsSupported(macOS_GPUFamily1_v3)`, which is a deprecated-enum
read on the hot path to choose between two calls that do the same thing at count 1. The seam has no
multi-viewport concept, so the count is always 1 and one code path is the answer.

**And Metal needs no clip-space trick at all.** `IsClipSpaceYInverted` is false and `IsUvOriginTopLeft` is
true, so `GpuCapabilities.ClipSpaceYInverted` is false and `GpuClip.Correct` is the identity. Vulkan needed a
negative viewport height to reach the same answer and it was the single most consequential line in that design.
Here it is free, and saying so is worth a sentence because a reader coming from phase 3 will look for the
trick.

No `SetViewport` member is added to the seam. Phase 2 counted 48 `SetFramebuffer` sites and zero viewport
sites, phase 3 confirmed it, and it has not changed. It remains a reasonable addition when the seam is being
revisited for its own reasons, and this is the last backend, so "when the seam is being revisited anyway" no
longer has a scheduled occasion.

---

## 8. The binding model

### 8.1 Three index spaces, and what holds the mapping

Metal gives a function three independent index spaces, `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]`,
each per stage, with at least 31 buffer entries per stage. There are no sets, no layouts and no descriptor
objects, so `IGpuResourceLayout` and `IGpuResourceSet` are pure engine bookkeeping on this backend and hold no
native handle at all. That is the incumbent's shape and it is right: there is nothing to allocate and nothing
to write.

| Seam | Metal | Index from |
|---|---|---|
| `IGpuResourceLayout` | nothing native, a binding-info array | engine |
| `IGpuResourceSet` | nothing native, a resource array | engine |
| `GpuResourceKind.UniformBuffer` | the buffer table | **the emitted MSL** (M-B1) |
| `StructuredBufferReadOnly` and `ReadWrite` | the buffer table | **the emitted MSL** |
| `TextureReadOnly` and `ReadWrite` | the texture table | **the emitted MSL** |
| `Sampler` | the sampler table | **the emitted MSL** |
| `GpuResourceLayoutElement.Dynamic` | the `offset` argument, or `setBufferOffset:` on a rebind | engine |
| a vertex stream | the buffer table, from index 30 downward | **the engine, pinned at the top** (M-B2) |

### 8.2 The table, and how it is built (M-B1, M-S5, M-T3)

At `CreateShadersFromSpirv` the backend holds the MSL text for both stages, because it produced it through
`SpirvCrossCompile`'s new MSL half. For each stage it:

1. finds the entry point by its qualifier (`vertex`, `fragment` or `kernel`) and reads its NAME rather than
   assuming one (M-S5). SPIRV-Cross renames the GLSL `main` because `main` is reserved in MSL, and the
   incumbent looks a function up by a name Veldrid supplies from a layer this backend does not have.
2. scans the argument list with a DEPTH-MATCHED parenthesis walk, because every argument carries an attribute
   of its own and a naive scan stops inside the first `[[buffer(0)]]` and sees one argument. This is the exact
   failure `ShaderValidation.BufferKindsFromEntryPoint` already documents and already solves.
3. records, per argument, its index and its space, keyed by the resource NAME the argument carries.

Resource-set activation then looks a layout element up by name in the table and binds at the index it finds. An
element with no entry in the table for a given stage is NOT bound for that stage, which is correct by
construction: SPIRV-Cross omits an argument a stage does not reference, and binding one anyway is what an
index-counting backend does that produces the off-by-one.

**The name is the join, row 1's spike checks it, and 2.2 rules that the fallback is NOT a per-space ordinal.**
SPIRV-Cross emits argument names derived from the GLSL block or variable name, and the engine's reflection
(`ShaderReflection` through `SpirvCrossCompile.Reflect`) already carries a `Name` on every
`GpuResourceLayoutElement`. The spike compiles every shipped program to MSL in process and checks the join for
every element. If it does not hold, the fallback is to reproduce the incumbent's arithmetic exactly, ship M-T3
as a DETECTOR rather than as an assertion, and file the numbering fix behind a real SPIRV-Cross binding.

**The index-table test (M-T3).** Device-free, on every `dotnet test`, over every shipped program: build the
table, pair it against the pipeline's layout array and each layout's element array, and assert every element
resolves to exactly one index in exactly the spaces its declared stages need. Plus M-B2's no-collision
assertion. The failure this catches is "everything compiles and every pixel is wrong", which is what S2 and
V-S8 both exist for, arriving through the one door Metal leaves open.

**The table is also the invalidation authority (M-R9).** Two pipelines whose tables are identical invalidate
nothing on a switch, and the comparison is a handle compare once the tables are content-deduplicated. That is
the fine comparison 2.7 rules for, and it is only computable because the table exists as an object rather than
as arithmetic re-derived per bind.

### 8.3 Vertex streams take the top of the space (M-B2)

The one real collision in Metal's model is that vertex STREAM buffers and resource buffers share the
`[[buffer(n)]]` space of the vertex stage. `ResourceBindingModel` is the fork's answer: `Default` puts vertex
buffers at `0..V-1` and shifts resource buffers up by `V`, `Improved` puts resource buffers at `0..B-1` and
shifts vertex buffers up by `B`. Either way one numbering depends on the other's COUNT, in two places
(`MTLPipeline`'s vertex descriptor and `MTLCommandList`'s `setVertexBuffer` index), and getting them out of
step binds a vertex buffer where a uniform should be.

**Reproducing `Improved` would be unsound under M-B1, which is why 2.7's companion ruling lands here.**
`NonVertexBufferCount + i` assumes the resource buffers occupy `0..NonVertexBufferCount-1`, which is exactly
the CPU-side count M-B1 removes as the authority. The count is the engine's belief about where the resource
buffers went, and the table is where they actually went.

So vertex streams are pinned at the TOP: buffer index 30 for stream 0, 29 for stream 1, and so on downward.
Resource buffers grow from 0 upward wherever the emission put them. Neither depends on the other's count, the
two can only collide if a pipeline declares more than 31 combined bindings on one stage (asserted at pipeline
creation with a named exception), and `ResourceBindingModel` stops being a concept the engine has (M-B3).

**And this changes no pixel, which is what makes it free.** A vertex stream's buffer index is invisible to the
emitted MSL, which reaches vertex attributes through `[[stage_in]]`. The index only has to agree between the
`MTLVertexDescriptor`'s layout index and the `setVertexBuffer` index, both of which this backend owns. A
device-free test asserts that no index M-B1 read out of any vertex function reaches 30 downward for that
program, so the no-collision property is a checked fact rather than an inherited assumption.

### 8.4 Declining argument buffers, heaps, bindless and `setBytes` (M-B6)

The idiomatic prior wants all four, so each decline needs an argument rather than an omission.

**Argument buffers.** They are available on every device the fleet targets, so the support matrix does not
decide this. **There is no consumer**: argument buffers exist to remove per-material bind traffic from
renderers that bind hundreds of distinct material sets per frame, and this engine's per-frame binding traffic
is dominated by OFFSETS-ONLY rebinds of ONE set, which cost one call each under M-R7 and which argument buffers
do not improve. Phase 2's measured D3D11 shape says the same from the other side (2 calls per draw, 4 per
distinct mesh). Every route to them is `msl_options.argument_buffers`, which changes the emission for every
program at once, puts the 36 committed `metal` goldens in play, and destroys M-S3's byte-equality parity claim
in the same move. And they interact badly with M-B1: an argument-buffer emission moves resources off the entry
point's signature and into a struct, so the table would no longer be there to read. **Trigger that reopens
it**: a consumer needing per-draw material variety beyond one dynamic offset, which today means a texture-array
atlas the splat terrain cannot express. That is the SAME trigger phase 3 named for descriptor indexing,
deliberately, because the two would be reopened by the same consumer and should be reopened together.

**Heaps.** Declined and LINKED to M-H1 rather than decided separately, per 2.6: heap resources are
hazard-UNTRACKED by default, so a heap costs the entire barrier subsystem this design deletes, in exchange for
suballocation a workload that allocates at load time does not need. If a heap ever arrives, a tracker arrives
with it in the same change.

**Bindless, indirect command buffers and tile shading.** Declined together for one shared reason: each is a
Metal-native capability with no seam member behind it, so taking one means either inventing public API with one
backend behind it or having the backend silently do something the other two do not. Both are worse than the
plain calls that already work.

**`setBytes` as the uniform path**, which is the one the platform prior most obviously wants to answer yes to.
`setVertexBytes:length:atIndex:` copies caller bytes straight into the encoder's command stream, so there is no
`MTLBuffer`, no per-frame segment, no completion gate, no backpressure counter, no #484 every-segment rule and
no stride invariant. It would delete the entire ring subsystem, the biggest single line item in both prior
phases. **It does not fit this seam.** The seam hands the backend a BUFFER: `CreateResourceSet` pins a
`GpuBufferRange(buffer, offset, size)` at load time across 68 call sites, renderers call
`UpdateBuffer(buffer, offset, data)` at record time and off timeline, and then bind the set with a dynamic
offset. To reach `setBytes` the backend would have to keep a CPU shadow of every ring-backed buffer, have
`UpdateBuffer` write into the shadow, and copy `size` bytes out of the shadow at every bind per stage. That is
a memcpy per bind per stage where the ring writes an integer, on a hot path that is thousands of offsets-only
rebinds per frame. **And the 4 KB cap is a content cliff rather than a design constraint**: the engine's
shipped uniform windows are already in the four-figure range (phase 2 records 1008, 1040 and 1120 bytes at
three sites), and a combined per-draw UBO carrying a bone array is exactly the shape that crosses it. A limit
whose breach is a runtime throw triggered by content is worse than a limit that does not exist. Trigger that
reopens it: a seam member that hands the backend BYTES rather than a buffer range, which is the push-constant
concept the seam does not have and which V-D8 declined on Vulkan for its own reasons.

---

## 9. Memory and the uniform ring

### 9.1 There is no allocator, and that is the whole of it (M-M1, M-M2)

Metal owns device memory. `newBufferWithLength:options:` and `newTextureWithDescriptor:` ARE the allocations,
and there is no memory-type enumeration, no `bufferImageGranularity`, no chunk pooling, no free list, no
dedicated-allocation heuristic, no `maxMemoryAllocationCount` to stay under, and no flush or invalidate.
**The entire subject of phase 3's section 9.1, three decisions, a work-breakdown row and a conditional VMA
rejection, has no occupant here.** V-M1 through V-M4 are absent, and the absence is a simplification rather
than an omission.

What replaces the memory-type choice is the STORAGE MODE, and there are three answers rather than a search:

- Buffers the CPU writes every frame (the uniform rings, the staging arenas, staging buffers) are
  `MTLStorageModeShared`, whose `contents()` pointer is stable for the buffer's life and visible to both sides.
  That is the incumbent's choice for every buffer it creates and it is the right one on unified memory, where
  the CPU and the GPU address the same pages and a write is a write.
- Buffers the CPU writes once at load (vertex, index, static structured) are ALSO `Shared` in v1, reproducing
  the incumbent exactly. `Private` plus a blit would be faster on a discrete Mac GPU and identical on Apple
  Silicon, and choosing differently here would change what a load-time upload costs inside the gate that must
  isolate the backend swap. Filed as a follow-up with the discrete-Mac case as its named consumer, and section
  16 records that the fleet has no discrete Mac to measure it on.
- Textures are `Private` and staging textures are a `Shared` buffer, reproducing the incumbent (M-M2, M-C5).

**The Intel Mac case is recorded rather than built.** On a discrete Mac GPU, Shared memory is uncached system
memory the GPU reads across PCIe, and `StorageModeManaged` with an explicit `didModifyRange:` is the correct
shape. The incumbent uses Shared universally and the fleet target is Apple Silicon, so parity says Shared and
the Managed path is a follow-up with a named trigger (a consumer report from an Intel Mac).

**Hazard tracking mode is left at the default on every allocation**, which is tracked, per M-H1, and no
`MTLHeap` is created anywhere, per 2.6's linkage.

### 9.2 The ring (M-M3 to M-M7)

Every `UniformBuffer`-usage buffer is ONE `MTLBuffer` of `stride * FramesInFlight` in `MTLStorageModeShared`,
where `stride = align(size, 256)` and `FramesInFlight = 3`. Its `contents()` pointer is taken once at creation
and kept. The `IGpuBuffer` identity NEVER changes and the frame base is applied AT BIND.

- A record-time `UpdateBuffer(buffer, offset, data)` is `memcpy(contents + frameBase + offset, data, n)`. No
  staging buffer, no blit, **no encoder split**, no allocation, no release.
- Every bind of a ring-backed uniform supplies `frameBase + rangeOffset + callerDynamicOffset` through the
  `offset` slot of its array call, or through `setBufferOffset:` on an offsets-only rebind (M-R7).
- Frame N uses segment `N % FramesInFlight`. Before handing out a segment the ring reads the timeline value the
  frame that last owned it recorded and blocks if it has not been reached, counting the stall into
  `BackpressureStallCount` and `BackpressureStallMs`.

**Why the stride is 256 and not the device's 16 (M-M3).** The incumbent's
`GetUniformBufferMinOffsetAlignmentCore` answers `MetalFeatures.IsMacOS ? 16u : 256u`, so a device-derived
stride would pack tighter on macOS. Three reasons not to. The seam already documents 256 as the safe alignment
across Metal, Direct3D 11 and Vulkan, on `SetGraphicsResourceSet`'s dynamic-offset overload and on its compute
twin, and every shipped renderer already writes 256-aligned slots. 256 is the spec-required maximum for the
equivalent Vulkan limit, so flooring there is what makes one number govern all three rings and one shared
policy test assert it, which is V-M5's own reasoning reaching the same floor from the other direction. And a
device-derived stride makes the ring's arithmetic a function of the machine, which puts a device-shaped number
under a golden-bearing path for a memory saving the fleet does not need. The device's own minimum is READ and
asserted at or below 256 by M-N4's probe, so a future device that raised it fails loudly instead of corrupting
quietly.

**The one invariant that shrinks (M-M4).** D3D11 owes a 16-constant round-up on both the first-constant and the
count, and its first version shipped the wrong one and silently dropped binds. Vulkan owes
`rangeOffset + callerDynamicOffset + range <= stride` against a VUID, because a descriptor carries a range.
Metal's `setBufferOffset:` carries no length at all, so what remains is
`frameBase + rangeOffset + callerDynamicOffset + size <= (frame + 1) * stride`, which is the same invariant with
nothing to violate it except arithmetic. It is asserted device-free over every shipped set shape anyway,
because it is the Stride row of section 9.4's inventory and this backend owns its own. **That is the simplest
of the three rings and the asymmetry is worth naming**: D3D11 pays a map window plus a 16-constant count,
Vulkan pays a positional `pDynamicOffsets` array plus a bind-window range, Metal pays neither.

**Why the ring is worth more here than on either predecessor (2.1).** On the incumbent a record-time uniform
write costs an `MTLBuffer` allocation, a `memcpy`, an ENCODER SPLIT, a blit, a release, and then a full
graphics state re-activation at the next draw. Under the ring it costs a `memcpy`. **The saved work is not the
copy, it is the encoder.**

**And it is a CORRECTNESS change here, which it was not on D3D11 (M-M7).** `MTLGraphicsDevice.UpdateBufferCore`
is an unguarded `memcpy` into `contents()` and nothing checks whether a submitted command buffer is reading
those bytes. D3D11's `MAP_WRITE_DISCARD` gives the driver licence to rename a buffer under a write and Metal
renames nothing, so this is a plain data race in shipped code, and the ring's completion gate is what removes
it. Automatic hazard tracking does not help: it orders GPU work against GPU work and says nothing about a CPU
write racing a GPU read, which is why M-H1 does not make this gate redundant.

**#484 adopted wholesale (M-M5).** An off-timeline write reaches EVERY segment, gated on the same completion
read, with a non-current segment deferred as a PENDING PATCH applied at its next acquire rather than waited on,
and the current segment always written ungated. Record-time writes stay current-segment only. It cost a
consumer defect to learn once, and the non-terminating retry loop drafted before it is the thing not to
re-invent: a loop that waits for every non-current segment at once never terminates in the GPU-bound steady
state.

**U3's two invariants verbatim (M-M6).** Only `UniformBuffer`-usage buffers are ring-backed, so a structured
buffer's own binding stays correct. And a ring-backed buffer never receives a non-uniform binding: a buffer
created `UniformBuffer | StructuredBufferReadOnly` or with either read-write structured bit throws at CREATION.
That combination is vacuous in the engine today and legal on the seam, and both Veldrid backends accept it, so
it is a **backend-divergent creation failure** documented as one in the package README rather than discovered
by a consumer.

### 9.3 Bulk uploads, textures and views (M-M8 to M-M10)

Record-time `UpdateBuffer` on a NON-uniform buffer, and record-time `UpdateTexture`, write into a per-list
`MTLStorageModeShared` STAGING ARENA (persistently mapped, sub-allocated, recycled when the list's timeline
value is reached, pooled by size with a real retention cap) and encode a blit copy. **They still split the
encoder**, which is exactly why the ring exists and why moving uniform writes off this path is the whole win.
They are bulk and rare relative to the uniform sites. The incumbent's per-call allocate-and-release is replaced,
which its own TODO asks for.

**Texture creation issues no command buffer (M-M9), so V-M10 has no occupant** and the Vulkan phase's
two-hundred-submits-per-scene-load finding does not transfer. The undefined-initial-contents question phase 3
answered with a deliberate clear is answered here by parity: the incumbent does not clear, the 36 `metal`
goldens are green under that, and adding a clear would change what a render target reads before anything writes
it.

**What DOES transfer is device-level `UpdateTexture` on a non-staging texture**, which creates a staging
texture, a command list and a whole queue submit, then disposes both. That moves onto a device-owned SETUP
command buffer under a short setup lock, flushed lazily at the next submit OR at any device-level read (`Map`,
a readback, an explicit drain). The read-path flush is what makes the claim true without a hole, and it is
V-M10's mechanism applied where it is genuinely needed rather than ported wholesale.

**`CopyBuffer` alignment.** The incumbent routes a copy whose source offset, destination offset or size is not
a multiple of 4 through an embedded compute shader driven by a dedicated compute pipeline. This design
reproduces the size-rounding half, which is a `(4 - size % 4) % 4` pad the incumbent already applies on the
aligned path, and THROWS with a named exception on a genuinely unaligned OFFSET. A device-free test over every
`CopyBuffer` call site asserts none produces one, so nothing legitimate reaches the throw. Shipping a second
embedded metallib and a second compute pipeline for a case no consumer produces is the kind of unreachable-code
reproduction G1 already declined once, and the follow-up is filed with the throw as its trigger.

**Every view is created at RESOURCE creation (M-M10)**, from the declared usage bits, following the incumbent's
rule that a view object is created only when the description actually narrows the target (a non-zero base mip
or layer, a partial range, or a different format) and the target's own texture is used otherwise. The
enforcement is STRUCTURAL rather than a counter, in the shape V-D2 and V-M11 used: no view factory is reachable
from the recording type, asserted by an architecture test over the type graph, so a draw-time view is a compile
error. X1's evidence is why, and it is worth restating in a Metal seat where `newTextureView` looks cheap
enough to do at a bind: all 25 `DEVICE_REMOVED` stacks in #423 surfaced inside the lazy view constructor on the
draw path.

### 9.4 The ring policy inventory, third column

Phase 3's section 9.4 wrote the policy out as a ten-row checklist with an Owner column, because a decision not
to share code is not a decision to re-derive the policy from memory. M-P5 adds the THIRD adapter to the shared
semantic tests, so the seven shared rows become assertions about three implementations. The three
backend-owned rows stay backend-owned and their reasons hold here.

| Policy | What it means here | Owner |
|---|---|---|
| Segment selection | Frame N uses segment `N % FramesInFlight`. The base is applied at BIND and never baked into a resource set | Shared tests |
| Fence gating | A segment is acquired only after the timeline value the frame that last owned it recorded has been reached | Shared tests |
| Backpressure | A blocked acquire increments the stall count and accumulates stall time | Shared tests |
| Off-timeline reach (#484) | A device-level write reaches EVERY segment | Shared tests |
| Off-timeline gating | The added segments are gated on the same completion read. The CURRENT segment is ungated and always written | Shared tests |
| Off-timeline never blocks | A segment failing the gate is queued as a PENDING PATCH applied at its next acquire, never waited for | Shared tests |
| Record-time writes | Stay current-segment only | Shared tests |
| Ordering | The ring cannot recycle safely before the completion primitive exists. Row 5 (the timeline) is a prerequisite of row 8 (the ring), which is the dependency edge phase 2's first spec dropped | Backend's own. A BUILD-ORDER fact, enforced by the work breakdown's row order |
| Lock legality | Because the off-timeline path never waits, a caller already holding `_submitLock` is legal | Backend's own. Each backend has its own lock and its own deadlock to not have |
| Stride | The stride is the SPACING of the segments. Here it is `align(size, 256)` and the offset is a plain argument, so there is no bind window and no VUID to satisfy (M-M4) | Backend's own. The arithmetic differs where the invariant does not |

**That is the shape of the answer to #531 for the ring, and it is why the ring's CODE is not extracted (2.8).**
The policy is shared and executable. The mechanism is three different things.

---

## 10. Synchronisation

### 10.1 The timeline (M-F1 to M-F5)

One `MTLSharedEvent` created at device creation, initial value 0, owned by the device. Every `Submit` calls
`encodeSignalEvent:value:` on the command buffer with the next value before committing. `IGpuFence` holds a
target, `Signaled` is `signaledValue >= target` (a non-blocking property read, which is exactly what the seam
demands), and `Reset()` clears the target so the fence can be handed to a later submit.

**What this deletes is most of the point.** The incumbent's fence path is a hand-built `BlockLiteral` and
`BlockDescriptor` allocated with `Marshal.AllocHGlobal`, an invoke pointer from
`Marshal.GetFunctionPointerForDelegate`, a `_NSConcreteGlobalBlock` isa loaded out of `libSystem.dylib` by
name, a lock plus a dictionary lookup INSIDE the driver's completion callback, a second process-global
dictionary and static callback for AOT targets, and a `ManualResetEvent` per fence with a pooled array of them
for `WaitForFences`. One shared event replaces all of that.

**What it does NOT delete is the completion handler, and this is where both drafts were incomplete (M-F2).**
M-G4 requires reading `MTLCommandBuffer.status` and `.error` at completion in every configuration, so a handler
is registered per submitted command buffer whatever the fence primitive is. The ruling is therefore to take
both, with the responsibilities split cleanly: **the shared event owns ORDERING and the handler owns
REPORTING.** The handler takes no lock, touches no dictionary, sets no event, and carries no ordering
responsibility at all, which is the answer to the observation that completion callbacks are delivered on an
arbitrary internal thread in no guaranteed order. A design that advanced a counter from that callback with `++`
would be depending on an unstated ordering fact, and a design that advanced it with an `Interlocked` maximum
would be correct and would still be re-deriving what the shared event gives for free.

The block itself is `[UnmanagedCallersOnly]` (M-F3), with no delegate and no GC handle. If row 1's spike finds
the block layout does not work that way, the named fallback is the incumbent's delegate-and-dictionary shape,
which is field-proven, and the design loses AOT-cleanliness on the completion path and says so.

**The seam's fence ordering becomes a theorem (M-F4).** The seam promises that a fence handed to a submission
made after some earlier work signals only once the queue has drained through it. A shared event's signal
operations from one queue execute in submission order and the values are monotonic, so the value reaching 6
requires the signal at 5 to have happened, which requires submission 5 to have completed. Polling a later fence
transitively covers every earlier submission, which is what `RetiredResourcePool` relies on and what M-M3's
segment gate reads. That is V-F2's argument, and it is the same argument because it is the same primitive.

**`SupportsCompletionFences = true` is PARITY.** `VeldridMap` already reports true for Metal, with a doc
comment explaining that Metal registers the fence against the command buffer and sets it from the completion
handler. Phase 2's C5 was an UPGRADE because D3D11's fence was a submit receipt. Nobody should look for that win
twice, and gate 2's skip criterion follows: the `RequiresCompletionFences` pair already RUNS on this leg, so the
criterion is NO NEW SKIPS rather than two fewer.

**`WaitForIdle` is `waitUntilSignaledValue:timeoutMS:` on the last submitted value (M-F5)**, counted into
`DrainCount` and `DrainMs`. The incumbent instead retains `_latestSubmittedCB` under a lock and calls
`waitUntilCompleted` on it, which needs the buffer kept alive to be read and gives nothing to count without
extra bookkeeping. There is no C6-style bet here: the incumbent's drain is already real, and phase 2's win was
in making an empty method body exist.

### 10.2 Hazards, and the machinery that is absent (M-H1 to M-H4)

Argued in 2.6. In one table, so a reader can see the size of what is not here.

| Phase 2 or phase 3 decision | Metal occupant |
|---|---|
| V-F6, `vkCmdPipelineBarrier2` with explicit stage and access masks | None. Automatic |
| V-F7, canonical resting layouts and list-local tracking | None. No layouts |
| V-F8, the `UNDEFINED`-discard determinism rule | None. No layouts |
| V-F9, deferred disposal behind a timeline value | None. A command buffer retains what it references |
| V-C1, a real image barrier at the sampled bind for rule 1 | None. The encoder boundary is the dependency |
| V-C2, a read-after-write barrier between dependent dispatches | None. Serial dispatch type |
| C1, SRV-versus-UAV auto-unbind in both directions | None. Not a binding-model problem here |
| V-M1's conditional VMA rejection | None. No allocator to reject one for (9.1) |

**No retire list, and it is a decision rather than an omission (M-H3).** An `MTLCommandBuffer` retains every
resource its encoders reference until it completes, so a resource disposed while a submitted buffer still
references it stays alive, and `release` at `Dispose` drops only the engine's own reference. V-F9's machinery,
which converts mid-life resource disposal racing queued async work from convention-safe to structurally safe,
is unnecessary here because Objective-C reference counting already does it. That is one of the four defects the
CI workflow header records as fixed engine-side, and a reader who finds no retire list here should find this
paragraph. **`commandBufferWithUnretainedReferences` is never used, and that is what the decision rests on**, so
a future reader reaching for it as an optimisation has to bring a retire list with it. What does NOT follow is
that `WaitForIdle` before disposal becomes pointless: the seam's callers keep it, because it is the seam's
contract and both Veldrid legs still need it.

**M-H4 is the one with a consequence beyond this backend.** Seam rule 1 (compute writes a storage texture, a
later graphics pass in the same list samples it) is satisfied by the API, and the seam's own comment already
names that mechanism for Metal and stays true. Seam rule 2 (a dispatch reading an earlier dispatch's writes) is
honoured AS WRITTEN with no seam member added, and this backend additionally satisfies it natively because
consecutive dispatches in one serial-dispatch compute encoder are ordered and tracked. **After this phase three
of three engine-owned backends honour rule 2 natively** (D3D11 tracks hazards, native Vulkan emits a real
barrier, Metal orders serially) and only the two Veldrid legs need the drain. That is the quorum #461 has been
waiting for and phase 3's VF10 already named as advanceable. It is EVIDENCE, not a contract change: rule 2 is
cross-backend, and a consumer that drops the drain because this backend tolerates it breaks on the backend its
machine falls back to.

### 10.3 The seam comment is now wrong in a third way

`GpuInterfaces.cs`'s rule 1 and rule 2 comment names mechanisms per implementation, which phase 3 corrected it
to do. Its Metal sentence is correct for both Metal implementations. Its rule 2 paragraph says a submit
boundary plus a device drain is the only ordering the seam can guarantee, then adds that the native Vulkan
backend is more permissive. **After this phase that paragraph needs a third arm**, and adding it is a doc task
with an owner, because R4's precedent is that an unwritten contract decays and a wrong written one decays
faster.

---

## 11. Swapchain, present, resize and threading

### 11.1 What is reproduced (M-W1)

The `CAMetalLayer` configuration is reproduced from the incumbent field for field, because it is visible only
to a human and W1's lesson binds hardest where nothing in CI runs: the layer acquired from the `NSWindow`'s
content view or created and attached when the view has no Metal layer with `wantsLayer = true`, `device`,
`pixelFormat` from `B8_G8_R8_A8_UNorm` or its sRGB sibling, `framebufferOnly = true`, and `drawableSize` from
the window's content size. The colour attachment is fetched from the LIVE drawable at descriptor-creation time
and the depth attachment is owned by the framebuffer.

**The Metal golden suite is headless and renders into offscreen textures, so not one line of `MTLSwapchain`,
`MTLSwapchainFramebuffer`, `nextDrawable` or `presentDrawable` runs in CI on any leg, ever.** That is MM7,
recorded as an observation so nobody reads a green golden leg as evidence about presentation, and it is worse
here in one respect than it was on Vulkan: the Metal leg is otherwise the best-covered leg in the matrix, so a
green Metal run reads as stronger evidence than it is.

**`IGpuFramebuffer` wrapper identity is stable across resize BY CONSTRUCTION (M-W7)**, because the colour
attachment is not an object the wrapper holds. W2 asked D3D11 to behave like Metal here and V-W5 had to build
it for Vulkan. Here there is nothing to build, and the DEPTH texture is the only part that is recreated.

### 11.2 What changes (M-W2 to M-W6)

**Vsync is applied unconditionally (M-W2).** 2.9 is the argument. The incumbent's three-value equality against
`MaxFeatureSet` means that on a machine outside that set, `SyncToVerticalBlank` silently does nothing.
`CAMetalLayer.displaySyncEnabled` is a macOS property on a macOS-only backend and needs no capability test.
Reproducing a fragility whose failure is silent is not parity, which is V-W2's ruling on the two Vulkan bugs
applied to a third. **And the frame-cap consequence is M-W3's**, which routes both `FrameCap.Resolve` and
`DisplaySettings.RequiresFrameCapWarning` through `IsMetal()` with the Metal arm as the conservative default
and gate 5's vsync toggle as the instrument that decides whether the arm is right.

**The drawable acquire keeps its timing and gets measured (M-W4).** `nextDrawable` is taken at the present
boundary for the NEXT frame, which is what makes the drawable known before recording starts, so nothing about
record-time framebuffer resolution changes. That is the same property V-W3 kept and the same reason. It BLOCKS
when no drawable is free, and unlike Vulkan's acquire there is no signal-a-semaphore variant, so the block is
not a synchronisation choice the way the Vulkan fence-wait was and it is not removable. **So this design does
not remove the stall, it moves it to the submit thread and COUNTS it**, into `AcquireWaitCount` and
`AcquireWaitMs`, the pair phase 3 appended to `GpuDeviceCounters` for `vkAcquireNextImageKHR`. This phase adds
nothing to the seam, which is a template paying out one phase later. `maximumDrawableCount` is set to
`FramesInFlight` so the depth of the drawable queue and the depth of the uniform ring are one number.
`CAMetalLayer.allowsNextDrawableTimeout` is a pacing knob that belongs to #380 with its own measurement, not to
the phase that must isolate the backend swap.

**A nil drawable stops discarding the frame silently (M-W5).** The wrapper is repointed at a device-owned
ORPHAN TARGET: one colour texture at the current drawable size clamped to a minimum of one by one, matching the
swapchain framebuffer's format and carrying its depth attachment, created lazily the first time this path is
reached and destroyed at the next successful `nextDrawable`. The frame records, submits and completes exactly
like any other frame, and only its present is skipped. `FramesBegun` counts it, because a skipped present is
not a skipped frame and it is the denominator every per-frame figure is divided by. Two rules make the wrapper
safe to bind at every instant, and they are V-W4's rules with Metal nouns: the wrapper's colour attachment is
repointed only on the submit thread at the present boundary with no recording in flight, and the orphan
target's lifetime is owned by the DEVICE rather than by the framebuffer, so a recording that bound it is not
left naming a destroyed texture.

**The present stays on its own command buffer (M-W6).** The idiomatic move is to encode `presentDrawable:` on
the frame's own buffer, which is the documented shape and removes a command buffer per frame, and the Vulkan
phase's corrected-in-flight semaphore routing (#563) gives the mechanism for free. It is declined for three
reasons. W1's lesson binds here harder than anywhere, because the swapchain is the one area with no automated
coverage in the whole net. The win is negligible and the risk is not: an `MTLCommandBuffer` is a cheap object
taken from a queue pool, and against one extra per frame, routing the present onto a frame's own buffer
inherits the Vulkan design's own NAMED limitation (a second list binding the swapchain framebuffer after the
first has ended discards what the first drew) into a backend where nothing measures it. And the ordering is
already correct, because `presentDrawable:` on a later-committed command buffer runs after the frame's by queue
order, which is the same guarantee the routed form gives. There is also a seam reason: `Present()` is a
separate call from `Submit()`, so the frame's buffer is already committed by the time `Present` runs, and
deferring the commit until `Present` would put a recorded frame's execution behind a call the consumer may not
make. **One extra command buffer per frame is a rounding error and the change is a submit-semantics change
wearing a performance costume.**

### 11.3 Resize (M-W7)

`ResizeSwapchain(w, h)` stores the pending size coalesced to the last requested and returns. A runtime
`SyncToVerticalBlank` change sets a pending flag too. Both apply at the next present boundary on the submit
thread, after draining the timeline, where the boundary provably owns the queue and no recording is in flight.
Applying means writing `drawableSize`, rebuilding the depth texture, swapping the new depth view into the
EXISTING wrapper so its identity survives, and taking one fresh drawable, so an ordinary boundary and a
resizing boundary leave the device in the same state.

The incumbent instead applies the resize inline on the calling thread: `MTLSwapchain.Resize` recreates the
depth texture (releasing the one in-flight frames may still be reading) and takes a new drawable, with no drain
anywhere. The Silk `FramebufferResize` callback fires on the render thread today, so nothing observable changes
in the shipped loop and the contract hardens against a consumer that does otherwise. That is W3's argument
verbatim.

**Metal needs no swapchain recreation**, only a `drawableSize` write and a depth rebuild, which is why the
seam's existing "a resize reconfigures nothing" wording already describes this backend and needs no edit. That
is a real difference from Vulkan, where a present-mode change forces a whole swapchain recreation, and
`docs/DEPENDENCY-SEAMS.md` already records the difference in the sentence phase 3 added.

### 11.4 Threading (M-W8)

- Recording is lock-free and per list. Any number of lists may record concurrently on any threads, because each
  owns its command buffer and its encoders.
- One `_submitLock` covers `commit`, `presentDrawable` plus its commit, and the resize apply. Held for
  microseconds, not a frame.
- The SETUP-BUFFER lock covers appends to the device-owned setup command buffer and its lazy flush. The flush
  takes the submit lock under it, in that order and never the reverse.
- The RING lock covers a segment acquire and an off-timeline write, scoped to the write and never to a frame. A
  caller already holding `_submitLock` is legal because the off-timeline path never waits (9.4).
- `Map` and `Unmap` on staging take the submit lock for the map call only, and `Map(staging, Read)`
  additionally waits on the timeline before returning the pointer (M-C6).
- Resource creation is otherwise free-threaded. `MTLDevice` factory methods are documented thread-safe, there
  is no `DriverConcurrentCreates` analogue to ask about, and V-W8's setup-buffer lock is present here only for
  the one path M-M9 gives it.
- Submit order is the observable order (M-N2), with the caveat the seam already documents in rule 2.
- `GpuDeviceContext._lifecycleGate` is unchanged. It was built for the Vulkan loader race, it also covers
  disposal, and it is not this backend's to remove.

Multi-threaded recording is STRUCTURALLY SUPPORTED and is not in the shipped contract, which is W5's position
unchanged for the third time. Nothing in the engine asks for it and no test exercises it.

---

## 12. Shader path

### 12.1 The MSL target lands in the back-end half (M-S1)

Phase 3 split `SpirvCrossCompile` along the seam it already had: `SpirvFrontEnd.ToSpirv` is the front end and
`VertexFragmentToHlsl` plus `ComputeToHlsl` are the back end. Its own file comment says in as many words that
this is the one place the SPIRV-Cross replacement changes for the D3D11 path, and phase 3's own text says the
split was carved out because the native Vulkan backend needs the front end ONLY. **That split was the entire
Metal-facing carrying cost of phase 3 and it was one file move**, and its payout is that this phase adds
`VertexFragmentToMsl` and `ComputeToMsl` beside the HLSL pair and touches nothing else.

They call `SpirvCompilation.CompileVertexFragment` and `CompileCompute` with `CrossCompileTarget.MSL` under a
private options set BUILT FROM a new `MslCrossCompilePin`, exactly as the HLSL pair is built from
`HlslCrossCompilePin`. The signatures stay Veldrid-free and return the same engine-owned `CrossCompiledPair`
and `CrossCompiledCompute` mirrors, so the backend reaches them across `InternalsVisibleTo` with no Veldrid
type crossing the boundary. The front end is untouched, so the SPIR-V byte-equality drift test and
`VulkanSpirvIncumbentParityTests` both keep meaning what they meant. The architecture test gains its third arm:
`KhaozEngine.Gpu.Metal` names the front end and the MSL members and never names an HLSL member.

### 12.2 #462 is not taken, and 2.2 is why (M-S2)

#566 banks that the shader back end grows an MSL target via the direct SPIRV-Cross migration, deliberately
deferred to this phase. **That input does not survive contact with the library.** `libveldrid-spirv` exports
three non-incidental C entry points, `CompileGlslToSpirv`, `CrossCompile` and `FreeResult`, and none of them
carries a resource-binding table. `spirv_cross::CompilerMSL::add_msl_resource_binding` is present as a C++
symbol and is not reachable through any supported ABI, on an object `CrossCompile` constructs internally and
never returns. So an engine-owned P/Invoke shim over that library gets exactly what the managed wrapper already
gets, because the managed wrapper is a thin P/Invoke over those same three exports.

Taking #462 in the sense that would matter here means linking SPIRV-Cross directly: a new native dependency
built per RID, with its own packaging story, inside the phase that is also introducing a hand-written
Objective-C interop layer. That is a bigger change than #462 was costed as and it is not this phase's.

**So `Veldrid.SPIRV` stays in the graph after this phase, for the front end and for BOTH back-end targets**,
and `libveldrid-spirv` is still a bundled native per RID, which is the packaging burden #420 partly exists to
reduce. This design does not reduce it and says so. Moving the whole toolchain onto engine-owned code is the
program's CLOSING ACT (section 19), and the three instruments that make it checkable already exist:
`VulkanSpirvByteEqualityTests` and `VulkanSpirvIncumbentParityTests` for the front end, and
`D3D11HlslByteEqualityTests` for the HLSL back end. Phases 2 and 3 built the regression net for a change
neither of them made.

### 12.3 Parity is TWO artefacts, not one (M-S3, M-S4)

The D3D11 byte-equality test carries a warning in its header that phase 3 transplanted and this phase
transplants again: its hash table is baked from that path's own emission, so what it detects is DRIFT, and **a
wrong emission baked once passes forever**. It compares nothing against the incumbent.

So:

1. **`MslCrossCompilePin`**, the options stated as constants with their citation, plus an `Identity` string
   built FROM those constants so a pin change moves every derived cache key by construction.
2. **A one-off in-process parity measurement against the incumbent's own path**, compiling every shipped
   program to MSL through both and asserting byte equality, TAKEN AND RECORDED in this document before the
   first golden run. That is the fact that licenses "no rebake" on the fleet's reference golden family.

**And phase 3's own upgrade is inherited: the measurement is ALSO a standing test.**
`VulkanSpirvIncumbentParityTests` makes the same comparison on every leg, so parity stopped being a fact about
one afternoon and became a fact about the current tree. `MetalMslIncumbentParityTests` does the same here, and
the reason to have both is the same: the equality is NOT true by construction, because `VeldridGpuDevice`
hands GLSL to the three-argument `CreateFromSpirv`, which constructs `new CrossCompileOptions()` and forwards
it, while this path compiles under `MslCrossCompilePin`. The two sets are maintained independently. A red run
there means the committed `metal` goldens are baked on one emission and asserted against another, and the
response is to decide which side moved rather than to re-bake the hash table.

**The pin's values, and why they are the library defaults.** `HlslCrossCompilePin` already records what
`CreateFromSpirv` does with its options: it forwards them verbatim and derives nothing from
`ResourceBindingModel`, which is not a member of `CrossCompileOptions` at all. So the incumbent's MSL is
emitted under the library defaults, and if `MslCrossCompilePin` states anything else the measurement fails and
the pin is what moves. `FixClipSpaceZ` and `InvertVertexOutputY` stay FALSE for the reason `HlslCrossCompilePin`
gives: they append a clip-space fixup to the emitted vertex shader and the engine already handles both
conventions through `GpuCapabilities`, so a fixup here would apply the correction twice, and on Metal that
would be immediately visible because `ClipSpaceYInverted` is false and `GpuClip.Correct` is the identity.
`NormalizeResourceNames` stays at its default too, and 2.2 rules that flipping it is not available as a lever
for M-B1's name join precisely because it would break this equality.

### 12.4 The compile options are pinned (M-S6)

`MTLShader` passes `MTLCompileOptions.New()`, a default-constructed object, so two defaults are currently
unstated facts about the runner rather than choices.

**`fastMathEnabled` defaults to on**, and fast math changes floating-point results. The committed metal goldens
were baked with it on. Pinning it to on is a no-op today and a guard forever, and flipping it is the kind of
change that moves every pixel with no other symptom.

**`languageVersion` defaults to the newest the OS supports**, which DRIFTS with the runner image. The workflow
already pins `macos-26` by number rather than to `macos-latest` so an image promotion cannot move the GPU under
a golden gate, and the language version is the same class of hazard one level up. Row 1 MEASURES what
`macos-26` reports and pins that value, so the pin is a no-op on the day it lands.

`MslCompilePin` holds both as constants with the citation and derives an `Identity` from them for the cache
key, in the exact shape `HlslCrossCompilePin` and `SpirvFrontEndPin` already use, so a pin change moves every
cache key by construction rather than by remembering. **`preserveInvariance` is left at its default (off)**,
matching the incumbent. It forces position computation to be invariant across pipelines, which matters for
multi-pass depth equality, and turning it on is a follow-up with a trigger (Z-fighting between the depth
prepass and a later pass) rather than a speculative change to a golden-bearing knob.

### 12.5 The `.metallib` cache (M-S7)

The incumbent compiles MSL from SOURCE through `newLibraryWithSource:` at every `CreateShadersFromSpirv` on
every launch, for roughly thirty graphics programs and their compute siblings, and caches nothing, exactly as
the incumbent Vulkan backend compiles every pipeline from SPIR-V at every launch.

A per-program `.metallib` is written to disk and loaded through `newLibraryWithData:`, keyed on the MSL pin
identity, the SPIR-V hash, the device's registry identity and the engine version. Header-validated before
trusting, discarded silently on any mismatch, best-effort so a read or write failure is never fatal and never
crashes a launch. This is S4's disk cache and V-S7's `VkPipelineCache` with a third noun, and M-P4 extracts the
KEY discipline the three share.

**`MTLBinaryArchive` is DECLINED for v1**, and the decline is argued rather than deferred. It is a compiled
pipeline-state cache one level further down, which is a second and newer mechanism for the same win, with its
own serialisation discipline and its own questions (its behaviour across an OS upgrade, and whether it accepts
a pipeline built from a runtime-compiled library at all). The compile time is in the LIBRARY, which is what the
`.metallib` cache addresses. Filed as a follow-up gated on a measurement showing where the remaining launch
cost is, rather than taken on the assumption that lower is better.

### 12.6 Three things this backend must not "fix" (M-B4, M-B5)

All three will be proposed from a Metal seat, which is exactly why each is written down.

**S5's holed-signature sinks stay.** They are FXC-and-WARP specific, Metal has ALWAYS tolerated the holes (the
memory record says so for both the vertex-input and the interpolant cases), and the D3D11 leg ships until the
closing act, so removing a sink because Metal tolerates it corrupts WARP. This is the third design that has had
to write that sentence.

**The "sample all textures up front in binding order" shader discipline stays**, even though M-B1's table
removes the SPIRV-Cross behaviour that made it necessary for this backend. Same reason: the Veldrid Metal leg
is still selectable and still numbers by first-sample order. It comes out with that leg, in the closing act,
not before.

**And the one-uniform-buffer-per-pipeline invariant stays in force until MM6 says otherwise (M-B4).** 2.3 is
the argument. A shader change on the strength of an untested hypothesis about somebody else's cross-compiler is
exactly what the memory says cost four separate diagnostic sessions.

**`SpirvLocalSize` is unchanged.** The compute workgroup size is hand-parsed out of the SPIR-V module today
because the seam's `IGpuComputeShader.ThreadGroupSize*` must report it, and Metal needs the same numbers for
`dispatchThreadgroups`'s `threadsPerThreadgroup`. The incumbent gets them from
`ComputePipelineDescription.ThreadGroupSize*`, which is the same source.

---

## 13. Compute, MSAA, staging and readback

**Compute (M-C1, M-C2).** Compute and graphics bindings are tracked separately with separate dirty arrays and
separate bound-pipeline slots, as the seam requires, so a compute bind never disturbs a graphics one.
`SetComputePipeline` and `Dispatch` end any open render encoder first (M-A5). The compute encoder is created
with the default SERIAL dispatch type, which is what makes M-H4 true. `Dispatch` issues
`dispatchThreadgroups:threadsPerThreadgroup:` with the group counts the seam passes and the pipeline's
`threadsPerThreadgroup` from `SpirvLocalSize`, reproducing the incumbent. Storage buffers are plain buffers at
a `[[buffer(n)]]` index with the pipeline's buffer mutability declared at creation the way the incumbent
declares it. C2's RAW byte-address forcing was an artefact of what SPIRV-Cross emits for HLSL and V-C4 already
ruled it has no analogue elsewhere, and there is no SRV-versus-UAV auto-unbind either, which is a D3D11
binding-model artefact whose Metal occupant is automatic tracking.

**MSAA (M-C3).** `MaxMsaaSampleCount` reproduces `MTLGraphicsDevice.GetSampleCountLimit`, which walks
`_supportedSampleCounts` from the top and returns the first supported count, ignoring both of its parameters,
and the pin carries the citation. **The pin also carries the note that the argument-ignoring is CORRECT**,
because `supportsTextureSampleCount:` is Metal's only sample-count query and it takes no format, so
`MaxMsaaSampleCount` on Metal is format-independent by construction. `VeldridMap.MaxMsaaSampleCount` takes a
MIN over three formats and on Metal all three answer the same, so the min is a no-op and reproducing it means
reproducing the same single answer. A native backend that "improved" this by asking per format would be
inventing a question the API cannot answer, which is precisely the C4 and V-C5 failure in a new costume, and
this is the third phase to have to write that down. `AntiAliasing.ResolveFor` clamps against the result and
`scene3d_hdr_msaa` is baked under it. The implementation issue re-reads the incumbent's source before writing
the reproduction, which is phase 3's row-15 correction inherited as a PROCESS rule rather than as a fact.

**The resolve (M-C4).** `ResolveTexture` opens an empty render encoder with the MSAA source as
`colorAttachments[0]`, `loadAction = Load`, `storeAction = MultisampleResolve` and the destination as
`resolveTexture`, then ends it, at mip 0 layer 0, outside any live encoder. That is the incumbent's shape and
its own TODO says the approach destroys the source texture's contents, which diverges from what
`ResolveSubresource` and `vkCmdResolveImage` do. It is reproduced anyway: the engine's MSAA sources are
re-cleared at the start of the next frame's pass, discarding is the bandwidth-correct answer on this
architecture, and it is what the goldens were baked against. **The divergence goes in the package README** so a
consumer that ever needs the source preserved finds a documented property rather than a surprise. Folding the
resolve into the producing pass's store action is the Metal-native answer and 2.5 records why it is deferred.

An out-of-range requested sample count THROWS at texture creation rather than silently falling to 1, which is
C4's departure inherited for C4's reason: the engine clamps upstream so nothing legitimate reaches the throw,
and a silent MSAA downgrade presents as a golden mismatch that reads like a rendering bug.

**Staging and readback (M-C5, M-C6). This is the highest-risk parity surface in the design and it earns its own
paragraph, for the same reason it did in phase 3.** Every golden in the suite reads back through
`IGpuDevice.Map(staging, ...)` and consumes `MappedData.RowPitch`. The incumbent backs a staging texture with a
`MTLStorageModeShared` `MTLBuffer` rather than a linear texture, sized by walking every mip level, and computes
the subresource layout IN SOFTWARE: `MTLTexture.GetSubresourceLayout` returns a row pitch and a depth pitch
from `FormatHelpers.GetRowPitch` and `GetDepthPitch` over the mip's storage dimensions clamped to the block
size, and `Util.ComputeSubresourceOffset` gives the offset. **A different arithmetic garbles all 36 goldens at
once.**

So the buffer backing and the software layout computation are reproduced byte for byte, and a device-free test
computes the layout for a spread of formats, sizes, mip levels and array layers and asserts it against a
checked-in table taken from the incumbent's own arithmetic. That converts "should be identical" into a checked
fact before a single golden is run, which is exactly what S3 did for the emitted HLSL and V-C7 did for the
Vulkan staging layout. Asserting the rows are tightly packed and moving on may well be right and is the wrong
posture, because the goldens depend on the arithmetic rather than on the assertion.

`Map(staging, Read)` WAITS on the timeline's last submitted value before returning the pointer (M-C6), counted
as a drain. The incumbent's `MapCore` returns `contents()` immediately with no wait, which works today only
because `GpuReadback` submits and drains before mapping, so the seam's guarantee currently rests on a caller
convention rather than on the backend. Getting it wrong returns a pointer to bytes the blit has not written
yet, which reads as an intermittently wrong golden, and an intermittently wrong golden on a real device is the
worst failure shape a five-legged blocking matrix has. `Unmap` is a no-op, as it is in the incumbent, because
`contents()` needs no unmapping on a `Shared` buffer.

---

## 14. Capabilities and diagnostics

`ReadCapabilities` stays the single source both `GpuDeviceContext.Capabilities` and `IGpuDevice.Capabilities`
come from, after they drifted before 15.2.0. The native device implements one and the context reads it from the
device.

| Member | Native source | Parity |
|---|---|---|
| `ClipSpaceYInverted` | **false**, with no viewport trick needed (7.3) | identical |
| `DepthRangeZeroToOne` | true | identical |
| `DeviceName` | `MTLDevice.name`, verbatim, NOT trimmed | identical by construction, given M-N1's default selection |
| `SamplerAnisotropy` | **true**, hardcoded, reproducing `GraphicsDeviceFeatures(samplerAnisotropy: true)` | identical |
| `SamplerLodBias` | **false**, because `MTLSamplerDescriptor` has no LOD bias at all | identical, and it is the one capability that differs from both other native backends |
| `MaxMsaaSampleCount` | the incumbent's own computation reproduced, format-independent by API (M-C3) | asserted identical, and satisfiable by construction rather than by luck |
| `SupportsShadowMaps` | the incumbent's own question: is `R32_Float` usable as BOTH render target and sampled | asserted identical |
| `SupportsCompute` | true | identical |
| `SupportsCompletionFences` | **true** (M-F4) | identical, and it was already true |

**ZERO permitted differences (M-G1)**, which is phase 3's bar rather than phase 2's, and it is right for the
same reason: the incumbent Metal backend has no capability defect to correct. The test carries the
reflection-completeness check phase 2 called the guard that matters most, that the comparison covers every
member of `GpuCapabilities`, so a member appended later cannot silently weaken the assertion. It runs in one
process on the Metal leg.

**Two phase-3 lessons are inherited by name rather than rediscovered.** `DeviceName` is NOT trimmed: the
incumbent takes `_device.name` as it comes, so a trim on the native path alone would fail parity on any device
whose reported name carries padding. And `SupportsShadowMaps` asks what the incumbent ASKS rather than what the
member's name suggests, which is the row-18 correction where a first implementation read "attachment" as
depth-stencil attachment for a colour format and would have answered false on every device in existence, with
the failure silent (the shadow path degrading to blob shadows on one backend with nothing red). Writing down
what a member's name suggests instead of what the incumbent asks is a parity failure by construction.

**The shared point and linear samplers WRAP on all three axes**, built from wrap-addressed descriptions and NOT
from the identically named `GpuSamplerDescription.Point` and `.Linear` statics, which default every axis to
CLAMP. The seam says so in writing on `IGpuDevice.PointSampler`, and reading the address mode off the statics
because the names matched cost two goldens on the D3D11 leg. This paragraph exists because the same mistake is
available a third time. The incumbent's other sampler mappings are reproduced exactly, including the two
conditionals (the border colour set only on macOS-family devices, and the comparison function set only when the
description carries one) and `maxAnisotropy` clamped to at least 1. G1's ruling applies: reachable hardcodes are
reproduced and unreachable degradations are not, and the border-colour conditional is reachable.

**Software rasterizer (M-G2).** Apple ships no software Metal rasterizer, so `softwareAdapter` is FALSE with
confidence rather than null. That is a genuinely different answer from "nobody asked", which is what
`GpuDeviceDiagnostics` documents null as meaning, and the Veldrid Metal path keeps the default (null), which is
correct because it cannot answer. CI pins nothing, and section 5 says why.

**Validation (M-G3, M-T7).** `KE_METAL_VALIDATION=0|1|shaders` maps onto the process-level `MTL_DEBUG_LAYER`
and `MTL_SHADER_VALIDATION` variables the Metal runtime reads at device creation, plus an engine-side log line
recording which tier was armed and a WARN when the variable was set after the runtime had already read it.
**There is no package to install anywhere**, which is a materially cheaper gate than V-T4's net-new CI work.
**Whether in-process environment mutation actually reaches the framework is a ROW-1 SPIKE rather than an
assertion**, because Metal API validation is a process-launch mechanism and no phase-3-style "install a layer"
answer exists. If it does not work, the CI leg sets the variable in the job environment and the local answer is
a documented prefix on the run command, which is worse ergonomics and no less correct. Section 16 records what
neither tier buys.

**Command-buffer errors and device loss (M-G4).** Metal reports command-buffer failures ASYNCHRONOUSLY:
`status` becomes `Error` and `error` carries an `NSError` whose `MTLCommandBufferError` code distinguishes the
classes that matter here, including the GPU-restart and timeout cases. The incumbent reads `status` in exactly
one place (`WaitForIdleCore`, to decide whether to wait) and never reads `error` at all, so a Metal device loss
today is invisible to the engine and to telemetry. The completion handler reads both in EVERY configuration,
latches the first failure with its code and localized description AT THE FAULT SITE, flips the liveness token
so all subsequent disposals are no-ops, and surfaces the reason through the existing `deviceLossReason` header
field. Phase 3's `CheckResult` lesson applies: a latch built on checks that compile away in Release never
fires, so this one is not `[Conditional("DEBUG")]`. That closes #427 for the Metal leg on the day the backend
lands, which is the correct time, because retrofitting the reporting after the first field crash wastes the
crash.

**Frame capture (M-G5).** `MetalFrameCapture` exists in `KhaozEngine.Gpu` today and reaches Veldrid's private
`_commandQueue` field by reflection, returning zero and skipping the capture if the layout differs. This
backend owns the queue, so the native path hands the pointer in and the reflection is unnecessary, and the
capture stops being one Veldrid refactor away from silently not working. The reflection version stays for the
Veldrid Metal leg for as long as that leg ships. The append audit's third silent site (4.2) is the gate that
decides which path runs, which is why this is a row rather than a nicety.

**Counters (M-G6).** `FramesBegun`, `DrainCount`, `DrainMs`, `BackpressureStallCount`, `BackpressureStallMs`,
`OffTimelineDeferred`, `OffTimelineOutstanding`, `AcquireWaitCount` and `AcquireWaitMs` are all populated from
the struct as it stands, with **NO seam addition**. Phase 3 appended the acquire pair for its own MV2 and this
phase reads it for M-W4 at no cost, which is a template paying out rather than a coincidence.
`BackpressureStallCount` counts the ring acquire ALONE here, where on Vulkan it folds in the command-buffer
slot wait, so its doc comment gains a sentence saying the second meaning is Vulkan's rather than universal.
They leave through `IGpuDevice.Counters`, are forwarded by `GpuDeviceContext` and `AppWindow`, and reach a
capture as sample-row channels, which is the path gate 4 reads.

**One counter would be genuinely useful and is deliberately NOT added: encoder boundaries per frame**, which is
the number MM1 is really about. It is available device-free from M-T2's budget sink, where it is a frozen
marginal rather than a runtime counter, and appending a member to a shipped seam struct for a number one
backend can answer is exactly the "no seam change" claim phase 3 warned costs a gate its result.

---

## 15. Test plan

| Layer | What it covers | Runs where |
|---|---|---|
| The 36 committed `metal` goldens, shared family (M-T1) | Pixel equivalence against the SHIPPED Metal backend on the SAME REAL DEVICE, at the one global tolerance of 0.06 absolute per channel where zero of the 1728 values in a golden grid may exceed it. No rebake | New `metal-native` leg, full suite on every trigger |
| `CrossBackendGoldenTests` | Unchanged. Still three families, still the 0.20 ceiling. **It is also the thing that would catch a bad `metal` bake, and the metal family is the reference the other two are read against** | Every `dotnet test` |
| **The MSL index-table test (M-T3)** | "Everything compiles and every pixel is wrong", arriving through the MSL door. Every emitted entry point across every shipped program, paired against the pipeline's layout array and each layout's element array. **Taken BEFORE the first golden run** | Every `dotnet test` |
| Vertex-stream collision assertion (M-B2) | That no index M-B1 read out of any vertex function reaches the top-pinned stream range, for every shipped program. The one thing standing between two index spaces | Every `dotnet test` |
| Native-call budget, device-free `[Fact]` (M-T2) | The Metal fan-out class through `IMetalEncoderSink`: argument-table writes, draws and dispatches, and ENCODER BOUNDARIES | Every `dotnet test`, both cheap legs |
| MSL byte equality per program (M-S3) | Back-end DRIFT, understood as drift and not as parity (12.3) | Every `dotnet test` |
| `MetalMslIncumbentParityTests` (M-S3) | That the two independently maintained option sets still emit the same bytes. A red run means the committed goldens are baked on one emission and asserted against another | Every `dotnet test` |
| The one-off MSL parity measurement (M-S4) | What actually licenses "no rebake". Taken once, in process, RECORDED in this document before the first golden run | Once, before the first golden run |
| `NativeVsVeldridMetalCapabilityParityTests` (M-T4) | Silent capability drift, ZERO permitted differences, plus the reflection-completeness check | Metal leg |
| **Metal API validation, two tiers (M-T7)** | `MTL_DEBUG_LAYER=1` on every native-leg run and `MTL_SHADER_VALIDATION=1` on the scheduled run. Encoder-state, argument-range and pipeline-compatibility errors, plus in-shader bounds. NOT a synchronisation validator (section 16) | Every native-leg run, and the schedule |
| Shared uniform-ring semantic tests, third adapter (M-T5) | Section 9.4's seven shared rows, run against all THREE backends' rings through the one test-only interface in `KhaozEngine.TestSupport.Gpu` | Every `dotnet test` |
| Ring stride and bind-window invariant, device-free (M-M4) | `frameBase + rangeOffset + callerDynamicOffset + size <= (frame + 1) * stride` for every shipped set shape | Every `dotnet test` |
| Staging subresource layout table test (M-C5) | A garbled readback on all 36 goldens at once | Every `dotnet test` |
| **Encoder-scope invalidation test (M-R4)** | Record a draw, force an encoder end through a blit, record a second draw, assert the second re-issued its vertex-stream binds. The corruption the incumbent avoids only through a second defect (2.1). Plus: a redundant pipeline bind re-activates nothing (M-R8) | Every `dotnet test` |
| Index-table invalidation test, device-free (M-R9) | That a pipeline switch between two programs with identical index tables invalidates nothing, and that one between differing tables invalidates exactly the slots whose indices moved | Every `dotnet test` |
| Clear-folding test, device-free (M-A2, M-A3) | Per-attachment `loadAction` for a three-target framebuffer, and the clear-only pass still clearing | Every `dotnet test` |
| Viewport and scissor guard test (M-A6) | Exactly one viewport call and one scissor call per framebuffer CHANGE, zero for a redundant rebind, and zero scissor calls at all when the bound pipeline has scissor test off | Every `dotnet test` |
| Autorelease-pool architecture test (M-N5) | That every public entry point which can create an autoreleased object wraps its body, over the type graph rather than by review | Every `dotnet test` |
| Timeline and fence unit tests (M-F1) | Monotonic signal, non-blocking `Signaled`, `Reset` re-arming, and the drain counting into `DrainCount` and `DrainMs` | Every `dotnet test` |
| Recording-contract test, device-free | N lists open concurrently, interleaved records, submitted out of record order, per-list order asserted and concatenated in SUBMIT order | Every `dotnet test` |
| Drawable-boundary test, device-free (M-W5) | The nil-drawable frame: orphan target bound, recorded, submitted, present skipped, `FramesBegun` incremented | Every `dotnet test` |
| `CopyBuffer` alignment assertion (9.3) | That no shipped call site produces an unaligned offset, so the named throw is unreachable | Every `dotnet test` |
| Uncommitted-command-buffer bound (6.1) | That the backend never holds more uncommitted buffers than `FramesInFlight` plus one | Every `dotnet test` |
| **MM6's two-uniform-buffer probes (2.3)** | Whether the one-UBO constraint is a property of the incumbent's numbering or of Metal, with a pixel READBACK assertion rather than a no-throw assertion | Metal leg, gate 3 |
| Full `macos-26` suite | 0 failed, 0 skipped, passed at or above the incumbent's on the same commit | Metal leg, every trigger |
| `OpenListTrackingGpuDevice` | Nested `Begin`. Stays the PORTABLE guard, passes trivially here, and is NOT evidence about this backend | Every `dotnet test` |
| `GpuFactSkipReasonTests` extension | That `KE_GPU_TESTS=probe` still answers correctly once a third provider registers | Every `dotnet test` |
| `GpuDeviceLifecycleTests` | Concurrent create, use and dispose against the native provider | Metal leg |
| `GpuBackendKindAppendAuditTests` | The audit sites, extended for `MetalNative`, with the three silent ones (4.2) each carrying a row | Every `dotnet test` |
| `ArchitectureTests`, `VeldridLockdownTests`, `GpuPublicApiTests`, the no-Veldrid pair, the MSL-half-only edge | Zero renderer changes, no Veldrid leakage, opt-in isolation, that the backend names no HLSL cross-compile member, and that no view factory is reachable from the recording type | Every `dotnet test` |
| Phase 2 and phase 3 frozen marginals, unchanged across M-P4 | That the extraction moved code and changed no behaviour on either existing backend | Every `dotnet test` |

**The budget test's gate, stated the way T2 and V-T2 state it.** The gate is (a) structural invariants: ONE
array call per (kind, stage) per full activation and never one per element, an offsets-only rebind at exactly
one call per visible stage, exactly one viewport call and one scissor call per framebuffer CHANGE with zero for
a redundant rebind, zero encoder boundaries between two draws in one pass, and zero record-time buffer
allocations. (b) Marginal per-draw deltas: 5 distinct meshes against 1, and 18 draws against 6, must move the
total by an exact per-draw delta. (c) Trace identity for 8 instances of one mesh against 1. (d) An upper bound
on encoder boundaries per frame. Absolute totals are documentation and may be updated freely, because a test
that is routinely edited to match reality stops being a gate.

**One marginal is a REGRESSION target rather than a parity target, and it is worth naming.** The incumbent
re-binds every vertex stream on every draw (2.1). The native backend binds a stream only when it changed, so
its per-draw marginal is strictly lower, and the budget test freezes the LOWER number.

**CI (M-T6), and the cost conversation has changed twice in this program's life.** A `metal-native` matrix leg
on hosted `macos-26` with `KE_GRAPHICS_BACKEND=metal-native`, `KE_GPU_TESTS=1`, `KE_METAL_REQUIRED=1` and
`MTL_DEBUG_LAYER=1`, running the FULL suite on every trigger, matching the incumbent Metal leg's
`fullSuite: always` tier exactly. It is a GUEST in the `metal` family, so `KE_UPDATE_GOLDENS` stays empty on it
for every trigger and it sits a bake dispatch out entirely, and `GoldenCompare.BakeRefusal` already derives
guest-ness generically so that costs no new code. The repository is PUBLIC, so hosted runners are free and the
historical macOS billing multiplier is gone. The incumbent Metal leg already runs the full suite on every
trigger, so matching its tier is the cheap option rather than the expensive one.

**This leg is the strongest regression net in the program and that changes what the design can lean on.** The
D3D11 native leg runs golden-only on push. The Vulkan native leg runs golden-only on push, on a software
rasterizer, with no swapchain coverage at all. This one runs everything, on a real GPU, every time. It is why
2.6's hazard ruling cuts the way it does, and it is what bounds the interop risk in 3.1.

`KE_METAL_REQUIRED=1` is phase 3's row-19 lesson inherited by name: rows that need a native device return early
when the probe refuses the machine, a dormant row is NOT a skip, and a zero-skipped gate could otherwise be
satisfied by rows that asserted nothing. On this leg the refusal throws and names what the probe objected to.

**The incumbent Metal leg is deliberately NOT coupled to the native backend's health**, for the reason the
workflow header already gives about the incumbent Vulkan leg. It arms no validation tier, sets no
`KE_METAL_REQUIRED`, and its rows that touch a native device stay dormant if the probe ever refuses the runner.
That leg is M-RO2's escape hatch, and an escape hatch that goes red whenever the thing it escapes from goes red
is not one.

The `NativeDeviceLifecycle` collection definition is copied into every test assembly carrying `[GpuFact]`,
because collection definitions are per assembly and phase 2 measured that adding a second live-device backend
without one took a leg from 17 minutes to 49.

**The naming contract is load-bearing and unchanged.** `cross-platform-gpu.yml` selects golden-only tiers with
`--filter FullyQualifiedName~Golden`. The Metal legs run the full suite on every trigger so the filter does not
bind for them, and every device-free test above deliberately does NOT carry the substring so it runs on the
cheap legs rather than inside the golden filter.

---

## 16. Unproven bets: gates, kill switches, exit criteria, deadlines

Every decision below rests on reasoning rather than measurement. Each names the measurement that settles it,
the switch that turns it off, the criterion that retires the switch, and **a deadline, which is the gate at
which a second-implementation switch is REMOVED, or the gate by which a bet carrying no such switch must be
RESOLVED** (M-RO4, sorted by phase 3's 2.7). A bet without all four is not shipped.

**One switch covers most of them, deliberately.** M-RO2 keeps Veldrid Metal selectable by token until the
closing act, so every STRUCTURAL decision here (the encoder model, the ring, automatic tracking, the present
path, the binding table) has a working, shipping, one-environment-variable escape. Per-decision switches are
spent in exactly one place.

**Two switches ship in this whole design and both are branches inside one implementation.** That is deliberate:
phase 2's gate 3 is still blocked behind an unresolved A/B with two drivers shipping, and this design does not
repeat it. The MSL binding question is decided at a row-1 SPIKE rather than carried by a switch, and the
recording model has nothing to A/B, so **there is no M1 analogue for the second phase running.**

**And this section is the honesty ledger.** Four of its rows are observations rather than bets, because they
name things no measurement available to this phase can settle. A reader looking for a gate that was never
possible should find the row saying so.

| # | Bet | Measurement gate | Kill switch | Exit criterion | Deadline |
|---|---|---|---|---|---|
| MM1 | The ring removes the encoder-split-per-record-time-write class (2.1), and that class is the dominant per-frame cost the incumbent carries. **The MAGNITUDE is unmeasured**: nobody has counted how many record-time `UpdateBuffer` calls per frame the #410 scene makes on Metal, and two releases of renderer-side engineering have already hoisted most of them out of the frame | Count record-time `UpdateBuffer` calls, encoder boundaries and record-time buffer allocations per frame on the #410 scene ON THE INCUMBENT first, through a throwaway instrumented build. Then the same three on the native backend | None. The ring is not optional (9.2), and M-M7 makes it a correctness change, so there is no in-backend fallback to hold | Native encoder boundaries per frame at or below the framebuffer-change count plus the compute and blit passes the frame genuinely needs, native record-time allocations at zero, and frame time no worse than gate 4's baseline. If the incumbent's counts turn out near zero already, the ring is still taken for M-M7 and this bet is **RECORDED AS NOT PAYING** rather than quietly forgotten | Gate 4 |
| MM2 | Per-attachment clears (M-A2) either do not move a committed metal golden, or move exactly the ones two unwritten attachments can explain | All 36 goldens with `KE_METAL_CLEAR` in both positions on the same build, on the first green run | `KE_METAL_CLEAR=attachment0` reproduces the incumbent exactly | Both positions green, OR exactly the scenes whose framebuffer has more than one colour target differ and the difference is explained by two attachments going from Load to Clear. **A difference anywhere else means something other than this clause moved.** By M-RO4's sort it is a branch inside one implementation, so it is removed at its gate and the losing branch deleted whichever way it goes | **Gate 1. Removed there** |
| MM3 | The array-batched flush (M-R6) collapses a full activation to one call per (kind, stage) and an offsets-only rebind to one per visible stage, with zero encoder boundaries between two draws in one pass | The device-free budget test (M-T2), confirmed on the first green run and then frozen as marginals | None needed. A call-count property with no runtime risk | The first green run's measured marginals are recorded in this document and become the frozen numbers, INCLUDING the vertex-stream marginal, which is a regression target rather than a parity target | Gate 3 |
| MM4 | `FramesInFlight = 3` is enough that ring backpressure never blocks the CPU, and `maximumDrawableCount = 3` is enough that the drawable acquire does not become the frame's pacing | `BackpressureStallCount` and `BackpressureStallMs` for the ring, `AcquireWaitCount` and `AcquireWaitMs` for the drawable. **The second pair is expected to be non-zero under vsync and that is not a failure**: a vsync-paced frame SHOULD wait for a drawable, so the gate reads the UNCAPPED capture | `KE_METAL_FRAMES_IN_FLIGHT=<n>`, owned by row 7 | Ring stall count zero across a full capture window, and acquire wait per frame near zero on the uncapped capture. A non-zero ring stall means 3 is wrong, not that the design is | Gate 4. A TUNING KNOB by M-RO4's sort, so it may survive as a knob, but only if the exit criterion was met at its DEFAULT. A knob is not a way to ship a failed default |
| MM5 | (observation, not a bet) **Automatic hazard tracking (M-H1) costs conservatism that nothing in this design measures**, and the cost cannot be priced at all because there is no safe untracked build to A/B against. The driver may serialise two encoders that could have overlapped, and there is no counter for lost overlap. Frame time against the incumbent measures the two configurations TOGETHER, since the incumbent is also tracked | None available in v1. A GPU trace in Xcode's frame debugger would show it and is not a CI instrument | n/a. `MTLResourceHazardTrackingModeUntracked` on an individual resource is the escape hatch, and taking it means writing the barriers for that resource | Recorded so a reader does not mistake "no barriers in the code" for "no serialisation on the device", and does not mistake a green gate 4 for evidence that tracking is free. If a measurement ever shows the cost, the heap decision (8.4) is re-argued in the same change | Open past gate 5, deliberately |
| MM6 | **The one-uniform-buffer-per-pipeline constraint is a property of the incumbent's numbering rather than of Metal**, so a pipeline with a second uniform buffer reads correct bytes under M-B1's table | Two `[GpuFact]` probes in the shape `GpuSkinningReproGpuTests` established: a pipeline whose vertex stage reads two resource buffers, and a pipeline with a fragment-only second UBO at set 1, each with a pixel READBACK assertion rather than a no-throw assertion. **Headless is the right instrument**, because `docs/DEPENDENCY-SEAMS.md` records that the constraint holds offscreen as well as windowed (2.3) | None. This is a measurement, not a shipped behaviour. **M-B4's invariant STAYS in force regardless of the result** | Both probes read correct values. **A pass does NOT authorise a shader change**: it authorises FILING the invariant's removal as its own work with its own gates on all three backends. A fail is recorded here as the constraint being real on Metal rather than on Veldrid, which is worth just as much and closes four sessions' worth of open question | Gate 3 |
| MM7 | (observation, not a bet) **The swapchain, the drawable and the present path have ZERO CI coverage**, because the Metal golden suite is headless and a headless device builds no `CAMetalLayer`. Every decision in section 11 is validated by a human at a window, or not at all | None available. A native soak that reproduces a presentation defect is consistent with several mechanisms | n/a | Recorded so a reader does not mistake a green full-suite leg, which is the best-covered leg in the matrix, for evidence about presentation. Gate 5's manual pass is the only instrument, **and it is a manual pass alone, because the tool both drafts named for it does not exist (2.10)** | n/a |
| MM8 | (observation, not a bet) **Metal has no synchronisation validator.** `MTL_DEBUG_LAYER` is API validation and `MTL_SHADER_VALIDATION` is in-shader bounds checking, and neither tracks read-after-write hazards across encoders. This is the fact M-H2 is decided on and the one place this phase's instrument set is WEAKER than phase 3's | None available. If Apple ships one, or if a `MTLCaptureManager` trace-based detector is built, 2.6 reopens with a named instrument | n/a | Recorded because V-M1's precedent is that a decision conditional on an instrument must say which instrument, and because a future reader comparing the two phases' validation gates should find this rather than conclude Metal's is simply cheaper | Open indefinitely |
| MM9 | The engine-owned interop layer is ABI-correct on arm64 (3.1) | Row 1's spike compiles one file against every call this design names and runs it against a real device, and then the full suite on the leg is the standing answer | None. An ABI error is a crash, not a tunable | The spike runs clean and the full suite is green on the leg. **An interop defect is expected to present as a crash rather than as a wrong pixel**, which is the one comforting property of this risk | Gate 2 |
| MM10 | The `.metallib` cache key plus header validation is enough that a stale or corrupt cache can never crash a launch (M-S7) | Startup time cold and warm, plus a deliberate corruption test that truncates and mutates the file and asserts a clean discard | The cache path is best-effort by construction: any read or write failure is a silent discard, which IS the fallback | Corruption test green and no launch failure attributable to the cache across the soak | Gate 4 |
| MM11 | (observation, not a bet) **The CPU-versus-GPU race the ring closes cannot be shown to have closed** (M-M7). The incumbent's ungated device-level write is a race by inspection and it has never produced a reported defect, and a race that does not reproduce cannot be shown to have stopped | None available | n/a | Recorded so the work is attributed honestly: this is a correctness improvement taken on a code reading rather than on a repro | n/a |
| MM12 | (observation, not a bet) **There is no Metal field baseline anywhere in this program's record.** #410's reporting machine is Windows. Phase 2 held gate 4 against 125 fps and 8.0 ms and phase 3 against 144 fps and 6.9 ms, both measured. **A gate stated against a number nobody has measured cannot be read** | Gate 4's FIRST task is a capture on the incumbent, on the same Mac, the same scene and the same capture window, before the native session | n/a | The baseline is recorded here when taken, and the pass bar is "no worse over a week" against it. This is a step neither predecessor needed | Gate 4's prerequisite |
| MM13 | (observation, not a bet) **Nothing here is tested on an Intel Mac or on any device below the current Apple Silicon families.** The hosted runner is arm64 and the fleet's machines are arm64. M-N3 replaces the incumbent's deprecated feature-set reads with `supportsFamily:` precisely because the derived answers are fragile, and that replacement is itself untested on the hardware that would exercise the other arm. The same applies to M-M2's `Shared`-everywhere choice, whose discrete-Mac follow-up has no machine to measure it on | None available to this phase | n/a | Recorded so a consumer report from an Intel Mac is read as the first data point rather than as a surprise | n/a |

---

## 17. Rollout

Opt-in first, then the CI leg as the continuous exercise, then a field soak on a Mac through a game's normal
update flow, then the default. Five gates, all green before any flip.

1. **All 36 goldens green** against the shared `metal` family on hosted `macos-26` at the existing tolerance,
   with the observed worst-cell delta RECORDED here, and **MM2 resolved with `KE_METAL_CLEAR` removed**. The
   `golden-deltas.<family>.txt` evidence file phase 2 added already appends on a PASS, and the leg uploads it as
   `golden-deltas-metal-native` on `always()`, so the number comes off a green run rather than needing one to
   break first. Note the naming trap the other two pairs have: the ARTIFACT is named for the leg and the FILE
   inside it for the family, so `golden-deltas-metal-native` contains `golden-deltas.metal.txt`, and downloading
   both macOS artifacts gives two same-named files that are two implementations measured against one set of
   references. **This gate is worth more than its siblings and carries more risk**: the `metal` family is the
   fleet's cross-backend reference, so a green here is the strongest evidence any leg in this program produces
   and a red here is a fleet event rather than a leg event.
2. **Full `macos-26` suite at 0 failed and 0 skipped**, with the passed count at or above the incumbent's on the
   same commit, `MTL_DEBUG_LAYER=1` producing no validation errors, and MM9 met. **The skip criterion is phase
   3's rather than phase 2's**: Veldrid Metal already signals on completion, so the `RequiresCompletionFences`
   pair already runs on this leg and the criterion is NO NEW SKIPS rather than two fewer. A skip is a failed
   implementation of something, whatever else is green.
3. **Budget test green** with the marginals recorded here, MM3 met, the MSL index-table test green, and
   **MM6's measurement TAKEN and recorded whichever way it went**. No M1-equivalent hangs over this gate,
   because there is one recording driver and nothing to A/B, which is the single biggest difference from phase
   2's rollout.
4. **A field session on a Mac at or above the incumbent Metal's numbers** across a full capture window, with
   zero command-buffer errors, the session header naming `MetalNative`, and MM1, MM4 and MM10's exit criteria
   met. **The baseline has to be TAKEN rather than looked up** (MM12), and that is a real difference from both
   predecessors. Before the field capture, pin the session log's build line and the capture-window stamps: a
   number attributed to the wrong build is the expensive failure here, and M-I4's throw-on-missing-provider
   exists specifically to make it impossible.
5. **A human windowed pass**: resize by drag, maximise, fullscreen toggle, a display change if the machine has
   two, alt-tab, and a vsync toggle mid-session. `deviceLossReason` and `softwareAdapter` present in the session
   header. Two additions this gate carries that its predecessors did not. **The vsync toggle is a MEASUREMENT,
   not a smoke test**: it is what decides M-W3's frame-cap arm, so the tester records whether the native
   backend's present throttles the CPU from vsync alone, and the disposition lands in `FrameCap.Resolve`'s and
   `DisplaySettings`'s doc comments either way. And **the windowed-only defect class is an explicit checklist
   item** (skinned meshes, and any pipeline reading a second uniform buffer, exercised at a window), because
   `SlathRepro` was deleted in `9.0.0` and neither draft noticed (M-RO6, 2.10).

**Gate 4 is harder here than it was in either predecessor, and the reason is worth naming.** On D3D11 the
incumbent WAS the problem and parity was already a win. On Vulkan the incumbent was the engine's best backend
on the only field evidence there was. On Metal the incumbent is the backend the fleet's REFERENCE IMAGES are
baked on and the one with no filed performance defect at all. So the pass bar is "no worse over a week" against
a baseline that has to be measured first, and anyone weighing whether this phase should happen should weigh
that honestly: **the case for it is #420's endpoint, the ring's correctness argument (M-M7), the MRT clear
defect (2.4) and the binding-model question (2.2), not a promised speedup.**

**What a flip MEANS here, and it is the biggest blast-radius difference in the program.** `ProbeOS` maps macOS
to `Metal`, so flipping changes the macOS default. That is not a player population: the fleet's players are
overwhelmingly on the Windows and Linux heads. It is the DEVELOPMENT platform. Every windowed playtest, every
capture, every editor session and every local golden bake on a Mac would run on the native backend the day the
flip lands.

That cuts both ways and both halves are worth stating rather than picking the convenient one. In favour: a
regression is felt within hours by the person best placed to diagnose it, on a machine with a debugger and a
`MTLCaptureManager`, which is the fastest feedback loop this program has ever had at a flip. Against: a
regression that only reproduces in a windowed session blocks development rather than a shipping build, and
gate 5 is the only instrument that sees it. That is a reason to hold gate 5 strictly rather than to relax
anything.

The flip itself is one line in `ProbeOS`, plus adding the kind to `_windowCandidates`, plus settling the two
frame-cap rows (4.2, M-W3), which are the reason `IsMetal()` exists. `Metal` through Veldrid stays selectable
by token (M-RO2), so a field regression is one environment variable away from an A/B on the same build. The
headless default stays on Veldrid until gate 4, because an early headless flip would silently reduce the
incumbent's coverage during exactly the window when both must stay green, which is RO3's ruling for the third
time.

### Rollout record

Every gate is PENDING. Nothing in section 18 has been implemented, so no gate can have been attempted. This
subsection exists so the standing of each gate is recorded here as it moves rather than reconstructed from
issue comments, in the shape phase 2's and phase 3's rollout records established.
