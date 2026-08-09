# DRAFT A (reuse-first): KhaozEngine.Gpu.Metal, the native Metal backend

**This is one of two competing complete drafts** for phase 4 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), specified by
[#566](https://github.com/APKiwiOrg/KhaozEngine/issues/566). It argues from the REUSE-FIRST prior: the shapes
phases 2 and 3 proved are the default, every departure has to be earned, and where Metal's model makes a ported
shape wrong the ruling goes to Metal and says so out loud. Draft B argues from the Metal-idiomatic prior. An
adjudicator rules between the two and the adjudicated document lands as
`docs/design/METAL-NATIVE-BACKEND-DESIGN-<date>.md`. Nothing here has run on a device.

Written against engine `17.34.0` (`Directory.Build.props`). The incumbent this design replaces and must reach
parity with is **`Veldrid 4.9.103`**, the vendored fork package `Directory.Packages.props` pins.

**The evidence base, established before anything was allowed to decide anything** (section 2.1 in full). The
short version, because V-I6 exists precisely because one phase-3 draft cited the wrong ref:

- `git diff v4.9.0 v4.9.103 -- src/Veldrid/MTL/ src/Veldrid.MetalBindings/` produces **zero lines**, and
  `git diff v4.9.103 master -- src/Veldrid/MTL/` also produces zero lines. So the Metal backend the engine ships
  is stock upstream `v4.9.0` and reading it on the fork's `master` checkout is safe for that directory.
- `src/Veldrid.MetalBindings/` is NOT identical to master. Three files differ, and two of them matter:
  `MTLResourceOptions` and `MTLStorageMode` are `uint`-backed enums on the shipped `v4.9.103` and were widened
  to `ulong` upstream. The real Metal types are `NSUInteger`, which is 64-bit on every Mac the fleet targets.
  Section 2.1 argues what that costs.
- Every citation below names a MEMBER, never a line number (V-I6). Phase 2's cited line numbers went stale
  inside one release and `GpuBackendKindAppendAuditTests` records it.

---

## 1. Decisions

| # | Area | Decision | Prior |
|---|---|---|---|
| M-P1 | Package | New `KhaozEngine.Gpu.Metal`, opt-in, outside every umbrella, `net10.0` (NOT `net10.0-macos`) with `[SupportedOSPlatformGuard("macos")]` entry points and `NoInlining` bodies behind `OperatingSystem.IsMacOS()`. Exact reuse of P1, so the assembly compiles and its device-free tests run on the Linux `ci.yml` leg and both Windows legs | Reuse (P1) |
| M-P2 | Binding | An ENGINE-OWNED `objc_msgSend` interop layer. There is no maintained managed Metal binding to take, so the Vortice and Silk.NET precedents have nothing to point at. Vendoring `Veldrid.MetalBindings` is rejected by name. Its source is READ as the reference implementation, which is legitimate and is not the same thing | Departure, forced (3.1) |
| M-P3 | Layering | References `KhaozEngine.Gpu` and `KhaozEngine.Diagnostics` only, no third-party package at all, which makes it the first backend with an empty `ThirdPartyHomes` row. The no-Veldrid pair (csproj read plus IL reference walk) is extended to it in both forms | Reuse (V-P3) |
| M-P4 | Shared home | **#531's extraction RIDES this phase, partially and in a named order.** THREE things extract, because three implementations now show the same shape rather than two predicting it: the uniform ring's SEGMENT POLICY, the completion TIMELINE, and the counting harness's MARGINAL-ASSERTION helpers. Section 2.8 argues each | Judge, for extraction |
| M-P5 | Shared home | TWO things do NOT extract, and the exclusions are reasoned rather than provisional: the record-then-flush SCHEDULE (three implementations, three flush shapes) and the generic EMITTER interface (V-P4 excluded it by name at two implementations and Metal is a third shape again, which confirms the exclusion rather than reopening it) | Reuse (V-P4's exclusions) |
| M-P6 | Shared home | The extraction row lands AFTER the Metal ring and timeline are green against the shared semantic tests, never before. If either does not fit the shape the other two share, the row closes as not planned with the written reason. That is the honest exit and it is stated here so it is available | Judge, new |
| M-I1 | Identity | Append `GpuBackendKind.MetalNative = 6` with an explicit ordinal and the append-only comment. New tokens `metal-native` and `mtl-native` | Reuse (V-I1) |
| M-I2 | Identity | The append audit is a TEST (`GpuBackendKindAppendAuditTests`), walked a third time in section 4.2. **THREE sites answer differently from Vulkan's append and all three degrade SILENTLY**: `FrameCap.Resolve`, `DisplaySettings`, and `VeldridGpuDevice`'s Metal frame-capture gate | Reuse, answers differ |
| M-I3 | Goldens | GUEST in the committed `metal` family through `GoldenBackendToken`, which throws on an unmapped kind and is pinned by the audit test. `BakeRefusal` derives guest-ness generically already, so `metal` is an OWNER token for `Metal` and a GUEST token for `MetalNative` with no new code | Reuse (I3, V-I3) |
| M-I4 | Identity | A missing provider registration THROWS and never falls back. An incapable machine is answered by `IsSupported()`'s functional probe and reported through `AfterFallback`. `PreflightProvider` already fixes the order | Reuse (V-I4) |
| M-I5 | Identity | Add `GpuBackendKinds.IsMetal()` beside `IsDirect3D11()` and `IsVulkan()`. It has real readers here, unlike `IsVulkan()`: the frame-cap arm and the frame-capture arm both ask exactly this question | Reuse (V-I5), with readers |
| M-N1 | Device | `MTLCreateSystemDefaultDevice()` as the DEFAULT, reproducing the incumbent, with `KE_METAL_DEVICE=<index>|<substring>|discrete|integrated|low-power` as explicit selection over `MTLCopyAllDevices()`, and any substitution LOGGED | Reuse (V-N3) |
| M-N2 | Device | ONE `MTLCommandQueue` for the device, created once, thread-safe by contract. No second queue, no `MTLEvent` cross-queue machinery. #534's argument transfers verbatim and the FFT ocean is the same named consumer | Reuse (V-N5) |
| M-N3 | Device | No process-wide instance object exists on Metal, so V-N1 has no analogue. `GpuDeviceContext._lifecycleGate` STAYS regardless: it also covers disposal and it is not this backend's to remove | Departure, absent |
| M-N4 | Device | `IsSupported()` is a functional probe: create a device, read its name, check the four things section 4.1 lists, dispose. It must never throw. The incumbent's `MTLGraphicsDevice.GetIsSupported` is reproduced as the FLOOR of that probe rather than as the whole of it | Reuse (V-I4), extended |
| M-R1 | Recording | `MetalCommandList` implements `IGpuCommandList` and encodes at RECORD TIME into a per-list `MTLCommandBuffer`. No engine-owned op stream, no second driver, no `KE_METAL_RECORD`, no M1 analogue. Phase 2's stream was a D3D11 adapter and phase 3 already declined it for the same reason | Reuse (V-R1) |
| M-R2 | Recording | **NO command-buffer ring, and this is where V-R2 does not port.** An `MTLCommandBuffer` is single-use: there is no reset and no pool to reset. `Begin()` takes a fresh buffer from the queue. The `FramesInFlight` depth stays, owned by the uniform ring alone, and the slot-wait moves there with it | Departure, ruled for Metal |
| M-R3 | Recording | `Begin()` additionally waits on the ring's frame slot, counted as backpressure. `End()` ends any live encoder. `Submit` commits and records the signalled completion value. The queue's uncommitted-buffer limit is a real bound and is named rather than assumed away (section 6.1) | Reuse (V-R3), rehomed |
| M-R4 | Recording | N lists record concurrently and genuinely, because a command buffer and its encoders are per list and nothing shared is read or written during recording. The PORTABLE seam contract is unchanged at one open recording per device. `IGpuCommandList.Begin`'s XML doc gains a Metal sentence | Reuse (V-R4) |
| M-R5 | Recording | TWO-state per-slot dirty records, not three, and not the incumbent's one-bit-per-set model either. Section 2.4 argues why the answer differs from BOTH neighbours: a Metal set activation is N calls rather than one, so the dynamic-offsets-only case is genuinely cheaper here in a way it is not on Vulkan | Judge, between the two |
| M-R6 | Recording | A pipeline switch invalidates recorded slots WHOLESALE, reproducing the incumbent's `SetPipelineCore` clear. Neither D3D11's outgoing-layout drain nor Vulkan's compatibility-prefix computation ports: Metal has no pipeline layout object at all, so there is nothing to compare for compatibility | Departure, ruled for Metal |
| M-R7 | Recording | Ending a render encoder invalidates the recorded VERTEX-BUFFER binds as well as the resource-set ones. The incumbent's `EndCurrentRenderPass` forgets to, and is saved only by a second defect (its bind loop never marks a stream active, so every stream is rebound on every draw). Fixing one without the other is a corruption (section 2.2) | Judge, new |
| M-A1 | Pass model | `MTLRenderPassDescriptor` with DEFERRED BEGIN. `SetFramebuffer` records, a clear before the first draw folds into `loadAction = Clear`, the first draw opens the encoder. This is V-A2 with Metal nouns and the mapping is one to one | Reuse (V-A2) |
| M-A2 | Pass model | The clear-only case is REPRODUCED deliberately: framebuffer plus clear plus `End` with no draw must still clear, because the incumbent forces it at two sites (`SetFramebufferCore` and `End`) and a golden depends on it | Reuse (V-A3) |
| M-A3 | Pass model | Any command illegal inside a render encoder (dispatch, blit, copy, mip generation, resolve) ends the encoder first. One invariant, one helper, one device-free test. Metal enforces the encoder exclusivity itself, so this is about WHERE the engine ends it, not whether | Reuse (V-A4) |
| M-A4 | Pass model | `storeAction = Store` unconditionally in v1, matching the incumbent, and deliberately NOT `DontCare` for depth. **The Metal upside is real and is recorded rather than taken**: on a tile-based deferred GPU a depth `DontCare` means the tile is never written out at all, which is a bigger win than the Vulkan equivalent. It still leaves contents undefined and the goldens require stability. Filed with a named consumer | Reuse (V-A6), upside recorded |
| M-A5 | Viewport | `SetFramebuffer` emits `setViewport` plus `setScissorRect` ON A FRAMEBUFFER CHANGE ONLY, replicating W6's identity guard exactly, plus the incumbent's own extra guard that a scissor is flushed only when the bound pipeline has `ScissorTestEnabled`. `ClipSpaceYInverted` stays false with no viewport trick at all, because Metal's clip space already matches | Reuse (W6, V-A5) |
| M-A6 | Pass model | MRT clears are fixed. The incumbent's `BeginCurrentRenderPass` loops over the clear array and writes `colorAttachments[0]` every iteration, so clearing attachment 1 clears attachment 0. Reproducing that is not parity, it is copying a bug no shipped golden reaches only because nothing clears a second attachment yet | Judge, corrected |
| M-B1 | Binding | **THE BINDING TABLE IS READ OFF THE EMITTED MSL, not counted on the CPU.** At shader-set creation the backend parses each stage's entry-point signature for its `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]` attributes and builds a per-program, per-stage table. Section 2.3 is the argument and it is the headline of this draft | Judge, against the incumbent |
| M-B2 | Binding | Vertex-STREAM buffer indices reproduce the incumbent's `ResourceBindingModel.Improved` formula EXACTLY (`NonVertexBufferCount + i`, in both the `MTLVertexDescriptor` layout index and the `setVertexBuffer` index), because that is the arithmetic the 36 committed `metal` goldens were baked under. A device-free test asserts it never collides with any index M-B1 read | Reuse, then asserted |
| M-B3 | Binding | The one-uniform-buffer-per-pipeline shader invariant STAYS, unchanged and unweakened, and no shader is touched in this phase. M-B1 is what makes it a CONVENTION rather than a load-bearing constraint, and lifting it is a follow-up with its own goldens, not a side effect of this one | Reuse (V-S6) |
| M-B4 | Binding | NO argument buffers, NO indirect command buffers, NO heaps, NO tier-2 bindless. Section 8.4 argues the decline against the idiomatic grain and names the trigger that reopens it | Reuse (V-D8's shape) |
| M-B5 | Binding | `ResourceBindingModel` leaves the engine's vocabulary entirely. It is a Veldrid concept whose only reader in the vendored fork is the Metal backend (`HlslCrossCompilePin` records this), and the native backend hardcodes the `Improved` arithmetic M-B2 pins. Nothing in `KhaozEngine.Gpu.Metal` names the concept | Judge, new |
| M-M1 | Memory | Every `UniformBuffer`-usage buffer is ONE `MTLBuffer` of `stride * FramesInFlight` in `MTLStorageModeShared`, whose `contents()` pointer is stable for the buffer's life. `IGpuBuffer` identity NEVER changes and the base is applied AT BIND. `FramesInFlight = 3` | Reuse (U1, V-M5) |
| M-M2 | Memory | `stride = align(size, 256)`, with the device's own minimum read and ASSERTED at or below 256. The incumbent reports 16 on macOS (`GetUniformBufferMinOffsetAlignmentCore`), the seam already documents 256 as safe across all three APIs, and every shipped slot is already 256-aligned. Taking the device's 16 would buy memory the engine does not need and lose the one number all three rings share | Reuse (V-M5), argued |
| M-M3 | Memory | The bind base is a plain `offset` argument to `setVertexBuffer:offset:atIndex:` and `setFragmentBuffer:offset:atIndex:`, so there is no positional array to compose and no VUID to satisfy. **This is the simplest of the three rings and the asymmetry is worth naming**: D3D11 pays a map window plus a 16-constant count, Vulkan pays a positional `pDynamicOffsets` array plus a bind-window range, Metal pays neither | Reuse, cheaper here |
| M-M4 | Memory | U3's two creation-time invariants are adopted VERBATIM: only `UniformBuffer` usage is ring-backed, and a ring-backed buffer never receives a non-uniform binding, which throws at creation as a documented BACKEND-DIVERGENT CREATION FAILURE | Reuse (U3, V-M7) |
| M-M5 | Memory | #484's correction is adopted WHOLESALE without re-deriving it: an off-timeline `UpdateBuffer` reaches EVERY segment, gated on the same completion read, a non-current segment deferred as a pending patch rather than waited on, the current segment always written ungated | Reuse (V-M8) |
| M-M6 | Memory | NO allocator. Metal has no `vkAllocateMemory` and no memory-type selection: `newBufferWithLength:options:` and `newTextureWithDescriptor:` allocate directly. V-M1 through V-M4 have no analogue and their absence is a simplification rather than an omission | Departure, absent |
| M-M7 | Uploads | Record-time bulk payloads (vertex, index, texture) take a per-list persistently mapped `MTLBuffer` STAGING ARENA and encode `copyFromBuffer:` on a blit encoder, pooled by size with a real retention cap. The incumbent creates and destroys a whole `MTLBuffer` per record-time write and its own TODO says so | Reuse (U4, V-M9) |
| M-M8 | Resources | Non-staging textures are `MTLStorageModePrivate` and staging textures are a `MTLStorageModeShared` buffer with the incumbent's SOFTWARE subresource layout reproduced byte for byte, asserted by a device-free table test against a checked-in table taken from the incumbent's own arithmetic | Reuse (V-C7) |
| M-M9 | Resources | Texture creation issues NO command buffer and NO clear, reproducing the incumbent. V-M10's setup-command-buffer machinery has no Metal analogue because there is nothing to schedule. The undefined-initial-contents question is answered by parity: the incumbent does not clear and the 36 goldens are green under that | Departure, absent |
| M-M10 | Views | Every `MTLTexture` view a resource set can name is created at RESOURCE creation from the declared usage bits. None at bind, none at draw, and no view factory reachable from the recording type, asserted by the same architecture test that holds M-B1's parse off the record path | Reuse (X1, V-M11) |
| M-H1 | Hazards | **AUTOMATIC HAZARD TRACKING IS KEPT.** Every resource is allocated tracked, which is Metal's default and the incumbent's configuration (`newBufferWithLength:options:` is called with options `0` and no texture descriptor sets `hazardTrackingMode`). Untracked resources plus explicit `MTLFence` and `memoryBarrierWithScope` are DECLINED, and section 2.5 is the argument | Judge, against the idiomatic prior |
| M-H2 | Hazards | The decline is CONDITIONAL in the opposite direction from V-M1's: V-M1 declined VMA on the condition that a synchronisation validator exists to catch what it gives up. **Metal has no synchronisation validator**, so the untracked path would be a hazard class with no detector anywhere in the net, on the one backend whose CI has a real device and therefore real reordering | Judge, new |
| M-H3 | Hazards | Automatic tracking orders GPU work against GPU work and says NOTHING about a CPU write racing a GPU read. The ring's completion gate is therefore load-bearing and is not made redundant by M-H1. The incumbent's device-level `UpdateBufferCore` is an ungated `memcpy` into a `Shared` buffer the GPU may be reading, and that is a real race this design closes | Judge, new |
| M-F1 | Fences | ONE device-wide monotonic COMPLETION COUNTER, advanced from each command buffer's `addCompletedHandler` block. `IGpuFence` holds a target and `Signaled` is a non-blocking read. `SupportsCompletionFences = true`, which is what the incumbent already reports, so this is PARITY rather than the upgrade it was on D3D11 | Reuse (V-F1) |
| M-F2 | Fences | The monotonicity makes the seam's documented fence ordering a theorem rather than a convention, exactly as V-F2 argued for the Vulkan timeline. The seam already states the Metal half of the premise in writing on `IGpuDevice.Submit(cl, fence)` | Reuse (V-F2) |
| M-F3 | Fences | The counter is advanced with an `Interlocked` MAXIMUM rather than an increment, because a Metal completion handler runs on an arbitrary internal thread. Commit order is the documented execution order, and a design that depends on completion CALLBACKS arriving in that order without saying so is depending on an unstated fact | Judge, new |
| M-F4 | Fences | `WaitForIdle` waits on the last committed value and is counted into the existing `DrainCount` and `DrainMs`. Real from day one, so there is no C6-style bet. The incumbent's `WaitForIdleCore` is already real (`waitUntilCompleted` on the latest submitted buffer), so nobody should look for phase 2's win here | Reuse (V-F4) |
| M-F5 | Fences | NO deferred-disposal retire list. An `MTLCommandBuffer` RETAINS every resource an encoder references, so a resource disposed mid-flight stays alive until the buffer completes. V-F9's machinery is unnecessary here and building it would be inventing a problem | Departure, absent |
| M-F6 | Liveness | The `DeviceLiveness` latch is reproduced EXACTLY (X3, V-F10). That is about the ENGINE's teardown order rather than about Objective-C refcounting, so M-F5 does not touch it. Device destruction drains first, unlike the incumbent's `PlatformDispose`, which drains and then releases in an order nothing asserts | Reuse (X3, V-F10) |
| M-W1 | Present | Present-path semantics are reproduced from the incumbent EXACTLY, including the SEPARATE command buffer that carries `presentDrawable:`. W1's lesson applies here with FULL force: the Metal golden leg is headless, so not one line of the swapchain path runs in CI on any leg ever | Reuse (W1, V-W1) |
| M-W2 | Present | The acquire TIMING is kept: `nextDrawable` for the next frame runs at present time, so the drawable is known before recording starts. That is the same property V-W3 kept and the same reason | Reuse (V-W3's kept half) |
| M-W3 | Present | The blocking `nextDrawable` is NOT replaced, because Metal offers no semaphore alternative: it is the only acquire and its block IS the frame pacing. What lands instead is the MEASUREMENT: `AcquireWaitCount` and `AcquireWaitMs`, which phase 3 already appended to `GpuDeviceCounters`, so this costs no seam change at all | Judge, ruled against changing it |
| M-W4 | Present | A frame whose `nextDrawable` returns nil binds a device-owned ORPHAN TARGET, records, submits and completes normally, and only its present is skipped, counted into `FramesBegun`. The incumbent instead silently drops every draw of that frame through `IsRenderable`, which is a whole frame of work discarded with nothing reported | Reuse (V-W4) |
| M-W5 | Present | `IGpuFramebuffer` wrapper identity is stable across resize BY CONSTRUCTION on Metal, because the colour attachment is fetched from the live drawable at descriptor-creation time. W2 asked D3D11 to behave like Metal here, so there is nothing to build. The DEPTH texture is the part that is recreated, and it gets the queued-resize treatment | Reuse (W2, V-W5), free |
| M-W6 | Present | `ResizeSwapchain` queues the size and applies it at the next present boundary on the submit thread, after a drain. The incumbent's `MTLSwapchain.Resize` recreates the depth texture and takes a new drawable with no drain and no synchronisation with in-flight work | Reuse (W3, V-W6) |
| M-W7 | Threading | No frame-long lock. Recording is lock-free and per list. One `_submitLock` covers commit, present and the resize apply. `MTLCommandQueue` is thread-safe by contract, encoders are not, and per-list command buffers are what makes that a non-issue | Reuse (W4, V-W8) |
| M-S1 | Shaders | GLSL 450 stays the single source. `SpirvCrossCompile` grows an MSL target in its BACK-END half (`VertexFragmentToMsl`, `ComputeToMsl`), which is exactly the split V-S3 created for this, evaluated against Metal's own goldens rather than against D3D11's | Reuse (V-S3, #462, #526) |
| M-S2 | Shaders | An `MslCrossCompilePin` beside `HlslCrossCompilePin`, with its own `Identity` derived FROM its constants, plus a per-program byte-equality DRIFT test. And, separately, a **one-off in-process parity measurement against the incumbent's own emission, taken and RECORDED before the first golden run**, because the drift test compares nothing against the incumbent and a wrong emission baked once passes forever | Reuse (S3, V-S2) |
| M-S3 | Shaders | The entry-point NAME is read out of the emitted MSL rather than assumed. SPIRV-Cross renames `main`, and the incumbent looks a function up by a name Veldrid supplies from a layer this backend does not have. Reading it is the same discipline as M-B1 and it costs the same parse | Judge, new |
| M-S4 | Shaders | S5's holed-signature workarounds STAY, and this backend is not an argument against them. They are FXC-and-WARP specific, Metal has always tolerated the holes, and the D3D11 leg ships indefinitely. Stated because it WILL be re-raised from a Metal seat where the sinks look pointless | Reuse (S5, V-S5) |
| M-S5 | Shaders | A compiled-pipeline cache persisted to disk through `MTLBinaryArchive`, keyed on the device registry ID, the OS build and the engine version, best-effort so any read or write failure is a silent discard. The incumbent compiles every program from MSL source on every launch and caches nothing | Reuse (S4, V-S7), spiked |
| M-S6 | Shaders | The MSL-index table gets S2's TABLE TEST: parse every emitted entry point across every shipped program, pair it against the pipeline's layout array, and assert the binding table the backend builds. Device-free, on every `dotnet test`, over all thirty-odd programs rather than a hand-picked few | Reuse (S2, V-S8) |
| M-C1 | Compute | Rule 1 falls out of the encoder model plus M-H1: ending the compute encoder and beginning a render encoder is an ordering point that automatic tracking honours. The seam's own rule 1 comment already names that mechanism for Metal and stays true | Reuse (V-C1's outcome) |
| M-C2 | Compute | Rule 2 is honoured AS WRITTEN and no seam member is added. Automatic tracking additionally orders dependent dispatches inside one command buffer, which is EVIDENCE for #461 and explicitly NOT a contract change. **After this phase THREE of four engine-owned backends answer yes**, which is VF10's quorum arriving | Reuse (V-C2) |
| M-C3 | Compute | Storage buffers are plain `device T&`. C2's RAW byte-address forcing was an HLSL artefact and V-C4 already ruled it has no analogue outside D3D11 | Reuse (V-C4) |
| M-C4 | MSAA | `MaxMsaaSampleCount` is READ OFF the incumbent's own `GetSampleCountLimit` and reproduced, with the citation pinned in a constant, and the implementation issue re-reads it before writing. The incumbent's version IGNORES both its arguments, which is arguably a defect, and reproducing it is still what parity means here | Reuse (V-C5), sharpened |
| M-C5 | MSAA | The resolve reproduces the incumbent's shape: a standalone render encoder with `storeAction = MultisampleResolve` and no draws. **Folding the resolve into the producing pass's store action is the Metal-native answer and it is deliberately NOT taken in v1**, because it changes what the producing pass writes out and `scene3d_hdr_msaa` is a committed golden | Reuse (W1's sequencing), upside recorded |
| M-C6 | Staging | `Map(staging, Read)` WAITS on the completion counter before returning the pointer, counted as a drain. The incumbent's `MapCore` returns `contents()` with no wait at all, which is correct today only because every engine caller drains first | Reuse (V-C8) |
| M-G1 | Capabilities | Field-by-field parity with the incumbent and ZERO permitted differences, plus the reflection-completeness check that the comparison covers every member of `GpuCapabilities` | Reuse (V-G1) |
| M-G2 | Diagnostics | `KE_METAL_DEVICE` per M-N1, and `SoftwareRasterizer` is FALSE on every Metal device, which is a real answer rather than an absent one: Apple ships no software Metal rasterizer, so the existing `softwareAdapter` telemetry field reports false with confidence rather than null | Reuse (V-G2), answered |
| M-G3 | Diagnostics | `KE_METAL_VALIDATION=0|1|shaders` maps onto the process-level `MTL_DEBUG_LAYER` and `MTL_SHADER_VALIDATION` environment variables the Metal runtime reads at device creation, plus an engine-side log line recording which tier was armed. **No package to install anywhere**, unlike V-T4's net-new CI work | Reuse (V-G3), cheaper |
| M-G4 | Diagnostics | Device loss is latched at the fault site from `MTLCommandBuffer.status` and `.error` in the completion handler, in EVERY configuration, and surfaces through the existing `deviceLossReason` header field. Metal reports command-buffer errors asynchronously and the incumbent reads neither | Reuse (V-G4) |
| M-G5 | Diagnostics | `MetalFrameCapture` moves off reflection. It exists today only because Veldrid does not expose its command queue, and this backend owns the queue. The Veldrid path keeps the reflection version for as long as the Veldrid Metal leg ships | Judge, new |
| M-G6 | Counters | `GpuDeviceCounters` is populated in full with NO seam addition. Every member phase 3 needed is already there, including the `AcquireWaitCount` and `AcquireWaitMs` pair row 17 appended, which M-W3 reads and which cost this phase nothing | Reuse, free |
| M-T1 | Tests | The 36 committed `metal` goldens run unmodified against the native backend on the SAME REAL DEVICE at the existing 0.06 absolute per-channel tolerance. No rebake, ever | Reuse (T1, V-T1) |
| M-T2 | Tests | A device-free native-call budget test aimed at the METAL fan-out class through a narrow `IMetalEncoderSink`, generic-constrained to a struct, covering the call classes that scale with draw count: per-element buffer, texture and sampler binds, vertex-stream binds, draws and dispatches, and ENCODER CREATIONS | Reuse (T2, V-T2), Metal classes |
| M-T3 | Tests | `NativeVsVeldridMetalCapabilityParityTests`, both structs in one process on the Metal leg, zero permitted differences | Reuse (V-T3) |
| M-T4 | Tests | Validation is a CI GATE in two tiers: `MTL_DEBUG_LAYER=1` on EVERY native-leg run, because it is an environment variable with no install and no measurable cost at this scale, and `MTL_SHADER_VALIDATION=1` on the scheduled run. Neither is a synchronisation validator and section 22 says so | Reuse (V-T4), retiered |
| M-T5 | Tests | The shared uniform-ring semantic tests gain a THIRD adapter in `KhaozEngine.TestSupport.Gpu`, covering section 9.4's seven shared rows, and the Metal ring must pass them before it renders a golden | Reuse (V-P5, V-T6) |
| M-T6 | Tests | The MSL binding-table test (M-S6), the staging-layout table test (M-M8), the recording-contract test, the encoder-invalidation test (M-R7) and the budget test all run device-free on every `dotnet test`, on the cheap legs rather than inside the golden filter | Reuse (V-T7) |
| M-T7 | Tests | A `metal-native` matrix leg on hosted `macos-26`, at the incumbent Metal leg's tier (`fullSuite: always`), guest in the `metal` family, sitting bake dispatches out, with `KE_METAL_REQUIRED=1` so a row that needs a native device fails rather than going dormant | Reuse (V-T8, V-T9) |
| M-RO1 | Rollout | Five gates, all green before any default flip (section 17) | Reuse (V-RO1) |
| M-RO2 | Rollout | `Metal` through Veldrid stays selectable by token INDEFINITELY. It is the kill switch for every STRUCTURAL decision here, which is why most bets carry no switch of their own | Reuse (V-RO2) |
| M-RO3 | Rollout | The headless default stays on Veldrid until gate 4. The `metal-native` CI leg is the continuous exercise | Reuse (V-RO3) |
| M-RO4 | Rollout | EVERY kill switch carries a decision deadline, sorted by 2.7's taxonomy: a switch keeping a SECOND IMPLEMENTATION alive is removed at its gate and the losing path deleted, a tuning knob may survive and its cell says on what condition | Reuse (V-RO4) |
| M-RO5 | Rollout | **The flip changes the macOS default, which is the DEV MACHINE default for the whole fleet.** That is a different blast radius from either predecessor and section 17 argues it in both directions rather than treating it as smaller or larger by assertion | Judge, new |

---

## 2. The arguments

Eight things this draft had to decide against its own prior or in spite of it. One comes first, because it is a
correction to the evidence base and it moves three of the arguments below.

### 2.1 The incumbent, established

Six load-bearing factual claims were checked in the sources before they were allowed to decide anything.

**The incumbent is `4.9.103`, and its `src/Veldrid/MTL/` tree is stock upstream `v4.9.0`.**
`Directory.Packages.props` pins `Veldrid 4.9.103`, served from the vendored nupkg.
`git diff v4.9.0 v4.9.103 -- src/Veldrid/MTL/` is empty and `git diff v4.9.103 master -- src/Veldrid/MTL/` is
also empty, so the reference implementation is 2023-era stock Veldrid and reading it on a `master` checkout is
safe for that directory. Phase 3 had to make exactly this check and one of its drafts got it wrong, so it is
made here rather than assumed.

**`src/Veldrid.MetalBindings/` is NOT identical, and the difference is an argument for M-P2.** Three files
differ between `v4.9.103` and `master`. Two are enum widths: `MTLResourceOptions` and `MTLStorageMode` are
declared `: uint` on the shipped package and `: ulong` upstream. The Metal types they mirror are `NSUInteger`,
which is 64-bit on every Mac in the fleet. A 32-bit enum passed where the runtime expects a 64-bit
`NSUInteger` is an ABI question the shipped bindings answer by luck rather than by construction, and it is
invisible from the managed side. That is a small, concrete, checkable instance of the general reason to own the
interop layer rather than vendor someone else's, and it is worth more than the general argument because it is a
fact rather than a worry.

**Record-time `UpdateBuffer` on Metal creates a buffer, ends the render encoder, blits, and destroys the
buffer. Verified, and it is worse than either sibling.** `MTLCommandList.UpdateBufferCore` creates a fresh
`BufferUsage.Staging` buffer through the resource factory, memcpys into it through the device-level update
path, calls `EnsureBlitEncoder`, encodes a `copy`, and disposes the staging buffer. The file carries its own
TODO saying the staging buffers should be cached and returned to a pool from the completion callback, so the
allocation churn is a known omission rather than a reading. What the TODO does not name is the expensive half.
`EnsureBlitEncoder` calls `EnsureNoRenderPass`, which calls `EndCurrentRenderPass`. So every record-time
uniform write ENDS THE RENDER COMMAND ENCODER.

That matters more on Metal than the equivalent does anywhere else, for two reasons that compound.

On a tile-based deferred GPU, which is every Apple Silicon Mac and therefore the hosted `macos-26` runner and
the fleet's own machines, ending a render encoder resolves tile memory out to device memory and beginning the
next one loads it back. `MTLFramebuffer.CreateRenderPassDescriptor` sets `loadAction = Load` on every colour
attachment and on the depth attachment, so the reload is unconditional. A per-draw uniform write therefore
costs a full store-and-load of every attachment in the pass.

And `EndCurrentRenderPass` sets `_graphicsPipelineChanged = true`, clears `_graphicsResourceSetsActive`, and
marks the viewport and scissor dirty, so the next draw re-issues the pipeline state, the cull mode, the front
face, the fill mode, the blend colour, the depth-stencil state, the depth clip mode, the stencil reference,
every element of every bound resource set, the viewport and the scissor. On the shipped model set that is
seven elements re-bound per stage they are declared for.

Structurally this is the same defect class as phase 2's blocking staging map and phase 3's render-pass split,
presenting a third way. It presents as "Metal is fine" for the same reason phase 3's presented as "Vulkan is the
best backend": two releases of renderer-side engineering already hoisted most of these writes out of the frame,
and #408 enumerates the residue, which is one partial write per caster per cascade, per water plane, per
overlay-mesh draw and per SpriteBatch slot.

**Device-level `UpdateBuffer` is an ungated memcpy into memory the GPU may be reading. Verified.**
`MTLGraphicsDevice.UpdateBufferCore` takes `DeviceBuffer.contents()` and copies straight into it. Every
`MTLBuffer` the incumbent creates is `MTLStorageModeShared` (`MTLBuffer`'s constructor calls
`newBufferWithLength:options:` with an options value of `0`, which is shared storage plus the default cache
mode), so that pointer is CPU-visible and GPU-visible at once with no synchronisation of any kind. There is no
fence, no frame index and no wait. It has never produced a reported defect, which is a fact about the engine's
call sites rather than about the code: section 22 records that this is one of the things this design fixes
without being able to measure the fix.

**The MSAA limit ignores its own arguments. Verified.** `MTLGraphicsDevice.GetSampleCountLimit(format,
depthFormat)` walks `_supportedSampleCounts` from the top and returns the first supported count, never reading
either parameter. `_supportedSampleCounts` is filled at device creation from
`MTLDevice.supportsTextureSampleCount`, which is also format-blind. So `GpuCapabilities.MaxMsaaSampleCount` on
Metal today is "the highest sample count this device supports for anything", and `AntiAliasing.ResolveFor`
clamps against it and `scene3d_hdr_msaa` is baked under it. V-C5's ruling applies with no modification: read the
computation off the incumbent, pin the citation, do not invent one. The temptation to "fix" it while porting is
exactly the C4 failure phase 2 had to correct in flight, where a first draft asked the driver a different
question and then asserted equality with the incumbent.

**A frame with no drawable silently discards every draw. Verified.**
`MTLSwapchainFramebuffer.IsRenderable` is `!CurrentDrawable.IsNull`, `BeginCurrentRenderPass` returns false when
the framebuffer is not renderable, `EnsureRenderPass` propagates the false, and `PreDrawCommand` returns false,
so `DrawCore` and `DrawIndexedCore` issue nothing. `MTLSwapchain.GetNextDrawable` sets `_drawable` from
`CAMetalLayer.nextDrawable`, which returns nil when the layer has no drawable to give. The whole frame's
rendering is discarded with nothing logged and nothing counted, and `FramesBegun` on a native backend would
still count it. V-W4 answered the same question for Vulkan and its answer ports (M-W4).

**Two smaller things, checked because they decide a row each.** `MTLPipeline` builds its `MTLVertexDescriptor`
layout indices and its `MTLCommandList` vertex-buffer bind indices from the same
`ResourceBindingModel.Improved` expression (`NonVertexBufferCount + i`), and `GpuDeviceContext` passes
`ResourceBindingModel.Improved` at the one windowed site and through `DefaultHeadlessOptions` at the headless
one, so `Improved` is what every shipped `metal` golden was baked under and `Default` is dead in this engine.
And `MTLFence` is a `ManualResetEvent` set from the command buffer's `addCompletedHandler` block, which is a
real completion signal, which is why `VeldridMap.SupportsCompletionFences` already answers true for Metal and
why M-F1 is parity rather than an upgrade.

### 2.2 Recording: the encoder model, and the two defects that cancel each other

**The prior's default is phase 3's ruling and it survives.** A `MTLCommandBuffer` between its creation and its
`commit` is an engine-invisible op stream the driver encodes into its own format, exactly as a
`VkCommandBuffer` is. Recording into a managed array first means encoding twice, allocating once more, and
moving the driver-side encode inside the submit lock. Phase 2's section 16 predicted this before either phase-3
draft existed, phase 3 confirmed it, and Metal gives no new reason to revisit it. M-R1, and there is no M1
analogue in this design either.

**Where the ported shape is WRONG, and this is the first place the prior loses.** V-R2 gives each list
`FramesInFlight` `VkCommandPool` objects and resets one per `Begin`, because resetting a whole pool is the
documented fast path and `RESET_COMMAND_BUFFER` pushes the driver onto a slower per-buffer allocator. Metal has
no analogue at any level. A command buffer is single-use: there is no reset, no pool object, and no allocator
to choose between. `commandBuffer` on the queue is the allocation, and the queue pools internally.

So M-R2 rules against the port: no command-buffer ring exists, `Begin()` takes a fresh buffer, and the
`FramesInFlight` depth moves entirely to the uniform ring, which is the only thing that still needs it. Phase 3
deliberately made the two share one number so there was one number to move if MV3 said 3 was wrong. Here there
is only one consumer of that number, which is simpler and is worth saying plainly rather than pretending the
sharing survived.

**What replaces the pool's implicit bound.** `MTLCommandQueue` has a maximum number of uncommitted command
buffers, and `commandBuffer` BLOCKS when it is reached. That is a real bound with a real blocking behaviour and
it is not the same bound as the ring's. Section 6.1 names it, the probe reads it where the API allows, and a
device-free test asserts the engine never holds more uncommitted buffers than `FramesInFlight` plus the one
present buffer, so the bound is never approached rather than being relied on.

**The second-order finding, and it is the one a reviewer should check first.** The incumbent's
`EndCurrentRenderPass` clears `_graphicsResourceSetsActive` but does NOT clear `_vertexBuffersActive`. Vertex
buffer bindings are ENCODER state on Metal and do not survive an encoder change, so on the face of it every
render-pass restart leaves the new encoder with no vertex buffers bound and every subsequent draw reads
garbage. It does not, because of a second defect: `PreDrawCommand`'s vertex-buffer loop issues `setVertexBuffer`
when the flag is false and never sets the flag to true, unlike the resource-set loop directly above it which
does. So the flag is permanently false, every stream is re-bound on every draw, and the missing invalidation
can never be observed.

Two consequences, and both are decisions rather than trivia. The incumbent pays one `setVertexBuffer` per
stream per draw, unconditionally, which is a per-draw fan-out on the hot path and belongs in the budget test's
marginals as a number the native backend must beat rather than match. And a native backend that ports the
redundancy tracking without porting the invalidation ships a corruption that no golden would catch, because the
goldens do not restart a render pass mid-scene. M-R7 makes the invalidation explicit and gives it its own
device-free test, and the test is written as "end the encoder, draw, assert the stream was re-bound" rather than
as a state assertion, so it fails on the corruption rather than on the bookkeeping.

**Concurrency.** N lists record concurrently and genuinely, because each holds its own command buffer and its
own encoders and nothing shared is read or written during recording. That is V-R4's property obtained from
Metal's own object model rather than from a barrier design, and it is a BACKEND property exactly as it is
there. The portable seam contract stays at one open recording per device and
`IGpuCommandList.Begin`'s XML doc gains a Metal sentence, which the doc's own structure already anticipates:
it currently names D3D11 and Vulkan and says the same code "on Metal" is a half-recorded frame, and that
sentence becomes wrong for the native backend on the day this ships.

### 2.3 The binding model, and why the CPU-side count has to go

This is the headline of this draft and it is the one place the reuse-first prior refuses to reuse the
incumbent.

**What the incumbent does.** `MTLResourceLayout`'s constructor assigns each element a per-kind, declaration-order
slot within its layout, with uniform and both structured kinds SHARING one buffer counter, both texture kinds
sharing a texture counter, and samplers their own. `MTLCommandList.GetBufferBase`, `GetTextureBase` and
`GetSamplerBase` then sum the preceding layouts' counts, so a resource's Metal index is its declaration position
flattened across the pipeline's layout array, per kind. `BindBuffer`, `BindTexture` and `BindSampler` issue
`setVertexBuffer` / `setFragmentBuffer` / `setVertexTexture` / `setFragmentTexture` / `setVertexSamplerState` /
`setFragmentSamplerState` at that index, with the `Improved` vertex-buffer offset applied on the vertex stage.

**What the shader actually got.** Metal has no binding decorations. SPIRV-Cross assigns each resource a
`[[buffer(n)]]`, `[[texture(n)]]` or `[[sampler(n)]]` index of its own, and this repo has three independently
recorded production incidents saying that index follows FIRST-REFERENCE order rather than the `set` and
`binding` decorations:

1. The multi-texture material pass. `ModelFrag` sampled the normal map before the albedo map textually, so on
   Metal the albedo sampler read the normal texture and untextured meshes rendered flat-normal coloured. The
   symptom was INVARIANT to every binding-layout rearrangement tried, including splitting the frame UBO into its
   own set, which is the fact that identifies the mechanism: nothing in the resource-binding layer can move it.
2. The terrain splat pass, the engine's first pipeline to bind two uniform buffers. The params UBO read the
   frame UBO's `ViewProj` bytes. Also invariant to set arrangement: one set with two UBOs and two sets with one
   each were broken identically.
3. The perspective outline pass. `EdgeFrag` sampled Color, Depth, Normal while binding Color, Normal, Depth, so
   the normal and depth samplers swapped and the crease term read depth data.

And the engine already SHIPS a check for the same mechanism.
`ShaderValidation.CheckMslBufferSlots` cross-compiles every compute source to MSL, parses the `kernel void`
entry point's `[[buffer(n)]]` arguments, and compares the KIND ORDER against the reflected layout order, with a
message that says in as many words that "Metal buffer indices are assigned in first-reference order while the
resource layout is counted in binding order". It exists because a kernel read its cascade tile size out of the
spectrum buffer, got zero, and produced a NaN surface.

**So the one-uniform-buffer-per-pipeline rule is a WORKAROUND, not a Metal limit.** With exactly one buffer in
the pipeline, first-reference order and declaration order agree trivially, whatever the shader does. The rule
is written up in `docs/DEPENDENCY-SEAMS.md` as a GPU-backend invariant and it is real as a statement about the
incumbent. It is not a statement about Metal. Metal binds a buffer to the index the function declares, and the
only question is whether the CPU knows what that index is.

**The ruling: read the emitted MSL (M-B1).** At `CreateShadersFromSpirv` the backend already has the MSL text
for both stages, because it produced it. It parses each stage's entry-point signature for `[[buffer(n)]]`,
`[[texture(n)]]` and `[[sampler(n)]]` and builds a per-stage table mapping the declared resource to the index
the compiler actually chose. Resource-set activation binds through that table. The CPU-side count disappears.

Four things make this the reuse-first answer rather than an invention.

It changes NO shader and NO emission. The GLSL is untouched, the cross-compile options are untouched, and M-S2's
byte-equality drift test plus the one-off parity measurement establish that the MSL this backend consumes is the
MSL the incumbent consumes. So the 36 committed `metal` goldens carry over with no rebake, which is the whole
parity position.

The parse ALREADY SHIPS, in `ShaderValidation.BufferKindsFromEntryPoint`, including the depth-matched
parenthesis scan that a naive parser gets wrong because every argument carries a `[[buffer(0)]]` attribute of
its own. This is promoting a shipped, incident-driven diagnostic into the binding path, not writing a parser.

It is checkable device-free, over the whole shipped set. M-S6's table test parses every emitted entry point
across every program and asserts the table, on every `dotnet test`, on the free Linux leg. The failure mode this
whole area has is "everything compiles and every pixel is wrong", and S2 and V-S8 both answered it with exactly
this test.

And it strictly DOMINATES the incumbent. Where first-reference order happens to equal declaration order, the
table is the same table and the backend behaves identically. Where it does not, the incumbent is wrong and this
is right. There is no case where reading the emitted index loses.

**What it does NOT do, stated so nobody over-reads it (M-B3).** It does not lift the one-UBO invariant. Lifting
it means changing shaders, which puts all three backends' pixels in play at once, which is the exact risk shape
phase 2 refused for SPIRV-Cross direct bindings and phase 3 refused for descriptor indexing. What it does is
turn a load-bearing constraint into a convention, so the follow-up that lifts it is a shader change with its own
goldens rather than a backend rewrite. That follow-up is filed with the splat pass named as its consumer.

**The vertex-stream half stays the incumbent's (M-B2).** `Improved`'s `NonVertexBufferCount + i` is what the
`MTLVertexDescriptor` layout indices and the `setVertexBuffer` indices were both built from, and the goldens
were baked under it. It is kept verbatim and then ASSERTED: a device-free test checks that
`NonVertexBufferCount` exceeds every buffer index M-B1 read out of the vertex function, for every shipped
program, so the no-collision property becomes a checked fact instead of an inherited assumption. That assertion
is cheap and it is the only thing standing between the two index spaces.

**The strongest counterargument, and the answer.** Parsing generated source text is brittle, and a SPIRV-Cross
version change could move the emission out from under the parser. Three answers. The parse is pinned by M-S6's
table test over the whole shipped set, so a moved emission is a red test on the cheap leg rather than a wrong
pixel on one backend. `MslCrossCompilePin` plus the byte-equality drift test already fail on any change to the
emission at all, so the parse cannot silently drift under a compiler upgrade that nothing else noticed. And the
alternative on offer is not "something robust", it is the CPU-side count that produced three incidents. A
parser with a test is better than an assumption without one.

**The reopening trigger.** If #462's direct SPIRV-Cross bindings ever land, `add_msl_resource_binding` lets the
engine DICTATE the indices instead of reading them, at which point M-B1's parse is deleted and the table is
authored. That is the right shape and it is the wrong phase: taking it here would put D3D11's 36 goldens and
both documented WARP corruption workarounds in play alongside a new backend. Filed, with M-B1 named as the seat
it replaces.

### 2.4 The schedule: two states, and a flush that is neither neighbour's

**Three states or two, a third time, and the answer is two for a different reason.** Phase 2 kept three because
`DynamicOffsetsOnly` skips textures and samplers on an API that binds resources one at a time. Phase 3 collapsed
to two because a Vulkan descriptor bind is ONE call whether one offset moved or every image changed, so the
third state changed no call and skipped no work.

Metal is phase 2's shape, not phase 3's: there is no "bind a set" call at all, and an activation is one
`set*Buffer` / `set*Texture` / `set*SamplerState` per element per stage the element is declared for. So the
D3D11 argument for the third state applies here on its face.

It is still declined, and the reason is M-M3. On Metal the ring base is an `offset` argument to the same
`setBuffer` call that binds the buffer, so an offsets-only rebind is exactly the buffer calls of that set and
nothing else. That is not a THIRD state, it is the two-state model's dirty case with the texture and sampler
binds skipped because nothing about them changed, which two-state dirty tracking per ELEMENT already expresses.
M-R5 therefore tracks dirt per slot with two states and computes the emitted call set from what actually
differs, which gets phase 2's saving without phase 2's bookkeeping. A device-free budget test pins it: an
offsets-only rebind of the model set must be exactly the buffer calls for its visible stages, and a full
activation must be the whole element set once.

**Clause 5 has no occupant here, and that is a ruling rather than an omission.** D3D11 drains pending sets under
the OUTGOING layout because the layout decides register numbering. Vulkan invalidates from the first
INCOMPATIBLE set onward because a pipeline layout mismatch invalidates bound descriptors. Metal has no pipeline
layout object at all and no compatibility rule to compute, so M-R6 reproduces the incumbent's wholesale clear on
`SetPipelineCore`. That is the blunt version phase 3 rejected for Vulkan, and it is correct here because there
is nothing finer to compute, not because the fine version was too hard. Saying which of those two it is matters:
a future reader who finds the blunt clear should find this paragraph rather than file a refinement.

**What DOES occupy the slot is M-R7.** Ending a render encoder invalidates every bind, including the vertex
streams, and 2.2 explains why the incumbent gets away with not saying so. That is the Metal-shaped hazard the
clause-5 slot holds on the other two backends, and it earns the same treatment: stated precisely, tested
device-free, and named as the thing a redundancy-tracking optimisation must not break.

### 2.5 Hazard tracking: keep automatic, and the condition that decides it

**The question, stated precisely.** Metal tracks hazards automatically by default. Every resource allocated
outside a heap carries `MTLHazardTrackingModeDefault`, which is tracked, and the driver inserts the
synchronisation between encoders that read and write the same resource within a command buffer. Untracked is an
explicit opt-in (`MTLResourceHazardTrackingModeUntracked` in `MTLResourceOptions`, present in the incumbent's
bindings and used nowhere in its backend), and it hands the application `MTLFence`, `updateFence`, `waitForFence`
and `memoryBarrierWithScope` to do the work itself.

The idiomatic prior will want untracked, and it has a real case. Tracking costs driver-side bookkeeping per
encoder over every referenced resource, and the engine's dependency graph is simple enough that hand-written
fences would express it in a handful of calls. Phase 3 made the structurally identical argument for its own
barrier model and won it.

**It loses here, on a condition rather than on a preference.** V-M1 declined VMA and wrote the decline as
CONDITIONAL on V-T4's synchronisation-validation gate existing, because that gate is the only instrument in the
net that sees an aliasing or hazard defect a golden on a software rasterizer cannot. The design said outright
that if the sync gate were ever dropped, the VMA decline must be re-argued.

**Metal has no synchronisation validator.** `MTL_DEBUG_LAYER` is API validation: it checks argument ranges,
encoder state, pipeline compatibility and resource usage. `MTL_SHADER_VALIDATION` checks in-shader bounds and
memory access. Neither of them tracks read-after-write hazards across encoders, and there is no third
environment variable that does. So an untracked Metal backend would carry the exact hazard class phase 3 spent a
whole new CI job on, with no detector anywhere in the net.

And it would carry it on the ONE backend whose CI has a real device. Phase 3's missing-barrier class was
invisible on lavapipe because a software rasterizer executes with far stronger implicit ordering than real
hardware. The Metal leg runs on hosted `macos-26` with a real GPU, which is a genuine advantage of this phase
and cuts both ways: real hardware really reorders, so a missing fence on this leg is a flaky golden rather than a
consistently green one, and a flaky golden on a five-legged blocking matrix is the worst failure shape the
program has.

**Decision: M-H1, keep automatic tracking, with the condition written down.** The decline of untracked is
conditional on the absence of a synchronisation validator, exactly as V-M1's decline of VMA was conditional on
the presence of one. If Apple ships a synchronisation validator, or if the fleet builds a `MTLCaptureManager`
trace-based detector that can see a missing fence, this reopens with a named instrument. Filed with that
trigger, not as a permanent no.

**Three things the decision does not buy, said out loud so nobody assumes it did.**

Automatic tracking orders GPU against GPU. It says nothing about a CPU write racing a GPU read, so the uniform
ring's completion gate is load-bearing and is not made redundant (M-H3). The incumbent's ungated device-level
`UpdateBufferCore` is that exact race and this design closes it.

Automatic tracking is per command buffer plus the queue's commit-order execution. The seam's own
`Submit(cl, fence)` documentation already states the commit-order half in writing, and the rule-1 comment already
states the encoder half. Neither sentence changes, which is the parity position.

And the cost is unmeasured. MM5 records that: there is no untracked build to A/B against unless somebody writes
one, so "tracking is cheap enough" is an inference from the incumbent's field behaviour rather than a
measurement. Section 22 carries it.

### 2.6 The pass model: the mapping is one to one, and the tiler upside is recorded rather than taken

**Phase 3's section 19 predicted this and it is right.** `MTLRenderPassDescriptor`'s per-attachment `texture`,
`loadAction`, `clearColor` / `clearDepth` and `storeAction` map onto `VkRenderingAttachmentInfo`'s
`imageView`, `loadOp`, `clearValue` and `storeOp` almost member for member. So V-A1 through V-A6 port with
Metal nouns and no argument: deferred begin, clear folding into `loadAction`, the clear-only case reproduced
deliberately, any encoder-illegal command ending the pass first, and `storeAction = Store` unconditionally.

The incumbent already has most of this shape. `ClearColorTargetCore` and `ClearDepthStencilCore` call
`EnsureNoRenderPass` and stash the value, `BeginCurrentRenderPass` folds the stashed values into `loadAction =
Clear`, and both `SetFramebufferCore` and `End` force a begin-and-end pair when a framebuffer was bound with
clears pending and nothing drew. That last one is V-A3's clear-only case, forced by the incumbent at two sites,
and a golden depends on it. Porting it is not inheriting an accident.

**One thing is corrected rather than reproduced (M-A6).** `BeginCurrentRenderPass` iterates the clear array and
writes `rpDesc.colorAttachments[0]` on every iteration, so a clear recorded for attachment 1 lands on attachment
0. No shipped scene clears a second colour attachment, so no golden sees it, which is why it is a correction
rather than a golden-visible change. Reproducing it would be copying a bug the seam's own
`ClearColorTarget(uint index, ...)` signature invites a consumer to reach.

**The tiler upside, recorded and not taken (M-A4, M-C5).** Two Metal-native wins are available in this area and
both are declined for v1 on W1's sequencing argument.

`storeAction = DontCare` on depth means the depth tile is never written to device memory at all on a tile-based
deferred GPU, which is a larger win than the Vulkan equivalent and a real one for the shadow atlas and the depth
prepass. It also leaves contents undefined, undefined is not stable across runs, and the goldens require
stability on the same device. V-A6 rejected it for Vulkan on exactly that reasoning and the reasoning does not
weaken because the prize is bigger. Filed with the shadow atlas and the depth prepass named, and with the
determinism argument named as the thing that follow-up owes.

`MTLStoreActionMultisampleResolve` folds the MSAA resolve into the producing pass's store action, which is the
Metal-native shape and removes the resolve encoder entirely. The incumbent instead opens a standalone render
encoder with `loadAction = Load` and `storeAction = MultisampleResolve` and immediately ends it, and its own
comment says the approach destroys the source texture's contents. `scene3d_hdr_msaa` is a committed golden baked
under that shape. So v1 reproduces the standalone encoder and the fold is filed. This is W1 applied twice, and
both times the argument is the same: the soak has to be able to attribute a regression to the recording model
and the memory model, and a v1 that also changes what a pass writes out cannot.

### 2.7 The drawable, and the one place W1's lesson binds hardest

**The Metal golden leg is headless, so the present path has ZERO CI coverage.** The Metal snapshot tests go
through `CreateHeadless`, which builds no swapchain and no `CAMetalLayer`. Not one line of `MTLSwapchain`,
`MTLSwapchainFramebuffer`, `nextDrawable` or `presentDrawable` runs in CI on any leg, ever. That is MV9's
position verbatim, on a leg that otherwise has the best coverage in the matrix, and it is the reason M-W1
reproduces the present path exactly.

**What the incumbent does.** `SwapBuffersCore` creates a SECOND `MTLCommandBuffer` from the queue purely to call
`presentDrawable:` and commits it, then calls `GetNextDrawable`, which releases the previous drawable and takes
`CAMetalLayer.nextDrawable` for the next frame. `nextDrawable` BLOCKS when the layer's drawable pool is
exhausted, which is the CPU throttle that makes the frame loop wait for the display.

**Three things a Metal-idiomatic prior will want to change, and all three are declined for v1.**

The extra command buffer looks wasteful and Apple documents calling `presentDrawable:` on the frame's own
buffer. It cannot be done without changing what `Submit` means: the seam's `Present()` is a separate call, so the
frame's buffer is already committed by the time `Present` runs, and deferring the commit until `Present` puts a
recorded frame's execution behind a call the consumer may not make. One extra command buffer per frame is a
rounding error and the change is a submit-semantics change wearing a performance costume. Declined.

The blocking `nextDrawable` looks like phase 3's CPU acquire stall, which V-W3 replaced with a semaphore. There
is no semaphore alternative on Metal. `nextDrawable` is the only acquire and there is no signalling variant, so
the block is not a synchronisation choice the way the Vulkan fence-wait was. What IS available is
`CAMetalLayer.maximumDrawableCount` and `allowsNextDrawableTimeout`, which are pacing knobs and belong to #380
with its own measurement, not to the phase that must isolate the backend swap. Declined, and M-W3 lands the
MEASUREMENT instead: `AcquireWaitCount` and `AcquireWaitMs`, which phase 3 already appended to
`GpuDeviceCounters` for exactly this shape, so this phase reads them for free and adds no seam member at all.

The layer configuration (`framebufferOnly = true`, `pixelFormat`, `drawableSize`, `displaySyncEnabled` for
vsync) is reproduced field for field, including the incumbent's feature-set gate on `displaySyncEnabled`, which
section 14 flags as fragile and section 22 records as untested on anything but a current Apple Silicon Mac.

**One thing IS changed, and it is a defect rather than a behaviour (M-W4).** A frame whose `nextDrawable`
returns nil currently discards every draw silently. V-W4 answered the same question and its answer ports without
modification: the wrapper is repointed at a device-owned ORPHAN TARGET, one colour texture at the current
drawable size clamped to a minimum of one by one, matching the swapchain framebuffer's shape and carrying its
depth attachment. The frame records, submits and completes exactly like any other, and only its present is
skipped. `FramesBegun` counts it, because a skipped present is not a skipped frame and it is the denominator
every per-frame figure is divided by. The orphan target is created lazily the first time the path is reached and
destroyed at the next successful `nextDrawable`.

**And resize gets the queued-boundary treatment (M-W6).** `MTLSwapchain.Resize` currently recreates the depth
texture and takes a new drawable inline, with no drain, while previous frames may still be reading the depth
texture it just released. W3 and V-W6 both queue the size and apply it at the present boundary on the submit
thread after a drain, and the same rule lands here. The colour half is free, because M-W5's identity stability
falls out of the placeholder-texture design, which is the one place phase 2 explicitly asked D3D11 to behave
like Metal.

### 2.8 The #531 extraction: three things, in a named order, with an exit

**The question #531 asks.** V-P4 extracted nothing at two implementations, on the rule of three and on the
argument that the eventual OUTLIER was D3D11, so an abstraction shaped by D3D11 and Vulkan would be asked to fit
Metal afterwards. Its own text says the code most likely to be genuinely common is the code in
`KhaozEngine.Gpu.Vulkan`, and that waiting for Metal means the common shape is OBSERVED rather than predicted.
Phase 4 is the trigger. So this draft owes an answer with the third implementation actually in hand.

**Candidate by candidate, against three implementations rather than two.**

**The uniform ring's SEGMENT POLICY: extract.** Section 9.4's inventory has ten rows and names seven as shared
policy, and the shared semantic tests of V-P5 and V-T6 already run those seven against two backends through one
test-only interface. What the third implementation adds is the observation that the POLICY reduces to two
primitives and nothing else: "write these bytes at this offset" and "has the GPU reached completion value N".
D3D11 already expresses both as internal interfaces (`ID3D11RingMemory`, `ID3D11CompletionRead`). Vulkan
expresses the first as a persistently mapped pointer and the second as `IVulkanTimelineSemaphore`. Metal
expresses the first as `contents()` and the second as M-F1's counter. Three independent implementations arriving
at the same two-primitive shape is what the rule of three is for. The segment selection, the fence gate, the
backpressure counting, #484's every-segment reach with its gating and its pending-patch queue, and the
record-time-writes-stay-current rule all live above those two primitives and are byte-for-byte the same policy
three times. Extract it as a generic type over the two primitives, into `KhaozEngine.Gpu/Internal/`.

**The completion TIMELINE: extract.** `D3D11MonotonicFenceTimeline`, `VulkanTimeline` and Metal's counter are
the same object: a monotone unsigned value, advanced on submit, read non-blocking, waited on blocking, with
`WaitTotals` counting drains. Both existing backends already have a `WaitTotals` type with the same name. The
PRIMITIVE underneath differs (an `ID3D11Fence`, a `VkSemaphore`, a completion callback) and the BOOKKEEPING does
not. Extract the bookkeeping over a one-method primitive.

**The counting harness's MARGINAL-ASSERTION helpers: extract.** `D3D11NativeCallLog` and Vulkan's counting sink
both produce a call log and both are asserted the same way: structural invariants, per-draw marginal deltas
between two scene shapes, trace identity between an instanced and a non-instanced draw, and upper bounds on
fan-out. That ASSERTION SHAPE is what T2 and V-T2 both had to write out longhand and what a third backend would
write a third time. Extract the helpers into `KhaozEngine.TestSupport.Gpu`, which already houses the shared ring
adapters and ships nothing.

**The record-then-flush SCHEDULE: do NOT extract, and the third implementation is what settles it.** Phase 2
flushes three-state per slot and emits one array call per kind per stage. Phase 3 flushes two-state and emits one
`vkCmdBindDescriptorSets` per contiguous run. Metal flushes two-state and emits one call per element per stage
with no run concept at all, and its pipeline-switch clause is wholesale where the other two are surgical and
opposite to each other. What survives all three is about a hundred lines of dirty-array bookkeeping whose
extraction needs a generic activation callback, a generic slot record and a generic invalidation policy. That is
three abstractions to save a hundred lines, and the abstraction would be the thing a future backend has to fight.
Declined, with the reason recorded so it is not re-raised.

**The generic EMITTER interface: do NOT extract, and V-P4's exclusion is CONFIRMED rather than reopened.** Its
argument was that promoting it makes a second copy of `IGpuCommandList` in engine types that every backend must
keep in sync with the seam, and that the D3D11 emitter exists to drive two D3D11 drivers, a problem only D3D11
has. Metal's sink is a third shape again: encoder-level `setVertexBuffer`, `setFragmentTexture`,
`drawIndexedPrimitives` and ENCODER CREATIONS, which is a call class neither neighbour has. Three shapes, one
exclusion, unchanged.

**The ORDER, and it is the load-bearing half (M-P6).** The extraction row lands AFTER the Metal ring and the
Metal timeline are written, green, and passing the shared semantic tests as a third adapter. Extracting first
and implementing into the extraction is exactly the failure V-P4 avoided, moved one phase later: the shape would
be D3D11-and-Vulkan's with Metal asked to fit it, which is the thing waiting was supposed to prevent. And the
row carries a written exit: if the Metal ring or the Metal timeline does not fit the shape the other two share,
the row closes as NOT PLANNED with that reason, and section 9.4's inventory keeps carrying the policy. A
decision to extract that cannot fail is not a decision.

**What this costs, stated plainly.** Two production types move packages, which is a real diff across
`Gpu.D3D11` and `Gpu.Vulkan` in a release that is otherwise additive, and phase 2's frozen native-call
marginals plus phase 3's are the regression proof that the move changed nothing. That proof is cheap because
both are device-free tests that already run on every `dotnet test`.

### 2.9 Three places the Metal-idiomatic prior wins, or would if this draft let it

The rulings above go one way often enough that a reader could conclude the reuse prior swept it. It did not, and
naming where the other prior is right is worth more than pretending otherwise.

**Automatic tracking is a genuine cost and this draft cannot price it.** M-H1 declines untracked on the absence
of a detector, which is an argument about RISK and not about cost. If the idiomatic draft has a measurement, or
proposes one this draft did not, it should win that row. MM5 is written as an observation rather than a bet
precisely because this draft has no instrument pointed at it.

**The tiler store actions are a real Metal win and this draft defers both of them.** M-A4's depth `DontCare` and
M-C5's folded resolve are the two places where Metal's model is genuinely better than a ported one, and both are
filed rather than taken. If the idiomatic draft can pay the determinism argument for the first and the golden
argument for the second inside this phase, that is a better answer than a follow-up, and this draft's only
defence is W1's sequencing, which is a real argument and not an unanswerable one.

**And the command-buffer model is Metal's, not a port (M-R2).** This draft rules against V-R2 on the first row
it reaches, which is the prior conceding on the mechanism it would most have liked to reuse. The honest reading
is that Metal's recording model is closer to Vulkan's than to D3D11's in every respect the seam touches and
different from both in its object lifetimes, and a draft that claimed otherwise would be arguing from the prior
rather than from the API.

---

## 3. Package, layering and the interop

`KhaozEngine.Gpu.Metal`, one assembly, referencing `KhaozEngine.Gpu` and `KhaozEngine.Diagnostics` and nothing
else. Target `net10.0`, NOT `net10.0-macos`, so the assembly compiles and its device-free tests run on the Linux
`ci.yml` leg and on both Windows legs, and so `KhaozEngine.Render.Tests` can reference it unconditionally. Every
entry point carries `[SupportedOSPlatformGuard("macos")]` and every Objective-C-touching body is
`[MethodImpl(MethodImplOptions.NoInlining)]` behind an `OperatingSystem.IsMacOS()` guard. That is P1's pattern
and it is already proven in this repo twice over: `D3D11ThreadingProbe` on the Windows side, and
`MetalFrameCapture`, which lives in `KhaozEngine.Gpu` today with `DllImport("/usr/lib/libobjc.A.dylib")`
declarations and loads harmlessly on Linux and Windows because nothing calls it there. With warnings as errors,
CA1416 makes the compiler enforce the boundary rather than a convention.

Guard work the package creates, all mechanical and all precedented:

- `ArchitectureTests.OptInBackends` gains `Gpu.Metal`, which then enforces
  `OptInBackends_AreNotReachableFromAnyUmbrella`.
- `ArchitectureTests.ThirdPartyHomes` gains NOTHING, which is a first: this is the only backend with no
  third-party package at all. `EveryThirdPartyPackage_IsDeliberatelyMapped` is unaffected, and the absence is
  worth a comment in the test so a later reader does not add a speculative row.
- `KhaozEngine.slnx` gains the project, which force-adds `KhaozEngine.Tests` to the selective-test set, so the
  architecture guards run on the landing PR.
- `check-doc-versions.sh` requires a bolded `**KhaozEngine.Gpu.Metal**` catalog row in the root `README.md` and
  a `KhaozEngine.Gpu.Metal/README.md` shipped via `<PackageReadmeFile>`.
- `GpuPublicApiTests` extends its walk to the new assembly, for `Veldrid` in the public surface.
- The no-Veldrid pair, in BOTH forms: the csproj read and the IL reference walk. The walk is the load-bearing
  one, because Veldrid is in the transitive closure through `KhaozEngine.Gpu` whatever the csproj declares.
- A new assertion, the Metal-specific member of that family: the backend names no member of `SpirvCrossCompile`'s
  HLSL half. It takes the MSL half and the front end only, which is the third arm of the architecture test
  V-S3 created.
- `docs/DEPENDENCY-SEAMS.md` gains the third instance of the out-of-package backend edge.

### 3.1 The interop layer (M-P2)

**There is no maintained managed Metal binding, and that is the whole argument.** Phase 2 took
`Vortice.Direct3D11` and phase 3 took `Silk.NET.Vulkan`, both on the reasoning that owning the BACKEND and
owning the BINDING are different things and that #420's endpoint is "no Veldrid in the graph" rather than "no
dependencies". That reasoning is unchanged and it has nothing to point at. Silk.NET 2.x ships Vulkan, OpenGL,
OpenCL, OpenAL, GLFW and SDL and no Metal. Vortice ships Direct3D, Vulkan and audio and no Metal. Apple ships no
managed binding of any kind. The candidates are: hand-rolled `objc_msgSend`, or vendoring
`Veldrid.MetalBindings`.

**Vendoring is rejected by name**, on V-P2's own wording: Veldrid-derived code inside the backend built to
remove Veldrid, invisible to every guard that reads package ids. Reading it as the reference implementation is a
different act and it is the right one, and this document does exactly that throughout.

**So: an engine-owned interop layer, and the shape is decided rather than left open.** One internal file family
under `Internal/ObjC/`, with three parts. A tiny runtime shim (`objc_getClass`, `sel_registerName`,
`objc_msgSend` in the overload set the calls need, `objc_retain` and `objc_release`), which is what
`MetalFrameCapture` already carries in miniature. A set of readonly-struct handle types over `IntPtr`, one per
Metal protocol the backend touches, so a device is not interchangeable with a queue at compile time. And the
enums, declared with the RIGHT underlying width, which section 2.1 shows the vendored bindings get wrong for
`MTLResourceOptions` and `MTLStorageMode`.

**Three things row 1's spike verifies before anything depends on them**, because each is a claim this document
would otherwise be making from memory:

- **The arm64 `objc_msgSend` ABI.** On arm64 there is no `objc_msgSend_stret` and no `objc_msgSend_fpret`, and
  every call including struct returns and floating-point returns goes through `objc_msgSend` itself. That is a
  simplification over the x86_64 rules the incumbent's bindings still carry, and the whole design leans on it
  being true. The spike compiles and runs one call of each return class on the hosted runner.
- **Completion-handler blocks without `Marshal.GetFunctionPointerForDelegate`.** The incumbent hand-rolls a
  `BlockLiteral` and `BlockDescriptor`, allocates them with `Marshal.AllocHGlobal`, takes a delegate function
  pointer, and keeps a static dictionary keyed on the block literal for the AOT case. `[UnmanagedCallersOnly]`
  removes the delegate and the dictionary and is AOT-clean by construction. The spike proves one command
  buffer's completion handler fires into an `[UnmanagedCallersOnly]` entry point and that the block layout is
  right.
- **`MTLBinaryArchive` is reachable and behaves**, which M-S5 depends on and which no other row can check.

If any of the three fails, the fallback is named rather than improvised: the interop layer keeps the incumbent's
delegate-and-dictionary shape for the block, which is field-proven, and the design loses AOT-cleanliness for the
completion path and says so.

**The counterargument owed.** A hand-rolled interop layer is the single largest line-count item in a phase whose
bar is parity, and phase 3 rejected hand-rolled P/Invoke for Vulkan in as many words: "thousands of lines of
struct definitions where every mistake is a memory corruption rather than a compile error". That rejection is
right and it does not transfer, because the two are not the same size. Vulkan's surface is a C API with hundreds
of structs that must be laid out byte-exactly. Metal's is an Objective-C API reached through one dispatch
function, where the surface this backend needs is roughly sixty selectors and a dozen enums, and the struct
layouts involved are `MTLSize`, `MTLOrigin`, `MTLRegion`, `MTLViewport`, `MTLScissorRect` and `MTLClearColor`.
The incumbent's own bindings for that surface are a few hundred diffable lines. The size argument is real and it
points the other way here.

---

## 4. Selection, identity and wiring

### 4.1 What the two previous phases already paid for

`GpuDeviceContext` is already inverted onto `IGpuDevice`. `GpuBackendProviders` and `IGpuBackendProvider` exist,
with the second constructor, the disposal hook and the capability read off the device.
`GpuBackendProviders.IsBuiltIn` lists the four Veldrid-backed kinds, so an APPENDED kind is provider-backed by
default and `MetalNative` needs no edit there. `PreflightProvider` fixes the order so a missing registration
throws before the probe can answer false, which is I2's whole content and is already enforced.
`GpuBackendProviderMissingException.BuildMessage` was corrected in phase 3 to state the naming convention
(`KhaozEngine.Gpu.<Backend>` exposes `KhaozEngine<Backend>.Register()`) rather than switch on the kind, so it
degrades correctly for this backend with no change at all. And the test-side seat is a static constructor on
`GpuFactAttribute` in `KhaozEngine.TestSupport.Gpu`, fired at xUnit discovery in ANY assembly carrying
`[GpuFact]`, so a `MetalBackendRegistration` sibling goes in the SAME project beside the D3D11 and Vulkan ones.

So this phase adds a REGISTRATION and re-litigates none of the wiring: `KhaozEngineMetal.Register()`, one public
entry point, called once at consumer startup, no `[ModuleInitializer]`, no reflection.

**`IsSupported()` is a functional probe with real content (M-N4).** The incumbent's
`MTLGraphicsDevice.GetIsSupported` checks the OS platform, then either counts `MTLCopyAllDevices` or creates the
system default device, wrapped in a bare `catch` that answers false. That is the FLOOR. On top of it the probe
reads four things, each cheap here and expensive anywhere later:

- a device exists and reports a name, which is what `GpuCapabilities.DeviceName` parity depends on.
- `supportsFamily:` reports at least the Apple or Mac family floor section 5.1 pins, so a machine below it
  answers false rather than crashing on frame one.
- the device's minimum constant-buffer offset alignment is at or below 256, which M-M2's stride depends on and
  which is the one number that would silently corrupt every ring bind if a future device raised it.
- `supportsTextureSampleCount:` answers for at least 1, which is what M-C4's limit read walks.

It must never throw. A machine with no Metal device answers false and routes through `AfterFallback` as
`FallbackAfterFailure`, exactly as today. Phase 3's row-4 correction is inherited without re-deriving it:
CREATION consults this probe BEFORE creating, so a machine-level refusal is always a `NotSupportedException`
naming what is missing, a missing REGISTRATION still throws its own exception, and the creation-time
`InvalidOperationException` narrows to the genuinely surprising case its message describes. The probe is
memoized on the provider instance, whose lifetime is the registration's.

### 4.2 The `GpuBackendKind` append audit, third time

The audit is a TEST (`GpuBackendKindAppendAuditTests` and its Vulkan sibling), which is what made the second
append a diff rather than a re-derivation and makes this one a diff again. Appending `MetalNative = 6` touches
the fifteen sites the corrected phase-3 record enumerates. **Three answer differently from BOTH previous appends
and all three degrade SILENTLY**, which is the highest silent-degradation count of the three phases and is the
reason this section is not a formality.

| Site | `VulkanNative`'s answer | `MetalNative`'s answer |
|---|---|---|
| `GpuDeviceContext.LogThreadingCaps` | No change, it gates on `IsDirect3D11()` | No change, same reason. No `D3D11_FEATURE_DATA_THREADING` analogue exists |
| `D3D11ThreadingProbe.IsApplicable` | No change | No change. `ThreadingCaps` and `ThreadingProbeFailure` are both null, which the record documents as "there was nothing to ask" |
| `CreateWindowed` and `CreateHeadless` switch expressions | Rides the existing explicit throwing arm | Same. Verify the message still names the provider registry generically, which phase 3 already made it do |
| `GpuBackendSelector.ToVeldrid` | Explicit throwing arm | Same, one more arm |
| `GpuBackendSelector.TryParseBackend` | Two tokens added | Add `metal-native` and `mtl-native`, in the whole-token style both previous appends used so a typo'd suffix gets the `UnrecognizedOverride` diagnostic rather than a silent run on the incumbent |
| `GpuBackendSelector.IsBackendSupported` | Route to the provider's probe | Same. Veldrid cannot answer for it |
| `GpuBackendSelector.ProbeOS` | Unchanged until the flip, and the flip means LINUX | Unchanged until the flip, and **the flip means macOS**, which is the fleet's development platform (section 17) |
| `GpuBackendSelector._windowCandidates` | Unchanged until default-ready | Same. A player does not choose an implementation |
| **`Windowing/FrameCap.Resolve`** | Falls into the uncapped arm, correct by default, recorded because it is #380's arm | **MUST CHANGE, and it is silent.** It applies a real software frame cap only on Metal plus vsync. `MetalNative` falls into the uncapped arm, so a native windowed run loses the software cap the incumbent Metal run has, and the symptom is a frame rate that changes when the backend token changes. Route through `GpuBackendKinds.IsMetal()` |
| **`Windowing/DisplaySettings`** | Same shape, same arm | **MUST CHANGE, same shape, same silence.** Route through `IsMetal()` |
| `GoldenCompare`'s two filename sites | Both route through `GoldenBackendToken` | Both, mapping `MetalNative` to `metal`. The switch has no discard arm and throws, and the audit test turns a missed mapping into a device-free red |
| `VeldridMap.SupportsCompletionFences` | Not an append site | Not an append site, and worth naming: it answers `true` for `GraphicsBackend.Metal` already, which is why M-G1 can demand ZERO capability differences here as phase 3 could |
| **`VeldridGpuDevice`'s Metal frame-capture gate** | Unaffected | **MUST CHANGE, and it is the third silent one.** It gates a `MTLCaptureManager` capture on `Backend == Metal`, so a native run arms nothing and a diagnostic capture silently produces no trace. M-G5 gives the native backend its own capture path, which is better than the gate: it owns the queue, so `MetalFrameCapture`'s reflection into Veldrid's private `_commandQueue` field is unnecessary there |
| `GpuBackendProviderMissingException.BuildMessage` | Fixed generically in phase 3 | No change, and that is the fix paying out |
| `GpuDeviceContext.LogSelection`'s token list | Reads `GpuBackendSelector.RecognizedTokens` | No change, and that is the second phase-3 fix paying out. The audit test's rows assert every listed token parses and every kind is listed, so the two new tokens are covered by construction |
| `GpuDeviceContext.CreateOrFallBack` | Correct by default | **Correct by default and the reasoning differs again, so it is recorded.** On macOS `ProbeOS` returns `Metal` while the request is `MetalNative`, so they differ and the request routes through the functional probe. A Mac whose native creation fails falls back to Veldrid Metal and reports `FallbackAfterFailure`, while a missing REGISTRATION still throws. The soak depends on telling those apart in a log line |

Beyond the table: `GpuBackendKinds.IsMetal()` is added (M-I5), and unlike `IsVulkan()` it has readers on day one.
Three of them, and all three are in the table above.

---

## 5. Device, queue and command buffers

### 5.1 The device (M-N1, M-N4)

One `MTLDevice`, created through `MTLCreateSystemDefaultDevice()` by default, which is what the incumbent does
and what keeps `GpuCapabilities.DeviceName` parity satisfiable by construction. 2.9's physical-device argument
from phase 3 applies verbatim: changing which GPU the engine runs on is a user-visible change unrelated to
swapping the backend, it breaks `DeviceName` parity in a design demanding zero capability differences, and it
puts a second variable into the one gate that must isolate the swap.

`KE_METAL_DEVICE` accepts an index into `MTLCopyAllDevices()`, a name substring, or one of `discrete`,
`integrated` and `low-power`, with a named-but-absent device producing a WARN plus the default path rather than
a hard failure. CI pins nothing, because a hosted `macos-26` runner has exactly one device and pinning a name
that could move with an image promotion would be a gate that can only produce false failures. That is a
deliberate difference from both Windows legs' `KE_D3D11_ADAPTER=warp` and both Linux legs'
`KE_VULKAN_DEVICE=llvmpipe`, and the reason is that those two pin AGAINST an accident (a paravirtual adapter
appearing, an ICD manifest moving) where here there is no accident available.

**The feature floor is `supportsFamily:`, not `MTLFeatureSet`, and this is a departure from the incumbent worth
arguing.** `MTLFeatureSupport` builds a `HashSet<MTLFeatureSet>` by asking `supportsFeatureSet:` for every enum
member and takes the highest as `MaxFeatureSet`, then derives `IsMacOS` from three specific macOS members.
`MTLFeatureSet` has been deprecated since macOS 10.15 and `MTLGPUFamily` plus `supportsFamily:` replaced it. Two
concrete things in the incumbent hang off the deprecated read and both are fragile on a current OS:
`GetUniformBufferMinOffsetAlignmentCore` returns 16 or 256 depending on `IsMacOS`, and `MTLSampler` sets a
border colour only when `IsMacOS`. A future OS that stops answering `supportsFeatureSet:` for the three named
members flips both silently. The native backend reads `supportsFamily:` for `MTLGPUFamilyApple*` and
`MTLGPUFamilyMac*` and derives the same two answers from it, and asserts the derived alignment against the
device's own reported minimum rather than against a family table.

### 5.2 The queue and the command buffers (M-N2, M-R2, M-R3)

One `MTLCommandQueue` for the device, created once at device creation. `MTLCommandQueue` is documented
thread-safe, which is what makes M-W7's lock-free recording true, and its command buffers execute in commit
order, which is what makes M-F2's monotonicity a theorem.

No second queue and no async compute. #534's argument transfers with no modification and the FFT ocean
(`OceanFftProducer`) is the same named consumer: a second queue on Metal needs `MTLEvent` cross-queue signalling
and its own submit lock, for a renderer whose uploads are megabytes at load time and whose compute is one chain
already gated by the seam's rule 2. The Metal-specific note worth adding to that issue is that Metal's
cross-queue story (`MTLEvent`, `MTLSharedEvent`) is cheaper than Vulkan's queue-family ownership transfers, so
the follow-up is smaller here than there, and it still has no consumer.

**A command buffer per `Begin`, and the bound that replaces the pool (M-R2).** `Begin()` calls `commandBuffer`
on the queue, retains it, and resets the recorder's tracked state: framebuffer, both pipelines, both dirty
arrays, the clear array, the viewport and scissor marks, and the vertex-stream records. `End()` ends whichever
encoder is live. `Submit` adds the completion handler, commits, and records the signalled completion value.

The queue has a maximum number of UNCOMMITTED command buffers and `commandBuffer` blocks when it is reached.
That is a real bound with a real block, and it is not the ring's bound. Two things keep it out of reach rather
than relying on it: `Begin` waits on the ring's frame slot first (M-R3), which bounds how far ahead the frame
loop can get, and a device-free test asserts the backend never holds more uncommitted buffers than
`FramesInFlight` plus one, the one being the present buffer M-W1 keeps. A blocked `commandBuffer` would present
as a frame-loop stall with no counter attached, which is the shape section 22 exists to keep off the list.

**The `FramesInFlight` slot wait is the ring's, and the counter says so.** Phase 3 folded the command-buffer
slot wait and the ring segment wait onto one `BackpressureStallCount` accumulator deliberately, because they
were the same statement about pipeline depth with the same lever. Here there is only one waiter, so the member
means exactly what its original doc comment says and gains nothing. That is a simplification and it is recorded
so a reader comparing the two backends does not go looking for a second meaning that does not exist here.

---

## 6. Command recording

### 6.1 The list and its encoders (M-R1 to M-R4)

`MetalCommandList : IGpuCommandList`, encoding at record time. There is no op stream, no second driver, no
`KE_METAL_RECORD` and no M1-analog A/B, and 2.2 is the argument.

State per list: the retained command buffer, the live encoder and which of the three kinds it is, the bound
framebuffer, the pending clear values, the bound graphics and compute pipelines with their changed flags, the
two per-slot dirty arrays, the vertex-stream records, the viewport and scissor marks, and the index-buffer
record.

Exactly one encoder is live at a time, which is Metal's own rule rather than a policy this design invents. Three
helpers own the transitions and every command routes through one of them: `EnsureRenderEncoder` (which may
return false, per M-W4's orphan-target rule the only way it can now do so is a genuine framebuffer failure),
`EnsureBlitEncoder`, `EnsureComputeEncoder`, plus the three `EnsureNo*` counterparts. That is the incumbent's
shape and it is right.

**Ending a render encoder invalidates every bind (M-R7).** Pipeline state, cull mode, front face, fill mode,
blend colour, depth-stencil state, depth clip mode, stencil reference, every resource-set slot, the viewport,
the scissor, AND every vertex stream and the index buffer. 2.2 records that the incumbent forgets the last two
and is saved only by a second defect that makes the vertex-stream cache permanently cold. The device-free test
is written behaviourally: record a draw, force an encoder end through a blit, record a second draw, and assert
the second draw re-issued its vertex-stream binds.

**Concurrency.** N lists record concurrently and genuinely. Each owns its command buffer and its encoders, and
nothing shared is read or written during recording. The portable seam contract is unchanged at one open
recording per device (M-R4), and `IGpuCommandList.Begin`'s XML doc gains a Metal sentence. That doc currently
says the same code "on Metal" is a half-recorded frame or a corrupted one, which is true of the Veldrid Metal
leg and becomes false for this backend on the day it ships, so the sentence is owed rather than optional.

### 6.2 The schedule (M-R5, M-R6)

1. `SetGraphicsResourceSet(slot, set)` and its dynamic-offset overload RECORD ONLY, into a per-slot record of
   `(set, engineDynamicOffset)`, marking the slot dirty when either differs. Two states (2.4).
2. `Draw`, `DrawIndexed` and `Dispatch` flush every dirty slot through the pre-command hook, then issue.
3. The flush activates a dirty slot by walking its layout's elements and issuing, per element, the calls for the
   stages that element declares. A uniform buffer whose only change is its dynamic offset re-issues its
   `setVertexBuffer` or `setFragmentBuffer` with a new offset and nothing else, which is the offsets-only case
   without a third dirty state.
4. `SetPipeline` binds the pipeline and clears every recorded slot wholesale (M-R6), reproducing the incumbent's
   `SetPipelineCore`. A rebind of the pipeline already current does nothing, which is the incumbent's identity
   guard and is kept.
5. A slot whose recorded set has gone null is skipped.
6. Repeated dirty marks between two draws collapse to one flush, which falls out of an array of slots rather
   than a list of binds. Phase 2's rule 8 is the same requirement for the same reason: the shadow pass does
   thousands of offsets-only rebinds of one set per frame, and an O(rebinds) record is an O(n squared) frame.
7. Vertex-stream binds are tracked with a real cache that is actually maintained, unlike the incumbent's, and
   invalidated by M-R7. `SetVertexBuffer(slot, b, offset)` marks the stream dirty when the buffer or the offset
   differs, and the flush issues one `setVertexBuffer` per dirty stream.

**The pipeline-state block.** `_graphicsPipelineChanged` drives one block of calls (`setRenderPipelineState`,
`setCullMode`, `setFrontFacing`, `setTriangleFillMode`, `setBlendColor`, and when the framebuffer has a depth
target `setDepthStencilState`, `setDepthClipMode`, `setStencilReferenceValue`), reproducing the incumbent's
`PreDrawCommand` including the depth-target guard. The guard is not cosmetic: `setDepthStencilState` with a null
state on a pass with no depth attachment is a validation error under `MTL_DEBUG_LAYER`, which M-T4 arms on every
run.

### 6.3 The interposition point (M-T2)

The device-free budget test needs a seam, and the interop layer's calls are static P/Invoke. The seam is a
narrow `IMetalEncoderSink`, generic-constrained to a struct so the JIT monomorphizes it away exactly as the
D3D11 emitter and the Vulkan `IVkCmdSink` are, covering the call classes that scale with draw count:

- per-element buffer, texture and sampler binds, split by stage.
- vertex-stream binds, which are their own class here for the reason phase 3 gave a vertex bind its own line:
  it genuinely scales with draw count and the incumbent pays one per stream per draw.
- draws and dispatches.
- **encoder creations**, which is the class neither neighbour has and the one this design most wants a number
  on, because 2.1 shows every record-time `UpdateBuffer` currently costs one.

Clears, copies, mip generation, resolves and the pipeline-state block go straight to the interop layer with no
indirection, because none of them scales per draw and freezing numbers over them would gate on figures nobody
should gate on. Phase 3's row-12 correction is inherited rather than rediscovered: a device-free assertion about
an EMISSION needs a line to interpose on, so the render-encoder begin and end pair and the viewport and scissor
setters sit on their own plain-handle `IMetalRenderApi`, and nothing on that seam is frozen as a marginal.

**Aiming this at Vulkan's call classes would have been the mistake.** Vulkan's fan-out class is per-draw
descriptor set allocation and per-draw `vkUpdateDescriptorSets`, and Metal allocates no descriptor of any kind.
Metal's fan-out class is per-element binds, per-draw vertex-stream binds, and encoder creations. A budget test
ported from either neighbour would pass green while this backend restarted a render encoder per uniform write.

### 6.4 What is not here

No parallel render command encoders. The seam has no sub-list concept and multi-threaded recording is not a
shipped feature (W5's position, unchanged). No indirect draws: the seam has no indirect draw and adding one has
no consumer, and the incumbent's `DrawIndirectCore` loops issuing one draw per indirect element anyway, which is
not what the API is for. No `commandBufferWithUnretainedReferences`: it removes exactly the retain M-F5 depends
on for safe mid-flight disposal, in exchange for a retain-release pair per referenced resource, and taking it
would put back the retire list this design does not need.

---

## 7. Render passes, clears and the viewport

### 7.1 The deferred begin (M-A1 to M-A3, M-A6)

State per list: the bound framebuffer, a pending clear value per colour attachment plus one for depth and
stencil, and whether a render encoder is live.

- `SetFramebuffer(fb)`. If a render encoder is live, end it. If the OUTGOING framebuffer had pending clears and
  no draw happened, force a begin-and-end pair to flush them (M-A2, and the incumbent forces exactly this in
  `SetFramebufferCore`). Record the new framebuffer, clear the pending array, mark the viewport and scissor for
  emission (M-A5).
- `ClearColorTarget(i, rgba)` and `ClearDepthStencil(d)`. If no encoder is live, store the value as pending,
  which becomes `loadAction = Clear` with that clear value on **attachment `i`**, which is M-A6's correction. If
  an encoder IS live, end it and store the value as pending, which is what the incumbent does through its
  `EnsureNoRenderPass` call and is the behaviour a golden may depend on.
- First draw. Build the `MTLRenderPassDescriptor` from the framebuffer: per colour attachment, `loadAction =
  Clear` with the pending value if there is one and `loadAction = Load` otherwise, `storeAction = Store` always
  (M-A4). Depth and stencil the same, with the stencil attachment populated only when the depth format carries
  stencil, which is the incumbent's `FormatHelpers.IsStencilFormat` guard. Create the encoder, emit the viewport
  and scissor if marked, then the draw.
- `End()`, or any command illegal inside a render encoder (M-A3): end the encoder, flushing pending clears
  through a begin-and-end pair if there were any and no draw came. The incumbent's `End` does exactly this.

**`storeAction = DontCare` is rejected for v1 and the rejection is argued rather than inherited.** It leaves
contents undefined, undefined is not stable across runs, and the goldens require stability on the same device.
The Metal-specific upside is larger than the Vulkan one and 2.6 records it with a named consumer, so the
follow-up has somewhere to start and owes a determinism argument.

### 7.2 The viewport (M-A5)

There is no `SetViewport` on the seam. The engine gets a viewport because Veldrid's base
`CommandList.SetFramebuffer` auto-calls `SetFullViewports()` and `SetFullScissorRects()`, wrapped in an
`if (_framebuffer != fb)` identity guard. **Both halves must be reproduced.** A backend that does not emit
rasterises nothing. A backend that emits UNCONDITIONALLY diverges on the shipped sequence `SetFramebuffer(fb)`,
`SetScissorRect(...)`, draw, `SetFramebuffer(fb)`, draw, where the second bind silently restores the full
scissor and the second draw renders outside the intended rectangle. That is golden-visible, and phase 2's first
spec froze the wrong behaviour into its tally test.

**Metal adds a third half, and it is the incumbent's own.** `PreDrawCommand` flushes the scissor only when
`_graphicsPipeline.ScissorTestEnabled`, so a pipeline with scissor test off never receives a scissor rect at
all. That is a real behavioural difference from both other backends and it is reproduced, with a device-free
assertion, because a native backend that emitted the scissor unconditionally would clip draws the incumbent does
not clip.

**And Metal needs no clip-space trick at all.** `MTLGraphicsDevice.IsClipSpaceYInverted` is false and
`IsUvOriginTopLeft` is true, so `GpuCapabilities.ClipSpaceYInverted` is false and `GpuClip.Correct` is the
identity. Vulkan needed a negative viewport height to reach the same answer and it was the single most
consequential line in that design. Here it is free, and saying so is worth a sentence because a reader coming
from phase 3 will look for the trick.

No `SetViewport` member is added to the seam. Phase 2 counted 48 `SetFramebuffer` sites and zero viewport sites,
phase 3 confirmed it, and it has not changed. It remains a reasonable addition when the seam is being revisited
for its own reasons, which this phase is not.

---

## 8. The binding model

### 8.1 The three index spaces

Metal gives a function three independent index spaces: `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]`,
each per stage. There are no sets, no layouts and no descriptor objects, so `IGpuResourceLayout` and
`IGpuResourceSet` are pure engine bookkeeping on this backend and hold no native handle at all. That is the
incumbent's shape (`MTLResourceLayout` holds a binding-info array, `MTLResourceSet` holds a resource array) and
it is right: there is nothing to allocate and nothing to write.

The seam maps as follows, and the right-hand column is what M-B1 changes:

| Seam | Metal | Index from |
|---|---|---|
| `IGpuResourceLayout` | nothing native, a binding-info array | engine |
| `IGpuResourceSet` | nothing native, a resource array | engine |
| `GpuResourceKind.UniformBuffer` | `setVertexBuffer` / `setFragmentBuffer` / `setBuffer` | **the emitted MSL** |
| `StructuredBufferReadOnly` and `ReadWrite` | the same three calls | **the emitted MSL** |
| `TextureReadOnly` and `ReadWrite` | `setVertexTexture` / `setFragmentTexture` / `setTexture` | **the emitted MSL** |
| `Sampler` | `setVertexSamplerState` / `setFragmentSamplerState` / `setSamplerState` | **the emitted MSL** |
| `GpuResourceLayoutElement.Dynamic` | the `offset` argument of the buffer call | engine |
| a vertex stream | `setVertexBuffer` at `NonVertexBufferCount + i` | the pinned `Improved` formula (M-B2) |

### 8.2 The table, and how it is built (M-B1, M-S3, M-S6)

At `CreateShadersFromSpirv` the backend holds the MSL text for both stages, because it produced it through
`SpirvCrossCompile`'s new MSL half. For each stage it:

1. finds the entry point by its qualifier (`vertex`, `fragment` or `kernel`) and reads its NAME rather than
   assuming one (M-S3).
2. scans the argument list with a DEPTH-MATCHED parenthesis walk, because every argument carries an attribute of
   its own and a naive scan stops inside the first `[[buffer(0)]]` and sees one argument. This is the exact
   failure `ShaderValidation.BufferKindsFromEntryPoint` already documents and already solves.
3. records, per argument, its index and its space, keyed by the resource NAME the argument carries.

Resource-set activation then looks a layout element up by name in the table and binds at the index it finds. An
element with no entry in the table for a given stage is NOT bound for that stage, which is correct by
construction: SPIRV-Cross omits an argument a stage does not reference, and binding one anyway is what an
index-counting backend does that produces the off-by-one.

**The name is the join, and that is a claim the spike checks.** SPIRV-Cross emits argument names derived from
the GLSL block or variable name, and the engine's reflection (`ShaderReflection` through
`SpirvCrossCompile.Reflect`) already carries a `Name` on every `GpuResourceLayoutElement`. If the two spellings
do not match for every shipped element, the fallback is ORDINAL within each space, which is the mapping the
kind-order comparison in `ShaderValidation.CheckMslBufferSlots` already relies on, and which is still strictly
better than the incumbent's cross-space flattened count. Row 1's spike decides between the two by running both
over all thirty-odd programs, and M-S6's table test pins whichever wins.

**The table test (M-S6).** Device-free, on every `dotnet test`, over every shipped program: build the table,
pair it against the pipeline's layout array and each layout's element array, and assert every element resolves
to exactly one index in exactly the spaces its declared stages need. Plus M-B2's no-collision assertion, that
`NonVertexBufferCount` exceeds every buffer index the vertex function declared. The failure this catches is
"everything compiles and every pixel is wrong", which is what S2 and V-S8 both exist for, arriving through the
one door Metal leaves open.

### 8.3 Dynamic offsets are just offsets (M-M3)

Every ring-backed uniform's per-frame base is the `offset` argument of the same `setBuffer` call that binds it,
composed as `ringBase(buffer, currentFrame) + rangeOffset + (isTheDeclaredDynamicElement ? engineDynamicOffset :
0)`. There is no positional array, no `pDynamicOffsets` ordering to get right, no `range` to keep inside the
buffer, and no VUID.

That is genuinely simpler than both neighbours and it is worth stating as a decision rather than as luck,
because the two invariants it removes are the two that cost phase 3 the most text: V-M6's
`rangeOffset + callerDynamicOffset + range <= stride` and 6.2's positional composition test. What REPLACES them
here is one Metal rule: `offset` must be a multiple of the device's minimum constant-buffer offset alignment,
which M-M2 pins at 256 and asserts against the device's own report. One device-free test over every shipped
resource-set shape asserts the composed offset is 256-aligned, which is the whole obligation.

### 8.4 Declining argument buffers, against the idiomatic grain (M-B4)

Argument buffers are available on every device the fleet targets and tier-2 argument buffers with unbounded
arrays are available on every Apple Silicon Mac, so the support matrix does not decide this and the decline
needs an argument rather than an omission.

**There is no consumer.** Argument buffers exist to remove per-material bind traffic from renderers that bind
hundreds of distinct material sets per frame. This engine's per-frame binding traffic is dominated by
OFFSETS-ONLY rebinds of ONE set, which under 8.3 cost exactly the buffer calls of that set and which argument
buffers do not improve. Phase 2's measured D3D11 shape says the same from the other side: 2 calls per draw, 4
per distinct mesh.

**Every route to them changes the emitted MSL.** Argument buffers are `msl_options.argument_buffers` in
SPIRV-Cross, which changes the emission for every program at once, which puts the 36 committed `metal` goldens
in play and destroys M-S2's byte-equality parity claim in the same move. That is the exact risk shape phase 2
refused for SPIRV-Cross direct bindings and phase 3 refused for descriptor indexing, and it is refused here for
the same reason and with more force, because the parity claim is the entire licence for "no rebake".

**And they interact badly with M-B1.** An argument-buffer emission moves resources off the entry-point signature
and into a struct, so the table M-B1 reads would no longer be there to read and the backend would need a second
index-derivation path. Declining is what keeps one.

**Indirect command buffers, heaps and tile shading** are declined together and for one shared reason: each is a
Metal-native capability with no seam member behind it, so taking one means either inventing public API with one
backend behind it or having the backend silently do something the other two do not. Both are worse than the
plain calls that already work. Heaps additionally interact with M-H1, since heap-allocated resources default to
UNTRACKED, so taking heaps would import 2.5's hazard class through a side door.

**The trigger that reopens it**, so this is a decision and not a permanent no: a consumer needing per-draw
material variety beyond one dynamic offset, which today means a texture-array atlas the splat terrain cannot
express. That is the SAME trigger phase 3 named for descriptor indexing, deliberately, because the two would be
reopened by the same consumer and should be reopened together.

<!--NEXT-->




