# Foliage point-shadow query index

Status: implemented at `465e16b27`, independently reviewed with no findings and pushed. Earlier focused CPU
and strict Metal contracts passed. Private comparison does not establish significant gain or 144 FPS.
Current main is reconciled at `c665943d3` for staged 21.12.2. Final post-merge verification, whole-branch
review, main integration, release and released-engine game adoption remain pending.
Date: 2026-10-23.

## Outcome and evidence

Reduce repeated point-shadow foliage scans without changing the selected proxies, static signatures,
draw runs, animation, shadow quality or update policy. This is the next bounded step in the Grimhollow
Mac frame performance round.

The last direct same-binary comparison measured 117.04 FPS with renderer-owned output initialization
disabled and 138.31 FPS with it enabled. The latter averages 7.230 ms, 0.286 ms above the 144 FPS budget.
The owner confirmed that the picture looked unchanged. This index has no measured speedup yet.

In the subsequent CPU sampling trace, point-shadow preparation occupies 20.90% of window-thread
sampled residence. Acquisition, signatures and queries together occupy 14.21%. Inlining prevents
assigning all of that time to individual callees. These shares are not pure CPU execution times or FPS.
The [consumer evidence](https://github.com/APKiwiOrg/Grimhollow/blob/c2ba1a5e7/docs/verification/mac-frame-performance-2026-10-23.json)
records the direct comparison separately from profiling.

Before this change, `MixFoliagePointSignature` visited every submitted foliage source for every static
light. `FoliagePointShadowStream.Query` scanned every proxy inside its submitted ranges and applied the
exact shell predicate. Rendering a rebuilt row repeated these queries on its first face. Rigid casters
already used `PointCasterIndex` to avoid this full scan. Both foliage consumers now query the frame-local
adapter, while the stream's direct query remains available to the contract tests.

## Decision

Build one spatial index of the current frame's submitted foliage proxies. Reuse `PointCasterIndex`
for the broadphase and exact shell test. Each entry retains its source ordinal and original proxy slot.
Entries are added in existing source, range and proxy order, so ascending index hits reproduce the
old order. Static signatures and first-face run construction consume that same indexed query.

The index is rebuilt once per point-shadow frame after existing source/range preparation. The
immutable proxy streams, GPU buffers and their retirement remain unchanged. There is no cache of
signatures across frames and no new revision contract to maintain.

Design scores use 1 for weak and 10 for strong. They are engineering judgments, not measured gains.
Criteria are potential gain, ease of proving correctness and implementation simplicity, equally weighted.

- Frame-local foliage index: 8, 9, 8, total 25. Reuses the existing tested query and current-frame inputs.
  It adds one retained CPU index and a mapping from its slots to proxy slots.
- Ground-cover scalar hoisting: 3, 10, 9, total 22. Straightforward arithmetic reuse, but its likely
  contribution is smaller and the sampled wrapper does not isolate those operations.
- Cross-frame signature cache: 9, 4, 4, total 17. Could skip more work, but no existing revision signal
  covers every signature input. Exact matrix bits and raw light-boundary membership complicate reuse.

The frame-local index is recommended. Its benefit is subject to direct measurement.

## Required behavior

- Build lazily at the existing `EnsureFoliagePointShadowFrame` boundary, including a sealed empty index.
  A new frame must not retain omitted sources or old selected prefixes.
- Preserve source order and the current merged ranges. The helper must also preserve supplied range
  order for its direct contract tests. Use original proxy slots when coalescing draws.
- Keep exactly the current `PointShadowCasterSphere.TouchesShadowingShell` result for raw light
  position, outer radius, near radius and exclusion box. Retain the existing broadphase's conservative
  handling of large, nonfinite and unbinnable spheres.
- Fold precisely the same mesh identity and raw rest-transform words in the same order. Existing
  rigid dissolve/complement handling, interior-resource versioning and light quantization remain intact.
- Keep mesh-validity and uploaded-buffer checks at their existing stages. A proxy's signature must
  not disappear merely because its GPU buffer has not yet uploaded.
- Keep colour-camera rejection separate from casting selection. Preserve off-camera casters,
  rest-pose point shadows, displaced directional shadows and current density selection.
- Preserve batch replacement, disposal, handle generation and render-origin behavior. Add no GPU
  buffer, GPU pass, upload policy, shader change, public API or user setting.
- Retain arrays and scratch lists. Frame builds and queries allocate nothing after capacities are warm.
- Keep the existing static dirty-row and oldest-first scheduling policy. Signature equality must not
  clear pending dirt or conceal atlas replacement.

## Proof and acceptance

An independent full scan of selected proxy ranges is the query oracle. Compare exact ordered
references and coalesced face runs, including boundary and lifecycle cases. A sparse 4,096-proxy
fixture must return the same one hit while making at most 16 exact shell tests. That is an operation
count proof, not a timing benchmark.

Run focused CPU contracts and the existing native foliage/point-shadow GPU tests once through
`build-slot`. Preserve zero warmed allocations. Review the change independently, then compare direct
client frame intervals at the same quality and slow view. The 144 FPS target requires direct evidence.

The integration owner handles final full verification, whole-branch review, main integration, release and released
engine adoption. Current-main reconciliation is complete. Version 21.12.2 was subsequently released, so this batch is staged
for 21.12.3. It ships in 21.13.0, which replaced the untagged 21.12.3. Private diagnostic
archives never merge into `main`.

## Implementation evidence

The sparse 4,096-proxy fixture passed exact membership and ordering against an independent selected-range
walk. Its full-scan scaffold failed only the work-count assertion with 4,096 exact tests. The spatial
adapter reduced that count to 1. A warmed frame build and query allocated zero bytes.

Focused CPU verification passed 103 tests with no failures. The prescribed filter also selected five
GPU-only interior-shadow tests, which were skipped without the GPU opt-in. Scene contracts cover omitted,
disposed and replaced batches, reduced and empty prefixes, mesh generations, source ordering, mesh and slot
gaps, off-camera casters, motion-only replacements, unuploaded proxy signatures and lazy origin rebasing.
These results prove operation counts and correctness contracts, not a measured FPS gain.

Native Metal verification passed all 14 selected foliage and point-shadow byte-identity tests with no
failures or skips, using the Metal validation layer. The worker ran only focused commands through
`build-slot`. These are earlier scoped results, not final post-merge verification.
Whole-tree dash, prose, file-size, instruction-budget and doc-version guards also passed, along with
the diff whitespace check. No baseline increase or guard bypass was used.

## Parent verification and measurement status

The parent independently checked the raw CPU and strict Metal evidence above. Implementation `465e16b27`
received independent review with no findings and was pushed. The original baseline, source provenance and
worker test results remain historical evidence. They are not results from the reconciled `c665943d3` branch.

A private identical-quality direct comparison changed only `Render3D.dll` and measured 137.9666 FPS before
and 139.2714 FPS after in one sequential 10-second pair. This does not establish a significant gain or
144 FPS. The owner reports no visible foliage in the selected baseline view, so foliage visual look
validation is inconclusive. Later CPU stacks show off-camera foliage paths, but no submission counts were
measured. The [durable consumer evidence](https://github.com/APKiwiOrg/Grimhollow/blob/d279463f16b73200fc59cc7b908a2a7354c7fb6b/docs/verification/mac-frame-performance-2026-10-23.json)
records the later comparison and investigation separately from the original baseline.

Further investigation completed through the archived private native-pool probe. That experiment is
**NOT ADOPTED**. Its off 148.09 FPS / on 138.15 FPS pair had 13.888% different local Metal scope entries and
mode-dependent instrumentation overhead. It is not an isolated causal pooling result and does not establish
that the target was achieved. Its source, counters and UI fields stay out of production.

The target assessment remains an unmet direct pair and an unproven overall outcome. Reconciliation at
`c665943d3` initially retained staged 21.12.2. The final source at `15eb297da` passed the Release build,
38,582 CPU tests with 1,528 skips, 34 strict Metal cases and one additional recorder case. Independent
whole-branch review approved it. The hosted full Metal, Direct3D 11 and Vulkan matrix passed all 29 jobs,
including the three assembly-coverage checks and Vulkan synchronization validation with its log producer
confirmed. The [verification record](../verification/mac-frame-performance-2026-10-24.json) preserves the
exact tested source, commands, counts and evidence hashes.

Tag `v21.12.2` now names the reconciled main commit `4bb5b0462`. This batch is therefore staged as 21.12.3,
with the released catalog entry preserved. A fresh build of the staged metadata, main integration, package
verification, an owner-started engine release and released-engine game adoption remain pending. The game
finishing suite also has a separate solo-host readiness timeout under investigation in
[Grimhollow #629](https://github.com/APKiwiOrg/Grimhollow/issues/629).
