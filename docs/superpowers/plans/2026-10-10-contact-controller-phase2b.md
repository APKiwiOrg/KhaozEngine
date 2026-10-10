# Contact Controller Phase 2b Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every phase 1 support refusal with a certified result by certifying the complete support
neighborhood at each probe contact, across statics.

**Architecture:** A new backend query on `IPhysicsCapsuleFeatures` publishes every front-facing surface element
within the contact band of a probe pose, polygons for boxes, hulls and meshes and tangent elements for curved
primitives, together with the convex joins between polygons. `SupportCertification` certifies support from the
whole neighborhood with the unchanged support rule, and `FootSupport` switches to it. Probe sweeps cull mesh back
faces.

**Tech Stack:** C# (.NET 10), BepuPhysics 2.4.0, xUnit.

**Spec:** `docs/design/CONTACT-CONTROLLER-PHASE-2B-SUPPORT-NEIGHBORHOOD-2026-10-10.md` (owner approved 2026-10-10).
Read its "Support neighborhood query", "Certifying support from the neighborhood" and "Proposals" sections.

## Plan decisions that refine the spec

- **Joins are published by the backend.** The join test needs bounded polygon geometry, which lives in
  `KhaozEngine.Physics.Bepu`. The result carries convex join pairs, so it needs no vertex span. Certification
  applies the walkable-only capping policy.
- **Capacity is 256 elements per probe**, matching the backend's existing 256 incident-face cap, so a dense mesh fan
  never meets a capacity refusal the old query would not.
- **Joins are a symmetric bit matrix**, row `i` bit `j` set when elements `i` and `j` are joined, sized
  `elements * ((elements + 63) / 64)` words. A convex fan of n faces has n(n-1)/2 joins, so a pair list with its own
  cap would refuse a UV-sphere pole. The matrix is derived from the element capacity and never refuses on its own.
- Task 7 records both in the spec.

## Global Constraints

- Every local build or test runs through build-slot: `build-slot --label p2b -- <command>`, and
  `build-slot --heavy --label p2b -- <command>` for the full build and full suite. Exit 75 means nothing ran, so
  retry. Never loop tests. Use the longest timeout the shell allows.
- `TreatWarningsAsErrors` holds. Zero warnings.
- Commit with hooks on and explicit paths. Subjects use `area(scope): summary`.
- No em or en dashes and no prose semicolons in shipped text, comments included.
- No `MoveTuning`, wire, navigation or consumer change. The legacy stepper stays byte-unchanged.
  `QueryCapsuleFeature` keeps its contract and tests.
- Every expectation is derived from geometry (installed float vertices or closed forms), never from a run. No
  assertion pins a contact position finer than half the contact skin (`ShellMotion.ContactSkin / 2`). No test
  depends on a micrometre sweep result.
- Box and mesh variants wherever the geometry allows (`SceneVariant.Box` and `SceneVariant.Mesh`).
- The band is `SupportCertification.ContactBand` (`0.0001f`).
- Elements are ordered by static handle, then element id.
- Version: ride the staged 20.30.1. Before a changelog edit, check `git tag --sort=-v:refname | head -1`. If v20.30.1
  is tagged, stop and report.

## Review Focus

- **Selected-view exclusions.** A static the query view excludes must never appear in a neighborhood. Pinned in
  Task 1 (`NeighborhoodHonoursViewExclusions`).
- **Dynamic bodies.** A dynamic box at the contact contributes nothing to support. Pinned in Task 1
  (`DynamicBodiesAreNotMembers`).
- **Dense fans.** A mesh vertex with 96 incident triangles under the probe certifies, and only more than 256 members
  refuses. Pinned in Task 2 (`DenseFanCertifiesWithinCapacity`) and Task 1 (`CapacityRefusesAtomically`).
- **Overlapping coplanar statics.** Two statics with coincident coplanar floors give the floor height, identically
  on repeat and on rebuild in the other insertion order. Pinned in Task 5 (`OverlappingCoplanarStaticsAgree`).
- **Rotated and tilted statics.** A yawed box and a cylinder lying on its side certify like their upright forms.
  Pinned in Task 1 (`YawedBoxElementsMatchTheOracle`) and Task 3 (`LyingLogSupportsItsTopLine`).

---

### Task 1: Neighborhood contract, collector, polyhedron polygons and joins

