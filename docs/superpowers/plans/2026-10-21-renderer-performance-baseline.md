# Renderer Performance Baseline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish a repeatable, hardware-identified whole-renderer baseline that explains each pass's cost and its distance from a stated hardware reference.

**Architecture:** Build an isolated, opt-in performance project over the existing production frame loop and public engine fixtures. Reuse the equipment program's GPU timestamp and frame-completion infrastructure after a reviewed dependency reconciliation, retain minimally instrumented frame totals, and collect detailed pass traces in separate blocks. Keep measurement, compiler diagnostics, work models and comparison arithmetic separate so an unavailable counter or an approximate model cannot become a measured result.

**Tech Stack:** .NET 10, native Vulkan, Direct3D 11 and Metal backends, xUnit, strict JSON, the paid hosted NVIDIA T4 workflow, and ordinary hosted correctness jobs.

**Spec:** [KhaozEngine #1526](https://github.com/APKiwiOrg/KhaozEngine/issues/1526) and the planning brief at `.superpowers/sdd/2026-10-21-renderer-performance-baseline/plan-brief.md`. This document proposes the measurement contract for owner review. It authorizes no implementation or run.

## Global Constraints

- Planning was read-only except this plan and its ignored planning report. No build, test, render, benchmark or CI dispatch was performed.
- Investigated main snapshot: `6a8831105a7daf06f064afe9701449f57d233476`. Investigated equipment snapshot: `b8165d84a37123120d887b12425325e0d3841171` on `feature/equipment-coverage`. Paths below refer to main unless explicitly marked equipment.
- Do not modify the equipment worktree. Future dependency reconciliation happens in the implementation worktree with its orchestrator's agreement.
- Every measurement or load run requires explicit owner approval naming the workload, leg, counts, duration and spending cap. A plan approval is not run approval. No local performance campaign, self-hosted runner or automatic rerun.
- Public deterministic geometry only. No consumer art, private maps, production credentials, game clients or consumer package dependency.
- Release configuration, native hardware identity, exact source and manifest hashes, fixed simulation time, ordinary GC and bounded preallocated sample storage.
- Software Vulkan and WARP may prove correctness. They never prove a GPU floor or a T4 performance result. Missing or invalid values are not zero.
- Preserve existing coverage gates, exactness contracts and authorization schemas. This baseline does not optimize shaders or change quality settings to pass a budget.
- Documentation alone does not bump an engine version. Future package changes follow current engine version, changelog, API documentation and verification rules. No release is implicit.

## Review Focus

1. The first marker must precede the host's initial clear and the last must follow native End finalization. Scene-only brackets are not whole-frame GPU time. Tasks 2 and 8 test both boundaries.
2. Unsupported counters, a different adapter, disjoint timestamps or a partial report must stay unavailable or invalid. Tasks 1, 2 and 9 to 11 test those refusals.
3. Detailed markers can change synchronization and encoder boundaries. Tasks 4, 5 and 8 prove disabled tracing preserves the command sequence and keep detailed results separate from total-only results.
4. Fixed intended counts can hide culling, a fallback or a stale cached pass. Tasks 6 and 7 pin actual routes, visible counts, pass occurrences and deterministic state.
5. Clock ramp, different drivers and uncertain traffic models can produce persuasive false comparisons. Tasks 8, 12 and 14 retain drift evidence, reject incompatible pairs and refuse invalid floor arithmetic.

## 1. Existing infrastructure and reuse boundary

| Source | What it measures or supplies | Limit | Disposition |
| --- | --- | --- | --- |
| `.github/workflows/gpu-hosted-bench.yml` | Paid `gpu-t4-linux` and `gpu-t4-windows`, Linux Vulkan, Windows Vulkan and Windows D3D11, explicit tested `ref`, NVIDIA ICD restriction, adapter checks, logs | Ordinary mode accepts a free-form test filter and is not a baseline protocol. Existing coverage mode is hardcoded to coverage paths, schemas and one selected leg | Extend with a mutually exclusive renderer mode. Preserve ordinary and coverage behavior |
| Same workflow, coverage inputs and steps | Authorization commit parent/diff checks, exact workload and manifest hashes, attempt 1, pinned PowerShell 7.6.7 installation with SHA-256 verification, fixed entrypoint, exact one-test TRX and final report checks | Main contains this bootstrap but not the coverage test project or GPU layer. Selecting it on bare main is not a working renderer harness | Reuse this execution and evidence pattern, with renderer-specific inputs and paths |
| `.github/workflows/stress-test.yml` | Caller of the pinned org hosted stress workflow, bounded iterations/load, optional support-cost artifacts | Standard hosted CPUs, usually software graphics, no T4. Repetition and synthetic contention change the workload | Use ordinary hosted correctness for fake protocol tests. If a separately approved CPU-only diagnostic is needed, use `load=0` and label the host. Never use it for GPU evidence |
| `.github/workflows/cross-platform-gpu.yml`, `docs/CROSS-PLATFORM.md` | Backend correctness, golden families, shader validation and existing native device diagnostics | WARP/lavapipe are software evidence. Hosted Metal is real Apple hardware but does not establish usable timestamp counters | Retain correctness gates independently of timing. Do not turn ordinary GPU CI into a benchmark campaign |
| `KhaozEngine.Diagnostics/FrameStats.cs` | Rolling supplied frame delta average/min/max, FPS and managed live heap estimate | Not a CPU execution bracket or a GPU timer. `Sample` scans its rolling queue. Live heap is not allocation volume or VRAM | Keep for overlays. Do not use it as the benchmark sampler |
| `KhaozEngine.Diagnostics/PassTimings.cs`, `KhaozEngine.Render3D/Scene3DPassTimings.cs` | Rolling CPU encode values and last-frame shadow/model/transparents/water-stall/post brackets | No GPU timing. Several passes share a bucket. Point shadows and work before those brackets are not a complete CPU frame. Present is outside Scene3D | Reuse semantics and old tests as compatibility checks, add a separate complete phase trace |
| `KhaozEngine.Primitives/RenderFrameStats.cs` | Draws, instances, estimated geometry triangles, four partitioned buffer-upload buckets, sprite quads/flushes/texture switches | Not every post draw, dispatch, uniform/texture upload, copy or native state change is represented. Triangle counts omit effect/post geometry | Retain as an independent cross-check, count all seam commands in the new recorder |
| `KhaozEngine.Render3D/WaterFrameStats.cs`, `ShadowPassDiagnostics.cs`, `Scene3D.Foliage.cs` | Ocean priming stall and clipmap rebuilds, actual shadow scheduling, foliage counts | Water values describe the last water draw and may be stale on an empty frame. These are subsystem diagnostics, not frame totals | Attach only to the matching frame and mark inactive subsystems explicitly |
| `KhaozEngine.Gpu/GpuDeviceCounters.cs`, `GpuDeviceDiagnostics.cs` | Cumulative device frame/wait/retire/acquire counters, nullable software-adapter and device-loss facts | No universal per-pass counters, resource byte ledger or physical GPU residency | Difference counters by frame, retain null semantics, add optional memory diagnostics |
| `KhaozEngine.Render.Tests/Gpu/FrameUploadAttributionGpuTests.cs`, `MetalRecordCostGpuTests.cs`, `InstanceGroupingCostTests.cs` | Stream attribution, record-path and grouping diagnostics | Feature-specific counts or CPU wall time. Some rows drain per frame. They do not measure per-pass GPU execution | Reuse fixtures and structural assertions, not their timing loops or historical values |
| `ShadowRerecordBenchGpuTests.cs`, `ShadowCascadeCullPerfGpuTests.cs`, `OceanFftPingPongPerfGpuTests.cs`, `TemporalResolveCostPerfGpuTests.cs`, `TemporalOutlineCostPerfGpuTests.cs`, `InteriorCutCostPerfGpuTests.cs` under `KhaozEngine.Render.Tests/Gpu/` | Existing shadow, ocean, temporal and interior controls using fixed rounds and CPU wall/encode timings | Different resolutions, pacing, capture/readback and warm-ups. A synchronized wall number is not an isolated GPU pass | Reuse scene construction and test ideas. Do not splice their numbers into this baseline |
| `KhaozEngine.Benchmarks/Program.cs`, `KhaozEngine.Benchmarks.csproj` | Server/ECS, replication, catalog, journal, items and JPEG benchmarks | No whole-renderer native GPU baseline | Leave these modes intact. Rendering needs its own outside-solution opt-in project |
| `KhaozEngine.Render3D/Render3DPreview.cs` | Convenient retained offscreen target and scene. `Capture` records/submits, `ReadbackRgba` explicitly reads pixels | Its sequence is not the shared window frame host. Pixel readback adds synchronization | Reuse pure fixture setup. Use the production host sequence for measurements, read one validation image outside timing |

### Equipment infrastructure, not on the investigated main

All paths in this subsection are at the equipment snapshot above.

- `KhaozEngine.Gpu/GpuTimestamps.cs`, `IGpuCommandListTimestampEnd.cs`, and `Internal/GpuTimestampResults.cs` define fixed pools, Pending/Ready/Invalid, elapsed ticks, periods, checked wrap handling and the completed-End tail. `IGpuDevice`/factory/command-list changes advertise and create these resources.
- Backend `Internal/*Timestamp*.cs` files implement Vulkan valid-bit/period normalization, D3D11 disjoint-query frequency and nonflushing polling, and Metal's probed counter sampling. Vulkan's `VulkanTimestampApi.Write` uses `AllCommandsBit`. Metal markers may close encoders. A capability bit or selected backend alone is not proof that a sample is valid.
- `KhaozEngine.Gpu/GpuFrameSubmission.cs` and its completion-consumer/participant contracts, used by `KhaozEngine.Windowing/FramePhases.cs`, retain the exact submission fence through outcome consumption. This ownership is as important as the queries themselves.
- `KhaozEngine.Render.Tests/Gpu/CoveragePerf/CoveragePerfTimestampRing.cs` owns three retained pools and resolves after the existing batch drain. A full ring fails the block rather than dropping a sample. Generalize its lifecycle for variable pass layouts, preserve the coverage adapter and its tests.
- `CoveragePerfManifest.cs`, `CoveragePerfAuthorizationCommit.cs`, `CoveragePerfDriver.cs`, `CoveragePerfSession.cs`, `CoveragePerfValidity.cs`, `CoveragePerfExecutionIdentity.cs`, `CoveragePerfReport.cs`, `CoveragePerfReportJson.cs` and `CoveragePerfPublication.cs` provide strict manifests, deadlines, raw samples, identity, partial/final publication and invalidity patterns. Reuse those mechanisms and tests. The fixed C0/C1/G1/G2/G3 presets, exact actor/pose pairs and percentage gates stay coverage-owned.
- `CoveragePerfNativeSession.cs` and `CoveragePerfDevice.cs` show how to drive actual `FramePhases`, separate CPU preparation from submit and harness drain, count effective work and retain outcomes. Its scene-entry first marker is not sufficient for this plan's initial-clear boundary. Its aggregate stops at Present entry, which is not the complete CPU wall interval proposed here.
- `CoveragePerfTypicalScene.cs` is a public 1280x720 retained actor/equipment fixture with deterministic poses, route fingerprints and actual pass counts. Reuse its construction/fingerprint techniques and geometry helpers where suitable, not its coverage workload as the whole renderer.
- `CoverageDispatchProfiler.cs` and `CoverageDispatchProfileGpuTests.cs` provide a 12-warm/9-observed diagnostic of 36 dispatches at 1 and 32 actors. They explicitly disclose marker perturbation. The ledger at `.superpowers/sdd/2026-10-20-equipment-coverage-performance-harness/` records the T4 first-block clock-ramp problem in `progress.md` and the original static inventory in `gpu-profile-report.md`. The supplied approximately 8.2 ms coverage-off frame is context from that different scene, not a baseline measured here.

### Backend gaps

| Backend | GPU timing after reconciled Part A | Compiler statistics | Remaining baseline work |
| --- | --- | --- | --- |
| Vulkan on hosted T4 | Probed queue timestamp bits/period, fixed pools and retained completion | No inspected main implementation of executable-statistic capture | Query extension and feature availability, enable diagnostic capture at device/pipeline creation, enumerate actual returned statistics, add scopes and memory evidence |
| D3D11 on hosted T4 | Timestamp/disjoint queries and completed-End support | No inspected hardware register/spill/occupancy interface | Keep effective immediate/deferred recording mode in identity. Add shader reflection and optional pipeline invocation queries in diagnostic mode. Hardware occupancy may remain unavailable |
| Metal on hosted macOS | Hosted runners are not an established timestamp-capable route. A local physical device can have usable counter sets | Optional counters differ by device. There is no promised portable register/spill/occupancy report | Probe sets, sampling points and allocation success. Without usable timestamps produce CPU/correctness evidence only. An owner-approved physical Apple GPU route is a later separate baseline, not a substitute T4 result |
| Lavapipe or WARP | Query availability can exist on software | Compiler/software statistics can exist | Correctness only. Refuse hardware-performance admission even if timestamps resolve |

### Reuse decision

Scores are 1 to 10, higher is better, equally weighted.

| Approach | Timing honesty | Maintainability | Independence from coverage completion | Total |
| --- | ---: | ---: | ---: | ---: |
| Main's Stopwatch/capture loops only | 2 | 8 | 10 | 20 |
| Reimplement queries and fence ownership from main | 8 | 3 | 9 | 20 |
| Reconcile the accepted GPU layer and shared ring, new renderer protocol | 10 | 9 | 7 | 26 |
| Wait for and couple the full coverage feature and its fixed driver | 8 | 6 | 2 | 16 |

Recommend the third option. It supplies native timing and already-reviewed ownership without importing coverage arithmetic into renderer results. Task 0 identifies the accepted dependency commits and either consumes them on main or proposes a separate reviewed infrastructure extraction to the owning orchestrator. Do not cherry-pick the whole feature or overwrite work in flight. If that infrastructure cannot be reconciled, pause native timing implementation and return the concrete dependency choice. Planning can finish now.

## 2. Fixed public scenes

The initial suite contains five scenes, not a quality-by-resolution Cartesian product. Non-empty scenes use `renderer-full-native-1080/1`: 1920x1080 display and internal targets, TAA Native, one sample, GPU skinning, frustum culling, HDR, bloom and physical atmosphere enabled, quantization/FXAA/debug visualization disabled. Outline and distortion are enabled only in S4. Freeze the complete resolved settings, camera matrices, formats, buffer sizes, seeds and geometry digests in checked-in JSON. No inherited mutable default is part of the contract.

All animation, wind, sun and wave state derives from integer frame ordinals and `dt=1/60`, never elapsed wall time. Use a 256-frame periodic camera/pose sequence, including sun phase, actor palettes and UI offsets. Measured camera/pose indices are 0 to 255 and warm-up uses the immediately preceding 192 indices modulo 256. A separate absolute block ordinal advances from 64 through 511 without wrapping, supplying EffectTimeSeconds to ocean FFT, wind, foliage motion history, particles, decals, beams and distortion. Each fresh block resets histories and both sequences before warming. Freeze both the periodic inputs and the absolute effect times in the sequence identity. Record cached versus recomputed passes. A moving camera/sun path must be pinned, not randomized per iteration.

| ID | Fixed scene contents | Intended load and reuse |
| --- | --- | --- |
| S0 `empty/1` | No terrain, actors, lights, water, effects or UI. Fixed clear and final presentation blit, AA/HDR/bloom/atmosphere off | Host, submit, timestamp and empty-pass control. No claim that subtracting it removes every other scene's overhead |
| S1 `village/1` | 64 distinct terrain chunks, each 32x32 quads. 24 buildings, each six box components. 1,024 props over four meshes. One directional light, four 2048 cascades, no points, actors, foliage or water | Terrain, unique draw runs, instancing, culling, static cache reuse and a small camera/sun path. Reuse `StreamedWorldSceneContent`'s distinct-handle and seeded placement patterns and `InteriorCutCostScene`'s village layout approach |
| S2 `outdoors/1` | 16 terrain chunks at the same topology, 64 retained native foliage patches with 64 instances each, one analytic river plane and one FFT ocean plane. Ocean has two 256x256 cascades. Clipmaps use 0.5 metre cells with exactly two named snap events per 256-frame period, outward at index 33 and return at index 224, each rebuilding both planes. One directional light with four 2048 cascades, two unshadowed points, no actors/UI | Displaced foliage and shadows, alpha cutout overdraw, FFT column/next-row work, clipmap movement and water pixels. Reuse pure shrub/patch geometry from `FoliageShadowGpuScene` and sea-state construction from `OceanFocusScene`, without their device/capture wrappers |
| S3 `crowd/1` | 16 terrain chunks, 128 props, 32 distinct animated actors in an 8x4 grid. Each actor has one 13,640-vertex tube mesh with 48 bones and stable motion identity. One directional light with four 2048 cascades. One static and one dynamic point light, 256 face resolution, two admitted rows, one dynamic update per frame and one static rebuild budget | GPU skinning and motion, repeated main/cascade/point vertex work, cached static base plus skinned transient shadows. Reuse `StreamedWorldSceneContent`'s `BuildTube` dimensions and `SkinnedPointShadowScene`'s current-pose policy. Coverage is off. Palettes are already shared across passes in main |
| S4 `effects-ui/1` | 16 terrain chunks, 64 props, four S3-style skinned actors, one directional light with four 2048 cascades, two unshadowed points. 32 decals, 256 particles, 64 textured billboards, eight beams, eight trails, eight overlay meshes, four silhouettes and four outlined actors. 64 distortion sprites. UI has 1,024 sprite quads and 2,048 glyph quads over four textures and 16 fixed clip regions | Transparency, distortion, outline masks, temporal outline/resolve, bloom/post and mixed text/UI batching. Use public primitives, generated in-memory color/alpha textures and an existing repository redistributable test font. Freeze the font digest. No screenshots or consumer icons as source content |

For S1/S2, terrain uses real splat/tile-ground material paths, not only flat colored boxes. Scene recipe files pin component vertices/indices, materials, texture sizes and placements. `StreamedWorldSceneContent`'s existing 447 chunks, 447 HLOD meshes and 3,000 props use deliberately light primitives. Retain it as a separate compatibility/count fixture, do not call it triangle-representative terrain.

Freeze actual expected visible/culled counts and the pass schedule from deterministic construction and correctness assertions before measuring. Require all 32 S3 actors to reach the main pass and the declared light rows to be admitted. For terrain and foliage retain intended, submitted and visible counts separately. Warm-up establishes valid TAA history, initialized ocean state and static shadow cache. Exactly two named clipmap snap events occur per period, an outward crossing and its return. A periodic camera returns to its starting state, so a single state-changing snap cannot close its period. Cold construction rebuilds two planes at warm-up index 64, subsequent warm-up motion rebuilds two at index 224, and measurement rebuilds four across indices 33 and 224. Cold FFT priming and target construction are separate startup records, with no FFT re-prime, preparer submission or wait in any measured frame.

Keep S0 through S4 at one resolution initially. Optional 2560x1440, 1/64/128-actor scales, CPU skinning, MSAA, shadow-off variants and private consumer scenes require new manifests and approval. They are follow-up investigations after this baseline identifies a bottleneck.

## 3. Metrics and pass coverage

### Frame and CPU intervals

- `cpu.frameWall`: production host entry before BeginSubmission through host return after submit, headless Present forwarding and device frame-boundary work. This is wall time, including actual waits, not CPU utilization.
- Non-overlapping CPU children cover begin/acquire, pose/queue, PrepareFrame, grouping/culling/visibility, upload packing, each pass encode, command-list End, pre-submit, Submit, Present forwarding/end-frame, and remaining host work. Nested pass details carry their parent and never double-count the aggregate. CPU work between existing Scene3D timing brackets must have an owner.
- Record `batch.wall`, completion drain and result harvest separately. Publish synchronized batch throughput as its own wall/frame metric. Do not relabel it as GPU time or add it to CPU encode time. Headless results cannot measure desktop swapchain/vsync/display latency.
- Startup records include device/pipeline creation, fixture upload, FFT priming, first TAA history and cache construction. They do not enter steady-state summaries.

### GPU boundaries

Use two layouts: `total-only` has exactly the outer timestamp pair, and `per-pass` has the same pair plus explicit scopes. The first marker is after native Begin but before the host's first framebuffer bind/clear or upload. The final index is armed through `IGpuCommandListTimestampEnd`, after scene, UI, copies and native End finalization. Starting at `Scene3D.RenderInternal` would miss earlier GPU work and is forbidden for the whole-frame label.

The retained frame submission is the timing fence. Three frames may be in flight. Drain once per group of at most three, resolve and consume before reusing a pool or fence. Use separate pools for any prepare-phase submission. Expected cold prime work is reported independently. An unexpected steady-state extra production submission or wait invalidates the steady row, with its partial trace preserved.

Detailed scopes are execution intervals under this instrumentation, not shader-alone times. Vulkan's all-command markers can serialize work and Metal may split encoders. Report per-pass-layout whole-frame time, total-only whole-frame time and their absolute/relative difference. Flag a perturbation above 5 percent or 0.10 ms, whichever is larger. Preserve the detailed data as diagnostic, do not subtract a guessed marker tax to invent uninstrumented pass times.

### Required pass registry

Use stable IDs with occurrence keys for cascade, light/row/face and outline group. Foliage records patch counts inside its pass instead of placing a timestamp pair around every draw. Every row has `Executed`, `NotExecuted`, `Unsupported`, `Invalid` or `BelowResolution`, never an absent row silently treated as free. Record these scopes at their owning renderer, including paths outside the five default scenes:

| Group | Required scopes |
| --- | --- |
| Host/preparation | Initial clear, preparer submissions and FFT prime, motion-target clear, frame-tail finalization/unattributed work |
| Scene begin | CPU-only `device.scene.begin` owns the existing pending-layout and retirement operations in `Scene3D.Begin` before command-list begin/acquire. It includes any retirement fallback wait without a GPU marker pair |
| Atmosphere/background | Transmittance LUT, multiple-scattering LUT, sky-view LUT, sky draw and starfield draw. Cached LUTs record a skipped occurrence |
| Directional shadows | Each cascade and rigid opaque/cutout/dissolve, skinned and displaced-foliage work under it. Keep parent and leaf totals distinct |
| Point shadows | Static rebuild, dynamic base and transient row, each face 0 to 5, rigid/foliage/skinned children and atlas copy/clear work |
| Opaque | Rigid model, splat terrain, tile ground, native foliage, GPU-skinned main and CPU-skinned fallback. Motion written in the same MRT draw is tagged fused, not assigned an invented separate GPU time |
| Depth/copies | Blob-shadow depth resolve, depth/normal resolve, color resolve, opaque temporal copy, temporal depth store and transition snapshot copy |
| Transparent/effects | Textured billboards, beams, trails, overlay meshes, silhouettes, blob and ground decals, water surface, particles, depth-tested lines and distortion field |
| Ocean | Current column FFT, next row FFT and every mip reduction occurrence. Separate these from the water surface draw |
| Water device uploads | CPU-only `device.water.geometry` owns buffer setup and initial flat/focused index uploads before ocean recording. CPU-only `device.water.bathymetry` owns field updates after ocean recording, including culled surfaces. Neither reserves GPU markers or claims a GPU duration |
| Temporal/post | Temporal outline inputs, resolve, depth store, bloom bright, blur horizontal, blur vertical, composite, tonemap, sharpen, quantize, edge, FXAA, distortion apply and final blit |
| Display/UI | Target-outline mask/grow/composite per group, fills, lines, additive/alpha billboards, transition overlay, SpriteBatch UI and scissor/texture runs, developer debug views |

A command-counting recorder attributes every Draw/DrawIndexed, Dispatch, copy, resolve, clear and buffer/texture update to one leaf or the explicit unattributed bucket. It records requested pipeline/resource/framebuffer/scissor switches and redundant consecutive binds. Native emitted state calls are a separate optional counter, not inferred from requested calls. Registry completeness tests compare an all-feature command trace with assigned leaves and detect newly unowned work. Inactive feature branches get bounded correctness fixtures even if they are not added to the paid suite.

Device leaves use the existing CPU phase callbacks and carry `DeviceOnly` in the frozen logical registry.
They participate in exclusive counter ownership, with marker indices zero and an unsupported GPU duration even
when the frame's GPU timestamps are valid. Skipping their GPU parent skips their logical rows too. Completeness
checks observe the full device, including waits and resource creation, and command-list uploads, and reject
exclusive counters assigned to nonleaf parents. All-feature warmed fixtures finish and verify cold retirement
before freezing. A separate bounded MSAA/blob fixture retains the cold retirement wait and proves CPU-only
ownership before rendering. Task 15 must retain both coverage paths and the scene-begin owner.

### Upload, memory and allocation data

- Count logical bytes for every buffer and texture update on both command-list and device paths, with load-time versus frame-time attribution. Count a forwarded update once. Record transfer/copy bytes separately from host upload bytes. Retain RenderFrameStats' four-bucket comparison without pretending it covers every stream.
- Per frame record draws, indexed draws, dispatch groups and local sizes, submitted instances/vertices/triangles, target dimensions/formats, pass occurrences, switches, wait/submit/fence counts and resource creations. Shader invocations and actual memory traffic require supported diagnostic counters or a model label.
- Track owned requested buffer bytes, texture payload bytes including mips/layers/samples, staging/ring capacity, target/history/scratch bytes, peak live bytes and resource counts. Add optional backend committed-allocation and resident/budget fields with source/unit and explicit unavailable states. Vulkan suballocated resources must not count both their payload and backing heap as the same total. D3D11 residency and Metal unified memory must not be called dedicated VRAM.
- Outside timed spans sample process working set/private bytes, managed live heap and supported native memory snapshots. During spans collect current-thread allocated bytes and GC generation deltas. Add process-wide allocation deltas for worker activity, retain scope and sampling precision, never sum thread and process totals. Do not force GC or create no-GC regions. Warm allocations are measured observations, not a new zero-allocation gate. A zero claim needs its own isolated allocation-region test.

### Per-pipeline diagnostics

Run compiler collection as a distinct diagnostic configuration on the same approved workload, outside timing blocks. Name each pipeline by shader/source byte digest, entry points, specialization/local size, layout and render state digest. Preserve raw vendor statistic names, descriptions, types and values, then normalize registers, spills and occupancy only when the meaning is documented.

- Vulkan: enumerate `VK_KHR_pipeline_executable_properties` support and `pipelineExecutableInfo`, enable them on the diagnostic device, create pipelines with `VK_PIPELINE_CREATE_CAPTURE_STATISTICS_BIT_KHR`, then call executable properties/statistics for every executable. Handle changing two-call counts and `VK_INCOMPLETE`. Capture internal representations only if separately needed. Returned compile statistics are vendor-defined, and do not promise achieved runtime occupancy. Unsupported extension, empty statistics or absent register/spill fields stay unavailable. [Khronos extension](https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_pipeline_executable_properties.html), [query requirements](https://docs.vulkan.org/spec/latest/chapters/pipelines.html).
- D3D11: retain compiled shader reflection instruction/resource counts and `TempRegisterCount` as intermediate-code statistics. They are not physical registers, hardware spills or achieved occupancy. Optional `D3D11_QUERY_PIPELINE_STATISTICS` supplies invocations in a separate diagnostic block. Hardware fields remain unavailable unless a separately approved NVIDIA profiler route actually supplies them. [Shader description](https://learn.microsoft.com/en-us/windows/win32/api/d3d11shader/ns-d3d11shader-d3d11_shader_desc), [query types](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query).
- Metal: enumerate available counter sets/sampling points. Retain supported counter data and compute `threadExecutionWidth`/maximum threadgroup limits as limits, not achieved occupancy. Do not invent physical register or spill counts. Xcode/Instruments capture is a separate owner-approved diagnostic if required, on hardware that supports it. [Counter sampling](https://developer.apple.com/documentation/metal/gpu-counters-and-counter-sample-buffers), [execution width](https://developer.apple.com/documentation/metal/mtlcomputepipelinestate/threadexecutionwidth).

Pipeline-statistics capture flags, profiler versions and validation layers belong in identity. Do not compare a captured diagnostic pipeline's timing directly with the production pipeline. Preserve SPIR-V/DXBC or compiler artifact hashes and the active precision/optimization flags. No Nsight installation or privileged performance-counter setting is part of the initial suite.

## 4. Hardware reference and gap arithmetic

The T4's published FP32 peak is 8.1 TFLOPS and its GDDR6 bandwidth is 320+ GB/s. These are device specifications, not a promise that any renderer shader can attain them. Tensor throughput is irrelevant to ordinary raster shaders. [NVIDIA T4 specifications](https://www.nvidia.com/en-us/data-center/tesla-t4/).

Build these twelve public controls on each measured backend, using the same timestamp/completion policy:

1. Empty frame with outer markers.
2. Empty render pass matching the main MRT formats and clears.
3. Empty depth-only pass matching shadow attachment state.
4. One near-empty compute dispatch with a retained observable write.
5. A dependent chain of 36 such dispatches with the production ordering/barrier pattern.
6. A matched independent 36-dispatch chain to distinguish chain latency from count alone.
7. Storage streaming read/write over 256 MiB, with the byte formula including reads and writes.
8. A matched 32 MiB storage control to expose cache/size sensitivity.
9. An FP32 FMA throughput kernel with multiple independent accumulators and verified emitted work.
10. A dependent arithmetic chain control, including the relevant divide/root instruction mix where needed.
11. A fullscreen unblended write matching the scene target formats.
12. An eight-layer blended/depth-tested fullscreen control matching S4 formats and sampling.

Each control has 64 warm-up and 128 measured frames, one control iteration. Keep output checks outside timing, verify that the compiler retained the work, and include work/dispatch/local-size hashes. A backend that does not expose an explicit barrier uses its real dependency-ordering implementation. Label the matched chain cost as combined dispatch/dependency overhead, not an isolated barrier measurement. Extra pipeline variants and vendor counter captures need an amended manifest, not an adaptive sweep.

Every executed pass gets a `RendererPassWorkModel` owned alongside its recipe: minimum logical read/write bytes, format/sample/pixel counts, vertex/invocation estimates, relevant operation counts, dispatch/draw count and serial dependencies, with exact/estimated/unknown provenance. For geometry and alpha overdraw use a range when actual invocations are unknown. Compressed traffic, caches and shader control flow prevent a claim of exact off-chip bytes from resource sizes alone.

Publish two reference columns:

- `peakLowerBoundNs`: `max(minBytes / peakBandwidth, minFp32Ops / peakFp32Rate)` only for components with defensible minimum work and matching instruction class. Unknown components remain unknown. A value is a loose bound, not a target.
- `calibratedReferenceNs`: the matched empty-pass/chain cost and `max(bytes / measuredBandwidth, operations / matchedThroughput, pixels / matchedFillRate)` with explicit dependency assumptions. Add only disjoint serial phases. If launch and throughput can overlap use max or a stated interval, not an unjustified sum. This is an empirical reference estimate, not a mathematical lower bound.

For each pass report measured nanoseconds, both reference values/ranges, absolute excess, measured/reference ratio, uncertainty and the dominant modeled resource. For copies use transfer bytes, for fullscreen work pixel/format traffic, for shadows vertex/depth throughput, for compute throughput and chain dependency, for UI pixel/vertex/blend work. An idle/cached pass reports NotExecuted. A sub-resolution span reports BelowResolution with the timer period. An observed result faster than its claimed lower bound flags a bad model instead of clamping the gap to zero. Unknown work yields `gapStatus=ModelIncomplete`, with available reference components and the missing evidence named. Thus every pass has a gap assessment, but none receives a fabricated precise floor.

CPU phases get empty-host/observer overhead alongside wall times and the actual hosted CPU identity. Do not compare a Linux CPU absolute time with Windows or an Apple machine as though T4 identity fixes host CPU performance.

## 5. Run protocol, cost and authorization

### Proposed baseline unit

One unit is one fresh workflow attempt, one leg and one process, `load=0`, with these finite counts:

- 1,200 discarded conditioning frames of S3 before measured blocks. Retain clock/power/temperature telemetry and the last conditioning-window means. This is a fixed rendering prelude, not a repeat-until-fast loop.
- Five scenes, two layouts, three iterations. Each block has exactly 192 warm-up and 256 measured frames. Total scene frames are `5 * 2 * 3 * 448 = 13,440`.
- Twelve controls, each 64 warm-up plus 128 measured frames, add 2,304 frames. Total unit frame count including conditioning is 16,944. Compiler metadata collection creates diagnostic pipelines outside these timed blocks and renders no additional frames. Optional invocation-counter/profiler runs are excluded from this unit.
- Rotate scene order by iteration and alternate total-only/per-pass order by `(sceneOrdinal + iteration) % 2`. The manifest contains the fully expanded ordered block list. No runtime shuffle, retry or convergence-selected sample count.
- Use three-frame batches, one completion drain, one harvest. All warm-up and measured frames must exist, including the final short batch. Serialize and publish after measurement, charged to the unit deadline.

Clock telemetry is sampled outside frame brackets at fixed batch boundaries or by a separately identified low-rate host sampler. Retain requested/observed clocks, P-state, utilization, temperature, power, throttling reasons and driver identity where supported. Compare the first and last 64 measured frames within a block and corresponding iteration means. Mark drift above 5 percent as `ClockOrWorkDrift` and do not promote that row as a stable baseline. Telemetry that is missing stays missing. It cannot be replaced by assuming the T4 is fully clocked. A drift failure requests a new approved protocol rather than silently discarding the first block or rerunning it.

Keep every valid frame, including slow outliers and GC frames. Publish per-block and combined mean, median, nearest-rank p95/p99, minimum, maximum, standard deviation, GC association and first/last-window values. Do not trim, winsorize or select the best iteration. Three iterations show repeatability inside one job, not fleet variance or statistical significance. With only 256 samples per block, p99 is descriptive and is not a tail guarantee. Device loss, invalid queries, missing frames, hot resource growth, unexpected production waits, route fallback, nonfinite values, timeout or failed final publication invalidate promotion while preserving partial evidence.

### Budget proposal, not authorization

Rates are the brief's Linux USD 0.052/minute and Windows USD 0.102/minute. Estimates include billed setup/build/teardown, not only measured frame duration. Queue waiting is not counted as running time. Verify actual billed minutes after a pilot.

| Unit | Work | Estimate | Hard job cap | Spend at cap |
| --- | --- | --- | --- | ---: |
| Linux pilot | S3 only, two layouts, one iteration, 64 warm and 128 measured per layout, 300 conditioning frames, controls 1/4/5/7/9/11 at 32 warm and 64 measured | 6 to 8 min | 12 min | USD 0.624 |
| Windows D3D11 pilot | Same pilot counts and controls | 8 to 10 min | 12 min | USD 1.224 |
| Linux full | Exact baseline unit above | 20 to 25 min | 35 min | USD 1.820 |
| Windows D3D11 full | Exact baseline unit above | 20 to 25 min | 35 min | USD 3.570 |
| Optional Windows Vulkan full | A separate owner decision after the two primary legs | 20 to 25 min | 35 min | USD 3.570 |

At an assumed 50 ms completed-frame service cost, 16,944 frames take 14.12 minutes before approximately 6 to 10 minutes of setup, drains, diagnostics and publication. This is an estimate, not a measurement. Slow frames or driver installation can exceed it. Set a 1,500-second cooperative unit limit including final publication, a 10,000 ms frame limit and the 35-minute outer job cap. Pilots use a 600-second unit limit and the same frame limit. Refuse partial evidence if the limit is reached. Do not raise limits automatically. The two pilots plus two primary full units cap the proposed first campaign at USD 7.238, rounded to USD 7.24, before any provider billing rounding. The comparable estimate is USD 4.21 to USD 5.29 for those four jobs. Windows Vulkan is not in that cap.

Pilots first validate accounting, marker perturbation, identity and cost. They do not establish the published baseline. A full unit needs a fresh owner approval after pilot review. A budget envelope does not authorize reruns, extra units or all legs at once.

### Strict renderer authorization

Use `khaozengine.renderer-perf-unit/1`, distinct from coverage. It binds unit ID, workload commit A, baseline main commit, exact recipe/settings/shader hashes, complete block/control order and counts, layout/pacing, Release/runtime environment, backend and T4 policy, runner/workflow/leg, deadlines, maximum billed minutes, rate/currency, purpose and the owner's exact approval reference. Hash exact UTF-8 bytes. Reject duplicate/unknown/missing/mistyped fields and nonfinite or excessive values before opening a device or reading a clock.

After explicit approval, authorization commit B has exactly one parent A and adds only `tools/Renderer.Perf.Tests/authorized/<unit>.json`. Dispatch B with manifest byte hash and workload A. A report separately records workflow SHA, authorization SHA, measured SHA, run ID/attempt and process ID. Only attempt 1 is admissible. A manifest is an audit record, not a credential or a substitute for actual consent. Implementation commits ship no executable approval manifest.

## 6. Report and comparison contract

`khaozengine.renderer-perf-report/1` is the canonical machine artifact. Emit one JSON file, a compact Markdown report, TRX, logs, exact manifest bytes and backend diagnostic sidecars with SHA-256 references. Reports preserve raw bounded data, not only derived averages.

| Section | Required data |
| --- | --- |
| `identity` | Unit, manifest/workload/main/authorization/workflow SHAs, run/attempt/process, harness assembly, OS/image/CPU/runtime/GC/tiering, environment, adapter/vendor/device/driver, effective backend/D3D recording mode, timestamp bits/period/capability, validation/capture flags and completion policy |
| `recipes` | Scene/profile versions and exact bytes/hashes, source asset provenance, dimensions/formats, geometry/texture/bone counts, camera/pose/wind/sun/wave sequence digests, expected and actual routes/counts |
| `blocks` | Ordered scene/layout/iteration IDs, warm-up and measured arrays, raw monotonic CPU ticks/frequency, raw elapsed GPU markers/period and pass-tree occurrence map, frame ID, actual work counters, uploads/copies, allocations/GC, waits/growth, clock observations and errors |
| `memory` | Logical payload versus backend committed/resident/budget snapshots, staging/history/scratch categories, process and managed snapshots, peaks, source/units, support status and reason |
| `pipelines` | Stable shader/pipeline digests, executable/entry stage, local size, raw typed compiler statistics and normalized fields with provenance, missing-field reasons, diagnostic configuration hash |
| `controls` | Work definitions/digests, raw samples, matched state/formats, achieved rates, chain/empty overhead, compiler-work-retention validation and clock identity |
| `assessment` | Frame and per-pass summaries, perturbation/drift flags, modeled work ranges, theoretical/calibrated references and gaps, validity reasons, unmatched/unattributed time and counter completeness |
| `publication` | Completed/Refused/Failed, returnedByRun, finalUnitPublished, original failure and later cleanup/publication errors, elapsed time and actual/estimated cost. Completed does not imply every optional metric is supported |

Every optional metric has a value/unit/source/status/reason. Invalid or absent is null with a reason, never a zero-valued success. Warm-up is retained separately. GPU frame totals use the outer pair, never the sum of nested scope durations. Keep parent/leaf relationships and unattributed gaps so a bad partition is visible.

The raw JSON requires `raw.format = "metric-vectors/2"`. `raw.metrics` is a shared table of
`{name, unit, source, status, reason}` descriptors. Named entries retain their complete original metric
key, including `logical.unattributed`, `logical.pass.<id>.<occurrence>` and `logical.total` ownership.
Scalar metric descriptors have `name = null`, with identity supplied by their containing field.
`raw.layouts` contains ordered arrays of zero-based descriptor indices with unique names per layout.
Every metric dictionary is `{layout, values, zeroRuns}`. The layout includes every metric and support reason.
The numeric `values` vector retains nonzero Available values in layout order. `zeroRuns` is a zero-based
reference to `raw.zeroRuns`, a shared table of explicit positive-zero run patterns. Each pattern counts
the positive zeros before, between and after those values, with exactly `values.Length + 1` nonnegative
counts. Sharing identical patterns does not omit a count or value from any frame. The expanded value count
must exactly match the Available descriptor count. Signed negative zero retains its numeric slot and sign. Unavailable descriptors consume no value slot and reconstruct null.
Standalone metrics, including GPU durations and publication accounting, are `{schema, value}` references.
`raw.passes` shares pass IDs, occurrences, parents, marker pairs and DeviceOnly ownership. Pass rows are
`[schema, elapsedTicks, durationSchema, durationValue]`, with the last two entries retaining the complete
scalar duration reference. Ticks and duration values are empty arrays for null or singleton numeric
arrays for present values. These optional arrays have at most one number, so null array entries remain
forbidden. `raw.cpuSpans` shares CPU ID/parent metadata. Each frame's `cpuPhases` is
`{originTicks, schemas, startOffsets, durations}` with equally sized columns in original segment order.
The Int64 origin is the smallest start tick, or zero for an empty trace. Start offsets and durations are
exact UInt64 differences, so even the full Int64 tick range round-trips without floating-point conversion.
Each start is origin plus offset, and each end is start plus duration, with checked Int64 reconstruction.
Standalone CPU spans, including drain and harvest, retain `{schema, startTicks, endTicks}`. Native diagnostic
typed scalars retain their existing Boolean/Int64/UInt64/Float64 representation.

All frames, CPU spans, pass occurrences, marker pairs, DeviceOnly flags and errors remain present.
The reader expands these references before applying the existing semantic and promotion checks.
All raw fields are required, including nullable descriptor and scalar fields. Duplicate descriptors,
duplicate layouts or zero-run patterns, duplicate metric names, unknown fields/formats, foreign or negative
indices, wrong value or CPU column counts, tick overflow, nonfinite values and unsupported values with
numbers are rejected. The 256 MiB byte cap and depth 32 stay fixed. Serialization enforces the byte cap while writing, before allocating an
oversized output. No frame sampling, counter omission, uncounted zeros or support-reason loss is allowed.

`memory.observations` retains each startup and batch observation with operation, stage, first frame
(null for startup), monotonic ticks/frequency, process metrics and native snapshots. A failed observation
retains its reason and empty values rather than reusing an earlier sample. The count is bounded by the
manifest operations plus retained batches. `memory.peaks` maps each observed available process/managed
metric to its maximum metric with the original source and observation index. Equal maxima keep the first
observation. `memory.process` and `memory.native` are the last available snapshot, not a peak substitute.
Task 14 readers must expand version 2 zero-run references, pass tuples and CPU interval columns before
analysis, and use observations and peak references when deriving memory summaries. Task 15 must integrate
this raw format with its publication/sidecar readers and verify native artifact sizes. Retain the full-unit
recorder-built size fixture with populated pass counters, target/dispatch facts, CPU traces and errors.
Do not change the cap or drop raw evidence. The synthetic size proof makes no hardware performance claim.

Publication captures its completion instant from the injected unit clock after the final JSON and Markdown
payloads have been serialized and written. Returned elapsed time and estimated cost include that work.
The file sink reserves two fixed-width JSON numeric slots within the same byte cap, then stamps those
completion values without serializing or copying the frame payload again. The stamp records that completed
instant and is deadline checked. A failed or late stamp follows failed-final correction and partial recovery.
Task 14 readers accept ordinary JSON whitespace in these slots. Task 15 retains this two-stage sink contract.

Every decoded pass row carries required `deviceOnly` ownership through its raw pass schema.
CPU-only device rows have `deviceOnly=true`, zero
begin/end markers, null elapsed ticks and null GPU duration, with `Unsupported` when executed or
`NotExecuted` when skipped. They survive partial and completed publication and do not prevent promotion
when all GPU rows and the frame satisfy the existing timing requirements. GPU rows carry `deviceOnly=false`,
retain positive ordered marker pairs and still require supported timing for promotion. Task 15 must integrate
this report shape with the device leaf registry.

`RendererPerfComparison.Compare(baseline, candidate)` compares only identical scene/profile/control versions, layout, backend/adapter, driver, relevant runtime/recording/capture settings and completion policy. Source/shader hashes may change because they identify the candidate being evaluated. Explain all identity differences, separate unlike rows and refuse a regression verdict on incompatible pairs. Compare total-only frame means and distributions, per-pass diagnostic deltas and reference-gap deltas. Preserve three iteration means instead of treating every correlated frame as an independent replicate. The first baseline supplies no universal pass/fail percentage. Recommend focused optimizations from measured excess and then agree regression budgets with the owner.

Raw artifacts stay on the workflow. A later checked-in baseline summary includes immutable run links, hashes and configuration, not thousands of frames or private captures. Future changes never overwrite the original baseline. A changed recipe, driver or protocol starts a new series with the old result retained.

## 7. Static leads, all unmeasured

These are priorities for measurement, not verified defects or promised savings. No optimization is included in this planning task. The most useful five are first.

| Priority | Unmeasured lead and source | Measurement that could confirm or refute it |
| --- | --- | --- |
| 1 | Any skinned caster and casting foliage keep the directional atlas dirty, potentially replaying substantial rigid depth work. `Scene3D.CascadeShadowPass.cs:25`, `Scene3D.ShadowCasters.cs:391`, `Scene3D.FoliageShadows.cs:41` | Per-cascade rigid/skinned/foliage time, actual depth draws/triangles, dirty reason and S1 versus S2/S3 schedules. Preserve the existing static-skip and cascade-culling optimizations |
| 2 | Point rows draw six faces and repeatedly walk caster lists. Skinned selection checks the light shell again inside each face and records per-draw face state. `Scene3D.PointShadowPass.cs:246`, `Scene3D.SkinnedPointShadows.cs:124` | CPU list-walk/encode time, state calls, six face intervals and actual vertex work at S3 counts. Main already shares one bone palette across passes in `Scene3D.GpuSkinning.cs:27`, so repeated palette upload is not this lead |
| 3 | Each frame regroups queued rigid instances, fills/scatters streams, applies cutoffs, rebases when active and uploads retained instance data. Main-pass visibility follows that upload. `Scene3D.cs:1440`, `Scene3D.InstanceGrouping.cs:39`, `Scene3D.RenderOrigin.cs:219` | Phase CPU times, uploaded bytes, retained versus visible counts and instance-count scaling. Caster retention can justify offscreen data. Do not assume every camera-culled instance is removable |
| 4 | TAA opaque copies, history/depth storage and the post chain move several full images. Bloom adds bright, two blur and composite draws, followed by tonemap/sharpen and final blit. `Scene3D.TemporalResolve.cs:183`, `Scene3D.TemporalResolve.cs:222`, `Rendering/PixelPostProcess.cs:343` | Matched-format bandwidth/fill controls and per-copy/post times in S4. Establish bandwidth or launch limitation before proposing fusion. Existing pass ordering, alpha and HDR behavior are correctness constraints |
| 5 | Sprite/glyph vertices are streamed per texture run and clip changes force flushes. Mixed UI order can increase small draws, updates and bind churn. `KhaozEngine.Render2D/SpriteBatch.cs:615`, `SpriteBatch.cs:637`, `SpriteBatch.cs:665` | S4 UI CPU/GPU time, bytes, flushes, texture/scissor changes and redundant native binds. Preserve alpha submission order and clipping |
| 6 | Skinned vertices are evaluated for main, cascades and relevant point faces despite shared palettes. `Scene3D.GpuSkinning.cs:56`, `Scene3D.ShadowCasters.cs:406`, `Scene3D.SkinnedPointShadows.cs:124` | Actual shader invocations and throughput versus draw/dependency controls. A cached deformed stream may trade work for memory, it is not assumed faster |
| 7 | Foliage patches are tested and submitted per cascade and can produce alpha-cutout overdraw. `Scene3D.FoliageShadows.cs:41`, `Scene3D.Foliage.cs:210` | Patch visibility/counts, per-cascade/opaque time, invocation or estimated overdraw ranges. Compare leaf geometry with the fill controls |
| 8 | Ocean work ping-pongs the current column and next frame's row over disjoint work buffers, followed by optional mip generation. `Rendering/OceanFftProducer.cs:307`, `OceanFftProducer.cs:338` | Each dispatch, copy and mip time, groups, bytes and actual resource dependencies. The column consumes the prior frame's row output. Native synchronization is separate evidence. Do not call the one-time prime a per-frame stall |

All Render3D shorthand paths in this table are under `KhaozEngine.Render3D/`. Line numbers are snapshot locators. Revalidate symbols before implementing a later optimization. The equipment reducers and overlay traversal are separate measured context in #1418, not newly measured renderer findings here.

## 8. File responsibilities and implementation interfaces

New files below are proposed, not existing APIs. Keep them below the size ratchet. Do not raise a baseline or add a second frame loop.

- `KhaozEngine.Render.Tests/Gpu/RendererPerf/` owns strict protocol, recipes, recorder, session, fake tests, work models and comparison. Public engine tests remain independent of paid execution.
- `RendererPerfContracts.cs` defines `RendererPerfLayout { TotalOnly, PerPass }`, immutable manifest/block/identity/report records, `RendererPerfFrameSample`, `RendererPerfMetric` support states and `RendererPassWorkModel`. `RendererPerfManifest.Parse(ReadOnlySpan<byte>)` returns the validated manifest. `RendererPerfReportJson.Serialize(RendererPerfReport)` returns exact UTF-8 report bytes.
- `KhaozEngine.Render.Tests/Gpu/Performance/GpuPerfTimestampRing.cs` owns the shared retained pool lifecycle, generalized from coverage. `TryBegin(IGpuCommandList, GpuFrameSubmission, int sampleId, uint markerCount, out IGpuTimestampPool?)` registers and retains before beginning. `PollCompleted()` publishes through a cold-supplied callback. A full ring returns false and invalidates the measurement, never silently drops it. Coverage keeps its own layout/report adapter. Renderer layouts allow at most 255 scope occurrences, hence 512 queries including the outer pair. A recipe freezes its ordered marker slots before warming. Skipped occurrences write their reserved indices in order and retain NotExecuted status. Unexpected occurrences or capacity overflow invalidate the block, never change the query count after BeginTimestamps.
- `KhaozEngine.Render3D/Diagnostics/RenderPassKey.cs` defines `(string Id, int Occurrence)`. `IRenderPassObserver` defines `BeginPass(IGpuCommandList, RenderPassKey)`, `EndPass(IGpuCommandList, RenderPassKey)`, `BeginCpuPhase(string id)` and `EndCpuPhase(string id)`. CPU-only phase callbacks never emit GPU markers. This is an internal opt-in seam. Null is the production default. Scene3D and owned renderers propagate the same observer, with no Diagnostics package dependency. The harness directly scopes UI through the same recorder.
- `RendererPerfRecorder.cs`, `RendererPerfDevice.cs` and `RendererPerfCommandList.cs` count commands and CPU spans, use native timestamp pools and retain per-frame storage. The command-list adapter starts the outer pair immediately after native Begin, before forwarding the initial bind/clear, and arms the native End tail. It delegates every optional native interface used by production. It must not trigger duplicate timestamps or double-count forwarded updates.
- `RendererPerfRecipe.cs` defines `IRendererPerfRecipe : IDisposable` with `Queue(Scene3D scene, int frameIndex)` and `RendererPerfRecipeFacts Facts`. Facts contain recipe/settings/geometry/asset digests and expected schedules. Village/crowd and outdoors/effects have separate owner files. JSON recipes live in `RendererPerf/recipes/`.
- `RendererPerfSession.cs` defines `IRendererPerfSession : IDisposable` with `void Frame(int frameIndex, ref RendererPerfFrameSample sample)`, `void CompleteBatch(Span<RendererPerfFrameSample> samples)` and `RendererPerfSessionInfo ReadInfo()`. It also defines `RendererPerfClock(Func<long> Now, long Frequency)` and delegate `IRendererPerfSession RendererPerfSessionFactory(RendererPerfManifest manifest, RendererPerfBlock block, RendererPerfClock clock)`. `RendererPerfNativeSession` uses production FramePhases and a retained offscreen target with explicit Scene3D then UI ordering. `RendererPerfDriver.Run(RendererPerfManifest, RendererPerfRunIdentity, RendererPerfSessionFactory, RendererPerfClock, IRendererPerfPublication)` returns a report finalized by the publication owner. The publication interface supplies partial/final publication with the same unit clock/deadline, so fake tests can exercise late or failed writes. Driver clock/factory/publication are injected.
- `KhaozEngine.Gpu/IGpuPipelineDiagnostics.cs` defines optional cold `GpuPipelineCompilerReport ReadCompilerReport()`. `IGpuResourceMemoryDiagnostics.cs` defines `GpuResourceMemorySnapshot ReadMemorySnapshot()`. Task 1 fixes typed values/status/provenance used by these records. Implement these optionally in native backends. Do not enlarge mandatory IGpuDevice/IGpuPipeline implementation obligations.
- `RendererPerfControls.cs`, `RendererPerfFloorModel.cs`, `RendererPerfComparison.cs` own controls, reference calculations and compatible-report comparison respectively. Renderer source receives no hardware-specific floor formula.
- `tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj` stays outside `KhaozEngine.slnx`, following the equipment entrypoint/reference pattern. `RendererPerfTests.Measure_authorized_unit` is the single performance fact, trait `Category=RendererPerformance`, gated by `KE_RENDERER_PERF_UNIT`. Only fake protocol and correctness tests belong in ordinary CI.

## 9. Ordered implementation tasks

Every task is one writer's independently reviewable change. The execution orchestrator owns worktree guards, dependency reconciliation, placement, review and integration. These are future commands, none was run during planning. Each code task first adds the named failing test, runs it once through build-slot, implements the contract, reruns its focused selection and commits explicit paths. Heavy full verification runs at the integration checkpoint, not in a loop for every task.

For Tasks 1 to 14, the shared focused command is `build-slot --label renderer-perf-task-N -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter '<selection named below>'`. A test requiring the separate Foundation or Windowing project names that actual project instead. Native checks are separately approved and run hosted, not automatically after a fake-test pass.

### Task 0: Reconcile dependencies and freeze the executable target

**Files:** Read the source boundary above. Future accepted-dependency records go in this plan's execution outcome, no unrelated feature files.
**Interfaces:** Produces the exact accepted GPU-layer/frame-submission commit set and a source-boundary record consumed by every following task.

- [ ] Compare current main and the accepted equipment infrastructure. Confirm timestamp pools, completion fences, End tails and FramePhases are present or identify a separately reviewed extraction. Coordinate its ownership before editing.
- [ ] Freeze the actual source, workflow and dependency SHAs. Verify the named APIs against those files, including hosted Metal counter evidence. Do not treat the moving equipment branch HEAD as accepted production code.
- [ ] Acceptance: the orchestrator can name one compilable dependency target without importing the full coverage feature or changing its gates. If unavailable, return the concrete upstream dependency decision before native implementation.

### Task 1: Strict protocol, report types and optional diagnostic contracts

**Files:** Create `RendererPerfContracts.cs`, `RendererPerfManifest.cs`, `RendererPerfReportJson.cs`, `RendererPerfProtocolTests.cs`, and the two optional `KhaozEngine.Gpu` interfaces above. Keep native implementations out of this task.
**Interfaces:** Produces every manifest/sample/report/metric type used later, `Parse`, `Serialize`, pipeline compiler and memory snapshot contracts.

- [ ] Add `RendererPerfProtocolTests.Rejects_invalid_or_reused_authorization_before_factory_or_clock`, `Preserves_missing_metric_status_and_partial_failure`, and `Round_trips_exact_scene_and_block_order`. Assert duplicate fields, wrong hashes/attempt, NaN, overflow and truncated final report never create a device.
- [ ] Implement the strict renderer schemas and exact pilot/full presets, including block order, limits and distinct optional-statistic provenance. Test unavailable native fields serialize as null with a reason.
- [ ] Verify with `FullyQualifiedName~RendererPerfProtocolTests`. Acceptance: all protocol failures retain their cause, coverage schemas are rejected and no executable approval file exists. Commit the contract and tests.

### Task 2: Shared timestamp lifecycle with boundary and ownership proofs

**Files:** Create `GpuPerfTimestampRing.cs` and `GpuPerfTimestampRingTests.cs` under `Gpu/Performance/`. Adapt `Gpu/CoveragePerf/CoveragePerfTimestampRing.cs` only after Task 0 reconciliation. Reuse existing pool/End-tail implementations.
**Interfaces:** Consumes Task 1 sample identities. Produces variable-sized fixed pools and retained-fence publication for both adapters.

- [ ] Add `GpuPerfTimestampRingTests.Retains_before_begin_and_never_reuses_unconsumed_fence`, `Full_ring_or_missing_sample_invalidates_block`, `Rejects_pending_invalid_disjoint_wrap_and_foreign_fence`, and `Tail_contains_deferred_clear_and_native_finalization` using fake native traces.
- [ ] Extract lifecycle without changing coverage's three-frame pacing, quarantine or layout semantics. Handle partial final batches, skipped reserved markers and the 512-query renderer cap. Query counts are fixed before warming, never grown in-frame. Test an unexpected pass and a skipped cached pass against the same fixed layout.
- [ ] Verify `FullyQualifiedName~GpuPerfTimestampRingTests|FullyQualifiedName~CoveragePerfTimestampRingTests|FullyQualifiedName~CoveragePerfTimestampEndTests`. Acceptance: coverage adapter behavior is preserved, exactly one matching completion owns each result, no polling flush/wait is introduced. Commit.

### Task 3: CPU phases and complete logical command counters

**Files:** Create `RendererPerfRecorder.cs`, `RendererPerfDevice.cs`, `RendererPerfCommandList.cs`, `RendererPerfCounterTests.cs`.
**Interfaces:** Consumes Task 1 records and Task 2 ring. Produces attributed command/CPU samples for the native session and pass observers.

- [ ] Add `RendererPerfCounterTests.Counts_each_forwarded_update_once`, `Owns_every_draw_dispatch_copy_clear_and_bind`, and `Preserves_optional_interfaces_and_disabled_sequence`. Use known buffer/texture dimensions, repeated binds, both upload routes and an actual End-tail fake.
- [ ] Implement preallocated counters and non-overlapping CPU children, raw versus effective optional native counters, current-thread/process allocation scope labels and explicit unattributed work. No reflection or report formatting in a measured frame.
- [ ] Verify `FullyQualifiedName~RendererPerfCounterTests`. Acceptance: arithmetic partitions reconcile, all forwarding preserves native semantics and disabled tracing adds no allocation or ordering change. Commit.

### Task 4: Geometry, shadow, atmosphere and motion trace boundaries

**Files:** Create `Render3D/Diagnostics/RenderPassKey.cs`, `IRenderPassObserver.cs`, `Scene3D.Profiling.cs` and `RendererPerfGeometryTraceTests.cs`. Add narrow call sites to `Scene3D.cs`, `Scene3D.CascadeShadowPass.cs`, `Scene3D.PointShadowPass.cs`, foliage/skinned shadow owners and `Rendering/AtmosphereRenderer.cs`.
**Interfaces:** Produces stable pass IDs/occurrences through the optional observer. Consumes Task 3 recorder.

- [ ] Add `RendererPerfGeometryTraceTests.Nests_every_cascade_face_and_geometry_route_once`, `Cached_shadow_or_lut_emits_NotExecuted`, and `Null_observer_preserves_render_trace`. Exercise opaque/cutout/dissolve, CPU/GPU skinning, static/dynamic/transient rows and skipped passes.
- [ ] Instrument the owning passes and fused motion metadata. Add CPU-only scopes for grouping, culling/visibility and upload packing, including work before the existing timing buckets. Keep helper logic in cohesive partials, do not increase a file-size baseline. Observer exceptions fail profiling and preserve the original frame failure.
- [ ] Verify `FullyQualifiedName~RendererPerfGeometryTraceTests` plus existing shadow scheduling tests selected by changed owners. Acceptance: every geometry/atmosphere command has one leaf owner, parent sums are not treated as extra work. Commit.

### Task 5: Water, transparent, temporal, post and display trace boundaries

**Files:** Modify `Scene3D.TemporalResolve.cs`, `Scene3D.TargetOutlinePass.cs`, relevant effect owners, `Rendering/OceanFftProducer.cs`, `WaterRenderer.cs`, `PixelPostProcess.cs` and `TemporalResolveRenderer.cs`. Create `RendererPerfPostTraceTests.cs`.
**Interfaces:** Consumes Task 4 pass seam. Produces the rest of the registry, including resolves/copies, FFT/mip occurrences and each bloom draw.

- [ ] Add `RendererPerfPostTraceTests.Attributes_every_post_copy_and_effect`, `Prime_is_startup_and_column_next_row_are_distinct`, `Fused_or_skipped_work_is_not_zero_duration`, and `Null_observer_preserves_all_feature_command_trace`.
- [ ] Add scopes to the exact owning draw/dispatch paths, with no extra submit, barrier or framebuffer bind merely to create a label. UI remains an explicit harness scope over SpriteBatch. Include bounded fixtures for MSAA/blob, starfield, transition and debug paths absent from the default suite.
- [ ] Verify `FullyQualifiedName~RendererPerfPostTraceTests`. Acceptance: all-feature command coverage finds no unowned command, total-only leaves per-pass callbacks disabled and Metal perturbation remains disclosed. Commit.

### Task 6: Empty, village and crowd recipes

**Files:** Create `RendererPerfRecipe.cs`, `RendererPerfVillageScene.cs`, `RendererPerfCrowdScene.cs`, recipes `empty.json`, `village.json`, `crowd.json` and `RendererPerfRecipeTests.cs`.
**Interfaces:** Produces S0/S1/S3 recipe objects/facts and stable digests, consuming existing engine primitives and scene APIs.

- [ ] Add `RendererPerfRecipeTests.Fixed_topology_counts_and_seed_have_stable_digest`, `Periodic_camera_and_pose_use_a_continuous_resettable_block_clock`, `Crowd_uses_32_visible_gpu_actors_and_declared_shadow_rows`, and `Recipe_contains_no_consumer_asset`.
- [ ] Implement the fixed counts/settings from section 2, sharing pure existing tube/placement logic rather than duplicating an entire fixture. Pin rest-pose/palette/material data and cache state assertions. Freeze a construction-only digest independently of GPU readback.
- [ ] Verify `FullyQualifiedName~RendererPerfRecipeTests`. Acceptance: repeated construction produces identical recipe/sequence facts and the published counts match actual route assertions. Commit.

### Task 7: Outdoors, effects and UI recipes

**Files:** Create `RendererPerfOutdoorsScene.cs`, `RendererPerfEffectsScene.cs`, `RendererPerfUiScene.cs`, recipes `outdoors.json`, `effects-ui.json` and `RendererPerfEffectsRecipeTests.cs`.
**Interfaces:** Produces S2/S4 recipe facts and expected pass schedules through Task 6's interface.

- [ ] Add `RendererPerfEffectsRecipeTests.Foliage_water_and_fft_routes_match_fixed_counts`, `Ui_quads_textures_and_clips_are_fixed`, `Clipmap_event_sequence_is_repeatable`, and `No_water_frame_cannot_reuse_stale_water_stats`.
- [ ] Reuse the pure public fixture geometry, pin the redistributable font/digest, and build the complete transparent/post/UI path without reading back every frame. Pin actual TAA history, ocean, shadow, outline and distortion routes.
- [ ] Verify `FullyQualifiedName~RendererPerfEffectsRecipeTests`. Acceptance: all required scene feature families have an exercised trace and reproducible input digests. Commit.

### Task 8: Native session, fixed driver, drift and publication

**Files:** Create `RendererPerfNativeSession.cs`, `RendererPerfDriver.cs`, `RendererPerfPublication.cs`, `RendererPerfDriverTests.cs` and `RendererPerfFrameBoundaryTests.cs`. Use reconciled `FramePhases` and retained submission contracts.
**Interfaces:** Implements Task 1/6 session/factory/clock contracts. Produces finalized raw reports for Tasks 12 to 14.

- [ ] Add `RendererPerfFrameBoundaryTests.Outer_pair_contains_host_clear_ui_and_end_tail`, `Cpu_wall_contains_host_return_and_separates_batch_drain`, and `Unexpected_prepare_submission_invalidates_steady_frame`.
- [ ] Add `RendererPerfDriverTests.Executes_only_manifest_order_and_exact_counts`, `Keeps_outliers_gc_and_first_block`, `Clock_drift_refuses_promotion_without_retry`, and `Timeout_or_failed_publication_preserves_partial_report`. Use held clocks/completion fakes, never sleeps or timing thresholds.
- [ ] Implement conditioning, fixed warm/measured counts, three-frame pacing, raw telemetry and deadlines. Disabled/no-timestamp/incorrect adapter modes fail admission before timing. Cold controls and readbacks remain outside measured intervals.
- [ ] Verify `FullyQualifiedName~RendererPerfDriverTests|FullyQualifiedName~RendererPerfFrameBoundaryTests`. Acceptance: no dropped/duplicated frame, no extra measurement, complete boundary evidence and both raw layouts retained. Commit.

### Task 9: Vulkan compiler and memory diagnostics

**Files:** Create `Gpu.Vulkan/Internal/VulkanPipelineDiagnostics.cs`, `VulkanMemoryDiagnostics.cs` and `RendererPerfVulkanDiagnosticsTests.cs`. Modify `VulkanGpuDevice.Create.cs`, `VulkanGraphicsPipeline.cs`, `VulkanComputePipeline.cs`, `VulkanMemoryAllocator.cs` at optional diagnostic boundaries only.
**Interfaces:** Implements Task 1 optional contracts, emits raw executable statistics and separate payload/heap memory facts.

- [ ] Add `RendererPerfVulkanDiagnosticsTests.Capture_requires_extension_feature_and_pipeline_flag`, `Incomplete_enumeration_retries_within_fixed_capacity`, `Missing_register_spill_occupancy_stays_unavailable`, and `Suballocated_payload_does_not_double_count_heap`.
- [ ] Implement cold executable enumeration, typed vendor data and memory snapshots. Keep the timed production device free of capture flags. Use bounded count retries for API enumeration only, not measurement retries.
- [ ] Verify `FullyQualifiedName~RendererPerfVulkanDiagnosticsTests`. Acceptance: fake extension absence, empty data and native failure are honest unsupported/error results. Hosted extension/driver availability remains a pilot question. Commit.

### Task 10: D3D11 compiler and memory diagnostics

**Files:** Create `Gpu.D3D11/Internal/D3D11PipelineDiagnostics.cs`, `D3D11MemoryDiagnostics.cs`, `RendererPerfD3D11DiagnosticsTests.cs`. Modify `D3D11ShaderCompiler.cs`, graphics/compute pipeline owners and resource factory where needed.
**Interfaces:** Implements Task 1 contracts with intermediate shader statistics and typed logical/optional native memory data.

- [ ] Add `RendererPerfD3D11DiagnosticsTests.Reflection_temporaries_are_not_hardware_registers`, `Unavailable_residency_is_not_zero`, and `Diagnostic_query_is_outside_timed_pipeline_configuration`.
- [ ] Retain DXBC hashes/reflection outside frames, implement optional invocation-query support without making it part of the baseline unit, record effective D3D recording mode and preserve timestamp disjoint validity.
- [ ] Verify `FullyQualifiedName~RendererPerfD3D11DiagnosticsTests`. Acceptance: register/spill/occupancy labels do not overstate reflection, native unsupported paths preserve timing capability. Commit.

### Task 11: Metal capability and memory diagnostics

**Files:** Create `Gpu.Metal/Internal/MetalPipelineDiagnostics.cs`, `MetalMemoryDiagnostics.cs`, `RendererPerfMetalDiagnosticsTests.cs`. Use existing `MetalTimestampApi.cs`, pipeline owners and native counter interop.
**Interfaces:** Implements Task 1 contracts with actual supported limits/counters and unified-memory provenance.

- [ ] Add `RendererPerfMetalDiagnosticsTests.Hosted_no_timestamp_route_is_cpu_only`, `Missing_counter_or_sample_buffer_refuses_gpu_time`, `Thread_width_is_a_limit_not_occupancy`, and `Logical_ring_capacity_is_separate_from_residency`.
- [ ] Implement device/counter enumeration and cold metadata. Do not add an unapproved local Metal run or claim parity with T4 timings. Preserve the null observer path and existing encoder lifetime policy.
- [ ] Verify `FullyQualifiedName~RendererPerfMetalDiagnosticsTests`. Acceptance: supported physical devices remain usable, unsupported hosted devices cannot accidentally pass GPU evidence admission. Commit.

### Task 12: Hardware controls and per-pass work models

**Files:** Create `RendererPerfControls.cs`, public control shader files alongside `RendererPerf/controls/`, `RendererPerfFloorModel.cs`, recipe work models and `RendererPerfFloorModelTests.cs`.
**Interfaces:** Produces control facts and `RendererPassWorkModel` data consumed by report assessment.

- [ ] Add `RendererPerfFloorModelTests.Matches_units_bytes_and_instruction_class`, `Never_adds_overlapping_launch_and_throughput_terms`, `Unknown_traffic_yields_model_incomplete`, and `Observed_below_bound_flags_invalid_model` with synthetic numeric vectors.
- [ ] Implement the twelve finite controls, output retention checks, hashes and work formulas. Give every registry pass a model or an explicit missing-evidence reason. Include format/mip/sample and read-plus-write byte arithmetic.
- [ ] Verify `FullyQualifiedName~RendererPerfFloorModelTests` and shader structural/corpus checks required by the actual added shaders. Acceptance: arithmetic is independently checkable without a GPU, no T4 rate is hardcoded as measured throughput. Commit.

### Task 13: Isolated entrypoint and paid workflow admission

**Files:** Create `tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj`, `RendererPerfTests.cs`, `authorized/README.md`, the explicit test-assembly access file if needed, and `RendererPerfWorkflowContractTests.cs`. Modify `.github/workflows/gpu-hosted-bench.yml` and `docs/CROSS-PLATFORM.md`.
**Interfaces:** Produces one fixed fact and renderer-specific inputs `renderer-perf-unit`, `renderer-perf-manifest-sha256`, `renderer-perf-workload-sha`, with `KE_RENDERER_PERF_UNIT` and `KE_RENDERER_PERF_REPORTS` passed explicitly.

- [ ] Add `RendererPerfWorkflowContractTests.Refuses_combined_modes_all_legs_and_freeform_filter`, `Authorization_adds_only_renderer_manifest_to_workload_parent`, and `Requires_one_executed_test_and_final_matching_report`.
- [ ] Reuse the checked-in NVIDIA device/ICD safeguards and pinned PowerShell bootstrap for the new mode. Enforce the preset's job cap, immutable tested ref and first attempt, upload partial evidence with `always()`, and reject missing TRX/final JSON/hash identity. Validate real vendor/device IDs and software flag, not only the word NVIDIA in a log.
- [ ] Verify `FullyQualifiedName~RendererPerfWorkflowContractTests` and the repository's existing workflow static checks. Acceptance: ordinary and coverage workflow modes remain equivalent, the project is outside the solution and no measurement is dispatched. Commit.

### Task 14: Offline summaries, compatible comparisons and canonical documentation

**Files:** Create `RendererPerfComparison.cs`, `RendererPerfAnalysis.cs`, `RendererPerfComparisonTests.cs`, `docs/RENDERER-PERFORMANCE.md`, and the matching row in `docs/INDEX.md`. Update GPU/consumer docs only for the actual new optional APIs and profiling behavior.
**Interfaces:** Implements `RendererPerfComparison.Compare(RendererPerfReport baseline, RendererPerfReport candidate)`, returning compatible rows, deltas and refusal reasons.

- [ ] Add `RendererPerfComparisonTests.Rejects_driver_recipe_layout_and_pacing_mismatch`, `Allows_candidate_source_hash_change`, `Keeps_correlated_iteration_means_separate`, `Nested_scopes_do_not_replace_outer_total`, and `Missing_metric_cannot_win_a_comparison`.
- [ ] Implement raw-to-summary and gap arithmetic, percentiles with specified rank, perturbation/drift flags, memory provenance and compact Markdown. Test report self-consistency, partition gaps and partial publication using synthetic fixtures.
- [ ] Verify `FullyQualifiedName~RendererPerfComparisonTests|FullyQualifiedName~RendererPerfProtocolTests`. Acceptance: an independent reader can reproduce every summary from the artifact, and no regression budget or unsupported hardware claim is invented. Commit.

### Task 15: Integration verification, then separately authorized pilots and baseline

Remaining cold compiler/publication preparation follows the [canonical amendment and four-task plan](2026-10-24-renderer-cold-publication.md), with the original workload, schedule, budgets and run-approval gates preserved.

**Files:** Future verification record `docs/verification/2026-10-21-renderer-performance-baseline.md` and compact immutable baseline summary under `docs/performance/renderer/`. Executable authorization manifests only after their individual owner approvals.
**Interfaces:** Consumes all prior artifacts and accepted dependency SHA, produces a reviewed baseline series or a specific refusal with preserved evidence.

- [ ] Reconcile current main in the task branch. Run zero-warning Release solution build, the engine's required Release test selection, formatting and all applicable file-size/prose/dash/doc/version guards once at the finishing checkpoint. Every local build/test uses build-slot and the full suite uses `--heavy`. Do not run the outside-solution performance fact locally.
- [ ] Review the exact command/scene/block coverage and the dependency diff. Obtain separate owner approval for each pilot unit, publish its manifest as the sole authorization-commit addition, and dispatch only the selected hosted leg. No automatic follow-up on green.
- [ ] Independently check native identity, complete counts, timestamps, End-tail coverage, clock behavior, compiler support, model completeness, perturbation and actual billed minutes. A failed pilot returns a scoped correction and fresh approval request, not a retry.
- [ ] Present the pilot evidence and concrete full-unit budget. Only after fresh approval run each primary full unit. Publish raw artifacts and checked-in summaries naming immutable workflow runs and hashes. Keep unsupported counters and incomplete work models visible.
- [ ] Acceptance: all five scenes and required pass rows have honest assessments, total-only frame numbers are valid on both T4 primary legs, partial/unsupported data cannot pass, and the owner receives the measured largest excesses with suggested next investigations. Optimization, consumer adoption, Windows Vulkan expansion and release remain separate work.

## 10. Engine and consumer responsibilities

All timing/completion seams, scopes, backend diagnostics, public fixtures, work models, hosted protocol and report/comparison code are engine-general and belong here. No consumer is required to establish the initial public baseline.

A consumer is needed later to establish representativeness of real art, view distances, materials, motion and UI. That is a distinct approved fit run with its own content hashes, private artifact policy and full issue URL. It may motivate a new public recipe version, but it cannot retroactively change this baseline or make private consumer content a dependency of engine CI. Adoption waits for an independently authorized engine release where the consumer requires a released pin.

## 11. Decisions needed before execution

1. Accept the five-scene 1080p TAA Native suite and the two primary legs, Linux Vulkan and Windows D3D11. Recommendation is yes. Windows Vulkan, higher resolutions and private consumer scenes stay separately budgeted.
2. Accept the infrastructure approach, with the coverage orchestrator choosing the exact reviewed Part A/submission/ring dependency set. If it is not yet on main, approve a narrow shared-infrastructure reconciliation rather than coupling this work to the whole coverage feature.
3. Approve implementation of the measurement-only plan. Do not interpret that as approval for paid work or performance optimization.
4. When implementation is ready, approve the exact first pilot manifest and its maximum USD 0.624 or USD 1.224 leg cost. The proposed four-job campaign cap is USD 7.24, but each full run still needs its own approval after pilot evidence. Budget is never inferred from the owner's broad performance goal.
5. Accept that the initial floor is a combination of a loose peak bound and a measured reference model, with honest unknowns. If achieved occupancy or physical spill data is mandatory when the driver does not expose it, choose a separate vendor-profiler investigation and budget before adding it.

## Planner self-review

- Inventory covers main, unmerged Part A, hosted execution, software backends, Metal limitations and old diagnostic timings. No old CPU wall result is relabeled GPU time.
- Five public scene recipes cover terrain, buildings, foliage, water, skinning, both shadow families, transparency, post/TAA and UI with fixed counts and no consumer art.
- The plan defines all requested metrics, every-pass accounting, compiler support states, whole-frame clear/End boundaries and CPU/throughput distinctions.
- Controls, work-model uncertainty, gap formulas, finite run counts, clock ramp, outliers, USD caps and immutable comparison/report identities have explicit owners and tests.
- Tasks 0 to 15 have bounded file ownership, interfaces and acceptance criteria. The five review-focus cases map to named tests. No code, timing result, CI run, optimization or release is claimed by this planning artifact.
