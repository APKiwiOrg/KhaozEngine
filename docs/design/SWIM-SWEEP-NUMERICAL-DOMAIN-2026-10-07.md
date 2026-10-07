# Proposed initial sweep tree numerical domain

Status: proposed, not accepted or implemented. This bounds the outer Bepu tree's floating-point
maintenance arithmetic. It is not proof of general shape geometry, native residency or whole-backend
completion. The original 32 integration cases passed at `beee76e50`. The two domain-policy facts are
RED because the unguarded backend returns Clear, not because an overflow was reproduced.

## Proposed condition

Every stored outer broad-phase minimum/maximum coordinate must be finite, ordered, and have absolute
value at most 1,000,000 in the current physics frame. The condition includes excluded objects because
maintenance runs over the shared tree before per-query selection. It does not limit logical world
coordinates, rendered water extents or native domain identity. Local frames/rebase remain available.
Other existing shape, orientation, error and work limits still apply.

The initial value is deliberately conservative, not a claimed largest valid coordinate. It is much
larger than the range useful for millimetre contact accuracy in binary32. A larger value has no
established gameplay benefit here. Native producers must explicitly satisfy the complete backend
domain at G1b rather than assume partitioning or a suitable origin.

## Source arithmetic argument

Let M = 10^6 and N <= 2^31-1, the actual nonnegative node/leaf counts represented by the pinned tree.
The following concerns use the outer stored bounds, not unchecked internal mesh vertices.

| Pinned operation | Conservative magnitude before ordinary rounding |
| --- | --- |
| A bound coordinate | M |
| Component extent, max-min | 2M |
| Area metric xy+yz+xz | 12M^2 = 1.2e13 |
| Partition leaf-count-weighted area sum | N * 12M^2 < 2.6e22 |
| Sum of internal-node area metrics | (N-1) * 12M^2 < 2.6e22 |
| Unhalved centroid, min+max | 2M |
| Centroid difference | 4M |
| Bin reciprocal, at most 64 bins with span > 1e-12 | 6.4e13 |
| Coarse centroid/bin product bound | 2.56e20 |

Binary32 maximum is about 3.4e38. There is ample room to bound each rounded intermediate with a much
looser 1e25 ceiling. This is an overflow exclusion argument, not an accuracy claim for heuristic costs.
For positive accumulation, terms too small to change the current rounded sum cannot inflate it.
Otherwise a coarse constant-factor bound on each positive increment suffices. No cost is used as a
geometric separation certificate.

The pinned sources are:

- [Tree_Add](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_Add.cs), which computes
  the three-product area metric and inserts using unions of existing and new bounds.
- [Tree_BinnedRefine](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_BinnedRefine.cs),
  which caps bins at 64, handles degenerate centroid spans, forms leaf-count-weighted candidate costs,
  clamps bin indices and reifies child/parent links from the selected subtrees.
- [Tree_Refit](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_Refit.cs) and
  [Tree_Remove](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_Remove.cs), whose
  parent bounds use componentwise minimum/maximum unions.
- [BroadPhase](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/BroadPhase.cs),
  which updates leaf references on insertion/removal and maintains the active/static trees.

This argument does not replace the pinned backend's valid-tree invariant, prove allocation success for
arbitrary world sizes, or certify unsupported shape-internal trees. The checked query preflight still
bounds visits/pending storage and uses the exact pinned A-first/B-pending order under the same gate.

## Intended enforcement and failure semantics

Enforce the condition while observing each registered stored bound at successful mutation completion.
An out-of-domain stored bound makes records incomplete for certification. Readiness remains invalid
across later unrelated successful mutations, using the tested evidence lifecycle. No automatic reset or
revalidation of an existing source is proposed. A fresh, correctly framed source is the current recovery
route. This affects the opt-in certificate, not legacy physical simulation or legacy query outputs.

Query apertures still use their independently proved outward encoding. Their arithmetic is not the
tree's maintenance cost arithmetic. This proposal does not invent a different trajectory or clamp a
Hit distance to make it fit.

## Evidence and remaining acceptance

`BepuCertifiedCapsuleSweepTests.Domain.cs` has two facts:

1. A stored maximum exactly at M passes its control. Replacing it with the next float beyond M must
   refuse. The current implementation returns Clear at the final refusal assertion.
2. An excluded object outside the proposed domain must still refuse the shared tree certificate.
   The current implementation returns Clear.

Both were executed once after correcting the unrelated Static indexer compilation error. Their inputs
are finite and moderate. They are desired-policy tests, not a reproduced numerical failure. No extreme
scene, load test or stress run was used.

The guard stays held pending source-proof/domain acceptance. The 13 updated lifecycle cases, existing
allocation regression, affected legacy checks, whole-branch review and native G1b remain separate gates.
