# Equipment Coverage Amendment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development. Continue the owner's approved execution method, with a fresh implementer and independent review for each unit below.

**Goal:** Complete Task 4 with useful whole-face conformation and a certificate grounded in actual rendering transforms.

**Architecture:** Keep the accepted sidecars, fixed topology and geometry safety checks. Add one full-weight core-face correction, then an explicit render-error context. Scene3D stages immutable geometry and resolves display certification after all pass matrices are captured, before any covered draw.

**Tech Stack:** Existing C#, System.Numerics, Render3D geometry/interval helpers, xUnit and scene fake-GPU fixtures. No new dependency.

**Spec:** [Owner-approved amendment](../../design/EQUIPMENT-COVERAGE-AMENDMENT-2026-10-16.md) and [original design](../../design/EQUIPMENT-COVERAGE-DESIGN-2026-10-16.md). This supplements the [approved ten-task plan](2026-10-16-equipment-coverage.md), not a new consumer feature or release.

**Status:** Owner approved the written amendment and continued subagent workflow. Tasks 1 to 3 are accepted. Task 4 safety corrections are separately reviewed through `afd0fbae3`. Units 4B and 4C below remain open. Execute them sequentially before completing parent Task 4.

## Global Constraints

- Preserve the rig, animations, base host, actual rigid equipment and exact protected/zero-work data.
- Fixed levels remain 1 to 4. Limits remain 4 commands, 32 regions, 1,024 support triangles, 256 boundary edges and 65,536 final vertices/triangles.
- Correct full-strength core faces once. Gather corrections by PositionGroup, not material or shading vertex.
- No global target plane, adaptive topology, convergence loop, budget increase or clamp of an uncleared core.
- A missing or stale actual render context cannot produce a drawable coverage certificate.
- One immutable resolved surface or explicit current-pose uncovered fallback serves every participating pass.
- Warm preparation, certification and submission allocate zero managed bytes. Reserve storage before recording.
- No new dependencies, private assets or changes to legacy uncovered adapter requirements.
- Every local build/test uses build-slot. No full suite per unit, random corpus, local stress or timing loop.
- Native appearance, separately authorized cost proof, release authorization and consumer motion acceptance remain separate.

## Review Focus

1. A core vertex with no pointwise work can still need a face correction. Unit 4B must prove that case.
2. A correction shared across material seams must not be applied twice or move lower-strength witnesses. Unit 4B owns it.
3. A fake origin, incomplete pass set or mismatched draw matrix must not hide render error. Unit 4C and Task 5 own it.
4. A late render refusal must not leave one pass covered and another uncovered. Task 5 owns atomic resolution.
5. Origin/view changes must reject stale certificates and temporal reuse without freezing the current actor. Tasks 5 and 7 own it.

### Unit 4B: One local core-face correction

**Files:**
- Create `KhaozEngine.Render3D/Coverage/CoverageCoreCorrection.cs` and coherent overlay helpers if required.
- Modify `CoverageEvaluator.cs` and `CoverageScratch.cs` at the full-weight construction stage.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageCoreCorrectionTests.cs` and update the old refined-level refusal expectations.

**Interfaces:**
- Internal `CoverageCoreCorrection.TryApply(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, Span<ModelVertex> fullCandidate, in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, out CoverageFailure failure) -> bool`.
- Scratch owns full requested recession and correction arrays indexed by PositionGroup, and bounded overlay workspace. Failure in scratch never writes caller output.
- Preserve the existing public evaluator signatures in this unit. Unit 4C separates geometry-only and display certification explicitly.

- [ ] **Step 1: Write `Convex_sheet_levels_2_to_4_clear` for levels 2, 3 and 4.** Use the unchanged base CurvedPanel and actual thick pyramid support. Require positive baseline breakthrough, positive core movement, zero independent clearance violations, exact protected bytes, no cracks/folds/new intersections and no open-boundary regression. Keep the independently constructed uncorrected pointwise candidate as a failing Validate control.
- [ ] **Step 2: Write `Zero_deficit_is_byte_exact`, `Incident_core_deficit_moves_a_pointwise_idle_vertex`, `Shared_group_correction_is_applied_once` and `Correction_respects_budget_caps_and_transition`.** Include already-clear flat/concave candidates, mixed material seams, lower-strength witnesses, a genuinely insufficient budget and an invalid intermediate transition surface.
- [ ] **Step 3: Run real red before implementation.** `build-slot --label coverage-correction-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageCoreCorrectionTests`.
- [ ] **Step 4: Compute the full-weight pointwise candidate and actual analytic face shortfalls.** On each core/support overlap, bound the maximum `z_face - h + C_cov`. Evaluate affine extrema over the actual overlap, carrying input, intersection and conversion uncertainty. Prove domain containment first. Numerical ambiguity refuses or yields a justified finite upper correction, never a zero-by-tolerance answer.
- [ ] **Step 5: Gather the maximum incident deficit per PositionGroup and apply it once.** Retain full requested recession before transition scaling. Include correction/conversion uncertainty in the exact-coordinate proof, then check final floats with the independent existing certificate. Enforce the unchanged total world budget and caps. Copy exactly when both work terms are zero.
- [ ] **Step 6: Format, then run focused green and affected evaluator/certification/safety tests through build-slot.** Keep zero warm allocation, scale/shear, disjoint commands and the closed-host positive. Do not weaken the oracle or replace the base fixture with a simpler one. Build Render3D Release with zero warnings and run affected guards.
- [ ] **Step 7: Commit and obtain independent review.** `feat(coverage): correct core triangle clearance shortfalls`. No overall Task 4 completion or native/cost claim yet.

### Unit 4C: Actual render-error context

**Files:**
- Create `KhaozEngine.Render3D/Coverage/CoverageRenderContext.cs`, `CoverageRenderError.cs` and focused bounded projective-arithmetic helpers as needed.
- Modify `CoverageEvaluator.cs`, `CoverageCertification.cs`, `CoverageCommandPose.cs` and `CoverageScratch.cs` only at geometry/display certificate boundaries.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageRenderErrorTests.cs`. Migrate geometry-only tests to the explicit diagnostic API.

