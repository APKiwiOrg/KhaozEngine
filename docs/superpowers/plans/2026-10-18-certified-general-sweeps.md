# Certified General Capsule Sweeps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete #1355's backend certificates so acquired authoritative contact ticks advance over supported rotated boxes, authored hulls, mesh triangles and compound descendants.

**Architecture:** Keep the identity-box fast path and existing selected-view lease composition. Install conservative bound evidence and immutable actual-shape topology at the mutation boundary, then certify finite capsule/feature distance over the original closed path with interval bounds and bounded exact fallback. Each shape-family task completes outer coverage, leaf proof, lifetime and allocation gates before the next family starts.

**Tech Stack:** C# / .NET 10, pinned BepuPhysics 2.4.0, xUnit, `KhaozEngine.Physics.Bepu`, `KhaozEngine.Game.Tests`, shared allocation-region helpers.

**Spec:** `docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md`, GS-1 to GS-16. Read it and the research record `.superpowers/sdd/contact-followups/research-a.md` from the main checkout before execution. Existing sweep/candidate designs and SG-1, SG-6 to SG-8, SG-22, SG-23 remain binding.

## Global Constraints

preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity.
No tolerance, skin, band or limit may be widened.
A proper fix beats a workaround even when larger.
A hull is never treated as its AABB, a rotated box is never certified by inflated planes, a mesh keeps one-sided triangle semantics, and a ray, a sampled path or a FootSupport witness never establishes swept completeness.
Partial work never yields a Clear prefix.
No change to ShellMotion, ShellGeometry, LeasedContactQueryContext, FootSupport or MovementQueryLease behaviour.
No timing or load run is part of any task.
No version or CHANGELOG change except in the final task, which adds a CHANGELOG bullet riding the staged version in Directory.Build.props.

- Execute serially in `/Users/antonio/KhaozEngine/.worktrees/certified-general-sweeps`, branch `feature/certified-general-sweeps`. Before each task run `pwd`, `git rev-parse --abbrev-ref HEAD`, `git status --short`. Stop on another branch or unexplained tracked changes. Preserve unrelated work. The design baseline is `dcb0599e3`.
- Implementers commit their assigned task and stop. Integration, push, release, cleanup and consumer adoption belong to the orchestrator. Do not dispatch parallel writers into these coupled files.
- Retain the 0.001 m explicit offset, 0.0001 m contact/join band, 256 raw footprint-member cap, whole-tick hold and raw Static/ElementId/canonical owner distinctions.
- GS-3 fixes every numerical/work ceiling. New general policy version 1 does not change wire generation 14, frozen profiles or navigation bytes. Do not broaden the identity-box reader's domain.
- Every local build and test command uses `build-slot --label <task> -- <command>`, with `--heavy` for wide filters and full runs. Run one command at a time. A task ends with one Release solution build and one full Game.Tests run. Do not run the full suite per fix round or retry unchanged failures.
- Use focused RED and GREEN runs for changed assertions. A compile/setup error is not RED. No timing sampler, stopwatch experiment, hosted cost job, load test, client boot or test-run loop.
- Allocation tests use `AllocationMeasurement` or the checked shared allocation region, in `AllocSensitive`. Cold provision and one priming invocation precede one measured invocation. Prove geometry succeeds before judging bytes. Do not change tiering for the suite.
- Before each commit stage explicit paths and run `sh scripts/check-dashes.sh staged`, `sh scripts/check-prose.sh staged`, `sh scripts/check-file-size.sh staged` and `bash scripts/check-doc-versions.sh`. Fix guard failures without bypasses or size-baseline growth. Commit with an explicit pathspec, no attribution.

## Review Focus

1. A rounded quaternion is near-unit but its transpose is not its inverse. Preserve actual installed geometry and refuse unproved operational disagreement (`RotatedBoxOperationalMapNeverBecomesAnExactInverse`, Task 1).
2. A public hull is really an installed centroid-compensating Compound, with recycled typed indices after removal. Preserve both offset and installation lifetime (`HullWrapperAndReusedHandleKeepInstalledVertexIdentity`, Task 2).
3. A moving capsule crosses an isolated triangle's edge from its open back, or is stationary on it. Preserve filter-specific eligibility and closed stationary semantics (`MeshSideAndStationaryEligibilityUsePinnedPolicy`, Task 3).
4. An early compound child returns Hit(0), but another required child has unsupported geometry or exhausts work. Refuse atomically (`CompoundHitZeroCannotHideARefusedDescendant`, Task 4).
5. The real producer acquires successfully, but a first lift or look-ahead refuses before any substep. Require Advanced, consumed signals and geometry before allocation acceptance (`AcquiredExplicitCostRowsAdvanceBeforeAllocationVerdict`, Task 5).

---

## File structure and shared interfaces

The following names are the internal handoff contract. Private representation choices stay with
their owner. No public Physics seam or Locomotion behavior is added.

| File | Responsibility |
| --- | --- |
| `CapsuleGeneralSweepPolicy.cs` | Version 1, GS-3 constants and value work counters |
| `InstalledSweepGeometry.cs` | Immutable installed topology and a value leaf view, bound to actual typed installation |
| `BepuPhysicsWorld.InstalledSweepGeometry.cs` | Registration lifetime/cache ownership and mutation-time bound publication |
| `InstalledSweepBounds.cs` | Outward actual-shape bounds, including composed poses and later mesh scale |
| `CapsuleSweepFeatureDistance.cs` | Capsule segment versus finite convex face/edge/vertex regimes and interior occupancy |
| `CapsuleSweepFeaturePolynomial.cs` | Original-fraction quadratics with exact membership constraints and closed root isolation |
| `BoundedGeometryArithmetic.Sweep.cs`, `SweepExactWorkspace.cs` | Shared arithmetic-owner extension, bounded exact polynomial signs without heap fallback |
| `CapsuleConvexSweep.cs` | Complete convex-leaf fraction proof and existing metre encoding |
| `CapsuleGeneralSweep.cs` | Complete root/descendant dispatch and atomic aggregation |
| `CapsuleTriangleSweep.cs`, `CapsuleMeshSweep.cs` | Finite sided triangle proof and certified inner-tree traversal |
| `CapsuleCompoundSweep.cs` | All supported installed convex descendants and their composed poses |
| `CapsuleSweepScratch.cs`, `QueryScratch.cs` | Bounded per-operation retained storage with take/restore |

All paths above are under `KhaozEngine.Physics.Bepu/`. Existing `SweepBounds`,
`CertifiedSweeps`, `SweepMutations`, `Rebase` and removal hooks retain their responsibilities.
Do not grow a ratcheted file by moving unrelated methods. `ShapeFactory` is a read-only installation
contract unless source-backed bound repair requires a narrowly reviewed change.

Task 1 defines these exact signatures for later tasks:

```csharp
// InstalledSweepGeometry.cs. Captured arrays stay private and immutable.
internal sealed class InstalledSweepGeometry
// InstalledSweepLeaf is a readonly value view over that topology and its current proved pose.
internal readonly struct InstalledSweepLeaf
// CapsuleGeneralSweepPolicy.cs. Counters are per query, cells reset per primitive.
internal struct CapsuleSweepWork
// CapsuleSweepScratch owns a SweepExactWorkspace and retained regime/traversal arrays.
internal sealed class CapsuleSweepScratch

// CapsuleSweepGeometry.cs. Capture runs at installation, ReadLeaf under the live gate.
internal static bool TryCapture(Shapes shapes, TypedIndex root,
    out InstalledSweepGeometry? geometry)
internal static bool TryReadLeaf(Shapes shapes, TypedIndex root, RigidPose parent,
    InstalledSweepGeometry geometry, int leafIndex, out InstalledSweepLeaf leaf)

// InstalledSweepBounds.cs. False publishes neither bound.
internal static bool TryCreate(InstalledSweepGeometry geometry, RigidPose pose,
    out Vector3 minimum, out Vector3 maximum)

// CapsuleConvexSweep.cs
internal static CapsuleSweepResult Sweep(in InstalledSweepLeaf leaf, Vector3 centre,
    float radius, float halfCylinderLength, Vector3 displacement,
    float maximumErrorMetres, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)

// CapsuleGeneralSweep.cs. Identity boxes still call CapsuleBoxSweep directly.
internal static CapsuleSweepResult Sweep(Shapes shapes, TypedIndex root, RigidPose pose,
    InstalledSweepGeometry geometry, in CapsuleQueryDimensions capsule, Vector3 centre,
    Vector3 displacement, QueryFilter filter, ref CapsuleSweepWork work,
    CapsuleSweepScratch scratch)
```

