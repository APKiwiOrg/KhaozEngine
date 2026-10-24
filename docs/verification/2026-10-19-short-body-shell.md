# Short-body shell verification

Task 1, Task 2a, partial Task 2b and Task 2b.1 certificate retention of the [short-body shell plan](../superpowers/plans/2026-10-19-short-body-shell.md).
Requirements are [SB-2 to SB-4 and acceptance group A](../design/CONTACT-SHORT-BODY-SHELL-2026-10-19.md).
This record covers value contracts, independent oracles, finite planar Distance certificates
and the supported planar sweep strata recorded below. Complete Sweep coverage, Contacts,
Penetration, selected-world traversal, controller adoption, navigation, the full suite and
release acceptance remain unfinished. Earlier partial checkpoints below are historical.

## Task 1 requirements and tests

| Requirement | Evidence in ConvexQueryContractTests |
| --- | --- |
| Exact represented dimensions and canonical base margin | ConeCylinderValuePreservesAllRepresentedBits |
| Closed shape kind, finite positive radius, ordered noncollapsed dimensions, exact touching margin | InvalidOrCollapsedShapeRejects |
| Continuous disc extrema, axial directions, cone and cylinder feature ties, exact Minkowski margin | ContinuousCircleSupportEnclosesEveryAzimuth |
| Represented banded-angle clearance and required duck/cow surplus | RequiredBodyPlaneSurplusIsProved |
| Default refusal and empty selection are distinct from usable evidence | DefaultsRefuseWithoutUsableFields, DistanceDistinguishesEmptySeparatedAndOverlapWithRawIdentity |
| Ordered impact bounds and an operational hit, without a partial clear prefix | SweepCarriesAnOrderedBracketAndOperationalHit |
| SS-R5 raw sweep identity distinguishes dynamic bodies with identical operational hits | SweepHitsDistinguishTwoDynamicBodiesWithIdenticalOperationalHits |
| SS-R5 raw sweep identity distinguishes coincident triangles and compound children under one static | SweepHitsDistinguishTwoCoincidentMeshTrianglesUnderOneStatic, SweepHitsDistinguishTwoCoincidentCompoundChildrenUnderOneStatic |
| SS-R5 zero identity on Clear and Unresolved | SweepWithoutHitRejectsEveryNonzeroIdentityComponent |
| Complete contact counts, atomic refusal, separate penetration proposal | ContactCapacityRefusalIsAtomicAndPenetrationIsSeparate |
| Finite evidence and unchanged encoded spatial error ceiling | NonfiniteResultEvidenceRejects, EvidenceCannotExceedTheExistingBudgetOrLeakPartialFields |
| Optional capability with immutable shape passed by readonly reference and existing filter | OptionalCapabilityUsesValueShapeByReadonlyReference |
| Explicit refusal for invalid oracle operands | OracleRefusesInvalidShapesDirectionsAndAngles |

The oracle independently reads represented binary32 operands as exact rationals. The maximum
of the axial point and two continuous disc supports follows from the circle's Cauchy-Schwarz
bound, with attainment in the radial direction. Exact Pythagorean generators exercise extrema
and feature ties without replacing the discs by a polygon. Integer square-root bounds enclose
radical terms, including the spherical margin. Forty rational Taylor terms and the alternating
remainder bound enclose sine and cosine. Rational comparisons against binary64 encodings round
the final interval endpoints outward. No production support map, feature selection or root
isolation supplies an expected answer.

## Task 1 red and green commands

All commands run synchronously, one at a time, from the task worktree. Build and test output is
kept under `/tmp/khaozengine-short-shell/` for this run. No timing or load proof is performed.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ConvexQueryContractTests'
```

- Initial red, exit 1. CS0246 names the intentionally absent ConvexQueryShape. No tests executed.
- First assertion-red attempt, exit 1. CS1739 exposed an unavailable xUnit IsType overload in the
  new reflection test. This is a test-authoring error, not accepted behavioral evidence. The test
  was corrected to the repository's GetMethod and NotNull pattern before rerunning.
- Behavioral red, exit 1. InvalidOrCollapsedShapeRejects failed with no ArgumentException thrown
  for a nonfinite radius before the constructor guard was implemented. Nine tests passed and one
  failed. This is the behavioral red for the value validation change.
- Focused green, exit 0. Twelve passed, zero failed and zero skipped. All four tests named in
  the brief ran. The task's referenced projects built in Release without warnings or errors.

## Task 1 represented clearance

The angle fixture uses `MathF.PI * 45f / 180f + MathF.PI * 3f / 180f`, matching the default slope
and hysteresis operand order in MoveTuning. Head and midpoint use `2f * H` followed by
`0.5f * (S + T)`. Clearance with margin zero is the base body's plane clearance. Clearance with
margin `0.001f` is the surplus after the unchanged touching skin.

The spec's nominal real-arithmetic clearance is 0.1524798078 m for duck and 0.1006698544 m for cow.
Its nominal surplus is 0.1514798078 m and 0.0996698544 m. These are display values, not tolerances.
The represented angle is `0.83775806f`, bits `0x3F567750`.

| Body | Represented base clearance enclosure in metres | Represented surplus enclosure in metres |
| --- | --- | --- |
| Duck | [0.15247978665861672, 0.15247978665861675] | [0.15147978661111927, 0.1514797866111193] |
| Cow | [0.10066982218487296, 0.10066982218487297] | [0.0996698221373755, 0.09966982213737552] |

Both represented surplus lower bounds prove the spec's downward display bounds, 0.15147 m
and 0.09966 m. Their differences from nominal figures are due to represented dimensions,
angle and margin. These enclosures do not introduce an admission epsilon.

## Task 1 regression and review

SS-R2 requires the unchanged capsule shell families and seven serial contact-navigation baselines.
The serial baseline fixture defaults to degree 1 through NavBakeExecution.Degree. Existing
capsule production and test sources are outside this task's diff. No absolute work counters are
introduced, preserving SS-R3.

```bash
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ConvexQueryContractTests|FullyQualifiedName~ShellGeometryTests|FullyQualifiedName~GroundCoreTests|FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~BepuCertifiedCapsuleSweepTests|FullyQualifiedName~CertifiedRotatedBoxSweepTests|FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedCurvedSweepTests|FullyQualifiedName~CertifiedCompoundSweepTests|FullyQualifiedName~CertifiedMeshSweepTests|FullyQualifiedName~CapsuleSweepValueTests' --logger 'console;verbosity=detailed'
```

Exit 0. All 258 passed, with zero failed or skipped. Detailed logging records the oracle's
represented enclosures above. The no-build selection uses the just-built focused-green binaries.

```bash
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationBaselineTests.SerialBakeMatchesNormalizedFullFileBaseline'
```

Exit 0. All seven pinned serial file baselines passed, with zero failed or skipped.

Value constructors validate structural evidence only. Request coverage, unit-normal proof,
witness membership, deepest-member selection, raw-ID tie breaking and lifetime remain backend
proof obligations. No new tolerance, skin, arrival rule, footprint rule or layer identity is added.
Public API analyzer baselines are not configured for the Physics project, so none are updated.
Living API documentation and version reconciliation remain Task 12.

Self-review checked the immutable inputs, default refusals, zero-output incomplete results,
finite ordered brackets, exact margin restriction, independent oracle arithmetic, closed feature
ties and unchanged capsule sources. No unresolved Task 1 concerns were identified.

Staged guards all exit 0: `sh scripts/check-dashes.sh staged`,
`sh scripts/check-prose.sh staged`, `sh scripts/check-file-size.sh staged` and
`git diff --cached --check`. The full solution build, full suites and format verification
remain Task 12, as required by the plan.

## Task 1 fix round 1, SS-R5

ConvexSweepResult now retains the supplied ConvexContactIdentity beside its existing SweepHit.
Clear and Unresolved require zero identity. Equal operational hits retain distinct raw dynamic
body handles, coincident mesh triangle feature IDs and coincident compound child indices.
The three provenance controls compare the full raw identities and the resulting value equality.
The no-hit control rejects each nonzero identity component independently for both statuses.
These are value-contract tests. Installed-target publication remains later backend work.

The same focused command above produced the following fix-round evidence:

- Missing-API red, exit 1. CS1061 and CS1729 identify the absent Identity property and six-argument
  constructor. No tests executed. Log task-1-fix-r1-red.log.
- Behavioral red, exit 1. After adding the field and constructor but before no-hit validation,
  SweepWithoutHitRejectsEveryNonzeroIdentityComponent failed for Clear and Unresolved because
  no ArgumentException was thrown. Fifteen passed, two failed, zero skipped.
  Log task-1-fix-r1-behavior-red.log.
- Focused green, exit 0. Seventeen passed, zero failed, zero skipped after requiring zero
  identity on both no-hit statuses. Log task-1-fix-r1-green.log.
- The SS-R2 Game.Tests selection above, with the freshly built binaries and without detailed
  logging, exited 0. All 263 passed, zero failed, zero skipped.
  Log task-1-fix-r1-game-regressions.log.
- The same seven serial navigation baselines above exited 0. Seven passed, zero failed,
  zero skipped. Log task-1-fix-r1-navigation-regressions.log.

All successful build/test logs contain no compiler warnings or errors. Every command ran
synchronously through build-slot, one at a time. No tolerance, capsule source or capsule API
changed. The plan's producing and consuming interfaces and SB-4 now record SS-R5. Physics has
no configured PublicAPI baseline to update. Full solution verification remains Task 12.

## Task 2 partial infrastructure

At the Task 2 infrastructure checkpoint, the change added query values, rounded support
proposals, sticky work accounts, query-private scratch and bounded exact polynomial roots.
That checkpoint did not implement ConvexFeatureKernel, ConvexPlanarFeatures or ConvexLeafEvidence.
Its root tests alone are not evidence for a cone-cylinder sweep, distance or penetration query.
The later Task 2a section below records the partial planar distance construction.

The root utility imports exact rational coefficients into a fixed 2048-coefficient scratch
arena, reduced only in tests. Every integer intermediate is checked against the existing
4096-bit ceiling. Polynomial division computes the square-free part and a sign-preserving
Sturm chain. Original radical signs and closed polynomial domains are checked at isolated
roots, with exact GCD checks retaining zero predicates. An identically zero squared radical
also retains the unsquared coefficient boundaries. An undecidable sign, exhausted account
or exhausted arena returns default evidence and poisons the primitive account. The arena is
restored on success and refusal. Fraction widths are compared with exact rational arithmetic.

Root expectations use literal factorizations and closed equations. They cover double roots,
a non-dyadic repeated root, rejection of a root introduced by squaring, closed feature ties,
a quartic's later feasible root, a degenerate squared radical, dyadic circle tangency and both
adjacent floats. Zero-cell admission, exact overflow, bounded scratch and scratch restoration
have deterministic controls. No absolute counter depends on SIMD or scheduling.

The off-axis support proposal is compared to Task 1's independent SupportBounds for represented
duck and cow. This checks proposal encoding only. At that checkpoint the installed-target
snapshots and oracle extensions were absent, along with attained planar witnesses, normal-cone
certificates, minimum member translation and SS-R6 dispatch controls. Task 2a below adds a
partial distance producer and dispatch controls. Minimum member translation remains Task 2c.

### Task 2 observed reds

All runs use the worktree and synchronous build-slot command prescribed by the brief.
Logs are under /tmp/khaozengine-short-shell/.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ConvexFeatureKernelTests'
```

- task-2-red.log, exit 1. Missing query/root/support types, no tests executed.
- task-2-behavior-red.log, exit 1. Five failed and one passed against the root stub.
  ClosedDoubleRootIsContact and the non-dyadic, radical-sign and closed-domain controls failed
  completeness assertions. The exhaustion control failed its sticky-refusal assertion.
- task-2-root-green.log, exit 1. Five passed and one failed. The overflow fixture was wrong,
  its quadratic stayed below the integer ceiling. Replaced it with a cubic that exceeds it.
- task-2-cell-red.log, exit 1. Seven passed and three failed. Both zero-cell initial-root
  controls exposed an uncharged early return. The adjacent-float control also expected an
  unnecessarily tight bracket. Its valid enclosure could end at the stationary midpoint.
  Corrected that test to distinguish the earlier root without requiring a new error budget.
- task-2-arena-red.log, exit 1. Eight passed and three failed. Both zero-cell controls and
  the new fixed-arena control failed before admission charging and the arena were implemented.
- task-2-roots-green.log, exit 0. Eleven passed, zero failed or skipped after those changes.
- task-2-game-regressions.log, exit 1. Of 274 selected tests, 273 passed. The new scratch-reuse
  control incorrectly assumed a linear root needed at most four coefficient slots. Replaced
  its reuse fixture with an identically zero equation. All 263 existing controls passed.
- task-2-unsquared-red.log, exit 1. Eleven passed and one failed. The original inequality
  (t-1/2) + (t-1/2)sqrt(1) becomes feasible at 1/2 even though its squared equation is
  identically zero. The solver incorrectly reported an empty interval. Retaining its
  unsquared coefficient boundaries fixes the actual root-construction defect.

### Task 2 final focused Game selection

```bash
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ConvexFeatureKernelTests|FullyQualifiedName~ConvexQueryContractTests|FullyQualifiedName~ShellGeometryTests|FullyQualifiedName~GroundCoreTests|FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~BepuCertifiedCapsuleSweepTests|FullyQualifiedName~CertifiedRotatedBoxSweepTests|FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedCurvedSweepTests|FullyQualifiedName~CertifiedCompoundSweepTests|FullyQualifiedName~CertifiedMeshSweepTests|FullyQualifiedName~CapsuleSweepValueTests' --logger 'console;verbosity=detailed'
```

Exit 0. All 275 passed, zero failed or skipped, including the twelve roots/support tests and
all 263 existing value-contract and capsule controls. Release compilation reported no warnings
or errors. Log task-2-game-final.log. Existing capsule production and test files are unchanged.

For the accepted polynomial fixtures that log counters, observed high-water maxima were 25
cells, 221 features and 108 scratch coefficients. These are root-work observations, not maxima
for installed planar targets. No test pins those values. Cell, feature and coefficient capacity
refusals remain enforced independently of diagnostics.

```bash
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationBaselineTests.SerialBakeMatchesNormalizedFullFileBaseline'
```

Exit 0. All seven pinned normalized full-file baselines passed, zero failed or skipped.
BuildFixture uses null execution and NavBakeExecution.Degree selects 1. Release compilation
reported no warnings or errors. Log task-2-navigation-final.log. All baseline sources and
hashes remain unchanged. Full solution build, full suites, formatting, version reconciliation
and adoption acceptance remain Task 12. Task 3 must wait for the missing Task 2 kernel.

Staged guards all exited 0 after explicit staging and inspection of the complete staged diff:
sh scripts/check-dashes.sh staged, sh scripts/check-prose.sh staged,
sh scripts/check-file-size.sh staged and git diff --cached --check. Only the seven new
Task 2 source/test files and this verification record are staged. No capsule source changed.

## Task 2a partial planar distance construction

This section records the historical incomplete 2a implementation at 447e3d8a5. The continuation
below supplies the remaining circular edge construction, coverage lemma and oracle comparisons.
A missing or unproved candidate returns false with default evidence. It never proves Clear.
Sweep, Contacts and Penetration requests also return false with default evidence until 2b/2c.

The planar kernel reads installed topology and the represented scalar/SIMD coefficient map.
It uses that common forward affine map exactly, with no normalized quaternion or assumed
rigid inverse. No GJK proposal, inscribed polygon, sphere cloud or AABB supplies a certificate.
Exact quadratic-field arithmetic retains A + B sqrt(D), checks the unsquared signs before
squaring and applies the existing 4096-bit intermediate ceiling. Candidate/certificate work
shares the sticky primitive feature and cell accounts. The existing capsule files are unchanged.

For an attained query point q and target point t, n = t - q. A candidate is accepted only
when q lies in the shell and t lies in its finite target feature. The target support proof
checks n dot (v - t) >= 0 for every installed target vertex v. The query support proof checks
n dot q >= S ny and, at both k = C and k = T,

```text
gap = n dot q - k ny
gap >= 0
gap^2 >= R^2 (nx^2 + nz^2)
```

These original-sign inequalities bound the two entire continuous discs. Together with the
apex they prove support for the complete convex hull, including the sharp apex and shoulder
normal cones. The common supporting normal proves global distance by convexity. In particular,
for any x in the query and y in the target, n dot (y - x) >= |n|^2. Cauchy-Schwarz then gives
|y - x| >= |n|, attained at q,t. No convergence criterion or feature omission enters that proof.

The existing margin is applied as the exact Minkowski sphere. Separated witnesses grow by
m n / |n| and distance becomes |n| - m. When |n| <= m, t is an attained common point instead.
Base-solid intersection and containment candidates likewise publish only attained common points.
The full outward distance bracket and both witness encoding errors share the existing 0.001 m
budget. Bounds in ConvexLeafEvidence enclose the mathematical attained points. Its Vector3
representatives do not supply operands to the exact normal-cone predicates.

Local provenance identifies the captured face, edge or vertex. ConvexPlanarDispatch binds the
exact leaf and raw ConvexContactIdentity before invoking the kernel. Its opaque evidence belongs
to that dispatch. Another dispatch cannot attach it even for coincident geometry or the same
geometry with a different identity. InstalledSweepLeaf is unchanged under SS-R6.

The independent oracle snapshots Bepu box dimensions, hull vertices, face loops and composed
represented poses directly. Its current distance oracle proves Cartesian boxes and box-shaped
hulls by reducing to the meridian polygon. It refuses other snapshots instead of comparing
against their AABB. This restriction is an explicit remaining oracle gap.

The initial API red failed with CS0246 for missing ConvexLeafEvidence. A fixture setup error
then failed before the kernel and is excluded from behavioral-red evidence. After explicitly
publishing the captured installed fixture pose, seven tests failed against the kernel refusal
stub. The first implementation run passed five and failed the two overlap/tangency controls.
That run exposed exact-zero normal encoding through an interval square root. The correction
uses exact rational endpoint residuals, preserving zero without adding a tolerance.

### Task 2a verified comparisons and regressions

