# Equipment coverage Part C: GPU coverage path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace per-frame CPU coverage construction and certification with an exact GPU construction, atomic uncovered fallback, shared pulled rendering and asynchronous diagnostics.
**Architecture:** Pack immutable topology and support data once, stage scalar command frames and palettes, then record a batched compute chain and every consuming draw in one command list. Keep candidate work private until select publishes one actor decision, retain the actually presented final surface on the GPU, and keep CPU drawing decisions independent of readback. Prove arithmetic and all frozen device outputs before connecting the chain to Scene3D.
**Tech Stack:** C# net10.0, System.Numerics as field containers, GLSL 450, precise SPIR-V/HLSL/MSL, Metal, Direct3D11 Shader Model 5.0, Vulkan, xUnit and the hosted cross-platform-gpu workflow.
**Spec:** docs/design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md
**Kernel contract:** docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md (Part B branch)

## Global Constraints

- Execute later in /Users/antonio/KhaozEngine/.worktrees/ec-gpu-path on feature/equipment-coverage-gpu-path, after root merges accepted Parts A and B into feature/equipment-coverage.
- Read snapshots for this plan were equipment-coverage at 4d31de360, ec-gpu-layer at 633adc68d, and ec-oracle at 89998744e. Part B Task 8 review can amend its public oracle API before execution.
- Device tolerance is zero. Every accepted output word, including position, normal, color, UV and tangent, must equal the reference word bit for bit.
- Keep all 72 cases and the unchanged 144-row outputs.sha256 manifest. Production GPU comparisons use the 72 mode-0 rows. The six challenging-collar w1 certification negatives still have required construction outputs.
- MarginFraction is .25f, word 0x3e800000. Construction reserves MarginFraction x Clearance. Certification uses the approved Clearance.
- F flushes each inventory operand and result when magnitude bits are at most 0x00800000, inclusive, preserving the sign bit. Uploaded copied fields also pass F. Flush events never refuse.
- Keep the section 4 scalar graphs, section 9 sequencing, first-code attribution and stored gather order. No float scatter atomics, reassociation, reciprocal substitution or tree summation.
- Compile every numerical kernel with GpuShaderCompileOptions(PreciseMath: true), authored precise intermediates, Metal fast math off and FXC D3DCOMPILE_IEEE_STRICTNESS.
- Limits remain 32 regions, 4 disjoint commands, 1,024 support triangles and 256 boundary edges per command, refinement levels 1 to 4, and 65,536 final vertices and triangles per host.
- CPU admission keeps identity, capacity, command-frame admission and origin magnitude. No runtime CPU reference construction or exact certification.
- No compute or a kernel-build failure selects ordinary vertex-shader skinning for every actor and reports one coverage capability failure per scene capability instance.
- Rendering never waits for outcomes. A full three-slot diagnostic ring drops that frame's diagnostic copy and preserves its Dispatched result.
- Direct3D11 readback completion needs Present or a drain. Bounded headless facts submit, drain once at their assertion boundary, and poll once. Production never adds that drain.
- Never sample a still-bound colour target from compute. Coverage kernels use buffers only. Pixel probes first bind a non-overlapping framebuffer, as A18 and issue 1489 require.
- Previous history is previous actually presented FINAL vertices, with current-origin rebasing, per-view identity and local transition rejection.
- Warm staging, dispatch, resolution and oracle checks allocate zero managed bytes.
- Budgets remain 2 ms added CPU preparation for 32 typical actors and 15 percent native GPU overhead. Every measurement needs fresh owner approval. This plan contains no timing run, pilot, benchmark, stress run or budget claim.
- Hosted Apple Paravirtual timestamps are unsupported and the GPU gate is unmeasurable there. WARP and lavapipe timestamps do not establish real-hardware performance.
- Every local build, test and formatter invocation uses build-slot. Full suites use --heavy. Never loop tests.
- Plain dotnet test skips GPU facts. Native facts use KE_GPU_TESTS=1 and KE_GRAPHICS_BACKEND=metal-native locally, one focused invocation at a time.
- Each task finishes with a zero-warning Release build, its focused tests and repo-root dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include paths, per B13.
- Warnings are errors. Keep KESIZE limits, explicit-path commits and existing adapter compatibility. Do not suppress warnings, increase size baselines or use stash.
- Root owns pushing, hosted dispatch, current-main reconciliation, final Task 10, version/changelog, packaging and release. No worker integrates or tags.

## Review Focus

1. A failure before refined attributes exist must still draw the complete original current pose in every material and pass, rather than reading uninitialized Before data. Tasks 5, 7 and 10.
2. Concurrent guard writers must not stop their peers, lose an earlier reference event, count another slot, or leak a partial surface. Tasks 6 and 7.
3. A GPU-only refusal or recovery while readback is held must seed history and reject only the actor's projected transition bounds in that same frame. Task 12.
4. A large bone translation can escape the existing 1.5x rest sphere even when model translation is near the origin. Origin admission and every CPU bound must conservatively contain that pose. Task 8, under ruling C1.
5. A full ring, abandoned recording, scene disposal or actor-slot reuse must neither reuse a live fence nor attribute an old result to a new actor. Task 13.

## Root rulings on the planner's decisions

The planner raised D1 to D3. Root resolved them as rulings C1 to C3, recorded in the program ledger. The spec's CPU-side bounds paragraph and contract section 6 are amended to match.

**C1, conservative bounds (D1).** The existing 1.5x rest sphere (Scene3D.cs SkinnedCullSafetyFactor, Internal/PointShadowCasterSphere.cs) is a heuristic and does not bound an arbitrary palette. Covered actors use a current-palette envelope instead, in Task 8:

- Cold data per host:
  - for each bone, the box of source positions it influences with a nonzero weight
  - the interval of per-vertex weight totals
  - whether every weight is nonnegative
  - whether any vertex takes the identity branch (total below 1e-8f)
- Per frame:
  1. Transform each bone's box by its composed palette matrix.
  2. Join the transformed boxes into H.
  3. Take the interval product of H with the weight-total interval. Pinned skinning blends the matrices without normalising, so for nonnegative weights each skinned position lies in that product.
  4. Join the source box when the identity branch is reachable.
  5. A host with any negative weight uses Task 8's per-slot interval sum instead.
  6. Enclose rounding, then expand by the maximum command recession, both as Task 8 specifies.
- A test asserts that the nonnegative path's bound equals the joined form, not the per-slot sum. The per-slot sum can be up to four times looser.
- This one bound serves the origin guard, culling, caster classification, the coverage sphere and rejection rectangles for covered actors.
- Uncovered actors keep the heuristic, tracked by https://github.com/APKiwiOrg/KhaozEngine/issues/1492.

**C2, submission ownership (D2).** Accept GpuFrameSubmission in Task 13 as the GPU layer's one frame-completion owner.

- Readback rings and Part A timestamp pools are both consumers of IGpuFrameCompletionConsumer. Part A already assigned Part C the frame fence shared by readbacks and timestamp resolve.
- Coverage history now commits on IGpuFrameSubmissionParticipant.Submitted, not at the end of command encoding. This replaces the earlier accepted temporal contract for covered actors only. The history buffers are now GPU-resident, so committing for a list that is never submitted would point the next frame at a buffer the GPU never wrote. Task 12's history tests assert this.
- GpuRetireQueue keeps minting its own fence. Moving it onto the frame fence is https://github.com/APKiwiOrg/KhaozEngine/issues/1493.

**C3, correction proof (D3).** Accept Task 2's method for division and square root on all three backends, Metal included.

- The native result is only a seed.
- An exact integer midpoint-residual test decides the result.
- When the seed's bracket misses, the search expands by powers of two over ordinal positive words and then bisects. That bounds the search by the whole positive binary32 domain, at most 31 bisections.
- That domain bound is the proved searched-neighbor bound section 6 requires. Correctness does not depend on any native accuracy claim, including Vulkan's inherited square-root accuracy.

## Preflight rulings

The preflight scan found two ordering blockers and four ownership gaps. Root ruled on them as P1 to P6, recorded in the plan ledger. They override the task text below where they differ.

- **P1.** Task 9 adds DispatchedHostPreparationCount and keeps CoveredHostPreparationCount until Task 15 deletes its last writer and test caller, so every intermediate commit builds.
- **P2.** Execution order is Tasks 1 to 11, then 13, then 12, then 14 to 17.
  - Task 13 lands GpuFrameSubmission, both interfaces, host wiring, participant callbacks and the outcome ring.
  - Task 12 then registers the scene's history participant and commits history on Submitted. It also takes Task 13's two history assertions.
- **P3.** Replace RecordThrough with internal `RecordPhases(IGpuCommandList commands, CoverageGpuBatch batch, CoverageGpuPhase firstPhase, CoverageGpuPhase lastPhase)`.
  - Reset always records first, and phases before firstPhase are skipped.
  - Production Record calls it with Skin to Select.
  - Task 5 creates it, CoverageGpuTestSession.cs and CoverageGpuGuardGlsl.cs. Task 7 extends the test session.
- **P4.** Task 13 makes GpuBufferReadbackRing implement IGpuFrameCompletionConsumer, with teardown, abandonment and fence-retention tests. The timestamp pool adapter comes with the GPU timing harness before the first approved pilot.
- **P5.** Recording fixtures assert commands, bindings, metadata and recording allocations only.
  - Every numeric GPU word, refusal, recovery and completed readback comes from a native executing fixture.
  - Each task that changes a route migrates its own focused tests.
  - Task 16 measures the full warm path, including completed readback, natively. A headless recording-only allocation test stays for staging and encoding.
- **P6.** Task 10 owns the main-pass insertion (Scene3D.cs:1512-1533) and the outline call.
  - Task 11 adds covered actors to CountSkinnedCasters (Scene3D.SkinnedCasters.cs:50-57).
  - Task 11 also extends the skinned point-face owner (Scene3D.SkinnedPointShadows.cs:97-148).
  - Both tasks test scenes whose ordinary skinned lists are empty.

## Execution rulings C4 to C8

The plan ledger records these rulings in full. They override the task text below where they differ.

- **C4.** Precise shaders keep IEEE zero signs, NaN and infinity on every backend.
  - Metal backports SPIRV-Cross 300681bbcb. Issue https://github.com/APKiwiOrg/KhaozEngine/issues/1495 tracks dropping it.
  - Vulkan requires SignedZeroInfNanPreserve.
- **C5.** No dynamic vector or matrix component access in coverage kernels.
- **C6.** Exact multiword code uses constant-index limbs, is sized to each operation's proved bound, and logs Metal thread limits.
- **C8.** Hosted run 39181650116 showed the exact predicate layer is too large for FXC cs_5_0 and Apple Paravirtual. The cause is call-site duplication of inlined helpers, not the arithmetic. One overlay call expands to about 1.04 million SPIR-V instructions. Evidence and design are in [the code-size investigation](proofs/2026-10-19-equipment-coverage-gpu-code-size.md).
  - Every physical kernel routes exact product, signed add, RN32, corrected division and square root through one shared call site per primitive, using bounded job loops.
  - Query graphs (height, boundary, triangle height, overlay, correction and face validation) become resumable, typed continuations that keep reference traversal order.
  - Kernels are split where a cap would otherwise be exceeded. Each physical phase compiles as its own module.
  - Gates for every coverage pipeline:
    - At most 30,000 exhaustively inlined SPIR-V instructions and 100,000 bytes per HLSL and MSL target.
    - Exactly one heavy primitive site per worker, explicitly rolled job and traversal loops, and no forbidden accesses.
    - Native FXC cs_5_0 compilation, Apple Paravirtual pipeline creation at 64 threads, and exact words on every backend.
  - Sizes are measured as instruction counts and bytes, never as time.
  - The numerical contract and the 144 frozen hashes are unchanged.
  - Section 6 of the investigation lists the per-task changes for Tasks 2, 3 and 5 to 7.

## File-structure map

All paths are repository-relative. In task file lists, Coverage/, Rendering/, Internal/ and an unprefixed Scene3D or Render3D filename are under KhaozEngine.Render3D/. Test prefixes Render3D/ and Gpu/ are under KhaozEngine.Render.Tests/. Attachments.Render3D/ means KhaozEngine.Attachments.Render3D/. New shader files contain GLSL string fragments, matching the existing shader-source approach, rather than adding a second build tool.