**Files:**
- Modify: `KhaozEngine.Physics/CapsuleFeatureQuery.cs` (new types and the interface method)
- Create: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportNeighborhood.cs` (public entry, lease and view checks)
- Create: `KhaozEngine.Physics.Bepu/SupportNeighborhoodCollector.cs` (broadphase candidates, ordering, capacity)
- Create: `KhaozEngine.Physics.Bepu/SupportNeighborhoodPolyhedra.cs` (box and hull leaf faces as polygons)
- Create: `KhaozEngine.Physics.Bepu/SupportNeighborhoodJoins.cs` (the convex join test)
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryView.cs` (forward the method through views)
- Modify: every other `IPhysicsCapsuleFeatures` implementer (`git grep -l "IPhysicsCapsuleFeatures"`), including
  `KhaozEngine.Game.Tests/Locomotion/Contacts/CountingQueryView.cs`, which counts the new call as a feature query
- Test: `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodTests.cs` (namespace `KhaozEngine.Tests.Physics`)

**Interfaces:**
- Produces, in `KhaozEngine.Physics`:

```csharp
public enum SupportElementKind : byte { Polygon, Tangent }

/// ElementId is stable per static: polyhedron leaf * 256 + face, mesh triangle index, tangent leaf * 4 + part
/// (0 sphere, capsule or cylinder side, 1 cylinder top cap, 2 cylinder bottom cap).
public readonly record struct SupportElement(StaticHandle Static, SupportElementKind Kind, int ElementId,
    Vector3 Normal, float NormalError, Vector3 Witness, float PositionErrorMetres,
    double SeparationLower, double SeparationUpper);

public readonly struct SupportNeighborhoodResult
{
    // Status (CapsuleFeatureStatus), Elements, RequiredElements, JoinWordsPerRow, and the lifecycle
    // fields CapsuleFeatureResult carries: QueryWorld, SourceWorld, Lease, Origin, GeometryGeneration.
    public static SupportNeighborhoodResult Refused(CapsuleFeatureStatus status, int requiredElements = 0);
    public static SupportNeighborhoodResult Completed(IPhysicsWorld queryWorld, IPhysicsQueryLease lease,
        int elements /* lifecycle arguments as CapsuleFeatureResult.Completed takes them */);
}

// On IPhysicsCapsuleFeatures:
SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
    float bandMetres, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default);
void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease);
// joins must hold elements.Length * ((elements.Length + 63) / 64) words. Row i starts at i * JoinWordsPerRow.
```

- Membership: every face of every selected static leaf whose closed-polygon distance to the probe segment, less
  the probe radius, has a lower bound at most `bandMetres`, and whose front faces the probe (the probe's closest
  point lies on the front side within the band). The witness is the closest point of the closed polygon to the
  probe segment, with `PositionErrorMetres` enclosing it. Separation bounds as `CapsuleFeatureResult` publishes
  them.
- Join: polygon elements `a` and `b` are joined when the closed-polygon distance between them has a lower bound at
  most `bandMetres` and every vertex of each lies at most `bandMetres` above the other's plane (upper bound). This
  holds inside one static and across statics.
- Refusals: `CapacityExceeded` (atomic, required counts set, nothing written), `Ambiguous` (non-manifold capture),
  `Unsupported` (pose outside the feature domain, layer filter). Same pose domain and lease rules as
  `QueryCapsuleFeature`. A view's excluded statics and dynamic bodies are never members.