**Interfaces:**
- `CoverageGeometryCertificate(bool FullCoreClearance, double MinimumCoreClearance, double ConstructionErrorBound, int DisplacedVertices)` is explicitly a CPU/world geometry result, not permission to draw.
- `CoverageEvaluator.TryEvaluateGeometry(...)` and `CoverageCertification.ValidateGeometry(...)` use the existing geometry arguments and return that certificate plus CoverageFailure. Their candidate and failure semantics stay atomic.
- `CoverageRenderStamp(ulong SceneIdentity, ulong FrameGeneration, ulong ViewGeneration, MotionKey ActorKey, ulong PreparationId)` binds the captured context. Scene3D validates this against its staged request and current render invocation.
- `CoverageRenderPass` is an immutable value with `uint CommandId` (0 means host), `ulong EquipmentInstanceId` (0 for host), actual `Matrix4x4 Model`, actual `Matrix4x4 ViewProjection`, `Vector3 LocalMin`, `Vector3 LocalMax` and `double PositionPerturbationBound`.
- `CoverageRenderContext` is a readonly ref struct containing exact `CoverageRefinement Topology`, stamp, latched `Vector3 RenderOrigin` and `ReadOnlySpan<CoverageRenderPass> Passes`. Its constructor is internal. The engine captures it into pre-reserved frame storage and freezes that storage for consumption. Headless tests use explicit synthetic capture data through existing test visibility.
- Pass bounds come from actual prepared/rigid vertices. PositionPerturbationBound covers the exact shader variant's additional world-position terms, including their float arithmetic. Task 5 captures these from the uploaded data and known shader path. Unknown or nonfinite terms refuse, not assume zero.
- Internal `CoverageRenderError.TryBound(topology, finalVertices, in actorModel, commands, scratch, in context, out double worldErrorBound, out CoverageFailure failure) -> bool` consumes the geometry producer contracts and actual draw descriptors.
- Replace the new, unreleased Task 4 TryEvaluate and Validate entry points with context-taking versions, inserting `in CoverageRenderContext context` before scratch. They return CoverageCertificate only after geometry and render bounds pass. CoverageCertificate also carries the RenderStamp. Diagnostic callers use the explicit Geometry methods. No context-free call may silently issue a drawable certificate. Pre-feature engine APIs and accepted Task 1 to 3 signatures remain unchanged.

- [ ] **Step 1: Write `Far_absolute_frame_exceeds_render_margin`, `Rebased_near_frame_is_certifiable`, `Actual_pass_vp_does_not_assume_camera_product`, `Missing_or_mismatched_context_refuses`, `Reserved_zero_command_ids_refuse` and `Render_refusal_is_atomic`.** Pin topology, positive command/instance IDs and canonical origin-subtracted matrix identity. Use actual near perspective, cascade/atlas and point-face matrices, not identity-only positives.
- [ ] **Step 2: Run red.** `build-slot --label coverage-render-error-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageRenderErrorTests`.
- [ ] **Step 3: Implement outward error propagation through the actual model-then-pass operations.** Include origin-subtraction residual, all bounded float operation error (including supported denormal behavior), actual pass matrices and known position terms. Qualify any inverse that maps clip error to equivalent geometric displacement. Use interval bounds and finite refusal, never camera distance or actor-relative magnitude as a substitute. Maximum error across participating passes must fit the existing margin policy without widening caps/budget.
- [ ] **Step 4: Separate geometry proof from drawable certification.** The full evaluator stages internally until render validation passes, then copies to caller output. A prior geometry success must not leak partial output on render refusal. Preserve transition truth, exact no-work data and all geometry failure codes. Missing/stale bindings use the defined identity/frame refusal, uncertifiable arithmetic uses UnsupportedTransform.
- [ ] **Step 5: Format, then run focused green and all affected coverage headless tests through build-slot.** Keep zero warm allocation, final-frame sentinel checks, far/rebased/custom-VP cases and the accepted core correction. Release build and scoped guards pass. Native depth/raster/pancaking/silhouette and temporal appearance remain Tasks 7/8, not a mathematical claim here.
- [ ] **Step 6: Commit and independently review.** `feat(coverage): certify actual render-frame rounding`. Task 4 completes only after 4B, 4C and all original geometric gates are accepted.

