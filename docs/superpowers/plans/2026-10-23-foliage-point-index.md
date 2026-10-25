# Foliage Point-Shadow Index Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove repeated full foliage scans from point-shadow queries while preserving exact selected proxies and draw order.

**Architecture:** A retained CPU index is rebuilt from the current frame's submitted foliage ranges. It delegates spatial filtering to `PointCasterIndex` and maps accepted slots back to source ordinals and proxy slots. Signature mixing and first-face draw preparation use the same result.

**Tech Stack:** C#, .NET 10, existing Render3D internals, xUnit, native Metal correctness tests.

**Spec:** [FOLIAGE-POINT-INDEX-DESIGN-2026-10-23.md](../../design/FOLIAGE-POINT-INDEX-DESIGN-2026-10-23.md).

## Global Constraints

- The owner approved this design and plan on 2026-10-23. Execute with one Codex implementer and independent review.
- Preserve the existing exact shell predicate, ordered raw matrix signature words and static dirty-row policy.
- Preserve TAA, HLOD, shadow resolution, foliage density, animation and GPU resource lifetime.
- No public API, shader, quality setting, cross-frame signature cache or new GPU allocation.
- No per-frame heap allocation after capacities are warm.
- Use only the task worktree on `fix/mac-frame-performance`. Code baseline is `fcc7f08e0cf3a15369e04cb0a666f8a0b0102dd1`.
- Every build, test or render uses `build-slot`. Native GPU work uses `--heavy`. No stress runs or full suites in the worker.
- Scratch evidence belongs under `/tmp/khaozengine-mac-frame-performance`.
- Parent owns pushing, integration, version/changelog, release handoff and game adoption. No tags are authorized.

## Review Focus

- Empty and replacement frames must discard previous source/prefix membership.
- Gaps, reordered disjoint ranges and integer range endpoints must retain the full scan's exact ordering.
- Large/nonfinite spheres and near/exclusion boundaries must never lose a previously accepted proxy.
- Stale meshes and absent GPU buffers must be handled at the same stage as before, without hiding signature inputs.
- Camera rejection and render-origin changes must retain off-camera rest-pose casters and existing upload behavior.

---

### Task 1: Index current-frame foliage point casters

**Files:**
- Create `KhaozEngine.Render3D/Internal/FoliagePointCasterIndex.cs`.
- Modify `KhaozEngine.Render3D/Internal/FoliagePointShadowStream.cs` only for a shared selected-range helper if needed.
- Modify `KhaozEngine.Render3D/Scene3D.FoliagePointShadows.cs` at frame preparation, signature mixing and first-face query construction.
- Update only the scope comment in `KhaozEngine.Render3D/Rendering/PointCasterIndex.cs` to name its foliage adapter use.
- Create `KhaozEngine.Render.Tests/Render3D/FoliagePointCasterIndexTests.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/FoliagePointIndexedFrameTests.cs` for scene-level transitions using existing rigs.
- Update this plan, its spec and `docs/INDEX.md` with actual verification state.

**Interfaces:**
- Consume `FoliagePointShadowStream.Casters`, `FoliagePointShadowRange` and the existing `PointCasterIndex.Reset/Add/Seal/Query` contract.
- Produce `internal readonly record struct FoliagePointCasterRef(int SourceOrdinal, uint ProxySlot)`.
- Produce `internal sealed class FoliagePointCasterIndex` with `Reset()`, `Add(int sourceOrdinal, FoliagePointShadowStream stream, ReadOnlySpan<FoliagePointShadowRange> ranges)`, `Seal()`, and `Query(Vector3 lightPosition, float radius, float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax, List<FoliagePointCasterRef> result)`.
- Expose internal `int TouchTests { get; }` for deterministic work-count assertions. Query clears its result first. Counts accumulate within the current sealed frame and reset on the next frame.
- If range selection is extracted, use `internal ReadOnlySpan<FoliagePointShadowCaster> SelectRange(FoliagePointShadowRange range)` on the stream. Preserve the old long end calculation, including empty/out-of-range inputs.