| Action and files | Responsibility |
| --- | --- |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuArithmeticGlsl.cs | Inventory operations and explicit F |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuExactWordsGlsl.cs | Fixed 32-bit limb operations, exact rounding and residuals |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuRoundingGlsl.cs | Corrected division and square root |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuPredicatesGlsl.cs | Exact Edge, Dot and B30 sides |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuSupportGlsl.cs | B16 height, boundary and overlay graphs |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuLayout.cs | Word offsets, phase enum, reservations and surface views |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuUpload.cs | Cold topology, skin, support and pulling-index packing |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuResources.cs | Shared immutable storage, capacity, leases and retirement |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuPipeline.cs | Precise pipeline creation and ordered phase encoding |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuSkinRefineGlsl.cs | Source skinning, alias checks and refinement |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuConformGlsl.cs | Pointwise group conformation |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuCorrectionGlsl.cs | Immutable face correction, group gather and transition |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuGuardGlsl.cs | Per-element notes, deterministic reduction and actor flag |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuNormalsGlsl.cs | Face validation and ordered normal/tangent gathers |
| Create KhaozEngine.Render3D/Coverage/Gpu/CoverageGpuSelectGlsl.cs | All-or-nothing publication and history seeding |
| Create KhaozEngine.Render3D/Coverage/CoverageCommandUniforms.cs | Small CPU admission/frame/palette scratch |
| Create KhaozEngine.Render3D/Coverage/CoverageActorBounds.cs | Current-palette envelope, recession expansion and origin guard |
| Create KhaozEngine.Render3D/Scene3D.CoverageGpu.cs | Resolution, dispatch ownership and capability state |
| Create KhaozEngine.Render3D/Scene3D.CoverageOutcomes.cs | Aggregate ring copy, retained metadata and result enumeration |
| Create KhaozEngine.Render3D/Scene3D.CoverageVerification.cs | Opt-in asynchronous real-device comparison |
| Create KhaozEngine.Render3D/Rendering/CoveragePulledResources.cs | Read-only final-buffer layouts and fixed draw payloads |
| Create KhaozEngine.Render3D/Rendering/ModelRenderer.CoverageShadows.cs | Coverage cascade and point-face draw helpers |
| Create KhaozEngine.Render3D/Internal/ShaderSources.CoveragePulling.cs | Main, outline, motion, cascade and point pulling vertices |
| Create KhaozEngine.Render3D/Internal/ShaderSources.CoverageTransition.cs | GPU-conditional local rejection mask |
| Create KhaozEngine.Gpu/GpuFrameSubmission.cs and IGpuFrameCompletionConsumer.cs | C2's shared frame-completion ownership |
| Create KhaozEngine.Windowing/Frame.Completion.cs | Window-frame access to that owner |
| Create KhaozEngine.Render3D/Coverage/CoverageTally.cs | Existing tally moved out of the deleted constructor |
| Modify Coverage/CoverageReferenceFrames.cs and CoverageReferenceSkinning.cs | Reuse only the pinned CPU input producers, without reference construction scratch |
| Modify Coverage/SkinnedHostConformer.cs, CoverageFrameArena.cs and SkinnedCoverageFrame.cs | GPU reservations, frozen requests and Dispatched lifecycle |
| Modify Coverage/CoverageMotionHistory.cs and CoverageSurfaceBounds.cs | CPU history metadata and conservative bounds, no CPU final-position arrays |
| Modify Coverage/CoverageUploadEvidence.cs | Preserve mesh authentication, remove render-error-only attribute scans |
| Modify Coverage/CoverageCertification.cs, CoverageScratch.cs and CoverageCommandPose.cs | Keep oracle geometry certification, remove display certification and dead construction scratch |
| Modify Scene3D.Coverage.cs, .CoverageUpload.cs, .CoverageEquipment.cs, .CoveragePrepare.cs, .CoverageSurfaces.cs, .CoverageMotion.cs, .CoverageOutlines.cs and .CoverageDiagnostics.cs | Replace CPU surface and pass-capture consumers |
| Modify Scene3D.cs, Scene3D.FrameView.cs, .FramePrepare.cs, .SkinnedSubmission.cs, .SkinnedDrawRecords.cs, .ShadowCasters.cs, .CascadeShadowPass.cs, .PointShadowPass.cs and .MotionTarget.cs | Encode compute before consumers and add independent pulled draw lists |
| Modify Rendering/ModelRenderer.Coverage.cs, TargetOutlineRenderer.Coverage.cs, PointShadowRenderer.Skinned.cs and CoverageTransitionMask.cs | Pulled pass integration and local rejection |
| Modify Render3DSurface.cs, Render3DPreview.cs, Render3DSnapshotRunner.cs, KhaozEngine.Windowing/Frame.cs, FramePhases.cs and AppWindow.cs | Submit the recording once with its retained completion and dispose it before the device |
| Modify KhaozEngine.Attachments.Render3D/HostCoverageBinding.cs, IAttachmentCoverageScene.cs and Scene3DAttachmentScene.cs | Cold support reservation during transactional equip |
| Delete Coverage/CoverageEvaluator.cs, CoverageCoreCorrection.cs and CoverageSurfaceNormals.cs | Retired exact CPU constructor and its constructor-only normal updater |
| Delete Coverage/CoverageRenderError.cs, CoverageRenderArithmetic.cs, CoverageRenderContext.cs and Scene3D.CoverageRenderContext.cs | Retired render certifier, pass list and stamp |
| Modify KhaozEngine.Render3D/README.md and docs/USING-KHAOZENGINE.md | Living API and lifecycle contract |
| Create docs/design/EQUIPMENT-COVERAGE-GPU-ARITHMETIC-PROOF.md | Checked limb/rounding proofs and emitted-code evidence |
| Create docs/superpowers/plans/proofs/2026-10-19-equipment-coverage-gpu-correctness.md | Exact-commit correctness, allocation and reviewed golden deltas |
| Modify docs/INDEX.md | Index the arithmetic proof |
| Create the test files named in Tasks 1 to 14 and 16 | Unit kernels, standalone chain, CPU admission and scene contracts |
| Modify the existing test files named in Tasks 9 to 17 | Retain reachable contracts while replacing removed API use |
| Modify the 18 backend files KhaozEngine.Render.Tests/Gpu/goldens/coverage-{front,rear,grazing-left,grazing-right,high,low-backlit}.{metal-native,direct3d11-native,vulkan-native}.txt only when reviewed | Backend-owned references, no automatic refresh |

The last row denotes six exact scene names crossed with the three exact backend suffixes. Fixtures/coverage-exact-v1 and coverage-reference-v1 are read-only in this plan. Also create Render3D/CoverageGpuRemovalTests.cs in Task 15. Modify Render3D/CoverageCaptureDevice.cs and CoverageSceneFixture.cs in Task 9 for recording-only compute support and cold support reservation.

## Shared interfaces and encoding decisions

Types in the GPU coverage directory are internal unless marked public below. Offsets are uint word offsets, byte sizes are checked uint values, IDs remain their existing uint/ulong/MotionKey types. Never marshal CLR structs directly into std430.

- CoverageGpuLayout.ModelWords = 16, ModelBytes = 64. Field order is position XYZ, normal XYZ, color XYZW, UV XY, tangent XYZW, matching CoverageReferenceBits.Fields.
- CoverageGpuHostUpload is a sealed record with uint[] Words, int SourceCount, VertexCount, TriangleCount, PositionGroupCount, AliasCount, ShadingGroupCount and BoneCount. CoverageGpuSupportUpload is a sealed record with uint[] Words, int TriangleCount and BoundaryCount. These are cold owned arrays, never frame temporaries.
- The host word header has eight count words in the preceding order, with word 7 reserved zero, followed by offset/count pairs for these tables in order: source attributes, integer skin indices, refined vertices, refined triangles, source triangles, source aliases, indices, position recipes, group weights, vertex weights, edge parameters, group representatives, core offsets, core faces, vertex offsets, vertex corners, shading offsets, shading corners, active-candidate offsets, active candidates, refined pull indices and fallback pull indices. Pad the header to 64 words. Each source attribute record is 20 words, the 16 model fields followed by four bone weights. Preserve all integer fields of the corresponding Part B recipe/record.
- A support header is 16 words: MaxSlope bits, GridMin XY bits, GridMax XY bits, triangle count, boundary count, then offsets for positions, source indices, triangles, boundary vertices, cell offsets and cell triangles. Words 13 to 15 are zero. Each command frame is 80 words: the three matrices in CoverageReferenceFrame order, Recess XYZ, ZScale, CapMargin, ClearanceCoverage, ActualClearanceCoverage, CorrectionRate, original Command index, MinZ, MaxZ, CollarWidth, Clearance, MaxWorldRecession, TransitionWeight, command ID, equipment low/high words, RegionIndex and static support offset. Words 68 to 79 are zero.
- CoverageGpuReservation is a readonly record struct with int ActorSlot, uint StaticBase, FrameBase, WorkBase, CurrentBase, PreviousBase and int SourceCount, VertexCount, TriangleCount.
- CoverageGpuSurfaceView is a readonly record struct with IGpuBuffer Current, Previous, StaticData, uint CurrentBase, PreviousBase, StaticBase, int VertexCount and TriangleCount.
- CoverageGpuBatch is a preallocated owner of actor reservations and frame words. It exposes int Count and ReadOnlySpan<CoverageGpuReservation> Actors. Cold Reserve(int actors, int frameWords, int workWords, int surfaceWords) may grow only before recording.
- CoverageGpuBatch(IGpuDevice device) owns the six buffers below and exposes IGpuBuffer StaticData, FrameData, Work, Current, Previous and Results. Dispose follows submitted-resource retirement. CoverageGpuLayout.CreateReservation(int actorSlot,uint staticBase,uint frameBase,uint workBase,uint currentBase,uint previousBase,CoverageGpuHostUpload host) returns CoverageGpuReservation after checked size/offset validation.
- Each actor frame header is 16 words: StaticBase, WorkBase, CurrentBase, PreviousBase, result offset ActorSlot*16, SourceCount, VertexCount, TriangleCount, PositionGroupCount, AliasCount, ShadingGroupCount, BoneCount, CommandCount, Flags, and history revision low/high words. Flags bit 0 is HistoryValid and bit 1 is MainPresented. The header is followed by BoneCount*16 composed-palette words, four 80-word sorted command frames, four Order words and 32 RegionCommands words. Unused commands are zero, unused region slots are -1.
- The 64-word work header contains offset/count pairs for Posed, FlushedSource, Before, PointAfter, TransitionAfter, Published, GroupPositions, FullRecessions, Corrections, ActiveRepresentatives, FaceCorrections, FaceValidation and Events, followed by the completed-stage failure code/slot/count/stage and Refused words. Vertex records use 16 words, group positions 3, group scalars/representatives 1, face validation 2 and each event 6 words in Code/Slot/Element/LocalOrder/Count/Reserved order. Events reserve max(SourceCount,AliasCount,PositionGroupCount,VertexCount,3*TriangleCount,2*PositionGroupCount) records. Each worker overwrites every record in its current phase's row, including zero-code entries, before reduction.
- Each final header and each aggregate result row is 16 words: frame low/high, view low/high, actor MotionKey.Value low/high, preparation low/high, code, equipment low/high, command ID, count, limit, Refused and TransitionChanged. Final payload is SourceCount*16 source words followed by VertexCount*16 canonical refined words. Outcome presentation is Covered when code=0, otherwise UncoveredFallback. Shader side uses only 32-bit word pairs for 64-bit IDs.
- CoverageGpuPipeline(IGpuDevice device) creates all pipelines once. void Record(IGpuCommandList commands, CoverageGpuBatch batch) is production. Internal RecordThrough(IGpuCommandList commands, CoverageGpuBatch batch, CoverageGpuPhase lastPhase) is the bounded test seam, using the same entry points. Tests may upload a frozen Posed segment and start at refinement, without adding a production input-mode branch.
- Each shader entry point is void main(), local_size_x=64 except reducers, whose local_size_x=1. Dispatch X is ceil(max per-actor element count / 64), Y is batch.Count, Z is 1. Reducers dispatch (1, batch.Count, 1).
- Six storage bindings suffice on Shader Model 5.0: static RO words, frame RO words, work RW words, current-final RW words, previous-final RW words and diagnostic/result RW words. No storage texture, descriptor indexing, float atomic, shader int64 or GPU indirect-draw feature is required.
- Work buffers hold private Posed, Before, pointwise After, transitioned After, Published, group scalars, face records and guard events. Read/write consumers are separated by dispatches.
- Each final allocation has a header and both a source-pose section and a canonical refined section. Select publishes the source pose for fallback and the canonical candidate for success. Vertex pulling selects the corresponding static index table using that same GPU outcome.
- Draws are nonindexed triangle lists beginning at vertex 0, base vertex 0. A uniform supplies firstCorner for a material. For fallback, the first refined descendant of each original triangle pulls its three original source indices. Remaining descendants pull the first source index three times, producing degenerate triangles. Thus fallback remains the original surface even when refinement itself refused. Canonical refined output remains indexed internally for equivalence readback.
- A single uint Refused word per actor is reset by Reset. Workers atomicOr(Refused, 1u) after a note. They never early-out on a flag written by a peer in the same phase. A separate completed-stage failure record, frozen by a reducer, gates later phases.
- Guard event rows are fixed: one per source/alias/group/generated-attribute/normal operation, up to three local notes per core face, and two per full-target group. Reducers traverse contract section 9 keys and saturate matching-code/slot counts at 2147483647.
- No GPU geometry certificate is produced. EquipmentIntersection=9 and HostIntersection=10 remain offline certification concerns.