## Changes carried into the remaining parent tasks

**Task 5, frame ownership and scheduling.** Add `Scene3D.CoverageRenderContext.cs` and `CoverageRenderContextTests.cs`. Separate final cascade/atlas and selected point-face matrix preparation from draw execution. Capture every needed matrix after the frame view/origin is latched and before the first covered host/equipment pass. Include a conservative union of candidate and fallback bounds when scheduling visibility-dependent work.

`SkinnedCoverageFrame` remains an opaque immutable scene/frame/actor/preparation token, but staging it is not a drawable certificate. `SkinnedHostConformer.TryPrepare` receives an explicit `CoverageFallback fallback` parameter and stages immutable geometry, palette, matrices and command snapshots. First scope defines only `CoverageFallback.UncoveredCurrentPose`, with no optional default. A true staging result means the request was accepted for this frame, not that later render certification already passed.

Before any covered draw, Scene3D resolves each staged token once using its actual context. Success freezes the covered surface. Failure freezes the caller-selected current-pose uncovered presentation for all passes and records the failure. No late partial covered pass or frozen prior pose is permitted. Uncovered fallback uses the captured current pose and the ordinary route, not a newly evaluated mutable caller array.

Add `CoveragePresentation` values Pending, Covered and UncoveredFallback, and `CoverageFrameOutcome(CoveragePresentation Presentation, CoverageFailure Failure)`. `Scene3D.GetCoverageOutcome(SkinnedCoverageFrame frame)` reports the current owned result and rejects foreign/stale tokens. It is a developer lifecycle result, not player-facing text. The actual drawable certificate stays in the arena. Per-view render contexts and history stamps must not be reused across different views.

Task 5 tests add `Late_render_refusal_selects_one_current_pose_fallback`, `All_pass_matrices_precede_first_covered_draw`, `Origin_or_view_change_rejects_stale_context` and `Prepared_bounds_cover_shader_position_inputs`. Assert the actual route and vertex slices for every pass. Preserve the existing mixed CPU/GPU, outline-only, shadow-only, mutation and resource-generation cases.

**Task 6, binding.** Add required `CoverageFallback fallback` to the new HostCoverageBinding constructor and forward it to staging. Existing uncovered interfaces remain unchanged. Equipment draw snapshots and actual uploaded geometry metadata supply the context's matching command/instance, bounds and shader-position contribution. No duplicate decoding of different bytes, forged labels or pre-certification draw submission.

**Task 7, temporal.** Retain previous FINAL vertices and current-origin rebasing. Validate the actual prior-position/matrix inputs and reject invalid temporal reuse, never freeze the actor. Add render-context refusal to the existing transition/missing-history cases. Do not claim continuous collision detection between separately submitted surfaces.

**Task 8, native proof.** Exercise nonzero origin, non-origin-aware camera, custom independently supplied VP, late refusal and all actual cascade/point/outline routes. Deliberate depth mapping and raster precision retain their own image/readback assertions.

**Task 9, cost.** Include correction overlays, context capture, interval bounds and late resolution in the approved workload when separate measurement permission is obtained. No budget or acceptance target is relaxed.

**Cost unit 4 implementation decision.** Scene staging now builds its candidate through the internal `CoverageEvaluator.TryConstructCandidate`. It shares the geometry evaluator's arguments, frames, interpolation, construction, normals and atomic copy, and issues no certificate. The full late `CoverageCertification.Validate` is the one certification of a scene frame and the only Covered gate. The public evaluators, `TryBound`, `RenderSeparation` and the fallback policy are unchanged. Every source vertex is copied into the interpolated surface, so the late magnitude is at least the staged one, and a larger construction bound only raises the other-region equipment threshold. Late acceptance therefore implies the former early acceptance, with the same candidate bytes and certificate. Argument, identity, construction and normal refusals still return false at staging. A geometry certification refusal now stages true and resolves to `UncoveredFallback`, so its timing, failure precedence (a normals `FoldedPatch` can come first) and token-driven visibility and caster selection can differ from a staging refusal. Scene staging records no GeometryCertification span.

## Execution handoff

The owner approved the amendment and continued the existing subagent workflow. This plan records those concrete changes to Tasks 4 to 9. Keep the same one-writer and independent-review gates. If implementation exposes a deeper contradiction, report it before changing the approved outcome. Do not invent another approval gate for routine implementation choices already described here.
