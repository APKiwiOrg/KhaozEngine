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