Keep legacy private fields/types until Task 15 when their final source consumers are migrated. Earlier tasks disconnect the CPU constructor/certifier from runtime and do not populate or read those old vertex arrays. This keeps every intermediate commit buildable without a temporary CPU fallback. Task 15 migrates compile-time test call sites before deleting APIs. Task 17 then owns the native appearance verdicts and golden review.

### Seven logical stages and physical dispatches

| Logical stage | Ordered physical phases | Publication boundary |
| --- | --- | --- |
| Setup | Reset | Clear actor flag, notes, group/face scalars and current result without touching retained previous state |
| 1, skin | Skin, ReduceSkin | Source-index first failure, complete source pose |
| 2, refine | ValidateSource, ReduceSource, ValidateAliases, ReduceAliases, InterpolateGroups, ReduceGroups, ValidateAttributes, ReduceAttributes, FillBefore | Before is filled only after all source, alias, group and generated-attribute checks pass |
| 3, conform | ConformGroups, ConformBoundary, ConformResume, CopyPointAliases, ReduceConform | Pointwise After immutable before face work |
| 4, correction | CorrectFaces, CorrectFacesOverlay, CorrectFacesFinish, ReduceFaces, GatherCorrections, CheckFullTargets, ReduceFullTargets, CopyCorrectedAliases | All faces finish before group gather, all full targets pass before alias publication |
| 5, transition | TransitionGroups, StoreTransition | Full-target checks occur even at transition weight 0 |
| 6, normals | ValidateVertices, ReduceVertices, ValidateFaces, ReduceFacesOrientation, GatherAttributes, ReduceAttributesFinal | Triangle-order orientation validation precedes any publishable normal/tangent |
| 7, select | Select | One outcome controls the complete surface, history seed and asynchronous payload |

Physical phase names are the exact CoverageGpuPhase members. Numerical phases port contract section 4. Reducers reproduce section 9, rather than selecting the first atomic writer. This deliberately spends dispatches on global dependencies. Fusing independent lanes inside a phase is allowed only after the native proof stays exact, without changing these task interfaces.

Task 6 applies C8 option D after the combined ConformGroups and CorrectFaces modules exceeded the
caps. Work header pairs 34/35 and 36/37 name checked, invocation-owned group and face continuation
tables. Their strides are six and twelve words. Group records contain query XYZ, height, spatial
weight and state. Face records contain the nominal triangle, discrepancy, overlay maximum and state.
ConformGroups initializes all six group words, including idle and failed records. CorrectFaces always
initializes the face state word. Idle face payloads are unspecified and must not be read. State 1
publishes a complete face payload for overlay, and state 2 publishes it for finish. Each face consumer
checks its required state before loading the payload. Dispatch barriers separate height,
boundary and conformation completion, and face preparation, overlay and final correction. All face
notes remain private to their face rows until ReduceFaces. Completed failure substages remain 1 to 5
for skin and refinement, with conformation 6, face correction 7 and full-target validation 8.

## Verification commands used by every task

Run from the execution worktree root in Bash. Retain c_start_commit before Task 1. This function is a shell-session definition, not a new repository script:

~~~bash
c_check() {
    local c_label="$1" c_filter="$2" c_path
    local -a c_cs=()
    build-slot --label "$c_label-build" -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release || return
    build-slot --label "$c_label-tests" -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$c_filter" --logger 'console;verbosity=normal' || return
    while IFS= read -r c_path; do
        test -n "$c_path" && c_cs+=("$c_path")
    done < <(git diff --name-only --diff-filter=ACMR HEAD -- '*.cs'; git ls-files --others --exclude-standard -- '*.cs')
    test "${#c_cs[@]}" -gt 0 || return 1
    build-slot --label "$c_label-format" -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include "${c_cs[@]}"
}
~~~

Expected every time: build exit 0 with 0 warnings and 0 errors, the selected tests pass with 0 failures and 0 unexpected skips, and formatter exit 0. The explicit native suite must report nonzero executed GPU facts. A red compilation failure for a missing planned type is valid. Where behavior already exists, plant and reverse only the task's implementation hunk to prove the test fails. Do not use stash.

Each commit step stages exactly the task's changed files and commits with that same explicit pathspec. Root supplies hosted proofs on the pushed commit using:

~~~bash
gh workflow run cross-platform-gpu.yml --ref feature/equipment-coverage-gpu-path -f legs=all -f tier=push -f disableGpuDiskCache=true -f bake=false -f renderTestFilter='<task filter>'
~~~

This is a later root action, not authorized execution during planning. All native tasks below need Metal, Direct3D11 and Vulkan results. The stated filter is also the exact renderTestFilter. Retain TRX, emitted shader artifacts and zero-error Vulkan synchronization-validation logs. Put Compute in new native class names so the existing synchronization subset selects them. Do not use tier=deep on hosted Apple Paravirtual.

## Task 1: Pinned scalar inventory and compiler lowering

**Files:** Create Coverage/Gpu/CoverageGpuArithmeticGlsl.cs. Create/Test Gpu/CoverageGpuArithmeticTests.cs and CoverageComputeArithmeticGpuTests.cs. Modify/Test Gpu/ShaderSourceValidationTests.cs:190-210 and ShaderCorpus.cs:44-149. Create the arithmetic proof document and modify docs/INDEX.md at its rendering-design rows.

**Interfaces:** Consumes CoverageReferenceArithmetic's scalar inventory and Part A's shader compile options. Produces GLSL float covF(float), covAdd(float,float), covSub(float,float), covMul(float,float), covAbs(float), covNeg(float), covMin(float,float), covMax(float,float), covClamp(float,float,float), covRead(uint word), bool covFinite(float), covLess(float,float), covLessOrEqual(float,float), covGreater(float,float), covGreaterOrEqual(float,float), covEqual(float,float), covNotEqual(float,float), and void covStore(inout uint word,float value). Produces float covDot3(vec3,vec3), covDot2(vec2,vec2), vec3 covCross(vec3,vec3), covSub3(vec3,vec3), covPoint(vec3,mat4), covLinear(vec3,mat4), bool covEqual3(vec3,vec3) and covFinite3(vec3), with mat4 only a scalar field container. Produces internal static string CoverageGpuArithmeticGlsl.Source.

- [ ] **1. Write failing Inventory_words_and_precise_lowering_are_pinned and Inventory_words_match_on_device.** Use dynamic storage inputs, not constant-foldable expressions. Assert every section 3 row for both zero signs, NaN propagation, infinities, explicit F at input and result, and next-normal survival.

~~~csharp
Assert.Equal(0x80000000u, flushNegativeSmallestNormal);
Assert.Equal(0x00800001u, flushNextNormal);
Assert.Equal(0x80000000u, minPositiveNegativeZero);
Assert.Equal(0u, maxPositiveNegativeZero);
Assert.Equal(0x80000000u, clampNegativeZeroToPositiveZero);
Assert.Equal(0u, separateMultiplyAdd); // (0x3f800001 * 0x3f7ffffe) + -1
Assert.False(nanFinite);
Assert.True(nanNotEqual);
Assert.Equal(0, missingFlushOrNoContractionSites);
~~~

- [ ] **2. Run red:** build-slot --label c01-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageGpuArithmeticTests. Expect missing CoverageGpuArithmeticGlsl.
- [ ] **3. Implement the exact helper signatures above in CoverageGpuArithmeticGlsl.cs using contract sections 3, 5 and 8.** Every arithmetic result is precise and passed through an integer-mask F. Use explicit NaN propagation and sign-aware min/max, never native min/max. Add all coverage entry points to the existing compiler corpus as they are introduced. In the proof document record SPIR-V arithmetic IDs/NoContraction, emitted HLSL precise and strict flags, and emitted MSL helpers plus fast-math policy. Distinguish SPIRV-Cross's single-operation fma helpers from prohibited contraction of separate source operations.
- [ ] **4. Run green:** c_check c01 'FullyQualifiedName~CoverageGpuArithmeticTests|FullyQualifiedName~CoverageComputeArithmeticGpuTests|FullyQualifiedName~PreciseShaderCompileTests'. Root hosted filter is the same. Require exact word equality and real FXC/Metal compilation. Source qualifiers alone do not complete this task.
- [ ] **5. Commit:** render3d(coverage): pin precise GPU scalar arithmetic.

## Task 2: Exact integer representation and corrected division/root

**Files:** Create Coverage/Gpu/CoverageGpuExactWordsGlsl.cs and CoverageGpuRoundingGlsl.cs. Create/Test Gpu/CoverageGpuExactWordsTests.cs, CoverageComputeRoundingGpuTests.cs and CoverageBinary32IntegerOracle.cs. Modify the arithmetic proof document from Task 1.

**Interfaces:** Consumes Task 1 helpers. Produces GLSL `struct CovExact { uint limb[18]; int sign; }`, exact add/subtract/product/compare/bit-length operations, uint covRound32(CovExact,int powerOfTwo), float covDiv(float,float), and float covSqrt(float). Internal test entry points return both pre-flush RN32 bits and final F bits. CoverageBinary32IntegerOracle exposes static uint Divide(uint a,uint b), Sqrt(uint a), and Round(BigInteger magnitude,int exponent,int sign).

- [ ] **1. Write failing Limb_carries_midpoints_and_corrected_results_match_independent_integers.** Use a fixed boundary corpus, no random/stress corpus. Include all-ones carries, products requiring the highest used limb, exact ties of each parity, signed zero, binade neighbors, divisor exponents outside Vulkan's native range, tiny results, overflow and invalid inputs. Compare pre-flush separately from F. Native test injection replaces only the approximate candidate with endpoint/adjacent-word values to prove correction rather than coincidental native accuracy.

  The division corpus includes input-word pairs (0x3f800000,0x40400000), (0x01000000,0x40000000), (0x01000001,0x40000000), (0x7f7fffff,0x3f000000), (0x7f000000,0x7f000000), (0x3f800000,0x7f7fffff), both zero signs and every zero/infinity/NaN divisor class. The root corpus includes 0x00800001, 0x3f7fffff, 0x3f800000, 0x3f800001, 0x40000000, 0x407fffff, 0x40800000 and 0x7f7fffff, plus both zero signs and invalid negative inputs. Test Round with magnitudes 2^24+1 and 2^24+3 at power -24, expecting 0x3f800000 and 0x3f800002.

~~~csharp
Assert.Equal(0x3eaaaaabu, divOneByThreeBeforeFlush);
Assert.Equal(0x3fb504f3u, sqrtTwoBeforeFlush);
Assert.Equal(0x00800000u, smallestNormalBeforeFlush);
Assert.Equal(0u, smallestNormalAfterFlush);
Assert.Equal(0x80000000u, sqrtNegativeZero);
Assert.Equal(0x7f800000u, correctlyRoundedOverflow);
Assert.Equal(0, limbOverflowCount);
Assert.Equal(expectedIntegerOracleWords, actualDeviceWords);
Assert.InRange(maxBracketBisections, 0, 31);
~~~

- [ ] **2. Run red:** build-slot --label c02-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageGpuExactWordsTests. Expect missing exact-word implementation.
- [ ] **3. Implement the specified representation and correction in the two new shader files, under ruling C3.** Use 32-bit limbs, umulExtended and carry comparisons, no shader float64/int64 or float error-free expansion. Decode finite binary32 coordinates as signed integers times 2^-149. Differences fit below 2^278, determinant products and their sum below 2^557, so 18 limbs (576 bits) cover the complete admitted finite-input domain. Handle signs, zero, infinity and invalid operations before finite searches.

  Use the native divide/root candidate first. Determine its exact midpoint bracket with integer residuals from contract section 6. If that bracket does not contain the exact result, expand an ordinal-word bracket by powers of two, saturated at positive-word endpoints 0 and 0x7f800000, then bisect to adjacent words. At most 31 bisections cover that domain. Use the exact finite overflow midpoint, not arithmetic with an infinity endpoint. This supplies a universal proved bound without assuming one-neighbor D3D11 correction or inventing a Vulkan root ULP bound. Metal keeps its native correctly rounded result only after the exact bracket check confirms it. Apply F only after RN32 is selected. Record the bound, residual scaling and integer-rounding proof separately for all three backends.
- [ ] **4. Run green:** c_check c02 'FullyQualifiedName~CoverageGpuExactWordsTests|FullyQualifiedName~CoverageComputeRoundingGpuTests|FullyQualifiedName~CoverageComputeArithmeticGpuTests'. Root hosted filter is the same. Require emitted native instructions and actual pre-/post-flush words, including injected bad candidates. No numerical fallback to uncorrected native arithmetic.
- [ ] **5. Commit:** render3d(coverage): correct GPU division and square root exactly.