- [ ] **Step 1: Write the failing tests** in `SupportNeighborhoodTests.cs`. Each builds a `BepuPhysicsWorld`,
  takes a lease, places a probe `new CapsuleShape(0.2f, 0.01f)` at an exact contact pose computed from the
  geometry, and queries with `bandMetres: SupportCertification.ContactBand` (reference the constant's value
  `0.0001f` directly in this project).
  - `BoxTopFaceIsTheOnlyMemberAboveItsCentre`: one element, `Kind == Polygon`, normal within `NormalError` of
    `UnitY`, witness Y equal to the top height within `PositionErrorMetres`.
  - `BoxCornerPublishesTopAndBothSides`: probe touching the top corner from outside gives exactly the top and the
    two side faces, and joins top with each side (convex), and the two sides with each other.
  - `NosingPoseIncludesTreadAndRiser`: the #1342 box pose (two stair boxes, treads 0.40, risers 0.25, probe rim
    `2.1e-7` past the riser at x `0.4`) completes and includes the upper box's top face. No `Unresolved`.
  - `TwoBoxRidgeIsJoinedAcrossStatics`: two separate boxes yawed into a 30 degree ridge sharing their top edge,
    probe on the ridge: both top faces are members and joined. A gap of `1e-3` between them gives no join.
  - `SunkRampIsNotJoined`: a box ramp sunk 0.05 into a floor box, probe at the intersection line: both members,
    not joined.
  - `YawedBoxElementsMatchTheOracle`, `CompoundLeavesContributeIndependently`, `NeighborhoodHonoursViewExclusions`,
    `DynamicBodiesAreNotMembers`, `CapacityRefusesAtomically` (span of 1 for the corner gives `CapacityExceeded`,
    `RequiredElements == 3`, nothing written), `ResultIsBoundToItsLeaseAndReceiver` (as the feature query's
    lifecycle tests).
  - `PolyhedronMembershipMatchesBruteForceOracle`: 500 seeded poses (`new Random(1438)`) of the probe near random
    boxes and hulls. The oracle computes each face's segment-to-polygon distance in double. Every face with oracle
    separation at most `band - 1e-6` is a member. Every member has `SeparationLower <= band` and oracle separation
    at most `band + PositionErrorMetres`.
- [ ] **Step 2: Run** `build-slot --label p2b -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~SupportNeighborhoodTests"`. Expected: build failure (types missing).
- [ ] **Step 3: Implement.** The collector reuses the broadphase gather in `ComputePenetrationCore`
  (`BepuPhysicsWorld.Queries.cs`) over the probe's bounds inflated by the band, statics only, honouring the view's
  exclusions. Per static it captures geometry the way `CapsuleFeatureGeometry` does (installed pose proof,
  compound flattening), so a curved leaf in a compound does not refuse the box leaves beside it. Distances, witness
  and errors use `FeatureNumber`, `GeometryInterval`, `BoundedGeometryArithmetic` and
  `GeometryVectorOperations.Publish`. Curved leaves are skipped until Task 3 and mesh statics until Task 2.
- [ ] **Step 4: Run** the filter again. Expected: all pass. Then run
  `--filter "FullyQualifiedName~KhaozEngine.Tests.Physics"` once. Expected: no regressions.
- [ ] **Step 5: Commit** `feat(physics): certify the support neighborhood of a probe on polyhedra`.

### Task 2: Mesh triangles in the neighborhood

**Files:**
- Create: `KhaozEngine.Physics.Bepu/SupportNeighborhoodMesh.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportNeighborhoodCollector.cs` (route mesh statics)
- Test: `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodMeshTests.cs`

**Interfaces:**
- Consumes: Task 1's types, collector and `SupportNeighborhoodJoins`.
- Produces: mesh triangles as `Polygon` elements with `ElementId` = triangle index. Front is
  `Cross(C - A, B - A)`, as `CapsuleFeatureMesh` defines. Capture reuses `CapsuleFeatureMesh` topology validation,
  so a non-manifold mesh refuses `Ambiguous`.

- [ ] **Step 1: Write the failing tests:**
  - `ValleyLinePublishesBothFaces`: the symmetric 10 degree V of `FootSupportBoundaryTests`, axis probe
    (radius `0.01`) on the valley line: both faces members, not joined.
  - `MeshNosingPoseIncludesTheTread`: the #1342 mesh stair pose (axis x `1.4000002`, nosing at `1.4`) completes and
    includes the tread triangle(s).
  - `PyramidApexJoinsEveryFace` and `SaddleJoinsOnlyConvexNeighbours`.
  - `BackFaceIsNotAMember`: a down-facing triangle at y `0.1` with the probe above it gives no member for it.
  - `NonManifoldEdgeCertifiesEachTriangle`: three triangles on one edge are three members with geometric joins.
  - `DenseFanCertifiesWithinCapacity`: 96 triangles around one vertex, probe on the vertex: completes with 96
    members.
  - `MeshMembershipMatchesBruteForceOracle`: 500 seeded poses over random heightfield meshes, same assertions as
    Task 1's oracle.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SupportNeighborhoodMeshTests"`. Expected: FAIL (meshes skipped).
- [ ] **Step 3: Implement** triangle membership through the mesh's bounding tree (`CapsuleFeatureMeshBounds`), the
  same per-triangle distance and witness as Task 1, and joins through `SupportNeighborhoodJoins`.