All fourteen planar tests pass. Duck and cow are independently compared with installed boxes
and box-shaped hulls, including off-axis vertices. The wrapper fixture reads a nonzero native
hull centroid. The explicit oblique circle-edge case returns default evidence and false.
The thin blocker lies inside the real circle and beyond a chord whose endpoints are rational
rotations of the 3:4:5 radius by cos=99/101 and sin=20/101. Thus the test exercises volume that
an inscribed polygon can omit. Exact dyadic tangency and both adjacent binary32 positions
retain distinct zero/positive distance evidence. The original margin is tested for separated
and overlapping Minkowski distance. The normal-cone oracle currently handles the base shape
only. Margin-normal oracle checks remain unfinished.

| Query regime | Independently compared finite target strata | Observed maximum cells | Observed maximum features |
| --- | --- | ---: | ---: |
| Apex | Face, vertex | 29 | 53 |
| Lower cone | Edge, vertex | 92 | 134 |
| Shoulder circle | Edge, vertex | 107 | 149 |
| Cylindrical side | Face, vertex | 12 | 28 |
| Head disc | Face, vertex | 9 | 25 |
| Head rim | Edge, vertex | 73 | 101 |

These are fixture observations, never pinned counter expectations. They do not prove every
cross-product of regimes or generic oblique targets. Those comparisons remain part of the
uncompleted 2a acceptance work.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore -p:BuildProjectReferences=false --filter 'FullyQualifiedName~ConvexPlanarDistanceTests' --logger 'console;verbosity=detailed'
```

Exit 0. Fourteen passed, zero failed and zero skipped. A preceding oracle-only red failed the
far-query minimum initializer, with twelve of thirteen passing. Initializing from the first
actual meridian-segment distance corrected it. The test-only builds reuse the production
references compiled in the prior successful Release focused run.

```bash
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ConvexPlanarDistanceTests|FullyQualifiedName~ConvexFeatureKernelTests|FullyQualifiedName~ConvexQueryContractTests|FullyQualifiedName~ShellGeometryTests|FullyQualifiedName~GroundCoreTests|FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~BepuCertifiedCapsuleSweepTests|FullyQualifiedName~CertifiedRotatedBoxSweepTests|FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedCurvedSweepTests|FullyQualifiedName~CertifiedCompoundSweepTests|FullyQualifiedName~CertifiedMeshSweepTests|FullyQualifiedName~CapsuleSweepValueTests' --logger 'console;verbosity=detailed'
```

Exit 0. All 289 passed, zero failed and zero skipped. This includes the fourteen new planar
controls, twelve root/support controls, seventeen value contracts and 246 unchanged capsule
controls. The final source cleanup removed two unused imports only.

```bash
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ContactNavigationBaselineTests.SerialBakeMatchesNormalizedFullFileBaseline'
```

Exit 0. All seven pinned degree-1 normalized file baselines passed with zero failures or skips.
This run rebuilt the final Bepu source. No existing capsule, installed-geometry, locomotion,
movement or navigation baseline source changed. No warnings or errors occurred in successful
Release compilation. No full suite, stress run, timing proof, world bake or live run was used.

## Task 2a continuation under SS-R8

The continuation starts from clean feature/short-body-shell at 447e3d8a5. It reuses existing
certified machinery instead of constructing another circle/line elimination polynomial.

- CircularProjection factors the exact operation graph from CapsuleCylinderFeatures.Evaluate.
  The existing capsule proposal calls its binary64 policy with the same operand order, radial
  clamp and axial clamp. Exact quadratic and outward interval policies evaluate that same
  projection for the new producer. Zero half length is the closed circular disc.
- ConvexCircularEdges uses SupportCurvedRootRegions through its existing
  ISupportCurvedRootFunction interface. It retains the original unsquared derivative. No
  new quartic coefficients, squared-root sign conventions or copied boundary algebra are added.
- SupportCurvedWork optionally admits each event through the enclosing primitive account.
  Existing support callers have no callback. Their work policy and arithmetic are unchanged.
  Root and refinement events charge shell cells, and evaluations charge shell features.
- SupportCurvedRootRegions accepts a lower live-cell capacity. Its default remains 1024.
  The shell uses its existing MaximumDepth + 1 pending-cell capacity. No ceiling increases.
  The 4096-bit exact ceiling, sticky exhaustion and default-evidence refusal remain enforced.

### Certified circular edge stationarity

Let t(s) = a + s*v, 0 <= s <= 1, and let D be the closed radius-R disc at height H.
The capsule projection supplies n(s) = t(s) - projection_D(t(s)). Define
f(s) = dot(n(s), v), the derivative of half the squared distance to D.
The projection onto a closed convex disc is firmly nonexpansive. Its horizontal residual is
monotone and 1-Lipschitz, while its vertical residual is exactly t.Y-H. Consequently every
one-sided slope of f lies between v.Y squared and dot(v,v). For v.Y != 0, f is strictly
increasing on the whole closed segment, including the disc boundary and the axis. It has at
most one zero. Exact endpoint signs establish absence or existence, and the existing root
regions preserve that existence bracket during interval Newton refinement. The original
radical sign is evaluated exactly at dyadic endpoints with the quadratic arithmetic policy.
A double root of a squared elimination equation therefore cannot disappear through a missed
sign crossing. The unsquared monotone function is the equation actually certified.

At the enclosed root, t is attained on the original installed edge, q is attained on the
entire circle when radial(t) > R, and n is their actual difference. The root identity gives
dot(n,v) = 0 exactly, rather than a near-zero sampled residual. For the rim, n.Y >= 0 proves
the shell normal cone. For the shoulder, n.Y <= 0 and
(C-S)*n.Y + R*(radial(t)-R) >= 0 prove its cone. These are closed supporting inequalities
for all three shell generators. Each target vertex satisfies dot(vertex-a,n) >= 0. Offsets
parallel to v have exact zero residual by the root identity, including both edge endpoints.
All other inequalities use outward enclosures. An undecided sign refuses the candidate.
These two normal cones prove the global distance and the dual separating support plane.
No root midpoint or point sampled around the circumference authorizes publication.

Horizontal edges use the existing exact radial stationary parameter. Roots exactly at a
dyadic endpoint also go through the exact point/KKT producer. Head-disc, cone-generator and
cylinder-generator constructions own the corresponding closed ties. The encoded witness
bounds enclose the attained root pair. Both witness encoding errors and the complete distance
bracket still share the existing 0.001 m evidence budget.

### Closed feature-pair completeness lemma

Let K be the represented closed cone-cylinder and T the convex hull of the installed finite
planar target's represented vertices. Both are compact convex solids. A positive minimum is
attained at q in K and t in T with n = t-q. Global optimality is equivalent to n belonging
to K's outward normal cone at q and -n belonging to T's outward normal cone at t.
Thus it suffices to find one attained pair with these two support inequalities. The kernel
can return the first such pair. Rejected proposals never establish a minimum or separation.

The following table enumerates all six closed shell features against each target stratum.
Feature boundaries are included in every incident construction, not discarded as open domains.

| Closed query feature | Finite target face | Finite target edge | Target vertex |
| --- | --- | --- | --- |
| Apex | Axial support and plane projection, or incident boundary stratum | Closed projection onto the segment | Attained apex and exact support inequalities |
| Lower cone | Exposed generator segment, with boundary reduction below | Original unsquared cone stationarity and its quadratic roots | Exact meridian projection onto the closed generator |
| Shoulder circle | Continuous radial support in the face normal | Certified disc root for oblique edges, radial stationarity for horizontal edges | Exact radial projection at C |
| Cylindrical side | Exposed vertical generator, with boundary reduction below | Radial stationarity plus closed C/T height crossings | Exact radial projection at the target height |
| Head disc | Axial exposed disc, with boundary reduction below | Horizontal radial minimum inside the disc, or closed head-height intersection | Vertical projection inside the full disc |
| Head rim | Continuous radial support in the face normal | Certified disc root for oblique edges, radial stationarity for horizontal edges | Exact radial projection at T |

For a target vertex these are the complete point-to-meridian polygon projections. Their
normal-cone predicates select the closed feasible projection without averaging seam normals.
For a target edge an interior minimizing parameter has zero derivative. Apex projection,
cone stationarity, cylinder radial stationarity, head-disc projection and the two certified
disc roots exhaust the table. Segment endpoints reduce to target vertices. A constant cone
stationary equation gives a flat minimizing interval. If an endpoint is in that interval its
vertex certificate suffices. If both interval boundaries lie inside the edge, the closed apex
projection is attained. The circular/cone or circular/cylinder ties also belong to the exact
incident cone or cylinder construction. Axis-parallel edges reduce to radial constancy and
the closed height crossings. An edge through the apex retains its exact closed intersection.

For a target face, its inward normal exposes either one of the table's unique query points,
a cone generator, a vertical generator or the head disc. A unique support point has one
orthogonal projection onto the face plane, checked against the original finite polygon.
If the chosen representative of an exposed generator misses the finite face, any nonempty
intersection of their projected closed domains meets a polygon edge unless the whole generator
is inside the polygon, in which case its midpoint is inside. Those edge or vertex pairs are
already enumerated. For the head disc, either the projected centre is inside, a polygon vertex
is in the disc, or a polygon edge crosses it. The radial segment minimum detects the last
case. A target normal on a face/edge boundary has this same higher-dimensional face
certificate, so an uncertain interval residual cannot require accepting a sampled equality.

Zero distance needs a separate intersection argument. Target vertices inside K are attained
common points. Target edges meet the cone/cylinder through closed quadratic intersections or
the head/apex through closed height crossings, including repeated roots and tangencies.
If no target edge meets K but a face meets it, the entire convex plane section of K lies
inside that finite face. The segment between K's opposing attained supports crosses every
plane between their support values. Its exact closed intersection with this plane is in K,
and finite-polygon membership proves it is in T. This is the new plane-section candidate.
If the two boundaries never meet, one solid contains the other. Target containment is detected
by its vertices, and shell containment by an admitted axial point. This exhausts overlap.

For a nonzero Margin m, the distance is max(0, distance(K,T)-m). When positive, moving q by
m*n/|n| gives the attained grown witness with the same supporting normal. Otherwise t itself
is an attained common point. This uses the exact admitted margin, not an additional skin.

The lemma is about candidate coverage and certified answers. Existing finite arithmetic,
work, installed-source and encoded-error limits can still refuse atomically. It does not
promise success after an exhausted account or an undecided exact/interval predicate. Sweep,
Contacts and Penetration remain atomic refusal under SS-R7, and world traversal remains Task 3.

### Independent oracle coverage

The Cartesian reduction remains unchanged in meaning. GeneralEntry handles arbitrary installed
convex snapshots with their actual represented vertices and face topology. An independent
numeric GJK proposes target barycentrics and separating directions only. Rational, normalized,
nonnegative barycentrics give an attained target point. Its independently computed exact
meridian-distance upper bound supplies the primal certificate. Exact continuous three-generator
support and the minimum over all target vertices supply the dual lower bound. Installed face
normals provide additional exact separating directions when the numeric simplex converges
slowly at a broad face. Neither endpoint calls production support, feature, transform or root
code. The oracle finishes when both endpoints fit the same outward binary32 encoding bracket,
with no new distance tolerance. A failed proposal returns unavailable evidence. No AABB or
sampled/inscribed circle stands in for the installed shape.

All eighteen feature-pair cells in the preceding table have independent distance controls in
ConvexFeaturePairCoverageTests. Their observed cell/feature maxima are diagnostic observations,
never pinned expectations.

| Query feature | Face cells/features | Edge cells/features | Vertex cells/features |
| --- | --- | --- | --- |
| Apex | 6/22 | 94/131 | 22/42 |
| Lower cone | 2/18 | 94/134 | 24/44 |
| Shoulder circle | 2/18 | 95/136 | 25/45 |
| Cylindrical side | 2/18 | 70/95 | 12/28 |
| Head disc | 4/20 | 71/99 | 9/25 |
| Head rim | 5/21 | 75/103 | 13/29 |

ConvexGeneralPlanarTests additionally checks both duck and cow at six regimes against boxes
and non-box tetrahedral hulls, each rotated about the oblique (1,2,3) axis at two represented
angles. These are 48 independent installed-target comparisons. ConvexCircularEdgeTests checks
shoulder/rim edge roots for boxes and hulls with both base margin and the existing 0.001 m
margin. The original much thinner oblique box regression now produces certified evidence too.

The four requested degenerate controls cover an edge parallel to the axis, an edge through
the apex, a face containing the rim plane and coincident shoulder tangency. A fifth control
reproduces an oblique face section missed by all three axial samples and both orthogonal
support projections. Its failing kernel assertion precedes the exact section construction.
The lowered-work controls derive their limits from one same-run probe. They prove sticky
root/refinement exhaustion and clean scratch reuse without CPU- or schedule-dependent pins.

NormalConeContains now handles Margin by exactly subtracting m*n/|n| in an independent
quadratic field, proving membership of the resulting base witness and all three complete
support inequalities. Zero normals require exact grown membership from closed meridian
features. Exact Pythagorean normal fixtures cover apex, cone, shoulder, cylinder, head and
rim, including nonunit normals. Adjacent-float and wrong-cone inputs are rejected without
repairing the supplied witness. Arbitrary irrational production witnesses remain interval
encodings. This exact Vector3 oracle does not reinterpret rounded encodings as exact surfaces.

### Continuation verification

Every build/test command is synchronous through build-slot, one at a time, in Release.
Logs are under /tmp/khaozengine-short-shell/. No full suite, timing/load run, live run,
world bake, push, merge, branch change, reviewer or subagent dispatch was performed.

The focused command form is below. Every row lists its actual label, selection and exit.
The selection is passed as FullyQualifiedName~ followed by the listed test class names,
joined with |FullyQualifiedName~. Test-only rows add -p:BuildProjectReferences=false.

```bash
build-slot --label LABEL -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'SELECTION'
```

| Label and log suffix | Selection | Test-only | Exit and result |
| --- | --- | --- | --- |
| short-shell-2a-circular-red, continuation-circular-red.log | ConvexCircularEdgeTests | No | 1, two kernel refusals and two too-thin hull installation fixture errors |
| short-shell-2a-circular-red-fixed, continuation-circular-red-fixed.log | ConvexCircularEdgeTests | Yes | 1, four intended kernel refusals after correcting hull thickness |
| short-shell-2a-circular-first, continuation-circular-first.log | ConvexCircularEdgeTests | No | 1, four intended unavailable-oracle assertions after the kernel certified the roots |
| short-shell-2a-oracle-first, continuation-oracle-first.log | ConvexCircularEdgeTests, ConvexPlanarDistanceTests | Yes | 0, 18 passed |
| short-shell-2a-general-margin-red, continuation-general-margin-red.log | ConvexGeneralPlanarTests, ConvexMarginOracleTests | Yes | 1, five passed and three failed, two margin refusals and one fixture assertion incorrectly excluding a valid shoulder tie |
| short-shell-2a-general-margin-green, continuation-general-margin-green.log | ConvexGeneralPlanarTests, ConvexMarginOracleTests, ConvexCircularEdgeTests, ConvexPlanarDistanceTests | No | 0, 26 passed |
| short-shell-2a-face-section-red, continuation-face-section-red.log | FaceSectionOverlapDoesNotRequireAnAxisSampleOrSupportProjection | Yes | 1, one intended kernel refusal |
| short-shell-2a-face-section-green, continuation-face-section-green.log | FaceSectionOverlapDoesNotRequireAnAxisSampleOrSupportProjection | No | 0, one passed |
| short-shell-2a-pair-matrix, continuation-pair-matrix.log | ConvexFeaturePairCoverageTests, ConvexGeneralPlanarTests, ConvexMarginOracleTests, ConvexCircularEdgeTests, ConvexPlanarDistanceTests | Yes | 1, 27 passed and the shoulder/face oracle exhausted its proposal iterations |
| short-shell-2a-oracle-diagnostic, continuation-oracle-diagnostic.log | ConvexFeaturePairCoverageTests | Yes | 1, temporary diagnostics confirmed proposal exhaustion, then were removed |
| short-shell-2a-pair-matrix-green, continuation-pair-matrix-green.log | ConvexFeaturePairCoverageTests, ConvexCircularEdgeTests | Yes | 0, nine passed, with eight base/margin circular rows and all eighteen feature pairs |

The three matrix/diagnostic commands enable detailed console logging. The final
focused Game command below rebuilds all relevant references and includes SS-R2's capsule shell,
general sweep and captured stair controls, curved support controls and every new planar test.

```bash
build-slot --label short-shell-2a-final-game --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexPlanarDistanceTests|FullyQualifiedName~ConvexCircularEdgeTests|FullyQualifiedName~ConvexGeneralPlanarTests|FullyQualifiedName~ConvexMarginOracleTests|FullyQualifiedName~ConvexFeaturePairCoverageTests|FullyQualifiedName~ConvexFeatureKernelTests|FullyQualifiedName~ConvexQueryContractTests|FullyQualifiedName~ShellGeometryTests|FullyQualifiedName~GroundCoreTests|FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~BepuCertifiedCapsuleSweepTests|FullyQualifiedName~CertifiedRotatedBoxSweepTests|FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedCurvedSweep|FullyQualifiedName~CertifiedCompoundSweepTests|FullyQualifiedName~CertifiedMeshSweepTests|FullyQualifiedName~CapsuleSweepValueTests|FullyQualifiedName~SupportNeighborhoodCurvedTests|FullyQualifiedName~SupportCurvedWorkTests|FullyQualifiedName~SupportDiscEdgeTests|FullyQualifiedName~ExactCurvedInstallControlTests|FullyQualifiedName~InstalledSupportPoseTests' --logger 'console;verbosity=detailed'
```

Exit 0. All 382 passed, zero failed or skipped. Log continuation-game-final.log. Release
compilation reported no warnings or errors. Existing capsule and support tests are unchanged.
The capsule projection extraction preserves the original binary64 operation graph. No
installed geometry, represented transform, locomotion or movement source changes are present.

The final support-footprint selection exercises the shared tilted curved root and boundary
families, certified footprint roles and bit-exact curved proof reuse. It uses the already
rebuilt Game assembly, with no repeated selection from the preceding focused Game run.

```bash
build-slot --label short-shell-2a-final-footprint --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~SupportFootprintTests|FullyQualifiedName~SupportFootprintProofReuseGeometryTests|FullyQualifiedName~SupportFootprintProofReuseWorkTests' --logger 'console;verbosity=detailed'
```

Exit 0. All 741 passed, zero failed or skipped. Log continuation-footprint-final.log. This
also selects the FootSupportFootprintTests name through the requested substring. Existing
curved support tests, captured support fixtures and expected bits are unchanged.

```bash
build-slot --label short-shell-2a-final-navigation --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ContactNavigationBaselineTests.SerialBakeMatchesNormalizedFullFileBaseline' --logger 'console;verbosity=detailed'
```

Exit 0. All seven pinned normalized full-file navigation baselines passed, zero failed or
skipped. Log continuation-navigation-final.log. BuildFixture uses null execution and
NavBakeExecution.Degree returns 1. Every baseline hash and all movement source remain unchanged.
Release compilation has no warnings or errors. The independent 1 mm arrival ball, existing
skin, roof and footprint protections and raw identity remain unchanged.

Self-review checked the complete source diff, root existence and closed-domain reduction,
attained witnesses, both support cones, unchanged capsule projection operation order, optional
support-account callback defaults, bounded pending storage, scratch reuse, oracle independence
and exact margin predicates. Task 2a has no remaining implementation item. Tasks 2b and 2c
remain atomic refusal, and Task 3's selected-world traversal and lifetime remain separate work.
No version, changelog, package or consumer changes belong to this intermediate task.

All four final staged guards exited 0 after explicit staging and complete diff review:
sh scripts/check-dashes.sh staged, sh scripts/check-prose.sh staged,
sh scripts/check-file-size.sh staged and git diff --cached --check. The first prose pass
rejected a console logger argument written in prose. Rewording that sentence resolved it.
Whole-tree dash, prose and file-size guards and bash scripts/check-doc-versions.sh also
exited 0. No source changed after the final test commands.

## Task 2b partial planar sweeps

Task 2b is incomplete. The supported producers below can publish independently proved
planar hits and full-path separation. Unimplemented moving feature pairs refuse atomically.
Contacts and Penetration still refuse before geometric work. This is not the complete
moving extension of the Task 2a enumeration lemma and does not unblock Task 3.

### Contact and clear proofs

Every accepted seed is an attained common point between the requested Minkowski-grown
shell and the exact represented target. Closed shell membership, the query normal cone,
the original finite face, vertex or segment domain, and support against every target
vertex are checked exactly. The nonnegative margin offset is parallel to the common normal
and has squared length equal to the exact requested margin squared.

For a contact at fraction tau with common outward normal n and dot(n, displacement) > 0,
the same dual plane strictly separates both complete solids at every earlier original-path
fraction. Its gap is (tau - fraction) times dot(n, displacement). A nonpositive speed needs
a separate strict dual separator over the whole earlier bracket prefix. A perturbed normal
is only a direction proposal. Exact complete support and all represented target vertices
decide whether its whole-prefix proof succeeds. Failure never publishes a hit or clear prefix.

The exact quadratic feature solutions retain their original closed geometric predicates.
Their algebraic time annihilators go through ConvexAlgebraicRoots.TryIsolate with both
original unsquared radical signs. The committed square-free reduction, Sturm counts and
dyadic isolation retain repeated roots. Exact quadratic time bounds intersect the root
bracket before outward distance encoding. Neither a sign crossing nor a root midpoint is
an existence witness. The exact attained time and point remain the witness.

A whole-path Clear requires one strict dual support-plane proof over [0,1]. Direction
proposals can come from a finite face or the distance kernel, but rounded proposal output
never authorizes a miss. The original displacement is used throughout. Clear carries no
identity, hit, contact point or normal. A path without a proved separator or contact refuses.

The encoded impact bracket, both point-encoding errors and normal-encoding error share the
existing 0.001 m evidence budget. The unchanged float skin subtraction must be strictly
earlier than the lower bracket when it advances. The actual represented feet addition after
Vector3.Normalize(displacement) times that advance must also satisfy a strict dual separating
plane. The -1024 m fixture independently checks its resulting placement against the oracle.

Opaque dispatch tickets retain their exact dispatch owner and operation. A foreign owner
cannot publish a ticket, including the same geometry under another raw identity. Distance
tickets cannot become sweep tickets or vice versa. Hit retains the exact SS-R5 identity
supplied by its SS-R6 dispatcher. Coincident children and two dynamic bodies remain distinct.
InstalledSweepLeaf, capsule code, existing support code and navigation hashes are unchanged.

### Independent oracle coverage

ShortBodyShellOracle.Entry now handles nonzero paths. Cartesian base-shell targets use a
separate rational piecewise meridian ray construction, with exact discriminants and outward
square roots. Closed breakpoints, stationary double roots and the final endpoint are retained.
Other targets and requested margins use independently certified attained points and complete
support planes, with convexity establishing the single contact-time interval. Numeric GJK is
only a fallback proposal source. Missing samples and failed proposals cannot certify Clear.
No production support, transform, normal-cone, polynomial or root helper supplies an answer.

Face centroids alone do not cover the installed shoulder contact after Bepu centering
rounding. The independent oracle now also certifies an exact face-plane point against every
original target halfspace and its separate meridian membership test. This fixes the observed
oracle refusal without changing a tolerance or treating a near-zero distance as overlap.

| Closed query feature | Face | Interior edge | Vertex |
| --- | --- | --- | --- |
| Apex | Base and margin | Unfinished | Unfinished |
| Lower cone | Base and margin | Base | Unfinished |
| Shoulder circle | Base and margin | Unfinished | Unfinished |
| Cylindrical side | Base and margin | Base | Base |
| Head disc | Base and margin | Base | Unfinished |
| Head rim | Base and margin | Unfinished | Unfinished |

These ten base feature-pair cells and six margin face cells have direct independent sweep
comparisons. Duck and cow each have independent wall, roof and floor comparisons for boxes
and hulls with both admitted margins. The thin full-width blocker, dyadic tangent, adjacent
floats, finite endpoint, zero path, initial boundary, raw identity and lowered-work controls
are covered. Positive initial overlap currently refuses. A known margin/cone-edge contact
is independently established and then explicitly checked for default production refusal.

### Exact remaining scope

- Complete moving apex, lower-cone, shoulder and rim vertex strata, including seam normal cones.
- Complete moving apex, shoulder and rim interior-edge strata and their closed degeneracies.
- Complete margin/cone and margin/circular edge and vertex algebra, including mixed quadratic
  fields and the higher-degree radical equations. Current mixed fields are not rounded together.
- Complete non-face zero-path and initial-overlap reporting and the full moving enumeration lemma.
- Broaden the independent oracle matrix to every missing feature pair and general represented pose.

Work and exact integer ceilings are unchanged. Exhaustion is sticky. Root arenas and fraction
width are restored in finally paths. Counter observations are diagnostic, with no SIMD- or
schedule-dependent absolute pins. Selected-world traversal, lifetime and whole-query publication
remain later tasks. There is no version, changelog, package, world, consumer or release change.

### Task 2b final focused checks

All 22 new tests passed in the focused red-to-green selection. The final Game selection
combines those three new classes with the exact Task 2a final-game filter above. Its command
uses label short-shell-2b-final-game, Release, no restore and the detailed console logger.
It exited 0 with 404 passed, zero failed or skipped. That includes every required SS-R2
shell, core, general sweep, cylinder, curved and captured-stair control. No warnings or errors
were reported. The test and command history is retained in the Task 2b implementation report.

```bash
build-slot --label short-shell-2b-final-footprint --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~SupportFootprintTests|FullyQualifiedName~SupportFootprintProofReuseGeometryTests|FullyQualifiedName~SupportFootprintProofReuseWorkTests' --logger 'console;verbosity=detailed'
```

Exit 0. All 741 passed, zero failed or skipped.

```bash
build-slot --label short-shell-2b-final-navigation --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ContactNavigationBaselineTests.SerialBakeMatchesNormalizedFullFileBaseline' --logger 'console;verbosity=detailed'
```

Exit 0. All seven original normalized full-file baselines passed at degree 1. The source
passes null execution and NavBakeExecution.Degree returns 1 for that value. The original
hashes, capsule production/test files, support files and navigation sources are unchanged.
The new focused tests observe maxima of 35 cells and 308 features, without pinning them.

All builds and tests ran through build-slot, one at a time. No full suite, timing/load run,
live run, push, merge, branch change, external write or delegation occurred. Explicit staging
and self-review cover exactly fifteen task-owned paths. All four staged guards pass before
the partial commit. Full solution verification and integration remain with the orchestrator.

## Task 2b continuation under SS-R9

### Shared rounded-circle equation

CircularContactEquation owns the existing CapsuleCylinderFeatures.Contact rounded-rim
expression. Its SweepExactWorkspace policy preserves the original capsule operand order,
short circuit and sign guards. Its exact polynomial policy supplies the same expression to
ConvexCircularVertexSweep. No capsule dimension, transform, support or test expectation changes.
CircularProjection and its outward interval policy enclose the attained rim projection.

For a moving represented vertex p(t), write rho squared = px squared + pz squared and
z = py - circle height. The shared capsule expression is
T = rho squared + R squared + z squared - m squared. Its squared equation is
T squared - 4 R squared rho squared = 0. The original radial domain rho >= R and T >= 0
are retained, so the unsquared equation is T = 2 R rho. This is the exact requested
Minkowski margin, with no additional radius or skin. The polynomial is generally quartic.
Square-free reduction, Sturm counts, dyadic isolation and exact radical signs select its
first feasible root, including repeated algebraic roots. No midpoint authorizes a contact.
For a cell with exactly one root the isolator now proves original feasibility before final
spatial narrowing. A rejected root needs no narrow impact bracket. A feasible root is narrowed
by counting only the left child, since the unique parent root must be in one of its children.
The proof and every work, depth, storage and exact-integer ceiling remain unchanged.

The rim requires ny >= 0. The shoulder requires ny <= 0 and
(C-S)*ny + R*(rho-R) >= 0. Each represented target vertex offset v from the contacting
vertex must satisfy v.n >= 0. Multiplication by positive rho expresses the latter as
sqrt(rho squared)*(vx*px + vy*z + vz*pz) - R*(vx*px + vz*pz) >= 0.
These are original radical predicates at the isolated root, not interval near-zero tests.
They prove the query normal cone and support against the entire original target.

Positive n.displacement at that attained root proves strict separation of the entire earlier
original path. Its support gap at any earlier fraction s is (t-s)*n.displacement. The actual
float skin-shortened placement receives a separate outward support-plane check against every
represented target vertex. Point encoding, unit-normal encoding and the whole impact bracket
share the unchanged 0.001 m evidence budget. Normalization near a small margin needs a tighter
root enclosure. Nonexpansive disc projection and the 2/m normalization bound derive it from
the existing quarter budget. This tightens arithmetic enclosure only.

### Moving closed-pair completeness lemma and implementation boundary

Let A(t) = K + mB + t*d and let T be the compact represented planar target. If the original
path starts disjoint and has a first contact tau, compactness makes that contact attained.
Convexity supplies a common supporting normal at an attained pair. Removing the exact
Minkowski offset m*n/|n| leaves a base-shell witness in one of the six closed strata in the
Task 2a table. The target witness belongs to a closed face, edge or vertex. The eighteen
closed pairs therefore exhaust first contact geometrically, just as they exhaust distance.

For a target edge, an interior witness has n.edge = 0. A segment endpoint belongs to the
vertex construction. For a face, an exposed shell generator or head disc either meets a
finite face interior or its projected intersection meets a polygon boundary. The latter
reduces to the already incident edge or vertex construction. A seam belongs to every incident
closed stratum and keeps its full common normal cone. No seam normal is averaged.

An axis-parallel edge has constant radial data and retains its closed height crossings.
An edge through the apex path retains the closed line intersection. An edge in a rim plane
retains the closed disc/segment stationary and endpoint strata. A stationary interval has
its first point at the original time endpoint or a closed domain boundary. An identically
zero equation therefore needs the original domain boundaries, not a sign-crossing test.
Isolated tangencies need square-free root existence plus a whole earlier-path dual proof.
The tangent's own plane alone need not be strict. Zero paths and initial overlap require
their separate attained initial-state reporting before any first-entry reduction.

This proves the required moving enumeration obligation. It does not prove that the current
producers solve every enumerated system. The new rounded circular vertex construction covers
only roots with certified positive normal speed, or a boundary root at time zero. Its other
roots, missing moving strata, undecided signs, excessive encoding error and exhausted work
still refuse atomically. In particular this continuation is not a claim of Task 2b completion.

### Continuation coverage and verification

Sixteen new tests include thirteen independent moving impact comparisons for rounded shoulder
and rim vertices against boxes and hulls. They cover identity poses, represented yaw, full represented
3D poses, moving height and the closed shoulder/cone seam. A non-face zero-path rounded boundary,
a squared inner-rim root with the margin larger than the radius, and sticky work denial with
same-run derived limits and restored scratch are also covered. Initial positive overlap still
refuses. Base circular vertex regimes and the apex and lower-cone vertex families, circular and
apex interior edges, rounded cone/edge systems and the rest of the full pair matrix remain open.

The independent oracle retains its separate rational meridian polygon construction. Those
residuals now also propose high-precision rational directions, whose complete support-plane
checks over every target vertex alone authorize separation. No production geometry, polynomial,
transform or root helper supplies an expected answer. This fixes near-contact oracle refusals
for rotated vertex fixtures without a tolerance or accepting a near-zero residual.

The final Release Game selection adds ConvexRoundedVertexSweepTests to the prior 404-test filter.
It passes all 420, including the unchanged SS-R2 shell, GroundCore, general cylinder/curved sweeps,
captured stairs, support curved controls and SS-R5/SS-R6 identity tests. The original footprint
selection passes all 741. The seven original normalized full-file navigation baselines pass at
degree 1. Their source calls BuildFixture with null execution, and NavBakeExecution.Degree
returns 1 for null. All hashes and baseline sources are unchanged. Both compiling commands
report no warnings or errors. There are no failed or skipped tests in these final selections.

All commands run synchronously through build-slot, one at a time. Exact commands, exit codes,
reds, arithmetic reuse, self-review and remaining scope are recorded in the Task 2b report's
Continuation section. Scratch logs are under /tmp/khaozengine-short-shell/. The new focused
fixtures observe maxima of 93 cells and 1799 features. The final 420-test selection observes
111 cells and 1799 features. These observations are diagnostic only. No absolute work counter
is pinned. Full solution verification, formatting, version reconciliation and integration
remain with the owning orchestrator. No full suite, timing/load run or release was started.

## SS-R10 bracket design amendment, not implementation evidence

The implementation plan's remaining Task 2 units now run in this order:

1. 2b.1 retains pre-encoding static primal/dual certificates and proves translated intervals.
2. 2c.1 certifies initial separation, touching and strict overlap.
3. 2c.2 certifies planar minimum member penetration and complete leaf contacts.
4. 2b.2 certifies attained events with a bounded time-labelled inner simplex.
5. 2b.3 publishes one bracket sweep and retires the four r0/r1 shell sweep producers.

For a fixed proposed direction n, the exact gap between the full represented target and
`p+t*d+K+mBall` is bounded below by a constant minus `t*dot(n,d)`. Include one uniform
represented-operation error over the complete cell in that constant. Positive endpoint
lower gaps prove the whole closed fraction interval. Contiguous intervals prove the entire
clear prefix. Only such dual proofs can authorize Clear. A rounded endpoint miss, an inner
simplex miss or a failed root proposal proves nothing about the original path.

For [a,b], define `B=(T-p)+[-b,-a]*d`. Propose at most four original labelled vertices
`vi-p-ti*d` of B. A statically certified attained point y in their simplex retains exact
nonnegative barycentrics summing to one. They recover `tau=sum(lambda*ti)` and
`target=sum(lambda*vi)`, with `p+q+tau*d-target=q-y`. Thus an attained static base distance
<= m proves an event on the original path. Four-vertex convex-hull coverage does not prove
bounded GJK convergence. Rank-deficient sources need their own exact affine/barycentric
membership, never vacuous solid halfspaces. An inner source cannot supply original raw
identity, local topology or operational normal without full original-member revalidation.

At m=0, prove exact common-point membership or strict overlap, never near-zero distance.
At m=0.001, retain the base pair before Margin clipping, prove positive base distance <= m,
and decide equality with original exact/root predicates. A zero base distance uses 2c's
certified normal. Algebraic event times retain their attained witness and an outward time
enclosure. Its upper endpoint need not itself touch, as with existing isolated-root evidence.
Closed static roots, double roots and seams remain included. Required tangent fixtures must
Hit under the production caps. Actual undecided signs, excessive correspondence/encoding
error or exhausted work refuse atomically.

Conditional on a proved decision at each midpoint, spatial time-bracket contraction to
0.00025 m needs at most 33 bisections in the SB-7 displacement domain. This allocates part
of the unchanged 0.001 m total evidence budget. It is not a whole-query work or convergence
bound. Every proposal, original-target support check and root refinement shares the existing
256-cell/4096-feature primitive account, admitted only once. The plan's expected small-fixture
envelopes of 96 face, 224 rim and 160 apex cells are unmeasured design targets, not test pins
or larger limits. Mandatory fixture exhaustion leaves implementation incomplete.

For an operational Hit, prove both original normal cones, finite unit-normal encoding and
the entire outward bracket/error. Its existing 0.001 m skin subtraction must be earlier than
the proved lower bound whenever advance is positive. Separately certify the actual binary32
Normalize/multiply/add placement. Initial touch/overlap instead has a zero-distance Hit with
its explicit initial-state normal. A separated zero path is Clear without normalization.

2b.3 removes ConvexPlanarSweep, ConvexSweepVertices, ConvexSweepEdges and
ConvexCircularVertexSweep when their replacement lands. ConvexSweepEncoding becomes the sole
bracket encoder. ConvexPlanarDispatch keeps original owner/operation/SS-R5/SS-R6 identity.
Shared CircularContactEquation arithmetic, circular projection and exact-root machinery stay
for static margin ties and unchanged capsule/support consumers. Preserve the r0/r1 regressions
through the sole kernel route. No parallel shell sweep path remains.

This design requires an internal amendment of 2a's certificate boundary, because its current
public encoding discards the base normal inside Margin and cannot retain root/barycentric
attainment. The spec is unchanged. This note does not accept Task 2b, complete Contacts or
Penetration, unblock Task 3 or assert that the proposed work envelopes have been measured.
No production/test code, geometry, tolerance, work ceiling, capsule API or baseline was changed
in this design pass. No build or test was run for this documentation amendment.


## Task 2b.1 retained static and interval certificates

The new query-private path retains attained base pairs before Minkowski-margin clipping or
encoding. ConvexPlanarSource authenticates the installed represented planar leaf, or retains
one to four original vertices as an inner rank 0 to 3 simplex. It carries exact coordinates
and vertex provenance without constructing a PhysicsShape. Inner certificates cannot encode
original-target results. The shared static enumeration keeps the legacy encoder at its original
acceptance points, including the accepted circular-region path. No Sweep producer is replaced.

ConvexDistanceCertificate is tagged quadratic or circular-root and is bound to its source,
original request and scratch generation. A positive base pair remains positive inside the
0.001 margin even when public Distance encodes zero. ConvexCertificateFraction carries an exact
quadratic value or an affine expression in one retained circular root. It never constructs a
rounded Pose. Independent roots refuse composition. Original-source predicates at a root time
use the retained root's witness expressions and exact polynomial/radical signs.

The circular certificate retains the original unsquared stationary equation. Its margin
comparison reuses CircularContactEquation and the existing GCD/SignAtRoot machinery. It checks
the original difference sign before using the squared equation, so an exact zero is a proved
equality rather than an interval containing zero. The root equation and Sturm chain stay in
the existing coefficient arena. Every sign operation restores its temporary coefficients.
A replacement evaluation invalidates the prior generation and restores retained storage.
Refusal also restores the caller's checkpoint, without resetting primitive work.

For a fixed represented direction n, the interval certificate verifies every original target
vertex and the complete apex, shoulder-disc and head-disc support, including m times norm(n).
Its forward-operation error covers the full [0,1] coordinate range and remains constant while
endpoints change. Fixed source vertices reuse the pinned RoundedDirection and Add operation
enclosures, including scalar/SIMD reduction orders. Their residual from the exact represented
vertex bounds the target error. Placement product and addition spacings use the complete affine
coordinate range, including fraction conversion. No inverse is used.
After subtracting that one uniform error, the gap is affine in the exact original fraction.
Strictly positive endpoint lower bounds prove every fraction in the closed interval. A cached
plane reuses its complete original-target support proof and charges changed endpoints to the
same work account. Failed endpoints and exact ties never become interval Clear.

ConvexIntervalCertificateTests covers the six named brief cases, both admitted margins, all
six static shell features, original signed-zero feet bits, non-binary32 fractions, arbitrary
represented target poses, all simplex ranks, positive base pairs inside the margin, exact
margin ties and adjacent margins, retained root equality/refinement, original-target root
revalidation, foreign-source refusal, coefficient exhaustion and stale scratch lifetimes.
The six-feature retention test compares the entire encoded evidence with the legacy path.
The unchanged eighteen-pair matrix remains part of the regression selection.

### Task 2b.1 red and green evidence

All commands ran synchronously through build-slot in Release. The focused command was
`build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexIntervalCertificateTests'`.

