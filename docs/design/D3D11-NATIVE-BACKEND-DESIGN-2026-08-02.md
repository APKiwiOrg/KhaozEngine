# KhaozEngine.Gpu.D3D11: native Direct3D11 backend design (2026-08-02)

**Status: spec complete, implementation not started.** Phase 2 of the staged native GPU backend program
([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)), specified by
[#421](https://github.com/APKiwiOrg/KhaozEngine/issues/421). This document is the deliverable for #421.
Implementation is a numbered issue list in section 15 and none of it has been written. Nothing here has run
on a device. Section 13 lists every decision that rests on reasoning rather than measurement, each with the
measurement that settles it, the switch that kills it, and the criterion that retires the switch.

Written against engine `17.26.0` (`Directory.Build.props`, the version this spec was authored on) and the
Veldrid fork at `fix/d3d11-immediate-4.9.0`, tip `v4.9.103`. The **assumed baseline** is an engine that
vendors fork `4.9.103` (the #428 second-recorder guardrail) and has #429's pre-record phase in the windowed
frame loop. That is an assumption at authoring time and not yet a fact: the in-flight #429 batch is what
vendors `4.9.103`, so the assumption converges to fact when that batch lands. Implementation issue 1 re-reads
both before starting, and if either has not landed, section 2.3's coexistence reasoning is unchanged (it does
not depend on the guardrail existing) but implementation issue 3's audit must be re-run against whatever is
actually on `main`.

**Provenance.** Two complete competing drafts were written independently against a shared, citation-heavy
constraints pack: a continuity-first draft (37 decisions, port the field-proven configuration and argue every
departure) and a clean-sheet pathology-first draft (32 decisions, design the hazard classes away and argue
every inheritance). This document adjudicates them. Where a draft won outright it is recorded. Where the
adjudication produced something neither draft proposed, section 2 says so and why. Section 17 lists what was
rejected from both.

---

## 1. Decisions

| # | Area | Decision | Origin |
|---|---|---|---|
| P1 | Package | New `KhaozEngine.Gpu.D3D11`, opt-in, outside every umbrella, `net10.0` (not `-windows`) with `[SupportedOSPlatformGuard("windows")]` entry points and `NoInlining` bodies | Both, converged |
| P2 | Package | References `KhaozEngine.Gpu` and Vortice only. The `Veldrid.SPIRV` edge stays in `KhaozEngine.Gpu` behind an internal, Veldrid-free cross-compile helper plus `InternalsVisibleTo`. New guard: no package id starting with `Veldrid` is reachable from `Gpu.D3D11` | A |
| P3 | Wiring | `GpuDeviceContext` refactored onto `IGpuDevice`: second constructor, disposal hook replacing the `((VeldridGpuDevice)GpuDevice)` cast, `D3D11ThreadingProbe` gains a raw-pointer entry point | Both, converged |
| P4 | Wiring | Explicit registration through a `GpuBackendProviders` registry via `KhaozEngineD3D11.Register()`. No `[ModuleInitializer]` and no reflection in the consumer path. (Corrected 2026-08-05. This row originally closed with "A module initializer IS used inside the test assembly, where load is guaranteed." The shipped mechanism is a static constructor on `GpuFactAttribute` in `KhaozEngine.TestSupport.Gpu` instead, since a library may not carry a module initializer under CA2255, with a thin belt initializer remaining in `KhaozEngine.Render.Tests`.) | Both, converged (B's nuance) |
| I1 | Identity | Append `GpuBackendKind.Direct3D11Native = 4`, pin explicit ordinals on all four existing members, add the append-only comment `GpuBackendSource` already carries. New tokens `d3d11-native` and `direct3d11-native` | B |
| I2 | Identity | A missing provider registration THROWS and never falls back. Machine incapability is a different case: `IsBackendSupported` answers it with its own functional probe and the existing `CreateOrFallBack` / `AfterFallback` path reports it | B, split made explicit |
| I3 | Goldens | SHARE the `direct3d11` golden family through a `GoldenBackendToken(GpuBackendKind)` mapping. `KE_UPDATE_GOLDENS` under the native token REFUSES to write unless `KE_GOLDEN_FAMILY_OVERRIDE=1` | B |
| I4 | Identity | `ProbeOS` and `_windowCandidates` unchanged until the default flip. No new telemetry field: the session header's `backend` name carries the attribution | B |
| I5 | Identity | Every `GpuBackendKind` switch and comparison in the engine gets a decided arm. Section 4.3 enumerates all thirteen | Judge, new |
| R1 | Recording | Engine-owned CPU command stream, replayed inside `Submit` through a generic struct emitter over engine-owned handle types. Zero native calls during record | B, gated by M1 |
| R2 | Recording | If M1 fails, the fallback is immediate emit through the SAME emitter interface. Everything above the emitter is shared by construction | Judge, new |
| R3 | Recording | `Begin` resets the stream and touches no device state. N lists may record concurrently. Each `Submit` replay opens with exactly one `ClearState`. Submit order is the observable order | B |
| R4 | Recording | The PORTABLE seam contract stays "one open recording per device". The native backend's tolerance of nested recording is a backend property, documented as such, and is not a licence to close #424 | Judge, synthesised |
| R5 | Recording | Port the 4.9.101 schedule intact: three-state per-slot dirty tracking, record-only set calls, single flush at draw and dispatch, slot-order flush, pipeline-switch drain under the OUTGOING layout, null-record guard, bound-record dedup | Both, converged |
| R6 | Recording | Emission uses the array overloads: one native call per (kind, stage) per flush, so the law is O(kinds x stages), not O(elements x stages). Batch `IASetVertexBuffers(0, 2, ...)`. Redundancy caches on pipeline-level objects | Both, converged |
| R7 | Recording | Every constant-buffer bind goes through `*SetConstantBuffers1` with explicit first-constant and num-constants. `ConstantBufferOffsetting` is a hard device requirement checked by the probe. Keep the `!DriverCommandLists` unset-before-set driver workaround | B, plus A's workaround |
| R8 | Recording | Resource disposal precisely scrubs the redundancy caches and unbinds the resource, rather than relying on a wholesale `ClearState` | A's hygiene point, kept |
| U1 | Uploads | Every `UniformBuffer`-usage buffer is ring-backed: one `ID3D11Buffer` of `size * FramesInFlight`, `DYNAMIC` plus `CPU_ACCESS_WRITE`, `FramesInFlight = 3`. The `IGpuBuffer` identity NEVER changes. The per-frame base is applied AT BIND | B, reconciled with A's objection |
| U2 | Uploads | The ring is mapped `MAP_WRITE_NO_OVERWRITE` for the record phase and unmapped at the start of `Submit`. Requires `MapNoOverwriteOnDynamicConstantBuffer`, checked by the probe. D3D11 has no persistent mapping, so this is the achievable form | B |
| U3 | Uploads | `CreateResourceSet`'s pinned `GpuBufferRange` survives because the frame base is a bind-time addend and is never baked into a set. ONLY `UniformBuffer` buffers are ring-backed, and a ring-backed buffer never receives a non-constant-buffer view (asserted) | Judge, reconciliation |
| U4 | Uploads | Vertex, index and texture record-time payloads go to a per-list reusable CPU arena and replay as `UpdateSubresource`. Static and load-time `DEFAULT` buffers keep `UpdateSubresource`. Structured buffers keep `DEFAULT` plus a full-range RAW view | Both, converged |
| U5 | Uploads | Device-level `UpdateBuffer` writes the CURRENT frame segment, preserving the documented off-timeline semantic. Segment recycling blocks on the previous owner's fence, with a stall counter in telemetry. CORRECTED IN FLIGHT by #484: it reaches EVERY segment, deferring an in-flight one to a pending patch rather than waiting. See section 6.4 | B, gated by M3 |
| X1 | Resources | All SRV, RTV, DSV and UAV objects and all blend, depth-stencil, rasterizer and input-layout objects are created at resource, set or pipeline creation. The emitter interface has NO `Create*` member, so draw-time creation is a compile error | B |
| X2 | Resources | Drop the incumbent's `D3D11ResourceCache`. The D3D11 runtime already returns an existing object for an identical state description | B |
| X3 | Resources | Reproduce the `DeviceLiveness` latch exactly: disposal after device death is a no-op, `IGpuFence.Signaled` reads true, `WaitForIdle` is a no-op | Both, converged |
| S1 | Shaders | GLSL 450 stays the single source. The static `SpirvCompilation` API does GLSL to SPIR-V to HLSL. We call `D3DCompile` (FXC) ourselves to `vs_5_0` / `ps_5_0` / `cs_5_0` at O3. `SpirvLocalSize` unchanged. DXC rejected on the DXIL-versus-DXBC fact | Both, converged |
| S2 | Shaders | Reproduce Veldrid's D3D11 register scheme exactly: per-kind declaration-order slots within a layout, `t` shared by texture and read-only structured, `u` shared by read-write, `s` for samplers, flattened across layouts in PIPELINE-ARRAY order and never by GLSL set number | Both, converged |
| S3 | Shaders | Pin the cross-compile options by reading `ResourceFactoryExtensions.CreateFromSpirv` in the fork, cite them in a constant, and assert emitted-HLSL byte equality per program against a checked-in hash | Judge, new |
| S4 | Shaders | Disk DXBC cache keyed on (GLSL source, target, compiler flags, engine version) | Both, B's key |
| S5 | Shaders | The holed-signature workarounds STAY and become enforced. `ShaderValidation` gains a Windows-only FXC compile leg plus a contiguous-`TEXCOORD` assertion on the reflected vertex input signature, and the same check runs at pipeline creation | Both, converged |
| W1 | Swapchain | v1 keeps the LEGACY BLIT swapchain, matching the incumbent's present path exactly. Flip model, `ALLOW_TEARING` and the #380 pacing work are sequenced after the soak as their own change | Judge, against both |
| W2 | Swapchain | The `IGpuFramebuffer` wrapper identity is STABLE across resize. We own the wrapper and swap the views underneath | Both, converged |
| W3 | Swapchain | `ResizeSwapchain` queues the size and applies it at the next present boundary on the submit thread, coalesced to the last requested size | B |
| W4 | Threading | No frame-long lock anywhere. Recording is lock-free. One `_submitLock` covers replay, present and the resize apply. Staging `Map` and `Unmap` take it only for the map call. Creation is free-threaded, serialised when `DriverConcurrentCreates` is false | B, plus A's creation-lock rule |
| W5 | Scope | Multi-threaded recording is STRUCTURALLY PERMITTED and is neither exercised nor supported in v1. It is not part of the shipped contract | B, reworded |
| W6 | Viewport | `SetFramebuffer` emits a full `RSSetViewports` plus a full `RSSetScissorRects` ON A FRAMEBUFFER CHANGE ONLY, replicating the incumbent's identity guard. No `SetViewport` is added to the seam | Both, corrected |
| C1 | Compute | Same schedule with a separate compute dirty array. SRV-versus-UAV auto-unbind in BOTH directions, implemented where the bind arrays are assembled | Both, converged |
| C2 | Compute | Structured buffers keep the RAW byte-address view forcing, because SPIRV-Cross emits `ByteAddressBuffer` for a GLSL storage block | Both, converged |
| C3 | Barriers | No new seam member. Rule 2 (`End` plus `Submit` plus `WaitForIdle`) is honoured as written. The automatic-hazard capability is filed as a follow-up, not built here | Both, converged |
| C4 | MSAA | `ResolveSubresource` at subresource 0. `MaxMsaaSampleCount` is the MIN over `R8G8B8A8_UNORM`, `R32_FLOAT` and `D32_FLOAT_S8X24_UINT` (CORRECTED as implemented: the depth attachment is queried as `R32G8X24_TYPELESS`, which is what `D3D11Formats.ToDxgiFormat` turns the incumbent's depth-flagged `D32_Float_S8_UInt` into before it queries, so "equal to the incumbent's" is the same question rather than a near one) via `CheckMultisampleQualityLevels`, asserted equal to the incumbent's. An out-of-range requested count THROWS rather than silently degrading | B's throw, A's parity assertion |
| C5 | Fences | Real completion fences: `ID3D11Fence` via `ID3D11Device5` and `ID3D11DeviceContext4::Signal`, with `ID3D11Query(Event)` as the fallback. `SupportsCompletionFences = true` on both paths | Both, converged |
| C6 | Fences | `WaitForIdle` becomes a real fence drain, replacing an empty method body, behind the `KE_D3D11_REAL_DRAIN` kill switch with drain count and duration in telemetry | Both, gated by M2 |
| G1 | Capabilities | Capability parity with the incumbent except `SupportsCompletionFences`, asserted field by field. The sampler HARDCODES are reproduced. The two unreachable sampler DEGRADATIONS are dropped | A's hardcodes, B's dropped fallbacks |
| G2 | Diagnostics | `KE_D3D11_ADAPTER=warp\|hardware\|<index>\|<substring>` explicit adapter selection, `DXGI_ADAPTER_FLAG_SOFTWARE` recorded in telemetry, CI pins WARP | Both, converged |
| G3 | Diagnostics | Device loss: HRESULT checks after `Present`, after every `Map` and after replay. `GetDeviceRemovedReason` called IMMEDIATELY at the fault site, latched, surfaced in the session header. Closes #427 for the native leg | Both, B's immediacy |
| G4 | Diagnostics | `KE_D3D11_DEBUG=1` enables the debug layer plus a rate-limited `ID3D11InfoQueue` pump. `KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS` keeps its exact semantics. No D3D11 frame-capture integration: RenderDoc attaches externally | Both, converged |
| T1 | Tests | The 36 committed D3D11 goldens run unmodified against the native backend at the existing 0.06 per-channel tolerance, on the same WARP rasterizer. No rebake | Both, converged |
| T2 | Tests | The native-call budget is a device-free plain `[Fact]` on every `dotnet test`. The GATE is the structural invariants, the marginal per-draw deltas, the instance-count trace identity and upper bounds on fan-out. Absolute totals are documentation, not the gate | Judge, against both |
| T3 | Tests | A `[GpuFact]` parity check on the WARP leg guards the device-free harness against drifting from the real replay path | B |
| T4 | Tests | `NativeVsVeldridCapabilityParityTests` compares both `GpuCapabilities` structs field by field in one process, with completion fences asserted as the ONLY difference | A |
| T5 | Tests | A new `direct3d11-native` matrix leg in `cross-platform-gpu.yml` on `windows-latest`, golden-only on push, full suite on schedule and dispatch | Both, converged |
| RO1 | Rollout | Five named gates (section 14), all green before the default flip | B |
| RO2 | Rollout | `Direct3D11` stays selectable by token INDEFINITELY, until Phase 3 removes the Veldrid path entirely | B |
| RO3 | Rollout | The headless default stays on Veldrid until gate 4. The `direct3d11-native` CI leg is the continuous exercise | Judge, against B |

---

## 2. The contested adjudications

Three decisions were genuinely contested. Each is decided with reasoning here rather than deferred.

### 2.1 The recording model

**The two poles.** Draft A records directly onto the immediate context, which is the field-proven
configuration (17.24.0 measured 125 fps flat, 8.0 ms frames, shadow encode collapsing 40x to 0.35 ms), and
adds a device-owned state cache that every recorder shares so a nested `Begin` JOINS the open recording
instead of wiping it. Draft B records into an engine-owned CPU command stream and makes every native call
inside `Submit`, which removes the whole #423 and #415 hazard class structurally, but whose cost attribution
for the deferred-context measurement is reasoning rather than measurement, flagged by its own author.

**What decides it, and what does not.** Draft B's headline argument, that only its model makes the
native-call-count test device-free, does not survive scrutiny and should not carry weight. Both drafts
independently arrive at the same emitter abstraction: a generic type parameter constrained to a struct
interface, expressed in engine-owned handle types (A's D31, B's D7). Once that seam exists, a counting
emitter is a plain object under either model, and the "zero `Create*` during replay" invariant is a
compile-time property under either, because both create views eagerly. What both models actually need for a
device-free test is a fake resource factory, and that is orthogonal. So the device-free gate is available to
whichever model wins and is not an argument for either.

Four things do decide it.

First, **hazard elimination versus a claim about absence.** B's model cannot be corrupted by a nested `Begin`
because `Begin` touches no device state and two recorders are two arrays. A's shared cache is a claim about
what does not happen, backed by a reading of why the per-list cache failed, and A says so in its own
least-confident list. A also creates an asymmetry during the coexistence window that the whole rollout
depends on: the same nested call is an exception on Veldrid D3D11 (fork 4.9.103) and a silent no-op on native
D3D11, so an A/B on the reporting machine is comparing two different sets of legal programs.

Second, **the map and unmap call count, on the one lever the field actually measured.** Under B, the ring is
mapped once per ring per submit and every record-time uniform write is a memcpy into already-mapped memory,
so a per-draw slot write costs ZERO native calls. Under A's shadow-image scheme the map must bracket each
flush point, because D3D11 forbids a mapped resource being bound to the pipeline and under immediate emit
draws happen during the record phase. In the #410 scene shape (roughly 1000 draws, with the #408 residue
being one partial write per caster per cascade, per water plane, per overlay-mesh draw and per SpriteBatch
slot) that is on the order of a thousand extra map and unmap pairs per frame. At the historically observed
per-call tax that is not a rounding error, and neither draft made this argument. **B's recording model and
B's ring are load-bearing for each other**, which B states, and the pair is worth more than either alone.

Third, **the seam's contract stays honest across three backends.** Under B, command lists are genuinely
independent and execute in submit order, which is what Vulkan and Metal naturally provide and what the seam
already documents. Under A's join semantics, an inner list's commands interleave at the point they were
recorded, which is a THIRD semantics that neither the seam nor any Phase 3 backend can reproduce. A concedes
this and proposes to document it. Introducing a permanently backend-specific meaning for `Begin` in the
release whose purpose is to make the seam implementable by engine-owned code is the wrong direction.

Fourth, and against B, **the risk B does not name.** The #417 probe measured that with lean submission the
driver's internal threading HELPS (109 fps with `PREVENT_INTERNAL_THREADING_OPTIMIZATIONS` on, 125 with it
off). Under immediate emit, driver-side consumption overlaps with the engine's own CPU frame work. Under B,
every native call bunches into the replay window, so record and driver-consume become two sequential phases
instead of overlapping ones. That, not the memcpy cost B estimates at 0.2 ms, is the mechanism that could
make B slower. **B's proposed WB4 gate, a stub-emitter replay measurement, cannot see it**, which makes the
gate as written insufficient.

**Decision.** Adopt B's recording model (R1, R3), with three amendments. The measurement gate M1 is
end-to-end frame time on a real scene with both drivers built against the same emitter, not a stub-emitter
microbenchmark. The named fallback if M1 fails is A's immediate emit through the same emitter interface (R2),
with the ring degrading to per-flush map and unmap, which is why the emitter seam must exist before either
driver is written. A's shared device-state cache is rejected, but A's precise unbind-and-scrub on disposal is
adopted (R8) as the hygiene that a single `ClearState` per replay does not cover mid-replay.

### 2.2 Backend identity

**A's central argument is refuted by B's construction.** A reuses `GpuBackendKind.Direct3D11` with a
`KE_GPU_D3D11_IMPL` switch, on the grounds that a separate enum member would force a golden-token decision
and lose the strongest free proof in the program. B shows that is not so: a one-function
`GoldenBackendToken(GpuBackendKind)` mapping returns `direct3d11` for the native kind, `GoldenCompare`
resolves the same filename, `CrossBackendGoldenTests` still sees three families, and the proof is kept in
full. Once golden sharing is available under both, A's decision has no argument left and three costs.

The costs. The published telemetry contract writes the enum NAME, so under A both implementations report
`"backend":"Direct3D11"` in the 17.25.0 session header and attribution moves to a secondary field that any
existing reader ignores. Field-capture discipline says pin the primary identity before reading numbers, and a
number attributed to the wrong build is the failure mode that costs a retraction. Field-soak ergonomics
favour B as well: one already-documented variable (`KE_GRAPHICS_BACKEND=direct3d11-native`) rather than two
that must both be right. And on a bake, A is strictly worse: `KE_UPDATE_GOLDENS` run on the native leg would
overwrite the reference the native leg is being checked against AND the incumbent's, with no way to express a
refusal, while B's separate token makes the refusal guard expressible.

**Failure on a machine where native creation fails** is where A looks better and is not. A falls back to
Veldrid with a WARN so a player never gets a client that will not start. B distinguishes two cases correctly
and A conflates them. A missing `KhaozEngineD3D11.Register()` call is a programmer error, and falling back
silently there means a soak session measures the incumbent and reports it as the native backend, which is the
exact retraction risk. An incapable MACHINE is a different case, and it is already handled by existing
machinery: `IsBackendSupported` is a functional probe (create a device, check the required D3D11 options,
dispose, cache), and a false answer routes through `CreateOrFallBack` and is reported through `AfterFallback`
as `FallbackAfterFailure`, exactly as it is today for Vulkan and Metal.

**Decision.** B, in full (I1 to I4), plus a new item A's framing surfaced: appending an enum member touches
thirteen switch, comparison and name-derivation sites, THREE of which silently degrade the native leg if
missed. Section 4.3
enumerates them and decides each arm (I5).

### 2.3 Nested Begin during coexistence

**Three positions exist and they are not symmetric.** The vendored fork guardrail REJECTS a second recorder,
which is right for a library that cannot see its callers. A JOINS, which is a third semantics no other backend
can offer. B ALLOWS N concurrent recordings that are genuinely independent and replay in submit order, which
is what Vulkan and Metal already do and what the seam already describes.

**Decision, in two halves.** The NATIVE BACKEND's contract is B's: nested and concurrent recording is legal
and defined, and the observable order is submit order. The PORTABLE seam contract is unchanged: exactly one
open recording per device, because Veldrid D3D11 still ships alongside and rejects a second one, Veldrid
Vulkan and Metal have their own list lifetimes, and the engine's own 17.26.0 invariant test asserts no nested
open list in the capture paths.

**Both halves have to be WRITTEN somewhere, and today neither is.** `GpuInterfaces.cs` documents `Begin()` as
"Begin recording" and `End()` as "Finish recording" and says nothing about how many may be open, so the
portable rule currently exists only as a test and as tribal knowledge, and the native rule would exist only in
this document. The obligation is therefore concrete and has an owner: implementation issue 6 carries a doc
task that adds the one-open-recording rule to the `Begin`/`End` XML docs on `IGpuCommandList` and adds a
`docs/USING-KHAOZENGINE.md` section stating both contracts and which one a consumer may rely on, which is the
portable one. Without that, R4 decays into "the native backend quietly tolerates it", which is how #424 gets
closed for the wrong reason.

**Consequences that must be written down or the decision decays.** `OpenListTrackingGpuDevice` stays as the
portable guard and passes trivially on native, so it must not be read as evidence about the native backend.
#424's seven latent sites stay OPEN and stay real work, because they are hazards on the Veldrid leg that
remains selectable indefinitely (RO2), and the native backend's tolerance is not a reason to close them.
#428's guardrail and #429's pre-record phase both stay until the Veldrid D3D11 leg is removed in Phase 3, at
which point they become dead weight and are retired together (follow-up F8).

---

## 3. Package and layering

`KhaozEngine.Gpu.D3D11`, one assembly, referencing `KhaozEngine.Gpu`, `Vortice.Direct3D11` and
`Vortice.D3DCompiler`. Target `net10.0`, NOT `net10.0-windows`, so the assembly compiles and its device-free
tests run on the Linux `ci.yml` leg and the macOS Metal leg, and so `KhaozEngine.Render.Tests` can reference
it unconditionally. Every entry point carries `[SupportedOSPlatformGuard("windows")]` and every
Vortice-touching body is `[MethodImpl(MethodImplOptions.NoInlining)]` behind an `OperatingSystem.IsWindows()`
guard. That is the pattern `D3D11ThreadingProbe` already proves keeps the Vortice assembly off the load path
on macOS and Linux, and with warnings as errors, CA1416 makes the compiler enforce the boundary rather than a
convention.

Guard work the package creates, all mechanical:

- `ArchitectureTests.OptInBackends` gains `Gpu.D3D11`, which then enforces
  `OptInBackends_AreNotReachableFromAnyUmbrella`.
- `ArchitectureTests.ThirdPartyHomes` gains `Gpu.D3D11` on the `Vortice.Direct3D11` key and a new
  `Vortice.D3DCompiler` key, or `EveryThirdPartyPackage_IsDeliberatelyMapped` fails.
- `KhaozEngine.slnx` gains the project, which force-adds `KhaozEngine.Tests` to the selective-test set, so
  the architecture guards run on the landing PR.
- `check-doc-versions.sh` requires a bolded `**KhaozEngine.Gpu.D3D11**` catalog row in the root `README.md`
  and a `KhaozEngine.Gpu.D3D11/README.md` shipped via `<PackageReadmeFile>`.
- `GpuPublicApiTests` is extended to scan the new assembly, which falls outside both existing lockdown scans
  as written.
- A NEW assertion: no package whose id starts with `Veldrid` is reachable from `Gpu.D3D11`.

**P2 is the layering decision worth defending.** The shader path needs SPIRV-Cross, and SPIRV-Cross arrives
as `Veldrid.SPIRV`. Draft B referenced it directly from the backend and blessed the edge in `ThirdPartyHomes`.
Draft A kept the reference in `KhaozEngine.Gpu` behind an internal cross-compile helper with
`InternalsVisibleTo`. A wins, for three reasons. `KhaozEngine.Gpu` already owns `ShaderValidation.cs`, which
uses precisely that static API with no device in existence, so the helper is already at home there. Blessing
a Veldrid package inside a backend whose premise is being Veldrid-free is a bad signal that no guard would
ever catch. And the helper becomes the single seat for the eventual SPIRV-Cross replacement (follow-up F2),
so Phase 3 changes one package rather than three. The helper's signature must be Veldrid-free (engine-owned
result record carrying HLSL text plus the reflection the backend needs), or the internal API leaks Veldrid
types across an assembly boundary that no public-surface scan checks.

