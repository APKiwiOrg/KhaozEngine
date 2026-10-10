# Contact Controller Phase 1 (Contact Foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Deliver the certified foot-support primitive, shell geometry and contact classifier that the #438
controller's later phases build on, with the capsule-feature query integrated as its face identity.

**Architecture:** Support comes from two downward probe capsules at the body axis (a 5 mm axis probe and a
leg probe of the footprint radius) swept with the existing `SweepCapsule`. Each hit is certified with
`QueryCapsuleFeature` under the caller's read lease, and a certified feature contributes
`min(plane at axis, witness height)` for its walkable faces. Analytic terrain contributes `h(axis)` exactly.
All new Locomotion types are internal under `KhaozEngine.Locomotion.Contacts`. The legacy stepper is untouched.

**Tech Stack:** C# (.NET 10), System.Numerics, Bepu 2.4 through `KhaozEngine.Physics.Bepu`, xUnit.

**Spec:** `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md` at `86ff2b637`. Task 3 amends it for
the deviations below.

## Deviations from the approved spec (owner review)

1. No `IPhysicsShapeSweeps` seam. Probe capsules through the existing `SweepCapsule` propose candidates and
   leave the probe exactly in contact, which is the pose certification needs. A cylinder sweep would add a
   public API with no remaining use.
2. No raw ray proposals. A 5 mm axis probe capsule replaces the axis ray, so every proposal is certifiable.
3. The certification rule generalizes the reviewed eligibility predicate. The walkable threshold is
   `cos(MaxSlopeRadians)`, not the legacy 0.9 near-flat gate. A convex crease of two walkable faces (a ridge)
   contributes the lower plane at the axis instead of refusing, since refusal would strand a body on every
   mesh ridge. Concave creases, vertices and curved primitives the backend reports as `Unresolved`,
   `Ambiguous` or `Unsupported` refuse, as before. Phase 1 pins those limits and Task 6 files them for phase 2.

## Global Constraints

- Legacy stepper files (`KhaozEngine.Locomotion/CharacterMovement*.cs`) stay byte-unchanged.
- No `MoveTuning` field, wire field, protocol generation or navigation bake identity change in phase 1.
- Every query in one support evaluation runs under the caller's `IPhysicsQueryLease`. A sign-dependent
  decision uses certified error. No tolerance is chosen to make a case pass. Refusal beats a guess.
- `BepuPhysicsWorld.QueryView.cs` is shared with swimming (thread `5ca0fade`). Integrate the reviewed
  forwarding unchanged and send one coordination note before merging to `main`.
- Every local build, test, render or bake runs through `build-slot`. One focused run at a time. The full
  suite runs once, in Task 6, in the heavy lane.
- Warnings are errors. Fix at the source. No suppressions.
- KESIZE: no file over 800 lines. New behavior gets its own type.
- Test namespaces sit under `KhaozEngine.Tests.*`. Tests touching process-global state use a collection with
  `DisableParallelization = true`.
- No em or en dashes in code, comments or docs. No prose semicolons in Markdown.
- Commit subjects are `area(scope): summary`. Stage explicit paths only.
- No tag or release. The post-push pack runs only from `main` after `origin/main` contains the commit.

## Review Focus

- A body on the seam between two coplanar statics must get a deterministic owner, never `Refused` or a
  flickering owner. Task 4 pins it.
- A lease that is expired, foreign or from another receiver must throw, never return `None`. Task 3 pins it.
- Non-finite or out-of-range inputs (NaN axis, `footRadius <= 0`, negative reach) must throw
  `ArgumentOutOfRangeException`. Task 3 pins it.
- A dynamic body under the footprint must never contribute support. Task 4 pins it.
- A one-sided mesh met from below must not become support. Task 2 (`underside`) and Task 4 pin it.

---

### Task 1: Integrate the capsule-feature query