- Initial red, exit 1, no tests executed. The intended absent source, fraction, interval and
  kernel certificate symbols failed compilation.
- First implementation compile, exit 1, no tests executed. Corrected an internal result type
  collision, a local variable collision and legacy predicate adapter overloads.
- Initial green, exit 0, 7 passed.
- Root-extension red, exit 1, no tests executed. The intended retained-root and bound-time
  symbols were absent.
- Root behavioral red, exit 1, 10 passed and 1 failed. A zero affine predicate retained degree
  1 and caused GCD to refuse. Constant affine predicates now have canonical degree zero.
- Expanded root green, exit 0, 11 passed.
- Focused green before self-review, exit 0, 14 passed.
- Ownership red, exit 1, 13 passed and 1 failed. Constant root-bound fraction refinement now
  checks its scratch owner before skipping the refinement branch. Ownership green passed 14.
- Translated-world red, exit 1, 14 passed and 2 failed. The initial safe global error bound was
  too coarse at 100 m for millimetre evidence. Reused the pinned forward-operation enclosures
  and full coordinate-range placement bounds. Final focused green passed 16, exit 0.

The Common commands initially ran once in order, exit 0 each. The main selection was rerun
after the interval-error refinement above and passed 436. The initial selection passed 434,
including the unchanged eighteen-pair matrix and SS-R2 controls. The footprint selection passed
741. All seven original normalized full-file navigation baselines passed with null execution,
which NavBakeExecution.Degree maps to degree 1. No baseline or capsule source changed.

