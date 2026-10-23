# Short-body shell verification

Task 1 and partial Task 2 of the [short-body shell plan](../superpowers/plans/2026-10-19-short-body-shell.md).
Requirements are [SB-2 to SB-4 and acceptance group A](../design/CONTACT-SHORT-BODY-SHELL-2026-10-19.md).
This record covers value contracts, the independent shape oracle and partial root infrastructure. Backend query certification,
controller adoption, navigation, the full suite and release acceptance remain later tasks.

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

Task 2 remains incomplete. This change adds query values, rounded support proposals, sticky
work accounts, query-private scratch and bounded exact polynomial root infrastructure.
It does not implement ConvexFeatureKernel, ConvexPlanarFeatures or ConvexLeafEvidence.
It must not be used as evidence for any cone-cylinder sweep, distance or penetration query.

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
duck and cow. This checks proposal encoding only. The installed-target snapshots, Entry,
NormalConeContains and MemberTranslationEnclosedBy oracle extensions are absent. There is no
installed-target comparison, attained planar witness, normal-cone certificate or minimum member
translation proof. SS-R6 is recorded in the owner ledger. Its distinct-coincident-leaf and
mismatched-publication controls require the missing kernel and dispatch implementation.

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