A packaging fact worth recording, because it contradicts two comments in the repo: dropping `Veldrid.SPIRV`
would NOT drop the `Newtonsoft.Json 9.0.1` CVE override. That arrives via
`Veldrid -> NativeLibraryLoader -> Microsoft.Extensions.DependencyModel`. Follow-up F7 corrects the comments.

**Strongest rejected alternative.** Put the native backend INSIDE `KhaozEngine.Gpu` next to
`VeldridGpuDevice`, which needs no registry, no `GpuDeviceContext` inversion and no new dependency edge
(`Vortice.Direct3D11` is already a direct dependency there for the threading probe). Rejected because it
makes the D3D11 interop non-optional for every consumer including the Linux server heads, and because #420's
endpoint is three engine-owned backends and no Veldrid, which a backend welded into the seam package cannot
become. This decision is contingent on #420 holding.

---

## 4. Selection, identity, and wiring

### 4.1 Getting the assembly loaded

`KhaozEngine.Gpu` cannot reference `KhaozEngine.Gpu.D3D11` (a cycle), so the selector cannot construct it.
`KhaozEngine.Gpu` gains a small `GpuBackendProviders` registry with
`Register(GpuBackendKind, IGpuBackendProvider)`, and `KhaozEngine.Gpu.D3D11` exposes exactly one public entry
point, `KhaozEngineD3D11.Register()`, called once at consumer startup. Referencing the package plus one line
is the opt-in. It is compile-time visible, trim-safe and testable.