`TryCapture` initially accepts only boxes, adds exact one-hull wrappers in Task 2, meshes in
Task 3 and general flat compounds in Task 4. A failed supported-family capture leaves a selected
unproved record, never an omitted object. Missing/corrupt lifetime records invalidate all evidence.
Root identity, cached topology identity and leaf indices must be checked before native access.

The new test-only `CertifiedGeneralSweepFixture` in `Physics/` supplies:

```csharp
internal static CapsuleSweepResult Read(BepuPhysicsWorld world, Vector3 centre, Vector3 delta,
    float radius = 0.25f, float length = 1f, QueryFilter filter = default,
    bool useDimensions = false, StaticHandle[]? exclusions = null)
internal static void Clear(CapsuleSweepResult result, float representedLength)
internal static void HitContains(CapsuleSweepResult result, double trueLower, double trueUpper)
internal static void Refused(CapsuleSweepResult result)
```

`Read` acquires the real receiver's lease and checks it before/after. Non-null exclusions select
an actual restricted view. `HitContains` asserts Hit, lower <= trueLower <= trueUpper <= upper,
bracket width <= CertifiedErrorMetres <= 0.001. `Clear` checks the entire encoded length and null
impact. `Refused` asserts equality to default. Test oracles use independent exact rational arithmetic
and installed-data reads, never the production feature kernel or ordinary Bepu miss as ground truth.

### Task 1: Certify rotated boxes end to end

**Files:**
- Create: `KhaozEngine.Physics.Bepu/CapsuleGeneralSweepPolicy.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.InstalledSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepBounds.cs`, `KhaozEngine.Physics.Bepu/CapsuleSweepFeatureDistance.cs`, `KhaozEngine.Physics.Bepu/CapsuleSweepFeaturePolynomial.cs`, `KhaozEngine.Physics.Bepu/BoundedGeometryArithmetic.Sweep.cs`, `KhaozEngine.Physics.Bepu/SweepExactWorkspace.cs`, `KhaozEngine.Physics.Bepu/CapsuleConvexSweep.cs`, `KhaozEngine.Physics.Bepu/CapsuleGeneralSweep.cs`, `KhaozEngine.Physics.Bepu/CapsuleSweepScratch.cs`.
- Modify: `KhaozEngine.Physics.Bepu/CapsuleSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SweepBounds.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.CertifiedSweeps.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SweepMutations.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs` (installation/removal hooks only), `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.Rebase.cs`, `KhaozEngine.Physics.Bepu/QueryScratch.cs`.
- Test: create `KhaozEngine.Game.Tests/Physics/CertifiedGeneralSweepFixture.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedRotatedBoxSweepTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedSweepPolynomialTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedSweepBoundsTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedSweepLifetimeTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedSweepExactOracle.cs`.
- Modify: `KhaozEngine.Game.Tests/Physics/BepuCertifiedCapsuleSweepTests.cs` (replace only the rotated-box blanket refusal).
- Docs: `docs/design/SWIM-TASK4-CERTIFIED-SWEEP-2026-10-07.md`, `docs/design/SWIM-TASK4-CANDIDATE-COVERAGE-2026-10-07.md`, `KhaozEngine.Physics.Bepu/README.md`, `docs/USING-KHAOZENGINE.md`, documenting this family's passed domain and remaining refusals.

**Interfaces:**
- Consumes: existing `CapsuleSweepPath.Point`, `InstalledPoseOperator.TryCreate`, interval operations, exact expansion, `CapsuleSweepDistanceEncoding.Hit(Vector3,double,double,float)` and the unchanged identity-box owner.
- Produces: test-only `CertifiedSweepExactOracle.BoxEntry(BepuPhysicsWorld world, StaticHandle handle, Vector3 centre, float radius, float halfCylinderLength, Vector3 delta) -> (double Lower, double Upper)`, from independently read installed points and rational predicates outside allocation windows. Also every signature in File structure and shared interfaces. Add `bool ProveOrExpandSweepBounds(CollidableReference reference, InstalledSweepGeometry geometry, RigidPose pose)` in the new world partial, called only while mutating. Later families extend capture/dispatch, not query seams.
- Produces: `CapsuleSweepFeaturePolynomial.TryFirstContact(in SweepFeatureRegime regime, double from, double to, double maximumFractionWidth, ref CapsuleSweepWork work, SweepExactWorkspace exact, out double lower, out double? upper) -> bool`. True/null upper is full-cell separation, true/non-null is an enclosing first-event bracket, false has zero/null outputs. `SweepFeatureRegime` holds the original-operand coefficient expression and exact eligibility constraints, defined in that owner.

- [ ] **Step 1: Write the failing geometric and polynomial tests.**

Use the exact nonidentity quaternion Q=`(0.5,0.5,0.5,0.5)`, which cyclically permutes
axes, and box half extents `(0.5,0.25,0.125)` at origin. It installs world half extents
`(0.125,0.5,0.25)`. Tests and decisive assertions are:

| Exact test name | Input and assertions |
| --- | --- |
| `RotatedBoxFaceEntryHasAContainingMetreBracket` | centre `(-2,0,0)`, delta `(4,0,0)`, radius 0.25, length 1. `HitContains(result,1.625,1.625)` |
| `RotatedBoxRoundedCornerIsNotInflatedPlanes` | centre `(0.3125,0,0.4375)`, delta `(0,0.125,0)`. Corner distance squared is `2*0.1875^2 > 0.25^2`. `Clear(result,0.125f)` |
| `RotatedBoxClosedStartAndStationaryCases` | centre `(-0.375,0,0)` tangent, delta zero, +Y and -X each give exact Hit(0). At `x=MathF.BitDecrement(-0.375f)`, zero and +Y are Clear. Centre zero is stationary overlap |
| `QuadraticDoubleRootAtOneThirdIsClosedHit` | exact polynomial `9*t*t-6*t+1`, closed `[0,1]`, requested width `1/16384`. Root bracket contains `1/3`, width <= `1/16384`, and is Hit despite no negative interval |
| `QuadraticClosedEndpointAndWholeCellSeparationAreDistinct` | `(t-1)^2` on `[0,1]` is endpoint Hit, `(t-1)^2+1/16` is separated, identically zero is start Hit, zero motion uses the same closed predicate |
| `RotatedBoxOperationalMapNeverBecomesAnExactInverse` | Q=`(0,0,0.5,0.8660254)`, position `(0.28660256,0.2,0)`, exact hidden-crate half extents from `SupportFootprintScenes.HiddenCrate`. Prove first lift Clear at GS-14 input, and a face/edge/vertex entry bracket against an independently enclosed installed transform. Norm error and transpose departure remain nonzero |
| `RotatedBoxThinCrossingAndBudgetExhaustionPublishNoPrefix` | Q above replaced by exact cyclic Q, half extents `(0.5,0.25,0.015625)`, centre `(-2,0,0)`, delta `(4,0,0)` gives entry `1.734375`. In separate kernel tests pre-consume the 256-cell primitive budget before a required cell, reach depth 48 with an unproved cell, and exhaust the 4096-bit exact workspace. Each refuses with zero/null out bounds, never a partial prefix |

Write assertions as executable tests, including the exact closed and atomic result checks:

```csharp
Assert.Equal(CapsuleSweepStatus.Hit, contact.Status);
Assert.Equal(0f, contact.ClearThroughDistance);
Assert.Equal(0f, contact.ImpactDistance);
Assert.InRange(contact.CertifiedErrorMetres, 0f, 0.001f);
Assert.Equal(default(CapsuleSweepResult), exhausted);
```

