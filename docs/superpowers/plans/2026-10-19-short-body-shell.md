# Certified Short-Body Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the engine capability in #1344 with a certified full-width short-body shell that preserves head height, 0.4 m step reach and every existing movement protection.

**Architecture:** Physics adds an optional value-input moving-convex capability. Bepu certifies the actual continuous cone-cylinder and represented installed targets before publishing operational sweep, distance, contact or penetration results. Locomotion selects a shell descriptor from geometry, keeping its original capsule branch, and Movement persists the descriptor in compatible KENB format 3 and proof identities.

**Tech Stack:** C#/.NET 10, System.Numerics, BepuPhysics 2.4.0, bounded exact/interval geometry, xUnit headless Game.Tests and Movement.Tests plus the existing physics integration test projects.

**Spec:** `docs/design/CONTACT-SHORT-BODY-SHELL-2026-10-19.md`, SB-1 to SB-12 and acceptance A to F. Read the owning checkout's `.superpowers/sdd/shell-1344/research.md` and `progress.md` in full when present. The spec retains all operative evidence, SB-O1/SB-O2 and eight proof obligations. It is the implementation contract.

## Global Constraints

- Preserve the Euclidean 0.001 m arrival ball, roof and footprint protections and raw layer identity.
- Raw physics identity remains `(StaticHandle, ElementId)`. Coincident members never coalesce.
- SS-R5: `ConvexSweepResult` carries `ConvexContactIdentity Identity` alongside `SweepHit? Hit`. Clear and Unresolved require zero identity. Hit retains the exact raw Dynamic, BodyHandle, ChildIndex and FeatureId, including coincident members.
- No tolerance, skin, band, epsilon, reach or existing work/storage ceiling may be widened or added to obtain acceptance.
- Preserve the independent 0.001 m placement offset, 0.0001 m contact/join band and 0.0005 m HeightError ceiling.
- Preserve 256 raw support members, 512 support witness slots and the cumulative 1,048,576-event curved Find ceiling.
- Preserve fitting capsule geometry, queries, arithmetic and format 1 legacy bytes. Preserve format 2 bytes when no short-body contact profile is present, at the same engine identity.
- Use deterministic counters, not timing. No timing, load, stress, allocation benchmark, repeated test loop, consumer launch or world bake is authorized.
- Ride staged 21.7.0 if it remains untagged at implementation finish. Otherwise select the next free patch above current main, with its CHANGELOG entry and guarded declarations in the same commit.
- Implementation workers stop at verified commits. Integration, push, packaging, release, tags and Grimhollow adoption belong to the owning orchestrator and their separate gates.
- Work only in `/Users/antonio/KhaozEngine/.worktrees/short-body-shell` on `feature/short-body-shell`, from `909fc59a8` plus the accepted design commit. Preserve unrelated work.
- Before each task require that exact git toplevel, branch and a clean status. One writer owns shared files at a time. Stop on unexplained edits.
- Every build or test runs synchronously through `build-slot --label short-shell-design -- <command>`, one at a time. Add `--heavy` for broad selections, the final full build/tests and format. Never use timing flags or shell test loops.
- Intermediate tasks run only their named focused selections. Full Release solution build, full Game/Movement/Physics coverage and format verification run once in Task 12.
- Stage explicit task-owned paths, inspect the staged diff and run staged guards before every commit. No attribution trailers. Scratch output belongs under `/tmp/khaozengine-short-shell/`.

## Review Focus

- A short shell's upper taper can miss a roof away from its axis. The full head disc must catch it before and after an anchor lift. Task 8 adds `ShortShellHeadDiscCatchesOffAxisRoofAfterLift`.
- A tiny cone span can round its midpoint to an endpoint, despite finite positive inputs. Refuse it before support-map division. Task 8 adds `RoundedMidpointCannotCollapseTheLowerCone`.
- A valid GJK miss can concern an inward proxy or the wrong transform. No such miss authorizes Clear for the continuous represented shell. Task 3 adds `OperationalProxyMissCannotPublishFullShellClear`.
- A mixed file can lose resting feet when a version check only recognizes format 2. Format 3 must retain every contact profile's arrays and legacy payload. Task 10 adds `FormatThreeMixedProfilesRetainAllRestingFeet`.
- Two raw slots can reach identical resting anchors while a Hold key omits the shell. Keep raw identity and all shell bits independently. Task 11 adds `CoincidentAnchorsAndDifferentShellsCannotShareHoldEvidence`.

---

## File structure and proof conventions

Physics owns the new shape value in `ConvexQueryShape.cs`, result values in `ConvexQueryResult.cs`
and optional interface in `IPhysicsConvexQueries.cs`. It adds no dependency or persistent
PhysicsShape subtype. Bepu owns continuous support evaluation, operational proposals, exact
feature certificates, finite target traversal and atomic world publication in the new files
listed by each task. Reuse InstalledSweepGeometry, InstalledPoseOperator and represented
geometry readers. Do not duplicate installation state or substitute authored coordinates.

New Bepu query code separates operational proposals (`ConvexQueryOperations.cs`), geometric
proof (`ConvexFeatureKernel.cs` and feature-family files), traversal (`ConvexQueryTraversal.cs`)
and world/view lifetime (`BepuPhysicsWorld.ConvexQueries.cs`). Keep these cohesive concerns
separate without splitting existing files at arbitrary lines. New code respects KESIZE. No
baseline growth, exemption, new dependency edge or increased capacity is preapproved.

Locomotion owns descriptor/factory/clearance in `Contacts/ContactShellDescription.cs`,
`Contacts/ShortBodyShellGeometry.cs` and `Contacts/ContactShellClearance.cs`. A new
`MovementShellQuery.cs` carries the real query and an independent scope envelope. The envelope
is never cast back to a capsule to make a collision decision. Existing context and resolver
files gain kind dispatch, retaining original capsule statements and their rounding order.

Movement owns shell encoding in `NavBakeIdentity.Shell.cs`, format selection in the existing
identity/writer/reader and immutable descriptors in NavProfileIdentity and reuse identities.
No game controller wiring, release operation or #1389 navigation capability enters these tasks.

Test helpers live in their named fixture/oracle files, never production code. The independent
oracle uses rational arithmetic and outward algebraic/trigonometric intervals. It cannot
call production support mapping, closest-feature selection or root isolation to obtain its
expected answer. Closed cone/cylinder equations and installed-shape snapshots define expected
facts. Numeric snippets below use locals from the named fixture, not undefined production APIs.

At each commit, stage its explicit changed files and the verification record, inspect the full
staged diff and require exit 0 from:

```bash
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
git diff --cached --check
```

New behavior tests run red before implementation. A missing new typed symbol can be the first
red only when that exact symbol is the intended change, never an unrelated restore/compiler
failure. Existing protection controls may pass immediately and must not be made artificially
red. Record reds, greens, exact command exits, executed counts and deterministic work in
`docs/verification/2026-10-19-short-body-shell.md`. Do not retry an unchanged failure. Any
required proper correction receives its own deterministic regression in the owning task.

### Task 1: Define the continuous query and independent shape oracle (SB-2 to SB-4, A)