- [ ] **Step 4: Run** the filter, then `KhaozEngine.Tests.Physics` once. Expected: all pass.
- [ ] **Step 5: Commit** `feat(physics): publish mesh triangles in the support neighborhood`.

### Task 3: Tangent elements for curved primitives

**Files:**
- Create: `KhaozEngine.Physics.Bepu/SupportNeighborhoodCurved.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportNeighborhoodCollector.cs` (route sphere, capsule and cylinder leaves)
- Test: `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodCurvedTests.cs`

**Interfaces:**
- Consumes: Task 1's types and collector.
- Produces: `Tangent` elements. Sphere and capsule: one element (part 0), witness the closest surface point to the
  probe segment, normal radial from the sphere centre or the capsule's core segment. Cylinder: side (part 0, normal
  radial from the axis), top cap (part 1), bottom cap (part 2), each a member when within the band. A rim contact
  publishes both the cap and the side. Tangent elements are never joined. Arithmetic is bounded over the backend's
  radius domain `[0.01, 2]`, and an element's error widens near tangency rather than refusing.

- [ ] **Step 1: Write the failing tests:**
  - `SphereTopUnderTheAxis`: sphere radius `0.25` centred at `(0.3, 0, 0)`, axis probe over the centre: one
    element, witness Y within error of `0.25`, normal within error of `UnitY`.
  - `SphereSideContactHasARadialNormal`, `CapsuleBarrelAndCapNormals`, `UprightCylinderCapAndSide`,
    `CylinderRimPublishesCapAndSide`.
  - `LyingLogSupportsItsTopLine`: a cylinder rotated 90 degrees about Z, axis probe over its axis line: the side
    element's witness Y equals centre Y plus radius within error.
  - `CompoundOfBoxAndCylinderPublishesBoth`.
  - `CurvedMembershipMatchesBruteForceOracle`: 500 seeded poses, oracle by closed-form distance in double.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SupportNeighborhoodCurvedTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** with `GeometryInterval` square roots and `InstalledPoseOperator` for the leaf frame,
  including the cylinder's base-alignment translation from `ShapeFactory`.
- [ ] **Step 4: Run** the filter, then `KhaozEngine.Tests.Physics` once. Expected: all pass.
- [ ] **Step 5: Commit** `feat(physics): publish tangent elements for curved statics`.

### Task 4: Back-face culling for probe sweeps

**Files:**
- Modify: `KhaozEngine.Physics/QueryFilter.cs`
- Modify: `KhaozEngine.Physics.Bepu/HitHandlers.cs` (`SweepHitHandler.AllowTest(collidable, childIndex)`)
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.Queries.cs` (pass the sweep direction and the flag to the handler)
- Test: `KhaozEngine.Game.Tests/Physics/SweepBackFaceCullingTests.cs`

**Interfaces:**
- Produces: `public readonly record struct QueryFilter(QueryMobility Mobility = QueryMobility.All, uint Layers = 0,
  bool CullBackFaces = false)`. With `CullBackFaces`, `SweepCapsule` skips a mesh triangle when the dot product of
  its world front normal `Cross(C - A, B - A)` with the sweep direction is positive. Vertical triangles (dot
  exactly zero) are kept. Compound children are unaffected. Raycasts against meshes are already one-sided in Bepu,
  and `ComputePenetration` ignores the flag. The XML doc states all three.

- [ ] **Step 1: Write the failing tests:** `DownFacingTriangleIsPassedWithCulling` (the sweep reaches the floor at
  y `0` below a down-facing triangle at y `0.1`), `UpFacingTriangleIsHitWithCulling`,
  `VerticalTriangleIsKeptWithCulling`, `WithoutCullingBackFacesStillBlock` (default filter hits the down-facing
  triangle at its height), `MeshNearestHitHoldsWithCulling` (the `MeshSweepNearestHitTests` scenes with the flag).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SweepBackFaceCullingTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the flag and the per-child test. The handler keeps `maximumT` handling from 309c8e861
  unchanged.
- [ ] **Step 4: Run** the filter and `MeshSweepNearestHitTests`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(physics): let sweeps cull mesh back faces`.

### Task 5: Certify support from the neighborhood