## Task 3: Exact predicates, B30 crossings and support queries

**Files:** Create Coverage/Gpu/CoverageGpuPredicatesGlsl.cs and CoverageGpuSupportGlsl.cs. Create/Test Gpu/CoverageComputePredicateGpuTests.cs and CoverageComputeSupportGpuTests.cs. Modify/Test Render3D/CoverageReferencePredicateTests.cs:1-141 only to share independent fixture data. Modify the arithmetic proof document.

**Interfaces:** Consumes CovExact, covRound32, covDiv/covSqrt and scalar helpers. Produces GLSL `struct CovPredicate { int sign; float value; }`, CovPredicate covEdge(vec3 a,vec3 b,vec3 p), covDot(vec3 a,vec3 b,vec3 c,vec3 d), float covCrossing(vec3 s,vec3 t,vec3 a,vec3 b), int covCell(float value,float min,float max), and bool covHeight(uint support,vec2 point,out float height,out int sourceTriangle), covBoundary(uint support,vec2 point,out float height,out float distance), covOverlay(uint support,vec3 a,vec3 b,vec3 c,out float maximum). The uint support argument is a static-word offset.

- [ ] **1. Write failing Exact_predicates_and_grid_preserve_discrete_decisions_on_device.** Reuse the independent BigInteger predicate oracle and B21/B27 regression inputs, B30 opposite tiny sides, exact endpoint/collinear cases, exact binary32 midpoint, large exponent separation and B16 AABB/cell neighbors. Assert sign independently of a rounded or flushed zero.

~~~csharp
Assert.Equal(-1, nonzeroTinyPredicate.Sign);
Assert.Equal(0x80000000u, nonzeroTinyPredicate.ValueBits);
Assert.Equal(0, exactZero.Sign);
Assert.Equal(0u, exactZero.ValueBits);
Assert.Equal(0x3f000000u, oppositeEqualTinySidesCrossing);
Assert.Equal(15, cellAtGlobalMaximum);
Assert.Equal(0, cellForNaN);
Assert.Equal(expectedFirstSourceTriangle, actualFirstSourceTriangle);
Assert.Equal(expectedBigIntegerSignAndWords, actualSignAndWords);
~~~

- [ ] **2. Run red:** build-slot --label c03-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageComputePredicateGpuTests. Expect missing predicate shader entry points.
- [ ] **3. Implement the exact signatures in their named files using contract sections 4, 7, 8 and 9.** Evaluate exact differences/products in limbs, scale both exact B30 sides before rounding with k = 299 - bitLength(maxMagnitude) for the 2^-298 determinant representation, then F and the pinned subtract/divide. Preserve all 16x16 grid candidates, inclusive AABBs and source order. Boundary ties keep the first edge. Overlay uses exact signs and the specified binary32 crossing/height trees. Do not reinstate B22/B24 bounds heuristics or replace the grid with a full scan.
- [ ] **4. Run green:** c_check c03 'FullyQualifiedName~CoverageComputePredicateGpuTests|FullyQualifiedName~CoverageComputeSupportGpuTests|FullyQualifiedName~CoverageReferencePredicateTests|FullyQualifiedName~CoverageReferenceCrossingTests'. Root hosted filter is the same. Append native limb/predicate lowering evidence to the proof.
- [ ] **5. Commit:** render3d(coverage): port exact GPU predicates and support queries.

## Task 4: Cold packed inputs and pinned CPU uniforms

**Files:** Create Coverage/Gpu/CoverageGpuLayout.cs, CoverageGpuUpload.cs and Coverage/CoverageCommandUniforms.cs. Modify Coverage/CoverageReferenceFrames.cs:13-33 and CoverageReferenceSkinning.cs:36-55. Create/Test Render3D/CoverageGpuUploadTests.cs and CoverageCommandUniformTests.cs.

**Interfaces:** Consumes new CoverageReferenceTopology(CoverageRefinement), new CoverageReferenceSkin(CoverageSourceTopology,int), CoverageSupportSurface.ReferenceData and Matrix4x4 CoverageReferenceSkinning.Compose(in Matrix4x4 jointWorld,in Matrix4x4 inverseBind). Produces internal static CoverageGpuHostUpload CoverageGpuUpload.Host(CoverageRefinement topology,int boneCount), CoverageGpuSupportUpload Support(CoverageSupportSurface support), and void WriteModel(in ModelVertex vertex,Span<uint> words). Upload records own uint[] Words and typed uint offsets. Produces CoverageCommandUniforms(CoverageRefinement topology,int boneCount), bool TryPrepare(ReadOnlySpan<Matrix4x4> palette,ReadOnlySpan<Matrix4x4> inverseBind,in Matrix4x4 model,ReadOnlySpan<CoverageCommandPose> commands,out CoverageFailure failure), and read-only ComposedPalette, Frames, Order and RegionCommands spans.

- [ ] **1. Write failing Cold_uploads_and_uniforms_preserve_reference_words_and_rows.** Assert complete arrays against the Part B producers, not a second packer. Include duplicate shading corners, reversed edge recipes, source aliases, four cold-validated integer bone indices, 32-bit indices and reordered command submissions.

~~~csharp
Assert.Equal(16, CoverageGpuLayout.ModelWords);
Assert.Equal(64, CoverageGpuLayout.ModelBytes);
Assert.Equal(257, support.CellOffsets.Length);
Assert.Equal(reference.CoreFaces, unpackedCoreFaces);
Assert.Equal(reference.ShadingCorners, unpackedShadingCorners);
Assert.Equal(reference.VertexCorners, unpackedVertexCorners);
Assert.Equal(referencePaletteWords, uploadedPaletteWords);
Assert.Equal(referenceFrameWords, uploadedFrameWords);
Assert.Equal(new[] { 1, 0 }, sortedOrderForCommands20Then10);
Assert.Equal(0, changedReferenceHashes); // all 144 rows
~~~

- [ ] **2. Run red:** build-slot --label c04-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageGpuUploadTests|FullyQualifiedName~CoverageCommandUniformTests'. Expect missing packing/uniform types.
- [ ] **3. Implement the exact interfaces and layout constants above, including all shared interfaces in this plan.** Copy topology/support outputs from contract section 2, including SourceIndices, MaxSlope, ordered boundaries, group representatives and all CSR rows. Build the two per-material pulling tables from refined triangles' SourceTriangle provenance. Check size arithmetic before allocation and preserve public topology limits.

  Add a scratch-independent CoverageReferenceFrames.TryPrepare overload taking topology, model, commands, Span<int> order, Span<int> regionCommands, Span<CoverageReferenceFrame> frames, CoverageReferenceArithmetic uniformArithmetic and out CoverageFailure. The old reference-scratch overload delegates to it. CoverageCommandUniforms owns only small arrays and the uniform arithmetic instance, not CoverageReferenceScratch. Compose each bone exclusively with the pinned B31 routine. Retain scalar CPU order and round-once binary64 frame preparation. Tests serialize these input words and compare them with independent integer-rounded palette products and explicit stored frame expectations on each hosted CPU platform.
- [ ] **4. Run green:** c_check c04 'FullyQualifiedName~CoverageGpuUploadTests|FullyQualifiedName~CoverageCommandUniformTests|FullyQualifiedName~CoverageReferenceOutputTests|FullyQualifiedName~CoverageReferenceSkinningTests'. Root runs this same headless filter on all hosted OS legs for cross-CPU input evidence. All 144 existing hashes stay unchanged.
- [ ] **5. Commit:** render3d(coverage): pack immutable GPU inputs and scalar uniforms.

## Task 5: Standalone batched skinning and refinement

**Files:** Create Coverage/Gpu/CoverageGpuPipeline.cs and CoverageGpuSkinRefineGlsl.cs. Extend CoverageGpuLayout.cs from Task 4 with reservations/batches and CoverageGpuGuardGlsl.cs with source/refinement events. Create/Test Gpu/CoverageComputeSkinRefineGpuTests.cs and CoverageGpuTestSession.cs.

**Interfaces:** Consumes Tasks 1 to 4 and the exact CoverageGpuPipeline/Batch/Phase signatures in Shared interfaces. Produces Reset through FillBefore, plus an internal test-only void CoverageGpuTestSession.UploadPosed(ReadOnlySpan<ModelVertex> posed,int actorSlot) and ModelVertex[] ReadBefore(int actorSlot), ReadSource(int actorSlot). Tests own submission and the final assertion drain.

- [ ] **1. Write failing Skin_and_refinement_match_reference_words and Refusal_before_Before_keeps_the_original_pose_available.** Use all frozen input cases for reference interpolation and actual reference skinning over their palettes. Add B19 protected +0/-0 aliases, alias mismatch, generated-direction cancellation, zero bone weights, subnormal copied color/UV/tangent, weights at the 1e-8 threshold and nonfinite skinning output.

~~~csharp
Assert.Equal(referenceSourceWords, deviceSourceWords);
Assert.Equal(referenceBeforeWords, deviceBeforeWords);
Assert.Equal(0x80000000u, protectedNegativeZeroWord);
Assert.Equal(0u, protectedPositiveZeroWord);
Assert.Equal((uint)CoverageFailureCode.FoldedPatch, mismatchedAlias.Code);
Assert.Equal((uint)CoverageFailureCode.UnsupportedTransform, cancelledGeneratedDirection.Code);
Assert.Equal(0u, failedRefinement.BeforePublished);
Assert.Equal(sourceWordCount, availableFallbackWordCount);
Assert.Equal(0, crossActorWordChanges);
~~~

- [ ] **2. Run red:** build-slot --label c05-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageComputeSkinRefineGpuTests. Expect missing CoverageGpuPipeline.
- [ ] **3. Implement the named setup/stage-1/stage-2 phases in CoverageGpuSkinRefineGlsl.cs and their encoding in CoverageGpuPipeline.RecordThrough.** Preserve the contract's original-record copies, canonical computed group positions and generated-attribute validation before FillBefore. Skin indices are uploaded integers. Source/refinement reducers select the first failing index with zero equipment/command/count/limit. Keep complete source storage independent from Before. Create no Scene3D route yet.
- [ ] **4. Run green:** c_check c05 'FullyQualifiedName~CoverageComputeSkinRefineGpuTests|FullyQualifiedName~CoverageCommandUniformTests'. Root hosted filter is the same, including zero-weight and signed-zero rows. Require mixed topology counts in one Y-batched dispatch.
- [ ] **5. Commit:** render3d(coverage): batch exact GPU skinning and refinement.

## Task 6: Point conformation, correction and deterministic guards

**Files:** Create Coverage/Gpu/CoverageGpuConformGlsl.cs and CoverageGpuCorrectionGlsl.cs. Modify CoverageGpuGuardGlsl.cs and CoverageGpuPipeline.cs from Task 5. Create/Test Gpu/CoverageComputeConformGpuTests.cs and CoverageComputeGuardGpuTests.cs.

**Interfaces:** Consumes contract section 4 Conform, PrepareFace/FinishFace, GatherGroup, CheckFullTarget and Transition graphs. Produces ConformGroups through StoreTransition. Guard records contain uint Code, int Slot, uint Element, LocalOrder and Count. The completed actor result contains the winning stage/code/slot/count separately from its atomic Refused word.

- [ ] **1. Write failing Conformation_and_correction_preserve_reference_order and Parallel_notes_reduce_by_reference_key_not_writer_order.** Assert every full-recession/correction/group-position word against the reference. Include a pointwise-idle core group moved by a face, reversed command submission, face rows with three local notes, cross-slot competing failures, B25 nonpositive deficit with partial strength, zero-transition over-budget, and refusal followed by a fresh success.

~~~csharp
Assert.Equal(referenceFullRecessionWords, actualFullRecessionWords);
Assert.Equal(referenceCorrectionWords, actualCorrectionWords);
Assert.Equal(CoverageFailureCode.RecessionExceeded, zeroWeightOverBudget.Code);
Assert.Equal(CoverageFailureCode.FoldedPatch, positiveDeficitPartialStrength.Code);
Assert.Equal(0u, nonpositiveDeficitCorrectionBits);
Assert.Equal(expectedReferenceFailure, reducedFailure);
Assert.Equal(2147483647, saturatedMatchingCount);
Assert.Equal(1u, refusedActorFlag);
Assert.Equal(0u, nextSuccessfulActorFlag);
Assert.Equal(0, notesLostWhenPeerFlagsRefusal);
~~~