Rejected: `[ModuleInitializer]` self-registration. The CLR loads an assembly lazily on first type reference,
so a `PackageReference` with no static type use does not guarantee the initializer runs. That failure is
silent and machine-dependent, which is the worst shape for a rollout whose purpose is attributing field
measurements to a backend. (Corrected 2026-08-05. This paragraph originally closed with "The same mechanism IS
used inside `KhaozEngine.Render.Tests`, where the test assembly is always loaded and the property it lacks for
consumers is guaranteed." The shipped mechanism is a static constructor on `GpuFactAttribute` in the shared
`KhaozEngine.TestSupport.Gpu` project, fired at test discovery, since CA2255 rejects a module initializer
inside that library too. `KhaozEngine.Render.Tests` keeps a thin module-initializer belt of its own, covering
the registry tests that carry no `[GpuFact]`.)

Rejected: reflection probing by assembly name. Trim and AOT hostile, invisible to `ArchitectureTests`, and it
turns a missing reference into a runtime string mismatch.

### 4.2 The `GpuDeviceContext` inversion

`GpuDeviceContext` is the only creation path and it is built around a Veldrid `GraphicsDevice`: the field,
`VeldridMap.ReadCapabilities(device)`, `D3D11ThreadingProbe.TryQuery(device, ...)` reaching
`BackendInfoD3D11.Device`, and the `((VeldridGpuDevice)GpuDevice).MarkDeviceDisposed()` cast in `Dispose`.
All four need a second shape, and this is the first implementation issue. A second private constructor takes
`(IGpuDevice, GpuCapabilities, GpuThreadingCaps?, GpuBackendSelection, bool ownsDevice)`. `MarkDeviceDisposed`
becomes an internal interface member or an action handed in at adoption. `D3D11ThreadingProbe` gains a
raw-pointer entry the native device can feed. Both paths keep the process-wide `_lifecycleGate`, both keep
the four ordered diagnostic logs, and both feed `GpuTelemetry.WithGpu` identically.

### 4.3 The `GpuBackendKind` append audit (I5)

Appending a member is safe for the enum itself (implicit ordinals Metal 0 through OpenGL 3, so the new member
is 4) and safe for telemetry (which writes the name). It is NOT safe for the thirteen places that switch on
it, compare against it, or derive a string from it. **Three of them degrade the native leg silently if
missed, and the worst of those does not throw.** Two further rows are listed only so a later reader does not
re-raise them.

(Corrected 2026-08-06. The enumeration below undercounts: fifteen sites in fact, the two extras being
`GpuBackendProviderMissingException.BuildMessage` and the token list in `GpuDeviceContext.LogSelection`'s
unrecognized-override warning (now derived from `GpuBackendSelector.RecognizedTokens` and pinned by audit
tests). The full corrected record is in the Vulkan design's section 4.2, so the Metal program's append
inherits the complete list.)

| Site | Behaviour today | Decision |
|---|---|---|
| `GpuDeviceContext.cs:160` (`LogThreadingCaps`) | `if (backend != Direct3D11) return;` | **Must change.** Include the native kind or the native leg loses the threading log AND the telemetry threading caps |
| `Internal/D3D11ThreadingProbe.cs:68` (`IsApplicable`) | `isWindows && backend == Direct3D11` | **Must change.** Same consequence, and this is the source of the header's `driverCommandLists` and `driverConcurrentCreates` |
| `GpuDeviceContext.cs:343-346` (`CreateWindowed`) and `:367-370` (`CreateHeadless`) | switch expressions that BOTH carry a `_ => GraphicsDevice.CreateMetal(...)` discard arm | **Must change, and this is the third silent-degradation site.** A new member does NOT throw `SwitchExpressionException`: it falls into the discard arm and asks Veldrid for a METAL device, which on Windows fails with an error naming the wrong API entirely. Add an explicit arm that throws a named exception saying the native kind is created through the provider registry and never here |
| `GpuBackendSelector.cs:280-283` (`ToVeldrid`) | switch expression | Explicit throwing arm. Verify whether it also carries a discard before assuming a missing arm is a compile error |
| `GpuBackendSelector.cs:174-177` (`TryParseBackend`) | four token cases | Add `d3d11-native` and `direct3d11-native` |
| `GpuBackendSelector.cs:219, 224` (`IsBackendSupported`) | early false for OpenGL, otherwise delegates to Veldrid | Route the native kind to the provider's own functional probe. Veldrid cannot answer for it |
| `GpuBackendSelector.cs:185-188` (`ProbeOS`) | Windows to `Direct3D11` | Unchanged until the flip (RO1 gate 5) |
| `GpuBackendSelector.cs:195` (`_windowCandidates`) | three entries | Unchanged until default-ready. A player does not choose an implementation |
| `Windowing/FrameCap.cs:72` (`Resolve`) | real software cap only on Metal plus vsync | Native falls into the uncapped arm, identical to the incumbent. Correct by default, recorded because it is #380's arm |
| `Windowing/DisplaySettings.cs:44` | same shape | Same |
| `GoldenCompare.cs:52` AND `:166` | the kind is lower-cased into the filename at TWO sites, not one | Both must route through `GoldenBackendToken` (I3), or a bake path and a compare path disagree about which family the native leg belongs to |
| `Internal/VeldridMap.cs:277-282` (`SupportsCompletionFences`) | switches on Veldrid's `GraphicsBackend`, NOT on `GpuBackendKind`, so it is not an append site at all | Listed only so a later reader does not re-raise it. Never consulted by the native device, which computes its own capabilities. No change |
| `Internal/VeldridGpuDevice.cs:141` (Metal frame capture) | `Backend == Metal` | Unaffected |
| `GpuDeviceContext.cs:306` (`CreateOrFallBack`) | `requested == fallback` value comparison, not a switch | Thirteenth site, and it is correct by default: on Windows the native kind is never equal to `ProbeOS`'s `Direct3D11`, so the request routes through the functional probe rather than short-circuiting. Recorded because it is invisible to a switch-arm sweep |

---

## 5. Command recording

### 5.1 The stream and the emitter

`D3D11CommandRecorder` implements `IGpuCommandList`. Each seam call appends a fixed-size 32-byte op struct
(opcode plus payload words plus a reference index) to a growable array, with resource arguments becoming an
index into a per-list reference list that also keeps the resource alive for the recording's lifetime.
`Begin()` truncates to zero and issues no native call, no lock and no device contact. `End()` seals.
`Submit(list)` takes the submit lock, replays, and releases.

The replay is `Replay<TEmitter>() where TEmitter : struct, ID3D11Emitter`, so the JIT monomorphizes and the
production path carries no indirection. The emitter is expressed in ENGINE-OWNED handle types, never raw COM
pointers, which is what makes a counting emitter a plain object. The emitter interface has no `Create*`
member at all (X1), so creating a view during replay is a compile error rather than an assertion.

**Order of magnitude, stated so the gate has something to falsify.** At roughly 1000 draws and roughly 10 ops
per draw the stream is 10k ops per frame, 320 KB of a reused array, and a switch dispatch measured in tens of
nanoseconds per op. Order 0.2 ms against 8.0 ms today. The payload question is answered by U1 and U2: uniform
writes go straight into the mapped ring, so the memcpy the renderer already performs IS the memcpy into
GPU-visible memory and there is no second copy. Bulk vertex, index and texture payloads take the CPU arena
(U4) and pay one memcpy, which they already pay today.

### 5.2 The schedule, ported intact (R5)

Not negotiable. This is the shape that produced the 40x shadow-encode collapse and 125 fps flat.

1. `SetGraphicsResourceSet` and `SetComputeResourceSet` RECORD only, comparing against what is already
   recorded and marking the slot `Clean`, `DynamicOffsetsOnly` or `Full`.
2. `Draw`, `DrawIndexed` and `Dispatch` flush every dirty slot through a pre-command hook, then issue.
3. `Full` slots activate fully. `DynamicOffsetsOnly` slots push ONLY the dynamic constant buffers and skip
   textures and samplers entirely.
4. Flush is in SLOT order. The one observable difference from bind order is a resource bound two incompatible
   ways at once, which D3D11 cannot honour either way.
5. `SetPipeline` drains pending sets under the OUTGOING layout and then FORGETS the records, before switching,
   because the layout decides register numbering. The wipe was missing from this clause as first written, which
   was an incomplete transcription of the fork (it does `ClearSets` plus `ClearArray` right after its drain) and
   shipped as a defect: with the records kept, a rebind of the same set at the same slot under a pipeline whose
   layout array renumbers that slot compares equal, marks clean and issues nothing, so the set stays at the
   outgoing registers while the incoming pipeline reads the new ones. A re-bind of the pipeline already current
   does neither, which is the fork's pipeline-identity guard.
6. A slot whose recorded set has since gone null is skipped.
7. Repeated dirty marks on one slot between two draws collapse to one flush.
8. Bound-record dedup is keyed and does not grow per rebind. The hot path is thousands of offsets-only
   rebinds of the same set per frame, so an O(rebinds) record is an O(n squared) frame.

### 5.3 Emission (R6, R7)

We own the register numbering (S2), so a layout's per-kind slots are contiguous by construction and sets
flatten continuously per kind across the pipeline's layout array. A full activation is therefore ONE array
call per (kind, stage):

| Call | Covers |
|---|---|
| `VSSetConstantBuffers1(base, 1, ...)` | the UBO, vertex stage |
| `PSSetConstantBuffers1(base, 1, ...)` | the UBO, pixel stage |
| `PSSetShaderResources(base, 4, ...)` | albedo, normal, roughness, shadow map |
| `PSSetSamplers(base, 2, ...)` | sampler, shadow sampler |

Four native calls for the model set, against the incumbent's 8 after batching and 42 before it. **The
worst-case 7-element set in the engine is the WATER set, not the model set, and it costs 6.** Both are 7
elements, but `WaterRenderer` declares `BathyTex`, `BathySamp`, `OceanMap`, `OceanSamp` and the dynamic
`Water` UBO at `Vertex | Fragment`, so the vertex stage needs its own SRV and sampler calls: two constant
buffers, two shader-resource arrays and two sampler arrays. Six is the number to quote as the bound, four is
the model pass. The scaling law moves from O(elements x stages) to O(kinds x stages), which the seam bounds
at 4 x 6 and which is 6 in practice. An offsets-only rebind, the shadow pass's thousands-per-frame path, is
exactly one `VSSetConstantBuffers1` plus the draw.

The immediate-emit fallback driver (R2) is selected by `KE_D3D11_RECORD=immediate` and shares every line
above. The environment variable is the M1 kill switch and exists from the moment issue 5 lands.

Every constant-buffer bind uses the `*SetConstantBuffers1` overload with explicit first-constant and
num-constants, including full-range binds, because the ring's per-frame base is always an addend (U1). The
`!DriverCommandLists` driver workaround stays: when the driver reports emulated command lists, issue an
explicit unset of the slot immediately before the `SetConstantBuffers1`. Both driver arms are asserted.

Also batch `IASetVertexBuffers(0, 2, ...)` when both streams are dirty, and keep redundancy caches for the
pipeline-level objects (VS, PS, blend, depth-stencil, rasterizer, input layout, topology) so a rebind to the
same state costs nothing. Those caches are reset by the one `ClearState` at the start of each replay and are
scrubbed precisely on resource disposal (R8).

Rejected: a post-record peephole optimiser over the stream. It would elide binds the online dirty tracker
cannot see across draw boundaries, at the cost of a second pass over 10k ops and of making the native-call
budget depend on an optimiser rather than on the recording, which is the wrong thing to freeze in a test.

---

## 6. Per-frame memory

### 6.1 The pathology

Veldrid's `UpdateBuffer` has a three-way branch, and branch 3, the pooled staging path, maps the immediate
context with `D3D11_MAP_WRITE` and no `DO_NOT_WAIT`, so each write blocks until the GPU is done with the
staging buffer being recycled. A reporting client paid 22 of those per frame and spent 12 to 17 ms per pass
on a scene that encodes in under 1 ms on Metal. Only a whole-buffer write from offset 0 takes the cheap path,
because D3D11 forbids a partial box on a constant buffer. Two releases of renderer-side engineering (17.18.0,
17.20.0) shrank it, and #408 listed the residue: water planes, overlay-mesh draws, SpriteBatch view-projection
slots and the splat material's combined block. 17.38.0 packed all four, so the Veldrid leg has no per-frame
partial uniform write left. The numbers above are what the pathology cost while it was live.

Verified: `GpuBufferUsage.Dynamic` has ZERO renderer call sites. Every per-frame UBO is created with plain
`GpuBufferUsage.UniformBuffer`, so the incumbent puts all of them on `ResourceUsage.Default` and every partial
write takes the stalling branch by construction.

### 6.2 The ring (U1, U2)

Every `UniformBuffer`-usage buffer is allocated as `size * FramesInFlight` with `D3D11_USAGE_DYNAMIC` and
`CPU_ACCESS_WRITE`, and carries a per-frame base offset. `FramesInFlight = 3`.

- A record-time `UpdateBuffer(buffer, offset, data)` writes at `mapped + frameBase + offset`. No staging
  buffer, no copy command, no stall, and no whole-buffer requirement.
- Every constant-buffer bind computes `firstConstant = (frameBase + rangeOffset + dynamicOffset) / 16` and
  `numConstants = align256(max(size, 256)) / 16`. BOTH numbers carry the 16-constant rule, not just the first:
  D3D11 wants `pNumConstants` to be a multiple of 16 constants in [0..4096], and an out-of-rule count makes the
  runtime drop the whole `*SetConstantBuffers1` call. The setters return void, so the slot stays empty after the
  replay's `ClearState` and the shader reads zeros with nothing reported. Rounding up is safe because
  `align256(size)` is exactly `D3D11UniformRing.SegmentStrideFor(size)`, so a bare buffer's rounded window is the
  frame's own segment and never reaches a neighbour. (Corrected 2026-08-05. This line originally read
  `numConstants = roundUp16(size) / 16` and claimed every real engine stride was already 256-aligned. That was
  false, and it was the defect: the model frame UBO binds 1008 bytes, the splat combined UBO 1120 and the palette
  buffer 1040, none of them 256-aligned. The premise shipped the silent drop that the first WARP run caught.)