Observed cells/features were 2/18 for the same-run nested-limit probe, 3/49 for each interval
sequence, 82/687 for retained circular time comparisons and 82/666 for original-root
revalidation plus foreign-source refusal. Circular storage retained 132 coefficients with a
245 high-water mark. Cached endpoint changes charged one cell and two endpoint features.
Temporary signs and failed revalidation restored the retained checkpoint. Coefficient-capacity
zero and replacement by a point certificate restored Used to zero. These are observations,
not absolute counter pins. Full solution verification remains Task 12.


At 100 m, the final uniform directional forward-error bound was
0.000022932887077331573 m. The closed interval endpoint lower gaps were
0.0019759684801101125 m at margin zero and 0.0009759684326126615 m at margin 0.001.
The complete coordinate-range proof covers every interior fraction. These observations
introduce no tolerance, ceiling or budget. The final main selection executed all 16 new tests.


All four required staged guards exited 0 after explicit staging and diff review.
`sh scripts/check-dashes.sh staged`, `sh scripts/check-prose.sh staged`,
`sh scripts/check-file-size.sh staged` and `git diff --cached --check`.
The staged set contains exactly 14 task-owned paths. The four r0/r1 sweep producers,
InstalledSweepLeaf, capsule/support implementations and movement/baseline sources are unchanged.

## Task 2c.1 attained initial relations

ConvexPlanarContacts.TryClassify consumes one already admitted primitive and the original-source
ConvexDistanceCertificate at the exact ConvexCertificateFraction. It neither normalizes motion
nor changes Pose. Separated requires a strictly positive base-distance versus margin predicate.
At positive margin, exact base distance less than margin proves grown overlap. Exact equality
with a positive base distance proves touching. Circular equality uses the retained original-root
predicate, including its original radical signs. An undecided sign is an atomic refusal.

At margin zero, a common point is not an overlap test and its zero difference is not a normal.
A touching direction must be nonzero and pass complete continuous shell support, original-target
membership and support against every original vertex. Candidate directions include incident
target faces, smooth shell gradients and the closed two-ray shoulder and rim cones. The latter
are clipped by all target inequalities and select an attained feasible interval endpoint.
Normals are never averaged across seams. Apex normals solve a closed disc and halfplane problem
with Y=-1. The origin, line projections and line intersections propose candidates. Each is
rechecked against every constraint and both complete solids.

Strict zero-margin overlap requires a rational world point and strict inequalities for both
full-dimensional solids. Target and shell interior points are proposals only. If necessary,
the common point's active shell gradients select an inward original-vertex direction or a
two-vertex combination. At the apex, a strict disc feasibility problem supplies an inward
direction subject to every incident target face. Mixing toward the target centroid and
shortening the step proposes interior points. Every final rational point is checked afresh
against the complete curved shell and every original target face. Shortening consumes the
same primitive's cell account. Exhaustion or unsupported exact arithmetic refuses, with no
new tolerance or limit. No failure to find a normal or an interior proposal proves a state.

Touching retains the exact nonzero direction and mathematical normalization construction.
Its finite Vector3 encoding uses the existing ConvexDistanceEncoding enclosure and error
checks. The operational normal has the opposite sign to query outward. Alternative incident
closed-cone constraints are verified against the same original source and scratch generation.
The source-bound initial dispatch ticket attaches only its own raw identity. Replacement
invalidates prior tickets, including coincident geometry owned by different dispatchers.
An overlapping relation supplies no penetration normal or completed Contacts result.
Contacts and Penetration remain atomic refusals in TryEvaluate until Task 2c.2. The four r0/r1
Sweep producers remain unchanged until Task 2b.3. Finite one-sided surfaces remain Task 5.

The retention-only static entry point checks one strict interior axis point before enumerating
distant boundary pairs. This is an exact membership proof against every source face and keeps
a fully containing solid from spending the cell account before reaching containment. The legacy
Distance entry point and its acceptance order remain unchanged. IsOwnedBy on the retained pair
is the only additional certificate API, used to refuse foreign-scratch touching constraints.

The independent oracle copies only the raw integer coefficients of attained quadratic points
and directions. It recomputes both membership and complete support signs in its own quadratic
field. A rounded normal can leave a closed target cone by one encoding bit, so these geometric
assertions deliberately precede Vector3 encoding. Unit-normal enclosures, finite encodings and
the operational sign are checked separately. All six shell regimes have finite face, edge and
vertex controls for touching, strict separation and strict overlap, plus positive-margin growth.
Additional controls exercise exact margin ties and adjacent floats, zero and both motion signs,
an exact nonzero path fraction, containment, crossing solids, apex and seam cone intersections,
an inward two-vertex overlap combination, retained root time, coincident owner tickets, expired
and foreign scratch evidence and same-run cell/feature-limit refusals.

### Task 2c.1 verification results

The exact focused command was
`build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexInitialStateTests'`.

- Initial red exited 1 with CS0246 for the absent ConvexInitialEvidence. No tests executed.
- First implementation and diagnostic runs each exited 1, with 6 passed and 2 failed.
  The containment control spent the 256-cell account before boundary enumeration reached
  containment. The retention-only strict interior check fixes that ordering. The other
  failure incorrectly tested a rounded direction as an exact closed-cone member. The
  independent oracle now recomputes exact coefficient signs before encoding.
- The next run exited 1, with 7 passed and 1 fixture expectation failure. The head-disc edge
  endpoints also lay on the disc, permitting a valid vertex witness. Lengthened that edge
  beyond the disc so it requires an interior edge witness. Production provenance is unchanged.
- Final focused green exited 0, with 12 passed, 0 failed and 0 skipped.

The three exact Common commands in the amended plan ran once each, synchronously, through
build-slot with --heavy. The Game selection passed 448, the footprint selection passed 741
and the navigation selection passed 7. Every command exited 0, with no failed or skipped tests.
This includes the complete unchanged eighteen-pair Distance matrix, capsule families,
GroundCore, general and curved sweeps, captured stairs, roof/footprint protections and raw
identity controls. All seven original normalized full-file hashes passed with null execution,
which selects degree 1. No baseline, capsule, protection or r0/r1 producer source changed.

Observed cells/features were 4/48 for apex/face touching, 137/270 for shoulder/edge touching,
130/237 for head-disc/edge touching, 18/37 for rim/vertex touching, 22/46 and 15/39 for the
apex and seam cone intersections and 31/64 for the inward two-vertex combination. The retained
root-time and foreign-scratch control used 83/671, retained 132 coefficients and had high-water
245. Its retained checkpoint stayed unchanged. All calls kept Descendants=1. Lowered cell and
feature limits derive from same-run successful probes, not absolute observed counts. Their
refusals return default evidence with Used=0. These observations add no tolerance or budget.

Whole-tree dash, prose and file-size guards also exited 0. No cap or baseline changed.

## Task 2c.2 partial member-depth construction

This is a tested partial boundary, not acceptance of Task 2c.2. General planar edge exit
candidates, including oblique circular-edge stationary witnesses, remain absent. No new Sweep
is published. Task 2b.2, Task 2b.3 and whole-view traversal remain blocked on the unit's completion.

ConvexPlanarPenetration consumes the accepted original-source initial relation on the same
admitted account. Original face and vertex pairs propose attained stationary exits. Each pair
passes complete continuous shell membership/support and every original target vertex support
inequality. The pair difference is exactly collinear with its nonzero normal. The local exit
is an upper proposal only, including an attractive nonminimum exit.

ConvexPenetrationPolytope constructs a bounded inner CSO hull. Each rational vertex is an exact
sum of an authenticated target vertex, an independently checked inner shell point and an
independently checked inner margin-ball point. Operational support only proposes those points.
Every incremental hull facet has exact orientation against a strict interior tetrahedron point.
Visible faces and their closed horizon are replaced within the original general-sweep caps.
The four-slot GJK simplex is unchanged. Positive offsets for all facets prove the origin is
strictly inside. The least exact squared facet distance gives a centred inball and therefore a
global depth lower bound. No EPA convergence criterion or failed expansion authorizes output.

Storage uses at most 508 vertices, 256 faces, 1,524 face indices and 128 horizon entries.
Retained rational coordinates and facet coefficients also obey the unused existing 2,048-scalar
coefficient capacity. Every support, facet, certificate and refinement attempt stays on the
existing sticky 256-cell and 4,096-feature account. Capacity or insufficient lower proof refuses
with default evidence. There is no growing EPA list, new tolerance or widened ceiling.

The certificate retains the strict initial relation, original support pair, local provenance,
normal, global lower enclosure, encoded translation and signed constraint. Mathematical depth,
translation length, both witnesses, unit-normal encoding and operational position-addition error
share the existing 0.001 m budget. Both the encoded translation and the rounded destination
pass exact original-member support checks. A further representable destination is another
charged proposal, never a tolerance added to geometry.

Contacts and Penetration use the existing TryEvaluate operation door for the certified subset.
Negative separation is [-upperDepth,-lowerDepth], touching remains zero and separation remains
positive. The dispatch ticket binds its raw owner, operation and scratch generation. Coincident
members remain separate and foreign or expired tickets refuse. One member clearing another
member is not asserted. Deepest-member selection, raw-ID ties and recovery requery remain Task 3/7.

The independent MemberTranslationEnclosedBy oracle receives shape and evaluated pose explicitly.
It copies installed snapshots, validates the translation by continuous support and independently
proves a lower bound using the complete normal fan of an inscribed rational shell polygon sum.
For a separated base under positive margin it instead consumes independent distance bounds.
The mandatory left-translation control is accepted at feet (-1/8,0,0) and rejected unchanged at
feet (-1/16,0,0). An attractive nonminimum stationary exit is rejected too.

The current proposal coverage is deliberately incomplete. General finite edges and retained
algebraic exit pairs require continuation before Task 2c.2 can be accepted. Algebraic path
fractions also refuse. Face and vertex certificates never stand in for those missing proofs.


### Task 2c.2 partial verification

The named Release class first failed on the missing certificate type. Its first execution
passed 3 and failed 4 on oracle/fixture construction. Corrected controls then passed all 7.
The added rounded-destination control failed with 8 passing, then the implementation correction
passed all 9. The final Common selections ran once each, synchronously through build-slot with
--heavy. Game passed 457, footprint passed 741 and navigation passed 7, all with exit 0 and
no skipped tests. The unchanged navigation test supplies null execution, selecting degree 1,
and checks all seven original normalized hashes. The full solution and format remain Task 12.

Same-run observations include 100 cells and 349 features for the isolated face exit, 108/501
for the vertex exit, 88/328 for margin-only overlap and 87/326 for the 100 m rounded destination.
The contained small box used 160/2225 at Margin 0 and 153/2230 at Margin 0.001. Its largest hull
had 30 vertices and 56 faces, accounting for 370 rational scalar slots. These are observations,
not CPU/SIMD/scheduling-dependent counter pins. Lowered limits derive from same-run probes.

## Task 2c.2 continuation under SS-R11

This is refusal evidence, not acceptance of Task 2c.2. The attempted pair-agnostic upper
implementation exhausted the unchanged 256-cell cap on required edge fixtures. No production
or test changes from this continuation are retained. The previous verified partial at
00c1bce80 remains the implementation checkpoint. Tasks 2b.2 and 2b.3 remain blocked on 2c.2.

### Attempt and bounded refusal

All face, vertex and EPA directions entered one static plane verifier. It independently
bounded complete continuous shell support, including both circles and Margin, against every
exact original target vertex. Both the encoded translation and the rounded operational
destination earned plane checks. No edge or circular-edge stationary penetration producer
was added. The original exact inner-CSO inball supplied the global lower bound.

The attempt evaluated accepted static distance at the exact original feet plus the encoded
translation. It retained the original request, rational fraction, authenticated source and
primitive account. Initial and translated certificates shared a scratch generation and the
existing coefficient arena, including a reservation for simultaneous hull scalars. The full
depth bracket, witnesses, normal encoding and position-addition residual still had to fit
0.001 m. This is a member proof, never a global-union MTV.

The required Margin 0 oblique-rim fixture used radius 1/4, LowerY 0, RingY 1/2 and UpperY 1.
Let q=(1/4,1,0), n=(1,1,0), v=(1,-1,1), c=q-n/256, a=(1,0,0) and b=(0,1,0).
Its original hull vertices were c plus either sign of v/8, plus each of 0, a, b and a+b.
The last complete lower/upper bracket was [0.0026310112114487235, 0.00708235321136515] m,
a gap of 0.004451341999916427 m, at 254 cells and 1,134 features. The next expansion or
verification exhausted 256 cells and 1,181 features. The last recorded hull had 10 vertices
and 16 faces. This does not close the required budget.

The interior cylinder-edge fixture was an installed box with half extents
(0.015625,2,0.015625) and centre (0.21875,0.75,0.0625), with the same query shell.
Its Margin 0 bracket reached [0.04104807388660196, 0.041536501940152085] m,
a gap of 0.0004884280535501218 m, at 182 cells and 1,094 features. The static-distance
certificate at exact translation (-0.0404728,0,-0.009339877) then exhausted the remaining
account, ending at 256 cells and 1,190 features. The bracket alone did not supply a complete
attained certificate. Its last hull had 12 vertices and 20 faces.

The Margin 0.001 oblique-rim row similarly narrowed its raw bracket to
[0.006360804313717966, 0.006654392774052639] m at 244 cells and 1,334 features, then
translated static distance exhausted the cap at 256 cells and 1,360 features. These are
same-run observations, not CPU, SIMD or schedule-dependent test pins.

SS-R11's stop condition was applied. No producer, ceiling, tolerance, skin or arrival rule
was changed to turn these refusals into success. Temporary tracing was removed. The complete
unsuccessful implementation and test diff is preserved in the owning SDD workspace as
`task-2c.2-r11-attempt.diff`, SHA-256
`c40f3a13f565c4c5de9fe79511428bd16d10fc8ed27e048d1eab7f191608809a`.
Its reverse check against the attempt and apply check against the restored base both exited 0.
The attempt is diagnostic material and must not be integrated as a completed unit.