**Files:**
- Modify: `KhaozEngine.Locomotion/Contacts/SupportCertification.cs`
- Modify: `KhaozEngine.Locomotion/Contacts/FootSupport.cs`
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportScenes.cs` (new builders below)
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportBoundaryTests.cs`, `FootSupportTests.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportNeighborhoodTests.cs`
- Modify or delete: `KhaozEngine.Movement.Tests/SupportCertificationTests.cs` and `LowLipFeaturePolicyControls.cs`
  rows that call the retired entry (see Step 3)

**Interfaces:**
- Consumes: Tasks 1 to 4.
- Produces:

```csharp
/// Writes one contribution per member, in element order, and returns the count. Refuses (returns -1) when the
/// result is not Complete or a member fails admissibility. Throws for an expired, foreign or wrong-receiver
/// result. Vertical members (upward bound reaching zero) write a contribution of kind Refused that callers skip.
internal static int CertifyNeighborhood(IPhysicsCapsuleFeatures capability, IPhysicsQueryLease lease,
    in SupportNeighborhoodResult result, ReadOnlySpan<SupportElement> elements, ReadOnlySpan<ulong> joins,
    Vector2 axis, float cosMaxSlope, Span<SupportContribution> contributions);
```

- Rules, from the spec: a walkable member contributes `min(plane at the axis, witness height, plane at the axis of
  every walkable member joined to it)`. A tangent member contributes `min(tangent plane at the axis, witness
  height)`. A steep member follows the phase 1 steep rule. Admissibility per member is the phase 1 `Admissible`
  bound set (position error at most `1/4000`, normal error at most `1e-5`, separation inside the band).
- `FootSupport.Probe` sweeps with `QueryFilter.StaticsOnly with { CullBackFaces = true }`, queries the neighborhood
  at the contact pose with `SupportCertification.ContactBand`, certifies, and offers every non-refused contribution
  with its member's `Static` and `ElementId` (as `FeatureId`). A refused neighborhood is a refused proposal at the
  contact's lowest point. The rule that a contribution wholly above the band refuses stays. Spans:
  `stackalloc SupportElement[256]`, `ulong[256 * 4]`, `SupportContribution[256]`.
- `FootSupportScenes` gains `Sphere(SceneVariant, ...)`, `UprightCylinder`, `LyingLog`, `PlateauBesideSteepFace`
  (plateau at y `0` over x `[-2, 0]`, separate 70 degree face falling from x `0`), `OverlappingCoplanarFloors`,
  `OverCapacityFan` (300 triangles meeting at one vertex, which exceeds the 256-element capacity) and `PartitionedTerrain(int pieces)` (one heightfield mesh with a ridge, a valley, a step and a
  fan, built whole or split into `pieces` statics that share boundary vertices exactly).