- [ ] **2. Run red:** build-slot --label c06-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageComputeConformGpuTests|FullyQualifiedName~CoverageComputeGuardGpuTests'. Expect missing conformation/correction phases.
- [ ] **3. Implement those phases in the named files using contract sections 4 and 9.** Discover the first active representative by ascending vertex index within each group's cold candidate list. Gather floats in stored CSR order. Overlay all support triangles in their source order. Reduce face notes before any group moves, then perform full-target checks before alias copies and transition. Use production ActualClearanceCoverage and conversion +0 only. A worker can read only the prior completed-stage failure as its gate. It records its local event and atomic OR without suppressing same-phase peer work. Reduction maps the winning sorted slot through Order to original command/instance IDs and counts only matching code and slot.
- [ ] **4. Run green:** c_check c06 'FullyQualifiedName~CoverageComputeConformGpuTests|FullyQualifiedName~CoverageComputeGuardGpuTests|FullyQualifiedName~CoverageReferenceCorrectionTests'. Root hosted filter is the same. Require notes in different workgroups and across actors, without sleeps, retries or race-dependent expected values.
- [ ] **5. Commit:** render3d(coverage): preserve GPU correction and refusal order.

## Task 7: Normals, atomic select and complete device-equivalence gate

**Files:** Create Coverage/Gpu/CoverageGpuNormalsGlsl.cs and CoverageGpuSelectGlsl.cs. Modify CoverageGpuPipeline.cs and CoverageGpuGuardGlsl.cs from Tasks 5/6. Create/Test Gpu/CoverageComputeEquivalenceGpuTests.cs and CoverageComputeSelectGpuTests.cs. Create the GPU correctness proof document.

**Interfaces:** Produces ValidateVertices through Select, completing void CoverageGpuPipeline.Record(IGpuCommandList,CoverageGpuBatch). Test session adds ModelVertex[] ReadFinal(int actorSlot,bool sourceFallback), CoverageFailure ReadFailure(int actorSlot), and uint ReadRefusedFlag(int actorSlot). Public scene integration is still absent.

- [ ] **1. Write failing Every_production_fixture_matches_every_reference_word and A_late_gather_failure_publishes_only_the_complete_fallback.** For all 72 cases compare fields in vertex order, preserving zero bits. First assert the 144-row reference hash test still passes, then compare the 72 production GPU outputs to reference outputs and their mode-0 hashes. Include all six B39 certification negatives as successful construction comparisons. Add construction-negative parity for all codes 3 to 8, count/ID attribution, face orientation disagreement, duplicate shading corners, tangent W=-1 and protected/no-motion record copies.

~~~csharp
Assert.Equal(72, comparedProductionCases);
Assert.Equal(6, comparedCertificationNegativeCases);
Assert.Equal(0, differingVertexWords);
Assert.Equal(expectedProductionHash, actualGpuHash);
Assert.Equal(referenceFailure, actualFailure);
Assert.Equal(referenceOriginalPoseWords, refusedSourceSectionWords);
Assert.Equal(0, publishedCandidateDrawsOnRefusal);
Assert.Equal(0, unexpectedGpuSkips);
~~~

- [ ] **2. Run red:** build-slot --label c07-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageComputeEquivalenceGpuTests|FullyQualifiedName~CoverageComputeSelectGpuTests'. Expect incomplete normal/select phases.
- [ ] **3. Port contract section 4 normals without deduplicating or reordering corners.** Validate all vertices, evaluate face-local basis/area results, then serially reduce faces in triangle order with per-slot orientation signs. Only after that succeeds, gather normals from ShadingCorners and tangents from VertexCorners. Recompute each basis with the pinned graph, retain tangent W, and preserve unchanged full records. Final reduction rejects the entire actor on a later failed gather. Select copies the complete canonical Published surface or complete source pose and writes the single outcome header. It never certifies geometry.

  Test the standalone construction path with frozen Posed data, and separately the complete skin-through-select path against CoverageReferenceSkinning.TrySkin followed by CoverageReferenceConstructor.TryConstruct on the same uploaded input words. Do not compare a recomputed skinning pose against the manifest for a different frozen Posed input.
- [ ] **4. Run green:** c_check c07 'FullyQualifiedName~CoverageComputeEquivalenceGpuTests|FullyQualifiedName~CoverageComputeSelectGpuTests|FullyQualifiedName~CoverageReferenceOutputTests|FullyQualifiedName~CoverageOracleTests'. Root hosted filter adds all earlier CoverageCompute classes. Record exact commits, all 72 case IDs, backend/device, shader hashes and Vulkan sync results in the correctness proof. This task's three-backend word proof is a hard gate before Tasks 8 to 17.
- [ ] **5. Commit:** render3d(coverage): prove exact GPU surfaces before scene integration.

## Task 8: Conservative actor bounds and origin admission

**Files:** Create Coverage/CoverageActorBounds.cs. Modify Coverage/CoverageSurfaceBounds.cs:8-75 only to construct conservative CPU bounds without GPU vertices. Create/Test Render3D/CoverageActorBoundsTests.cs and CoverageOriginGuardTests.cs.

**Interfaces:** Consumes composed B31 palettes, cold source bounds and cold per-influence weight ranges. Produces CoverageActorBounds(CoverageSourceTopology source), MeshBounds BoundSkin(ReadOnlySpan<Matrix4x4> composedPalette), MeshBounds BoundRefinement(in MeshBounds skin), MeshBounds ExpandWorld(in MeshBounds skin,in Matrix4x4 model,ReadOnlySpan<CoverageCommandPose> commands), double OriginError(in MeshBounds skin,in Matrix4x4 model,Vector3 renderOrigin,ReadOnlySpan<CoverageCommandPose> commands), static CoverageScalarBounds Round32Envelope(CoverageScalarBounds value), and static bool Allows(double error,float clearance). Bounds computations are CPU preparation, not a reference construction.

- [ ] **1. Write failing Palette_escape_and_rebasing_have_conservative_bounds, Origin_error_equal_to_margin_is_admitted, and Every_command_margin_is_checked_in_sorted_order.** Include translated/scaled/sheared palettes, zero/unnormalized signed weights admitted by the existing skin contract, model shear, far zero origin, and rebased positives. Independently skin the bounded test fixture only in the test to check containment.

~~~csharp
Assert.True(allActualSourceAndReferenceFinalVerticesInsideBounds);
Assert.True(boundAfterBoneTranslation.Max.X >= 100f);
Assert.Equal(joinedFormBound, nonnegativeHostBound);
Assert.True(CoverageActorBounds.Allows(.015625d, .0625f));
Assert.False(CoverageActorBounds.Allows(Math.BitIncrement(.015625d), .0625f));
Assert.True(nearAccepted);
Assert.False(farUnrebasedAccepted);
Assert.True(farRebasedAccepted);
Assert.Equal(CoverageFailureCode.UnsupportedTransform, originRefusal.Code);
Assert.Equal(10u, tighterSortedCommandFailure.CommandId);
~~~

- [ ] **2. Run red:** build-slot --label c08-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageActorBoundsTests|FullyQualifiedName~CoverageOriginGuardTests'. Expect missing bound producer.
- [ ] **3. Under ruling C1, implement the exact interfaces in CoverageActorBounds.cs.** For a host whose weights are all nonnegative, use C1's per-bone joined form. The per-slot form below, with source-coordinate bounds and four influence-weight intervals, applies only to a host with a negative weight. Per frame enclose palette coefficients, the pinned left-associated weighted matrix sums and point transforms with outward CoverageIntervals operations. Round32Envelope encloses RN32 followed by F at each inventory operation and uploaded-field read, including signed zero and the inclusive flush threshold. Do not treat an outward binary64 interval as a binary32-rounding bound. Include the identity branch at the skin total threshold. This is O(bones) and fixed interval work, without a per-vertex CPU loop.

  BoundRefinement expands each axis by gamma6 times its maximum absolute endpoint plus 6*2^-126, outward, with gamma6=6u/(1-6u) and u=2^-24. This encloses the fixed positive-weight interpolation graphs and their flushes without a per-recipe frame loop. Check that bound against independent reference interpolation in tests, including near-float-limit and subnormal inputs. ExpandWorld and OriginError start from this refined envelope, not only the source skin box. Transform it to world space and expand each axis by max(command.MaxWorldRecession), rounded outward. Disjoint commands do not accumulate, so use maximum, not sum. Reuse this one bound for culling, caster classification, coverage sphere and rejection rectangles.

  For origin admission bound the model dot's seven rounded operations with u=2^-24 and gamma7=7u/(1-7u). Use outward binary64 arithmetic to enclose origin-subtraction residual plus sqrt(3) times (gamma7 times the maximum absolute-product sum plus 7 times 2^-126). Expand the local coordinate domain by max recession times the outward column norms of the admitted inverse actor transform before evaluating that product sum. This covers world-bounded recession even under scale/shear. Reject nonfinite bounds. Allows compares error <= (double)clearance * .25d. Refuse the first sorted command whose margin is exceeded, with UnsupportedTransform and its IDs. Document this magnitude guard's scope without claiming the removed render-pass certificate.
- [ ] **4. Run green:** c_check c08 'FullyQualifiedName~CoverageActorBoundsTests|FullyQualifiedName~CoverageOriginGuardTests'. Root includes this filter in the final hosted CPU/input proof. No GPU readback or rest-sphere heuristic is used by the production bound.
- [ ] **5. Commit:** render3d(coverage): bound GPU actors and admit render origins conservatively.

## Task 9: Shared resources, CPU staging and capability fallback

**Files:** Create Coverage/Gpu/CoverageGpuResources.cs and Scene3D.CoverageGpu.cs. Modify Coverage/SkinnedHostConformer.cs:10-32,40-67, CoverageFrameArena.cs:10-87 and SkinnedCoverageFrame.cs:3-20. Modify Scene3D.Coverage.cs:9-124, .CoverageUpload.cs:37-103, .CoverageEquipment.cs:26-95, .CoverageSurfaces.cs:61-113, .SkinnedSubmission.cs:32-95, .FrameView.cs:71-83, .FramePrepare.cs:20-60 and Scene3D.cs:741-749,1420-1434. Modify Attachments.Render3D/HostCoverageBinding.cs:44-125, IAttachmentCoverageScene.cs:11-26 and Scene3DAttachmentScene.cs:1-45. Modify/Test Render3D/CoverageFrameTests.cs:1-44, CoverageResourceTests.cs:1-209, CoverageAttachmentBindingTests.cs:1-374 and CoverageAttachmentLifetimeTests.cs:1-428 at staging/equip assertions. Modify CoverageCaptureDevice.cs:1-43 and CoverageSceneFixture.cs:1-199 at their device/conformer setup. Create/Test Render3D/CoverageGpuStagingTests.cs.

**Interfaces:** Keep public SkinnedHostConformer.TryPrepare(ReadOnlySpan<Matrix4x4>,in Matrix4x4,ReadOnlySpan<CoverageCommandPose>,MotionKey,CoverageFallback,out SkinnedCoverageFrame,out CoverageFailure) -> bool. Add public void SkinnedHostConformer.ReserveSupports(ReadOnlySpan<CoverageSupportSurface> supports), called only at cold equip. Add default-implemented void IAttachmentCoverageScene.ReserveCoverageSupports(SkinnedHostConformer conformer,ReadOnlySpan<CoverageSupportSurface> supports) forwarding to the conformer, preserving third-party implementer compatibility. Append CoveragePresentation.Dispatched=3. Replace CoveredHostPreparationCount with public int DispatchedHostPreparationCount. Produce private void ReserveCoverageGpuFrame() and void ResolveCoverageGpu(IGpuCommandList commands). Same-frame GetCoverageOutcome returns Pending before resolution, Dispatched after encoding, or a CPU/capability UncoveredFallback.

- [ ] **1. Write failing Begin_latches_origin_before_opening_the_arena, Accepted_staging_copies_inputs_without_constructing_vertices, and Capability_failure_is_reported_once_and_uses_vertex_skinning.** Keep identity/completeness tests and transactional equip rollback. Force no-compute and shader-build failure separately.

~~~csharp
Assert.Equal(new[] { "LatchOrigin", "OpenCoverageArena" }, observedBeginOrder);
Assert.Equal(0, cpuConstructOrCertifyCalls);
Assert.Equal(CoveragePresentation.Pending, staged.Presentation);
Assert.Equal(CoveragePresentation.Dispatched, resolved.Presentation);
Assert.Equal(1, capabilityReportCountAfterTwoFrames);
Assert.Equal(1, kernelBuildAttemptCountAfterTwoFrames);
Assert.Equal(0, dispatchesWithoutCapability);
Assert.True(ordinaryVertexSkinningSelected);
Assert.Equal(frozenPaletteWords, uploadedPaletteWordsAfterCallerMutation);
Assert.Equal(previousEquipmentIdentity, identityAfterFailedReplacement);
~~~