### Commands and review

All three commands used build-slot, one at a time, in Release with no restore. None used
--heavy because no Common selection was run.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexPlanarPenetrationTests'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~GenericPlaneExitCertifies' --logger 'console;verbosity=detailed'
```

The named class ran once red before implementation and once after the upper-path attempt.
The diagnostic selection ran once with temporary bracket and certificate-stage tracing.
Logs are under `/tmp/khaozengine-short-shell/`.

| Log | Exit | Result |
| --- | ---: | --- |
| task-2c.2-r11-red.log | 1 | 9 passed, 10 failed, 0 skipped |
| task-2c.2-r11-green-1.log | 1 | 10 passed, 11 failed, 0 skipped |
| task-2c.2-r11-diagnostic.log | 1 | 0 passed, 8 failed, 0 skipped |

The original nine tests passed in the first red. The attempt also regressed the previously
accepted vertex-exit fixture to a cap refusal. Its Margin 0.001 cylinder-edge certificate
used an attained shoulder-boundary pair, so the new test's exclusive Cylinder expectation
was too strict. That tie does not refute the certificate. A separate control proved the
attempted upper-plane value still lacked explicit member/pose binding. Exact original
fraction/source retention and atomic root-loan refusal controls passed. No warning or
compiler error was reported in these builds.

The three Common regression selections were not run after SS-R11's explicit stop condition.
The earlier 457 Game, 741 footprint and seven degree-1 navigation passes remain historical
baseline evidence, not continuation results. No full solution or format claim is made.
The capsule APIs, InstalledSweepLeaf, four r0/r1 producers, roof and footprint protections,
raw identity, Movement and all seven navigation baselines remain unchanged from 00c1bce80.

Penetration is requested only at rational fractions under SS-R11. Tasks 2b.2 and 2b.3 must
choose a dyadic overlapping upper endpoint for overlap events. Root-backed penetration stays
an atomic refusal. Root-defined touching events use Task 2c.1 touching normals. The attempted
rational-only guard was reverted with the rest of the incomplete implementation, so the
rootless irrational-fraction control remains part of the unintegrated continuation work.


## Task 2c.2 continuation under SS-R12

Status NEEDS_CONTEXT. No Task 2c.2 implementation completion is claimed. Production and tests
are restored to the 00c1bce80 foundation. This section records the bounded attempt and its
limitations, not a new certificate or changed budget. Tasks 2b.2 and 2b.3 remain blocked.

The starting guard returned the required worktree, feature/short-body-shell, d4cb0001b and a
clean tracked tree. The SS-R11 archive SHA-256 matched c40f3a13f565c4c5de9fe79511428bd16d10fc8ed27e048d1eab7f191608809a.
It passed git apply --check and was applied before the diagnostic. No cap, tolerance, four-slot
simplex, storage capacity, capsule source, baseline or r0/r1 producer changed.

### First stage diagnostic

One diagnostic run preceded algorithm changes. Temporary nested scopes attributed each
successful cell and feature charge once. Support work nested inside EPA appears under support,
not again under EPA. Plane verification includes the retained face and vertex proposals.

| Stage, cells / features | Cylinder edge, Margin 0 | Rim edge, Margin 0 | Rim edge, Margin 0.001 |
| --- | ---: | ---: | ---: |
| Initial classification | 84 / 121 | 162 / 276 | 140 / 222 |
| EPA expansion | 9 / 233 | 8 / 190 | 11 / 329 |
| Support evaluation | 12 / 96 | 11 / 91 | 14 / 119 |
| Plane verification | 77 / 644 | 75 / 624 | 79 / 664 |
| Witness construction | 74 / 96 | 0 / 0 | 12 / 26 |
| Encoding | 0 / 0 | 0 / 0 | 0 / 0 |
| Total | 256 / 1190 | 256 / 1181 | 256 / 1360 |

The diagnostic exited 1 with all three rows refusing. The witness stage here is the archived
translated static certificate. The zero-margin rim never reaches it. Counts are observations,
not CPU, SIMD or scheduling dependent test pins. Log names and exact selections follow below.

### Corrected attempt and independent stop evidence

The attempt kept one plane verifier. It constructed the exact shell support point at a rational
direction and selected an original target support vertex, checking the complete support cones.
It retained the original source, complete request, evaluated feet and certificate lifetime.
The new witness path called no translated static distance certificate. Circle and Margin sphere
proposals used exact rational parametrizations of their true surfaces. Original target topology
identified exposed-feature provenance, without an edge or circular-edge stationary producer.
The cylinder assertion allowed the closed Shoulder boundary. Rational fractions and original
exact translated feet remained explicit. Root-defined penetration fractions still refused.

The corrected focused run exited 1 with 3 passed, 19 failed, 0 skipped, total 22. The Release
build emitted no compiler warnings or errors. All eight required generic edge rows refused at a
cell or feature ceiling. However, those totals require the self-review qualification below.

| Stage, cells / features | Cylinder edge, Margin 0 | Rim edge, Margin 0 | Rim edge, Margin 0.001 |
| --- | ---: | ---: | ---: |
| Initial classification | 84 / 121 | 162 / 276 | 140 / 222 |
| EPA expansion | 34 / 2588 | 8 / 189 | 15 / 570 |
| Support evaluation | 37 / 333 | 11 / 99 | 18 / 180 |
| Plane verification | 101 / 971 | 75 / 685 | 83 / 773 |
| Witness construction | 0 / 0 | 0 / 0 | 0 / 0 |
| Encoding | 0 / 0 | 0 / 0 | 0 / 0 |
| Total | 256 / 4013 | 256 / 1249 | 256 / 1745 |

The zero-margin cone uses 190 / 318 for classification and 66 / 586 for plane proposals.
The zero-margin shoulder uses 192 / 293 and 64 / 564 respectively. Both reach 256 cells before
EPA seeding, witness construction or encoding. These failures are independent of the new
witness binding defect. Surface proposals cannot affect a run that has not reached them.

The zero-margin rim's last complete lower/upper is [0.0018728585267235082,
0.007082350860313556] m, a 0.005209492333590048 m gap, at 254 cells / 1202 features with hull
10 / 16. It refuses at 256 / 1249 without reaching witness construction. Thus even a corrected
binding comparison cannot complete this recorded bracket inside the remaining work.

The rounded rim reaches [0.0065234433551562936, 0.00652667930536875] m at 256 / 1745,
hull 18 / 32. The cylinder reaches [0.04153649899673638, 0.04153649928761278] m at
254 / 3863, hull 36 / 68. These narrow brackets are not completed certificates.

### Self-review limitations and preservation

The attempt introduced a binding defect. ConvexExactPoint's generated structural equality
compares ConvexQuadratic and ConvexRational storage. A default rational zero and a constructed
rational zero can represent the same number with different stored denominators. Comparing
OriginalFeet == pair.Feet can therefore falsely reject identical exact feet. It must instead
use exact coordinate differences and their signs. Simple face fixtures stopped at 159 / 2512
without exhaustion, consistent with a pre-admission binding refusal. The new foreign-member,
another-pose/shape and exact-fraction witness controls therefore did not reach their intended
acceptance/rejection assertions. No green binding or witness-path claim is made.

Most later exhaustion totals include futile refinements after this false refusal. They cannot
be used as proof of intrinsic witness cost. The two pre-EPA zero-margin failures and the wide
zero-margin rim bracket above are independent evidence for SS-R12's mandatory stop. No further
algorithm variant, cap increase, producer, second path or test run was attempted after that stop.
The owner and orchestrator must review initial classification and proposal work placement before
another continuation. The structural equality defect must also be corrected before reuse.

All temporary instrumentation was removed. The uninstrumented attempt is archived at
.superpowers/sdd/2026-10-19-short-body-shell/task-2c.2-r12-attempt.diff,
57,752 bytes, SHA-256 2d8c64abfc2ebc09eb4ca447d5262c245e06d2f1f4541bff8bf91ac8253a0c80.
The archive passes reverse application against the attempt and forward application against the
restored foundation. An exact status-set assertion verified ownership of the nine tracked and
three new paths before restoration. Production, tests, capsule families, Movement, Locomotion,
raw identity, roof and footprint protections and all seven degree-1 baseline sources are unchanged.

### Commands and regression disposition

All commands ran synchronously through build-slot, one at a time. No full suite, load test,
timing proof, consumer run, world bake, merge, push, tag, branch change or subagent dispatch ran.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SS12StageDiagnostic' --logger 'console;verbosity=detailed'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ConvexPlanarPenetrationTests&FullyQualifiedName!~SS12StageDiagnostic'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexPlanarPenetrationTests' --logger 'console;verbosity=detailed'
```

| Log under /tmp/khaozengine-short-shell | Exit | Result |
| --- | ---: | --- |
| task-2c.2-r12-diagnostic.log | 1 | 0 passed, 3 failed, 0 skipped |
| task-2c.2-r12-red.log | 1 | 10 passed, 11 failed, 0 skipped |
| task-2c.2-r12-corrected.log | 1 | 3 passed, 19 failed, 0 skipped |

There is no green focused completion. The three passing corrected tests are separated/touching
and expired-loan contacts, rational-fraction/root-loan refusal and the independent reused-pose
oracle control. The Common Game, footprint and seven degree-1 navigation selections were not
run because SS-R12 requires stopping on these unchanged-cap failures. The foundation's previous
457, 741 and 7 passes remain historical evidence only. Full solution verification stays Task 12.


### Task 2c.2 continuation under SS-R13

The continuation starts at 6babbf3dd and applies the archived SS-R12 attempt whose SHA-256
is 2d8c64abfc2ebc09eb4ca447d5262c245e06d2f1f4541bff8bf91ac8253a0c80.
The owner retains the 256-cell and 4096-feature caps, every storage/integer ceiling and every
tolerance. No stationary edge producer, second certification path or capsule change is added.

ConvexExactPoint equality compares numeric values instead of rational storage. Exact rational
parts and signed squared radicals identify equal quadratic coordinates, including equivalent
field encodings. Hashing uses the same normalized values. Default versus constructed zeros,
unequal coordinates and equivalent sqrt(2) versus sqrt(8)/2 have dedicated controls.

The exact rational inner CSO hull is built before static contact classification. Its complete
facet scan supplies the minimum signed distance. A strictly positive lower radius proves every
facet offset positive and therefore strict origin containment in an inner subset of the original
CSO. ConvexPenetrationOrigin retains that overlap proof's original source, request, exact rational
fraction and scratch generation. It claims neither an invented distance pair nor a common point.
Only when the hull cannot prove overlap does the existing 2c.1 classifier report touching or
separation. A classifier overlap without the required hull/upper certificate still refuses.

ConvexPenetrationProposal computes each direction's original-target minimum and complete shell
support enclosure once, binds them to source/request/exact feet, and supplies them to the sole
plane verifier and hull refinement. A proposal is pruned before exact witness verification only
when the lower endpoint of Support/|direction| cannot improve the best verified upper length.
The encoded separator and the operational rounded destination remain checked against the original
member. Exact surface witnesses and their closed support cones come from that verified plane,
with source/request/pose and scratch lifetime binding. The lower inball and upper translation
still share the unchanged 0.001 m encoded-error budget.

Temporary nested scopes attributed every admitted cell and feature to one stage. Each of the five
rows ran once before and once after the algorithm corrections. No counter is an absolute test pin.
The EPA stage includes hull seeding, source validation and facet scans. Support work is charged
under support, not again under its caller. The unmodified SS-R12 attempt gives the following table.

| Stage, cells / features | Cylinder edge, Margin 0 | Rim edge, Margin 0 | Rim edge, Margin 0.001 | Cone edge, Margin 0 | Shoulder edge, Margin 0 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Initial classification | 84 / 121 | 162 / 276 | 140 / 222 | 190 / 318 | 192 / 293 |
| EPA and hull maintenance | 34 / 2588 | 8 / 189 | 15 / 570 | 0 / 0 | 0 / 0 |
| Support evaluation | 37 / 333 | 11 / 99 | 18 / 180 | 0 / 0 | 0 / 0 |
| Plane verification | 101 / 971 | 75 / 685 | 83 / 773 | 66 / 586 | 64 / 564 |
| Witness construction | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| Encoding | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| Total | 256 / 4013 | 256 / 1249 | 256 / 1745 | 256 / 904 | 256 / 857 |

The SS-R13 corrections produce the following table. All five rows return live certificates,
without exhaustion. Their last complete depth brackets are recorded in the implementation report.

| Stage, cells / features | Cylinder edge, Margin 0 | Rim edge, Margin 0 | Rim edge, Margin 0.001 | Cone edge, Margin 0 | Shoulder edge, Margin 0 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Initial classification | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| EPA and hull maintenance | 9 / 241 | 11 / 336 | 11 / 337 | 4 / 70 | 8 / 198 |
| Support evaluation | 67 / 611 | 69 / 631 | 69 / 645 | 62 / 561 | 66 / 601 |
| Plane verification | 17 / 23 | 23 / 41 | 23 / 41 | 19 / 29 | 20 / 32 |
| Witness construction | 1 / 14 | 1 / 21 | 1 / 21 | 1 / 25 | 1 / 21 |
| Encoding | 0 / 1 | 0 / 1 | 0 / 1 | 0 / 1 | 0 / 1 |
| Total | 94 / 890 | 104 / 1030 | 104 / 1045 | 86 / 686 | 95 / 853 |

The initial before-change run exits 1 with six assertion failures, including the equality
regression. The instrumented after-change run exits 1 with 21 passed and 7 failed out of 28.
All five stage rows pass. The seven remaining failures require the exact minimum's feature labels
rather than the attained plane's features. SS-R12 already defines support-functional witnesses.
A plane inside the global error bracket may expose a different original feature than the exact
minimum, so forcing those labels would require the stationary-pair construction SS-R11 forbids.
The tests now check the actual support provenance instead. Independent installed snapshots prove
the exposed target face/edge/vertex set, the local id must refer to vertices in that set, and exact
shell equations check the reported query feature. All hand-derived optima and independent member
translation oracle assertions remain, with no numeric relaxation.

The before/after logs are task-2c.2-r13-before-red.log and task-2c.2-r13-after.log under
/tmp/khaozengine-short-shell. All instrumentation and diagnostic-only classes are removed before
final verification. No root-backed or rootless irrational fraction is admitted for penetration.
The r0/r1 producers, installed capsule geometry/arithmetic, raw dispatch identity, arrival ball,
roof and footprint protections and the seven degree-1 baseline sources remain unchanged.

The first uninstrumented selection passes all support-provenance assertions but exits 1 with
20 passed and 3 independent upper-oracle rejections. One focused diagnostic confirms that each
rejection tests support in the encoded translation vector's own direction. A clearing vector
need not itself be a separating normal after componentwise binary32 encoding. The independent
member oracle now uses an exact translated snapshot and its existing GJK proposals plus exact
continuous support predicates to find a positive separation lower bound. It reads no production
normal or hull. Its existing 256-iteration ceiling and the ordinary distance-oracle stopping
condition are unchanged. The independent lower-depth proof is unchanged. Failure to independently
prove separation still rejects the upper side. A residual-overlap negative control is added.
This oracle correction does not change any production work count in the after table.

Final uninstrumented verification is green, with no compiler warnings or errors.

| Log under /tmp/khaozengine-short-shell | Exit | Executed result |
| --- | ---: | --- |
| task-2c.2-r13-green-final.log | 0 | 24 passed, 0 failed, 0 skipped |
| task-2c.2-r13-reg-game.log | 0 | 472 passed, 0 failed, 0 skipped |
| task-2c.2-r13-reg-footprint.log | 0 | 741 passed, 0 failed, 0 skipped |
| task-2c.2-r13-reg-navigation.log | 0 | 7 passed, 0 failed, 0 skipped |

The three Common commands run exactly once, synchronously, including the unchanged capsule
families and captured stairs. The seven unchanged normalized full-file hashes use null execution,
hence degree 1. The final navigation command rebuilds Bepu after removing the unused scalar-count
diagnostic accessor. No work, storage or exact-integer ceiling or tolerance is changed. No source
change to the four r0/r1 producers, capsule APIs/arithmetic, InstalledSweepLeaf, Movement,
Locomotion, raw layer identity, roof/footprint protections or baseline fixtures is present.
The member certificate makes no global-union MTV claim. Whole-view completeness, deepest-member
ties and recovery requery remain Tasks 3/7. Full solution verification remains Task 12.

The final staged dashes/prose/file-size/whitespace guards and whole-tree guards pass. Instruction
size is 8,310 / 16,384 bytes. The documentation-version guard confirms unchanged 21.12.1 declarations
and package inventory. No baseline or exemption changes. Task 2c.2 is complete at this boundary,
with integration, push, packaging and release reserved for the owning orchestrator.


## Task 2b.2 bounded temporal proposal refusal

Task 2b.2 stops as NEEDS_CONTEXT on BASE b455cb675. The required early diagnostic
exhausts the unchanged 256-cell account on the zero-margin HeadDisc/Face endpoint fixture.
No implementation is accepted. Production code and tests are restored to the accepted base.
This checkpoint changes this verification record only. The complete attempted diff remains at
.superpowers/sdd/2026-10-19-short-body-shell/task-2b.2-attempt.diff, 41,986 bytes,
SHA-256 30668d41e1a5ddecaf9b0f119c7eee3bf305e02d0b8e4f83976c53c6d29b076e.
Its git apply --check succeeds against the restored base.

The attempt uses at most four labelled original endpoint vertices in the existing proposal
simplex capacity. Rounded GJK only proposes that inner source. The accepted static constructors
verify its attained pair, exact barycentrics recover time, and original-member support and
closed-cone predicates revalidate the pair. Rational overlapping upper endpoints consume the
accepted 2c.2 penetration path. No Sweep is published and no inner-source miss publishes Clear.

Both named runs use build-slot, synchronously and without --heavy. Logs are under
/tmp/khaozengine-short-shell.

| Log | Exit | Executed result |
| --- | ---: | --- |
| task-2b.2-red.log | 1 | No tests executed. Expected missing ConvexImpactCertificate type |
| task-2b.2-early.log | 1 | 6 passed, 2 failed, 0 skipped, total 8 |