- [x] **Step 1: Add the full-scan oracle and failing work-count contract.**

  Use two sources with 2,048 casting proxies each. Source 0 has X = 64 * i and source 1 has
  X = 64 * (2048 + i), for i from 0 through 2047. Y and Z are zero.
  Select all proxies and query a radius-2 light at the origin. Use a mesh with radius below 2 m.
  Add a minimal full-scan implementation behind the new interfaces so correctness assertions already pass
  and the work-count assertion provides meaningful RED, rather than a compile failure.

  ```csharp
  Assert.Equal(new[] { new FoliagePointCasterRef(0, 0) }, actual);
  Assert.Equal(4096, oracleTouchTests);
  Assert.InRange(index.TouchTests, 1, 16);
  Assert.Equal(expectedOrderedReferences, actualOrderedReferences);
  Assert.Equal(expectedFaceRuns, actualFaceRuns);
  ```

  Cover range gaps and order, multiple meshes/generations, empty selections, near and exclusion boundaries,
  unbinnable/oversize/nonfinite spheres and fixed-seed oracle cases. The oracle walks selected proxies directly,
  independently of spatial cells. Keep matrix/hash arithmetic in existing tests.

  Name the ordering case `Query_preserves_source_and_supplied_range_order`. For five nearby proxies in
  source 0 and ranges `(3, 2)` then `(0, 1)`, assert proxy slots `[3, 4, 0]` and runs `(3, 2)` then `(0, 1)`.
  Name the boundary case `Query_matches_full_scan_at_shell_and_index_boundaries`. Include tangent spheres,
  adjacent float values, a sphere larger than the 8 m cell, nonfinite transforms and an unbinnable centre.
  Each input asserts exact equality to the same full-scan oracle, rather than a weakened superset.

- [x] **Step 2: Run the focused RED once.**

  ```bash
  build-slot --label foliage-point-index-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~FoliagePointCasterIndexTests' --logger 'trx;LogFileName=foliage-point-index-red.trx' --results-directory /tmp/khaozengine-mac-frame-performance
  ```

  Expected failure is the sparse work-count assertion, with 4,096 exact tests. Membership and ordering pass.

- [x] **Step 3: Build and query the shared spatial index.**

  `Add` appends selected proxy references and cached absolute spheres in source/range/proxy order.
  `Seal` resets and fills one `PointCasterIndex` with those sequential slots, then seals it. `Query` maps
  sorted accepted slots back to references. Retain all storage and scratch lists. Keep existing fallback
  behavior for oversized and unbinnable spheres. Do not change the exact predicate or mixer.

  Reset at the existing guarded frame-preparation boundary. Seal empty input before its early return.
  After source/range preparation, add sources in list order and seal once. Signature mixing resolves each
  reference to the original caster and uses the unchanged nine mixer words. First-face preparation groups
  by source ordinal, mesh identity and consecutive original proxy slot. Preserve its buffer and mesh checks,
  six-face reuse, and stream upload/retirement paths.

- [x] **Step 4: Add scene-transition and warmed-allocation assertions.**

  Test omitted/replaced/disposed batches, reduced and empty prefixes, mesh handle reuse, camera changes,
  render-origin moves and motion-only replacements. Reuse current fake-device point-shadow rigs and assert
  existing static rebuild counts, proxy uploads and exact face draw runs. An unchanged next frame remains
  cached, while a relevant membership/rest-pose change still dirties its row. Warm fixed capacities before
  checking `Reset/Add/Seal/Query` with `AllocationRegion` and assert zero allocated bytes.

  Use `Empty_frame_removes_prior_membership`, `Changed_prefix_or_rest_pose_dirties_the_static_row`,
  `Camera_and_motion_only_changes_preserve_rest_shadow_state`, and
  `Warmed_frame_build_and_query_allocate_zero_bytes` as the named contracts. Their core assertions are:

  ```csharp
  Assert.Empty(emptyFrameHits);
  Assert.Equal(0, emptyIndex.TouchTests);
  Assert.Equal(0, unchangedFrameStaticRebuilds);
  Assert.Equal(1, changedPrefixStaticRebuilds);
  Assert.Equal(0, motionOnlyStaticRebuilds);
  Assert.Equal(expectedOffCameraRuns, actualOffCameraRuns);
  Assert.Equal(0, allocatedBytes);
  ```

- [x] **Step 5: Run the focused GREEN and native contracts.**

  ```bash
  build-slot --label foliage-point-index-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~FoliagePoint|FullyQualifiedName~PointCaster|FullyQualifiedName~PointSignature|FullyQualifiedName~PointShadowResidency|FullyQualifiedName~PointShadowReconfigure|FullyQualifiedName~InteriorCutShadow' --logger 'trx;LogFileName=foliage-point-index-green.trx' --results-directory /tmp/khaozengine-mac-frame-performance
  build-slot --heavy --label foliage-point-index-native -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native KE_METAL_REQUIRED=1 MTL_DEBUG_LAYER=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~FoliageShadowGpuTests|FullyQualifiedName~PointShadowByteIdentityGpuTests' --logger 'trx;LogFileName=foliage-point-index-native.trx' --results-directory /tmp/khaozengine-mac-frame-performance
  ```

  Expected result is zero failures, warnings or native skips. Run commands sequentially and finish them before
  reporting. New failures get diagnosis, not repeated runs. Full verification remains parent-owned.