Also add `RotatedBoxFarBoundsAndExcludedUnsupportedSelectionAreIndependent` with a rotated
box at X=100, then an unsupported sphere at X=100. The box permits Clear, the selected sphere
refuses, excluding the sphere permits Clear, and an invalid stored bound remains refused even
when excluded. `RotatedBoxInwardBoundsAreRepairedBeforeEvidencePublication` injects one inward
binary32 neighbor during installation, proves ancestor containment and a closed boundary hit,
then injects repair failure and requires persistent refusal after an unrelated successful add.

- [ ] **Step 2: Run RED and classify it.**

Create the repository's required gitignored feed directory before the first restore/build:

```bash
mkdir -p local-feed
build-slot --label gs1-red --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedRotatedBoxSweepTests|FullyQualifiedName~CertifiedSweepPolynomialTests|FullyQualifiedName~CertifiedSweepBoundsTests"
```

Expected: compiled behavior/missing-capability failures in the new family. A compilation or invalid
fixture failure is repaired before claiming RED. Existing identity-box assertions stay unchanged.

- [ ] **Step 3: Implement the installed-box capture and conservative bound publication.**

Implement `TryCapture`, `TryReadLeaf`, `InstalledSweepBounds.TryCreate` and
`ProveOrExpandSweepBounds` with the signatures above. Keep actual topology and installation
lifetime in the new world partial, remove it with the shape and refresh posed evidence on mutation.
Audit pinned `BroadPhase` update/refit methods before calling them. If stored bounds need expansion,
update through the owning tree path, validate ancestors and reread the result before completion.
Preserve the 1,000,000 coordinate ceiling and existing tree preflight, count and selection guards.

- [ ] **Step 4: Implement the closed convex distance kernel and bounded exact fallback.**

Implement `CapsuleConvexSweep.Sweep` and `CapsuleSweepFeaturePolynomial.TryFirstContact`.
`CapsuleSweepFeatureDistance.TryBuild(in InstalledSweepLeaf leaf, int featureIndex,
int axisRegime, Vector3 centre, float radius, float halfCylinderLength, Vector3 displacement,
CapsuleSweepScratch scratch, out SweepFeatureRegime regime) -> bool` enumerates GS-6's finite
features and parameter guards. Include axis/solid occupancy and parallel reduced strata. Use
original-fraction quadratic minima/root bounds, including exact double roots, not only point
witnesses at dyadic endpoints. Extend the shared arithmetic owner for the required exact expressions
with checked 4096-bit storage. Keep exact expansion inside its proved degree/exponent range.
Never allocate `BigInteger` on this route or erase uncertain transform coefficients.

- [ ] **Step 5: Integrate rotated dispatch, lifetime and allocation tests.**

Implement `CapsuleGeneralSweep.Sweep`, add its scratch slot, and dispatch from `CertifyCandidates`
only after all selected-bound checks pass. Continue through every candidate after Hit(0).
Add `RotatedBoxMutationAndFrameEvidenceStayCurrent` for remove/re-add, wake/sleep, velocity,
step, constraints/targets, rebase to `(16,8,-16)`, no-op rebase, stale/disposed/wrong-view leases
and failed/missed mutation. Add `RotatedBoxDimensionAndWarmQueryParity` for shape/value overloads,
radius `0.4` then `0.40000007`, length `0.59999996` then `0.6000001`, exact tangent fallback,
bounded refusal, a nested scratch loan and two simultaneous reader intervals. Assert bit-equal
results where inputs match, zero measured managed bytes and unchanged retained capacity after prime.
Update the old rotated-box refusal test to require proved far-box Clear. Keep the sphere control.

- [ ] **Step 6: Run GREEN and finish the task once.**

```bash
build-slot --label gs1-green --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedRotatedBoxSweepTests|FullyQualifiedName~CertifiedSweepPolynomialTests|FullyQualifiedName~CertifiedSweepBoundsTests|FullyQualifiedName~CertifiedSweepLifetimeTests|FullyQualifiedName~BepuCertifiedCapsuleSweepTests"
build-slot --label gs1-build --heavy -- dotnet build KhaozEngine.slnx -c Release
build-slot --label gs1-suite --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
build-slot --label gs1-format --heavy -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: all commands exit 0, zero build warnings, every new fixture discovered and no new skipped
facts. Record counts and remaining hull/mesh/compound refusals. No timing claims.

- [ ] **Step 7: Update this family's documentation, stage exact task paths, run the Global Constraints guards and commit.**

Use this explicit Bash/zsh path list, including the newly created owners:
```bash
gs_task_paths=(
  KhaozEngine.Physics.Bepu/{CapsuleGeneralSweepPolicy,InstalledSweepGeometry,BepuPhysicsWorld.InstalledSweepGeometry,InstalledSweepBounds}.cs
  KhaozEngine.Physics.Bepu/{CapsuleSweepFeatureDistance,CapsuleSweepFeaturePolynomial,BoundedGeometryArithmetic.Sweep,SweepExactWorkspace,CapsuleConvexSweep,CapsuleGeneralSweep,CapsuleSweepScratch}.cs
  KhaozEngine.Physics.Bepu/{CapsuleSweepGeometry,BepuPhysicsWorld.SweepBounds,BepuPhysicsWorld.CertifiedSweeps,BepuPhysicsWorld.SweepMutations,BepuPhysicsWorld,BepuPhysicsWorld.Rebase,QueryScratch}.cs
  KhaozEngine.Game.Tests/Physics/{CertifiedGeneralSweepFixture,CertifiedRotatedBoxSweepTests,CertifiedSweepPolynomialTests,CertifiedSweepBoundsTests,CertifiedSweepLifetimeTests,CertifiedSweepExactOracle,BepuCertifiedCapsuleSweepTests}.cs
  docs/design/SWIM-TASK4-{CERTIFIED-SWEEP,CANDIDATE-COVERAGE}-2026-10-07.md
  KhaozEngine.Physics.Bepu/README.md
  docs/USING-KHAOZENGINE.md
)
git add -- "${gs_task_paths[@]}"
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
bash scripts/check-doc-versions.sh
git commit -m "feat(physics): certify rotated box capsule sweeps (#1355)" -- "${gs_task_paths[@]}"
```

### Task 2: Certify installed convex hulls and their centroid wrappers

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/CapsuleSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepBounds.cs`, `KhaozEngine.Physics.Bepu/CapsuleGeneralSweep.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.InstalledSweepGeometry.cs`, and `KhaozEngine.Physics.Bepu/CapsuleSweepFeatureDistance.cs` only for hull topology adapters.
- Test: create `KhaozEngine.Game.Tests/Physics/CertifiedHullSweepTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedHullSweepLifetimeTests.cs`. Modify `KhaozEngine.Game.Tests/Physics/CertifiedSweepExactOracle.cs` for independent hull expectations.
- Docs: `docs/design/SWIM-TASK4-CERTIFIED-SWEEP-2026-10-07.md`, `docs/design/SWIM-TASK4-CANDIDATE-COVERAGE-2026-10-07.md`, `KhaozEngine.Physics.Bepu/README.md`, `docs/USING-KHAOZENGINE.md`, supported-domain paragraphs.

**Interfaces:**
- Consumes: Task 1's exact capture/read/bounds/convex/dispatch signatures and scratch protocol.
- Produces: `CapsuleSweepGeometry.TryCaptureHull(Shapes shapes, TypedIndex root, out InstalledSweepGeometry? geometry) -> bool`. It supports an installed hull and the exact single-hull Compound wrapper produced by `ShapeFactory`, preserving child offset and typed identity. No change to `CapsuleConvexSweep.Sweep`.
- Produces: test-only `CertifiedSweepExactOracle.HullEntry(BepuPhysicsWorld world, StaticHandle handle, Vector3 centre, float radius, float halfCylinderLength, Vector3 delta) -> (double Lower, double Upper)`. It reads installed points and independently isolates the exact event using test-only rationals, outside allocation windows.