**Files:**
- Create: `KhaozEngine.Physics/ConvexQueryShape.cs`, `KhaozEngine.Physics/ConvexQueryResult.cs`, `KhaozEngine.Physics/IPhysicsConvexQueries.cs`
- Create: `KhaozEngine.Game.Tests/Physics/ConvexQueryContractTests.cs`, `KhaozEngine.Game.Tests/Physics/ShortBodyShellOracle.cs`
- Create: `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Produces `ConvexQueryKind.ConeCylinder = 1` and immutable `ConvexQueryShape(ConvexQueryKind Kind, float Radius, float LowerY, float RingY, float UpperY, float Margin = 0)` with `WithMargin(float margin) -> ConvexQueryShape`.
- Produces `ConvexSweepStatus` values Unresolved = 0, Clear = 1, Hit = 2, and `ConvexContactIdentity(bool Dynamic, int BodyHandle, int ChildIndex, int FeatureId)` preserving raw provenance.
- Produces `ConvexSweepResult(Status, ClearThroughDistance, ImpactDistance, CertifiedErrorMetres, Hit, Identity)` with field types `ConvexSweepStatus`, `float`, `float?`, `float`, `SweepHit?`, `ConvexContactIdentity`. Unresolved has zero/null fields, Clear covers the full request and has no hit, and both require zero Identity. Hit has ordered bounds, its proved operational hit and the exact raw target Identity under SS-R5. Existing SweepHit and capsule APIs are unchanged.
- Produces `ConvexDistanceResult(bool Complete, bool HasTarget, float LowerDistance, float UpperDistance, Vector3 QueryWitness, Vector3 TargetWitness, Vector3 Normal, ConvexContactIdentity Identity)`. Empty selection is Complete with HasTarget false and zero remaining fields. Separated witnesses enclose nonnegative nearest distance. Overlap distance is zero and penetration is a separate query.
- Produces `ConvexContact(Vector3 Normal, float LowerSeparation, float UpperSeparation, ConvexContactIdentity Identity)`, `ConvexContactResult(bool Complete, int Written, int RequiredCapacity, float CertifiedErrorMetres)` and `ConvexPenetrationResult(bool Complete, bool Overlapping, Vector3 Translation, float LowerDepth, float UpperDepth, ConvexContactIdentity Identity)`.
- Produces `IPhysicsConvexQueries.SweepConvexCertified(in ConvexQueryShape shape, Pose pose, Vector3 displacement, QueryFilter filter = default) -> ConvexSweepResult`, `DistanceConvex(in ConvexQueryShape shape, Pose pose, QueryFilter filter = default) -> ConvexDistanceResult`, `QueryConvexContacts(in ConvexQueryShape shape, Pose pose, Span<ConvexContact> destination, QueryFilter filter = default) -> ConvexContactResult`, and `ComputeConvexPenetration(in ConvexQueryShape shape, Pose pose, QueryFilter filter = default) -> ConvexPenetrationResult`.
- Produces test-only `ShortBodyShellOracle.Clearance(float radius, float lower, float ring, float upper, float angle, float margin) -> (double Lower, double Upper)` and `SupportBounds(in ConvexQueryShape shape, Vector3 direction) -> (double Lower, double Upper)`. Clearance includes the supplied Minkowski margin, so use margin 0.001 for surplus above the unchanged skin.

- [ ] **Step 1: Write the failing value-contract and independent geometry tests.**

Add `ConeCylinderValuePreservesAllRepresentedBits`, `InvalidOrCollapsedShapeRejects`,
`ContinuousCircleSupportEnclosesEveryAzimuth` and `RequiredBodyPlaneSurplusIsProved`.
Enumerate signed-zero Margin, nonfinite fields, unknown kind, collapsed cone and R <= 0.
Use exact generators for support extrema, including axial directions and feature ties.

```csharp
Assert.Equal(BitConverter.SingleToUInt32Bits(0.45f), BitConverter.SingleToUInt32Bits(duck.RingY));
Assert.True(duckSurplus.Lower >= 0.15147);
Assert.True(cowSurplus.Lower >= 0.09966);
Assert.Equal(0.001f, duck.WithMargin(0.001f).Margin);
Assert.Throws<ArgumentException>(() =>
    new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 0.4f, 0.4f, 0.5f));
```

- [ ] **Step 2: Run the contract red.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ConvexQueryContractTests'`.
Expected: missing new query types or failed value assertions. Record the exact intended red.

- [ ] **Step 3: Implement the value/interface contract and the independent oracle.**

Validate finite ordered dimensions and Margin exactly 0 or 0.001, with canonical base +0.
Default results refuse without usable fields. Enforce bracket and complete-output invariants.
The oracle evaluates the spec's continuous generators with rational/outward root and trig
bounds over the represented production angle, not nominal degrees or sampled directions.
No world backend or locomotion behavior changes in this task.

- [ ] **Step 4: Run the same focused selection green.**

Expected: exit 0, both surplus lower bounds proved and every invalid value rejected. Record
nominal values separately from represented enclosures. Never turn the displayed bounds into
an admission epsilon.

- [ ] **Step 5: Run staged guards and commit explicit Task 1 paths.**

Commit `feat(physics): define continuous short-shell query contracts`.

### Task 2: Certify continuous feature distance and planar targets (SB-2, SB-5, A/B)

SS-R7 splits the remaining construction into 2a (planar Distance, local feature evidence,
planar dispatch identity and distance/normal-cone oracles), 2b (Sweep roots) and 2c (Contacts,
Penetration and bounded GJK/EPA proposals). Each unit receives its own review. Unfinished
operations return false with default evidence. The original Task 2 obligations below remain
required across those units.

**Files:**
- Create: `KhaozEngine.Physics.Bepu/ConvexQueryPolicy.cs`, `KhaozEngine.Physics.Bepu/ConvexQueryScratch.cs`, `KhaozEngine.Physics.Bepu/ConvexQueryOperations.cs`
- Create: `KhaozEngine.Physics.Bepu/ConvexFeatureKernel.cs`, `KhaozEngine.Physics.Bepu/ConvexFeatureRegimes.cs`, `KhaozEngine.Physics.Bepu/ConvexPlanarFeatures.cs`, `KhaozEngine.Physics.Bepu/ConvexAlgebraicRoots.cs`
- Create: `KhaozEngine.Game.Tests/Physics/ConvexFeatureKernelTests.cs`, `KhaozEngine.Game.Tests/Physics/ConvexQueryFixture.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/ShortBodyShellOracle.cs`, `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes Task 1's exact shape and result values, including `ConvexSweepResult.Identity` under SS-R5, existing InstalledSweepLeaf and installed represented geometry readers.
- Produces internal `ConvexQueryOperation` values Sweep, Distance, Contacts, Penetration and `ConvexQueryRequest(ConvexQueryShape Shape, Pose Pose, Vector3 Displacement, ConvexQueryOperation Operation)`.
- Produces `ConvexQueryWork` with sticky descendant/cell/feature accounts under SB-7 and `ConvexQueryScratch` owning bounded exact arithmetic, interval cells and proposal simplex storage.
- Produces `ConvexFeatureKernel.TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence) -> bool`. Evidence owns ordered brackets, attained witnesses, a normal-cone certificate and stable local face/edge/vertex provenance under SS-R6. The exact dispatch owns raw ConvexContactIdentity and attaches it to that dispatch's evidence. InstalledSweepLeaf is unchanged. False yields default evidence.
- Produces `ConvexQueryOperations.Support(in ConvexQueryShape shape, Vector3 direction) -> Vector3` for operational proposals only. Its arithmetic graph is enclosed by the kernel, not mistaken for exact circle support.
- Produces `ConvexAlgebraicRoots.TryIsolate(in ConvexFeatureRegime regime, double from, double to, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexRootBracket first) -> bool`. Regime owns exact polynomial coefficients, original radical signs and closed feature-domain inequalities. First owns the enclosing first feasible root or a proved empty interval. No uncertain sign becomes an empty interval.
- Produces test-only ConvexOracleTarget snapshots read independently from installed Bepu primitives/topology/poses and `ShortBodyShellOracle.Entry(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target, Vector3 displacement) -> ConvexOracleEvent`, where ConvexOracleEvent holds Status and outward Lower/Upper distances. Also produces `NormalConeContains(in ConvexQueryShape shape, Vector3 queryWitness, Vector3 queryOutwardNormal) -> bool` and `MemberTranslationEnclosedBy(ConvexOracleTarget target, in ConvexPenetrationResult result) -> bool` on ShortBodyShellOracle. Operational contact/recovery normals point away from the target, so pass their negative to the query-normal-cone oracle.

- [ ] **Step 1: Write planar feature reds using the oracle before the solver.**

Add `ConeSideHeadDiscAndRimsHaveAttainedDistanceWitnesses`,
`CircleSupportIsNeverReplacedByInscribedVertices`, `ClosedDoubleRootIsContact`,
`SharpShoulderNormalBelongsToTheNormalCone` and `PlanarPenetrationHasMinimumMemberTranslation`.
Use duck and cow against finite box/hull faces, edges and vertices, including a thin blocker
between any proposed polygon vertices. Assert exact dyadic tangency and both adjacent floats.

```csharp
Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper);
Assert.True(ShortBodyShellOracle.NormalConeContains(shape, evidence.QueryWitness, -evidence.Normal));
Assert.True(evidence.HasAttainedWitness);
Assert.False(uncertainEvidence.IsComplete);
```

- [ ] **Step 2: Run the kernel red.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ConvexFeatureKernelTests'`.
Expected: missing kernel or failed continuous-volume assertions.