- [x] **Step 6: Run relevant guards, update evidence and commit.**

  Run the repository dash, prose, file-size, instruction-budget and doc-version guards, plus `git diff --check`.
  Record exact exits and operation counts under the scratch root. Update plan/spec state without claiming an
  FPS gain. Commit only owned files with a `perf(render3d)` subject. Return the exact commit and verification
  evidence to the parent. Do not push, merge, tag, launch a client or change a consumer pin.

## Worker verification record

- Starting guard passed at clean `5f26816a27b03e434beb822d8d4bdf2b2bba595b` on the required worktree and branch.
- RED exited 1 with six passed and the sole expected sparse work-count failure, at 4,096 exact tests.
- Focused GREEN exited 0 with 103 passed, zero failures and five GPU-only interior-shadow skips.
  Sparse index queries use 1 exact test against the oracle's 4,096. Warmed frame allocation is zero bytes.
- Two earlier GREEN attempts exposed test fixture errors, an out-of-range wind value and an eager-upload
  expectation. The fixtures now preserve the existing valid wind range and lazy shadow-row upload policy.
- Native Metal verification exited 0 with all 14 selected tests passed, zero failures and zero skips,
  with the Metal validation layer enabled.
- Whole-tree dash, prose, file-size, instruction-budget and doc-version guards passed, along with
  `git diff --check HEAD`. The file-size result is a fresh complete scan, independent of the earlier
  interrupted docs-checkpoint scan. The owned-path commit completes worker execution.
- Full suites, independent review, runtime measurement, integration and release remain parent-owned.

## Parent verification and measurement

- [x] Independently inspect the returned diff and raw evidence, then complete independent code review.
  Implementation `465e16b27` was reviewed with no findings.
- [x] Push the validated engine change before proceeding to runtime comparison. Implementation `465e16b27`
  was pushed.
- [x] Complete a private identical-quality direct client comparison, changing only `Render3D.dll`.
  One sequential 10-second pair measured 137.9666 FPS before and 139.2714 FPS after. Keep profiler attribution
  separate. This pair does not establish a significant gain or 144 FPS.
- [x] Assess the 144 FPS target from measured frame intervals. The direct pair is below target, and the
  overall outcome remains unproven. Further investigation completed through the archived native-pool probe,
  which is not adopted. Neither the operation-count fixture nor that probe establishes target success.
- [x] Complete source verification and independent whole-branch review at `15eb297da`. The Release build,
  full CPU suite, focused Metal cases and hosted full backend matrix passed. The exact gates are in the
  [verification record](../../verification/mac-frame-performance-2026-10-24.json).
- [x] Recheck current main and tags. Version 21.12.2 was released at `4bb5b0462`, so this batch is staged as
  21.12.3 with the released changelog entry preserved.
- [ ] Build the staged metadata, integrate main, verify packages and complete the released-engine game
  handoff. Never merge diagnostic archives. The separate game boot-timeout gate is
  [Grimhollow #629](https://github.com/APKiwiOrg/Grimhollow/issues/629).

### Parent evidence status

- The parent independently checked raw results of 103 CPU passes and five expected GPU-only skips,
  14 strict Metal passes without skips, sparse exact tests reduced from 4,096 to 1 and zero warmed allocation.
  These are earlier scoped results, not final post-merge verification. The worker record and original baseline
  hashes above remain historical provenance.
- The owner reports no visible foliage in the selected baseline view, so foliage visual look validation is
  inconclusive. Off-camera foliage paths appear in later CPU stacks, but no submission counts were measured.
- The private native-pool experiment is **NOT ADOPTED**. Its off 148.09 FPS / on 138.15 FPS pair had 13.888%
  different local Metal scope entries and mode-dependent instrumentation overhead. It is not an isolated
  causal pooling result or evidence that the target was achieved. Its source, counters and UI fields must not
  enter production.
- [Durable consumer evidence](https://github.com/APKiwiOrg/Grimhollow/blob/d279463f16b73200fc59cc7b908a2a7354c7fb6b/docs/verification/mac-frame-performance-2026-10-23.json)
  records the private comparison and subsequent investigation. Final source verification is now recorded
  separately above. Integration, package verification, release and game adoption remain with the parent.

## Plan self-review

Spec coverage, interface consistency, all five review-focus classes and task proportion checked.
One implementation task owns the new index and both consumers. Existing revision, hash, shader and native
lifetime contracts remain the reference. Execution preference is Codex subagents with independent review.
