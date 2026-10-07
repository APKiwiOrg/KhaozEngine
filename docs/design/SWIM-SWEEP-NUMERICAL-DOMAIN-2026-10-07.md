# Initial sweep tree numerical domain

Status: the conservative refusal policy is accepted for implementation with the corrections below.
The guard passed its 48-case focused batch. This bounds specified outer-tree
floating-point expressions, not every maintenance intermediate by one blanket constant. It is not
proof of general shape geometry, native residency or whole-backend completion. The original 32
integration cases passed at `beee76e50`. The two domain-policy facts originally
failed because the unguarded backend returned Clear, not because an overflow was reproduced.

## Admitted condition

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

Let M = 10^6 < 2^20, K = 2^44, p = 5/4 and n < 2^31. Counts are the actual nonnegative
installed tree counts. The bounds below apply to finite nonempty installed bounds satisfying the
condition at the relevant operations. They are not an accuracy claim for heuristic costs.

For a stored coordinate in [-M,M], an extent is at most 2^21. Each rounded product of two extents is
at most 2^42. The three-product area metric is less than K. The rounded difference
postmetric-premetric also has absolute value at most K, since both metrics are nonnegative and bounded
by that exactly representable value. Count-to-float conversion is at most 2^31, so the two-term
count-weighted partition cost is at most 2^76. These expressions are finite.

### Grouped signed accumulation

`Tree_RefinementScheduling.RefitAndMeasure` and `RefitAndMark` accumulate signed local metric changes.
A nonnegative-only sequential sum argument is insufficient. Use the absolute majorant K*n^p for
arbitrary binary grouping of n local terms. The base case is the per-term K bound. For groups with
counts m >= k, let x = k/m. Round-to-nearest addition of nonnegative represented a,b satisfies
fl(a+b) <= max(a,b)+2*min(a,b): the larger operand itself is a representable rounding candidate at
distance min(a,b) from the exact sum. The induction's exact sums are already far below overflow.

- If x <= 1/16, monotonicity and the induction give a majorant K*m^p*(1+2*x^p).
  Since 2*x^(1/4) <= 1 < 5/4, this is bounded by K*m^p*(1+p*x), hence by
  K*(m+k)^p using convexity of (1+x)^p.
- If 1/16 < x <= 1, convexity leaves a relative margin of at least 1/128:
  ((1+x)^p-(1+x^p))/(1+x^p) >= (x/4)/2 >= 1/128. This greatly exceeds
  binary32 unit roundoff 2^-24. One rounded addition therefore remains inside K*(m+k)^p.
  An addition whose exact result is subnormal is exact for represented binary32 addends.
- Signed additions use absolute majorants. Symmetry and monotonicity of round-to-nearest bound
  |fl(a+b)| by fl(|a|+|b|), so the same induction applies.

Thus grouped absolute accumulation is at most K*n^(5/4) < 2^82.75 < 1e25 for n < 2^31.
This bounds the unnormalized accumulated change, not every subsequent expression.

### Root normalization and installed scale

`RefitAndMark` normalizes by the root metric only when that metric is at least 1e-10, otherwise it
returns zero. Including rounding, the normalized magnitude is less than 1e35. The earlier blanket
1e25 statement was therefore wrong and is replaced by these separate bounds.

The installed `BepuPhysicsWorld.Step` calls `_sim.Timestep(dt, null)`. Pinned `BroadPhase.Update` takes
its single-threaded path and calls `ActiveTree.RefitAndRefine(Pool, frameIndex)` and the corresponding
static-tree method. Both omit the optional refinement scale, whose pinned default is exactly 1.
There is no public world setter for that scale. This argument does not cover arbitrary custom scales.
`GetRefineTuning` explicitly rejects a nonfinite normalized cost. Any failed Step leaves the mutation's
evidence transaction incomplete and cannot publish a current sweep certificate.

### Centroids, sentinels and the separate equality gap

The pinned binner uses unhalved min+max centroids, at most 2M before rounding. It has at most 64 bins,
and divides by a span only when that span is greater than the represented `1e-12f` epsilon. Coarse
centroid/bin-product bounds remain below 1e22. This does not establish every degenerate-span path.

Empty-bin/null bounding-box sentinels are excluded from the installed-coordinate and metric argument.
Their extrema are identities for min/max unions, not admissible geometry. The candidate-cost branches
require positive leaf counts on both sides before evaluating metrics on the populated bounds.

There is a distinct source-derived gap: the all-degenerate fallback requires every span to be strictly
less than epsilon, while per-axis reciprocals require strictly greater spans. Exactly
`(epsilon, 0, 0)` satisfies neither condition. All reciprocals become zero, all entries enter bin zero,
and no nonempty split replaces the initialized split/bound values. This is tracked as
[#1324](https://github.com/APKiwiOrg/KhaozEngine/issues/1324). Its real-scene reachability and runtime
failure mode have not been reproduced. It is not an observed overflow, is not solved by the coordinate
guard, and grants no permission to repair Bepu in this change. Broader maintenance/sweep completeness
remains open.

### Pinned sources

- [Tree_Add](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_Add.cs) for the area
  metric, insertion unions and count-weighted choices.
- [Tree_RefinementScheduling](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_RefinementScheduling.cs)
  for signed change accumulation, root normalization, scale default and nonfinite-cost refusal.
- [Tree_BinnedRefine](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_BinnedRefine.cs)
  for centroid construction, bin limits, strict epsilon predicates, sentinels and partition costs.
- [Tree_Refit](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_Refit.cs),
  [Tree_Remove](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_Remove.cs) and
  [BroadPhase](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/BroadPhase.cs)
  for parent unions, leaf-reference updates and the installed maintenance calls.

The argument does not replace valid-tree invariants, prove allocation success for arbitrary world
sizes or certify unsupported shape-internal trees. The checked query preflight remains required.

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

The conservative guard is accepted for code-only implementation with the corrected argument above.
The 13 lifecycle cases and allocation regression passed in one 14-case run. The affected legacy batch
passed 105 cases. The guarded batch then passed 48 cases, including both domain facts, all original
32 integration cases, 13 lifecycle cases and the allocation fact. Scoped format apply and verification
passed on the exact eleven implementation/test files. Only one initializer whitespace layout changed.
See [the guard evidence](../verification/2026-10-08-certified-sweep-domain.json). Whole-branch review
and native G1b remain separate gates.

## Migration compatibility ruling

Migration accepted frame compatibility of the proposal at `32e54bd32`. It constrains one installed
physics view, not MapDoc logical coordinates, cave depth, water extent or world size. Producers must
install every active/static/excluded bound in the chosen local frame from the start and preserve the
condition at mutation completion. Rebase alone is not recovery after a breach: the permanent
invalidation rule requires a fresh correctly framed source. Invalid evidence must never become
clear, dry or proven unreachable. No conflict with the approved R2 framing was identified.

This accepts compatibility only. It does not accept the maintenance-arithmetic argument or broader
sweep completeness. G1b must prove the actual installed set, frame/rebase lifecycle and all other
backend limits. Guard acceptance does not extend that migration ruling to broader certification.