- [ ] **Step 1: Write the failing hull tests with these exact names and expectations.**

| Exact test name | Fixture and assertion |
| --- | --- |
| `HullTetrahedronEmptyAabbCornerIsClear` | points `(0,0,0),(2,0,0),(0,2,0),(0,0,2)`, identity installation, sphere radius 0.25/length 0, centre `(1.5,1.5,1.5)`, delta `(0.125,0,0)`. `Clear(result,0.125f)` |
| `HullFaceEdgeVertexAndInteriorUseInstalledTopology` | same tetrahedron, centre `(-2,0.75,0.25)`, radius 0.25/length 1, delta `(4,0,0)` gives entry 1.75. Sphere at `(1,-0.25,0)` is edge tangency, at `(-0.25,0,0)` is vertex tangency and at `(0.25,0.25,0.25)` is interior overlap. Stationary cases give Hit(0) |
| `HullIsolatedVertexTangencyAtOneThirdCannotBeSampledAway` | points `(0,0,0),(2,2,0),(2,0,2),(2,2,2)`, sphere radius 0.25, centre `(-1,-0.25,0)`, delta `(3,0,0)`. Isolated first event at fraction 1/3, distance 1. `HitContains(result,1,1)` |
| `HullWrapperAndReusedHandleKeepInstalledVertexIdentity` | install translated tetrahedron at `(3,2,-4)` through public `AddStatic`, verify one-hull wrapper and centroid compensation, remove and re-add a different tetrahedron at a reused native index. New event matches independently read new points, never the previous topology |
| `FarHullAndMobilityExclusionsNoLongerPoisonClear` | public hull at X=100 permits unrelated Clear. Dynamic hull under Dynamics filter is certified at its lease pose. Static-only and actual view exclusions match the same selected set |
| `HullUncertainTopologyAndLateUnresolvedLeafRefuseAtomically` | inject malformed face index, unresolved feature guard, and 4096-bit overflow in separate bounded fixtures. Each result is exactly default, even with an earlier identity-box Hit(0) |

```csharp
CertifiedGeneralSweepFixture.HitContains(face, 1.75, 1.75);
CertifiedGeneralSweepFixture.HitContains(isolatedTangent, 1, 1);
Assert.Equal(default(CapsuleSweepResult), malformed);
Assert.Equal(expectedInstalledVertexBits, observedInstalledVertexBits);
```

`CertifiedHullSweepLifetimeTests.HullMutationDimensionAndExactFallbackAllocateNothing` covers
shape/value parity, a frame Y offset 8, rebase/remove/re-add, dynamic sleep/wake, and zero bytes
for a warm exact tangency and warm refusal. Preserve Task 1's mutation failure and capacity guards.

Add `HullClosedPathAndTangentialMotionUseSharedContract` on the first tetrahedron, sphere radius
0.25/length 0. At `(-0.25,0.5,0.5)`, zero delta, `(0,0.125,0)` and `(-0.125,0,0)` each give
Hit(0). At `x=MathF.BitDecrement(-0.25f)`, delta `(0,0.125,0)` is Clear through 0.125.
From `(-1.25,0.5,0.5)` with delta `(1,0,0)`, assert a closed endpoint Hit containing distance 1.

- [ ] **Step 2: Run RED.**

```bash
build-slot --label gs2-red --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedHullSweepLifetimeTests"
```

Expected: hull behavior fails on the selected-bound/unsupported wrapper gate after valid setup.

- [ ] **Step 3: Implement `TryCaptureHull` and route complete hull proof.**

Read actual Bepu hull face loops/points and validate the GS-3 topology caps. Capture the real
single-child wrapper and offset. Reuse the convex kernel for every finite feature and interior
regime. Publish conservative wrapper/root bounds at mutation completion and include all copied
identity/lifetime checks. General compounds still refuse. Do not infer a hull from its AABB.

- [ ] **Step 4: Run GREEN and the once-only task finish.**