**Files:**
- Bring from `5eceb2c16` (`fix/low-lip-resting-proof`), unchanged:
  - `KhaozEngine.Physics/CapsuleFeatureQuery.cs`
  - `KhaozEngine.Physics.Bepu/`: `BepuPhysicsWorld.CapsuleFeatures.cs`, `BepuPhysicsWorld.QueryView.cs`,
    `BoundedGeometryArithmetic.cs`, `CapsuleFeatureGeometry.cs`, `CapsuleFeatureGeometryNumbers.cs`,
    `CapsuleFeatureGeometryQuery.cs`, `CapsuleFeatureMesh.cs`, `CapsuleFeatureMeshBounds.cs`,
    `CapsuleFeatureMeshClosest.cs`, `CapsuleFeatureMeshQuery.cs`, `CapsuleFeaturePolyhedra.cs`,
    `CapsuleFeaturePolyhedraClosest.cs`, `CapsuleFeaturePredicates.cs`, `GeometryVectorOperations.cs`,
    `InstalledMeshSides.cs`, `InstalledPoseOperations.cs`, `InstalledPoseOperator.cs`,
    `RepresentedGeometryTransforms.cs`, `KhaozEngine.Physics.Bepu.csproj`
  - `KhaozEngine.Game.Tests/Physics/`: every file the branch adds there (16 files)
  - `KhaozEngine.Movement.Tests/`: `CornerFeatureControlScene.cs`, `CornerFeatureOracle.cs`,
    `LowLipFeatureSourceCapacityTests.cs`, `LowLipFeaturePolicyControls.cs`
  - `docs/design/`: the four `CAPSULE-FEATURE-*-PROOF-*.md`, `LOW-LIP-FACE-OBSERVATIONS-2026-10-07.json`
  - `docs/superpowers/specs/2026-10-07-capsule-feature-correspondence-design.md` (linked by the proofs)
  - `docs/verification/`: every `2026-10-07-*.json`, plus `2026-10-08-exact-normal-*.json`,
    `2026-10-08-feature-mesh-green.json`, `2026-10-08-installed-pose-*.json`