The early run passes exact rank 0/1/2/3 affine recovery, the box event at both margins,
the attained 1/3 tangent at both margins, inner-source miss refusal, and foreign-leaf,
request, scratch and expired-loan controls. The closed-domain matrix passes apex, lower
cone, shoulder and cylinder against face, edge and vertex. HeadDisc/Face then exhausts.
The separate rounded circular tangent refuses before time recovery, without exhaustion.
No full green result, complete matrix or circular margin-root coverage is claimed.

Temporary stage traces give the following event-creation accounts. These are observations,
never absolute counter assertions. Original validation includes touching normal checks and
encoding. Penetration is separately shown. Post-return test assertions charge extra features.

| Stage, cells / features | Box, Margin 0 | Box, Margin 0.001 | Cylinder tangent, Margin 0 | Cylinder tangent, Margin 0.001 | Rounded rim tangent, Margin 0.001 | Head disc face, Margin 0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Original source preflight | 0 / 8 | 0 / 8 | 0 / 4 | 0 / 4 | 0 / 4 | 0 / 8 |
| Support proposals and simplex reduction | 8 / 50 | 8 / 50 | 10 / 53 | 10 / 53 | 6 / 19 | 256 / 2910 |
| Static attainment | 3 / 6 | 3 / 6 | 37 / 51 | 42 / 59 | 3 / 7 | 0 / 0 |
| Margin comparison after static attainment | included below | included below | included below | included below | 0 / 1 | 0 / 0 |
| Exact time recovery and margin comparison | 1 / 5 | 1 / 5 | 1 / 5 | 1 / 5 | 0 / 0 | 0 / 0 |
| Original-member validation and touching encoding | 10 / 20 | 1 / 11 | 9 / 28 | 1 / 14 | 0 / 0 | 0 / 0 |
| Accepted 2c.2 penetration and encoding | 88 / 773 | 86 / 736 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| Total through return | 110 / 862 | 99 / 816 | 57 / 141 | 54 / 135 | 9 / 31 | 256 / 2918 |

The zero-margin head-disc fixture is a dyadic rectangular hull above the head plane.
Its near face spans X and Z in [-1/64, 1/64], Y=3/4, with its far face at Y=49/64.
The query starts at feet (0,-1/8,0), moves by (0,1/8,0), and touches that finite face at t=1.
The proposal phase consumes every cell before its first static certificate attempt.
The trigger waits for a repeated retained endpoint label, four retained slots, or a rounded
closest norm at most Margin. None triggers on this required endpoint contact. The feature
account remains below its unchanged 4096 cap. This is an incomplete proposal policy, not
proof that the temporal-source identity cannot succeed within the existing budgets.

The rounded-rim fixture has R=5/16, head Y=3/4, original vertex (3/16,3/4,1/4),
feet (-m,-2m,2m), displacement (-8,-15,31)/512 and m=0.001f.
The previous report claimed an attained fraction of 1024*m/25 and offset
m*(9,20,12)/25. The SS-R14 diagnostic below disproves that positive-time claim for the
archived displacement. The line attains that offset at -1024*m/25, outside [0,1].
The attempt proposes an inner segment whose static distance is strictly above m, then
refuses. It reaches no retained circular root. This is not an undecided root sign.

The mandatory cap stop applies immediately. No later implementation, focused rerun,
Common regression selection or full-suite verification runs on the unsuccessful attempt.
Capsule families, seven degree-1 navigation baselines, roof and footprint protections,
arrival geometry, raw identity and all four r0/r1 sweep producers remain unchanged.
The archived attempt is incomplete and must not be applied as an accepted implementation.
Original circular overlap, complete finite-stratum coverage and retained root barycentrics
still need completion. The owning orchestrator must review the bounded proposal policy
before continuing. No cap, tolerance, per-pair producer or second certification path is added.


## Task 2b.2 continuation under SS-R14

Status NEEDS_CONTEXT on BASE bc9fa7dc5. The required first diagnostic compiles in Release
without warnings or errors, passes seven tests and fails the archived rounded-rim test.
All eighteen zero-margin finite-stratum rows now pass, including HeadDisc/Face at 28 cells.
No production cap is exhausted except the intentional zero-cell negative control.
The remaining failing fixture is strictly separated throughout [0,1], so it cannot supply
the required attained positive-time event. No implementation is accepted at this checkpoint.

The prototype seeds from exact original endpoint certificates, selects the attained target
feature's exact affine sub-simplex, lifts its original vertices to the other endpoint and
verifies the inner source before iterating. Separated certificates drive exact support
directions. Exact squared-distance decrease gates continuation. Rounded reduction no longer
blocks the first static certificate on a flat pair. Endpoint certificates are obtained
inside this test entry point on the same primitive account. A coordinator loan of an already
retained endpoint certificate remains unfinished.

The only build/test run is the required stage diagnostic, synchronously through build-slot.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexTemporalSimplexTests' --logger 'console;verbosity=detailed'
```

Log /tmp/khaozengine-short-shell/task-2b.2-r14-diagnostic.log. Exit 1.
Total 8, passed 7, failed 1, skipped 0. The failing test is
RoundedCircularDoubleRootRetainsItsUnsquaredSigns, 32 cells and 74 features, no exhaustion,
default impact evidence. The previous report contains the earlier test-first red result.
There is no all-green class result from this continuation.

Temporary cumulative traces produce the following stage differences. Counts are observations,
never absolute assertions. Original validation includes touching encoding. Test assertions
after return add features. A dash means the stage is not reached.

| Stage, cells / features | Box m=0 | Box m=0.001 | Cylinder tangent m=0 | Cylinder tangent m=0.001 | Archived rounded rim m=0.001 | HeadDisc/Face m=0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Original source preflight | 0 / 8 | 0 / 8 | 0 / 4 | 0 / 4 | 0 / 4 | 0 / 8 |
| Original endpoint certificates | 2 / 10 | 2 / 10 | 36 / 57 | 36 / 57 | 23 / 42 | 7 / 19 |
| Exact affine seed selection and lifting | 6 / 22 | 6 / 22 | 1 / 4 | 1 / 4 | 1 / 4 | 6 / 22 |
| Exact-normal support and affine refinement | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 2 / 7 | 0 / 0 |
| Inner static attainment | 6 / 12 | 6 / 12 | 17 / 22 | 22 / 29 | 6 / 14 | 6 / 12 |
| Margin, exact progress and time recovery | 1 / 6 | 1 / 6 | 1 / 4 | 1 / 4 | 0 / 3 | 1 / 6 |
| Original-member validation and touching encoding | 10 / 20 | 1 / 11 | 9 / 28 | 1 / 14 | - | 8 / 33 |
| Accepted 2c.2 penetration and encoding | 88 / 773 | 86 / 736 | 0 / 0 | 0 / 0 | - | 0 / 0 |
| Total through return | 113 / 851 | 102 / 805 | 64 / 119 | 61 / 112 | 32 / 74 | 28 / 100 |

The rounded-rim trace reaches two noncircular static certificates, at cumulative 27 / 57 and
32 / 72. Both are separated by more than m. The latter makes no exact distance progress and
the attempt refuses. No circular retained root, exact margin tie, recovered time or original
operational normal is reached. This is not a cap or radical-sign failure.

The fixture's exact Y coordinates prove its separation independently of that implementation.
Let m be the positive represented float 0.001f. The original target's minimum Y is 3/4.
Feet Y is -2m-15t/512, so the grown query's maximum Y is 3/4-m-15t/512. The gap is
m+15t/512, strictly positive for every t in [0,1]. Even the base query's vertical gap is
at least 2m, already greater than the admitted margin. Thus no required event exists in
the requested interval. An inner-source miss still does not publish Clear.

The hand-derived offset m*(9,20,12)/25 has length m and is perpendicular to (8,15,-31).
With the archived displacement (-8,-15,31)/512 it is attained at t=-1024m/25. Reversing
that displacement would place the claimed tangent at positive time. No fixture or expected
status is changed here. The owning orchestrator must resolve this input contradiction
before another continuation. It must not be treated as a failure of circular certification.

The eighteen closed-domain rows all pass at margin zero, including head disc and rim rows
that the previous cap stop prevented from reaching. The both-margin full matrix, stationary
intervals, arbitrary represented poses, independent oracle extension, immutable root-defined
weights, circular strict-overlap normal conversion and remaining radical/budget controls
remain incomplete. The three Common regression commands do not run after this mandatory
diagnostic stop. No new capsule, footprint or degree-1 baseline result is claimed.

All attempted source and test changes, including instrumentation, are archived and restored
before this evidence-only commit. The new archive is
.superpowers/sdd/2026-10-19-short-body-shell/task-2b.2-r14-attempt.diff, 55,132 bytes,
SHA-256 0b9e8ee45cc0da4185334e448c1943fa207386fe08745799412e98995d9c591c.
Its git apply --check exits 0 against the restored base. The earlier archive remains unchanged.
The full report, stage matrix and self-review are in task-2b.2-report.md in that SDD directory.
No source/test path, cap, ceiling, tolerance, r0/r1 producer, capsule arithmetic, baseline,
arrival rule, roof/footprint protection or raw identity change is committed. No instrumentation
remains in production. No push, merge, tag, branch change or integration runs.

## Task 2b.2 accepted continuation under SS-R15


Starting BASE 6ee04598b. The exact root, branch, HEAD and clean tracked-tree guard
passed before task work. The SS-R14 archive applied after git apply --check, both exit 0.
Read the complete brief and report, SS-R1 through SS-R15, the applicable engine rules,
the Global Constraints and the design's impact and bounded-work sections. Used execution,
systematic debugging, test-driven development and verification-before-completion.
No subagents or reviewers are dispatched. No fetch, branch change, integration, push,
release, tag, full suite, full solution build, timing, load or consumer launch is authorized.

### Corrected fixture and independent oracle

The displacement is corrected to (8,15,-31)/512, keeping all other fixture values.
The independent sweep oracle tests an exact rational proposed time using the represented
snapshot vertices and the closed meridian circle inequality, retaining its unsquared sign.
At 1024*m/25 the original vertex is at Euclidean distance exactly m from the rim.
The archived negative displacement remains a control. An independent continuous support
plane over both endpoint copies proves separation for its whole interval.

First diagnostic command, exit 1, 8 executed, 7 passed, 1 failed.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexTemporalSimplexTests' --logger 'console;verbosity=detailed'
```

Log /tmp/khaozengine-short-shell/task-2b.2-r15-diagnostic.log. The corrected tangent's
independent attainment check passes. The failure is the old oracle's undecided negative
control, before the corrected production event runs. Added the independent endpoint
support-plane check, without changing production. The same command then exits 0,
8 executed and passed. Log /tmp/khaozengine-short-shell/task-2b.2-r15-corrected.log.
The negative-time production request returns default event evidence at 32 cells and
74 features, without exhaustion. It cannot publish Clear. The corrected root event
returns at 86 cells and 439 features, without exhaustion. These are observations only.
The deliberately lowered zero-cell control is the only exhausted account in that run.

### Completion of the remaining certificate obligations

Added the missing attained-weight and endpoint-loan tests before their implementation.
The named focused command, without the detailed logger, exits 1 at the intended missing
Attainment/Original members and endpoint-loan overload. No tests execute. Log
/tmp/khaozengine-short-shell/task-2b.2-r15-obligations-red.log.

The attained record now copies its original vertex/time labels and exact weight expressions
into private arrays exposed only as read-only spans. Circular edge weights are 1-s and s
at the same retained unsquared stationary root. Other slots carry exact zero. Rational and
quadratic weights retain the exact affine solution. Root refinement changes enclosures only,
never labels, coefficients or the affine identity. Fraction/root ownership and replacement
remain enforced, and inner certificates must use the unshifted original-feet frame.

The impact certificate distinguishes primal attainment from its operational normal. Its Pair
is explicitly the inner primal certificate. It never publishes that pair's normal or feature
as an original target result. Touching revalidates both full original cones and uses 2c.1.
A strict circular margin gap supplies a Lipschitz neighbourhood, from which a dyadic upper
endpoint is selected. The accepted 2c.2 path independently proves overlap and supplies its
original-member minimum-translation normal there. Penetration can borrow the existing scratch
generation, so the root-defined event and its immutable weights remain alive. No second
certifier, moving equation, additional arithmetic extension or raised limit is introduced.

A coordinator can loan its already attained original endpoint certificate. Geometry, pose,
request, fraction, scratch and translation are checked before reuse. The existing four-slot
proposal scratch holds exact temporal labels during refinement. Removed the unused rounded
proposal/reduction prototype, which was no longer part of the SS-R14 ordering.

The first implementation diagnostic uses the same detailed focused command, exits 1,
21 executed, 20 passed and 1 failed. Log
/tmp/khaozengine-short-shell/task-2b.2-r15-obligations.log. The only failure is a test assertion
on the m=0.001 Apex/Vertex row. The accepted penetration bracket uses a Shoulder support,
while the primal attained point is Apex. An operational MTV's support feature need not equal
the primal event's feature. The test now checks the exact primal common point and intended
stratum, separately requiring the accepted original penetration certificate. The m=0 contact
feature assertion remains unchanged. This changes no geometry or tolerance.

The next detailed focused command exits 0, all 21 tests pass, including all 36 finite-stratum
rows. Log /tmp/khaozengine-short-shell/task-2b.2-r15-matrix.log. Only deliberate lowered budgets
exhaust. The root-defined circular strict overlap returns a live attained record and a live
2c.2 normal at a rational dyadic endpoint, at 150 cells and 934 features through return.
The original-normal control supplies a positive inner +Y pair whose normal fails on the full
member. It succeeds with the accepted original -X penetration normal. Stationary paths,
arbitrary represented 3D boxes and non-box hulls, endpoint reuse, root lifetime, foreign leaf,
foreign scratch, another request, independent roots, lowered cells/features/coefficients and
original radical-sign controls pass. Probe budgets derive from the same run, never fixed pins.

### SS-R15 stage tables

Temporary stage checkpoints in task-2b.2-r15-matrix.log give the following differences.
Every entry is cells / features, observed in this run only. Static attainment includes the
accepted circular root machinery. Original validation includes touching encoding or dyadic
endpoint selection. Penetration is the existing 2c.2 path. Totals exclude post-return test
comparisons. No interval checks or coordinator work are hidden in this event-only routine.

| Stage | Box m=0 | Box m=0.001 | Rim tie m=0.001 | Circular strict overlap | HeadDisc/Face m=0 | Shoulder/Edge m=0.001 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Original preflight | 0 / 8 | 0 / 8 | 0 / 4 | 0 / 4 | 0 / 8 | 0 / 6 |
| Original endpoint certificates | 2 / 10 | 2 / 10 | 33 / 54 | 33 / 54 | 7 / 19 | 129 / 237 |
| Exact affine seed and lifting | 6 / 22 | 6 / 22 | 1 / 4 | 1 / 4 | 6 / 22 | 3 / 12 |
| Inner static attainment | 6 / 12 | 6 / 12 | 52 / 166 | 54 / 170 | 6 / 12 | 26 / 36 |
| Margin, weights and time | 1 / 6 | 1 / 6 | 0 / 79 | 0 / 56 | 1 / 6 | 1 / 5 |
| Original validation and encoding | 10 / 22 | 1 / 13 | 0 / 193 | 0 / 157 | 8 / 34 | 1 / 24 |
| Existing 2c.2 penetration | 88 / 773 | 86 / 736 | 0 / 0 | 62 / 489 | 0 / 0 | 80 / 685 |
| Total through return | 113 / 853 | 102 / 807 | 86 / 500 | 150 / 934 | 28 / 101 | 240 / 1005 |

The negative-time control remains 32 / 74. Its stages are preflight 0 / 4, original endpoints
23 / 42, affine seed 1 / 4, inner static attainment 6 / 14, exact-normal refinement 2 / 7,
and margin/progress decisions 0 / 3. It never reaches original-event validation and returns
default evidence without exhaustion.

The complete matrix follows. These counts include post-return validation, so feature totals
are slightly larger than the through-return totals above. There are no absolute counter pins.

| Query feature | Face m=0 | Edge m=0 | Vertex m=0 | Face m=0.001 | Edge m=0.001 | Vertex m=0.001 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Apex | 47 / 168 | 48 / 142 | 26 / 72 | 129 / 831 | 115 / 600 | 67 / 336 |
| Lower cone | 47 / 166 | 121 / 229 | 26 / 67 | 129 / 828 | 192 / 832 | 80 / 456 |
| Shoulder | 47 / 164 | 168 / 354 | 26 / 67 | 133 / 910 | 240 / 1008 | 83 / 543 |
| Cylinder | 47 / 164 | 121 / 219 | 26 / 67 | 131 / 864 | 189 / 772 | 79 / 451 |
| Head disc | 28 / 104 | 28 / 78 | 40 / 96 | 100 / 643 | 86 / 476 | 83 / 377 |
| Rim | 54 / 177 | 146 / 285 | 22 / 65 | 136 / 862 | 217 / 798 | 75 / 421 |

No required row exhausts. All instrumentation is removed before the final focused and Common
runs. The final focused coverage also includes independent represented-target halfspace and
meridian checks of each matrix endpoint, exact original margin-tie/upper-witness checks and
nonzero paths with a stationary contact interval.

### Final focused and Common verification

After removing instrumentation, the exact detailed focused command exits 0, 23 executed
and passed, zero failures/skips. Log /tmp/khaozengine-short-shell/task-2b.2-r15-green.log.
The two additional rows are the nonzero stationary contact interval at both margins.
All independent endpoint membership checks and the exact original margin tie pass.

The first Common command is copied verbatim from the appended brief and executed once,
synchronously through build-slot --heavy. Exit 0, 495 executed and passed, zero failures/skips.
Log /tmp/khaozengine-short-shell/task-2b.2-r15-common-game.log. This includes the accepted
Task 2 classes, capsule families, ShellGeometry, GroundCore, general/hull/rotated box,
cylinder/curved/compound/mesh sweeps, captured stairs and the SS-R5/SS-R6 identity controls.

The second Common command runs once with --no-build, also synchronously through the heavy
slot. Exit 0, 741 executed and passed, zero failures/skips. Log
/tmp/khaozengine-short-shell/task-2b.2-r15-common-footprint.log. All footprint, proof-reuse
geometry and proof-reuse work controls remain green.