```bash
build-slot --label gs2-green --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedHullSweepLifetimeTests|FullyQualifiedName~CertifiedRotatedBoxSweepTests"
build-slot --label gs2-build --heavy -- dotnet build KhaozEngine.slnx -c Release
build-slot --label gs2-suite --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
build-slot --label gs2-format --heavy -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: exit 0, zero warnings, full suite passes. Document supported hull wrappers, point/pose
domain and remaining mesh/mixed-compound refusals with actual counts.

- [ ] **Step 5: Update family docs, stage exact Files paths, run the Global Constraints guards and commit.**

```bash
gs_task_paths=(
  KhaozEngine.Physics.Bepu/{CapsuleSweepGeometry,InstalledSweepGeometry,InstalledSweepBounds,CapsuleGeneralSweep,BepuPhysicsWorld.InstalledSweepGeometry,CapsuleSweepFeatureDistance}.cs
  KhaozEngine.Game.Tests/Physics/{CertifiedHullSweepTests,CertifiedHullSweepLifetimeTests,CertifiedSweepExactOracle}.cs
  docs/design/SWIM-TASK4-{CERTIFIED-SWEEP,CANDIDATE-COVERAGE}-2026-10-07.md
  KhaozEngine.Physics.Bepu/README.md
  docs/USING-KHAOZENGINE.md
)
git add -- "${gs_task_paths[@]}"
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
bash scripts/check-doc-versions.sh
git commit -m "feat(physics): certify installed hull capsule sweeps (#1355)" -- "${gs_task_paths[@]}"
```

### Task 3: Certify finite one-sided mesh triangles and traversal

**Files:**
- Create: `KhaozEngine.Physics.Bepu/CapsuleTriangleSweep.cs`, `KhaozEngine.Physics.Bepu/CapsuleMeshSweep.cs`.
- Modify: `KhaozEngine.Physics.Bepu/CapsuleSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepBounds.cs`, `KhaozEngine.Physics.Bepu/CapsuleGeneralSweep.cs`, `KhaozEngine.Physics.Bepu/CapsuleSweepScratch.cs` under that package for mesh topology/scale and inner traversal.
- Test: create `KhaozEngine.Game.Tests/Physics/CertifiedMeshSweepTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedMeshSweepCoverageTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedMeshSweepLifetimeTests.cs`. Modify `KhaozEngine.Game.Tests/Physics/CertifiedSweepExactOracle.cs` for independent triangle expectations.
- Read and retain: `KhaozEngine.Game.Tests/Physics/SweepBackFaceCullingTests.cs`, `KhaozEngine.Physics.Bepu/MeshSweepNearestHitTests.cs` and pinned Bepu 2.4.0 triangle/mesh/contact/sweep source.
- Docs: `docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md`, `docs/design/SWIM-TASK4-CERTIFIED-SWEEP-2026-10-07.md`, `docs/design/SWIM-TASK4-CANDIDATE-COVERAGE-2026-10-07.md`, `KhaozEngine.Physics.Bepu/README.md`, `docs/USING-KHAOZENGINE.md`, including the exact side/filter matrix.

**Interfaces:**
- Consumes: Task 1's convex feature/polynomial arithmetic, work counters, bounds and scratch, Task 2's independent test-oracle approach.
- Produces: `CapsuleTriangleSweep.Sweep(in InstalledSweepLeaf triangle, Vector3 centre, float radius, float halfCylinderLength, Vector3 displacement, QueryFilter filter, float maximumErrorMetres, ref CapsuleSweepWork work, CapsuleSweepScratch scratch) -> CapsuleSweepResult`.
- Produces: `CapsuleMeshSweep.Sweep(Shapes shapes, TypedIndex root, RigidPose pose, InstalledSweepGeometry geometry, in CapsuleQueryDimensions capsule, Vector3 centre, Vector3 displacement, QueryFilter filter, ref CapsuleSweepWork work, CapsuleSweepScratch scratch) -> CapsuleSweepResult`. Add a mesh branch to Task 1's capture/dispatch, preserving signatures.

- [ ] **Step 1: Write failing closed-triangle, side-policy and coverage tests.**

The reference up-facing quad at Y=0 is `(-2,0,-2),(2,0,-2),(-2,0,2),(2,0,2)` with
indices `[0,1,2,1,3,2]`. Reverse B/C for the back-facing variant. Use `Cross(C-A,B-A)`.

| Exact test name | Exact values and assertions |
| --- | --- |
| `MeshFrontFaceEntryHasAContainingMetreBracket` | radius 0.25, length 1, centre `(-0.5,2,-0.25)`, delta `(0,-4,0)`. Entry distance 1.25 for an up-facing quad, culling true and false |
| `MeshSideAndStationaryEligibilityUsePinnedPolicy` | same front start and reversed winding. Culling true passes the down-facing quad, culling false retains the raw back-face blocking row at 1.25. At centre `(-0.5,0.75,-0.25)` zero delta is a front closed tangency Hit(0). At centre Y=0.875 zero/+X stays separated. Vertical quad normal +Z on a -Y sweep is culled when requested |
| `MeshFiniteEdgeVertexAndThinInteriorTriangleAreNotLost` | triangle `(0,-2,-2),(0,-2,2),(0,2,-2)`, centre `(2,0,-0.5)`, delta `(-4,0,0)`, radius 0.25/length 1, first face entry 1.75. Add a triangle with edge length 0.015625 strictly between separated endpoints. Independent face/edge/vertex oracle must be enclosed |
| `MeshConcaveAndConvexSeamsKeepRawTriangleIdentity` | quads meeting at 90 degrees, both convex and concave orientation, coincident boundary edge, reversed triangle/insertion order. Brackets agree, raw triangle IDs remain distinct, and a seam cannot suppress a required leaf |
| `MeshScaledRotatedBoundsCoverEveryRequiredTriangle` | installed scale `(2,0.5,1.5)`, exact cyclic quaternion and the 60-degree rounded quaternion from Task 1. Independent installed triangle oracle is contained, including a closed tree-bound neighbor. Scale zero, negative and NaN refuse |
| `MeshInnerTraversalExhaustionAndLateFailurePublishNoPrefix` | valid synthetic inner trees test 8192 node, 4096 leaf and 128 pending ceilings with a smaller injected limit for focused refusal. A query with 4097 relevant triangles returns default without inferring which stage refused. Separately inject a shared primitive budget of 1 against two required triangles and assert the sticky counter/refusal. A late unresolved triangle after Hit(0) still gives default |

For exact contact boundaries assert:

```csharp
CertifiedGeneralSweepFixture.HitContains(front, 1.25, 1.25);
CertifiedGeneralSweepFixture.Clear(culledBack, 4f);
Assert.Equal(0f, stationary.ImpactDistance);
Assert.Equal(default(CapsuleSweepResult), lateFailure);
```

Add `FarMeshCannotPoisonClearButInwardInnerBoundsCannotHideGeometry`: supported mesh at X=100
permits unrelated Clear, an injected inward inner bound is repaired/proved at mutation or leaves
evidence refused, and an invalid bound on an excluded mesh still cannot hide structural failure.
Add `MeshMutationDimensionAndWarmExactQueriesAllocateNothing` with rebase `(16,8,-16)`,
remove/re-add, both dimension overloads, exact edge tangency, bounded refusal and zero warm bytes.
Mesh dynamics remain rejected by the installer, do not invent a dynamic-mesh fixture.
Extend `MeshSideAndStationaryEligibilityUsePinnedPolicy` on the up-facing quad: at centre
`(-0.5,0.75,-0.25)`, tangential delta `(0.125,0,0)` and moving-away delta `(0,0.125,0)`
retain closed Hit(0). Centre Y=0.625 is initial front overlap. From centre Y=1.75 with delta
`(0,-1,0)`, the exact closed endpoint is Hit at distance 1. Check the source-derived stationary
policy separately from direction-based culling, with no new side tolerance.

- [ ] **Step 2: Run RED.**

```bash
build-slot --label gs3-red --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedMeshSweepTests|FullyQualifiedName~CertifiedMeshSweepCoverageTests|FullyQualifiedName~CertifiedMeshSweepLifetimeTests"
```

Expected: compiled failures at the still-unsupported mesh gate, with valid raw winding controls.

- [ ] **Step 3: Implement `CapsuleTriangleSweep.Sweep` and pin side eligibility.**

Read pinned Bepu capsule/triangle sweep and contact testers and the engine filter before writing
side logic. Record stationary, edge, back-face and `CullBackFaces` decisions with source symbols
in the spec. Apply the retained policy to exact feature eligibility over the closed path. Reuse
the distance/root kernel without treating the triangle as a closed volume. Unproved operational
side agreement refuses, including a contact/sweep disagreement the numerical budget cannot contain.

- [ ] **Step 4: Implement `CapsuleMeshSweep.Sweep` and conservative traversal.**

Capture installed triangle identity and strictly positive scale at installation. Certify root and
inner bounds, closed pruning and checked traversal before visiting triangles. Count examined nodes,
pending branches and primitive descendants even after a tentative Hit. Retain one path/lease and
aggregate only after every required triangle completes. Provision bounded traversal workspace once.

- [ ] **Step 5: Run GREEN and the once-only task finish.**

```bash
build-slot --label gs3-green --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedMeshSweep|FullyQualifiedName~SweepBackFaceCullingTests|FullyQualifiedName~MeshSweepNearestHitTests"
build-slot --label gs3-build --heavy -- dotnet build KhaozEngine.slnx -c Release
build-slot --label gs3-suite --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
build-slot --label gs3-format --heavy -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: exit 0, zero warnings, raw sweep/culling controls and full Game.Tests pass. All new mesh
facts are discovered. Record exact remaining scale/degeneracy/transform/work refusals.

- [ ] **Step 6: Update family docs, stage exact Files paths, run the Global Constraints guards and commit.**

```bash
gs_task_paths=(
  KhaozEngine.Physics.Bepu/{CapsuleTriangleSweep,CapsuleMeshSweep,CapsuleSweepGeometry,InstalledSweepGeometry,InstalledSweepBounds,CapsuleGeneralSweep,CapsuleSweepScratch}.cs
  KhaozEngine.Game.Tests/Physics/{CertifiedMeshSweepTests,CertifiedMeshSweepCoverageTests,CertifiedMeshSweepLifetimeTests,CertifiedSweepExactOracle}.cs
  docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md
  docs/design/SWIM-TASK4-{CERTIFIED-SWEEP,CANDIDATE-COVERAGE}-2026-10-07.md
  KhaozEngine.Physics.Bepu/README.md
  docs/USING-KHAOZENGINE.md
)
git add -- "${gs_task_paths[@]}"
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
bash scripts/check-doc-versions.sh
git commit -m "feat(physics): certify finite mesh capsule sweeps (#1355)" -- "${gs_task_paths[@]}"
```

### Task 4: Certify all supported compound descendants atomically

**Files:**
- Create: `KhaozEngine.Physics.Bepu/CapsuleCompoundSweep.cs`.
- Modify: `KhaozEngine.Physics.Bepu/CapsuleSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSweepBounds.cs`, `KhaozEngine.Physics.Bepu/CapsuleGeneralSweep.cs`, `KhaozEngine.Physics.Bepu/CapsuleSweepScratch.cs` under that package.
- Test: create `KhaozEngine.Game.Tests/Physics/CertifiedCompoundSweepTests.cs`, `KhaozEngine.Game.Tests/Physics/CertifiedCompoundSweepLifetimeTests.cs`.
- Read only: `KhaozEngine.Physics.Bepu/ShapeFactory.cs`, including `AddFlattenedChild` and dynamic mirrors.
- Docs: `docs/design/SWIM-TASK4-CERTIFIED-SWEEP-2026-10-07.md`, `docs/design/SWIM-TASK4-CANDIDATE-COVERAGE-2026-10-07.md`, `KhaozEngine.Physics.Bepu/README.md`, `docs/USING-KHAOZENGINE.md`, family and remaining-refusal paragraphs.