- [ ] **Step 3: Implement support proposals and finite closed feature certification.**

Evaluate the three support generators from SB-2, with isotropic Margin added only when
requested. Certify clear intervals with dual separating support planes. Enumerate apex,
lower-cone, shoulder circle, cylindrical side, head disc and rim against finite target
face/edge/vertex regimes. Isolate first feasible contact roots on the original [0,1] path
using square-free polynomial reduction, Sturm root counts and dyadic interval isolation,
then verify every original radical sign and closed domain predicate. Repeated roots are
isolated from the square-free part, never detected by sign crossing alone. All integer
intermediates obey the existing 4096-bit ceiling. Include
zero-length paths, feature boundaries, interior stationary points and repeated roots.
Distance/penetration witnesses must satisfy feature-domain and KKT normal conditions.
GJK/EPA proposes candidates inside the same bounded account. Failed convergence cannot
publish evidence. Outward encode the complete bracket, not only its midpoint.

- [ ] **Step 4: Run the same selection green.**

Expected: exit 0, actual circular volume proved and uncertain/overflow cases refused.
Record feature/cell maxima for these finite fixtures, without elapsed time or throughput.

- [ ] **Step 5: Run guards and commit explicit Task 2 paths.**

Commit `feat(physics): certify continuous cone-cylinder feature distance`.

### Task 3: Publish certified world/view queries with operational correspondence (SB-4 to SB-7, B/C)

**Files:**
- Create: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.ConvexQueries.cs`, `KhaozEngine.Physics.Bepu/ConvexQueryTraversal.cs`, `KhaozEngine.Physics.Bepu/ConvexQueryBounds.cs`
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryView.cs`, `KhaozEngine.Physics.Bepu/QueryScratch.cs`
- Create: `KhaozEngine.Game.Tests/Physics/CertifiedConvexWorldTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedConvexCorrespondenceTests.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/ConvexQueryFixture.cs`, `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes Tasks 1/2, including `ConvexSweepResult.Identity` under SS-R5. Produces IPhysicsConvexQueries on BepuPhysicsWorld and its exact selected QueryView.
- Produces `ConvexQueryBounds.TryCreate(in ConvexQueryRequest request, out Vector3 minimum, out Vector3 maximum) -> bool` with a proof of the operational aperture and installed-tree pruning pad.
- World entry points match Task 1 exactly, publishing the exact raw Identity on Hit and zero Identity on Clear or Unresolved under SS-R5. Owner cores take immutable StaticQueryExclusions and the exact receiver, using EnterQuery and its current read interval. Unsupported target families refuse until their following tasks land.
- Produces test-only `ConvexQueryFixture.SkinShortenedAdvanceIsInside(in ConvexSweepResult result, Vector3 displacement) -> bool`, consuming the SS-R5 result with Identity and evaluating the spec's original displacement length, operational hit distance and certified lower bound with outward exact arithmetic.

- [ ] **Step 1: Write world capability, agreement and complete-publication reds.**

Add `WorldAndSelectedViewExposeCertifiedConvexQueries`,
`OperationalProxyMissCannotPublishFullShellClear`, `OperationalHitRequiresCertifiedAdvance`,
`RootAndCentroidWrapperUseInstalledRepresentedPose` and `LaterUnresolvedLeafCancelsEarlierHit`.
Test all four operations, empty selection, initial overlap, exact tangency, nearest raw-ID
ties, rotated/non-unit represented transforms and view exclusions. A test-only proposal seam
injects miss/hit/wrong-normal disagreement, without a production permissive switch.

```csharp
Assert.Equal(ConvexSweepStatus.Unresolved, mismatched.Status);
Assert.Null(mismatched.Hit);
Assert.Equal(ConvexSweepStatus.Hit, actual.Status);
Assert.True(ConvexQueryFixture.SkinShortenedAdvanceIsInside(actual, displacement));
Assert.Equal(expectedRawIdentity, distance.Identity);
```

- [ ] **Step 2: Run the world/correspondence red selection.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CertifiedConvexWorldTests|FullyQualifiedName~CertifiedConvexCorrespondenceTests'`.
Expected: absent capability or disagreement/publication assertion failure.

- [ ] **Step 3: Implement world aggregation and independent operation certification.**

Authenticate selected receiver, view and installation first. Preflight and enumerate the
proved swept aperture under the same interval. Bind exact query fields to both proposal and
certificate, including circle/Margin and all represented transformations. Aggregate earliest
Hit, nearest distance and all raw contacts only after every relevant leaf completes. Keep
caller output untouched until all checks and capacity sizing succeed. Complete empty
selection differs from unavailable evidence. Nonfinite, unsupported and disagreement paths
return default refusal. Do not change any capsule entry point or fast path.

- [ ] **Step 4: Run the same focused selection green.**

Expected: exit 0 for identity/rotated boxes and hulls, every injected mismatch refused and
no selected-view leak. Record the actual represented enclosure and operation graph cases.

- [ ] **Step 5: Run guards and commit explicit Task 3 paths.**

Commit `feat(physics): publish certified moving-convex world queries`.

### Task 4: Complete curved target families without proxy shortcuts (SB-5/SB-6, B)

**Files:**
- Create: `KhaozEngine.Physics.Bepu/ConvexCurvedFeatures.cs`
- Modify: `KhaozEngine.Physics.Bepu/ConvexFeatureKernel.cs`, `KhaozEngine.Physics.Bepu/ConvexFeatureRegimes.cs`
- Create: `KhaozEngine.Game.Tests/Physics/CertifiedConvexCurvedTests.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/ShortBodyShellOracle.cs`, `KhaozEngine.Game.Tests/Physics/ConvexQueryFixture.cs`, `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes Task 2's one TryEvaluate contract and Task 3's unchanged public entry points, including the exact raw `ConvexSweepResult.Identity` under SS-R5.
- Produces `ConvexCurvedFeatures.TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence) -> bool` for sphere, capsule and cylinder targets.

- [ ] **Step 1: Write the curved-family matrix red.**

Add `EveryCurvedFamilyCompletesEveryConvexOperation` with duck/cow, base/grown shapes,
identity/tilted targets and separated/hit/touch/penetration cases. Cover sphere point extrema,
capsule endpoint/interior regimes, cylinder side, cap disc and rim circle. Include two curved
features meeting the shoulder, tiny glancing roots, cylinder-axis symmetry and closed tangency.

```csharp
Assert.True(distance.Complete);
Assert.True(contacts.Complete);
Assert.True(penetration.Complete);
// This row is a known first-contact case. Clear rows assert full-path coverage instead.
Assert.Equal(ConvexSweepStatus.Hit, oracle.Status);
Assert.Equal(oracle.Status, sweep.Status);
Assert.True(sweep.ClearThroughDistance <= oracle.Lower);
Assert.True(sweep.ImpactDistance is float upper && upper >= oracle.Upper);
```

- [ ] **Step 2: Run the curved red.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CertifiedConvexCurvedTests'`.
Expected: currently unsupported curved leaves refuse required complete cases.