- [ ] **2. Run red:** build-slot --label c09-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageGpuStagingTests. Expect missing Dispatched lifecycle and GPU resources.
- [ ] **3. Implement the interfaces above.** Share one immutable host upload per exact source/map/refinement identity and support upload per exact support identity. Actors own reservations into preallocated work and double-final arenas. Grow and retire buffers at conformer creation, cold ReserveSupports/equip or size-aware PrepareFrame, never during recording. Keep transactional acquisition and release on equip failure, conformer disposal and host unload.

  Keep the existing Begin order, which already latches origin before BeginFrameView opens the arena, and pin it with the test. Stage only identity checks, command uniforms, B31 palettes, bounds and immutable snapshots. Do not populate Posed/Before/Candidate CPU arrays, which are deleted with their final unused consumers in Task 15. Replace the old preparation/resolution calls with ReserveCoverageGpuFrame and ResolveCoverageGpu. ComputeCoverageSphere now encloses Task 8's world bound, with outward float radius conversion, instead of scanning candidate vertices. Complete host/equipment participation and resource liveness are resolved once before dispatch. An incomplete request selects CPU UncoveredFallback, not a partial batch entry.

  Create kernels once. Catch a kernel-build failure once, dispose provisional resources, report UnsupportedAdapter as the capability reason, and retain ordinary vertex-shader skinning for all requested hosts independent of UseGpuSkinning. Never call CoverageReferenceConstructor or retry kernel creation per frame. Keep already staged equipment drawing normally.
- [ ] **4. Run green:** c_check c09 'FullyQualifiedName~CoverageGpuStagingTests|FullyQualifiedName~CoverageFrameTests|FullyQualifiedName~CoverageResourceTests|FullyQualifiedName~CoverageAttachmentBindingTests|FullyQualifiedName~CoverageAttachmentLifetimeTests'. Update living docs alongside the APIs. Tests changing global capabilities/logging join a DisableParallelization collection.
- [ ] **5. Commit:** render3d(coverage): stage GPU requests and retain capability fallback.

## Task 10: Main and outline vertex pulling

**Files:** Create Rendering/CoveragePulledResources.cs and Internal/ShaderSources.CoveragePulling.cs. Modify Rendering/ModelRenderer.Coverage.cs:9-49, TargetOutlineRenderer.Coverage.cs:8-20, Scene3D.CoverageSurfaces.cs:13-55, .CoverageOutlines.cs:21-32, .SkinnedSubmission.cs:16-108 and .SkinnedDrawRecords.cs:1-82. Create/Test Gpu/CoverageComputePullingGpuTests.cs. Modify/Test Render3D/CoverageMixedRouteTests.cs:1-71 and CoverageOutlineTests.cs:1-25 at prepared-route assertions.

**Interfaces:** Produces internal readonly record struct CoveragePulledDraw(CoverageGpuSurfaceView Surface,int FirstCorner,int CornerCount,uint InstanceSlot,MotionKey Actor,PointShadowCasterSphere Sphere,bool VisibleMain,ShadowCastKind ShadowKind). Produces void ModelRenderer.DrawCoverage(IGpuCommandList commands,in CoveragePulledDraw draw,IGpuResourceSet? material), and void TargetOutlineRenderer.EnqueueCoverage(in CoveragePulledDraw draw,int drawIndex,Matrix4x4 world,float alphaCutoff,float dissolve,bool complement,Vector3 origin). Equipment uses its existing draw methods.

- [ ] **1. Write failing Main_and_both_outline_masks_pull_one_selected_surface and Early_refinement_refusal_draws_original_triangles_in_all_materials.** Compare the actual GPU buffer header/offset identities and pixel readbacks against a CPU reference capture. Include two materials with nonzero firstCorner, outline-only, two actors, opposite global skinning settings and a failed generated-normal interpolation.

~~~csharp
Assert.Equal(currentSurfaceBuffer, mainSurfaceBuffer);
Assert.Equal(currentSurfaceBuffer, fullOutlineSurfaceBuffer);
Assert.Equal(currentSurfaceBuffer, visibleOutlineSurfaceBuffer);
Assert.Equal(0, coveredCpuVertexUploads);
Assert.Equal(0, coveredOrdinarySkinningDraws);
Assert.Equal(originalPosePixels, forcedEarlyRefusalPixels);
Assert.Equal(0, fallbackExtraVisibleTriangles);
Assert.Equal(0, finalSurfaceWordMismatches);
~~~

- [ ] **2. Run red:** build-slot --label c10-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageComputePullingGpuTests. Expect missing pulled draw route.
- [ ] **3. Implement the exact draw interfaces and the shared pulling layout.** Record CoverageGpuPipeline.Record before any coverage draw. Bind final buffers as read-only vertex storage, not IA vertex buffers. Pull every ModelVertex field with the shared index/outcome rule, compile vertex position arithmetic precisely, and reuse existing main/outline material and dissolve fragments. Read zero-based gl_VertexIndex plus the explicit firstCorner uniform. Keep fragment interpolants contiguous on D3D11. Declare no unused IA attributes and carry no old IA-signature liveness sink into the pulled vertex programs. A GPU refusal stays in the pulled route and never waits for a CPU route switch. CPU admission/capability failures use the ordinary route.
- [ ] **4. Run green:** c_check c10 'FullyQualifiedName~CoverageComputePullingGpuTests|FullyQualifiedName~CoverageMixedRouteTests|FullyQualifiedName~CoverageOutlineTests|FullyQualifiedName~D3D11FxcValidationTests'. Root hosted filter is the same. Record pixel evidence as well as buffer identities.
- [ ] **5. Commit:** render3d(coverage): pull selected GPU surfaces in main and outlines.

## Task 11: Cascades and every point-shadow face

**Files:** Create Rendering/ModelRenderer.CoverageShadows.cs. Extend Internal/ShaderSources.CoveragePulling.cs from Task 10. Modify Scene3D.ShadowCasters.cs:305-490, .CascadeShadowPass.cs:15-70, .PointShadowPass.cs:35-340 and Rendering/PointShadowRenderer.Skinned.cs:133-180 at BeginGpuSkinnedFace through DrawCpuSkinnedCaster. Modify/Test Gpu/CoverageShadowGpuTests.cs:1-46 and CoverageGpuScene.cs:95-165. Create/Test Gpu/CoverageComputeShadowGpuTests.cs.

**Interfaces:** Consumes CoveragePulledDraw and its common surface. Produces void ModelRenderer.DrawCoverageCascade(IGpuCommandList commands,in CoveragePulledDraw draw,int cascade,bool dissolve,bool complement) and void PointShadowRenderer.DrawCoverageCaster(IGpuCommandList commands,in CoveragePulledDraw draw,uint casterSlot). Face/cascade uniforms remain existing prepared matrices.

- [ ] **1. Write failing Every_cascade_and_point_face_uses_the_selected_surface.** Exercise four cascades, six point faces, dynamic base and static-light transient rows, shadow-only and main-camera-culled actors, full/refused surfaces, dissolve/complement and multiple material offsets.

~~~csharp
Assert.Equal(4, comparedCascades);
Assert.Equal(6, comparedFacesPerPointRow);
Assert.Equal(0, shadowSurfaceOffsetMismatches);
Assert.Equal(0, uncoveredGhostEnvelopePixels);
Assert.Equal(referenceFallbackDepthWords, refusedShadowDepthWords);
Assert.True(shadowOnlyActorHasCoverageCaster);
Assert.False(shadowOnlyActorHasMainDraw);
~~~

- [ ] **2. Run red:** build-slot --label c11-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageComputeShadowGpuTests. Expect absent pulled shadow consumers.
- [ ] **3. Implement the exact cascade/point interfaces.** Add a third skinned geometry list rather than forcing a global CPU/GPU selection. Use Task 8's expanded CPU bound for scheduling and shells. Pull the same current-final header and fields in every face/cascade vertex program, preserving existing shadow mapping, alpha and dissolve behavior. Cache no original-host shadow silhouette for a dispatched actor.
- [ ] **4. Run green:** c_check c11 'FullyQualifiedName~CoverageComputeShadowGpuTests|FullyQualifiedName~CoverageShadowGpuTests|FullyQualifiedName~D3D11FxcValidationTests'. Root hosted filter is the same. Pixel probes bind a different framebuffer before any compute sampling.
- [ ] **5. Commit:** render3d(coverage): share pulled surfaces across shadow passes.

## Task 12: GPU final history, motion and local transition rejection

**Files:** Modify Coverage/CoverageMotionHistory.cs:8-145, Scene3D.CoverageMotion.cs:11-210, .MotionTarget.cs:115-175, Rendering/CoverageTransitionMask.cs:10-165 and Internal/ShaderSources.CoveragePulling.cs. Create Internal/ShaderSources.CoverageTransition.cs. Extend CoverageGpuSelectGlsl.cs. Create/Test Gpu/CoverageComputeHistoryGpuTests.cs. Modify/Test Render3D/CoverageMotionTests.cs:1-312, CoverageDissolveHistoryTests.cs:1-72, CoverageTransitionTests.cs:1-268, CoverageTransitionLargeOriginTests.cs:1-126 and CoverageTemporalReservationTests.cs:1-109 at final-position/history assertions.

**Interfaces:** Keep CoverageMotionHistory as CPU metadata only. Its Surface becomes (MotionKey Actor,ulong Revision,CoverageGpuReservation Reservation,Matrix4x4 Model,CoverageSurfaceBounds Bounds,bool Presented). Produces bool TryGetPrevious(MotionKey actor,ulong revision,out Surface surface), void Record(in Surface surface), void Begin(long frameIndex,object viewIdentity,bool compatible), void DiscardPending(), void Commit() and void Reset(). CoverageTransitionMask adds void AddGpuChangedBounds(in CoverageChangedBounds bounds,IGpuBuffer state,uint transitionWordOffset). Select writes uint TransitionChanged in current-final metadata. Produces void ModelRenderer.DrawCoverageMotion(IGpuCommandList commands,in CoveragePulledDraw draw,in Matrix4x4 previousRenderModel).

- [ ] **1. Write failing Held_readback_refusal_and_recovery_seed_history_locally, Previous_final_positions_rebase_to_the_current_origin, and Unpresented_or_other_view_surfaces_are_not_history.** Cover first use, equip, unequip, replacement, failed replacement, refusal/recovery, actor incarnation, skipped frame, camera identity change, missing material, shadow/outline-only, total dissolve, near-plane crossing and a failed encoding. Hold readback via a fake completion, without sleeping.

~~~csharp
Assert.Equal(currentFinalPositionWords, seededPreviousWords);
Assert.Equal(previousActuallyPresentedWords, steadyPreviousWords);
Assert.Equal(0, cpuFinalPositionCopies);
Assert.Equal(0, unrelatedActorRejectedPixels);
Assert.Equal(0, unrelatedBackgroundRejectedPixels);
Assert.True(actorTransitionBoundsRejected);
Assert.True(nearPlaneCrossingRejectsViewport);
Assert.Equal(0, globalHistoryResetCalls);
Assert.False(otherViewPreviousReused);
Assert.False(abandonedEncodingCommittedHistory);
Assert.Equal(expectedRebasedMotionWords, actualMotionWords);
~~~

- [ ] **2. Run red:** build-slot --label c12-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageComputeHistoryGpuTests. Expect CPU-only history and no GPU-conditional rejection.
- [ ] **3. Implement the exact interfaces above.** Retain two final allocations and their outcome metadata per actor. Select sees prior GPU outcome plus CPU identity/validity bits. On invalid identity, absence, refusal change or recovery it seeds previous from the complete selected current surface and sets TransitionChanged. Steady successful motion reads the previous actually presented final positions. Compare previous/current GPU outcome directly, independent of delayed diagnostics.

  CPU history eligibility uses consecutive frame, view object identity, actor/revision, complete visible main participation and finite conservative rebased bounds. Current origin rebases the stored previous absolute model. A source/refined topology transition invalidates correspondence and seeds current positions plus the matching selected-kind header. Only successful complete encoding marks a main-presented surface as pending. Task 13 commits it after successful submission and discards it on abandonment, independently of diagnostic-ring availability. Shadow-only, outline-only and fully discarded draws never become presented history.

  Extend the existing R8 mask's rectangle draw with a vertex-stage state read. CPU supplies the conservative union of current/previous bounds for each potentially changing actor. The GPU predicate emits the rectangle only for TransitionChanged. CPU-known unequip/removal still emits unconditionally. Near-plane uncertainty uses a viewport rectangle for that transition. The mask is drawn after select and before temporal resolve. No CPU flag read, global reset or always-on actor rectangle is allowed.
- [ ] **4. Run green:** c_check c12 'FullyQualifiedName~CoverageComputeHistoryGpuTests|FullyQualifiedName~CoverageMotionTests|FullyQualifiedName~CoverageDissolveHistoryTests|FullyQualifiedName~CoverageTransitionTests|FullyQualifiedName~CoverageTransitionLargeOriginTests|FullyQualifiedName~CoverageTemporalReservationTests'. Root hosted filter is the same plus existing CoverageTemporalGpuTests. Verify both temporal precision paths and the resolved image/mask, not only motion records.
- [ ] **5. Commit:** render3d(coverage): retain GPU final history and reject local transitions.

## Task 13: Stable submission completion and asynchronous outcomes