The third Common command runs once, exit 0, seven executed and passed, zero failures/skips.
Log /tmp/khaozengine-short-shell/task-2b.2-r15-common-navigation.log. The unchanged test calls
BuildFixture(fixture) with its default null execution argument, hence degree 1, and compares
each normalized full-file SHA-256 with its unchanged pin. All seven equality checks pass.

| Fixture | Unchanged normalized SHA-256 |
| --- | --- |
| low-lip-bank | 494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922 |
| deck-over-ground | 77addcbc9c054f85e1a14f916bd95af75d02f105c54ad8e56a3a25716b0d44cc |
| wade-only-pool | a735ea5a41a1adb58666219b053197ed457b3c7edd830e18f29c7c5c8a1a1635 |
| rebased-origin | e5c86385e4141a289d41d8efbb66766e5ac7c942f825521fff91dede8e9b4eee |
| mixed-legacy-contact | 94fc16f1dee6cfa4c99db7ce490443271a0eb2fe3d9f0906312a836c5cd9a856 |
| stair-accepted-refused | 62a061314987a1b8243f9eb8da639cd0239ec200f8f62f11e69e62f5db20959a |
| multi-layer-row-bands | ae89cde4394002043b200b5e25e4eb0ef778ced4438cd7e985bde8dd96de811a |

The exact three Common commands, each exit 0, are

```bash
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexIntervalCertificateTests|FullyQualifiedName~ConvexInitialStateTests|FullyQualifiedName~ConvexPlanarPenetrationTests|FullyQualifiedName~ConvexTemporalSimplexTests|FullyQualifiedName~ConvexBracketSweepTests|FullyQualifiedName~ConvexRoundedVertexSweepTests|FullyQualifiedName~ConvexPlanarSweepTests|FullyQualifiedName~ConvexSweepOracleTests|FullyQualifiedName~ConvexSweepPublicationTests|FullyQualifiedName~ConvexPlanarDistanceTests|FullyQualifiedName~ConvexCircularEdgeTests|FullyQualifiedName~ConvexGeneralPlanarTests|FullyQualifiedName~ConvexMarginOracleTests|FullyQualifiedName~ConvexFeaturePairCoverageTests|FullyQualifiedName~ConvexFeatureKernelTests|FullyQualifiedName~ConvexQueryContractTests|FullyQualifiedName~ShellGeometryTests|FullyQualifiedName~GroundCoreTests|FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~BepuCertifiedCapsuleSweepTests|FullyQualifiedName~CertifiedRotatedBoxSweepTests|FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedCurvedSweep|FullyQualifiedName~CertifiedCompoundSweepTests|FullyQualifiedName~CertifiedMeshSweepTests|FullyQualifiedName~CapsuleSweepValueTests|FullyQualifiedName~SupportNeighborhoodCurvedTests|FullyQualifiedName~SupportCurvedWorkTests|FullyQualifiedName~SupportDiscEdgeTests|FullyQualifiedName~ExactCurvedInstallControlTests|FullyQualifiedName~InstalledSupportPoseTests' --logger 'console;verbosity=detailed'
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~SupportFootprintTests|FullyQualifiedName~SupportFootprintProofReuseGeometryTests|FullyQualifiedName~SupportFootprintProofReuseWorkTests'
build-slot --label short-shell-design --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ContactNavigationBaselineTests.SerialBakeMatchesNormalizedFullFileBaseline'
```

### Self-review and finish boundary

Self-review covered the complete task diff, the exact temporal-source affine identity,
all four ranks, closed source domains, immutable root weights and lifetime, original-member
normal validation, rational penetration admission, dyadic endpoint selection, exact margin
and radical signs, all new negative controls and the unchanged protected-source diff.
The missing-API red, the oracle negative-control correction, the incorrect feature assertion
and every final green result are recorded above. This is an author self-review, with no
subagent or reviewer, as requested.

There is one event path through the accepted static certificate. An unsupported mixed field,
independent circular-root comparison or undecided predicate refuses atomically. The unit does
not claim universal bounded convergence or publish a Sweep/Clear result. The final bracket
coordinator and retirement of r0/r1 remain Task 2b.3, as planned. No obligation of 2b.2 is
deferred. There are no material concerns from this self-review.

The four r0/r1 producers, capsule APIs and arithmetic, InstalledSweepLeaf, all movement and
navigation baseline sources, raw layer identity, the 1 mm arrival ball, roof and footprint
protections, version and release files are unchanged. All temporary instrumentation is
removed. No cap, ceiling, tolerance, dependency, version or exemption is changed. Full solution
build/test/format remains Task 12. No manual validation is required for this headless unit.
Integration, pushing and release remain with the orchestrator. No push, merge, tag or branch
change is performed.

### Self-review correction, installed bound versus temporal coordinates

The final review found one domain check applied to the wrong coordinates. The retained
static helper checked source.Vertex(i) against the existing 2048 m installed-target bound.
For an inner temporal source, those coordinates also contain the original displacement.
A valid installed target near zero, with query feet (4096,0,0), displacement (-4096,0,0)
and the singleton event interval [1,1], therefore refused before static attainment.
The original feet at t=1 are exactly zero, so this is an admitted and representable event.

Added both-margin deterministic regressions and an out-of-domain original-vertex control
before the correction. Exact red command

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexTemporalSimplexTests.TemporalTranslation' --logger 'console;verbosity=detailed'
```

Exit 1, three executed, one passed and two failed. Both valid rows refuse at 8 cells and
39 features, without exhaustion. The installed-bound negative control passes. Log
/tmp/khaozengine-short-shell/task-2b.2-r15-frame-red.log.

Corrected ConvexFeatureKernel.TryDistanceCertificate to check each inner label's original
installed vertex through the same represented map. The original 2048 m bound is unchanged,
and TryImpact still preflights every original target vertex. Request bounds and exact-bit
limits also remain unchanged. This is a provenance correction in the reused static helper,
with no new equation, certificate path, limit or tolerance. The singleton interval tests
also cover exact endpoint contact after a large represented translation.

The detailed full focused command then exits 0, all 26 tests pass, zero failures/skips.
Log /tmp/khaozengine-short-shell/task-2b.2-r15-frame-green.log. The earlier 23-test and Common
passes predate this correction and are not reused as final proof. The three unchanged Common
commands are rerun once each after this source change for fresh final verification.

### Final verification after the self-review correction

All commands run synchronously, one at a time, through build-slot. The unchanged Common
commands above run once each after the final source correction. Final results are

| Selection | Exit | Passed / executed | Log under /tmp/khaozengine-short-shell/ |
| --- | ---: | ---: | --- |
| Focused temporal simplex class | 0 | 26 / 26 | task-2b.2-r15-frame-green.log |
| Common Game | 0 | 498 / 498 | task-2b.2-r15-final-common-game.log |
| Common footprint | 0 | 741 / 741 | task-2b.2-r15-final-common-footprint.log |
| Common serial navigation | 0 | 7 / 7 | task-2b.2-r15-final-common-navigation.log |

There are no failures or skips. All seven unchanged normalized hashes again pass with
null execution, hence degree 1. Release compilation reports no warnings or errors.
The frame correction is the only production change after the earlier Common checkpoint,
and these fresh runs verify it. All source changes are complete before these final runs.

Self-review rechecked the frame correction against the original admitted-input bounds and
its positive and negative controls. No required row refuses, no production budget exhausts
and no material concern remains. The final diff adds no moving pair equation or second
certification path. Instrumentation, version, capsule APIs/arithmetic, r0/r1 producers,
InstalledSweepLeaf, raw identity, navigation baselines, arrival and roof/footprint protections
remain as recorded above. Task 2b.2 is complete, with integration and Task 2b.3 left to the
owning orchestrator. No manual validation is needed.


## Task 2b.3 bounded bracket sweep refusal

Status NEEDS_CONTEXT on BASE ab5180a0a. This is an evidence-only checkpoint. No bracket
sweep or producer retirement is accepted. The task's early mandatory-fixture stop applies.
The attempted implementation is archived locally at
`.superpowers/sdd/2026-10-19-short-body-shell/task-2b.3-attempt.diff`.
Its size is 25,330 bytes and SHA-256 is
`154ff9ed9d97ae5c879724f952a5b87110bf5a75073a47529b9a8c392c5babcf`.
Production and test files were restored to the accepted base before this documentation change.
The archive passes `git apply --check` against that restored base, exit 0.

### Diagnostic and exact stop

The first new-class red ran through build-slot and exited 1 before test execution on the
intended missing ConvexSweepEncoding.TryBracket API. The first coordinator diagnostic
compiled successfully, then failed its one test with 14 refusals among 24 fixture rows.
Seven rows exhausted 256 cells. Inspection found that a failed midpoint could fall back to
whole-source static enumeration through a default endpoint loan. That is forbidden here.
The diagnostic coordinator was corrected to loan only its active-feature certificate at the
midpoint, including the existing static face common-point acceptance. If that feature cannot
be revalidated, it refuses instead of restarting full original-target Distance. There are no
moving feature equations, second Sweep route or new primitive admissions in this corrected
attempt. The initial event loans the retained initial endpoint certificate.

The corrected diagnostic still fails. Ten rows return Hit, fourteen refuse, and seven
exhaust 256 cells. All 24 use one primitive admission, and all restore polynomial Used to
zero. No partially consumed account is reset. Every observed value below is specific to this
run, with no CPU, SIMD or schedule-dependent test pin. No tolerance, ceiling or budget changed.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests.StageDiagnostic' --logger 'console;verbosity=detailed'
```

The first command exits 1 with zero tests on the intended missing typed symbol. The second
command ran once for each coordinator revision. Both exit 1, one executed and failed test,
zero skipped. The corrected run has no compiler warnings or errors. Logs are
`/tmp/khaozengine-short-shell/task-2b.3-red.log`, `task-2b.3-stage.log` and
`task-2b.3-stage-retained.log` in that same directory. None is a green class run.

### Fixture construction

All shapes have radius 0.25, apex height 0.25, shoulder height 0.5 and head height 0.75.
Each requested margin is exactly binary32 0 or 0.001. Let alpha be 2^-4, 2^-12 or 2^-20.
The face fixture is a box with half extents (0.03125,1,2), centre (0.28125,0.625,0),
feet (-margin-alpha/2,0,-0.5) and displacement (alpha,0,1). Its near face is x=0.25.
The apex approach uses half extents (2,0.03125,2), centre (0,0.21875,0),
feet (-0.5,margin+alpha/2,0) and displacement (1,-alpha,0). Its top is y=0.25.
Both have an exact original-path contact at t=1/2.

The rim approach is a tetrahedron with p=(0.25,0.75,0) and vertices p,
p+(1,0,0)/32, p+(1,1,1)/32 and p+(1,2,-1)/32. Feet are
(-margin-alpha/2,0,-0.09375), displacement is (alpha,0,0.1875), and t=1/2 is an
attained contact. The zero-angle face and rim rows use alpha=0 with that tetrahedron at
p=(0.25,0.625,0) or p=(0.25,0.75,0). The apex tangent tetrahedron has vertices
(0,0.25,0), (0.03125,0.21875,0), (-0.03125,0.21875,0.03125) and
(0,0.21875,-0.03125), with feet (-0.09375,margin,0) and displacement (0.1875,0,0).
These closed contacts follow directly from the original shell and target equations.
They were not weakened to allow Unresolved. The complete independent-oracle matrix is not
claimed at this stop.

### Stage breakdown

C/F means cells/features. These are geometry-coordinator counts after one explicit primitive
admission, excluding the kernel's original installed-vertex preflight feature charges. Adding
that preflight cannot remove a cell exhaustion. Initial includes retained static classification.
Plane includes the initial separator and failed full-path restriction. First event includes
accepted temporal seeding, inner static attainment, original revalidation and any 2c.2 normal.
Refinement includes cached restrictions, active-feature revalidation, earlier attained events
and attempted encoding. Each row sums those four stages into its total.

| Family | Margin | Approach | Published | Initial C/F | Plane C/F | First event C/F | Refinement C/F | Total C/F | Exhausted |
| --- | ---: | --- | --- | --- | --- | --- | --- | --- | --- |
| face | 0 | 2^-4 | False | 3/20 | 2/23 | 117/851 | 134/935 | 256/1829 | True |
| face | 0 | 2^-12 | False | 3/20 | 2/23 | 113/822 | 138/962 | 256/1827 | True |
| face | 0 | 2^-20 | False | 3/20 | 2/23 | 125/1034 | 25/121 | 155/1198 | False |
| face | 0 | tangent | False | 32/51 | 2/15 | 28/69 | 66/200 | 128/335 | False |
| rim | 0 | 2^-4 | True | 32/51 | 2/15 | 19/61 | 38/135 | 91/262 | False |
| rim | 0 | 2^-12 | False | 32/51 | 2/15 | 28/72 | 58/174 | 120/312 | False |
| rim | 0 | 2^-20 | False | 32/51 | 2/15 | 28/72 | 66/200 | 128/338 | False |
| rim | 0 | tangent | False | 32/51 | 2/15 | 28/72 | 66/200 | 128/338 | False |
| apex | 0 | 2^-4 | True | 7/27 | 2/23 | 20/142 | 12/46 | 41/238 | False |
| apex | 0 | 2^-12 | True | 7/27 | 2/23 | 20/142 | 12/46 | 41/238 | False |
| apex | 0 | 2^-20 | False | 7/27 | 2/23 | 20/142 | 26/120 | 55/312 | False |
| apex | 0 | tangent | True | 16/27 | 2/15 | 27/71 | 10/34 | 55/147 | False |
| face | 0.001 | 2^-4 | False | 3/20 | 2/23 | 104/786 | 147/1072 | 256/1901 | True |
| face | 0.001 | 2^-12 | False | 3/20 | 2/23 | 107/812 | 144/1135 | 256/1990 | True |
| face | 0.001 | 2^-20 | False | 3/20 | 2/23 | 112/916 | 25/121 | 142/1080 | False |
| face | 0.001 | tangent | True | 32/51 | 2/15 | 25/60 | 59/202 | 118/328 | False |
| rim | 0.001 | 2^-4 | True | 32/51 | 2/15 | 69/364 | 119/1158 | 222/1588 | False |
| rim | 0.001 | 2^-12 | False | 32/51 | 2/15 | 100/928 | 122/987 | 256/1981 | True |
| rim | 0.001 | 2^-20 | True | 32/51 | 2/15 | 120/1890 | 59/202 | 213/2158 | False |
| rim | 0.001 | tangent | True | 32/51 | 2/15 | 25/63 | 59/202 | 118/331 | False |
| apex | 0.001 | 2^-4 | False | 7/27 | 2/23 | 244/1090 | 3/6 | 256/1146 | True |
| apex | 0.001 | 2^-12 | True | 7/27 | 2/23 | 227/1066 | 12/46 | 248/1162 | False |
| apex | 0.001 | 2^-20 | False | 7/27 | 2/23 | 231/1168 | 16/82 | 256/1300 | True |
| apex | 0.001 | tangent | True | 16/27 | 2/15 | 19/52 | 13/54 | 50/148 | False |

The refinement cells are separated further below. No full original-target enumeration is
used by the corrected coordinator at a midpoint. Temporal events still use the accepted
static inner-source verifier. Its root, original-member and penetration costs are included
in the event column, not hidden or reset. There is no bound of 33 on those proposal costs.

| Family | Margin | Approach | Interval / active feature / event / encoding cells | Contractions | Last [a,b] |
| --- | ---: | --- | --- | ---: | --- |
| face | 0 | 2^-4 | 3/2/129/0 | 2 | 0.47513670959043997,0.5999120019408407 |
| face | 0 | 2^-12 | 3/2/133/0 | 2 | 0.47500121802090733,0.599669652024075 |
| face | 0 | 2^-20 | 2/2/21/0 | 0 | 0,0.9500000095366614 |
| face | 0 | tangent | 15/42/9/0 | 8 | 0.498046875,0.5 |
| rim | 0 | 2^-4 | 12/13/12/1 | 10 | 0.4990234375,0.5 |
| rim | 0 | 2^-12 | 13/36/9/0 | 7 | 0.4978162948226169,0.5017361081676769 |
| rim | 0 | 2^-20 | 15/42/9/0 | 8 | 0.49805363019307447,0.5000067816840277 |
| rim | 0 | tangent | 15/42/9/0 | 8 | 0.498046875,0.5 |
| apex | 0 | 2^-4 | 11/0/0/1 | 11 | 0.499755859375,0.5 |
| apex | 0 | 2^-12 | 11/0/0/1 | 11 | 0.499755859375,0.5 |
| apex | 0 | 2^-20 | 5/2/19/0 | 3 | 0.4375,0.5 |
| apex | 0 | tangent | 9/0/0/1 | 9 | 0.4990234375,0.5 |
| face | 0.001 | 2^-4 | 3/2/142/0 | 2 | 0.4751429547008304,0.5999206348716802 |
| face | 0.001 | 2^-12 | 3/4/137/0 | 2 | 0.4750012424349707,0.712501863652456 |
| face | 0.001 | 2^-20 | 2/2/21/0 | 0 | 0,0.9500000097273963 |
| face | 0.001 | tangent | 16/42/0/1 | 9 | 0.4990234375,0.5 |
| rim | 0.001 | 2^-4 | 12/18/88/1 | 10 | 0.4990234375,0.5 |
| rim | 0.001 | 2^-12 | 15/42/65/0 | 8 | 0.49978311880145604,0.5017430526006774 |
| rim | 0.001 | 2^-20 | 16/42/0/1 | 9 | 0.49903023301230653,0.5000068088107651 |
| rim | 0.001 | tangent | 16/42/0/1 | 9 | 0.4990234375,0.5 |
| apex | 0.001 | 2^-4 | 3/0/0/0 | 3 | 0.437501333712129,0.5000015242424332 |
| apex | 0.001 | 2^-12 | 11/0/0/1 | 11 | 0.4997558653267422,0.5000000059546498 |
| apex | 0.001 | 2^-20 | 5/2/9/0 | 3 | 0.43750000002035283,0.5000000000232604 |
| apex | 0.001 | tangent | 10/2/0/1 | 9 | 0.4990234375,0.5 |