- [ ] **Step 3: Implement the complete curved feature regimes.**

Retain analytic circle and cap domains. Use closed KKT/witness equations with outward
algebraic isolation, not sphere samples, polygon rings or a capsule inscribed in the moving
shell. Read represented target primitives and their operator enclosures. Root elimination
retains domain/sign checks and double-root events. Each attempt charges the existing account.
Refuse indeterminate arithmetic, do not suppress difficult leaves from the candidate set.

- [ ] **Step 4: Run the same focused selection green.**

Expected: exit 0 and complete independent hit/distance/contact/penetration evidence for all
three curved families. Document any supported-domain limitation as a refusal, never a pass.

- [ ] **Step 5: Run guards and commit explicit Task 4 paths.**

Commit `feat(physics): certify short-shell queries against curved targets`.

### Task 5: Complete finite meshes, compound descendants and pruning (SB-5 to SB-7, B/C)

**Files:**
- Create: `KhaozEngine.Physics.Bepu/ConvexMeshQueries.cs`, `KhaozEngine.Physics.Bepu/ConvexCompoundQueries.cs`
- Modify: `KhaozEngine.Physics.Bepu/ConvexQueryTraversal.cs`, `KhaozEngine.Physics.Bepu/ConvexQueryBounds.cs`, `KhaozEngine.Physics.Bepu/ConvexFeatureKernel.cs`
- Create: `KhaozEngine.Game.Tests/Physics/CertifiedConvexMeshTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedConvexCompoundTests.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/ConvexQueryFixture.cs`, `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes the installed immutable mesh/tree/compound geometry and Task 2's leaf evidence, preserving distinct coincident members in `ConvexSweepResult.Identity` under SS-R5.
- Produces internal mesh/compound `TryEvaluate(in ConvexQueryRequest request, InstalledSweepGeometry geometry, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence) -> bool` on ConvexMeshQueries and ConvexCompoundQueries. Traversal supplies the authenticated installed root and composed leaves through geometry, without reparsing authored shapes.

- [ ] **Step 1: Write mesh/compound completion and coverage reds.**

Add `FiniteOneSidedMeshCompletesAllConvexOperations`, `SparseMeshPruningFindsHiddenLaterBlocker`,
`CoincidentChildrenKeepRawIdentity`, `EveryExistingCompoundLeafKindCompletes` and
`UnprovedDescendantCancelsWholeConvexResult`. Cover both CullBackFaces values, back-to-front
initial overlap, sided tangent motion, tiny triangles, seams, yaw/tilt, centroid wrappers,
flat/nested admitted compounds and the existing non-rigid compound refusal boundary.

```csharp
Assert.Equal(oracleRawTriangle, actual.Identity.FeatureId);
Assert.Equal(expectedDistinctRawCount, contacts.Written);
Assert.Equal(ConvexSweepStatus.Unresolved, withUnsupportedChild.Status);
Assert.Equal(beforeSentinels, afterRefusedOutput);
```

- [ ] **Step 2: Run mesh/compound reds.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CertifiedConvexMeshTests|FullyQualifiedName~CertifiedConvexCompoundTests'`.
Expected: unsupported families or missing complete-coverage assertions fail.

- [ ] **Step 3: Implement sided finite traversal and complete descendant aggregation.**

Reuse installed forward/inverse operational enclosures and mutation-owned bounds. Prove the
convex query aperture against actual Bepu trees before pruning. Traverse finite triangles,
never filled mesh bounds. Carry original triangle/child IDs through all operations. Match
the existing closed initial-query and directional culling rules. Every admitted descendant
gets the same exact leaf kernel and shared whole-query budget. An unresolved later child
invalidates previously accumulated hits/contacts. No world scan replaces bounded traversal.

- [ ] **Step 4: Run the same selection green.**

Expected: exit 0, complete target-family matrix, hidden blocker found and every unsupported
descendant/side/operation disagreement refused atomically.

- [ ] **Step 5: Run guards and commit explicit Task 5 paths.**

Commit `feat(physics): certify convex queries over meshes and compounds`.

### Task 6: Prove lifetime, numerical domains, atomic refusal and work ownership (SB-6/SB-7, C)

**Files:**
- Create: `KhaozEngine.Physics/ConvexQueryDiagnostics.cs`
- Modify: `KhaozEngine.Physics/WorkerDiagnostics.cs`, `KhaozEngine.Physics.Bepu/ConvexQueryPolicy.cs`, `KhaozEngine.Physics.Bepu/ConvexQueryScratch.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.ConvexQueries.cs`
- Create: `KhaozEngine.Game.Tests/Physics/CertifiedConvexLifetimeTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedConvexRefusalTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedConvexWorkTests.cs`
- Modify: `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes the existing current-view/read-interval authentication and Tasks 2/3's bounded owners, including zero `ConvexSweepResult.Identity` on Clear and Unresolved under SS-R5.
- Produces `ConvexQueryCounts(long Queries, long Candidates, long Descendants, long Features, long Cells, long Refusals, int PeakScratchLoans)` and `ConvexQueryDiagnostics.Capture() -> IDisposable`, `ConvexQueryDiagnostics.Current -> ConvexQueryCounts`, integrated with worker exchange/merge/restoration. Events sum, scratch high water takes maximum.
- Produces internal budget injection that can only lower SB-7 ceilings. It cannot alter production policy or bypass enforcement. Existing capsule/support diagnostics and CounterCount remain correct.

- [ ] **Step 1: Write deterministic lifetime and boundary tests before any safety correction.**

Add `ConvexQueriesRejectEveryStaleSelectedBasis`, `FiniteDomainAdjacentFloatsRefuseAtomically`,
`EveryConvexBudgetExhaustsStickyWithUntouchedOutputs`, `FifthScratchLoanRefusesAndRestoresOuterLoans`
and `WorkerConvexCountersRestoreWithoutControllingSafety`. Exercise removal/handle reuse,
mutation after candidate capture, Step, rebase, sleeping/waking dynamics, wrong thread,
disposal, view exclusions, foreign receiver and current lease. Mutations happen after
disposing the read lease, never by attempting a write under its own read gate.

```csharp
Assert.False(staleDistance.Complete);
Assert.Equal(ConvexSweepStatus.Unresolved, exhausted.Status);
Assert.Equal(beforeSentinels, afterSentinels);
Assert.InRange(counts.PeakScratchLoans, 0, 4);
Assert.Equal(withDiagnostics.Status, withoutDiagnostics.Status);
```

- [ ] **Step 2: Run the focused safety selection.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CertifiedConvexLifetimeTests|FullyQualifiedName~CertifiedConvexRefusalTests|FullyQualifiedName~CertifiedConvexWorkTests'`.
Expected: diagnostics API is initially absent. Record that red, plus any demonstrated safety
failure. Passing protection controls stay passing.

- [ ] **Step 3: Implement diagnostic ownership and repair only demonstrated safety gaps.**

Bind query/view/geometry identities before work and before publication. Check all SB-7
finite limits and unsupported filter values before traversal. Restore scratch in finally,
retain at most four loans and make all budget exhaustion sticky. Stage outputs only after
complete validation, with known RequiredCapacity and untouched tails on refusal. Counters
observe actual work, never replay a proposed certificate or drive enforcement. Restore
worker/caller diagnostics even on setup failure and query refusal.

- [ ] **Step 4: Run the same focused selection green.**

Expected: exit 0, exact boundary behavior and bounded deterministic counts. Record every
ceiling and zero live loans after completion. Do not measure allocation rate or wall time.