**Interfaces:**
- Consumes: Tasks 1 and 2's immutable leaf reader and convex sweeper. Mesh root support from Task 3 remains independent.
- Produces: `CapsuleCompoundSweep.Sweep(Shapes shapes, TypedIndex root, RigidPose pose, InstalledSweepGeometry geometry, in CapsuleQueryDimensions capsule, Vector3 centre, Vector3 displacement, QueryFilter filter, ref CapsuleSweepWork work, CapsuleSweepScratch scratch) -> CapsuleSweepResult`.
- Extends capture to the existing flat installed Compound of box/hull leaves. Nested seam compounds are already flattened. Do not change `ShapeFactory`'s mesh-child refusal or introduce BigCompound support.

- [ ] **Step 1: Write failing representation, ordering and refusal tests.**

| Exact test name | Exact values and assertions |
| --- | --- |
| `CompoundDescendantsMatchSplitInstalledGeometry` | box half `(0.5,0.25,0.125)` with cyclic Q at X=0, tetrahedron from Task 2 at X=2. Capsule centre `(-2,0,0)`, delta `(8,0,0)`, radius 0.25/length 1. First distance 1.625. Split, compound and reversed child order give identical complete brackets |
| `NestedSeamCompoundUsesActualFlattenedChildPoses` | parent translation `(3,2,-4)`, parent cyclic Q, nested child translation `(0.25,0.5,-0.125)` and 60-degree rounded Q. Assert installed flat child count and exact read-back poses. Independently enclosed transformed event is in the bracket, no duplicated hull centering |
| `CompoundHitZeroCannotHideARefusedDescendant` | first child overlaps at centre zero, second is an unsupported sphere radius 0.5 at X=100. Result is default. Replace sphere with a supported leaf whose exact budget is exhausted and require the same default |
| `CompoundCapacityIsAtomicAtSixtyFiveChildren` | 64 supported leaves within the GS-3 root cap may certify, 65 refuse without a prefix. Also exercise the aggregate 4096 descendant cap across separate roots with small injected work limits |
| `CompoundMeshChildRemainsAnInstallationRefusal` | public `CompoundShape` containing one mesh remains `NotSupportedException` before any supported-family certificate is claimed. The test does not bypass the installer |
| `CompoundBoundsCannotPruneARequiredRemoteChild` | one child translated X=100 inside a root at origin, query through the translated child. Root union and child bounds contain the complete child, malformed or inward bounds cannot certify Clear |

```csharp
CertifiedGeneralSweepFixture.HitContains(split, 1.625, 1.625);
Assert.Equal(split, compound);
Assert.Equal(compound, reversed);
Assert.Equal(default(CapsuleSweepResult), hitThenUnknown);
Assert.Equal(default(CapsuleSweepResult), sixtyFive);
```

Add `CompoundMutationAndConcurrentScratchKeepInstallationLifetime`: public static/dynamic compounds,
Step, velocity/constraint wake, sleep transfer, rebase `(16,8,-16)`, no-op, removal/handle reuse,
wrong/disposed lease/view and separate concurrent scratch. Add
`CompoundWarmDimensionAndRefusalQueriesAllocateNothing` for exact overload parity, changed rounding
dimensions, late refusal and zero managed bytes after priming, without cache growth.
Add `CompoundClosedStationaryAndTangentialEventsMatchSplitLeaves` for a supported cyclic-Q box
and a distant supported hull. At centre `(-0.375,0,0)`, zero delta, +Y and -X give Hit(0).
At `x=MathF.BitDecrement(-0.375f)`, delta `(0,0.125,0)` is Clear through 0.125. From centre
`(-1.375,0,0)`, delta `(1,0,0)` has a closed endpoint event at distance 1. Repeat with split
statics and reversed children, including the all-supported initial-overlap case.

- [ ] **Step 2: Run RED.**

```bash
build-slot --label gs4-red --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedCompoundSweepTests|FullyQualifiedName~CertifiedCompoundSweepLifetimeTests"
```

Expected: mixed-compound assertions fail on the current family gate after real flattening succeeds.

- [ ] **Step 3: Implement `CapsuleCompoundSweep.Sweep` and extend installed capture/bounds.**

Read every actual child shape and pose, preserve stable raw leaf identity, compose through the
shared transform proof and certify union bounds at mutation completion. Reuse the box/hull leaf
owners, count every required child against the shared work object and defer publication until all
finish. Preserve unsupported descendants as whole-query refusal even after Hit(0). No recursion
through an unsupported installed shape, no unbounded nested traversal or result caching.

- [ ] **Step 4: Run GREEN and the once-only task finish.**

```bash
build-slot --label gs4-green --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CertifiedCompoundSweepTests|FullyQualifiedName~CertifiedCompoundSweepLifetimeTests|FullyQualifiedName~CertifiedHullSweepTests|FullyQualifiedName~CertifiedRotatedBoxSweepTests"
build-slot --label gs4-build --heavy -- dotnet build KhaozEngine.slnx -c Release
build-slot --label gs4-suite --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
build-slot --label gs4-format --heavy -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: exit 0, zero warnings, every supported descendant completes, all existing installation
refusals and full suite remain correct.

- [ ] **Step 5: Update family docs, stage exact Files paths, run the Global Constraints guards and commit.**

```bash
gs_task_paths=(
  KhaozEngine.Physics.Bepu/{CapsuleCompoundSweep,CapsuleSweepGeometry,InstalledSweepGeometry,InstalledSweepBounds,CapsuleGeneralSweep,CapsuleSweepScratch}.cs
  KhaozEngine.Game.Tests/Physics/{CertifiedCompoundSweepTests,CertifiedCompoundSweepLifetimeTests}.cs
  docs/design/SWIM-TASK4-{CERTIFIED-SWEEP,CANDIDATE-COVERAGE}-2026-10-07.md
  KhaozEngine.Physics.Bepu/README.md
  docs/USING-KHAOZENGINE.md
)
git add -- "${gs_task_paths[@]}"
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
bash scripts/check-doc-versions.sh
git commit -m "feat(physics): certify compound capsule sweep descendants (#1355)" -- "${gs_task_paths[@]}"
```

### Task 5: Prove acquired explicit ticks on all four cost scenes

**Files:**
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/GeneralSweepExplicitCostTests.cs`, `KhaozEngine.Game.Tests/Locomotion/Contacts/GeneralSweepExplicitCostTests.Allocation.cs`.
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/CountingQueryView.cs` only for value counters/last certified request observation needed by these tests. No stack strings or diagnostic allocations in measured queries.
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportCostExecution.cs` only to expose its actual acquisition status as a value. Read the existing cost scenes, roof oracle, acquisition fixture and lifetime-allocation tests without changing them.
- Docs: append actual correctness/allocation evidence to `docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md`. No cost artifact or SG-12 verdict edits.

**Interfaces:**
- Consumes: existing `SupportCostExecution(SupportCostRow,string)`, `Invoke`, `Producer`, `View`, `Scene`, `LastMovementLease` and `SupportCostRoofOracle.Limit`.
- Produces: mandatory correctness fixtures using real `Acquire` and real explicit stepping. No production signature changes. Request observation is a fixed-size value in `CountingQueryView`, forwarded results unchanged. Add test-only `MovementAvailability? LastAcquisitionStatus { get; private set; }` to `SupportCostExecution`, assigned the returned status immediately after `environment.Acquire` and before branching.

- [ ] **Step 1: Write the failing mandatory explicit assertions and first-lift controls.**

`AcquiredExplicitCostRowsAdvanceBeforeAllocationVerdict` is a four-row theory with exact names
`stairs box`, `stairs hull`, `stairs mesh`, `hidden crate`. Select the existing row and use
`with { Explicit = true, MustPass = true }`, operation `ExplicitTick`. Do not change the cost
sampler's original advisory rows. At dt `1f/30f`, stairs feet `(0.525,0.5,0)` and crate feet
`(0,0,0)`, require:

```csharp
Assert.NotNull(run.LastMovementLease); // real acquisition reached the explicit kernel
Assert.Equal(MovementAvailability.Known, run.LastAcquisitionStatus);
Assert.Equal(MovementStepOutcome.Advanced, result.StepOutcome);
Assert.False(result.Refused);
Assert.True(result.GeometryPassed);
Assert.True(result.Motion.X > 0);
Assert.True(counters.GroundSubsteps > 0);
Assert.True(eachSupportReadMatchesIndependentOracle);
Assert.True(finalFootprintAndRoofMatchIndependentOracle);
Assert.True(result.Motion.Y <= SupportGateCostScenes.Tuning.MaxStepClimbSpeed * row.Dt);
```

Check the actual acquisition status through the value observer, not by inferring Known from no
exception. Apply `run.CheckGeometry(output, readsMatch)` to the captured support-read comparisons.
The existing per-read footprint and final roof oracles judge allowed
pacing and height bounds, never a widened vertical epsilon. Stairs inputs already have
`StairSignal=true`, so record and assert a consumed look-ahead query at its expected forward axis.

`FirstLiftKeepsBothExactDimensionPairs` uses both GS-14 pairs `(0.4,0.59999996)` and
`(0.40000007,0.6000001)`, centres `(0.525,1.6010002,0)` or `(0,1.1010001,0)`, and
delta `(0,0.4,0)`. Assert Clear through exactly 0.4, null impact, error <=0.001. At the original
dimensions and margin 0 require complete contacts, Written=0 and Required=0. Exercise owner
and actual empty-exclusion restricted view, shape and value overloads.

`BorrowedExplicitCostRowsMatchAcquiredWholeState` runs `ExplicitTickBorrowed` with a separately
acquired composed lease on the same four rows. Assert full MoveState, outcome, motion, support
identity and query counters agree. `GroundTickBorrowed` is never the expected authoritative leg.

- [ ] **Step 2: Run the mandatory acceptance tests before adding any adaptation.**

```bash
build-slot --label gs5-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~GeneralSweepExplicitCostTests"
```

This is an integration acceptance gate. Tasks 1 to 4 may already make it GREEN. If it is RED,
trace the first refused exact request under the real lease, including zero substeps. Do not invent
a change solely to obtain RED, replace the fixture with a dry route or relax the assertion.

- [ ] **Step 3: Add real-route allocation and refusal regressions.**

`AcquiredGeneralGeometryTicksAllocateOnlyNamedLifetimeTokens` measures each successful scene
after cold/prime through `AllocationMeasurement`, including fresh acquisition and disposal.
Compare bytes with independently inventoried movement/release binding, physics and producer pin
tokens (current arm64 total 312, components 192/64/56), without hardcoding that layout elsewhere.
Assert no query-dependent growth as Find/shell/sample counts differ. Separately test borrowed
queries at zero bytes. Do not call `SupportCostSampler.Measure`, which also samples timing.

`GeneralSweepExplicitScopeAndRawSupportGuardsRemainAtomic` runs an undersized scope, stale pin,
wrong frame and missing canonical binding. Assert existing EnvironmentUnresolved/Invalid hold,
unchanged input state and no backend call for the undersized scope. Re-run the named SG-6 guards,
256/257 member sentinel tests and existing raw provenance rows without editing their expectations.
Any remaining backend defect is fixed in its geometry owner with its own focused RED, while the
five frozen Locomotion owners remain unchanged.

- [ ] **Step 4: Run acceptance GREEN and the once-only task finish.**

```bash
build-slot --label gs5-green --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~GeneralSweepExplicitCostTests|FullyQualifiedName~FootSupportNeighborhoodTests|FullyQualifiedName~FootSupportBoundaryTests|FullyQualifiedName~FootSupportCompletenessTests|FullyQualifiedName~ContactLeasedQueryTests"
build-slot --label gs5-build --heavy -- dotnet build KhaozEngine.slnx -c Release
build-slot --label gs5-suite --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
build-slot --label gs5-format --heavy -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: exit 0, all four acquired rows Advanced, borrowed explicit parity, successful geometry
before token-only allocation verdicts, unchanged physical refusals and zero warnings. Record
actual test counts, first-lift inputs, substeps, consumed signal counts and token bytes.

- [ ] **Step 5: Stage exact Files paths, run the Global Constraints guards and commit.**

```bash
gs_task_paths=(
  KhaozEngine.Game.Tests/Locomotion/Contacts/{GeneralSweepExplicitCostTests,GeneralSweepExplicitCostTests.Allocation,CountingQueryView,SupportCostExecution}.cs
  docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md
)
git add -- "${gs_task_paths[@]}"
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
bash scripts/check-doc-versions.sh
git commit -m "test(physics): prove acquired explicit general sweep ticks (#1355)" -- "${gs_task_paths[@]}"
```

### Task 6: Prove captured Grimhollow hull stairs and publish the final contract

**Files:**
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/grimhollow-stairs-collision.json`, `KhaozEngine.Game.Tests/Locomotion/Contacts/CapturedStairSweepFixture.cs`, `KhaozEngine.Game.Tests/Locomotion/Contacts/CapturedStairExplicitSweepTests.cs`, `KhaozEngine.Game.Tests/Locomotion/Contacts/CapturedStairExplicitSweepTests.Lifetime.cs` in that Contacts directory (the JSON alone is under Fixtures).
- Modify: `KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj` only if needed to copy/embed the JSON by repository convention.
- Modify: `KhaozEngine.Game.Tests/Physics/CertifiedSweepExactOracle.cs` for the independent closed arrival-ball predicate.
- Modify: `docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md`, `docs/design/SWIM-TASK4-CERTIFIED-SWEEP-2026-10-07.md`, `docs/design/SWIM-TASK4-CANDIDATE-COVERAGE-2026-10-07.md`, `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md`, `docs/design/CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md`, `docs/INDEX.md`, `KhaozEngine.Physics.Bepu/README.md`, `docs/USING-KHAOZENGINE.md`, `CHANGELOG.md`.
- Read only: `Directory.Build.props` and current release tags. No game checkout or content changes.

**Interfaces:**
- Consumes: Tasks 1 to 4's public certified backend and Task 5's acquired acceptance requirements.
- Produces: test-only `CapturedStairSweepFixture(bool compound, Quaternion yaw, Vector3 origin)` with `BepuPhysicsWorld World`, `EnvironmentAcquisitionFixture Producer`, `MovementQueryScope Scope`, `MoveTuning Tuning`, `FramedMovementState StateAtFeet(Vector3 feet)`, and `MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, float dt)`.
- `StepTowards` acquires through `Producer.Acquire`/real `MovementEnvironmentContext.TryAcquire`, asserts Known, invokes public `ExplicitCharacterMovement.StepTowards` with the acquired lease and disposes it on the same thread. No prebuilt/scripted lease and no dry fallback. `StateAtFeet` takes absolute world feet and converts through the actual frame/origin. Convert final support feet back before comparing with an absolute target.
- Produces: test-only `CertifiedSweepExactOracle.WithinArrivalBall(Vector3 actual, Vector3 target, float radius) -> bool`, using independent exact dyadic squared-distance comparison. Pass the unchanged represented radius `0.001f`, never an axis-wise tolerance.

Build the producer after all statics are installed, over an actual empty-exclusion restricted view.
Use `MovementSpaceKey("world","room")`, query identity
`MovementQueryIdentity("certified-general-sweeps",1,"captured-stairs")`, the current physics frame
and a witnessed physics-local scope from `(-1500,-30,-30)` to `(1500,30,30)`, MaxRise=20 and
MaxDrop=20, as in the investigated acquisition route. The dry body/coverage callbacks return
Known for that room with no water. The analytic-ground callback returns Known with null. Bind
support only when the actual `(Static,FeatureId)` is in the captured registration, to canonical
owner `("world","stairs")`, or the separate `approach-pad`/`landing-pad` owners. Do not return
a constant successful binding for an unknown raw member. Expire the pin/registration with the
real geometry generation after mutation. The undersized-scope refusal test explicitly replaces
this broad scope and must not enlarge itself.

- [ ] **Step 1: Capture the exact eight hull parts and write the failing provenance/first-lift tests.**