**Files:** Create KhaozEngine.Gpu/GpuFrameSubmission.cs and IGpuFrameCompletionConsumer.cs, KhaozEngine.Windowing/Frame.Completion.cs and Scene3D.CoverageOutcomes.cs. Modify KhaozEngine.Windowing/Frame.cs:8-10 to partial, FramePhases.cs:43-80 and AppWindow.cs:743-756 at Dispose, Render3DSurface.cs:57-61, Render3DPreview.cs:117-130, Render3DSnapshotRunner.cs:37-60 and Scene3D.CoveragePrepare.cs:12-22. Create/Test Gpu/GpuFrameSubmissionTests.cs, Render3D/CoverageOutcomeTests.cs and Gpu/CoverageComputeOutcomeGpuTests.cs. Modify/Test Gpu/TemporalFixture.cs:1-149 at its frame submission.

**Interfaces:** Under ruling C2, produce public interface IGpuFrameCompletionConsumer with bool HasPending(IGpuFence completion), void DiscardUnsubmitted(IGpuFence completion) and void ReleaseCompleted(IGpuFence completion). ReleaseCompleted is teardown-only, consumes completed staging entries and may discard their diagnostic payloads, and never treats submitted work as unsubmitted. In the same interface file produce public interface IGpuFrameSubmissionParticipant with void Submitted() and void Abandoned(). Produce public sealed class GpuFrameSubmission : IDisposable with constructor (IGpuDevice device,int retainedFrames=3,int consumerCapacity=8), void Begin(), bool TryRegister(IGpuFrameSubmissionParticipant participant), bool TryRetain(IGpuFrameCompletionConsumer consumer,out IGpuFence? completion), void Submit(IGpuCommandList commands), void Abandon(), and void Dispose(). Frame exposes `public GpuFrameSubmission Submission { get; }`. Produce public readonly record struct CoverageGpuOutcome(ulong FrameId,ulong ViewId,MotionKey Actor,CoverageFrameOutcome Outcome), public bool Scene3D.TryReadCoverageOutcome(out CoverageGpuOutcome outcome), and internal void Scene3D.RecordCoverageReadbacks(IGpuCommandList commands,GpuFrameSubmission submission).

- [ ] **1. Write failing Held_completion_keeps_frame_actor_and_fence_identity, Full_ring_drops_only_diagnostics, and Abandon_or_reuse_never_relabels_an_old_actor.** Use three retained frames, two scenes sharing one submission, actor-slot reuse and a held first fence. Native D3D11 case drains once after submission before its one successful poll.

~~~csharp
Assert.Equal(CoveragePresentation.Dispatched, sameFrameOutcome.Presentation);
Assert.False(scene.TryReadCoverageOutcome(out _));
Assert.Equal(0, productionWaitCalls);
Assert.Equal(0, productionFlushCalls);
Assert.Equal(1, actualSubmissionCount);
Assert.Equal(submissionFence, bothScenesRecordedFence);
Assert.Equal(100ul, firstRead.FrameId);
Assert.Equal(oldActorKey, firstRead.Actor);
Assert.Equal(CoveragePresentation.UncoveredFallback, firstRead.Outcome.Presentation);
Assert.Equal(CoverageFailureCode.CapContact, firstRead.Outcome.Failure.Code);
Assert.Equal(0, fenceResetsBeforeAllConsumersRelease);
Assert.Equal(0, submittedEntriesDiscardedByAbandon);
Assert.True(renderingContinuedWithFullRing);
Assert.True(historyCommittedWithoutDiagnosticSlot);
Assert.False(historyCommittedAfterAbandon);
~~~

- [ ] **2. Run red:** build-slot --label c13-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~GpuFrameSubmissionTests|FullyQualifiedName~CoverageOutcomeTests'. Expect missing submission/result APIs.
- [ ] **3. Implement the exact interfaces above, under ruling C2.** GpuFrameSubmission owns three precreated fences and fixed consumer/participant arrays. Begin polls/reclaims only frames whose GPU completion and every consumer are finished. If no slot/consumer capacity is available, TryRetain returns false and diagnostics are skipped. Register each scene's cold-created submission-state object independently of TryRetain. Submit submits the caller list once, with the retained fence if any, then calls Submitted on registered participants. A full diagnostic ring therefore does not stop history commits. Abandon calls Abandoned on participants and discards only unsubmitted ring entries. If participant registration is full, that scene explicitly discards its pending history rather than claiming a commit, while rendering continues. Retain each fence until all associated outcome and verification rings release it. Dispose first abandons any unsubmitted recording, drains once only at host teardown, calls ReleaseCompleted on retained consumers, confirms no staging entry remains, then disposes owned fences. A consumer disposed first releases its own completed staging entries and thereafter reports HasPending=false.

  Record one aggregate actor-result buffer copy through a three-slot GpuBufferReadbackRing per scene, tagged with frame ID. Its payload includes actor incarnation/key, preparation/view identity, code, original command/instance and count. Capture host identity strings and slot mappings in fixed retained CPU metadata before submission. Never decode through a mutable CoveragePreparation after Begin. TryReadCoverageOutcome polls the ring without waiting and enumerates the retained frame's actors from preallocated payload storage. No outcome availability changes a draw or history decision.

  Wire surface, preview, snapshot and test hosts to the same submission owner. The window frame lazily creates its shared owner at the first pre-record Begin with its actual device. AppWindow.Dispose disposes that owner before the command list/device. Preview, snapshot and native test hosts own and dispose their corresponding owner explicitly. Ensure exception paths call Abandon before the next recording. Keep GPU work, aggregate copy and all consumers in the same submitted command list.
- [ ] **4. Run green:** c_check c13 'FullyQualifiedName~GpuFrameSubmissionTests|FullyQualifiedName~CoverageOutcomeTests|FullyQualifiedName~CoverageComputeOutcomeGpuTests|FullyQualifiedName~GpuBufferReadbackRingTests'. Root hosted filter is the same. Prove D3D11's outcome after the explicit test drain without adding a production flush.
- [ ] **5. Commit:** gpu(submission): retain frame completion for coverage readbacks.

## Task 14: Dev-only real-device verification

**Files:** Create Scene3D.CoverageVerification.cs. Modify Scene3D.CoverageOutcomes.cs and .CoveragePrepare.cs from Task 13. Create/Test Render3D/CoverageVerificationTests.cs and Gpu/CoverageComputeVerificationGpuTests.cs. Update KhaozEngine.Render3D/README.md and docs/USING-KHAOZENGINE.md coverage diagnostics sections.

**Interfaces:** Produce public void Scene3D.ConfigureCoverageVerification(MotionKey actor,int everyNFrames), where MotionKey.None or everyNFrames=0 disables it and negative intervals throw. Internal verification ring retains exact palette/model/commands, frame/view identity and uploaded input words per sampled slot. The sampled actor must be explicitly selected, not inferred by index.

- [ ] **1. Write failing Verification_is_off_by_default_and_logs_the_first_differing_vertex.** Use N=3, held completion, a full ring, actor replacement, exact equality, and injected differences in two vertices/fields. No reference execution occurs when disabled.

~~~csharp
Assert.Equal(0, disabledReferenceCalls);
Assert.Equal(new ulong[] { 3, 6 }, sampledFrameIds);
Assert.Equal(0, equalityMismatchLogs);
Assert.Equal(1, differingPoseLogCount);
Assert.Equal(7, firstMismatchVertex);
Assert.Equal("Position.X", firstMismatchField);
Assert.Equal(0x3f800000u, loggedExpectedWord);
Assert.Equal(0x3f800001u, loggedActualWord);
Assert.Equal(sampledPoseWords, loggedPoseWords);
Assert.Equal(0, waitOrFlushCalls);
~~~

- [ ] **2. Run red:** build-slot --label c14-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageVerificationTests. Expect missing configuration/verification route.
- [ ] **3. Implement the exact configuration signature and asynchronous comparison.** Allocate snapshot/ring/reference scratch only on opt-in or explicit cold growth. Record surface readback beside outcomes using the same stable submission fence. After completion reconstruct the pinned CPU reference for exactly that stored pose, compare field words in vertex order and log the first differing vertex, field, expected/actual bits and complete pose inputs. Compare refused outcomes to their selected source fallback as well. Ring exhaustion skips verification, never drawing. This is conformance diagnostics, not an alternate runtime surface or certification.
- [ ] **4. Run green:** c_check c14 'FullyQualifiedName~CoverageVerificationTests|FullyQualifiedName~CoverageComputeVerificationGpuTests'. Root hosted filter is the same. Real-GPU owner playtesting with this mode remains a later handoff, without an agent boot or measurement in this plan.
- [ ] **5. Commit:** render3d(coverage): verify sampled GPU surfaces asynchronously.

## Task 15: Remove the retired CPU/display path and migrate its tests

**Files:** Delete the six Coverage files and Scene3D.CoverageRenderContext.cs named in the map. Create Coverage/CoverageTally.cs by moving the existing tally and Create/Test Render3D/CoverageGpuRemovalTests.cs. Modify Coverage/CoverageCertification.cs:12-95, CoverageScratch.cs:11-85, CoverageCommandPose.cs:1-240, CoverageUploadEvidence.cs:13-39 and Scene3D.CoverageDiagnostics.cs:12-45. Modify/Test Render3D/CoverageExactCaptureTests.cs:28-80,131-150,190-245, CoverageReferenceCorrectionTests.cs:210-250, CoverageCertificationTests.cs:1-289, CoverageSafetyRegressionTests.cs:1-295 and CoverageNormalTests.cs:1-251. Replace render-certification-only tests by deleting Render3D/CoverageRenderErrorTests.cs, CoverageRenderSeparationTests.cs, CoverageRenderContextTests.cs, CoverageLateCertificationTests.cs and CoverageCandidateConstructionTests.cs. Migrate Render3D/CoverageEvaluatorTests.cs:1-268, CoverageCoreCorrectionTests.cs:1-216 and CoverageCoreOverlayEquivalenceTests.cs:1-191 to reference/oracle assertions, retaining their independent geometric negatives. Modify Gpu/CoverageCostWorkload.cs:1-428, CoverageCostGeometry.cs:1-232, CoverageCostHarness.cs:60-154, CoverageCostContractTests.cs:1-554, Render3D/CoverageCostFixtureTests.cs:1-292 and tools/EquipmentCoverage.Cost.Tests/CoverageCostTests.cs:18-70 where retired runtime/API assumptions occur. Migrate removed API call sites in Render3D/CoverageSceneFixture.cs and Gpu/CoverageGpuScene.cs:95-212, CoverageGpuActor.cs:1-183 and CoverageGpuBindingProof.cs:70-100 before deletion.

**Interfaces:** Keep public CoverageCertification.ValidateGeometry(CoverageRefinement,ReadOnlySpan<ModelVertex>,ReadOnlySpan<ModelVertex>,in Matrix4x4,ReadOnlySpan<CoverageCommandPose>,CoverageScratch,out CoverageGeometryCertificate,out CoverageFailure) -> bool and its existing no-certificate overload. Keep CoverageOracle.TryCheck exactly as Part B finally approves. Remove CoverageEvaluator, TryConstructCandidate, CoverageCertification.Validate, CoverageCertificate, CoverageRenderStamp/Pass/Context, pass-role lists and CPU surface/preparation counters. CoverageTally.Note and Succeeded retain their existing signatures and attributes.

- [ ] **1. Write failing Retired_display_and_constructor_APIs_are_absent and Frozen_captures_remain_immutable_oracle_evidence.** Use reflection for removed public types/methods and a scoped source contract for runtime calls. Replace old late-render-refusal tests with CPU origin/completeness and forced GPU refusal assertions in Tasks 9 to 12 before deleting them.

~~~csharp
Assert.Null(renderAssembly.GetType("KhaozEngine.Render3D.CoverageRenderContext"));
Assert.Null(renderAssembly.GetType("KhaozEngine.Render3D.CoverageCertificate"));
Assert.Null(renderAssembly.GetType("KhaozEngine.Render3D.CoverageEvaluator"));
Assert.Equal(0, runtimeReferenceConstructorCalls);
Assert.Equal(0, resolutionCertificationCalls);
Assert.Equal(144, unchangedReferenceManifestRows);
Assert.Equal(72, frozenCaptureCases);
Assert.Equal(66, oraclePassingFrozenCases);
Assert.Equal(6, oracleCertificationNegativeFrozenCases);
Assert.Equal(beforeFixtureDigests, afterFixtureDigests);
Assert.Throws<InvalidOperationException>(() => runOldCpuAuthorization());
Assert.Equal(0, oldAuthorizationMeasurementCallbacks);
~~~