- [ ] **Step 5: Run guards and commit explicit Task 6 paths.**

Commit `test(physics): prove convex query lifetime and bounded atomic refusal`.

### Task 7: Certify skin touching normals and recoverable penetration proposals (SB-4/SB-5, B/C)

**Files:**
- Create: `KhaozEngine.Physics.Bepu/ConvexRecovery.cs`
- Modify: `KhaozEngine.Physics.Bepu/ConvexQueryOperations.cs`, `KhaozEngine.Physics.Bepu/ConvexFeatureKernel.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.ConvexQueries.cs`
- Create: `KhaozEngine.Game.Tests/Physics/CertifiedConvexTouchRecoveryTests.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/ConvexQueryFixture.cs`, `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes Tasks 1 to 6's complete contacts, per-member penetration and current selected query, including the SS-R5 `ConvexSweepResult.Identity` contract.
- Produces `ConvexRecovery.TrySelect(ReadOnlySpan<ConvexContact> contacts, ReadOnlySpan<ConvexLeafEvidence> members, out ConvexPenetrationResult result) -> bool`. Select the deepest certified member MTV with raw-ID tie break. The result is a correction proposal, not proof that the obstacle union clears.

- [ ] **Step 1: Write true Minkowski-touch and recovery reds.**

Add `SkinProbeUsesExactMinkowskiSupportAtEveryFeature`,
`TouchNormalNeverComesFromASmallerProxy`, `DeepestMemberMtvIsNotAveragedAcrossASeam`,
`SymmetricPenetrationUsesStableRawTieBreak` and `RecoveryProposalMustEarnFinalClearance`.
Use apex/shoulder/head-rim touch, all target families, corners, two-wall wedges and coincident
members. An isolated overlap must produce a certified clearing member translation. A wedge
which cannot clear in the controller's fixed passes stays refused.

```csharp
Assert.Equal(0.001f, grown.Margin);
Assert.Equal(baseRadiusBits, BitConverter.SingleToUInt32Bits(grown.Radius));
Assert.True(ShortBodyShellOracle.NormalConeContains(grown, queryWitness, -touch.Normal));
Assert.True(ShortBodyShellOracle.MemberTranslationEnclosedBy(target, penetration));
Assert.False(unclearedFinalPlacementIsAccepted);
```

- [ ] **Step 2: Run the touch/recovery red selection.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CertifiedConvexTouchRecoveryTests'`.
Expected: missing recovery selection or failed exact-touch/recovery behavior.

- [ ] **Step 3: Complete exact touch and per-member minimum-translation publication.**

Certify the support sum K plus the existing skin sphere, including its lower/upper extent.
Prove contact normal-cone membership and outward direction, without seam averaging. Use
minimum-distance-to-boundary evidence for each convex target and deterministic deepest-member
selection. Return refusal on an unproved MTV, normal or active target. Reuse the complete
world/query validation from Task 3. No global-union MTV claim, manifold reduction, new
recovery pass or increased penetration tolerance.

- [ ] **Step 4: Run the same focused selection green.**

Expected: exit 0. Record complete member witnesses and the deliberately uncleared union
case. No raw proposed MTV becomes accepted placement without a new clearance check.

- [ ] **Step 5: Run guards and commit explicit Task 7 paths.**

Commit `feat(physics): certify short-shell touching and recovery proposals`.

### Task 8: Select the shell and integrate grounded movement without capsule drift (SB-1 to SB-3, SB-8, A/D)

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ContactShellDescription.cs`, `KhaozEngine.Locomotion/Contacts/ShortBodyShellGeometry.cs`, `KhaozEngine.Locomotion/Contacts/ContactShellClearance.cs`
- Modify: `KhaozEngine.Locomotion/Contacts/ShellGeometry.cs`, `KhaozEngine.Locomotion/Contacts/ContactShell.cs`, `KhaozEngine.Locomotion/Contacts/ContactQueryContext.cs`, `KhaozEngine.Locomotion/Contacts/ShellMotion.cs`, `KhaozEngine.Locomotion/Contacts/GroundPlacement.cs`, `KhaozEngine.Locomotion/Contacts/ContactPlacement.cs`, `KhaozEngine.Locomotion/Contacts/GroundCore.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ShortBodyShellGeometryTests.cs`, `KhaozEngine.Game.Tests/Locomotion/Contacts/ShortBodyGroundTests.cs`
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/ShellGeometryTests.cs`, `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Produces public `ContactShellKind` values None = 0, Capsule = 1, ShortBody = 2 and immutable `ContactShellDescription` with Kind, byte RecipeVersion, CapsuleQueryDimensions Capsule, float CentreOffsetY and ConvexQueryShape Convex. Inactive fields are canonical defaults, construction belongs to the factory.
- Produces `ContactShell.Describe(in MoveTuning tuning) -> ContactShellDescription` and `ContactShellDescription.PoseAt(Vector3 kernelFeet) -> Pose`. Capsule uses its original centre addition, ShortBody uses feet directly. RecipeVersion is 1 for either active kind.
- Produces `ContactShellClearance.TryBound(in ConvexQueryShape shape, float bandedAngle, out double lower, out double upper) -> bool`, enclosing the shape's minimum plane clearance including its Margin, with directed trigonometric bounds and no comparison epsilon. Base-shape validation compares lower with 0.001, and printed surplus subtracts that skin outward.
- Context adds general overloads `SweepShell(in ContactShellDescription shell, Vector3 feet, Vector3 direction, float distance, out SweepHit hit, QueryFilter filter = default) -> bool`, `PenetrationShell(in ContactShellDescription shell, Vector3 feet, out Vector3 mtv) -> bool`, and `QueryShellContacts(in ContactShellDescription shell, Vector3 feet, Span<ConvexContact> contacts) -> ConvexContactResult`. Retain original capsule-only overloads. Capsule descriptor branches delegate to them with unchanged arithmetic. Until Task 9, a leased ShortBody explicitly refuses, while dry ShortBody is complete. Existing leased capsule tests still compile and pass.

- [ ] **Step 1: Write geometry selection and ground behavior reds.**

Add `DuckAndCowSelectExactConeCylinderWithUnchangedStepReach`,
`FittingCapsulesKeepEveryShapeAndCentreBit`, `OldCapsulePlaneFailureDoesNotSelectShortBody`,
`RoundedMidpointCannotCollapseTheLowerCone`, `ShortShellHeadDiscCatchesOffAxisRoofAfterLift`,
`ShortBodiesClimbQuarterMetreRisersWithPacedSupportRise` and
`ShoulderContactCannotManufactureAnUnchargedClimb`. Keep exact-equality cylinder-floor tests.
Test all traction-band signs, skin equality/adjacent floats and 48 degree plane clearance.

```csharp
Assert.Equal(ContactShellKind.ShortBody, ContactShell.Describe(duck).Kind);
Assert.Equal(0.4f, duck.StepHeight);
Assert.Equal(originalCapsuleBits, describedCapsuleBits);
Assert.True(duckMarginLower >= 0.15147 && cowMarginLower >= 0.09966);
Assert.False(offAxisRoofPlacementAccepted);
Assert.True(stepPartRise <= originalClimbBudget);
```

- [ ] **Step 2: Run the geometry/ground red.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodyShellGeometryTests|FullyQualifiedName~ShortBodyGroundTests'`.
Expected: current span validation rejects required bodies or new descriptor is absent.

- [ ] **Step 3: Implement the descriptor, certified plane guard and dry ground dispatch.**

Use the exact selection/representation order in SB-1/SB-2. Bound sin/cos of the represented
angle with outward rational/interval Taylor remainder bounds on the admitted first quadrant,
refining within a fixed bounded account. Refuse undecided comparisons. Do not change the
old capsule slope guard or equality-floor arithmetic. Keep public Shape's signature and
capsule cache identity, throwing explicitly for ShortBody. Dispatch dry shell motion through
IPhysicsConvexQueries, and refuse a missing capability. Use full-radius foot support and
full-head-disc roof checks. Recovery and final placement requery after every correction,
with the original four recovery/four slide limits. Keep original capsule statements in
their own branch. Do not replace them with an equivalent-looking generalized recurrence.