- Modify: `docs/INDEX.md` (one row per brought design doc, taken from the branch's rows)
- Create: `docs/verification/2026-10-09-p1-feature-integration.json`
- Never bring: `CharacterMovement*.cs`, `LowLipTraversalTests*.cs`, `GroundMoveQuerySelectionTests.cs`, the
  low-lip runtime evidence or `LOW-LIP-*` design docs other than the face observations.

**Interfaces:**
- Produces: `IPhysicsCapsuleFeatures.QueryCapsuleFeature(IPhysicsQueryLease, StaticHandle, CapsuleShape,
  Pose, float, Span<CapsuleIncidentFace>, QueryFilter)`, `AssertFeatureCurrent`, `CapsuleFeatureResult`,
  `CapsuleIncidentFace`, `CapsuleFeatureKind`, `CapsuleFeatureStatus`, implemented by `BepuPhysicsWorld` and
  its selected query views.

- [ ] **Step 1: Check out the listed paths from `5eceb2c16`**

Run: `git checkout 5eceb2c16 -- <each listed path>`. Then `git status --short` must show only those paths.
Check every relative link in the brought docs resolves.

- [ ] **Step 2: Build**

Run: `build-slot --label p1-integrate-build -- dotnet build KhaozEngine.slnx -c Release -m:1`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 3: Run the integrated suites**

Run: `build-slot --label p1-feature-suite -- sh -c 'dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release -m:1 --no-build --filter "FullyQualifiedName~BoundedGeometryArithmeticTests|FullyQualifiedName~CapsuleFeature|FullyQualifiedName~DyadicDistanceComparisonTests|FullyQualifiedName~GeometryExactNormalTests|FullyQualifiedName~GeometryVectorOperationsTests|FullyQualifiedName~InstalledPose|FullyQualifiedName~QuaternionGeometryTransformTests|FullyQualifiedName~RepresentedGeometryTransformTests" && dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release -m:1 --no-build --filter "FullyQualifiedName~LowLipFeatureSourceCapacityTests|FullyQualifiedName~LowLipFeaturePolicyControls"'`
Expected: zero failed, zero skipped in both. Record both totals and the log hash in the verification JSON.

- [ ] **Step 4: Format and guards**

Run: `build-slot --label p1-integrate-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore`
then `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`,
`bash scripts/check-doc-versions.sh`. Expected: all exit 0.

- [ ] **Step 5: Commit**

```bash
git add <the listed paths> docs/INDEX.md docs/verification/2026-10-09-p1-feature-integration.json
git commit -m "feat(physics): integrate the certified capsule-feature query"
```

### Task 2: Certified support contribution

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/SupportCertification.cs`
- Modify: `KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj` (add `InternalsVisibleTo` for
  `KhaozEngine.Game.Tests` and `KhaozEngine.Movement.Tests`)
- Test: `KhaozEngine.Movement.Tests/SupportCertificationTests.cs` (uses the Task 1 corner fixtures)
- Modify: `docs/design/CAPSULE-FEATURE-INSTALLED-POSE-PROOF-2026-10-08.md` only if it names the old predicate home

**Interfaces:**
- Consumes: Task 1 contract types.
- Produces:

```csharp
namespace KhaozEngine.Locomotion.Contacts;
internal enum CertifiedSupportKind : byte { Refused, Walkable, Steep }
internal readonly record struct SupportContribution(CertifiedSupportKind Kind, double Lower, double Upper,
    Vector3 Normal, Vector3 Witness, int FeatureId);
internal static class SupportCertification
{
    internal const float ContactBand = 0.0001f;
    internal static SupportContribution Certify(IPhysicsCapsuleFeatures capability, IPhysicsQueryLease lease,
        in CapsuleFeatureResult result, ReadOnlySpan<CapsuleIncidentFace> faces, Vector2 axis, float cosMaxSlope);
}
```

`Lower` and `Upper` enclose the contribution height. `Normal` is the face that wins the min.

- [ ] **Step 1: Write the failing tests**

Port every case of `LowLipTraversalTests.ConsumerEligibility.cs` at `5eceb2c16` into `SupportCertificationTests`,
calling `SupportCertification.Certify` with the scene's candidate XZ as `axis`. Expected kinds:

| Case | Expected |
|---|---|
| `open`, `convex`, coplanar raw faces | `Walkable` |
| `ridge` (two walkable tops) | `Walkable`, `Lower <= ridge height <= Upper` |
| `concave`, `competing`, `underside`, `wall`, `dome`, `unsupported-compound` | `Refused` |
| each widened variant (`position`, `separation`, `direction`, `wall`) | `Refused` |
| expired lease | throws `ObjectDisposedException` |
| foreign receiver (`scene.World` as capability) | throws `InvalidOperationException` |

Add `ConvexStepEdgeContributesTheTopPlaneAtTheAxis`: the `convex` scene gives `Lower <= top Y <= Upper` and
`Upper - Lower <= 0.0005`. Add `SteepFaceInteriorIsSteepNotRefused`: a box rotated 60 degrees about Z,
probe capsule settled on its top face by `SweepCapsule`, gives `Steep`.

- [ ] **Step 2: Run them and see the expected failure**

Run: `build-slot --label p1-cert-red -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release -m:1 --filter "FullyQualifiedName~SupportCertificationTests"`
Expected: build fails with `CS0246` for `SupportCertification`.

- [ ] **Step 3: Implement `SupportCertification.Certify`**

Keep the reviewed predicate's checks unchanged: `Complete` status, `AssertFeatureCurrent`, error caps
(position 0.25 mm, normal 0.00001), the whole separation interval inside `[-ContactBand, +ContactBand]`, kind in
`{FaceInterior, OpenBoundary, ConvexCrease}`, the separation normal's lower Y bound above zero, every incident
face's lower normal Y at least zero, and the exact-zero bypass for zero-error normals. Then:
- A face is walkable when its lower normal Y is at least `cosMaxSlope`.
- No walkable face gives `Steep`.
- A face or open-boundary patch needs every incident face walkable. A convex crease needs at least one.
- Contribution interval: for each walkable face, the plane through the witness box (each axis
  `GeometryPoint +/- PositionErrorMetres`) with the normal box (each component `+/- NormalError`), evaluated at
  `axis` as `w.y + (n.x (w.x - axis.X) + n.z (w.z - axis.Y)) / n.y`. Take the minimum over walkable faces,
  then the minimum with the witness Y interval. Use binary64 with `Math.BitDecrement` and `Math.BitIncrement`
  after every operation, as the predicate does.

- [ ] **Step 4: Run the tests to green**

Run the Step 2 command. Expected: all pass.

- [ ] **Step 5: Format and commit**

Run `dotnet format` with `--include` on the touched files through `build-slot`. Expected exit 0.

```bash
git add KhaozEngine.Locomotion/Contacts/SupportCertification.cs KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj KhaozEngine.Movement.Tests/SupportCertificationTests.cs
git commit -m "feat(locomotion): certify a feature's support contribution at the axis"
```

### Task 3: Foot support primitive

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/FootSupport.cs`, `KhaozEngine.Locomotion/Contacts/SupportSample.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportTests.cs`, with a small
  `FootSupportScenes.cs` builder in the same folder (box and two-triangle mesh variants of each scene)
- Modify: the spec, per Step 6

**Interfaces:**
- Consumes: `SupportCertification.Certify`, `SupportContribution`.
- Produces:

```csharp
namespace KhaozEngine.Locomotion.Contacts;
internal enum SupportStatus : byte { None, Walkable, Steep, Refused }
internal readonly record struct SupportSample(SupportStatus Status, float Height, float HeightError,
    Vector3 Normal, StaticHandle? Static, int FeatureId, Vector3 Witness);
internal readonly record struct FootSupportQuery(Vector2 Axis, float FeetY, float FootRadius,
    float ReachUp, float ReachDown, float CosMaxSlope);
internal static class FootSupport
{
    internal const float AxisProbeRadius = 0.005f;
    internal static SupportSample Find(Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in FootSupportQuery query);
}
```

`Static` null means analytic terrain. Coordinates are the world's local frame, as `StepCore` receives them.

- [ ] **Step 1: Write the failing tests**

Common values unless stated: `FootRadius` 0.2, `ReachUp` and `ReachDown` 0.4,
`CosMaxSlope = cos(45 degrees)`, `FeetY` 0, lease from `world.AcquireQueryReadLease()`. Each physics case runs
on the box and the mesh variant with the same expected values. Heights assert `|Height - expected| <= HeightError`
and `HeightError <= 0.0005`.

| Test | Scene and axis | Expected |
|---|---|---|
| `TerrainAloneIsExact` | `groundHeight` 0.3, world null | `Walkable`, 0.3, `HeightError` 0, `Static` null |
| `PropFloorAboveTerrainWins` | terrain 0, floor top 0.1 | `Walkable`, 0.1, the floor static |
| `SlopesSupportThePlaneAtTheAxis` | planes at 5, 20, 40 degrees rising to +X, axis x 0.5 | `0.5 tan(theta)` |
| `SlopeBeyondTheLimitIsSteep` | 60 degrees, axis x 0.5 | `Steep` |
| `LipInsideTheDiscLiftsToItsTop` | box x in [0, 2], top 0.0425, floor 0, axis x -0.1 | 0.0425, the lip static |
| `LipOutsideTheDiscLeavesTheGround` | same, axis x -0.25 | 0, the floor static |
| `CrateWithinStepHeightIsStoodOn` | crate top 0.3 at x in [0, 1], axis x -0.1 | 0.3 |
| `ShallowTreadsChainToTheReachedTread` | treads 0.35 deep, risers 0.25, axis 0.1 before tread 2's nosing, `FootRadius` 0.15 and 0.2, `FeetY` 0.25 | 0.5 |
| `DescendingRampAheadKeepsTheFlat` | floor x <= 0 at 0, ramp from (0, 0) falling to (2, -0.5), axis x -0.1 | 0 |
| `AscendingRampAheadKeepsTheFlat` | ramp rising to (2, 0.5), axis x -0.1 | 0 |
| `ConvexRidgeSupportsTheAxisSide` | 10 degree roof, ridge 0.5 at x 0, axis x -0.1, `FeetY` 0.48 | `0.5 - 0.1 tan(10 degrees)` |
| `SwimmingBankStepIsSupportedAtTheAxis` | bank top 0.25 at x in [1, 5], z in [-4, 4], floor 0, `FootRadius` 0.125 (capsule radius 0.25), axis (0.881, 0) | `Walkable`, 0.25, bank static, `Witness.X` within 0.001 of 1 |
| `IdenticalQueriesAreBitIdentical` | lip scene | two calls return equal records |
| `WorldWithoutFeatureCapabilityThrows` | an `IPhysicsWorld` decorator without `IPhysicsCapsuleFeatures` | `NotSupportedException` |
| `ExpiredOrForeignLeaseThrows` | disposed lease, then another world's lease | `ObjectDisposedException`, `InvalidOperationException` |
| `InvalidQueryValuesThrow` | NaN axis, `FootRadius` 0, `ReachDown` -1 | `ArgumentOutOfRangeException` each |

- [ ] **Step 2: Run them and see the expected failure**

Run: `build-slot --label p1-foot-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release -m:1 --filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.FootSupportTests"`
Expected: build fails with `CS0246` for `FootSupport`.

- [ ] **Step 3: Implement `FootSupport.Find`**

Validate inputs, then gather contributions inside `[FeetY - ReachDown, FeetY + ReachUp]`:
- Terrain: `groundHeight(axis)` when the delegate is non-null. `Walkable` unless `groundNormal` reports Y below
  `CosMaxSlope`, error 0.
- Physics (world non-null): require `IPhysicsCapsuleFeatures` (else `NotSupportedException`), call
  `lease.AssertCurrent()` and require `lease.Origin == world.Origin`. For each probe, axis then leg, sweep a
  `CapsuleShape(radius, 0.01f)` straight down from lowest point `FeetY + ReachUp` over `ReachUp + ReachDown`
  with `QueryFilter.StaticsOnly`. A start overlap (zero distance with zero normal) records a refused proposal
  at `FeetY + ReachUp`. A hit queries `QueryCapsuleFeature` at the contact pose with
  `SupportCertification.ContactBand` and a 256-face stack buffer, then `Certify`. A `Refused` result records a
  refused proposal at the probe's lowest point.
- Select the `Walkable` contribution with the highest interval midpoint. Ties on midpoint prefer the witness
  nearest the axis in XZ, then terrain, then the lower `StaticHandle.Value`. With no `Walkable`, take the
  highest `Steep`, else `None`.
- If a refused proposal lies above the selected contribution's `Upper`, or there is none, return `Refused`
  with `Height` NaN.
- `Height` is the midpoint as float. `HeightError` is the half-width rounded up to float.

- [ ] **Step 4: Run the tests to green**

Run the Step 2 command. Expected: all pass.

- [ ] **Step 5: Amend the spec**

In the spec, replace the `Physics seam` section, the `Support primitive` candidate list, the phase 1 table
row, the `Files` bullet for `IPhysicsShapeSweeps`, the suite line naming it, the `Phase 1 boundaries` seam
sentence and the query-cost risk, so they describe the probe capsules, the certification rule above and the
deviations section of this plan. Run `sh scripts/check-dashes.sh --tree` and `sh scripts/check-prose.sh --tree`.

- [ ] **Step 6: Format and commit**

```bash
git add KhaozEngine.Locomotion/Contacts/FootSupport.cs KhaozEngine.Locomotion/Contacts/SupportSample.cs KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportTests.cs KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportScenes.cs docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md
git commit -m "feat(locomotion): find certified foot support under the axis"
```

### Task 4: Foot support boundaries and limits

**Files:**
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportBoundaryTests.cs`
- Modify: `KhaozEngine.Locomotion/Contacts/FootSupport.cs` only where a test below fails for a real defect

**Interfaces:**
- Consumes: Task 3 types. Produces nothing new.

- [ ] **Step 1: Write the tests**

| Test | Scene | Expected |
|---|---|---|
| `CrackNarrowerThanTheDiscIsBridged` | two boxes, tops 0, gap 0.2, no floor in reach, axis mid-gap | `Walkable`, 0 |
| `CrackWiderThanTheDiscFindsNothingInReach` | gap 0.8, floor at -1 | `None` |
| `RailBetweenTheAxisAndTheRimIsCaught` | floor 0, rail 0.02 wide, top 0.1, at x in [0.14, 0.16] | 0.1 |
| `OverhangAboveTheBandIsIgnored` | floor 0, box y in [1.0, 1.2] over the axis | 0 |
| `FloorBelowTheBandIsNone` | floor at -0.5 | `None` |
| `GeometryInsideTheLegStartRefuses` | crate top 0.5 at x in [0, 1], axis x -0.1 | `Refused` |
| `CoplanarTilesNeverRefuse` | two adjacent boxes, tops 0, seam under the axis | `Walkable`, 0, the lower handle, both calls equal |
| `ExcludedStaticNeverContributes` | floor 0 plus box top 0.1, query view excluding the box, lease from that view | 0, the floor static |
| `DynamicBodyNeverContributes` | floor 0 plus a dynamic box top 0.1 under the axis | 0, the floor static |
| `OriginFrameIsLocal` | the lip scene in a world built with origin (256, 0, -256) | equals the zero-origin sample |
| `OneSidedMeshFromBelowIsNotSupport` | a down-facing triangle top 0.1 over a floor at 0 | 0, the floor static |
| `BoxTopCornerVertexRefuses` | floor 0, box x in [0, 1], z in [0, 1], top 0.0425, axis (-0.1, -0.1) | `Refused` (vertex, phase 1 limit) |
| `ValleyOffTheLineSupportsTheAxisSide` | 10 degree V valley mesh, bottom 0 at x 0, axis x -0.1 | `Walkable`, `0.1 tan(10 degrees)` |
| `ConcaveValleyLineRefuses` | same valley, axis on the valley line | `Refused` (concave, phase 1 limit) |
| `CurvedPrimitiveRefuses` | `SphereShape(1)` at (0, -0.8, 0), axis 0 | `Refused` (unsupported, phase 1 limit) |

- [ ] **Step 2: Run them**

Run: `build-slot --label p1-foot-bounds -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release -m:1 --filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.FootSupportBoundaryTests"`
Expected: all pass. A failure is investigated to its cause before any code change. Never change an
expectation or the contact band to pass.

- [ ] **Step 3: Format and commit**

```bash
git add KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportBoundaryTests.cs KhaozEngine.Locomotion/Contacts/FootSupport.cs
git commit -m "test(locomotion): pin foot support boundaries and certified limits"
```

### Task 5: Shell geometry and contact classifier

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ShellGeometry.cs`, `KhaozEngine.Locomotion/Contacts/ContactClassifier.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/ShellGeometryTests.cs`,
  `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactClassifierTests.cs`

**Interfaces:**
- Consumes: `SupportStatus`.
- Produces:

```csharp
namespace KhaozEngine.Locomotion.Contacts;
internal static class ShellGeometry
{
    internal static void Validate(in MoveTuning tuning);           // throws ArgumentException
    internal static CapsuleShape Shape(in MoveTuning tuning);       // radius CapsuleRadius
    internal static Vector3 Centre(Vector3 feet, in MoveTuning tuning);
}
internal enum ContactClass : byte { Support, SteepSupport, RisingSupport, Wall, Ceiling }
internal static class ContactClassifier
{
    internal static ContactClass? ClassifySupport(SupportStatus status);      // None and Refused give null
    internal static ContactClass ClassifyShell(Vector3 contactNormal, float cosMaxSlope);
}
```

- [ ] **Step 1: Write the failing tests**

- `ShellOfTheDefaultTuning`: `MoveTuning.Default` gives length 0.6 (`2 * 0.9 - 0.4 - 2 * 0.4`), radius 0.4,
  centre `feet + (0, 1.1, 0)`.
- `ShellAtTheMinimumIsASphere`: half-height 0.6, radius 0.4, step 0.4 validates. Length is the existing
  `Math.Max(0.01f, ...)` floor.
- `ShellTooShortThrows`: half-height 0.5, radius 0.4, step 0.4 throws `ArgumentException`.
- `ShellClassesByNormal`: normal Y 0.8 gives `RisingSupport` (cos 45 degrees is 0.7071), 0.5 and 0 give
  `Wall`, -0.1 gives `Ceiling`.
- `SupportClassesByStatus`: `Walkable` gives `Support`, `Steep` gives `SteepSupport`, `None` and `Refused`
  give null.

- [ ] **Step 2: Run them and see the expected failure**

Run: `build-slot --label p1-shell-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release -m:1 --filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.ShellGeometryTests|FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.ContactClassifierTests"`
Expected: build fails with `CS0246`.

- [ ] **Step 3: Implement both types**

The shell spans from `feet + StepHeight` to `feet + 2 * CapsuleHalfHeight`. Validation requires
`2 * CapsuleHalfHeight - StepHeight >= 2 * CapsuleRadius`.

- [ ] **Step 4: Run to green, format, commit**

```bash
git add KhaozEngine.Locomotion/Contacts/ShellGeometry.cs KhaozEngine.Locomotion/Contacts/ContactClassifier.cs KhaozEngine.Game.Tests/Locomotion/Contacts/ShellGeometryTests.cs KhaozEngine.Game.Tests/Locomotion/Contacts/ContactClassifierTests.cs
git commit -m "feat(locomotion): add the knee-height shell and contact classes"
```

### Task 6: Finish phase 1

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine.Physics/README.md`,
  `KhaozEngine.Physics.Bepu/README.md`, every declaration `scripts/check-doc-versions.sh` guards
- Create: `docs/verification/2026-10-09-p1-final.json`

- [ ] **Step 1: File the phase 2 follow-ups**

Search first with `scripts/ledger.sh search <identifier>`. Then file `kind/backlog`, `confidence/verified`
issues, each linking #438: certified support at concave creases, at vertices, and on curved primitives
(sphere, capsule, cylinder statics), and the per-down-pass query cost measurement.

- [ ] **Step 2: Document the public capability**

Add `IPhysicsCapsuleFeatures` to the two package READMEs and a short use section to
`docs/USING-KHAOZENGINE.md`: query under a lease, refusal statuses, never consume after the lease expires.
Sweep every Markdown file for the touched names.

- [ ] **Step 3: Version and changelog**

`git fetch`, re-read `<KhaozEngineVersion>` on `origin/main` and `git tag`. With 20.29.1 tagged and nothing
staged, set 20.30.0 and add its `CHANGELOG.md` entry in the same commit. If a version is already staged, ride
it and append. Run `bash scripts/check-doc-versions.sh`. Expected: exit 0.

- [ ] **Step 4: Reconcile and review**

Merge current `origin/main` into the branch and resolve there. Get the whole-branch review from a fresh
reviewer and fix only evidence-backed findings.

- [ ] **Step 5: Full verification on the exact candidate**

Run once, heavy lane:
`build-slot --heavy --label p1-final -- sh -c 'dotnet build KhaozEngine.slnx -c Release && dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket" && dotnet format KhaozEngine.slnx --verify-no-changes --no-restore'`
then the five repository guards from `AGENTS.md`. Expected: zero warnings, zero failed tests, format and
guards exit 0. Record totals and log hash in the verification JSON.

- [ ] **Step 6: Coordinate, integrate, pack**

Send swimming (thread `5ca0fade`) one note naming the `BepuPhysicsWorld.QueryView.cs` change and the commit.
Merge the branch into `main` as a fast-forward, push `main`, wait until `origin/main` contains it, then run
`scripts/pack-local-feed.sh` from `main`. Comment the result on #438. No tag.