- [ ] **2. Run red:** build-slot --label c15-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~Retired_display_and_constructor_APIs_are_absent|FullyQualifiedName~Frozen_captures_remain_immutable_oracle_evidence'. Expect retired types/call sites still present.
- [ ] **3. Perform the listed removals without deleting the offline exact certifier.** Simplify ValidateGeometry to its geometry-only path and retain its exact predicates, BVHs, frame preparation and failure precedence. Trim only scratch members made unreachable after constructor/display removal. Keep CoverageIntervals, CoverageAffine and CoverageShaderPosition where bounds, the oracle or temporal code still consume them.

  Retain frozen capture readers, manifest identity/protected witnesses and live-input provenance checks. Remove the exact-constructor writer and live constructor replay, whose outputs are now frozen evidence. Migrate exact-construction regression assertions to the reference plus frozen exact captures, and certification negatives to ValidateGeometry/CoverageOracle. Do not replace a geometry-negative test with a source-name test.

  Complete compile-time consumer migration in this task. Replace test-only CPU vertex/pass accessors with actual selected-buffer readback and recorded binding metadata. Use a test-owned assertion drain, never a production readback helper in the draw path. Task 17 still owns their native pixel verdicts. Remove _coveragePointMatrices.Clear/Add at Scene3D.PointShadowPass.cs:136,185 after capture assertions migrate, retaining the actual renderer's PackFace matrices. Keep only the small scene/view identity fields in Scene3D.CoverageGpu.cs that token/history validation needs.

  Remove obsolete CPU cost callbacks and labels from the real scene route. Preserve historical cost reports and authorized files as history. Make the old CPU authorization entry point reject before fixture/device/clock work. Keep fake manifest/report accounting tests and buildability. New GPU timing instrumentation/measurement remains outside Part C and requires fresh approval.
- [ ] **4. Run green:** c_check c15 'FullyQualifiedName~CoverageGpuRemovalTests|FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageOracle|FullyQualifiedName~CoverageCertificationTests|FullyQualifiedName~CoverageSafetyRegressionTests|FullyQualifiedName~CoverageNormalTests|FullyQualifiedName~CoverageEvaluatorTests|FullyQualifiedName~CoverageCoreCorrectionTests|FullyQualifiedName~CoverageCoreOverlayEquivalenceTests|FullyQualifiedName~CoverageCostContractTests'. Build the separate tool without running it: build-slot --label c15-cost-build -- dotnet build tools/EquipmentCoverage.Cost.Tests/EquipmentCoverage.Cost.Tests.csproj -c Release. Root hosted renderTestFilter is the same focused filter, bake=false. Full native scene/golden acceptance follows in Task 17. Sweep removed names across Markdown and update living docs in this commit, leaving historical designs explicitly superseded.
- [ ] **5. Commit:** render3d(coverage): remove CPU construction and render certification.

## Task 16: Zero-allocation and 32-actor correctness evidence

**Files:** Modify/Test Render3D/CoverageAllocationTests.cs:1-267, CoverageTemporalAllocationTests.cs:1-95 and CoverageOracleAllocationTests.cs:1-45. Create/Test Gpu/CoverageComputeBatchGpuTests.cs. Modify the correctness proof document and Scene3D.CoverageDiagnostics.cs only for counters observing actual upload/dispatch ownership.

**Interfaces:** Consumes public staging/binding, production pipeline, outcome enumeration and CoverageOracle.TryCheck. No new runtime API. Counters describe dispatched actors and uploaded frame words, never CPU certification or synchronous coverage success.

- [ ] **1. Write failing Warm_changed_poses_dispatch_resolution_and_readback_allocate_zero and Thirty_two_mixed_actors_are_independent.** Preallocate capacity, warm once, then observe two distinct poses over two actors, including one refusal/recovery and a completed readback. Keep test assertions, logging, reference output generation and test drains outside the allocation interval. Independently check all 32 actors' words and outcome keys with mixed topologies, commands and submission order.

~~~csharp
Assert.Equal(0L, stagingAllocated);
Assert.Equal(0L, dispatchAllocated);
Assert.Equal(0L, resolutionAllocated);
Assert.Equal(0L, outcomeEnumerationAllocated);
Assert.Equal(0L, oracleWarmAllocated);
Assert.Equal(32, comparedActors);
Assert.Equal(0, crossActorSurfaceOrOutcomeMismatches);
Assert.Equal(0, productionReadbackWaits);
Assert.Equal(0, warmCapacityGrowths);
~~~

- [ ] **2. Run red:** build-slot --label c16-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageAllocationTests. Expect any current hot allocation or missing production GPU observation to fail, not a no-op fake path.
- [ ] **3. Eliminate only observed hot allocations in the owned staging, packing, dispatch and readback paths.** Use existing preallocated arrays/spans and cold reservations. Put allocation-sensitive process state in the existing AllocSensitive collection. Batch 32 actors once for correctness, not a timing series. Keep capability failure and full-ring paths allocation-free after their first report.
- [ ] **4. Run green:** c_check c16 'FullyQualifiedName~CoverageAllocationTests|FullyQualifiedName~CoverageTemporalAllocationTests|FullyQualifiedName~CoverageOracleAllocationTests|FullyQualifiedName~CoverageComputeBatchGpuTests'. Root hosted filter is the same. Record allocation bytes and exact word results only, with CPU/GPU timing gates explicitly unmeasured.
- [ ] **5. Commit:** test(coverage): prove zero allocations and 32 actor GPU correctness.

## Task 17: Scene evidence, golden review and final handoff

**Files:** Modify/Test Gpu/CoverageGpuScene.cs:95-212, CoverageGpuScene.Refusal.cs:1-88, CoverageGpuScene.Temporal.cs:1-328, CoverageGpuActor.cs:1-183 at AssertCovered/Signature, CoverageGpuBindingProof.cs:70-100, CoverageSurfaceGpuTests.cs:1-73, CoverageOutlineGpuTests.cs:1-17, CoverageTemporalGpuTests.cs:1-165, CoverageTemporalKernelGpuTests.cs:1-129, CoverageGoldenTests.cs:8-28 and CoverageArtifacts.cs:1-119 at evidence metadata. Reuse CoverageGhostEnvelope.cs, CoverageMaskCompleteness.cs, CoverageHistoryNegative.cs and CoveragePixelProof.cs without weakening assertions. Modify the correctness proof and living coverage docs. Conditional golden files are the 18 exact paths defined in the map.

**Interfaces:** CoverageGpuScene capture uses the actual final-buffer readback after one test submission/drain, with CoverageGpuOutcome keyed to that capture. SubmittedSurfaceSignature hashes GPU-returned vertex words, not a CPU recomputation. Existing CoverageCapture retains colour/depth/normal/motion/shadow/outline/mask images and adds frame/view/actor IDs and the actual pulled surface offsets.

- [ ] **1. Write failing GPU_scene_evidence_preserves_surface_masks_and_negative_controls and Eighteen_goldens_remain_exact_or_have_reviewed_deltas.** Adapt old render-certification negatives into forced numerical refusal or CPU origin admission negatives, according to what they asserted. Keep independent ghost envelopes, no-hole witnesses, removal-only control, one shared scene/fresh-scene parity, cel/PBR, UV-free/normal-mapped hosts, all view angles, nonzero origins and all pass/history transitions.

~~~csharp
Assert.True(baseline.BreakthroughPixels > 0);
Assert.Equal(0, conformed.BreakthroughPixels);
Assert.Equal(0, conformed.NewBackgroundHoles);
Assert.True(conformed.ProtectedWitnessesRetained);
Assert.Equal(0, staleShadowOutlineOrHistoryPixels);
Assert.Equal(referenceWords, actualGpuReadbackWords);
Assert.Equal(6, verifiedGoldenComparisonsOnThisBackend); // 18 across the three hosted legs
Assert.Equal(0, unreviewedGoldenRefreshes);
Assert.Equal(0, loosenedToleranceValues);
~~~

- [ ] **2. Run red:** build-slot --label c17-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageSurfaceGpuTests|FullyQualifiedName~CoverageOutlineGpuTests|FullyQualifiedName~CoverageTemporalGpuTests|FullyQualifiedName~CoverageGoldenTests'. Expect obsolete CPU signatures/outcome timing or genuine GPU deltas, never permission to update references.
- [ ] **3. Adapt capture/evidence consumers to actual GPU results and preserve the existing invariants.** Remove pass-capture assertions and replace them with recorded consumer buffer/offset/role assertions and rendered targets. Tests assert same-frame Dispatched, then the delayed keyed result after their explicit assertion drain. Keep both UseGpuSkinning settings for uncovered controls, forced GPU refusal across every pass and transition, and ordinary vertex-skinning no-compute controls.

  Run all 18 golden comparisons initially with bake=false. For each result whose worst delta exceeds 0, generate a side-by-side full-resolution packet for root, naming backend, scene, before/after hashes, worst delta and the geometric explanation. Refresh only after root reviews that exact delta. Root requests bake=true only for the owning backend, then the worker commits only the reviewed artifact. If root declines or the cause is unknown, stop this task with that golden unresolved. Never regenerate the reference manifest or widen an image tolerance.
- [ ] **4. Run green:** c_check c17 'FullyQualifiedName~Coverage'. Root hosted filter='FullyQualifiedName~Coverage', legs=all, tier=push, disableGpuDiskCache=true, bake=false after any approved refresh. Require clean Vulkan sync logs and every expected GPU case executed. Run sh scripts/check-dashes.sh --tree, sh scripts/check-prose.sh --tree, sh scripts/check-file-size.sh --tree, sh scripts/check-agent-instructions.sh --tree, bash scripts/check-doc-versions.sh and git diff --check. Update the proof with exact commits, counts, lowering proofs, zero allocations, item-6 pixel results and all 18 unchanged or reviewed deltas. Build and scoped whitespace verification are included by c_check.
- [ ] **5. Commit:** test(coverage): verify GPU scene behavior and reviewed goldens.

## Root checkpoint after Part C

- Root reviews all task commits and C1 to C3 dispositions, then reconciles current main in the owning equipment branch before the original Task 10 final verification/API sweep.
- Run one full Release solution build and one full headless suite through build-slot --heavy, plus the scoped whitespace check and repository guards. Request the standard all-leg full hosted workflow with an empty renderTestFilter. Do not substitute plain dotnet test for GPU evidence.
- Part C correctness is complete only when evidence items 5 to 8 are complete, including reviewed golden decisions. Performance gates remain unmeasured and owner-gated. No release, consumer pin or real-device playtest is implied.
- Original Task 5 deferrals Q2 and Q5 become moot with removed late refusal/pass capture. Root still reviews Q4 and Q6 in the original Task 10, as amendment R directs.
- If any bit comparison fails, preserve its exact inputs, first differing word and emitted code, then fix the implementation or return a contract contradiction to root. Do not alter frozen evidence.
- If an owner later authorizes real-hardware conformance playtesting, hand them the appropriate consumer boot command with verification explicitly enabled. This plan does not boot a client.

## Self-review

- Spec Part C static topology/support/shared storage maps to Task 4 and Task 9. CPU command frames, B31 palettes, identity/completeness/capacity and origin latch/admission map to Tasks 4, 8 and 9.
- All seven numerical stages, guard codes, the one actor atomic flag, deterministic attribution and select/fallback map to Tasks 1 to 7. Extra dispatches preserve section 9 global sequencing and do not invent extra numerical phases.
- Main/outline/cascade/point-face vertex pulling maps to Tasks 10 and 11. Motion, previous FINAL seeding, current-origin rebasing, per-view identity and local transition rejection map to Task 12.
- CPU-only conservative bounds map to Task 8 under ruling C1. No-compute/build-failure fallback maps to Task 9.
- Asynchronous frame/actor outcomes and retained fence identity map to Task 13 under ruling C2. Real-device verification maps to Task 14. Ruling C3 fixes the correction-proof interpretation before Task 2.
- Every listed removal maps to Task 15. Render-certification tests are deleted or replaced by Tasks 8 to 12 and 17. Exact geometry certification remains for CoverageOracle, while frozen captures remain unchanged.
- Evidence 5 is Task 7 on all three backends before any scene integration. Evidence 6 is Tasks 8 to 13, 16 and 17. Evidence 7 is Task 16, including the oracle. Evidence 8 is Task 17 with root review before any refresh.
- Shared names, parameter types and return types agree across tasks: uint word offsets, ulong frame/view IDs, MotionKey actor identities, 16-word ModelVertex records, CoverageGpuReservation, CoverageGpuSurfaceView, CoveragePulledDraw and CoverageGpuOutcome each have one definition.
- Review Focus 1 is tested in Tasks 5, 7 and 10, Focus 2 in Tasks 6 and 7, Focus 3 in Task 12, Focus 4 in Task 8, and Focus 5 in Task 13.
- The plan gives signatures, pinned algorithms, test assertions and verification gates. It does not transcribe the contract's 57 numerical bodies. Its implementation and device/scene tests will be longer than this plan. Seventeen independent test cycles separate arithmetic, construction, rendering, temporal state, submission and evidence without adding a setup-only task.