- [ ] **Step 4: Run the focused ground green and existing controls.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodyShellGeometryTests|FullyQualifiedName~ShortBodyGroundTests|FullyQualifiedName~ShellGeometryTests|FullyQualifiedName~ShellMotionTests|FullyQualifiedName~ContactShellTests|FullyQualifiedName~GroundCoreTests|FullyQualifiedName~FootSupportFootprintTests'`.
Expected: exit 0, required body margins, paced 0.25 m steps, off-axis roof refusal and unchanged
capsule bits/verdicts. Record original support radius and discrete climb evidence.

- [ ] **Step 5: Run guards and commit explicit Task 8 paths.**

Commit `feat(locomotion): select certified full-width short-body shells`.

### Task 9: Carry the real shell through leased, air and medium movement (SB-8/SB-9, C/D)

**Files:**
- Create: `KhaozEngine.Locomotion/MovementShellQuery.cs`, `KhaozEngine.Locomotion/MovementQueryLease.ConvexShell.cs`, `KhaozEngine.Locomotion/MovementShellBounds.cs`, `KhaozEngine.Locomotion/MovementConvexResolver.cs`
- Modify: `KhaozEngine.Locomotion/MovementQueryLease.ContactQueries.cs`, `KhaozEngine.Locomotion/Contacts/ContactQueryContext.cs`, `KhaozEngine.Locomotion/Contacts/LeasedContactQueryContext.cs`, `KhaozEngine.Locomotion/Contacts/LeasedContactQueryContext.Motion.cs`, `KhaozEngine.Locomotion/Contacts/AirPass.cs`, `KhaozEngine.Locomotion/MovementCapsuleResolver.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ShortBodyLeasedMotionTests.cs`, `KhaozEngine.Game.Tests/Locomotion/Contacts/ShortBodyAirWaterParityTests.cs`
- Modify: `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes the certified `ConvexSweepResult` with exact raw Identity on Hit and zero Identity on Clear or Unresolved under SS-R5 before presenting its operational SweepHit.
- Produces internal `MovementShellQuery(ContactShellDescription Shell, Pose Pose, MovementBodyQuery Envelope)`. Envelope supplies scope only, never a collision shape.
- Produces `MovementShellBounds.TryCreate(in MovementShellQuery shell, Vector3 displacement, out Vector3 minimum, out Vector3 maximum) -> bool`, enclosing the actual shape, grown probe and original mapping error.
- Produces MovementQueryLease overloads `SweepContactShell(in MovementShellQuery shell, Vector3 direction, float distance, QueryFilter filter, out bool blocked, out SweepHit hit) -> MovementAvailability`, `PenetrateContactShell(in MovementShellQuery shell, out bool overlapping, out Vector3 mtv) -> MovementAvailability` and `QueryContactShell(in MovementShellQuery shell, Span<ConvexContact> contacts, out ConvexContactResult result) -> MovementAvailability`.
- Produces a `MovementCapsuleResolver.TryResolveContact` overload whose first argument is `in MovementShellQuery shell`, with the existing remaining body/delta/lease/water/boundary/path/output parameters unchanged. Its capsule branch delegates to the original overload, its ShortBody branch uses the new certified doors.
- Produces `MovementConvexResolver.TryResolveContact` with that same shell-first signature for ShortBody work. Keep the old resolver's forwarding overload small and put this new concern in the new type, without growing a frozen KESIZE baseline.

- [ ] **Step 1: Write leased/air/handoff reds before adapting the resolver.**

Add `LeasedShortShellQueriesTheRealVolumeAtEveryScopeEdge`,
`OrdinaryAndExplicitShortBodyHeadsMatchTheirPlacementRecipes`,
`AirCeilingLandingAndRecoveryUseTheSelectedShortShell`,
`WadeSwimHandoffKeepsFullBodyWaterlineAndCoverage` and
`ShortBodyArrivalStillUsesTheEuclideanMillimetreBall`. Include unsupported capability,
stale lease, rebased positive/negative origin, large coordinates, midpoint rounding,
zero-motion Hold and a diagonal residual whose components pass but XYZ distance fails.

```csharp
Assert.Equal(fullBodyRadiusBits, sampledMediumRadiusBits);
Assert.Equal(fullBodyHeightBits, sampledMediumHeightBits);
Assert.Equal(expectedPlacementOffsetBits, actualPlacementOffsetBits);
Assert.Equal(referenceCapsuleVerdict, unchangedCapsuleVerdict);
Assert.False(diagonalOutsideMillimetreIsAccepted);
Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, missingCapability.Outcome);
```

- [ ] **Step 2: Run the leased/air/handoff red.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodyLeasedMotionTests|FullyQualifiedName~ShortBodyAirWaterParityTests'`.
Expected: capsule-only adapter/resolver cannot complete the new required body path.

- [ ] **Step 3: Implement real-shape leased scope and complete path dispatch.**

Construct one descriptor per immutable tuning/workspace basis. Keep the exact geometric
feet and placement mapping distinct from the full body's centre. Validate scope with the
full convex aperture, touching Margin and existing mapping enclosure, then authenticate
the same selected receiver before and after querying. New physics hit distances are metres
along the complete displacement. Prove any conversion back to the controller's original
direction/distance parameter, including its non-unit represented direction, before using
the skin-shortened advance. An inward conversion or uncertain enclosure refuses. Never use
MovementCapsuleRounding to certify a ShortBody query. Carry the real shell through domain projection, air, final
placement and recovery while full-body medium coverage stays unchanged. Preserve all
existing traversal, arrival, head-offset and capsule arithmetic. Whole-tick refusal cannot
leave accepted partial state, ground signals or a stale binding.

- [ ] **Step 4: Run focused new paths and parity controls green.**

Run `build-slot --heavy --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodyLeasedMotionTests|FullyQualifiedName~ShortBodyAirWaterParityTests|FullyQualifiedName~ContactLeasedQueryTests|FullyQualifiedName~MovementCapsuleResolverTests|FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~ContactController'`.
Expected: exit 0, both heads and ordinary/explicit paths agree under their documented offsets,
real-volume scope proof, retained waterline and no capsule or captured-stair regression.

- [ ] **Step 5: Run guards and commit explicit Task 9 paths.**

Commit `feat(locomotion): certify short-shell air and leased medium paths`.

### Task 10: Encode compatible format 3 and immutable runtime shell identities (SB-10/SB-11, E)