- [ ] **Step 1: Write the failing tests.**
  - Former refusals, renamed to their certified outcome, box and mesh variants: `ConcaveValleyLineSupportsTheCrease`
    (Walkable at the crease height from the installed vertices), `BoxTopCornerSupportsTheTop` (Walkable `0.1`),
    `CurvedPrimitiveSupportsItsTop` (sphere: Walkable at `centre.Y + 0.25`), `BackFaceOfOneSidedMeshSeesTheFloor`
    (Walkable `0`).
  - `PlateauBesideSeparateSteepFaceStaysWalkable` (#1340): axis x `0.01`, feet `0`, foot radius `0.2`, reach `0.4`
    both ways: Walkable at `0`, `Static` is the plateau.
  - `TwoStaticRidgeMatchesTheSingleStatic(degrees)` for 10, 30 and 45 (#1333): same status, and heights agree within
    the sum of their `HeightError`s, with `Ridge` and `TwoStaticRidge` at the same axis.
  - `ConvexApexGivesTheMinimumPlane`, `SaddleGivesEachFacesPlane`, `SunkRampTakesTheHigherSurface`.
  - `CylinderCapIsWalkableAndSideIsSteep`, `LyingLogSupportsItsTopLine`, `SphereFlankBeyondTheSlopeLimitIsSteep`.
  - `OverlappingCoplanarStaticsAgree`: Walkable `0`, identical on repeat and when rebuilt in the other order.
  - `OverCapacityFanRefuses`: `Refused`.
  - `NonManifoldMeshCertifiesPerTriangle`: three triangles on one edge and a duplicate triangle give the walkable
    support of their surfaces.
  - The phase 1 row that pins the hidden lower surface limit keeps its expectation or moves to the certified one.
    Say which in the report, with the geometric reason.
  - `SupportIsIndependentOfPartition`: `PartitionedTerrain(1)`, `(4)` and one static per triangle, over a grid of
    axes at 0.05 spacing: identical status, heights within the sum of `HeightError`s.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.FootSupport"`. Expected:
  the new rows FAIL, existing rows pass.
- [ ] **Step 3: Implement** `CertifyNeighborhood` on the existing interval kernel (`PlaneAtAxis`, `Difference`,
  `Product`, `Quotient`), then switch `FootSupport.Probe`. When no production caller of `Certify` remains, delete it.
  Port each of its `KhaozEngine.Movement.Tests` rows that pins a rule the new entry keeps (lifecycle, receiver,
  widened evidence, admissibility) to `CertifyNeighborhood`, and delete the rows that only pin the retired
  closest-feature refusals. List every ported and deleted row in the report.
- [ ] **Step 4: Run** the FootSupport filter, then `KhaozEngine.Tests.Locomotion.Contacts` and
  `KhaozEngine.Movement.Tests` once each. Expected: all pass. Any changed expectation outside the former refusal
  rows is a defect to report, not to accept.
- [ ] **Step 5: Commit** `feat(locomotion): certify foot support from the whole neighborhood`.

### Task 6: Ground core on the neighborhood

**Files:**
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundScenarioTests.cs`
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundCoreTests.cs`, `GroundSeatTests.cs`
- Create: `docs/verification/2026-10-10-p2b-support-cost.json`

**Interfaces:**
- Consumes: Task 5's `FootSupport`. No production change is expected. A defect a ground row exposes is fixed at its
  root with a focused row and named in the report.

- [ ] **Step 1: Change the tests.**
  - Delete `StalledByNosingGap`, `HeldByNosingGap`, `NosingEscapeRefusesAnotherStall`,
    `NosingEscapeRefusesAnotherHeldStart` and every escape call. `StairsClimbEveryRiser` and `QueryCostPerTick`
    assert their full expectations on all 32 and all cost rows.
  - `RefusedStartHolds` and `RefusedTargetBlocks` run on `OverCapacityFan`. `GroundSeatTests.CurvedPropRefuses`
    becomes `CurvedPropSeatsOnItsTop` (Seated at the sphere top).
  - Add `SupportFindAllocatesNothing`: after one warm call, `GC.GetAllocatedBytesForCurrentThread()` around one
    `FootSupport.Find` on flat ground, stairs, a 96-triangle fan and a sphere is `0`.
  - Add `SupportFindTiming`: 200 calls per scene, write mean microseconds to the test output. No assertion.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts"`. Expected: all pass.
  If a stair row fails, trace it to its cause before anything else.
- [ ] **Step 3: Record** the timing output and allocation results in `2026-10-10-p2b-support-cost.json`, with the
  machine and configuration.
- [ ] **Step 4: Commit** `test(locomotion): run the ground core on certified neighborhoods`, then comment the
  measured cost on #1334 (plain prose, no colons, dashes or semicolons).

### Task 7: Finish phase 2b

**Files:** `CHANGELOG.md`, `docs/INDEX.md`, the phase 2b spec (status, the two plan decisions, the capacity),
the program spec (support primitive section rewritten for the neighborhood, known limits, invariant 9), the phase 2
spec's refusal row, `docs/verification/2026-10-10-p2b-final.json`.

- [ ] **Step 1:** Append one line to the staged `## 20.30.1` entry: the support primitive certifies the complete
  neighborhood, closing the phase 1 refusals, with no consumer or movement change. Update the docs listed above.
  Set the phase 2b spec and its INDEX row to implemented.
- [ ] **Step 2:** Full verification once, heavy lane: `dotnet build KhaozEngine.slnx -c Release`, the full tests
  excluding `LiveSocket` (the filter `docs/verification/2026-10-10-p2-final.json` records), `dotnet format
  KhaozEngine.slnx --verify-no-changes --no-restore`, then the five repository guards from AGENTS.md. Record totals
  in the verification JSON. Commit `docs(release): record contact controller phase 2b`, push the branch.
- [ ] **Step 3 (controller):** hosted x64 CI on the branch, merge `origin/main`, whole-branch review, fast-forward
  main, push, `scripts/pack-local-feed.sh`, close #1329 to #1333, #1340 and #1342 with the merge, comment on #438.
  No tag.