- The ring is mapped lazily on the first write of a record phase with `MAP_WRITE_NO_OVERWRITE` and unmapped at
  the start of the next `Submit`. Two native calls per ring per submit, which is the floor.
- Frame N uses segment `N % FramesInFlight`. Before handing out a segment the allocator checks the fence of
  the frame that last owned it and blocks if incomplete (U5, gated by M3, overridden by
  `KE_D3D11_FRAMES_IN_FLIGHT=<n>`).

**The ring depends on real fences.** The segment check in U5 is a `GetCompletedValue()` read, so the fence
primitive (C5) has to exist before the ring can recycle a segment safely. That ordering edge is explicit in
section 15 and it is the one dependency the work breakdown must not lose: a ring built against submit-receipt
fences recycles a segment the GPU is still reading and corrupts a frame silently.

**#408's residue would have died here with no renderer change**, because a partial write is a sub-range of a
mapped segment and the constant-buffer partial-box restriction never applies. The renderers packed it away in
17.38.0 anyway, which is what the Veldrid leg needed, so this is now a property the backend keeps rather than a
reason to build it. The `IsWholeBuffer` discipline becomes
irrelevant on this backend and the guard stays, because it still describes the Veldrid leg. #407 (the
per-cascade bone palette re-upload) becomes cheap but stays wasteful, so it is defanged rather than resolved
and stays open.

**The correction both the issue text and one draft needed.** D3D11 has NO persistent mapping. A mapped
resource cannot be used by the pipeline. So the achievable form is a ring mapped for the duration of the
RECORD phase and unmapped before any GPU work touches it, and that form is only legal because recording is
deferred (R1). Under immediate emit, draws happen during record, so a ring mapped across the frame would be a
mapped resource bound to the pipeline. The ring and the recording model are load-bearing for each other and
must be weighed as one decision.

### 6.3 Reconciling the ring against `CreateResourceSet` pinning (U3)

Draft A rejected per-frame rings outright, on the grounds that `CreateResourceSet` pins
`GpuBufferRange(buffer, offset, size)` at load time across 68 call sites and a ring that re-points a buffer
per frame would silently invalidate every one. That objection is correct about the shape it names and does
NOT apply to the shape B proposes. B's ring is ONE `ID3D11Buffer` per `IGpuBuffer`, allocated N times larger.
The `IGpuBuffer` identity never changes, a set's pinned range still names the same handle and the same
logical offset, and the frame base is applied at BIND time by the `*SetConstantBuffers1` first-constant
computation, never baked into a set. The pinning holds.

Two invariants make that true rather than merely likely, and both are asserted at creation:

1. ONLY `UniformBuffer`-usage buffers are ring-backed. Structured buffers are not, which is why their
   full-range RAW SRV and UAV created once at creation remain correct. Verified: `OceanFftProducer` binds its
   structured buffers with no dynamic offset at all.
2. A ring-backed buffer never receives a non-constant-buffer view. If a future consumer ranges a structured
   buffer that happens to be ring-backed, the view would silently address the wrong segment, so the
   combination is rejected at creation rather than at draw. Concretely, a buffer created
   `UniformBuffer | StructuredBufferReadOnly` (or with either read-write structured bit) throws at creation on
   this backend. That combination is VACUOUS in the engine today, so nothing legitimate reaches the throw, but
   it is legal on the seam and the Veldrid backend accepts it, so this is a **backend-divergent creation
   failure** and must be documented as one rather than discovered by a consumer.

### 6.4 Everything else (U4)

Vertex, index and texture record-time payloads take a per-list reusable CPU arena, memcpy at record and
`UpdateSubresource` at replay. D3D11 permits a partial box on a non-constant buffer, so there is no partial
penalty there, and these are bulk and rare relative to the 40 UBO sites. A second ring for them buys an
asynchronous copy in exchange for a whole subsystem the traffic does not justify. Static and load-time
`DEFAULT` buffers keep `UpdateSubresource`.

**Device-level `UpdateBuffer` under the ring, specified rather than implied** (U5, 13 non-test sites). It is
off-timeline, so it can be called with no recording open, with one open, or from a foreign thread, and the
ring makes each of those a real question that draft B left unanswered.

- **Which segment.** "Current" means the segment the NEXT `Submit` will bind, which is the one any recording
  in progress is already writing. That is what preserves the documented semantic (the write lands when
  called, and a later-submitted list reads what the CPU wrote most recently). It is deliberately NOT the
  segment of the frame currently executing on the GPU.

  **Corrected in flight by #484: it writes EVERY segment, not only the current one.** This bullet as written
  above is what shipped first and it was wrong, in a way that only a consumer could show. It considered a
  uniform buffer that is rewritten every frame and nothing else, so a value written ONCE reached one segment
  out of three and two frames in every three bound memory nothing had ever written, silently.
  `ModelRenderer`'s splat-params tail does exactly that. The resolution is option (a) of #484, ring-side: an
  off-timeline write reaches all `FramesInFlight` segments, so it persists for the buffer's life the
  way the incumbent's does, while a RECORD-TIME write stays current-segment only (every shipped one of those
  is unconditional per frame, so replicating them would be N memcpys for a value the next frame overwrites,
  on the hot path). The added segments are gated on the same completion read as `AcquireSegment`, and a
  segment that fails the gate is not waited for: the write is queued as a PENDING PATCH that the segment's
  next acquire applies, so this call still never blocks and a caller holding the submit lock is legal.
  WAITING WAS DRAFTED FIRST AND WAS WRONG, which is the part worth keeping here rather than in the shipped
  docs: a retry loop that waited for every non-current segment to be free at once never terminates in the
  GPU-bound steady state, because the frame thread submits again for every frame the GPU retires, so one
  non-current segment is always in flight. The current segment stays ungated and is always copied, because
  gating it would change the semantic this bullet describes and deferring it would put its value a whole wrap
  away. Shipped behaviour is the package README's ring section and `docs/USING-KHAOZENGINE.md`.
- **Who maps.** The ring is unmapped at the start of `Submit`, so an off-timeline write arriving between two
  frames finds it unmapped. That write maps `NO_OVERWRITE`, writes, and leaves it mapped for the next record
  phase to reuse. Mapping is idempotent and refcount-free: one flag on the ring, checked under the same lock
  as the write.
- **Thread legality.** Device-level `UpdateBuffer` and `UpdateTexture` are callable from ANY thread, behind a
  SHORT lock scoped to the write itself and never to a frame. This is draft A's D19 rule and it is adopted
  rather than dropped: B's W4 named the short lock for staging `Map` and `Unmap` and said nothing about the
  device-level update path, which is the busier of the two. The lock is `_submitLock`, so an off-timeline
  write cannot land in the middle of a replay. Since the corrected U5 above never waits, a caller ALREADY
  holding that lock is legal too, which is a case the waiting draft would have deadlocked on.
- **What is still forbidden.** Writing off-timeline to a range a recording has already recorded a bind for,
  and then expecting the recorded bind to see the old value. That was already true before the ring and the
  seam already documents the CPU being several frames ahead, so nothing changes for a caller. It is restated
  because the ring makes the failure quieter.

**Strongest rejected alternative.** Draft A's dynamic-plus-CPU-shadow scheme, with `WRITE_NO_OVERWRITE`
partial flushes where the cap allows and whole-shadow `WRITE_DISCARD` re-uploads where it does not. It works
under either recording model, which is its real virtue, and it is the named fallback if M1 fails (R2).
Rejected as the primary because it doubles resident memory for every dynamic buffer, adds a memcpy the ring
does not need, pays a map and unmap pair per flush point (section 2.1), and its no-cap degradation path is
O(writes x buffer size) on exactly the old-driver machines where measurement is hardest to obtain.

---

## 7. Resources, views and state objects

All views are created eagerly (X1). The evidence is blunt: all 25 `DEVICE_REMOVED` stacks in #423 surfaced
inside the `D3D11TextureView` constructor during `ActivateResourceSet`, so lazy view creation put an
allocation on the draw path and put it on the exact path a corrupted context makes fail.

Textures get, from the declared usage bits at creation: a full-chain SRV if `Sampled` or `GenerateMipmaps`,
an RTV at mip 0 layer 0 if `RenderTarget`, a DSV if `DepthStencil`, a UAV at mip 0 if `Storage`. At most four
objects, and the bound is real rather than optimistic because the seam cannot express anything else:
`CreateFramebuffer` has no mip or layer parameter, `ResolveTexture` is subresource 0 only, and per-face
cubemap rendering is not expressible. A `GpuBufferRange` inside a `CreateResourceSet` description resolves at
SET creation, still not at draw time.

Blend, depth-stencil, rasterizer and input-layout objects are built at `CreateGraphicsPipeline` and stored on
the pipeline. The input layout needs the vertex shader bytecode signature, which is available at that moment.
The incumbent's 328-line `D3D11ResourceCache` is dropped (X2), because the D3D11 runtime already returns an
existing object for an identical state description. That is a claimed runtime behaviour rather than one this
design measured, and the worst case if it is wrong is a small allocation count, not a correctness failure.

`DeviceLiveness` is reproduced exactly (X3): a shared volatile token flipped by the context inside its
lifecycle lock before the real device is destroyed, every wrapper's `Dispose` gated on it, `IGpuFence.Signaled`
reading true after death, and `WaitForIdle` a no-op after death.

---

## 8. Shader path

**DXC is eliminated on a fact, not a preference.** DXC emits DXIL. D3D11's `CreateVertexShader` and friends
consume DXBC. There is no supported DXC path to DXBC, so DXC is off the table for a D3D11 backend regardless
of anyone's view of SM 6.x, and it should not be relitigated.

Hand-authored HLSL is rejected outright. There are roughly 30 graphics programs (27 non-test
`CreateShadersFromSpirv` call sites) plus 2 compute kernels, built out of 52 GLSL source constants, all
`#version 450`, sharing spliced common blocks. A program is a vertex and fragment PAIR, so the constant count
is close to twice the program count and the two must not be conflated: 52 is sources, about 30 is programs.
Metal and Vulkan keep consuming the same GLSL until Phase 3 at the earliest, so a second authored dialect
means every shader change happens twice and the two drift.

So (S1): GLSL 450 stays the single source, the static `SpirvCompilation` API does GLSL to SPIR-V to HLSL
through the internal helper in `KhaozEngine.Gpu` (P2), and the backend calls `Vortice.D3DCompiler.Compiler.Compile`
itself to `vs_5_0` / `ps_5_0` / `cs_5_0` at `OptimizationLevel3`, or `Debug` under `KE_D3D11_DEBUG`.
`SpirvLocalSize` keeps hand-parsing the workgroup size out of the module, because D3D11 takes it from the
module while the seam's `IGpuComputeShader.ThreadGroupSize*` must still report it.

Owning the FXC call buys the disk cache (S4), the register numbering (S2) and a CPU-only FXC leg in CI (S5).

### 8.1 Register numbering (S2)

The emitted HLSL numbers its own registers and the CPU side must agree exactly, or everything compiles and
every pixel is wrong. Within one layout, each element gets a per-kind declaration-order slot: `UniformBuffer`
to `bN`, `Sampler` to `sN`, `TextureReadOnly` and `StructuredBufferReadOnly` SHARING the `tN` counter, and
`StructuredBufferReadWrite` and `TextureReadWrite` SHARING the `uN` counter. Across layouts, sets flatten in
PIPELINE-ARRAY order, per kind. The GLSL `set=` number does NOT decide the base: `SpriteBatch` deliberately
puts its UBO at `set=1` with texture and sampler at `set=0`, so "set 0 comes first" is false in shipped code.

This gets a CPU table test over EVERY layout the renderers declare, not a hand-picked few: there are more
than thirty `CreateResourceLayout` sites outside the seam package and the tests, covering well over a dozen
distinct shapes. The "six" figure that appears in the constraints pack is the count of DYNAMIC layout
ELEMENTS, which is a different and much smaller set, and testing only those six would leave the whole
texture-and-sampler register space unasserted, which is exactly the space where a numbering error compiles
cleanly and renders wrongly.

### 8.2 Pinning the cross-compile options (S3, new)

Draft A asserted that using the same SPIRV-Cross yields byte-identical HLSL and therefore identical DXBC.
That is true only if the options match, and `CreateFromSpirv` derives something from `ResourceBindingModel.Improved`
that a direct `SpirvCompilation.CompileVertexFragment` call does not get for free. The implementation issue
reads `ResourceFactoryExtensions.CreateFromSpirv` in the vendored fork, pins the exact options in a constant
with the citation, and asserts emitted-HLSL byte equality per program against a checked-in hash. That is
device-free, runs on every leg, and converts "should be identical" into a checked fact before a single golden
is run.

### 8.3 The holed-signature hazards (S5)

Two documented production incidents, both D3D11 and FXC and WARP specific, both tolerated by Metal and
Vulkan. SPIRV-Cross drops unread vertex inputs and a non-contiguous `TEXCOORD` sequence miscompiles: the
shadow vertex reads only Position and IModel0 to 3, so locations 1 to 4 and 9 to 11 were dropped and building
that pipeline at scene construction corrupted WARP so the main model and splat passes rendered no colour. The
interpolant twin dropped a fragment-unused interpolant below the live block, the highest live interpolant read
garbage, and the terrain blew to flat white.

Because this design keeps SPIRV-Cross, the drop-unused behaviour is inherited unchanged, so **the sinks and
the interpolant ordering stay necessary and must stay**. They stop being remembered and become enforced:
`ShaderValidation` gains a Windows-only leg that actually runs FXC on the HLSL it produced (which it has never
done) plus an assertion that the reflected vertex input signature has contiguous `TEXCOORD` indices from 0,
and the same check runs at pipeline creation so a runtime failure names the shader rather than corrupting a
frame. That leg is a plain `[Fact]` with no device, on the Windows GPU workflow, so it runs on every push
that touches the path filter rather than only on the schedule.