**Files:**
- Create: `KhaozEngine.Movement/NavBakeIdentity.Shell.cs`
- Modify: `KhaozEngine.Movement/NavBakeIdentity.cs`, `KhaozEngine.Movement/NavBakeIdentity.Contact.cs`, `KhaozEngine.Movement/NavBakeIdentity.Runtime.cs`, `KhaozEngine.Movement/NavProfileIdentity.cs`, `KhaozEngine.Movement/GroundNavigation.Validation.cs`
- Modify: `KhaozEngine.Movement/GroundNavigationBake.cs`, `KhaozEngine.Movement/GroundNavigationBake.Reader.cs`, `KhaozEngine.Movement/GroundNavigationBake.Writer.cs`, `KhaozEngine.Movement/ExplicitMovementProfile.cs`, `KhaozEngine.Movement/ContactNavigationInitialHold.cs`, `KhaozEngine.Movement/ContactNavigationProof.Reuse.cs`
- Create: `KhaozEngine.Movement.Tests/ShortBodyNavIdentityTests.cs`, `KhaozEngine.Movement.Tests/ShortBodyNavFormatTests.cs`
- Modify: `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Produces `NavBakeIdentity.ShortBodyFormatVersion = 3` and `HasContactPayload(ushort version) -> bool` true for versions 2/3, replacing equality tests where both share payload grammar.
- Produces `NavBakeShellIdentity.Write(NavBakeWriter writer, in ContactShellDescription shell) -> void`, `TryRead(ref NavBakeReader reader, in ContactShellDescription expected, out ContactShellDescription actual) -> bool` and `FirstDifference(in ContactShellDescription bake, in ContactShellDescription want) -> string?`. The fixed record is exactly SB-10's 26 bytes.
- NavProfileIdentity retains `ContactShellDescription Shell`, None for legacy. New runtime checks compare stored descriptor bits before graph use. Initial-Hold key construction gains `in ContactShellDescription shell`, including its kind/recipe/all dimension bits. Profile-domain tokens bind the same immutable descriptor.
- ExplicitMovementProfile retains capsule recipe 1 and adds ShortBody recipe 2, exact controller-model text from SB-11 and the same canonical shell record. Public explicit navigation remains unsupported.

- [ ] **Step 1: Write format/identity reds before changing the writer.**

Add `ShortBodyCollectionChoosesFormatThreeAndExactShellRecord`,
`FormatThreeMixedProfilesRetainAllRestingFeet`, `LegacyAndCapsuleOnlyFilesKeepExistingBytes`,
`EveryShellBitOrRecipeMismatchRefusesBeforeTraversal`,
`OldFormatsCannotInferShortShellFromMissingFields` and
`ExplicitShortShellFingerprintKeepsEveryExistingSemanticInput`.
Construct expected format 3 records independently from the documented field order. Include
truncated records, unknown kinds/recipes, signed-zero unused fields, nonzero reserved placement,
nonfinite values, mismatched body dimensions and inconsistent file/payload bounds.

```csharp
Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(shortFile.AsSpan(4, 2)));
Assert.Equal(26, expectedShellRecord.Length);
Assert.Equal(expectedRecordBits, actualRecordBits);
Assert.Equal(originalFormatOneFile, legacyFile);
Assert.Equal(originalFormatTwoFile, capsuleFile);
Assert.Equal(expectedAllProfilesRestingFeetBits, loadedRestingFeetBits);
Assert.Throws<ArgumentException>(() => graph.Validate(wrongShellBinding));
```

- [ ] **Step 2: Run the identity/format red.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodyNavIdentityTests|FullyQualifiedName~ShortBodyNavFormatTests'`.
Expected: absent format 3 or shell mismatch fails. Preserve the original format 1/2 goldens.

- [ ] **Step 3: Implement canonical identity, compatibility and all format gates.**

Select version 3 only for a short contact profile, version 2 for other contact collections,
version 1 for all legacy. Append exact shell records after each unchanged format 2 block.
Keep the reserved placement field zero. Update identity sizes, compare/decode, writer header,
reader allowed versions, contact payload bounds, scratch allocation and every resting-array
branch. Decode the actual descriptor and check against tuning, never infer short-shell semantics
from old bytes. Preserve stored descriptor in runtime graph identity and reuse domains.
An old/short format disagreement is an explicit rebake mismatch. Keep capsule-only explicit
fingerprints unchanged at fixed engine identity. Include shell kind/recipe/bits in short
fingerprints and Hold keys without changing legacy binding selection or aquatic restrictions.

- [ ] **Step 4: Run focused identity/format and historical controls green.**

Run `build-slot --label short-shell-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodyNavIdentityTests|FullyQualifiedName~ShortBodyNavFormatTests|FullyQualifiedName~NavBakeIdentityTests|FullyQualifiedName~ExplicitMovementProfileTests|FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~ContactNavigationBasisTests'`.
Expected: exit 0, exact old bytes, canonical 26-byte records, complete mixed roundtrip and
early mismatch refusal. Do not regenerate any existing golden to accommodate a difference.

- [ ] **Step 5: Run guards and commit explicit Task 10 paths.**

Commit `feat(navigation): persist certified short-shell identity in format three`.

### Task 11: Prove full footprint, raw layers, arrival and deterministic bake compatibility (SB-9 to SB-12, D/E)

**Files:**
- Create: `KhaozEngine.Movement.Tests/ShortBodyNavigationProtectionTests.cs`, `KhaozEngine.Movement.Tests/ShortBodyNavigationReuseTests.cs`, `KhaozEngine.Movement.Tests/ShortBodyNavigationScheduleTests.cs`, `KhaozEngine.Movement.Tests/Fixtures/ShortBodyNavigationFixture.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ShortBodySupportProtectionTests.cs`
- Modify only for demonstrated defects: `KhaozEngine.Movement/NavAreaFootprint.cs`, `KhaozEngine.Movement/ContactNavigationProof.cs`, `KhaozEngine.Movement/ContactNavigationInitialHold.cs`, `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`
- Modify: `docs/verification/2026-10-19-short-body-shell.md`

**Interfaces:**
- Consumes all prior public/immutable interfaces, including `ConvexSweepResult.Identity` under SS-R5, existing raw-source mapping and proof-reuse modes.
- Produces test fixture `ShortBodyNavigationFixture.Build(string scene, string mode, int degree) -> byte[]`, using production serialization and modes Off, Footprints, Combined. Existing stable-provider assertion governs Hold sharing.
- Produces immutable scenes for duck/cow, mixed legacy/player/short profiles, hidden support, forbidden corner, roof/lip, stacked layers and exact-arrival boundaries. Degree matrix is 1, 2, 3, 8 and greater than total layer-row count, distinct rows only. This is one finite theory selection, not repeated stress.

- [ ] **Step 1: Write complete protection and reuse controls before any correction.**

Add `ShortBodyKeepsCompleteBodyRadiusSupportAndEveryTouchedAreaColumn`,
`NearestRawSurfacePrecedesAllowedTags`, `RaisedShortAnchorEarnsNewFullHeadroom`,
`CoincidentAnchorsAndDifferentShellsCannotShareHoldEvidence`,
`ArrivalHoldAndDirectedEdgesKeepExactEuclideanBoundary`,
`EveryReuseModeAndDegreeWritesIdenticalShortBodyFiles` and
`ExistingSevenCapsuleBaselinesKeepAllPinnedHashes`.
Use foot discs exactly 0.1/0.45 m, hidden curved support, split statics, joins, support holes,
disallowed circular corners, a lower layer under an upper deck, original raw ordinals and
height bits, signed zeros, asymmetric stairs and the unchanged eight pool-edge refusals.

```csharp
Assert.Equal(expectedRawColumnOrdinalHeightBits, actualRawIdentity);
Assert.Equal(expectedFullRadiusAreaVerdict, actualAreaVerdict);
Assert.Equal(referenceFile, candidateFile);
Assert.Equal(pinnedNormalizedHash, actualNormalizedHash);
Assert.Equal(0, wrongShellHoldReuseHits);
Assert.False(outsideEuclideanBallIsAccepted);
```

- [ ] **Step 2: Add schedule, mutation and whole-build refusal controls.**

`ShortBodyBakeKeepsSerialFailureOrdinalAndCleansWorkerState` forces band completion order
with held completion gates, not sleeps. Cover changed selected-view geometry after the last
reuse hit, partial worker setup, phase-thread changes and source cleanup. Original source
column/ordinal/height/layer never coalesces by equal resting height. Assert independent
endpoint support, roof, full-body medium and final arrival for each accepted edge.

- [ ] **Step 3: Run the two focused acceptance selections once.**

```bash
build-slot --heavy --label short-shell-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodyNavigationProtectionTests|FullyQualifiedName~ShortBodyNavigationReuseTests|FullyQualifiedName~ShortBodyNavigationScheduleTests|FullyQualifiedName~ContactNavigationProofReuseBaselineTests|FullyQualifiedName~ContactNavigationProofReuseProtectionTests'
build-slot --label short-shell-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ShortBodySupportProtectionTests|FullyQualifiedName~FootSupportFootprintTests|FullyQualifiedName~SupportNeighborhoodEquivalenceTests'
```

