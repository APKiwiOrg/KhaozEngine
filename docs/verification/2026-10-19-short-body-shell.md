# Short-body shell verification

Task 1, Task 2a and partial Task 2b of the [short-body shell plan](../superpowers/plans/2026-10-19-short-body-shell.md).
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