The portable JSON records source repository `APKiwiOrg/Grimhollow`, source path
`assets/models/kit/stairs.glb`, SHA-256
`f5ce2f2853608907e0bf6ae415969b1ef2fb70b8faf1adf146a60c4d67126af9`, identity node transforms,
eight meshes, 24 POSITION entries and eight unique vertices per mesh. Materialize the eight corners
from each exact float min/max in GS-15 into explicit hull points, preserving float round-trip bits.
The test runs from the committed fixture without access to another checkout or the private GLB.
`CapturedStairSourceAndFloatBitsAreStable` asserts every bound, vertex count and source hash,
including Y values `0,0.22,0.25,0.47,0.5,0.72,0.75,0.97,1` without retuning.

`CapturedStairPlayerScaleFirstLiftIsCertified` uses tuning radius 0.3, half-height 0.75,
StepHeight 0.4, FootRadiusFraction 0.5, shell radius 0.3/length 0.5, feet `(0,0.5,-0.25)`,
explicit offset 0.001 and actual represented centre `(0,1.4510001,-0.25)`. Assert complete
zero-margin zero contacts, raw upward miss as a control, and certified Clear through 0.4. Raw miss
does not supply the certificate. Test split hull statics and the real hull compound installer.

- [ ] **Step 2: Write acquired explicit movement, arrival and lifetime tests.**

`CapturedStairAcquiredTickAdvancesTowardsPositiveZ` uses the exact starting feet above,
dt `1f/30f` and direction `(0,1)`. Require Known acquisition, Advanced, Grounded or SupportGranted,
positive Z motion, nonzero substeps, independent installed-footprint/roof agreement and raw provenance.

`CapturedStairAscentAndDescentReachTheThreeDimensionalMillimetreBall` runs one bounded functional
simulation per direction, at most 240 ticks at dt `1f/30f`. Add explicitly labelled approach and
landing pads outside the captured geometry: X `[-0.5,0.5]`, Z `[-2,-1]` at top Y=0 and Z `[1,2]`
at top Y=1, thickness 0.25. Do not alter any captured part. Start feet `(0,0,-1.25)`, target
`(0,1,1.25)` for ascent, reverse for descent. Acquire/dispose each tick. Command remaining XZ
divided by `WalkSpeed*dt`, clamped to magnitude 1, through the precise `StepTowards` path.
After horizontal arrival allow idle ticks for the existing vertical pacing to settle. Keep the
0.001 explicit offset separate from support feet before evaluating the final 3D distance:

```csharp
Assert.True(ticks <= 240);
Assert.True(CertifiedSweepExactOracle.WithinArrivalBall(
    finalSupportFeet, targetSupportFeet, 0.001f));
Assert.All(steps, step => Assert.Equal(MovementStepOutcome.Advanced, step.Outcome));
Assert.True(allRawMembersAndCanonicalBindingsMatchInstalledOracle);
```

`CapturedStairArrivalOracleRejectsThePerAxisCube` independently checks that offset
`(0.00075f,0.00075f,0.00075f)` fails although each component is below 0.001. Offset
`(0.001f,0,0)` passes the closed boundary and `(MathF.BitIncrement(0.001f),0,0)` fails.
Do not call production arrival logic to supply these expectations.

Run both directions for split and compound representations, yaw identity and 90 degrees about Y,
origin zero and physics origin `(16,8,-16)`. Rotate start, target and directions consistently.
Compare support heights/owners against independently read installed geometry, not rotated authored
decimals. The explicit resting offset is tested as 0.001, not absorbed into an arrival epsilon.

`CapturedStairRegistrationAndLeaseFailuresHoldAtomically` binds `(request.Static,request.FeatureId)`
against a captured provenance map to the canonical staircase owner. Remove the selected part,
re-add at reused handle, invalidate registration, expire/dispose the lease and undersize the scope
in separate fixtures. Assert refusal/hold with unchanged state and no out-of-scope backend read.
`CapturedStairWarmAcquisitionRetainsOnlyNamedTokenBytes` uses cold/prime/one measured real acquired
successful tick, both representations, with the live named-token inventory and zero borrowed query
bytes. No timing instrumentation or loop of measured runs.

- [ ] **Step 3: Run the captured-content acceptance tests.**

```bash
build-slot --label gs6-red --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CapturedStairExplicitSweepTests"
```

Expected: fixtures compile and exercise the real installed hulls and acquisition. The prior tasks
may make the new acceptance tests GREEN immediately. If any fails, trace the first refused request
and repair the relevant backend proof with a focused failing test. Do not adjust game tuning,
contacts, arrival, footprint or the captured geometry to force a pass.

- [ ] **Step 4: Publish the exact proved domain and the staged-version changelog bullet.**

Sweep Markdown for `IPhysicsCapsuleSweep`, `CapsuleBoxSweep`, `TryReadIdentityBox`, identity-box
development claims and `#1355`. Update GS-16's living docs and historical status paragraphs to name
each proved family, limits, culling policy and remaining refusals. Remove phase 5's #1355 deferral
only when these tests pass. Link the evidence without implying SG-12/#1334, native G1b, navigation,
release or consumer adoption acceptance. Preserve P5A-2, P5A-9, P5A-14 and A2/A3/A5.

Read `Directory.Build.props` and current tags again. Baseline is staged 21.4.1, but append to the
then-staged heading rather than copying that number. If it has already been tagged, report the
staged-version conflict to the orchestrator, do not silently bump. Add exactly this bullet, with
normal wrapping, and do not change the version knob:

```text
- `IPhysicsCapsuleSweep` certifies rotated boxes, convex hulls, one-sided mesh triangles and supported
  compound descendants, so authoritative contact ticks can advance over authored geometry (#1355).
```

- [ ] **Step 5: Run final focused regressions and the once-only task finish.**

```bash
build-slot --label gs6-green --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~GeneralSweepExplicitCostTests|FullyQualifiedName~GroundScenarioTests|FullyQualifiedName~ExplicitCharacterMovementTests|FullyQualifiedName~ContactControllerTests|FullyQualifiedName~ContactCompositionTests"
build-slot --label gs6-build --heavy -- dotnet build KhaozEngine.slnx -c Release
build-slot --label gs6-suite --heavy -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
build-slot --label gs6-format --heavy -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: all exit 0, zero warnings, new tests discovered with no skips. Full Game.Tests includes
phase 3 air/state, phase 4a signals, phase 4b handoff/placement, phase 5 ordinary selector,
prediction/server replay parity, legacy profiles and navigation identity guards. Record their
actual outcomes, not only the targeted filter's result. Retain real lift, arrival, provenance,
lease and allocation evidence. A functional 240-tick bound is not timing/load permission.

- [ ] **Step 6: Inspect the whole branch, stage exact Files paths, run the Global Constraints guards and commit.**

Confirm the five frozen Locomotion owners and `Directory.Build.props` have no diff. Confirm no
test epsilon, physical skin, cap or raw feature identity changed. Report remaining unsupported
geometry and separate adoption/cost gates to the orchestrator. Commit with the explicit Files list:
```bash
gs_task_paths=(
  KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/grimhollow-stairs-collision.json
  KhaozEngine.Game.Tests/Locomotion/Contacts/{CapturedStairSweepFixture,CapturedStairExplicitSweepTests,CapturedStairExplicitSweepTests.Lifetime}.cs
  KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj
  KhaozEngine.Game.Tests/Physics/CertifiedSweepExactOracle.cs
  docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md
  docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md
  docs/design/CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md
  docs/INDEX.md
  CHANGELOG.md
  docs/design/SWIM-TASK4-{CERTIFIED-SWEEP,CANDIDATE-COVERAGE}-2026-10-07.md
  KhaozEngine.Physics.Bepu/README.md
  docs/USING-KHAOZENGINE.md
)
git add -- "${gs_task_paths[@]}"
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
bash scripts/check-doc-versions.sh
git commit -m "feat(physics): prove authored stair sweeps and document certified families (#1355)" -- "${gs_task_paths[@]}"
```
Stop at the verified commit. No push, merge, issue comment, tag or consumer pin is assigned here.