Expected: exit 0 for each, every stated matrix row executed, unchanged seven capsule hashes
and format 1 golden, identical new format 3 bytes across reuse/schedule modes. Any new
failure is a recorded red requiring the proper owning-module fix before its focused green.
Do not add speculative production edits or replace a protection fixture. No elapsed-time
criterion or extra work budget can override a rejected geometric proof.

- [ ] **Step 4: Record all eight research obligations and actual deterministic work.**

Map each row of the spec's eight-obligation table to test names and observed exits. Include
the exact shell descriptor, all target operations, banded plane intervals, support radii,
roof/area/arrival/raw-layer evidence and counts for base/grown queries. Record bounded cells,
features, descendants and scratch peaks. Do not claim SG-21 cost or game route acceptance.

- [ ] **Step 5: Run guards and commit explicit Task 11 paths.**

Commit `test(navigation): prove short-shell protections and deterministic bakes`.

### Task 12: Publish contracts, reconcile the staged version and run final Release verification (SB-12, F)

**Files:**
- Modify: `KhaozEngine.Physics/README.md`, `KhaozEngine.Physics.Bepu/README.md`, `KhaozEngine.Locomotion/README.md`, `KhaozEngine.Movement/README.md`, `docs/USING-KHAOZENGINE.md`
- Modify: `docs/design/CONTACT-SHORT-BODY-SHELL-2026-10-19.md`, `docs/INDEX.md`, `docs/verification/2026-10-19-short-body-shell.md`, `CHANGELOG.md`
- Modify only if the staged version has released: `Directory.Build.props` and every guarded version declaration reported by `scripts/check-doc-versions.sh`

**Interfaces:**
- Consumes accepted Task 1 to 11 commits, observed reds/greens, target-family matrix, exact identity/protection evidence including `ConvexSweepResult.Identity` under SS-R5, and deterministic counters.
- Produces living API/compatibility contracts and a once-only full Release verification record. This final task changes no production behavior, so it audits preceding reds rather than inventing a docs-only failing test.

- [ ] **Step 1: Audit spec coverage and final premises before broad verification.**

Map SB-1 to SB-12, acceptance A to F, all five Review Focus tests and each of the eight
research obligations to actual evidence. Confirm every target family completes every required
operation for both bodies and both Margin values. Verify refusal boundaries independently
from ordinary successful rows. Record any missing proof as a blocker, not an adoption pass.
Inspect current main and remote tags without merging or pushing. If code reconciliation is
needed, hand it to the orchestrator before treating this branch's full run as final integration
evidence. No release action is authorized.

- [ ] **Step 2: Update living docs and sweep every changed name and behavior.**

Document descriptor selection, exact cone-cylinder, 48 degree clearances, full-head-disc roof
policy, optional backend discovery, domain/work limits, results/refusal semantics, true
Minkowski touch and per-member recovery. Document ContactShell.Shape compatibility, formats
1/2/3, exact shell identity, rebake mismatch, explicit fingerprint recipe and per-profile
controller selection. Search all Markdown for ContactShell, ShellGeometry, capsule shell,
SweepCapsule, NavBakeIdentity, KENB, format 2, ExplicitMovementProfile, #1344 and #438.
Correct stale living statements and list any additional affected path before staging.
Keep historical released evidence intact and name Grimhollow adoption as release-gated.

- [ ] **Step 3: Ride the current untagged version or stage the next free patch.**

Re-read Directory.Build.props, current main, newest release tags and CHANGELOG.md. If
21.7.0 is still the current staged untagged version, leave the version unchanged, append
the certified short-body shell/convex query/format 3 behavior to that entry and roll its
date. If 21.7.0 has released, select the next free patch above current main, update all
guarded declarations and add its dated
CHANGELOG entry in this same final commit. Preserve every released entry. Do not blindly
stamp 21.7.0 onto a concurrently advanced branch or tag anything.

- [ ] **Step 4: Run the full Release solution build once.**

```bash
build-slot --heavy --label short-shell-design -- dotnet build KhaozEngine.slnx -c Release
```

Expected: exit 0, zero warnings/errors, all solution heads and changed projects built.
Local-feed must already exist before restore. Record the actual exit and build summary.

- [ ] **Step 5: Run full Game, Movement and Physics coverage once through the full solution selection.**

```bash
build-slot --heavy --label short-shell-design -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
```

This is the required full run, not a new-feature filter. It includes the complete
KhaozEngine.Game.Tests and KhaozEngine.Movement.Tests projects. Physics/Bepu unit tests live
in Game.Tests/Physics, there is no KhaozEngine.Physics.Tests project. The same solution run
also includes KhaozEngine.Physics.HeadlessLoaderTests, KhaozEngine.TileWorld.Physics.Tests
and KhaozEngine.MapDoc.Physics.Tests. Record actual discovered/passed/failed/skipped counts
for those areas and confirm none was omitted. Expected: exit 0 with zero failures. Plain
CPU tests skip GPU facts under the existing policy, this lane adds no GPU work. Never run
those full suites again merely to obtain a second green.

- [ ] **Step 6: Run format verification once after the final edits.**

```bash
build-slot --heavy --label short-shell-design -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --verbosity diagnostic
```

Expected: exit 0 with no changes and no suppressed workspace-load warnings. Confirm all
changed projects loaded. A meaningful failure gets a correction and justified affected
verification, never an unchanged retry or a broader tolerance.

- [ ] **Step 7: Stage explicit paths and run final staged/whole-tree governance.**

```bash
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --cached --check
```

Expected: all exit 0. Inspect final diff for changed old goldens, capsule arithmetic, work
limits, hidden proxies, radius/height/step tuning, arrival distance, raw identity or a
nonzero reserved placement field. None is authorized. No local pack or windowed proof.

- [ ] **Step 8: Commit and return the verified engine work to the orchestrator.**

Commit `docs(locomotion): publish certified short-shell contracts and verification`.
Return task commits, red/green commands and exits, actual clearance intervals, target-family
coverage, protection/identity matrix, work bounds and full verification results. Stop.
No push, merge, comment, subagent, release, tag, Grimhollow tuning or rebake. The orchestrator
reconciles main and owns integration. A later engine-rooted release precedes game adoption.

## Plan self-review and handoff

SB-1 to SB-3 map to Tasks 1/8, SB-4/SB-5 to Tasks 1 to 4/7, SB-6 to Tasks 3 to 6,
SB-7 to Tasks 2/3/5/6, SB-8 to Tasks 8/9, SB-9 to Tasks 8/9/11, SB-10/SB-11 to
Tasks 10/11 and SB-12 to Tasks 11/12. All acceptance groups and all eight research
obligations have named tests in their owning tasks. Each Review Focus item has its exact
test in Task 3, 8, 10 or 11. Shared interfaces retain the same descriptor, shape, result,
query, normal, raw identity and format names across tasks. The full Release solution test
selection covers all requested Game, Movement and Physics tests once, without inventing a
nonexistent Physics.Tests project or duplicating Physics tests already in Game.Tests.

The spec chooses the continuous silhouette and certificates, the plan fixes the file and
interface boundaries. Implementation bodies remain the implementer's work. Every new
feature has an observed red before code, and protection-only controls retain their existing
verdicts. A failure cannot be repaired with a smaller shell, tolerance, extra reach, skipped
target or larger budget. Unresolved evidence never becomes a usable prefix or accepted route.

The design author commits only this plan, its spec and the index row, then stops. The owning
orchestrator reviews both artifacts and authorizes execution separately. This handoff does
not request an execution choice or dispatch a worker from the design lane.
