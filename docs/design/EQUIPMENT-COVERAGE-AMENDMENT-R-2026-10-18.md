# Equipment coverage amendment R: float runtime conformation with an offline exact oracle

Status: model R and its four design sections are owner approved in conversation. This written
amendment awaits owner review. Implementation, measurements and any release remain unauthorized
until their own approvals.
It amends sections 7, 10, 11 and 12 of the [approved design](EQUIPMENT-COVERAGE-DESIGN-2026-10-16.md)
and replaces the actual render-frame error contract of the
[construction and render-error amendment](EQUIPMENT-COVERAGE-AMENDMENT-2026-10-16.md).
The engine work is tracked by [#1418](https://github.com/APKiwiOrg/KhaozEngine/issues/1418) and the
consumer work by [Grimhollow #547](https://github.com/APKiwiOrg/Grimhollow/issues/547).

## Outcome and evidence

The outcome is unchanged. A rigid close-fitting chestplate sits on the skinned body in full motion,
with no host surface breaking through it, using the approved bounded collar conformation.

The approved model proved every submitted pose at runtime. It cannot meet its own CPU budget. The
budget allows 2 ms added preparation for 32 typical actors, or 62.5 microseconds per actor. The
[conditioned pilot after cost unit 4](../superpowers/plans/proofs/2026-10-16-equipment-coverage-cost.md)
measured 26.292074 ms added per actor-frame on a hosted CPU: 12.461836 ms exact construction and
13.776059 ms full late validation. One actor costs more than a 60 Hz frame. Construction alone is
about 199 times the per-actor share. Cost units 1 to 4 removed duplicate and avoidable work, and no
further constant-factor unit can close a gap of this size. The approved plan returns a budget miss to
design review.

The review considered compiled pose-cell proofs, certificate reuse, pose envelopes, actor scheduling,
conservative proxies, GPU certification and exact memoization. The scoring and the owner's choice
are recorded in the ledger research and root assessment for this program.

## Owner rulings

- **R1, model.** Runtime runs a float version of the approved conformation with a cheap guard. The
  exact certifier becomes an offline oracle. This reverses the approved rule that every submitted
  pose is proved at runtime (design section 7).
- **R2, oracle domain.** For each armour asset, the oracle certifies every authored frame of every
  clip worn with it and a declared blend grid of the game's real layering. Any failure fails the
  game's CI and blocks that content.
- **R3, approach.** The float constructor replaces the exact constructor in place on the CPU. There
  is one runtime implementation. No exact runtime switch and no GPU vertex-stage conformation.
- **R4, design sections.** The runtime path, oracle, correctness evidence, and gates and delivery
  order below were each approved as presented.

## What Covered means after this amendment

Covered means the float constructor produced a finite candidate without any guard refusal, and
every host and equipment slice of the actor was recorded completely in this frame. It no longer
means the submitted surface carries a runtime proof.

The no-breakthrough guarantee now comes from the oracle. It is proved exactly for every sampled
pose in R2. It is not proved between samples, for blends outside the declared grid, or for poses
produced by layers the game did not declare. Those poses still use the same construction and guard,
so a gross failure still falls back, but a subtle breakthrough there is possible and unproved.

## Runtime contract

### Staging

`Scene3D.StageCoverage` keeps its entry checks, identity checks and CPU skinning. It then calls one
internal skin-and-construct function, shared with the oracle, that runs in order:

1. Magnitude and command frame preparation.
2. Interpolation of the posed source to the refined surface.
3. Float conformation of each eligible position group toward the actual inward support sheet, with
   the collar smoothstep, through new float height and boundary lookups on `CoverageSupportSurface`.
   The exact bounded lookups remain for the oracle.
4. The one local core-face correction, in float.
5. Transition weighting.
6. Normals and the atomic copy into the candidate.

Interval spreads, slack terms and uncertainty refusals that existed only to feed the per-frame proof
are removed from construction. Where the exact code took an interval maximum to recede
conservatively, the float code adds a fixed relative rounding allowance in the receding direction,
so float outputs never systematically under-recede. Welded position groups still share one computed
position, so seams receive identical bits.

### Guard

The guard is the constructor's own refusal set: cap contact, core escape, unsupported support,
recession budget, non-finite output, folded patch and the core-correction refusals, with their
existing failure codes. It adds one check that replaces the render-frame error contract.

**Origin magnitude.** Staging computes the largest coordinate magnitude of the candidate relative
to the latched frame render origin. If a conservative float32 rounding bound for that magnitude
exceeds `Clearance x MarginFraction` for any command, staging refuses with `UnsupportedTransform`.
With the default camera-relative origin this never triggers for nearby actors. With a zero origin or
a camera that does not honour the render origin, a distant actor falls back instead of risking
precision breakthrough. `Scene3D.Begin` latches the render origin before it opens the coverage arena
that staging requires, so the latched origin is always available. The plan fixes the exact bound.

Any guard refusal returns false from staging. The actor draws the explicit uncovered current pose in
every participating pass, as today.

### Resolution

`PrepareCoverageRendering` keeps the liveness and completeness checks: every host slice recorded,
every equipment slice live, and every equipment slice submitted or outlined. If they pass, the
outcome is Covered. Otherwise it is the uncovered fallback with the existing failure codes. Late
disposal and unload keep their existing fallback.

### Kept unchanged

- One shared CPU-prepared final stream for colour, motion, outline, cascade and point-face passes.
- Frozen per-frame decisions, and re-staging for a new render view.
- Previous actually presented FINAL vertices, current-origin rebasing, per-view history identity and
  local transition rejection, including the existing perturbation bounds used for history.
- Zero warm managed allocation, preallocated capacities and the explicit uncovered fallback.
- Region metadata, the actual inner sheet, collar weights, recession budget, fixed topology and
  protected uncommanded bytes.

### Removed

- The exact construction path and its scene-only bypass `TryConstructCandidate`.
- Render pass capture for coverage, the pass list, `CoverageCertification.Validate` at resolution,
  the render stamp and the drawable `CoverageCertificate`.
- The render-arithmetic certifier: `CoverageRenderError`, `CoverageRenderArithmetic` and
  `CoverageRenderContext`. No runtime or oracle consumer remains.
- Public evaluators that required a render context. Nothing in this feature is released, so no
  published API compatibility is affected.

## Oracle contract

### Engine

A public headless `CoverageOracle` needs no `Scene3D` and no GPU. It is built for one
`CoverageRefinement` and its inverse binds, and owns its scratch and buffers.

`TryCheck(palette, model, commands, out certificate, out failure)` skins the pose and runs the
shared skin-and-construct function, then the exact `CoverageCertification.ValidateGeometry` on that
output. For identical inputs its candidate equals the staged runtime candidate byte for byte.

A failure states its stage. A construction refusal carries its guard code. A certification refusal
carries its predicate code, command and count. Warm checks allocate no managed memory, so a consumer
can run thousands of samples in one test.

The origin-magnitude guard has no meaning offline, so the oracle checks actor-local geometry. The
runtime guard covers render precision separately.

### Consumer

Each game owns its sampler and grid, because layering is game-specific. Grimhollow adds a headless,
blocking CI test that enumerates every armour asset with the host it is worn on:

- Every authored frame of every clip, at the clip's own sample rate.
- A blend grid over the game's real layering: travel weight, travel-clip pairs, and turn and jump
  crossfades, at weights 0, 0.25, 0.5, 0.75 and 1. Clip pairs are sampled together at each authored
  frame of normalised phase.

Poses come from the game's actual locomotion sampler, so the test uses the same palette code as the
client. Any failure fails CI and reports asset, clip, frame, weights, stage, code and command.

## Correctness evidence

1. **Reference first.** Before the exact constructor is deleted, a test captures its outputs on the
   typical and limit fixtures, seams, transition weights 0, 0.5 and 1, and refinement levels 2 to 4.
   They are committed as reference data.
2. **Float fidelity.** Each float position stays within `Clearance x MarginFraction` of the exact
   reference, the spread the exact construction already refuses to exceed for a conformed point.
   A core-corrected group that exceeds it is investigated, never accepted by widening the bound.
   Refusal codes match on the existing construction negatives.
3. **Oracle on float outputs.** Every fixture's float output passes exact geometry certification.
4. **Same bits.** The oracle candidate equals the staged runtime candidate for identical inputs.
5. **Scene behaviour.** Tests that asserted render-certification refusals are rewritten or removed.
   Each fallback route keeps a test through a construction refusal, in every pass, through CPU and
   GPU routes, outlines, shadows and temporal history. Incomplete submissions still fall back. The
   origin-magnitude guard has a positive and a negative case.
6. **Oracle negatives.** An over-tight clearance, a support escape and a recession-budget excess
   each fail with the correct stage and code.
7. **Allocation.** Warm staging, resolution and oracle checks allocate zero managed bytes.
8. **Native.** The hosted Coverage selection runs on Metal, Direct3D11 and Vulkan, with Vulkan
   synchronisation validation and the ledger artifact audit. Ghost envelopes, masks and negative
   controls still apply. Any of the 18 goldens that is no longer worst 0 receives a side-by-side
   inspection, and its reference is refreshed only after root review with the delta recorded. No
   tolerance is loosened.

## Gates and delivery order

The 2 ms for 32 typical actors CPU gate, the 15 percent native GPU gate and the zero-allocation gate
are unchanged.

After implementation, one hosted one-actor pilot with the existing unit and 64 plus 32 frame
conditioning needs fresh owner approval. If its per-actor cost makes the gate plausible, the first
32-actor pilot needs separate approval and decides the CPU gate. The GPU gate stays open until a
native GPU timing route exists. Nothing claims it.

Delivery order:

1. Owner approval of this amendment and its plan amendment.
2. Exact reference capture.
3. Float support-sheet lookups.
4. Float constructor in place of the exact constructor.
5. Scene runtime with Covered from completeness, and removal of pass capture and the render certifier.
6. `CoverageOracle` with the shared skin-and-construct function.
7. Test migration and native hosted verification.
8. The approved pilot.
9. Task 10: merge current main, full verification, documentation and API sweep, whole-branch review.
   The deferred Task 5 minors are settled there. Q2 and Q5 become moot once late refusal and pass
   capture are removed. Q4 and Q6 are reviewed.
10. Exact engine release with owner tag approval through the engine-rooted release handoff.
11. Grimhollow adoption: the blocking oracle CI test over real armour and clips, armour fit and
    catalog change, owner full-motion look and the final playtest.

## Superseded text

- Design section 7: runtime checks no longer apply to every submitted pose. The finite offline pose
  suite becomes the R2 oracle domain, and it certifies exactly rather than only finding failures.
- Design section 10 and the construction and render-error amendment: the required render-error
  context, pass capture before the first covered draw and the derived `WorldErrorBound` are replaced
  by the origin-magnitude guard.
- Design section 11: the budgets are unchanged. Their measured workload is the float runtime path.
- Design section 12: the synthetic oracle becomes a public engine API used by consumers.
- Plan amendment Task 5 and cost unit 4 text: staging true now awaits only the resolution
  completeness checks, not late certification. The Task 5 late-render-refusal, all-pass-matrix,
  stale-context and prepared-bounds tests are rewritten or removed under evidence item 5.