**Strongest rejected alternative.** Take SPIRV-Cross's C API directly now, while there is only one native
backend to migrate rather than three after Phase 3. Rejected because direct bindings change the emitted HLSL,
which changes the register scheme AND the drop-unused behaviour, which puts the 36 goldens and both
documented WARP corruption workarounds in play simultaneously. That is a separate change with its own risk
budget, filed as F2.

---

## 9. Swapchain, present, resize and threading

### 9.1 The swapchain stays on the blit model for v1 (W1)

Both drafts specified a flip-model swapchain (`IDXGIFactory2::CreateSwapChainForHwnd`, `FLIP_DISCARD`,
optional `ALLOW_TEARING`), and BOTH named the same counterargument against their own decision: the swapchain
is the one area with zero automated coverage anywhere in the net (goldens are headless, the shape tests are
device-free, the WARP leg never presents), so a flip model is validated only by a human looking at a window,
which is exactly the evidence class that produced the Windows black screen, #380, and the fork's resize
hazard. Both drafts then said that a judge wanting one edit to reduce rollout risk should take this one, and
neither took it, because each wanted a complete design.

Take it. v1 reproduces the incumbent's present path exactly: unversioned `IDXGIFactory`, `BufferCount = 2`,
`SwapEffect.Discard`, `Windowed = true`, `SampleDescription(1, 0)`, `B8G8R8A8_UNorm`, present at sync interval
1 or 0 with no other throttling. The soak (gate 4) then measures the recording model and the memory model and
nothing else, which is what makes a regression attributable. Flip model, `ALLOW_TEARING`, the RTV-unbound-at-
present obligation and a waitable frame-latency swapchain are one sequenced follow-up (F4) with their own
manual validation and their own #380 measurement.

This is not a split of the difference. It is the sequencing both authors identified as correct.

### 9.2 What does change (W2, W3)

Framebuffer identity is STABLE across resize (W2), independently of swap effect. The incumbent disposes the
depth texture and the whole framebuffer and builds a new one, which is why `VeldridGpuDevice.ResizeSwapchain`
re-wraps only on a reference change, a workaround whose comment names the Windows black screen after
fullscreen, maximise or drag-resize. We own the wrapper, so we keep its identity and swap the views
underneath. That makes D3D11 behave like Metal, which is the behaviour the rest of the engine was written
against.

Resize is enforced rather than documented (W3). `ResizeSwapchain(w, h)` stores the pending size, coalesced to
the last requested, and returns. The submit thread applies it at the next present boundary, where it provably
owns the context and no replay is in flight. Cost is one frame of resize latency, which is invisible. Gain: a
foreign-thread resize during recording becomes structurally impossible instead of contractually forbidden.
The Silk `FramebufferResize` callback fires on the render thread today, so nothing observable changes in the
shipped loop and the contract hardens against a consumer that does otherwise.

### 9.3 Threading (W4, W5)

The shipped contract:

- Recording is lock-free and touches no device state (the first record-time uniform write per ring acquires the
  mapping under the submit lock for the Map call alone, per 6.2's lazy map, and the `KhaozEngine.Gpu.D3D11`
  README's threading section is the authoritative statement of the threading contract). Any number of
  `IGpuCommandList` instances may record concurrently on any threads.
- One `_submitLock` covers replay, present and the resize apply. It is held for microseconds, not a frame.
  This deletes #415's entire hazard list rather than guarding it: there is no frame-long monitor to exit from
  a foreign thread, no `Map` serialising behind a frame, and no lock-recursion leak to fix.
- `Map` and `Unmap` on staging resources take `_submitLock` only for the duration of the map call.
- Resource creation is free-threaded, serialised behind a short creation lock when `DriverConcurrentCreates`
  is false, which the threading probe already reports.
- Device-level `UpdateBuffer` and `UpdateTexture` are callable from any thread behind the same short lock,
  per 6.4.
- `Submit` order is the observable order.

The process-wide `GpuDeviceContext._lifecycleGate` is unchanged and still serialises device creation and
disposal across backends.

**Not in the shipped contract (W5).** The design STRUCTURALLY PERMITS concurrent recording on several
threads, because `Begin` touches no device state and each recorder owns its own arrays. That is a property of
the architecture and it is not a supported feature in v1: nothing in the engine asks for it, no test exercises
it, and the redundancy caches and ring allocator have not been reviewed for concurrent record. A consumer
recording from two threads in v1 is outside the contract even though it will probably work, which is exactly
the shape that produces a bug report nobody can triage. Shipping it is F3.

### 9.4 The implicit viewport (W6)

There is no `SetViewport` on the seam at all. The engine gets a viewport because Veldrid's base
`CommandList.SetFramebuffer` auto-calls `SetFullViewports()` and `SetFullScissorRects()` on every framebuffer
bind. A backend that does not replicate this rasterises nothing.

**The implicit behaviour includes an identity guard, and replicating it is not optional.** Veldrid's
`SetFramebuffer` is wrapped in `if (_framebuffer != fb)`, so re-binding the SAME framebuffer issues nothing
at all and, critically, does NOT reset the viewport or the scissor. An unconditional emit would therefore
diverge on the shipped sequence `SetFramebuffer(fb)`, `SetScissorRect(...)`, draw, `SetFramebuffer(fb)`,
draw, where the second bind would silently restore the full scissor and the second draw would render outside
the intended rectangle. That is golden-visible, and the first version of this spec froze the wrong behaviour
into the tally invariant, which would have made the test certify the bug.

So `SetFramebuffer` records an op that replays as `OMSetRenderTargets`, then `RSSetViewports(1, full)` and
`RSSetScissorRects(1, full)`, **only when the bound framebuffer actually changes**. A redundant re-bind emits
zero native calls. The device-free tally asserts exactly one viewport call and one scissor call per framebuffer
CHANGE and zero for a redundant re-bind, and that pair of assertions is what pins the guard. A later explicit
`SetScissorRect` overrides the scissor and must not be undone by anything.

No `SetViewport` member is added to the seam here: 48 `SetFramebuffer` sites and zero viewport sites means new
public API with no consumer, and it is a reasonable Phase 3 addition when the seam is being revisited anyway.

---

## 10. Compute, barriers, MSAA, staging and fences

### 10.1 Compute and the two ordering rules

Rule 1 (compute writes a storage texture, a graphics pass in the same list then samples it) names the D3D11
mechanism in the seam itself: the backend unbinds the UAV as the SRV is bound. So the backend tracks bound
SRVs and UAVs per texture and automatically unbinds the conflicting one in both directions (C1), implemented
where the bind arrays are assembled, so a texture appearing in the SRV array whose UAV is currently bound
emits a null into the UAV slot in the same batch. Under array batching this costs nothing extra.

Rule 2 (a dispatch reading an earlier dispatch's writes must be separated by `End`, `Submit`, `WaitForIdle`)
is honoured as written (C3). The ocean's `PrimeRowPass` does exactly this and measures the stall into
`LastStallMs`. No new barrier member is added, because rule 2 is a cross-backend contract and Vulkan and Metal
are out of scope until Phase 3.

Worth naming so it is not rediscovered: D3D11 is a hazard-tracked API and inserts the synchronisation between
dependent dispatches on the same context itself, so rule 2 is a Vulkan-shaped requirement being paid on a
backend that does not need it. The right resolution is a seam capability that lets a consumer skip the drain
on backends that track hazards, which is a seam change plus a renderer change and is therefore explicitly out
of #421's "zero renderer changes by construction" scope. Filed as F1 with the ocean named as its consumer.

Structured buffers keep the RAW byte-address treatment (C2). SPIRV-Cross emits `ByteAddressBuffer` and
`RWByteAddressBuffer` for a GLSL storage block, never `StructuredBuffer<T>`, so `StructureByteStride` stays
advisory and the SRV and UAV are created with raw flags. Keeping this identical is not optional. It is why the
ocean's existing shaders work.

### 10.2 MSAA (C4)

Three resolve sites, all whole-texture at mip 0 layer 0, one `ResolveSubresource(dst, 0, src, 0, format)`
each. `MaxMsaaSampleCount` is the MIN over `R8G8B8A8_UNORM`, `R32_FLOAT` and `D32_FLOAT_S8X24_UINT` computed
with `CheckMultisampleQualityLevels`, because every MRT attachment must support the count, with any query
failure yielding 1. It is asserted equal to the incumbent's on the same machine by T4, because a different
answer silently changes what `AntiAliasing.ResolveFor` picks, which changes the field look and the golden
output.

CORRECTED as implemented: the depth attachment is asked about as `R32G8X24_TYPELESS`, not as the fully typed
`D32_FLOAT_S8X24_UINT` this clause names. The incumbent's `GetSampleCountLimit(D32_Float_S8_UInt,
depthFormat: true)` runs the pair through `D3D11Formats.ToDxgiFormat` first, and that mapping answers
`R32G8X24_Typeless` (`src/Veldrid/D3D11/D3D11Formats.cs` lines 131 to 133), so the typeless sibling is the format
it actually hands the driver. Written as first drafted, the two backends would have asked the driver two
different questions and T4's "asserted equal to the incumbent's" would have rested on them happening to answer
the same, which is a parity claim that holds until a driver disagrees. The typeless format makes it satisfiable
by construction.

One departure: an out-of-range requested sample count THROWS at texture creation rather than silently falling
to 1. The engine already clamps upstream, so nothing legitimate reaches the throw, and a silent MSAA downgrade
presents as a golden mismatch that reads like a rendering bug.

### 10.3 Fences (C5)

Veldrid's D3D11 fence is a `ManualResetEvent` set on the CPU the instant `ExecuteCommandList` returns, a
submit receipt rather than a completion signal, which is why `SupportsCompletionFences` is hardcoded false,
`GpuRetireBarrier.TryCreate` returns null, `GpuRetireQueue` keeps a frame-count fallback and two tests
skip.

Primary: one device-wide monotonic `ID3D11Fence` via `ID3D11Device5::CreateFence`, signalled with
`ID3D11DeviceContext4::Signal` at the end of replay. `IGpuFence.Signaled` is `GetCompletedValue() >= target`,
a non-blocking read, which is exactly what the seam demands. `Reset()` re-arms with a fresh target.
Fallback for pre-Windows-10-1703: `ID3D11Query(QueryType.Event)` polled with `DO_NOT_FLUSH`, also
non-blocking. `SupportsCompletionFences = true` on both paths.

Downstream, all flipping the day this lands: the retire barrier stops returning null, `GpuRetireQueue`
gets the fenced path, `RetireFenceGpuTests` and `Scene3DUnloadDrainTests` stop skipping, and the barrier's own
recorded hazard (it submits an empty list from inside `Scene3D.Begin`) does not exist under deferred recording,
because replaying an empty stream clears nothing. **The one thing that made real fences dangerous on D3D11 is
removed by the recording decision**, which is why the two land in the same phase. #425's bound is provided
incidentally by the ring's fence-gated recycling (U5) and stays open for a DESIGNED bound rather than an
emergent one (F5).

### 10.4 The empty `WaitForIdle` (C6)

`WaitForIdleCore()` in Veldrid's D3D11 backend is an empty method body, verified directly, and the seam has 83
`WaitForIdle()` call sites, 32 of them outside the test projects. Every drain in the engine currently does
nothing on D3D11, including the one half of the only ordering guarantee the seam offers.

The uncomfortable part is that this has never caused a known bug, and there is a reason. D3D11 tracks resource
hazards automatically, defers destruction by reference counting (so `WaitForIdle(); tex.Dispose()` is safe
without the drain), and `Map(READ)` on the immediate context blocks until the resource is ready by definition.
Every category of call site (dispose drains, readback, the ocean's compute chain, target recreation) is
satisfied by one of those three. The empty body is arguably correct-by-API rather than an oversight.

Implement it for real anyway, in this order of weight. A real drain can only ever be MORE conservative than an
empty one, so the risk is purely performance and therefore measurable rather than latent. The seam names
`WaitForIdle` as its ordering primitive, and a primitive that does nothing on one backend makes the contract's
guarantees backend-dependent in an undocumented way. The measurement is currently a lie:
`OceanFftProducer.LastStallMs` reads near zero because the call it times is empty, and the native backend
exists to enable attribution work. And the one HOT caller, the `GpuRetireQueue`'s per-boundary drain fallback,
disappears in the same change because real fences ship with it, so the net hot-path cost is plausibly
negative.

Gated by M2, with the `KE_D3D11_REAL_DRAIN=0` kill switch and drain telemetry. Also file the divergence
(F6): the Veldrid D3D11 path's drain remains a no-op for as long as both implementations ship, so a test that
passes on both passes for different reasons and is not evidence about either.

---

## 11. Capabilities, diagnostics, WARP and device loss

`ReadCapabilities` stays the single source both `GpuDeviceContext.Capabilities` and `IGpuDevice.Capabilities`
come from, after they drifted before 15.2.0. The native device implements one and the context reads it from
the device.

| Member | Native source | Parity |
|---|---|---|
| `ClipSpaceYInverted` | false | identical |
| `DepthRangeZeroToOne` | true | identical |
| `DeviceName` | `IDXGIAdapter::GetDesc().Description`, trailing nulls trimmed | identical by construction |
| `SamplerAnisotropy` | true | identical |
| `SamplerLodBias` | true | identical |
| `MaxMsaaSampleCount` | MIN over the three MRT formats (C4) | asserted identical |
| `SupportsShadowMaps` | `CheckFormatSupport(R32_FLOAT)` for render target and shader resource | asserted identical |
| `SupportsCompute` | true | identical |
| `SupportsCompletionFences` | **true** (C5) | the ONE permitted difference |

The incumbent's sampler HARDCODES are reproduced (comparison null, minLod 0, maxLod `uint.MaxValue`, border
`TransparentBlack`), because they are reachable and they affect output. **The device's shared point and linear
pair adds a fifth: WRAP on all three axes**, taken from `D3D11SharedSamplers` rather than from the engine's
identically named `GpuSamplerDescription.Point` / `.Linear` statics, which default every axis to CLAMP. The
incumbent's pair is Veldrid's `SamplerDescription.Point` / `.Linear`, which wrap, and the renderers assume wrap.
Reading the address mode off the statics because the names matched is what cost `scene3d_texbillboard` (0.393)
and `scene3d_particles_flipbook` (0.359) on run 30963173087. Its two silent DEGRADATIONS
(anisotropic falling to trilinear when `SamplerAnisotropy` is false, `MipLodBias` forced to 0 when
`SamplerLodBias` is false) are dropped, because both capabilities are always available on the feature levels
this backend requires and reproducing unreachable code is pure cost. `GpuClip.Correct` needs no change: it
negates clip-space Y only when `ClipSpaceYInverted` is set, and D3D11 is not.

**WARP (G2).** Nothing in the engine selects WARP today. The Windows CI leg gets it only because
`windows-latest` has no hardware adapter and DXGI falls back. That is a latent CI-integrity problem: a runner
image that grows a paravirtual adapter silently changes the rasterizer the golden gate tests. Add
`KE_D3D11_ADAPTER` accepting `warp`, `hardware`, an index or a name substring, log it through the existing
adapter line, record `DXGI_ADAPTER_FLAG_SOFTWARE` in the telemetry session header, and pin `warp` in CI. A
named adapter that is not present is a WARN plus default enumeration, never a hard failure.

**Device loss (G3).** `DEVICE_REMOVED` is sticky and surfaces at the next HRESULT check far from the cause,
which is why #423's 25 stacks all pointed at a texture view constructor rather than at the corruption. Check
the HRESULT after `Present`, after every `Map` and after replay, and on `DXGI_ERROR_DEVICE_REMOVED` or
`DEVICE_RESET` call `GetDeviceRemovedReason()` IMMEDIATELY, before subsequent calls muddy it, latch it, flip
the liveness token so all subsequent disposals are no-ops, and surface it in the 17.25.0 session header. That
closes #427 for the native leg on the day the backend lands, which is the correct time, because retrofitting
the reporting after the first field crash wastes the crash.

**Debug layer (G4).** `KE_D3D11_DEBUG=1` creates the device with `D3D11_CREATE_DEVICE_DEBUG` and pumps
`ID3D11InfoQueue` messages into the engine logger at a rate limit, with corruption and error severities
promoted to WARN. The engine hardcodes Veldrid's debug flag false today, so there is currently no way to get
debug-layer output from a diagnostic run at all. This is the cheapest diagnostic in the design and it is what
would have diagnosed #423 in one session instead of a program.
`KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS` keeps its exact current semantics including value parsing and the
unrecognised-value warning, honouring #417's read that with lean submission the driver threading helps and
should not be fought. No D3D11 frame-capture integration: RenderDoc attaches externally and needs no engine
support.

---

## 12. Test plan

| Layer | What it covers | Runs where |
|---|---|---|
| The 36 committed D3D11 goldens, shared family (T1) | Pixel equivalence against the SHIPPED D3D11 backend at 0.06 per channel, same WARP rasterizer, same DXGI adapter. No rebake | New `direct3d11-native` leg, golden-only on push, full on schedule |
| `CrossBackendGoldenTests` | Unchanged. Still three families, still the 0.20 ceiling | Every `dotnet test` |
| Native-call budget, device-free plain `[Fact]` (T2) | The #418 fan-out defect class | Every `dotnet test`, every PR, the cheap Linux leg |
| Native-call parity `[GpuFact]` (T3) | The device-free harness drifting from the real replay path | Windows matrix |
| `NativeVsVeldridCapabilityParityTests` (T4) | Silent capability drift, especially the MSAA clamp and the adapter name | Windows matrix |
| Full WARP suite | The 3170 passed / 0 failed / 2 skipped baseline, with the two skips becoming RUNS once completion fences land, so the native target is 3172 / 0 / 0 | Windows matrix, schedule and dispatch |
| HLSL byte-equality per program (S3) | Cross-compile option drift against the incumbent | Every `dotnet test` |
| Register-numbering table test (S2) | Everything compiles and every pixel is wrong | Every `dotnet test` |
| Shader FXC validation plus signature contiguity, plain `[Fact]` (S5) | HLSL that SPIRV-Cross emits and FXC rejects, and holed input signatures | Windows GPU workflow, no device |
| Ring and fence unit tests | Segment recycling under fence pressure, wrap behaviour, offset alignment, the ring-backed-buffer view invariant (U3) | Every `dotnet test` |
| `OpenListTrackingGpuDevice` | Nested `Begin`. Stays the PORTABLE guard for the Veldrid leg, passes trivially on native, and is not evidence about native (R4) | Every `dotnet test` |
| **Native recording-contract test**, device-free `[Fact]` | The thing R3 and R4 actually assert and which nothing else covers: open N recorders, interleave recorded commands across them, submit them out of record order, and assert the replayed op sequence is exactly per-list order concatenated in SUBMIT order, with exactly one `ClearState` at the head of each replay. Without it, "nested Begin is legal and submit order is observable" is a sentence in a design doc with no executable meaning | Every `dotnet test`, homed in issue 6 |
| `ArchitectureTests`, `VeldridLockdownTests`, extended `GpuPublicApiTests`, the new no-Veldrid-edge assertion | Zero renderer changes, no Veldrid leakage, opt-in isolation | Every `dotnet test` |
| `GpuDeviceLifecycleTests` | Concurrent create, use and dispose against the native provider | Windows |

**The native-call budget's gate is not the absolute numbers (T2).** Draft A specified exact frozen figures
derived from reading layouts rather than from running anything, and then said in its own counterargument that
they should have been upper bounds. Draft B said to weight the marginal assertions. Neither decided it, so:
the GATE is (a) four structural invariants (zero `Create*`, which is a compile error anyway, zero `Map` or
`Unmap` during replay, exactly one `ClearState` per submit, and one `RSSetViewports` plus one
`RSSetScissorRects` per framebuffer CHANGE with zero for a redundant re-bind, per 9.4), (b) the MARGINAL
per-draw deltas (5 distinct meshes versus 1, and
18 draws versus 6, must move the total by an exact per-draw delta, and an offsets-only rebind must be exactly
one call per visible stage), (c) the trace being byte-identical for 8 instances of one mesh and for 1, and
(d) upper bounds on the full-activation fan-out cases. Absolute totals are recorded as documentation and may
be updated freely. A test that is routinely edited to match reality stops being a gate, and the per-draw
delta jumping from 2 to 8 is the #418 defect returning, which no legitimate renderer change causes.

**CI (T5).** A `direct3d11-native` matrix leg on `windows-latest` with `KE_GRAPHICS_BACKEND=direct3d11-native`,
`KE_D3D11_ADAPTER=warp`, `KE_GPU_TESTS=1`, golden-only on push and full suite on schedule and dispatch,
mirroring the existing `direct3d11` leg's policy. Cost note for whoever approves it: this is a private repo
and Windows bills at 2x, so this doubles the D3D11 leg's spend on the gated paths. The leg is path-filtered
and the push path is golden-only, so routine cost is small.

**The naming contract is load-bearing.** `cross-platform-gpu.yml` selects with
`--filter FullyQualifiedName~Golden`, so a GPU test whose fully-qualified name lacks "Golden" is silently not
run cross-backend. The native-call budget test is deliberately NOT named "Golden", because it must run on the
Linux leg rather than inside the golden filter. Any new `[GpuFact]` that must run cross-backend carries the
substring or is added to the full-suite filter explicitly.

---

## 13. Unproven bets: gates, kill switches, exit criteria

Every decision below rests on reasoning rather than measurement. Each names the measurement that settles it,
the switch that turns it off in the field, and the criterion that retires the switch. A bet without all three
is not shipped.

| # | Bet | Measurement gate | Kill switch | Exit criterion |
|---|---|---|---|---|
| M1 | The deferred recording model (R1) is not slower than immediate emit. The named risk is NOT the memcpy but the loss of overlap between engine CPU work and driver-side consumption, given #417 measured that driver threading helps | See the note below. **M1 is a milestone, not an issue's exit criterion**, and it is measured on the minimal renderable path, not on a stub | `KE_D3D11_RECORD=immediate` selects the immediate-emit driver (R2), with the ring degrading to per-flush map and unmap | Deferred is within 2 per cent of immediate on the reporting machine's #410 scene at the 17.24.0 baseline. Passing REMOVES `KE_D3D11_RECORD` and deletes the immediate driver. Failing inverts it: R2 becomes the shipped model, U2 degrades to draft A's shadow scheme, and the deferred driver is deleted instead |
| M2 | A real `WaitForIdle` (C6) costs less than it saves, because the `GpuRetireQueue`'s per-boundary drain fallback disappears in the same change | Drain count and total drain duration per frame in the telemetry session, plus `OceanFftProducer.LastStallMs` becoming a true number | `KE_D3D11_REAL_DRAIN=0` restores the no-op, for the soak window only | Two consecutive soak builds show total drain duration under 0.2 ms per frame at the 125 fps baseline. Then the switch is removed. If it does not, F1 (the automatic-hazard capability) becomes a blocker rather than a follow-up |
| M3 | `FramesInFlight = 3` is enough that segment backpressure never blocks the CPU (U5). This is also a behaviour change: the seam documents that the CPU may be several frames ahead | A backpressure stall counter and accumulated stall time in the telemetry session | `KE_D3D11_FRAMES_IN_FLIGHT=<n>` | Backpressure stall count is zero across a full soak capture window. A non-zero count means 3 is wrong, not that the design is |
| M4 | Array-batched emission (R6) collapses the model set to 4 native calls, the worst-case water set to 6, and an offsets-only rebind to 1 per visible stage | The device-free budget test, confirmed on the first green run and then frozen as marginal assertions (T2) | none needed, it is a call-count property with no runtime risk | First green run's measured per-draw deltas are recorded in this document's history and become the frozen marginals |
| M5 | (observation, not a bet) W1 defers the flip model, so v1 carries the incumbent's blit present path unchanged and CANNOT discriminate whether the blit model is the mechanism behind #380 | None available in v1. A native soak that reproduces #380 unchanged is consistent with both "blit causes it" and "blit does not", so it proves nothing either way. The DISCRIMINATING measurement is the same scene on the F4 flip-model prototype, A/B against v1's blit path on the same machine and build | n/a, v1 changes nothing here | Recorded so that a reader does not mistake an unchanged #380 in the soak for evidence. #380 stays its own issue with its own unverified mechanism list |

**Why M1 is a milestone and not an issue's exit criterion.** The first version of this spec made M1 the exit
criterion of the command-stream issue and had it block the issues that build the rest of the backend. That is
circular: measuring end-to-end frame time on a real scene needs resources, pipelines, shaders, a bind flush,
draws and a swapchain, which are exactly the issues M1 was said to block. Nothing renderable exists when that
issue closes, so the only measurement available at that point IS the stub-emitter microbenchmark this spec
already rejected as unable to see the risk.

So: implementation issue 5 exits on build-and-unit only, meaning both drivers exist, compile, and pass their
device-free op-encoding and replay-ordering tests. **M1 is a named measurement milestone taken after the
minimal renderable path lands**, which is issues 5, 6, 7, 9, 10, 11 and 14. At that point the backend can
render a frame and both drivers can be A/B'd on the same build. M1 gates exactly one thing: removal of
`KE_D3D11_RECORD` and deletion of the losing driver. It does not gate any implementation issue, and no issue
waits on it. If M1 has not been taken by the time gate 3 is assessed, gate 3 is not met.

---

## 14. Rollout

Opt-in first, field soak on the #410 reporting machine through Ruinborne's established update-feed flow, then
default, per #420's decision record. Five gates, all green before the flip.

1. All 36 goldens green against the shared `direct3d11` family at 0.06, with the observed worst-cell delta
   RECORDED. Two implementations of the same API on the same rasterizer should agree far inside the tolerance,
   and the recorded number is what a future reader compares against.
2. Full WARP suite at **0 failed and 0 skipped**, with the passed count at or above the incumbent's on the
   same commit. The bar is deliberately RELATIVE: the 3170-passed figure from run 30727547602 is a snapshot of
   a suite that grows every release, so an absolute number would be stale before the backend ships. What is
   absolute is the skip count. A native run still reporting the two `RequiresCompletionFences` skips is a
   failed implementation of C5 no matter what else is green.
3. Native-call budget green, with the per-draw marginal deltas recorded here so a future reader can see what
   correct looked like. **M1 taken and resolved**, meaning `KE_D3D11_RECORD` is removed and the losing driver
   deleted. Gate 3 is not met while both drivers still ship.
4. A field session on the reporting machine at or above the 17.24.0 numbers (125 fps flat, 8.0 ms frame,
   0.35 ms shadow encode, 0.30 ms model encode) across a full capture window, with zero `DEVICE_REMOVED` and
   the session header naming `Direct3D11Native`. The Vulkan number on the same machine (144 fps, 6.9 ms) is
   the ceiling. The stated goal is closing the last roughly 1 ms and enabling deeper attribution, so the pass
   bar is "no worse and no crashes over a week", not "faster". M2 and M3 exit criteria met.
5. `GetDeviceRemovedReason` present in the session header and the software-adapter flag present.

The flip itself is one line in `ProbeOS` plus adding the kind to `_windowCandidates`. `Direct3D11` stays
selectable by token INDEFINITELY (RO2), so a field regression is one environment variable away from an A/B on
the same build. That escape hatch is worth more than the code it costs, and it is the primary diagnostic
instrument for a defect that only reproduces on one machine that is not in CI.

Rejected (RO3): flipping the HEADLESS default (tests and tools) as soon as gates 1 to 3 are green, on the
grounds that opt-in-and-unexercised is a real risk while `main` moves. The `direct3d11-native` CI leg IS the
continuous exercise, and an early headless flip would silently reduce the incumbent's coverage during exactly
the window when both must stay green.

Before the field capture, pin the session log's build line and the capture-window stamps. A number attributed
to the wrong build is the expensive failure here, and I2 (throw rather than fall back on a missing provider)
exists specifically to make that impossible.

### Rollout record (2026-08-05)

Where the five gates stand once row 17's CI leg, its parity `[GpuFact]` and the soak counters have landed.

**Gate 3's marginals are frozen and measured.** `D3D11NativeCallBudgetTests` asserts them device-free on every
`dotnet test`, so these are the numbers gate 3 asked to be recorded here, and what a future reader compares
against:

- **Fixed head, 26 calls.** What a frame costs before any mesh or draw is counted: one `ClearState`, three for
  the framebuffer change, two clears, seven for the first pipeline bind, two for the extra width of the
  per-draw set's one full activation over its offsets-only pushes, and eleven for the tail.
- **Per distinct mesh, 4 calls.** One full activation of the seven-element model set, array-batched. Eight or
  fourteen here is the #418 fan-out returning.
- **Per draw, 2 calls.** One offsets-only push of the per-draw uniform window plus the draw itself. This is the
  shadow pass's shape thousands of times a frame, and the number the whole recording model exists to hold.
- **An offsets-only rebind is exactly one call per VISIBLE stage**, one for a vertex-only set and two for
  vertex plus fragment. Not one per element, not one per resource, and not a re-activation.

**Gate 1 is met.** All 36 goldens ran green against the shared `direct3d11` family at the 0.06 tolerance, on
the first `direct3d11-native` leg run to come back green (run 30968278343, commit d5778fc7), since the very
first run was red and its fixes (below) landed in-branch before this one. The observed worst-cell delta was
0.011 (`scene3d_splat_shadow`), the next worst 0.0057, and most scenes sat at or under 0.0006. The mechanism
behind the number: every golden compare now appends its worst-cell delta to
`goldens-evidence/golden-deltas.<family>.txt` on a pass as well as a fail, and the leg uploads that file as
`golden-deltas-direct3d11-native` on `always()`, so the observed figure comes off a green run instead of
needing one to break first.

**Gate 2 is met.** The same run's full WARP suite passed 4052, failed 0, skipped 0 on the native leg, against
the incumbent leg's 4050 passed, 0 failed, 2 skipped, on the same commit in the same run. The two incumbent
skips are the `RequiresCompletionFences` pair, which the native leg runs, and running that pair is the
absolute criterion gate 2 asked for.

**Gate 3 is not met, and what it waits on is M1 rather than a number.** The marginals above are green, but
`KE_D3D11_RECORD` still ships and both drivers with it, so the deferred-versus-immediate A/B on the #410
reporting machine is still owed. Gate 3 closes when that measurement is taken, the losing driver is deleted and
the switch is removed.

**Gate 4 waits on the field soak, and its instrument is now wired end to end.** M2's drain count and duration
and M3's two backpressure readings leave the backend through `IGpuDevice.Counters`, forwarded by
`GpuDeviceContext` and `AppWindow`, and reach a capture as sample-row channels via
`GpuTelemetryChannels.AppendTo`, which the consumer calls from its own frame sampler. The counters are
cumulative, so a window's cost is the last row minus the first regardless of sampling cadence. One number the
gate reads is deliberately NOT in that set: `OceanFftProducer.LastStallMs` is surfaced by the renderer, and M2
judges it as a consumer-side reading beside the device counters rather than folding it in.

**Gate 5 is met.** `softwareAdapter` and `deviceLossReason` both ship in the telemetry session header, from
row 16.

**The first `direct3d11-native` leg (run 30955744945) failed 113 of 4028**, from one mechanism plus a
test-assembly registration gap: the 6.2 constant-count round-up was to 16 bytes rather than 256, so every window
whose size fell strictly between two multiples of 256 was dropped silently by the runtime, and the module
initializer lived only in `KhaozEngine.Render.Tests`, so `KhaozEngine.MapEditor.Tests` carried `[GpuFact]`
without ever registering the backend it needed. Both are fixed in-branch.

#### Addendum, 2026-08-22: the default was flipped ahead of these gates

**The default flipped at 17.40.0 by DECISION on 2026-08-22, ahead of gate 3's M1 measurement and gate 4's field soak.** Section 14 of this document
opens "all five gates, all green before any flip", and that condition was overridden rather than met. This paragraph is
the record of the override, written here because a reader who finds the gate table above and the shipped
`ProbeOS` disagreeing must not have to reconstruct which one is current.

**What the flip does NOT do is close a gate.** Every gate that still carries a live instrument stays OPEN as an
issue and is still owed: Vulkan gate 3's `sync` validation job and Vulkan gate 5's windowed pass
([#510](https://github.com/APKiwiOrg/KhaozEngine/issues/510)), Metal's MM1
([#566](https://github.com/APKiwiOrg/KhaozEngine/issues/566)) and Direct3D 11's M1
([#460](https://github.com/APKiwiOrg/KhaozEngine/issues/460)). The one gate RETIRED is the streak gate MV7
([#564](https://github.com/APKiwiOrg/KhaozEngine/issues/564), closed as not planned): a gate whose criterion is
four consecutive green weekly runs cannot be read after the thing it was gating has shipped, and the weekly
evidence review ([#609](https://github.com/APKiwiOrg/KhaozEngine/issues/609)) now judges on ANY RED ON A NATIVE
LEG rather than on a streak.

**The escape hatch this document leans on now has an expiry.** `Direct3D11` through Veldrid (RO2) stays selectable by
`KE_GRAPHICS_BACKEND` for ONE release, not indefinitely, and is removed in the next one by the Veldrid removal
program (MF1, tracked from [#540](https://github.com/APKiwiOrg/KhaozEngine/issues/540)). Anywhere above that
says "indefinitely" is history rather than the current plan. A field regression found in that window is still
one environment variable away from an A/B on the same build, which is the property the gates were protecting,
and after the window it is a revert instead.


---

## 15. Work breakdown

Each row becomes one implementation issue, `kind/backlog` unless noted, `confidence/authored`, linked to #421.

| # | Scope | Regression evidence |
|---|---|---|
| 1 | Refactor `GpuDeviceContext` onto `IGpuDevice`: second constructor, disposal hook replacing the `VeldridGpuDevice` cast, capability read from the device, raw-pointer entry on `D3D11ThreadingProbe` | The constructor takes a Veldrid `GraphicsDevice` today, so no non-Veldrid device can be returned through the only creation path. `GpuDeviceLifecycleTests` stay green and the Veldrid path stays byte-identical |
| 2 | `GpuBackendProviders` registry, `KhaozEngineD3D11.Register()`, throw-on-missing-provider, native `IsBackendSupported` functional probe checking `ConstantBufferOffsetting` and `MapNoOverwriteOnDynamicConstantBuffer` | A silent fallback would let a soak session measure the incumbent and report it as the native backend |
| 3 | Append `GpuBackendKind.Direct3D11Native` with explicit ordinals and the append-only comment, selector tokens, the `GoldenBackendToken` mapping, the bake refusal, and the thirteen-site switch audit in 4.3 | `GoldenCompare` lower-cases the kind into the filename, so a new kind silently orphans 36 goldens. Three of the thirteen sites degrade silently: two drop the native leg's threading diagnostics and one asks Veldrid for a Metal device on Windows |
| 4 | Project skeleton, Windows guards, architecture rows, `OptInBackends`, README catalog row, package README, slnx, extended `GpuPublicApiTests`, the no-Veldrid-edge assertion, the internal Veldrid-free cross-compile helper in `KhaozEngine.Gpu` | `check-doc-versions.sh` fails on a packable project without a catalog row, and `ArchitectureTests` fails on an unmapped third-party package |
| 5 | The command stream, `D3D11CommandRecorder`, the generic emitter interface over engine-owned handles, the counting emitter, and BOTH drivers (deferred replay and immediate emit) behind `KE_D3D11_RECORD`. **Exit criterion: build-and-unit only**, meaning both drivers compile and pass their device-free op-encoding and replay-ordering tests. It does NOT exit on M1, which nothing renderable can measure yet | The 7.5 ms deferred-context cost (#410's first win) must not reappear, and the overlap risk in 2.1 must be measurable later, which is why both drivers are built here and neither is deleted here |
| 6 | Replay loop, redundancy caches, one `ClearState` per submit, precise unbind-and-scrub on disposal, the framebuffer-change-guarded viewport and scissor emit, the **native recording-contract test** (N recorders, interleaved, submit-order replay asserted), and the **doc task for R4** (the one-open-recording rule onto `IGpuCommandList.Begin`/`End` XML docs plus a `USING-KHAOZENGINE.md` section stating both contracts) | The seam has no `SetViewport`, so missing the implicit behaviour rasterises nothing, and an unguarded emit silently resets a live scissor (9.4). `GpuInterfaces.cs` documents neither contract today, so R4 has no written home without this |
| 7 | Formats, resource handles, eager SRV/RTV/DSV/UAV, resource layouts and the register-assignment scheme, pipelines, state objects, `DeviceLiveness` no-op disposal | All 25 `DEVICE_REMOVED` stacks in #423 surfaced inside the `D3D11TextureView` constructor during `ActivateResourceSet`. The register table test catches "compiles and every pixel is wrong" |
| 8 | Constant-buffer rings, frame segments, `MAP_WRITE_NO_OVERWRITE`, bind-time frame base, the ring-backed-buffer view invariant, the CPU arena for bulk payloads, `UpdateBuffer` routing at both levels per 6.4, the backpressure counter (M3). **Depends on the fence primitive from 13** | 22 blocking staging maps per frame at 12 to 17 ms per pass. #408's residue list. Zero renderer sites pass `GpuBufferUsage.Dynamic`, verified. A ring built against submit-receipt fences recycles a segment the GPU is still reading and corrupts a frame silently |
| 9 | Bind flush with three-state dirty tracking, slot-order flush, pipeline-switch drain, null-record guard, bound-record dedup, array-batched activation, `*SetConstantBuffers1` everywhere, the `!DriverCommandLists` workaround, plus the device-free native-call budget test | #418 (one native call per resource per stage) and the 40x shadow-encode collapse its fix produced. Both driver arms asserted |
| 10 | Draw and dispatch paths, vertex and index binding, topology, per-pipeline blend factor | The 36 goldens |
| 11 | Shader compile through the internal helper, our own FXC call, register numbering, the pinned cross-compile options plus the HLSL byte-equality test, the disk DXBC cache, the FXC-in-CI leg and the signature contiguity assertion at both validation and pipeline creation | `ShaderSources.Shadow.cs` (a holed `TEXCOORD` sequence corrupted WARP so the main passes rendered no colour) and `ShaderSources.Terrain.cs` (flat-white terrain) |
| 12 | Compute pipelines and dispatch, SRV-versus-UAV auto-unbind in both directions, RAW structured views, MSAA resolve, `GenerateMipmaps`, copies, staging `Map` and `Unmap` with row pitch, readback | `GpuInterfaces.cs` names the unbind as the D3D11 mechanism for rule 1, proven by the compute `[GpuFact]` suite on all three backends |
| 13 | **13a (fence primitive, an early prerequisite of 8):** `ID3D11Fence` via `ID3D11Device5` with the `ID3D11Query(Event)` fallback and the monotonic signal at end of replay. **13b (the seam-visible half):** `SupportsCompletionFences = true`, `IGpuFence` wiring, and a real `WaitForIdle` behind `KE_D3D11_REAL_DRAIN` with drain telemetry (M2). Split so 8 can depend on 13a without waiting on 13b | `RetireFenceGpuTests` and `Scene3DUnloadDrainTests` must RUN and pass, and the suite reports 0 failed and 0 skipped |
| 14 | Blit-model swapchain matching the incumbent exactly, present, stable framebuffer identity, queued resize applied at the present boundary | The Windows black screen after fullscreen or drag-resize, and #415's cross-thread `Monitor.Exit` from the resize path |
| 15 | Threading contract: lock-free recording, the single short submit lock, staging map scoping, creation lock when `DriverConcurrentCreates` is false | `GpuDeviceLifecycleTests` extended with a foreign-thread update case and a concurrent-resize case |
| 16 | Capability reads and the parity test (T4), **the sampler creation path: reproduce the incumbent's hardcodes (comparison null, minLod 0, maxLod max, border `TransparentBlack`, and WRAP on all three axes for the device's shared pair) and drop the two unreachable degradations (G1)**, `KE_D3D11_ADAPTER`, the software-adapter telemetry flag, `KE_D3D11_DEBUG` with the `ID3D11InfoQueue` pump, device-loss latch and `GetDeviceRemovedReason` in the session header (closes #427 for the native leg) | `cross-platform-gpu.yml` relies on accidental WARP fallback, so a runner image change silently reshapes the golden leg. The sampler hardcodes are golden-visible and G1 had no owner before this |
| 17 | The `direct3d11-native` CI matrix leg, **the T3 WARP native-call parity `[GpuFact]` that guards the device-free harness against drift**, the soak build, the five rollout gates, and the `ProbeOS` flip | #423 records the push-triggered D3D11 golden gate degraded from 2026-07-30 until 17.26.0 without anyone noticing. T3 only has meaning once a CI leg exists to run it on, which is why it is homed here and not with the budget test |

**Order.**

- **1 to 4 are prerequisites** and land first.
- **5 builds both drivers** and exits on build-and-unit. It does not block on a measurement and nothing waits
  on a measurement here.
- **13a (the fence primitive) is pulled early**, because 8 depends on it. This is the one dependency edge the
  first version of this spec dropped: the ring's segment recycling reads a completion fence, so a ring built
  before real fences exist is a silent corruption.
- **6, 7, 9, 10, 11 and 14 are the minimal renderable path** and follow 5. Once they and 8 land, the backend
  renders a frame with real per-frame memory. **M1 is taken here**, and it gates only the removal of
  `KE_D3D11_RECORD` and the deletion of the losing driver.
- **8 follows 13a**, and parallelises with the renderable-path issues otherwise.
- **12, 13b, 15 and 16 parallelise** after their own prerequisites.
- **17 is last.**

**KESIZE.** The fork's `D3D11CommandList.cs` is 1751 lines against an 800-line cap, which is a warning about
what happens without a file plan. The recorder, the stream encoding, the replay loop, the bind flush, the
emitter, the ring allocator and the resource factory are seven separate types by construction, so the ratchet
is satisfied by design rather than by a late split. No `.filesize-baseline` edit should be needed, and if one
is, that is the user's call.

---

## 16. Relationship to #420 Phase 3 (the Vulkan and Metal replacements)

**What this design makes easier.**

- `GpuDeviceContext` is inverted onto `IGpuDevice` and the provider registry exists, so backend three and
  four are registrations rather than another inversion.
- The opt-in package shape, the architecture guard rows, the golden-family mapping, the capability-parity
  test pattern and the CI matrix leg are all templates the next backend copies.
- The enum append precedent is established, including the thirteen-site audit that appending actually costs.
- The recorder, the three-state dirty tracking, the flush schedule and the emitter interface sit ABOVE the
  emitter, so a Vulkan or Metal backend can supply only an emitter. **Qualified, because the unqualified
  claim is too strong.** "Reuse" means physically MOVING that code out of a package called
  `KhaozEngine.Gpu.D3D11` into a shared home, which is a Phase 3 refactor and not a free consequence of this
  design. And not all of it is portable as written: R5's pipeline-switch drain under the OUTGOING layout is
  D3D11-shaped, because it exists so register numbering is computed under the right layout, and Vulkan's
  descriptor-set model has no equivalent need. What genuinely generalises is the three-state dirty tracking,
  the record-then-flush-at-draw schedule and the emitter seam. The rest is a candidate, not a given.
  (Corrected 2026-08-05. Two things moved under this bullet. The program renumbered: phase 3 is Vulkan ALONE
  and Metal is phase 4, so every "Phase 3" in this section means the Vulkan-and-Metal span it was written as.
  And the refactor it anticipates was DECLINED rather than scheduled: `VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05`
  section 2.2 and its V-P4 extract nothing, on the rule of three and on the argument that D3D11 is the likely
  OUTLIER of the eventual three, so an abstraction extracted from two backends today would be shaped by the
  outlier and then asked to fit Metal. Even the three-state dirty tracking named here as genuinely general did
  not survive: Vulkan collapses it to two, because a descriptor bind is one call whether one offset moved or
  every image changed. What IS shared at two implementations is the uniform ring's SEMANTIC TESTS. The
  extraction is filed as that document's VF1, triggered by phase 4 landing.)
  (**Corrected again 2026-08-11, and this time with an answer rather than a deferral.** Phase 4 landed, so the
  extraction this bullet anticipated and the 2026-08-05 note deferred is DECIDED:
  `METAL-NATIVE-BACKEND-DESIGN-2026-08-09` section 2.8 names five things that move into
  `KhaozEngine.Gpu/Internal/` and four that do not, each refusal in writing. Moving: the `DeviceLiveness` latch,
  the counter accumulators, the diagnostic rate limiter, the shader-cache key and file discipline, and the
  completion timeline's bookkeeping. Staying per backend: the ring's CODE, the record-then-flush schedule, the
  dirty MODEL and the generic emitter interface. So this bullet's own "what genuinely generalises" list was
  wrong in BOTH directions at two implementations, which is the whole argument for waiting: the dirty tracking
  it named as general did not survive Vulkan, the schedule it named as general did not survive Metal, and what
  did generalise is the bookkeeping nobody listed. The extraction lands after phase 4's rollout gate 3 rather
  than with it, so a golden failure has one candidate cause instead of two.)
  (**Corrected in place at phase 4's row 18, 2026-08-11: the list above is what section 2.8 RULED, and executing
  it moved it again.** Two things extracted, one of them only in part, and three were refused. The
  `DeviceLiveness` latch moved whole and absorbed a fourth copy nobody had counted. The counters moved at the
  CARRIERS and were refused at the accumulation SITES, where every backend's counting rule differs for an argued
  reason. The rate limiter, the shader-cache key and the timeline bookkeeping were all refused, the first two
  because the third implementation this list assumed does not exist: Metal has no rate limiter and no disk shader
  cache, and cannot have either. Which makes the bullet above right one more time and for one more reason. Its
  "what genuinely generalises" list was wrong in both directions at two implementations, and the list written at
  THREE was still wrong in both directions until it was executed. See the row 18 addendum in section 2.8 of
  `METAL-NATIVE-BACKEND-DESIGN-2026-08-09`.)
- The `Veldrid.SPIRV` edge is confined to `KhaozEngine.Gpu` behind one internal helper (P2), so the
  "no Veldrid in the graph" endpoint is one package to change, not three.
- Making `WaitForIdle` real on D3D11 means all three backends will honour rule 2 identically, so the
  automatic-hazard capability (F1) becomes a single seam addition rather than a per-backend special case.
- The seam's own model stays honest: command lists are independent and execute in submit order on every
  backend. Draft A's join semantics would have made D3D11 permanently different from anything Phase 3 could
  reproduce, and that was a decisive argument against it (2.1).

**What this design makes harder, stated plainly.**

- The CPU op stream is a D3D11-SPECIFIC adapter. Vulkan and Metal have real deferred command buffers, so
  their emitters emit at record time straight into the native buffer and an op stream there would be pure
  overhead. The architecture must therefore treat the op stream as ONE DRIVER of the emitter and not as a
  mandatory layer, which is the same property R2 requires for the M1 fallback. Getting this seam wrong now
  costs a refactor in Phase 3, so it is called out in implementation issue 5.
- Two D3D11 backends coexist indefinitely (RO2). #424's seven nested sites, #428's guardrail and #429's
  pre-record phase all stay live work until the Veldrid D3D11 leg is removed, and only Phase 3 can remove it.
  F8 tracks retiring them together. (Corrected 2026-08-05. Phase 3 as it shipped is Vulkan alone and it removes
  no Veldrid path at all: `Veldrid` and `Veldrid.SPIRV` both stay in the graph after it, for Metal and for the
  shader front end, and the phase 3 design's own VF11 retires the Veldrid VULKAN leg only once phase 4 removes
  Veldrid entirely. So this coexistence outlives phase 3 and F8 waits on phase 4. RO2's wording in section 1
  is unchanged in substance, since the token lives until the Veldrid path goes, and it reads as phase 4 under
  the new numbering.)
- The shader-signature workarounds stay load-bearing while SPIRV-Cross does. They come out with SPIRV-Cross
  (F2), not before, and removing one early would corrupt the Veldrid leg.

---

## 17. Rejected from both drafts

| Rejected | Why |
|---|---|
| **The flip-model swapchain in v1** (both drafts proposed it) | Zero automated coverage anywhere in the net, validated only by a human looking at a window, and it would sit inside the soak that must isolate the recording and memory changes. Both authors argued against their own decision here and neither took the edit. Sequenced as F4 |
| **Frozen absolute native-call counts as the gate** (A specified exact figures, B specified totals plus marginals, neither decided) | A's figures were derived from reading layouts, not from running anything, and A's own counterargument says they should have been upper bounds. The gate is invariants, marginals and trace identity (T2) |
| **A's `NativeVsVeldridD3D11GoldenDivergenceTests`** (render both implementations in one process, compare at a tighter tolerance) | Redundant with the shared golden family, which already holds the native backend to the incumbent's committed reference on the same rasterizer at 0.06. Replaced by recording the observed worst-cell delta as rollout gate 1, which buys the same tightness with no second Windows leg |
| **A's D8 shared device-state cache with joining nested `Begin`** | Invents a third command-list semantics that neither the seam nor any Phase 3 backend can reproduce, is a claim about what does not happen rather than a measurement, and makes the same nested call legal on native and an exception on Veldrid during the exact window the rollout uses for A/B (2.1, 2.3) |
| **A's D4 shared `GpuBackendKind`** | Its central argument (a separate member forces a golden-token decision) is refuted by B's `GoldenBackendToken` mapping, leaving three costs and no benefit (2.2) |
| **A's D14 blanket rejection of rings** | A is right that an identity-changing ring breaks `CreateResourceSet`'s pinning, and it reached that from a fair reading of "persistent-mapped ring buffers" in the issue text. It simply does not reach B's shape, which keeps `IGpuBuffer` identity and applies the frame base at bind, so the pinning holds. The rejection is scoped, not wrong (U3, 6.3) |
| **A's bit-for-bit reproduction of the incumbent's two silent sampler degradations** | Unreachable on the feature levels this backend requires. Reproducing dead code. The reachable HARDCODES are kept, and both halves are homed in implementation issue 16 |
| **Porting `D3D11ResourceCache`** | The D3D11 runtime already returns an existing object for an identical state description (X2) |
| **B's claim that only the deferred model makes the native-call test device-free** | False. Both models reach it through the same emitter seam both drafts independently specified, and both need the same fake resource factory. The model was decided on other grounds (2.1) |
| **B's WB4 gate as written (stub-emitter indirection measurement)** | Cannot see the driver-overlap risk, which is the actual mechanism by which the deferred model could be slower. Replaced by M1's end-to-end A/B on a real scene |
| **B's early headless default flip** | Would silently reduce the incumbent's coverage during the window when both legs must stay green. The `direct3d11-native` CI leg is the continuous exercise (RO3) |
| **B's direct `Veldrid.SPIRV` reference from `Gpu.D3D11`** | Blesses a Veldrid package inside a backend whose premise is being Veldrid-free, and scatters the eventual SPIRV-Cross replacement across two packages instead of one (P2) |
| **D3D11 deferred contexts** | #410's first win was turning them off: encode 20.32 ms to 12.75 ms, doubled again where the runtime emulates driver command lists |
| **DXC** | Emits DXIL, which D3D11 cannot consume. FXC is the only compiler for SM 5.0 DXBC |
| **Hand-authored HLSL** | Roughly 30 graphics programs and 2 compute kernels, built from 52 GLSL source constants, would need a second dialect maintained in parallel with the GLSL that Metal and Vulkan still consume |
| **SPIRV-Cross direct bindings now** | Changes the emitted HLSL, which changes register numbering AND drop-unused behaviour, putting the goldens and both documented WARP corruption workarounds in play at once (F2) |
| **`[ModuleInitializer]` self-registration in the consumer path** | The CLR loads an assembly lazily on first type reference, so a package reference alone does not guarantee it runs. Silent and machine-dependent |
| **A post-record peephole optimiser** | Makes the native-call budget depend on an optimiser rather than on the recording, which is the wrong thing to freeze in a test |
| **Adding `SetViewport` to the seam** | 48 `SetFramebuffer` sites and zero viewport sites. New public API with no consumer. A reasonable Phase 3 addition |
| **Removing the shader signature sink workarounds** | The native backend uses the same SPIRV-Cross and the same FXC, so it has the same gap intolerance, and the Veldrid leg ships alongside indefinitely |
| **Fighting the driver's threading optimizations** | #417 measured 109 versus 125 fps with the flag on. With lean submission the driver threading helps |

---

## 18. Follow-ups this design knowingly leaves open

Filed as issues when this spec lands, not discovered later.

- **F1.** A seam capability for automatic compute hazards, so the ocean stops paying a Vulkan-shaped drain on
  a hazard-tracked API (10.1). Needs a seam change and a renderer change, both outside #421's scope. Becomes a
  blocker if M2's exit criterion fails.
- **F2.** Drop the managed `Veldrid.SPIRV` dependency with an engine-owned P/Invoke shim over
  `libveldrid-spirv`, seated in the internal helper P2 creates. Phase 3.
  (**Answered 2026-08-11, and the answer is "not in phase 3 or phase 4 either", with a reason that only became
  visible at phase 4.** `METAL-NATIVE-BACKEND-DESIGN-2026-08-09` section 12.2 declines it by name and section
  2.2 is why: `libveldrid-spirv` exports three C entry points and NONE of them carries a binding table, so a
  shim over that library cannot reach `add_msl_resource_binding` and cannot pin Metal's binding indices. The
  shim was never the endpoint, a DIRECT SPIRV-Cross binding is, and that lands in the closing act (section 19
  of the same document) where it deletes the MSL argument parse and the SPIR-V decoration walk phase 4 shipped
  instead. What phase 3 DID pay for is the seat: `SpirvCrossCompile`'s front-end and back-end halves are split,
  so the eventual replacement is one half of one file rather than a change scattered across three packages.)
- **F3.** Multi-threaded command recording, enabled by this design and not shipped (W5).
- **F4.** Flip-model swapchain, `ALLOW_TEARING`, the RTV-unbound-at-present obligation and a waitable
  frame-latency swapchain, with #380's pacing measurement as its own gate (9.1, M5).
- **F5.** #425's retire-pool bound written explicitly against the fence path rather than inherited from the
  ring's backpressure, so it is a designed bound rather than an emergent one.
- **F6.** Record that the Veldrid D3D11 path's `WaitForIdle` is a no-op, so the divergence is written down for
  as long as both implementations ship. A test passing on both for different reasons is not evidence about
  either (10.4).
- **F7.** Correct the `Newtonsoft.Json` attribution at THREE sites, not two: `Directory.Packages.props`,
  `KhaozEngine.Gpu.csproj`, and `ArchitectureTests.cs:56-57`, whose comment reads "Pinned alongside
  Veldrid.SPIRV shader reflection, stays inside Gpu". All three name `Veldrid.SPIRV` where the nuspec chain
  says `Veldrid -> NativeLibraryLoader -> Microsoft.Extensions.DependencyModel` (section 3).
- **F8.** Retire #428's second-recorder guardrail, #429's pre-record phase and #424's site list TOGETHER, and
  only once the Veldrid D3D11 leg is removed in Phase 3. Not before (2.3).
- **F9.** #407 (the per-cascade bone palette re-upload) is defanged by the ring and not resolved. It stays
  open (6.2).
