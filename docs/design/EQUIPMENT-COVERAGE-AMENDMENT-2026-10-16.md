# Equipment coverage construction and render-error amendment

Status: owner approved. Implementation is pending and no release is authorized.
It amends sections 4, 7, 9, 10 and 12 of the
[approved design](EQUIPMENT-COVERAGE-DESIGN-2026-10-16.md).
The engine work is tracked by [#1418](https://github.com/APKiwiOrg/KhaozEngine/issues/1418).

## Outcome and evidence

Keep rigid equipment looking worn while preserving the closed host, exposed skin, authored rig
and animations. The existing core, collar, world recession budget, fixed topology and refusal
rules remain constraints. The owner has approved a small visible collar change, not unlimited
recession or hidden body removal.

Task 4 at `f4b713f2e` exposed two gaps in the approved implementation assumptions.

First, a pointwise target does not guarantee that a triangle between its target vertices stays
behind a convex crease in the actual sheet. The synthetic pyramid sheet passes at refinement
level 1 but fails at levels 2, 3 and 4. Recorded vertical gaps are approximately 0.0124, 0.0144
and 0.0160 for a required 0.02. The certification refusal is correct. Switching the positive
test to level 1 does not satisfy the intended refined-fixture gate.

Second, engine draws are relative to a latched scene origin, which can be zero, rather than
necessarily relative to each actor. A world-error bound based only on actor-relative geometry
cannot cover those submitted transforms. The actual render-frame inputs must be part of the
certificate. These are source and fixed-test observations, not performance measurements.

The independent review also identified intersection and ambiguity defects. Fixing those is
already required by the approved safety contract and proceeds separately from this proposal.

## Construction choices

Scores are engineering judgments from 1 to 10, not measured results. Coverage usefulness has
weight 3, fit preservation 2, proof clarity 2, runtime cost 1 and maintainability 1. The priorities
reflect the owner's close-fit objective while retaining a proof of the whole surface.

| Choice | Usefulness | Fit | Proof | Cost | Maintenance | Weighted total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| One local face-deficit correction | 9 | 9 | 9 | 6 | 7 | 76/90 |
| Minimum sheet height over each vertex star | 9 | 6 | 9 | 6 | 7 | 70/90 |
| Keep pointwise construction and refuse these poses | 3 | 9 | 10 | 10 | 9 | 66/90 |

The recommended face-deficit correction adds only the clearance shortfall of nearby triangles.
It leaves an already-clear candidate unchanged and keeps the existing full certificate as its
final gate. It adds a bounded overlay pass and needs a new numerical proof and positive tests.

The vertex-star minimum also gives a simple geometric proof. It can recede a vertex by the full
height variation across its neighborhood even when only a small crease shortfall needs fixing.
That can consume more budget and alter the exposed contour more. A star extending outside the
sheet also requires a bound for nonlinear nearest-boundary continuation.

Keeping the current construction costs least, but knowingly retains refusal of the shared
refined positive fixture and similar valid convex-sheet cases. It would require an explicit
change to the usefulness requirement. It is not the recommendation.

## Recommended construction

Keep the existing actual-sheet and nearest-boundary pointwise construction as the first pass.
Compute its full-weight candidate before applying a transition weight. Then perform exactly
one additional correction over full-strength core triangles inside the actual support domain.

For each core face F, let z_F(x,y) be its candidate's linear coverage-space height and h(x,y)
the actual support height. On the analytic overlay of F with the support triangles, compute a
certified upper bound for the maximum clearance shortfall:

```text
d_F = max(0, upper_bound(max over F of (z_F(x,y) - h(x,y) + C_cov)))
delta_v = max(d_F for core faces F incident to position group v)
corrected_z_v = candidate_z_v - delta_v
```

`C_cov` is the conservative world-clearance conversion already required by the design. The
upper bound includes the interval and final-position conversion errors needed for this extra
pass. A mathematically certified no-shortfall face contributes exactly zero. An uncertain face
may contribute a conservative finite correction or refuse, never silently accept ambiguity.

The difference of the two linear heights is affine on each overlay polygon, so its maximum
occurs at a polygon vertex. Use those actual overlays, not samples or a hull proxy. Core domain
containment is proved before computing the correction. A failure remains `CoreEscaped` or the
appropriate unsupported/numerical refusal.

Gather maxima by PositionGroup across every material and shading alias. A core face has strength
1 at every corner. Every corner of F therefore receives at least d_F. For barycentric weights
lambda_i in F, the corrected face height is at most:

```text
z_F - sum(lambda_i * delta_i) <= z_F - d_F <= h - C_cov
```

This is the exact-coordinate proof. Outward bounds and final-float certification must preserve
it in the implementation. A numerical result that cannot establish it is refused.

Only vertices incident to core faces receive this extra correction. Other collar vertices keep
the pointwise result. Collar faces adjoining corrected core vertices can change through those
shared corners, so every collar face still passes equipment and host checks. Lower-strength and
protected vertices are never independently moved to rescue a failed core. A position group
cannot belong to two positive-strength commands.

The total requested recession is pointwise recession plus the one correction. Check its actual
world length against the existing authored budget. Do not clamp, increase the budget or change
the map. Caps, folds, host intersections and all solid-clearance tests still apply. Failure uses
the current-pose uncovered fallback. No adaptive topology, iteration-until-clear loop or global
minimum plane is introduced.

Zero pointwise work and zero correction copy original data exactly. A vertex that needed no
pointwise movement can now move when its incident core face has an interior shortfall. This is
the material behavior change requiring approval. Extra recession remains local and budgeted,
but its appearance on the actual consumer armour remains a later art and motion acceptance gate.

Apply transition weight once to the complete full-weight requested displacement. Weight below
1 never claims full core clearance. Recompute normals from the final surface using the accepted
gradient path. Preserve exact protected and zero-work bytes and all fixed rational seams.

As in the approved design, certification covers each actually submitted surface, including each
submitted transition weight. It is not continuous collision detection between two submissions.
A separated final pair does not prove that an imagined continuous deformation path was clear.
Do not present that separate swept-path question as a final-state intersection regression.

## Actual render-frame error contract

The final certificate must use the exact values the renderer submits. Add a required immutable
render-error context bound to scene/frame generation, the compiled host, pose and actual draw
matrices. Missing or stale context cannot issue a successful drawable coverage certificate.
Pure geometry diagnostics may remain available under an explicit non-display contract.

The context captures the latched origin, actual origin-subtracted actor and equipment model
matrices, and the actual position-transform matrices for each participating pass. Use actual
ViewProjection values, not an assumption that a custom camera's View and Projection multiply
to its independently supplied ViewProjection. Include jitter/backend correction, cascade atlas
transforms and selected point-face matrices exactly where the shader applies them.

Bound finite precision from the actual position-operation sequence. The CPU-prepared route
generally multiplies model then pass matrix, rather than a folded model/MVP. A conservative
interval bound includes origin-subtraction residual, float dot-product rounding, matrix
composition residuals, and known position-dependent shader terms. Use prepared/final geometry
bounds, not rest bounds with an arbitrary safety factor. An unsupported operation sequence,
overflow, unstable inverse used by the bound or insufficient authored margin refuses coverage.

For pass projection roundoff, bound its equivalent geometric displacement through the supplied
transform where that inverse is certifiable. Do not infer such a bound from camera distance
alone. Accumulate outward errors and take the maximum required by the participating passes.
The reported WorldErrorBound must be that derived value, not a fixed actor-relative heuristic.
The consumed error must fit the existing safety-margin policy without widening caps or budget.

This numerical certificate does not certify depth-buffer resolution, raster coverage, deliberate
shadow pancaking or intentional silhouette expansion. Those have separate native rendering and
appearance gates. Their presence must be explicit, never hidden in a claimed exact world bound.
Temporal history has its own prior-final-position and rebasing validation. Invalid history is
rejected for temporal reuse, never used to freeze the current actor.

Task 5 must capture all required matrices before the first covered host or equipment draw.
Today final cascade and point-face matrices are populated after CPU skinning. Split matrix
preparation from draw execution as necessary, then evaluate/certify and submit one canonical
surface. No pass may submit an uncertified surface while later pass metadata is still unknown.
Use preallocated storage and keep the existing uncovered route intact.

Source anchors are [render origin](../../KhaozEngine.Render3D/Scene3D.RenderOrigin.cs),
[render ordering](../../KhaozEngine.Render3D/Scene3D.cs),
[model shader](../../KhaozEngine.Render3D/Internal/ShaderSources.Model.cs),
[cascade matrices](../../KhaozEngine.Render3D/Scene3D.CascadeShadowPass.cs),
[point matrices](../../KhaozEngine.Render3D/Scene3D.PointShadowPass.cs), and
[motion preparation](../../KhaozEngine.Render3D/Scene3D.MotionTarget.cs).

## Acceptance and impact on the existing plan

Task 4 remains open. Update its construction and context interfaces under the
existing subagent-driven plan. Preserve all source-verified safety corrections and independently
review the amendment implementation before Task 5 proceeds.

Required fixed tests are:

- The same base panel at levels 2, 3 and 4 passes with positive core work, exact protected data,
  no new topology/intersection defect and the required independent whole-face clearance.
- The uncorrected pointwise candidate remains a failing interior-clearance negative.
- Already-clear flat and concave candidates receive no extra correction. The corrected candidate
  stays within its budget and caps, or refuses with the exact reason and no partial output.
- Shared position groups, mixed material/shading seams, disjoint commands and transition weights
  preserve their accepted contracts. The closed-host case retains the protected rear.
- Far translations with zero origin, rebased frames, independent custom camera VP, and relevant
  cascade/point matrices exercise actual error inputs. A missing/stale context or excessive error
  refuses. A nearby or correctly rebased positive succeeds.
- Known shared-corner crossings, baseline-contact crossings and tiny projected overlaps refuse.
  Legitimate topology contact remains valid.

Task 5 supplies real render contexts and proves parity across the covered CPU route. Task 6
binds exact host/equipment identities. Tasks 7 and 8 retain temporal and native backend evidence.
Task 9 measures the additional overlay and context work only under separately authorized cost
testing. The 2 ms and GPU overhead budgets are unchanged and remain unproved.

No release, load test, consumer pin, new art dispatch or relaxation of full-motion acceptance
follows from approving this amendment. The existing final verification and owner playtest remain.
