# Foliage point-shadow query index

Status: approved by the owner on 2026-10-23 for one Codex implementation task and independent review. Implementation has not started.
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

Source proves that `MixFoliagePointSignature` visits every submitted foliage source for every static
light. `FoliagePointShadowStream.Query` scans every proxy inside its submitted ranges and applies the
exact shell predicate. Rendering a rebuilt row repeats these queries on its first face. Rigid casters
already use `PointCasterIndex` to avoid this full scan.

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
client frame intervals at the same quality and slow view. The 144 FPS target remains open until measured.

The [implementation plan](../superpowers/plans/2026-10-23-foliage-point-index.md) assigns the code and
tests. The parent owns final full verification, current-main reconciliation, version/changelog work,
integration and released engine adoption. Private diagnostic archives never merge into `main`.