### Two independent bottlenecks

The zero-margin face rows at 2^-4 and 2^-12 spend 117 and 113 cells on the first attained
event. Those events are near t=0.95, while the exact first contact is t=0.5. Earlier event
and original-normal work then consumes 129 and 133 additional cells, respectively. Both
reach 256 while the bracket is still wider than 0.12 m. The corrected attempt reuses its
initial plane and active original feature, but does not retain a reusable temporal-source
refinement across those event requests. This is a measured coordinator limitation, not a
proof that the accepted event producer can never fit a different coordinator policy.

The apex margin 0.001, 2^-4 row spends 7 cells on initial classification, 2 on the plane,
244 on the first event and the remaining 3 on prefix contractions. It exhausts with the
fraction bracket [0.437501333712129,0.5000015242424332]. The rim margin 0.001, 2^-12
row exhausts at 256/1981 with [0.49978311880145604,0.5017430526006774].

Separately, the zero-margin 2^-20 apex row refuses without exhaustion at 55/312.
Its bracket is [0.4375,0.5], a path width 0.06250000000002842 m. The retained axis
plane's uniform correspondence error is 2.980277713504623e-8 m. With normal closure
2^-20, that consumes 0.03125047683715824 of the fraction range. The true normal gap at fraction
0.00025 before contact is only 2.384185791015625e-10 m, about 125 times smaller than
the current uniform bound. Cached restriction alone cannot fix this direction's bound.
The zero-margin face and rim tangencies similarly refuse near fraction brackets
[0.498046875,0.5], with about 3e-8 m uniform error and quadratic normal closure.
A later continuation needs a valid tighter correspondence proof or another accepted plane
proposal strategy, as well as temporal event reuse. These observations do not authorize
removing correspondence error, widening a tolerance or increasing the work cap.

### Retirement and regressions

No producer is retired at this stop. ConvexPlanarSweep, ConvexSweepEdges,
ConvexSweepVertices and ConvexCircularVertexSweep remain exactly at ab5180a0a. The kernel
and dispatch, root-specific encoder entries, shared CircularContactEquation and arithmetic
policies, capsule APIs, ConvexAlgebraicRoots, ConvexQuadratic and static producers are restored.
No existing regression is migrated. The direct circular-producer sign regression and initial
overlap refusal assertions remain pending the successful replacement commit.

The full new class, lowered-budget controls, all-six-feature/three-target-stratum matrix,
duck/cow, represented-pose and large-coordinate completion, three Common selections, SS-R2
capsule families, roof/footprint protections and seven degree-1 navigation baselines are not
rerun after this mandatory stop. Earlier tasks' green results are historical only. Full
solution verification remains Task 12. No green bracket sweep, complete error budget or
retirement claim is made.

### Self-review and continuation boundary

The archived coordinator still has incomplete publication work. Its initial Hit bypasses the
required distinct zero-prefix token. Its provisional encoder duplicates the target witness
instead of preserving the complete attained query/target pair and combined correspondence
budget. Default/foreign prefix lifetime controls and the complete Normalize/multiply/add
operation enclosure also need review. Returning Hit in ten diagnostic rows is not acceptance
of these unfinished proofs. The nine named tests were drafted before implementation, but
only the missing-API red and the diagnostic ran. Their unexecuted assertions are not evidence.

The first-event endpoint-selection policy is also unfinished. The prototype always loans
its t=0 certificate, without comparing it with t=1 to enforce SS-R14's nearest-endpoint
selection. Its cell totals therefore describe this attempt and are not a lower bound on
what a fully compliant coordinator can achieve. This policy still needs implementation,
alongside reusable temporal refinement and the complete publication proofs.

The owning orchestrator must revisit the two measured bottlenecks before another attempt.
Do not continue from the archived encoder as if it were complete. Keep the unchanged caps,
one planar Sweep route, original raw identity and accepted certificate producers. Complete
the mandatory fixture matrix before retiring the old producers in the final implementation
commit. No subagent/reviewer, push, merge, tag, branch change, release, consumer launch,
load run, test loop or full suite occurred. This checkpoint contains documentation only.


## Task 2b.3 continuation under SS-R16

The required repeated diagnostic remains red, 23 Hit rows and one refusal. The rim row
with Margin 0.001 and approach 2^-12 still exhausts 256 cells, now with 1756 features.
The continuation stops under SS-R16. No replacement or producer retirement is accepted.
The implementation and instrumentation are archived and production/test files restored.

The attempt retains temporal simplex labels, inner certificates, attained events and
original support proofs across bracket requests. It first searches to the initial plane
zero, uses exact represented coefficients under 2a source authentication, proposes retained
exact feature normals before generic directions and reserves operational placement
enclosures for final publication. Face first events now land at contact instead of near
0.95. All 2^-20 and tangent rows publish in the diagnostic. These are diagnostic successes,
not acceptance of the unfinished witness encoder and initial zero-prefix obligations.

The unchanged 24-row fixture construction and the before table are recorded above.
The same diagnostic was run once before and once after corrections. Both commands exit 1,
each with one executed and failed test and zero skipped. Both Release builds have no
compiler warnings or errors. No green full class or Common regression result is claimed.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests.StageDiagnostic' --logger 'console;verbosity=detailed'
```

The after table uses the same columns as the first attempt. C/F is cells/features.
The first-event column includes endpoint selection, temporal/static attainment, original
revalidation and any penetration normal. All 24 rows retain one primitive admission and
restore polynomial Used to zero. These are same-run observations, never absolute test pins.

| Family | Margin | Approach | Published | Initial C/F | Plane C/F | First event C/F | Refinement C/F | Total C/F | Exhausted |
| --- | ---: | --- | --- | --- | --- | --- | --- | --- | --- |
| face | 0 | 2^-4 | True | 3/20 | 2/12 | 48/149 | 12/26 | 65/207 | False |
| face | 0 | 2^-12 | True | 3/20 | 2/12 | 48/149 | 12/26 | 65/207 | False |
| face | 0 | 2^-20 | True | 3/20 | 2/12 | 48/149 | 12/26 | 65/207 | False |
| face | 0 | tangent | True | 32/51 | 2/8 | 49/104 | 50/137 | 133/300 | False |
| rim | 0 | 2^-4 | True | 32/51 | 2/8 | 26/74 | 39/114 | 99/247 | False |
| rim | 0 | 2^-12 | True | 32/51 | 2/8 | 49/109 | 72/211 | 155/379 | False |
| rim | 0 | 2^-20 | True | 32/51 | 2/8 | 49/109 | 50/137 | 133/305 | False |
| rim | 0 | tangent | True | 32/51 | 2/8 | 49/109 | 50/137 | 133/305 | False |
| apex | 0 | 2^-4 | True | 7/27 | 2/12 | 19/120 | 12/26 | 40/185 | False |
| apex | 0 | 2^-12 | True | 7/27 | 2/12 | 19/120 | 12/26 | 40/185 | False |
| apex | 0 | 2^-20 | True | 7/27 | 2/12 | 19/120 | 12/26 | 40/185 | False |
| apex | 0 | tangent | True | 16/27 | 2/8 | 31/75 | 10/22 | 59/132 | False |
| face | 0.001 | 2^-4 | True | 3/20 | 2/12 | 23/122 | 12/26 | 40/180 | False |
| face | 0.001 | 2^-12 | True | 3/20 | 2/12 | 52/138 | 12/26 | 69/196 | False |
| face | 0.001 | 2^-20 | True | 3/20 | 2/12 | 52/138 | 12/26 | 69/196 | False |
| face | 0.001 | tangent | True | 32/51 | 2/8 | 46/95 | 73/225 | 153/379 | False |
| rim | 0.001 | 2^-4 | True | 32/51 | 2/8 | 76/377 | 106/1103 | 216/1539 | False |
| rim | 0.001 | 2^-12 | False | 32/51 | 2/8 | 121/965 | 101/732 | 256/1756 | True |
| rim | 0.001 | 2^-20 | True | 32/51 | 2/8 | 141/1927 | 66/183 | 241/2169 | False |
| rim | 0.001 | tangent | True | 32/51 | 2/8 | 46/100 | 73/225 | 153/384 | False |
| apex | 0.001 | 2^-4 | True | 7/27 | 2/12 | 22/119 | 12/26 | 43/184 | False |
| apex | 0.001 | 2^-12 | True | 7/27 | 2/12 | 46/135 | 12/26 | 67/200 | False |
| apex | 0.001 | 2^-20 | True | 7/27 | 2/12 | 46/135 | 12/26 | 67/200 | False |
| apex | 0.001 | tangent | True | 16/27 | 2/8 | 32/79 | 15/47 | 65/161 | False |

The remaining row spends 121 cells on its first event, then 101 on refinement. Those
refinement cells split into 22 interval, 42 active-feature, 37 event and zero encoding.
Eight successful contractions leave [0.49978311880145604,0.5017430526006774], about
0.367488 mm of nominal path width, before a further temporal event exhausts.
The mathematical map residual is zero. The row fails before encoding. This does not prove
that no 1 mm result exists, only that this coordinator did not close its internal 0.25 mm
bracket allocation inside the unchanged account. No cap, ceiling, tolerance or fixture
was changed and no further algorithm run follows this stop.

All four r0/r1 sweep producers, their existing regressions and the root-specific encoder
remain unchanged. No six-feature/three-target-stratum completion, lowered-budget controls,
duck/cow or represented-pose expansion is accepted. The Common selections, SS-R2 capsule
families, roof/footprint regressions and seven degree-1 navigation baselines are NOT RUN
after the mandatory stop. Capsule/support APIs and arithmetic, InstalledSweepLeaf, raw
identity contracts, Movement and baseline sources have no diff from the starting base.

The continuation report in the existing SDD handoff directory includes both stage tables,
the complete refinement table, self-review and exact migration inventory disposition.
The attempted production/test diff is task-2b.3-r16-attempt.diff, 45,159 bytes,
SHA-256 c2386d0b5e603d32ed86e38058caf9caf627166c2070f120bee5b38721a4bcf3.
Its archive/restoration and applicability checks exit 0. The prior archive is unchanged.
Only this evidence record is committed. No partial replacement is committed under the
final implementation subject. Integration, pushes and releases remain with the orchestrator.


## Task 2b.3 continuation under SS-R17

Status NEEDS_CONTEXT. Starting base 35c3ac37f. The starting guard and application of
the SS-R16 archive pass. SS-R17 removes the fixed 0.25 mm width precondition and tries
the exact combined 1 mm encoded-error gate at every bracket. The attempted encoder
retains both original-member witnesses, includes final normal normalization/encoding,
outward distance and the final placement-operation residual, and separately proves
the skin-shortened actual placement. No cap, tolerance or accepted shape changes.

The restored stage red exits 1, one failed test and 23/24 Hits. The added narrow-bracket
large-witness-error control first fails compilation on the intended missing typed
encoder door. After implementation, the stage diagnostic exits 0, one passed test
and 24/24 Hits. The formerly failing rim/0.001/2^-12 row uses 203/1178 C/F. Counts are
observations, never absolute test pins. Every row has one admission and restores the
polynomial arena. The complete measured stage table follows.

| Family | Margin | Approach | Hit | Initial C/F | Plane C/F | First event C/F | Refinement C/F | Total C/F | Exhausted |
| --- | ---: | --- | --- | --- | --- | --- | --- | --- | --- |
| face | 0 | 2^-4 | True | 3/20 | 2/12 | 48/149 | 10/49 | 63/230 | False |
| face | 0 | 2^-12 | True | 3/20 | 2/12 | 48/149 | 10/49 | 63/230 | False |
| face | 0 | 2^-20 | True | 3/20 | 2/12 | 48/149 | 10/49 | 63/230 | False |
| face | 0 | tangent | True | 32/51 | 2/8 | 49/104 | 32/108 | 115/271 | False |
| rim | 0 | 2^-4 | True | 32/51 | 2/8 | 26/74 | 29/111 | 89/244 | False |
| rim | 0 | 2^-12 | True | 32/51 | 2/8 | 49/109 | 48/154 | 131/322 | False |
| rim | 0 | 2^-20 | True | 32/51 | 2/8 | 49/109 | 32/108 | 115/276 | False |
| rim | 0 | tangent | True | 32/51 | 2/8 | 49/109 | 32/108 | 115/276 | False |
| apex | 0 | 2^-4 | True | 7/27 | 2/12 | 19/120 | 10/49 | 38/208 | False |
| apex | 0 | 2^-12 | True | 7/27 | 2/12 | 19/120 | 10/49 | 38/208 | False |
| apex | 0 | 2^-20 | True | 7/27 | 2/12 | 19/120 | 10/49 | 38/208 | False |
| apex | 0 | tangent | True | 16/27 | 2/8 | 31/75 | 8/39 | 57/149 | False |
| face | 0.001 | 2^-4 | True | 3/20 | 2/12 | 23/122 | 10/49 | 38/203 | False |
| face | 0.001 | 2^-12 | True | 3/20 | 2/12 | 52/138 | 10/49 | 67/219 | False |
| face | 0.001 | 2^-20 | True | 3/20 | 2/12 | 52/138 | 10/49 | 67/219 | False |
| face | 0.001 | tangent | True | 32/51 | 2/8 | 46/95 | 53/184 | 133/338 | False |
| rim | 0.001 | 2^-4 | True | 32/51 | 2/8 | 76/377 | 104/1123 | 214/1559 | False |
| rim | 0.001 | 2^-12 | True | 32/51 | 2/8 | 121/965 | 48/154 | 203/1178 | False |
| rim | 0.001 | 2^-20 | True | 32/51 | 2/8 | 141/1927 | 48/154 | 223/2140 | False |
| rim | 0.001 | tangent | True | 32/51 | 2/8 | 46/100 | 53/184 | 133/343 | False |
| apex | 0.001 | 2^-4 | True | 7/27 | 2/12 | 22/119 | 10/49 | 41/207 | False |
| apex | 0.001 | 2^-12 | True | 7/27 | 2/12 | 46/135 | 10/49 | 65/223 | False |
| apex | 0.001 | 2^-20 | True | 7/27 | 2/12 | 46/135 | 10/49 | 65/223 | False |
| apex | 0.001 | tangent | True | 16/27 | 2/8 | 32/79 | 8/39 | 58/153 | False |

The expanded class exits 1, 28 executed, 20 passed and 8 failed. Three failures expose
mandatory cap exhaustion, two arise from invalid exclusive output-stratum assertions
on shared contacts, and three arise from incorrectly consuming work before primitive
admission. Same-run lowered-cell and lowered-feature portions pass. The nine named
brief tests, prefix lifetime, initial overlap, large-coordinate refusal and the
narrow-bracket large-witness-error control pass. This is not a green full class.

One bounded diagnostic records the whole required six-feature by three-target-stratum
matrix, with both margins, without stopping at its first failed row. It exits 1, six
executed, three passed and three failed. All 18 zero-margin rows pass. At Margin 0.001,
11 pass and seven exhaust. Successful rows pass exact/outward combined-error checks.
The seven failures trigger the mandatory stop before Common verification.

| Requested pair | Initial C/F | Plane C/F | First event C/F | Refinement C/F | Total C/F | Last fraction bracket |
| --- | --- | --- | --- | --- | --- | --- |
| LowerCone/Edge | 117/171 | 2/10 | 133/1247 | 4/23 | 256/1451 | 0.9321966988892079,0.9943431454818217 |
| Shoulder/Edge | 100/159 | 2/10 | 124/3927 | not reached | 226/4096 | 0,1 |
| Rim/Edge | 102/165 | 2/10 | 128/3921 | not reached | 232/4096 | 0,1 |
| Shoulder/Face | 3/20 | 2/12 | 153/1190 | 98/1326 | 256/2548 | 0.99609375,1 |
| Rim/Face | 7/24 | 2/12 | 153/1164 | 94/1313 | 256/2513 | 0.9921875,1 |
| Shoulder/Vertex | 18/29 | 2/8 | 99/4059 | not reached | 119/4096 | 0,1 |
| Rim/Vertex | 20/31 | 2/8 | 103/4057 | not reached | 125/4096 | 0,1 |

For Shoulder/Face, LowerCone/Edge and Rim/Face the last combined outward totals are
0.0010920097478398564, 0.010986213766887395 and 0.0013812641935392915 m, respectively.
Their complete encoded widths are 0.0010918378829956055, 0.01098605990409851 and
0.0013810843229293823 m, with both witness residuals, normal and placement error added.
All three also fail the exact skin-before-prefix check. The other four exhaust feature
work before the first event, so no attained normal or combined encoder budget exists.
The detailed continuation report records each component, the complete 36-row matrix,
commands, exit codes, failed-test interpretation and self-review. No further algorithm
correction follows this required stop.

Exact commands run, each synchronously through build-slot without heavy.

```bash
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests.StageDiagnostic' --logger 'console;verbosity=detailed'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests.StageDiagnostic' --logger 'console;verbosity=detailed'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests' --logger 'console;verbosity=detailed'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConvexBracketSweepTests.CompletionStopDiagnostic' --logger 'console;verbosity=detailed'
```

The exits in order are 1, 1, 0, 1 and 1. Logs are under
/tmp/khaozengine-short-shell/ss-r17, named stage-red.log, class-red.log, stage-green.log,
class-first.log and completion-stop.log. All processes have finished.

All four r0/r1 producers, old root-specific encoder entries, kernel/dispatch and migrated
legacy tests are restored to the starting base. Shared arithmetic policies, capsule
and support APIs, InstalledSweepLeaf, Movement and baseline sources are unchanged.
The three Common selections, SS-R2 capsule families, roof/footprint protections and
seven degree-1 navigation baselines are NOT RUN after the stop. No green retirement,
full represented-pose or duck/cow completion is accepted.

The archived attempt is task-2b.3-r17-attempt.diff in the existing SDD handoff directory,
99,498 bytes, SHA-256 661ec3fb8466de0c67adf8369ffde8de81037375ab7af22c47f7502780c38b3c.
Its guarded archive/restoration and git apply --check exit 0. Only this proof record
is committed. No instrumentation or partial implementation is committed under the final
subject. No subagent, reviewer, push, merge, fetch, branch change, tag, version bump,
release, consumer launch, load run, loop or full suite occurred. The orchestrator must
review the seven measured failures before another implementation run.
