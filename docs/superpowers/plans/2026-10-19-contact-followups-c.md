# Contact Controller Follow-ups C Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve #1494 with represented-only support geometry and bound cumulative curved Find certification work for #1487 without weakening support safety.

**Architecture:** Give support an explicit represented operator factory and shared operator-accepting capture cores while retaining the feature/sweep dual factory. Build every support fact from point-valued matrix sources, then share one sticky work owner across all curved sub-solves in a footprint request. Gate the capsule crown and cylinder admission work on exact new-contract single-Find counters.

**Tech Stack:** C#/.NET 10, System.Numerics, BepuPhysics 2.4.0, existing interval/expansion/source-polynomial arithmetic, xUnit headless Game.Tests.

**Spec:** `docs/design/CONTACT-CONTROLLER-FOLLOWUPS-C-2026-10-19.md`, CC-1 to CC-12 and acceptance A to G. Read with lane A's spec/plan, lane B's spec, SG-1 to SG-24 and SC-9. The owning checkout's research-1494 and research-1487 reports supply the audited source inventory and original probes.

## Global Constraints

- Preserve the Euclidean 0.001 m arrival ball, roof and footprint protections and raw layer identity.
- Raw identity remains `(StaticHandle, ElementId)`. Coincident members never coalesce.
- No tolerance, skin, band, epsilon, reach or existing storage limit may be widened or added.
- Preserve the independent 0.001 m placement offset, 0.0001 m contact/join band and 0.0005 m HeightError ceiling.
- Preserve 256 raw members, 512 witness slots, 1,024 live root/admission cells and 12 terminal root cells.
- SC-9 optimized/reference equality stays bit-exact. Identity, exact signed-permutation and exact cyclic installs stay bit-identical.
- The legacy feature and sweeps factory keeps its dual contract unchanged.
- No timing, load, repeated test invocation, consumer launch, navigation bake, release or tag belongs to this lane.
- Ride staged 21.5.1 without a version bump. Only the final implementation task appends its CHANGELOG entry and rolls its date.
- Work only in `/Users/antonio/KhaozEngine/.worktrees/contact-followups-c` on `fix/contact-followups-c`, based on `a1e82663567bda9301ad4eaceb7ebad40ee19f17` plus the design commit. Never reset accepted work.
- Before each task, require `git rev-parse --show-toplevel` to equal that path, the branch to match and status to be clean. Stop on unexplained edits. One writer owns shared files at a time.
- Every build/test uses build-slot, synchronously, one command at a time. Wide focused selections use `--heavy`. Full solution build, full Game.Tests run and format verification occur once, in Task 7 only.
- Leave `KHAOZ_SUPPORT_COST_ARTIFACT` and `KHAOZENGINE_SUPPORT_EQUIVALENCE_CAPTURE` unset. No test loop, stopwatch, sleep, timeout acceptance or hosted workflow is authorized.
- Commit explicit owned paths after staged dash/prose/file-size guards and diff inspection. Workers never push, merge, tag, close/comment on issues, change branches or dispatch other agents. Integration belongs to the orchestrator.

## Review Focus

- Equivalent zero in the conjugate unit-axis check must not reject an install, while source-map signed-zero bits must remain distinct. Task 1 adds `SupportRepresentedFactoryPreservesSignedZerosWithoutNewCorrespondenceRefusal`.
- A mesh pose change under a slightly non-orthogonal represented map must rebuild the correct inverse/preimage and raw triangles. Task 1 adds `RepresentedMeshPoseMissUsesItsActualInverse`.
- Exhaustion with diagnostics disabled must survive Restore, source Reset and a later below-band pass. Task 4 adds `CurvedWorkExhaustionSurvivesAllSubsolvesWithDiagnosticsOff`.
- A nearly attained crown outside the original disc or under an independent low roof must not bypass a guard. Task 5 adds `CapsuleCrownNeedsOriginalDiscAttainmentAndIndependentRoofClearance` if that branch is required, otherwise Task 5 retains the equivalent public guard test without a crown helper.
- Same-generation reacquisition, changed views or coincident children must not revive memo or continuity evidence. Task 3 adds `RepresentedCoincidenceKeepsRawKeysAndIntervalAuthentication`.

---

## File structure and proof conventions

`InstalledPoseOperator` remains the sole affine arithmetic owner. Its new support door and
`InstalledPoseOperations` composition door feed shared `CapsuleFeatureGeometry` topology cores.
`SupportPolygonSourceMap` owns point-valued planar sources. `SupportCurvedLeaf.SourceColumn`
owns the point-valued curved source. No second topology reader or rational library is introduced.

New `SupportCurvedWork.cs` in Physics.Bepu owns enforcement and its fixed policy. New
`SupportCurvedWorkDiagnostics.cs` in Physics owns internal observation types, so Locomotion
does not depend on the backend. Existing SupportQueryDiagnostics owns observer lifetime.
The footprint collector owns and provisions one work object with its scratch. Constructor wiring
shares it with the closed-form and tilted arithmetic, root and surface-admission owners.

At each task commit, the three staged guards mean these exact commands after explicit staging.
All must exit 0 before the commit.

```bash
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
git diff --cached --check
```

Test responsibilities are split into represented-pose tests, represented planar/curved source
tests, represented safety tests, work-owner tests and the end-seam gate. Keep large existing
types within the size ratchet by adding cohesive test partials and new responsibility owners.
Do not increase a baseline or split at an arbitrary line.

Use installed geometry readers and `InstalledPoseOracle.Represented`, which independently
rounds every coefficient operation as a rational. Compound expectations first use the actual
installed/composed binary32 leaf pose. Keep Real for feature/sweeps and negative controls.
No production predicate, cache injection, normalized quaternion, rounded world-vertex authority
or production result snapshot supplies an expected geometric sign.

Assertion snippets use local variables from the named fixture/test and pin observable fields.
`before`/`after` for goldens are separately serialized rows under identical query bits.
Witness/normal comparisons use their published errors and independent enclosures, with no
extra epsilon. Exact-control comparisons serialize raw bits, including signed zeros and joins.

### Task 1: Switch planar support through the explicit represented factory (CC-1 to CC-4, A to D)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/InstalledPoseOperator.cs`, `KhaozEngine.Physics.Bepu/InstalledPoseOperations.cs`, `KhaozEngine.Physics.Bepu/CapsuleFeatureGeometry.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportNeighborhoodPolyhedra.cs`, `KhaozEngine.Physics.Bepu/InstalledSupportGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSupportGeometryCache.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportNeighborhoodMesh.cs`, `KhaozEngine.Physics.Bepu/InstalledSupportMeshWorldForm.cs`, `KhaozEngine.Physics.Bepu/InstalledSupportWorldForm.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportNeighborhoodPolygon.cs`, `KhaozEngine.Physics.Bepu/SupportSourceMapComparer.cs`, `KhaozEngine.Physics.Bepu/SupportFootprintSourcePredicates.cs`, `KhaozEngine.Physics.Bepu/SupportPlanarProposal.cs`
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprint.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportNeighborhood.cs` (support-only pose-domain gates)
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/CapturedStairExplicitSweepTests.RepresentedSupport.cs`
- Create: `KhaozEngine.Game.Tests/Physics/InstalledSupportPoseTests.cs`, `SupportFootprintTests.RepresentedSource.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportEquivalenceQueryAnchors.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/support-equivalence-queries.json`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/support-exact-install-controls.txt`
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportNeighborhoodEquivalenceTests.cs`, `KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj`
- Modify: `KhaozEngine.Game.Tests/Physics/SupportFootprintExactOracle.cs`, `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodOracle.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintScenes.cs` (planar support expectations)
- Create: `docs/verification/2026-10-19-contact-followups-c.md` (anchor provenance and 51-site disposition table)

**Interfaces:**
- Produces `InstalledPoseOperator.TryCreateSupport(in RigidPose pose, out InstalledPoseOperator result) -> bool` and `InstalledPoseOperations.ProveSupportComposition(in RigidPose child, in RigidPose root, in RigidPose world) -> bool`.
- Produces `CapsuleFeatureGeometry.CaptureLeaf(Simulation simulation, TypedIndex index, in RigidPose pose, in RigidPose localPose, int leafId, in InstalledPoseOperator transform, CapsuleFeatureCaptureScratch? scratch, out CapsuleFeaturePolyhedron? leaf, bool topologyOnly = false) -> CapsuleFeatureStatus`. Existing overloads remain dual wrappers.
- Produces `TransformVertex(Vector3 point, in InstalledPoseOperator transform, out FeaturePoint world, bool exactZeroProducts = false) -> CapsuleFeatureStatus` and `TransformVertices(Vector3[] local, in InstalledPoseOperator transform, FeaturePoint[] world) -> CapsuleFeatureStatus` on CapsuleFeatureGeometry. Legacy pose wrappers retain their correspondence gate before delegating.
- Produces `SupportPolygonSourceMap(Vector3 X, Vector3 Y, Vector3 Z, Vector3 Translation)`. `SupportPolygon.BeginSource(in InstalledPoseOperator map) -> void` keeps its signature and rejects an internal non-point/mismatched map, rather than extracting a midpoint.
- Produces test-only `SupportEquivalenceQueryAnchor(string Row, Pose Pose, float Radius, float Length, float Band, Vector2 Axis, float Slope, float AlternateSlope, int? TargetId, string SceneDigest)` and `SupportEquivalenceQueryAnchors.Read(string row) -> SupportEquivalenceQueryAnchor`. JSON stores every float as its 32-bit integer pattern.
- Task 2 consumes the support factory/composition door. Task 3 consumes the frozen anchors and represented planar oracle. Public queries, lease and result signatures stay unchanged.

- [ ] **Step 1: Write the original zero-origin initial-tick regression first.**

In the new CapturedStair partial add `CapturedStairZeroOriginInitialTickUsesRepresentedSupport`.
Use `Fixture(true,true,false)`, original feet `fixture.Rotate(new(0,0.5f,-0.25f))`, unchanged
tuning and the real TickCapture acquisition route. Never offset this row by 0.001f.

```csharp
Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
Assert.Equal(MovementAvailability.Known, fixture.LastAcquisitionStatus);
Assert.All(capture.Footprints, f => Assert.Equal(CapsuleFeatureStatus.Complete, f.Result.Status));
Assert.True(Vector3.Dot(result.State.State.Position - state.State.Position, along) > 0);
Assert.Equal(new MovementSupportKey("world", "stairs"), result.State.Selection!.Value.Support);
Assert.Throws<ObjectDisposedException>(fixture.LastLease!.AssertCurrent);
```

- [ ] **Step 2: Run only that red regression before any production or oracle edit.**

```bash
build-slot --label fu-c-stairs-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CapturedStairZeroOriginInitialTickUsesRepresentedSupport'
```

Expected: assertion failure with Blocked versus Advanced, not a compilation failure. Preserve the
actual refused request and the first failure in the proof record. Existing evidence locates the
request near axis `(-0.14999999f,0)`, radius 0.15f. Do not substitute those rounded diagnostics for
the actual request operands.

- [ ] **Step 3: Freeze every original golden question while the old helpers still run.**

Instrument the existing Feature/Neighborhood query entry points with a test-only anchor recorder.
Capture row ID, probe dimensions, final Pose bits, band, axis, both slope readings, optional raw
feature target and a digest of original shape/child/pose bits. Preserve PRNG draw order and seeds.
When the anchor capture variable is set, write that fixture as well as comparing the original
support-equivalence.txt. It must not enable digest replacement or suppress a mismatch.

```bash
KHAOZENGINE_SUPPORT_ANCHOR_CAPTURE=KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/support-equivalence-queries.json build-slot --heavy --label fu-c-freeze-anchors -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~NeighborhoodsAndSupportMatchTheRecordedFixture'
```

Expected: exit 0 under baseline production and the old digest file, with an anchor for every row.
Record baseline commit, fixture SHA-256 and row count. Add
`SupportEquivalenceAnchorsKeepEveryOriginalQueryBit`, comparing all serialized int patterns and
scene digests to baseline generation. Preserve the historical anchor math inside the test-only
anchor owner so this comparison never starts using the changed support oracle. Route the regular
equivalence test through the frozen queries before changing oracle geometry. It still constructs exactly the original scenes.
Also add `ExactInstallControlsMatchBaseline` in InstalledSupportPoseTests and record complete
raw support serialization for identity, `(0,1,0,0)` and cyclic `(0.5f,0.5f,0.5f,0.5f)` before
the production edit, including both zero signs. Commit this separate control fixture and hash.

```bash
KHAOZENGINE_SUPPORT_CONTROL_CAPTURE=KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/support-exact-install-controls.txt build-slot --label fu-c-freeze-controls -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ExactInstallControlsMatchBaseline'
```

Expected: supported baseline controls and a complete bit serialization. With the variable unset,
the test compares rather than records. Never regenerate these controls after the source change.

- [ ] **Step 4: Add exact factory, boundary, composition and inverse tests.**

`SupportRepresentedFactoryMatchesEveryScalarAndWideLane` covers identity, signed-zero identities,
quaternions `(0.5f,0.5f,0.5f,0.5f)`, `(0,1,0,0)`, literal yaw quarter-turn and
`Quaternion.CreateFromYawPitchRoll(0.37f,0.1f,-0.05f)`. Compare each scalar/all-lane coefficient
bit with the independent rational coefficient graph, and support X/Y/Z with represented fields.
Check determinant, Gram and inverse enclose their independent point-map values. Retain norm,
finite/range and nonpositive-determinant rejection. Check compound world-pose bits against
`InstalledPoseOracle.Composed`, including hull wrappers and child-order controls.

`SupportRepresentedFactoryPreservesSignedZerosWithoutNewCorrespondenceRefusal` asserts stored
zero bits survive and the numerical conjugate-unit-axis comparison adds no rejection.
`RepresentedSourceMapRejectsDualInputAndUsesTwelveBitPatterns` asserts BeginSource rejects a
genuinely dual interval operator, all nine polynomial inputs are constants, twelve source bits
control equality/hash and no raw key is replaced by that equality.

`RepresentedVertexDiscKeepsClosedBoundaryAndAdjacentRadii` uses acceptance B's exact triangle
and radius. The old interval straddles membership independently. For the represented map
assert VertexDisc zero, outside at BitDecrement(radius) and inside at BitIncrement(radius),
with the production enum signs checked against exact rational membership. Only Zero/inside
are retained. Assert Complete in-domain query publication and unchanged unused sentinel tails.

`RepresentedMeshPoseMissUsesItsActualInverse` compares first materialization, cache hit, changed
pose fallback and reference capture. Assert actual inverse encloses all preimages, cross-derived
normals, unchanged triangle IDs and exact optimized/reference results. Do not assume transpose. Core assertion contracts are

```csharp
Assert.Equal(expectedCoefficientBits, scalarCoefficientBits);
Assert.Equal(expectedCoefficientBits, wideLaneCoefficientBits);
Assert.Equal(GeometrySign.Zero, boundarySign);
Assert.Equal(GeometrySign.Positive, outsideRadiusMembership);
Assert.Equal(GeometrySign.Negative, insideRadiusMembership);
Assert.Equal(beforeUnusedMembers, afterUnusedMembers);
Assert.Equal(beforeUnusedJoins, afterUnusedJoins);
Assert.Equal(baselineControlBits, currentControlBits);
```

VertexDisc evaluates distance squared minus radius squared. Positive is outside, Negative inside.

- [ ] **Step 5: Implement the represented factory and shared capture cores.**

Use one private scalar/SIMD capture implementation from both factories. The support door validates
directly and invokes the existing certificate constructor with represented columns in both slots.
Do not route it through dual TryCreate. Share the composition-enclosure implementation and retain
its actual rounded SIMD continuation. Keep the legacy factory, RealCoefficients and union intact.

Implement operator-accepting topology/transform overloads with the original expression order and
coordinate checks. Route every planar support capture/materialization door in Files through them
and ProveSupportComposition, including local-topology admission and support proposal pose checks.
Keep PosePoint only in the retained legacy wrappers. Build all cached facts from the same map.
Change source-map storage/comparer, source constants/reuse and exact identity qualification.
Do not change polygon extrema, proposal, contact, join, rectangle or pruning formulas.

- [ ] **Step 6: Update planar support expectations and run the focused green selection.**

Use rational represented geometry on installed leaf poses in planar support oracles. Keep the
real feature oracle and frozen query inputs unchanged. Run

```bash
build-slot --heavy --label fu-c-planar-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~InstalledSupportPoseTests|FullyQualifiedName~CapturedStairZeroOriginInitialTickUsesRepresentedSupport|FullyQualifiedName~RepresentedVertexDisc|FullyQualifiedName~RepresentedSourceMap|FullyQualifiedName~RepresentedMeshPoseMiss|FullyQualifiedName~SupportEquivalenceAnchorsKeepEveryOriginalQueryBit|FullyQualifiedName~InstalledPoseFeatureTests|FullyQualifiedName~InstalledPoseDomainTests'
```

Expected: exit 0, original captured tick Advanced/Complete and retained feature/domain controls
unchanged. Update the 51-site table with changed/derived-unchanged/legacy-retained dispositions.
Known old support expectations are reconciled in Task 3, never hidden by skipping tests.

- [ ] **Step 7: Stage Files paths explicitly, run the three staged guards and commit.**

Expected: `git diff --cached --check` and all guards exit 0, only the listed concern changes.
Commit `fix(support): use represented transforms for planar support`. No version or CHANGELOG edit.

### Task 2: Unify curved capture, tangent and exact-source arithmetic (CC-5, D)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/SupportNeighborhoodCurved.cs`, `KhaozEngine.Physics.Bepu/SupportCurvedSourceArithmetic.cs`, `KhaozEngine.Physics.Bepu/SupportFootprintCurved.cs`
- Modify: `KhaozEngine.Physics.Bepu/InstalledSupportGeometry.cs`, `KhaozEngine.Physics.Bepu/InstalledSupportWorldForm.cs` (curved forms)
- Modify: `KhaozEngine.Game.Tests/Physics/SupportFootprintCurvedProofOracle.cs`, `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodOracle.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintScenes.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.CurvedSource.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.TiltedSubdivisionDiagnostic.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.LyingLogs.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.TiltedRegions.cs` (synthetic source constructors and obsolete source-family expectations)
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.RepresentedCurves.cs`
- Modify: `docs/verification/2026-10-19-contact-followups-c.md`

**Interfaces:**
- Consumes Task 1's TryCreateSupport and ProveSupportComposition.
- Produces `SupportCurvedLeaf.SourceColumn -> Vector3`, replacing FeaturePoint while retaining all other fields and part IDs.
- Produces `SupportCurvedSourceArithmetic.Reset(Vector3 source) -> void`. Arithmetic Number, Sign, Checkpoint/Restore and radical/compile limits retain their contracts.
- Existing `SupportNeighborhoodCurved.Leaf`, tangent, Measure, Round, Side and Lowest signatures remain unchanged. Task 4 later supplies work ownership through constructors, not another source parameter.

- [ ] **Step 1: Write curved represented-source tests before changing capture.**

Add `RepresentedCurvedColumnFeedsEverySupportRole(bool capsule, bool aboutX, int sign)` for
cylinder/capsule, both quarter-turn axes and both ends, radii 0.1f/0.15f/0.3f, plus sphere,
upright, exact cyclic horizontal and genuine positive/negative tilt controls. Use the actual
installed leaf centre and represented Y for independent normalized-axis expectations.

```csharp
Assert.Equal(expectedColumnBits, actualColumnBits);
Assert.True(axisEnclosureContainsIndependentNormalizedColumn);
Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
Assert.InRange(sample.HeightError, 0f, 0.0005f);
Assert.Equal(expectedRawElementId, member.ElementId);
```

The bool above is a local conjunction of exact component-enclosure comparisons, not a new helper
API. Check normal/witness errors, Axis then Footprint role order, missing axis roles at misses,
finite cylinder caps, capsule hemispheres and proposal contact heights. Include neighborhood
tangents and both curved capture overloads, compound reversed order and cached/reference paths.
Add `RepresentedCurvedRadicalsRemainGuardedAfterPointSources` with an irrational normalized
column, exact slope equality and genuine bounded arithmetic failure. Singleton input does not
make a root or publication exact.

- [ ] **Step 2: Run the focused red selection.**

```bash
build-slot --label fu-c-curves-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~RepresentedCurvedColumnFeedsEverySupportRole|FullyQualifiedName~RepresentedCurvedRadicalsRemainGuardedAfterPointSources'
```

Expected before changing the public internal field type: exact column assertions expose the
non-singleton old column. Write initial assertions over the existing FeaturePoint bounds so the
red is behavioral, then adapt to Vector3 with the implementation. No compile failure is evidence.

- [ ] **Step 3: Implement point-valued curved sources and remove obsolete alternatives.**

Route both Leaf doors and compound admission to the support factory/composition proof.
Populate SourceColumn from exact represented Y, and derive Axis/Across from that same column.
Reset installs exact constants. Remove unused Source operation, `_column`, source-polynomial
variables and alternative-transform compile branches. Keep expression/radical/denominator and
storage/degree guards. Adapt `_axisSign` and every remaining SourceColumn access to Vector3.
Update synthetic constructors without reintroducing a box-valued support source. Retain generic
polynomial-box tests in their generic arithmetic owner. Recast genuinely unresolved geometric
or capacity tests separately from the now-removed transform ambiguity.

- [ ] **Step 4: Update curved independent geometry and run the green selection.**

```bash
build-slot --heavy --label fu-c-curves-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~RepresentedCurved|FullyQualifiedName~SupportNeighborhoodCurvedTests|FullyQualifiedName~CurvedOracle|FullyQualifiedName~CurvedTilted|FullyQualifiedName~LyingLog'
```

Expected: exit 0. Audit footprint/admission/lowest/tangent consumers against sites 35 to 42 and
both collectors. No production support caller may reach the dual factory or real oracle by
indirection. Preserve compiled feature/sweep controls. Record exact changed source-family
expectations and why any fallback count falls. No timing conclusion follows.

- [ ] **Step 5: Stage Files paths, run staged guards, inspect and commit.**

Commit `fix(support): use one represented column for curved certification`. No history/version edit.

### Task 3: Reconcile frozen goldens and complete stairs/safety acceptance (CC-9 to CC-11, A to E)

**Files:**
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/CapturedStairExplicitSweepTests.cs`, `KhaozEngine.Game.Tests/Locomotion/Contacts/CapturedStairEdgeOracle.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.Source.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.BandHeights.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.SourceCorrections.cs`, `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodMeshTests.cs`
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.RepresentedSafety.cs`
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/support-equivalence.txt`, `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportNeighborhoodEquivalenceTests.cs`
- Modify: `docs/verification/2026-10-19-contact-followups-c.md`

**Interfaces:**
- Consumes Task 1's frozen anchor reader, represented planar geometry and Task 2's curved geometry.
- Produces `CapturedStairEdgeOracle.AssertRepresentedDisagreement(SupportFootprintRequest request) -> void`, asserting real D < 0, represented D > 0 and the production edge predicate's represented sign on the same installed raw edge.
- Produces reviewed golden row dispositions and complete captured acceptance using unchanged public acquisition/movement/query APIs. No new production selection, memo or continuity interface.

- [ ] **Step 1: Replace the six retained refusal assertions with represented acceptance.**

Keep every Installations/Traversals row and original source/feet/targets. Remove only the special
Blocked/Unsupported acceptance branch and its final must-refuse assertion. Keep raw sweep and
shell-clear controls from AssertSupportBoundary as separate controls. Apply AssertAccepted to
the original two initial and four traversal rows. Preserve the exact two-map negative control
on the initial request and near-tangency evidence for traversal requests. The literal yaw
quaternion and matrix coefficient bits must be pinned independently, not read from a production map.
For literal `(0,0.70710677f,0,0.70710677f)`, pin c bits `0x33800000`, s bits `0x3f7fffff`
and negative-s bits `0xbf7fffff` in row-basis X `(c,0,-s)`, Y `(0,1,0)`, Z `(s,0,c)`.
Assert real D < 0, represented D > 0 and the production exact edge sign Positive at the actual request.

```csharp
Assert.All(capture.Footprints, f => Assert.Equal(CapsuleFeatureStatus.Complete, f.Result.Status));
Assert.All(steps, step => Assert.Equal(MovementStepOutcome.Advanced, step.Outcome));
Assert.True(ticks <= 240);
Assert.True(CertifiedSweepExactOracle.WithinArrivalBall(finalSupportFeet, target, 0.001f));
Assert.Equal(new MovementSupportKey("world", descent ? "approach-pad" : "landing-pad"), state.Selection!.Value.Support);
```

Keep `CapturedStairArrivalOracleRejectsThePerAxisCube` and all original off-tangency rows.
No manufactured refusal, moved query, larger frame budget or cache injection is permitted.

- [ ] **Step 2: Add safety, coincidence and exact-control regressions.**

Add `RepresentedRotatedTreadStillRejectsIndependentRoof` using the same quarter-turn lower tread,
a separate low roof and original raw proposal guards. Assert Find Refused and no lower-floor
fallback. Add `RepresentedCoincidenceKeepsRawKeysAndIntervalAuthentication` for coincident
separate statics and distinct compound children. Assert both raw members, all raw join rows,
direct-only continuity and exact request memo hit in the original lease. New lease, view,
scope/analytic fact, mutation, rebase and reused handle must miss or reject as before.

Add `RepresentedExactInstallsKeepBaselineBits` for identity, `(0,1,0,0)` and cyclic
`(0.5f,0.5f,0.5f,0.5f)`. Assert baseline raw serialization of status, both roles, witness/normal,
height/error, XZ, owner, join bits and signed zeros. Use SupportFootprintEquivalence.AssertMatchesReference
on cold/warm/interleaved/rebuilt rows. Retain nested/concurrent fixtures and zero warm allocation.
Read the exact-install control fixture frozen by Task 1, never recreate it from the new implementation.
In the coincidence assertions, coincidentRawMembers is the pair of independently identified
admitted face rows, not every face of the installed shapes. Assertion contracts are

```csharp
Assert.Equal(SupportStatus.Refused, roofSample.Status);
Assert.Equal(2, coincidentRawMembers.Count);
Assert.NotEqual(coincidentRawMembers[0].ElementId, coincidentRawMembers[1].ElementId);
Assert.Equal(baselineControlBits, currentControlBits);
Assert.Equal(0, warmAllocatedBytes);
```

For separate statics, unequal Static handles establish the raw-key distinction. The unequal
ElementId assertion applies to the two children of one compound. Never require both fields to differ.

- [ ] **Step 3: Reconcile every obsolete expectation from research section 4 explicitly.**

Rename InstalledCoefficientBoundaryRefusesAtomically to a represented-boundary decision test
and assert B's exact zero. Replace SourceCoefficientUncertainContactBandRefusesAtomically with
the independently derived represented verdict. Retain
`UncertainContactBandSignRefusesBothSpans` or a genuine unresolved-height replacement with the
same sentinels. Select SourceCorrections walls by installed geometry and exact orientation,
replace 4/9-variable assertions with nine constants, and keep real exact-fallback tests.
Recompute the mesh sliver's exact area. Keep raw triangle 1 when nonzero and relevant, while
exactly degenerate triangles still skip. Never add a sliver or small-normal threshold.

- [ ] **Step 4: Review old/new digest differences under the frozen questions.**

Run `NeighborhoodsAndSupportMatchTheRecordedFixture` once as a focused comparison after geometry
changes. Expected: explained rotated support mismatches before editing the fixture. For each
changed row, record old/new digest, identical input bits/scene digest, changed fields and an
independent represented derivation in the proof record. The exact comparison command is

```bash
build-slot --heavy --label fu-c-golden-diff -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~NeighborhoodsAndSupportMatchTheRecordedFixture'
```

Reject any changed identity mesh,
identity fan or feature row. Update individual reviewed rows only. Do not set the digest capture
variable or overwrite the fixture with generated output.

- [ ] **Step 5: Run captured, golden and safety green selection.**

```bash
build-slot --heavy --label fu-c-acceptance-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~RepresentedRotatedTread|FullyQualifiedName~RepresentedCoincidence|FullyQualifiedName~RepresentedExactInstalls|FullyQualifiedName~SupportNeighborhoodEquivalenceTests|FullyQualifiedName~FootSupportBoundaryTests|FullyQualifiedName~FootSupportCompletenessTests|FullyQualifiedName~FootSupportNeighborhoodTests|FullyQualifiedName~SupportFootprintTests|FullyQualifiedName~SupportNeighborhoodMeshTests|FullyQualifiedName~ExactRepeatMemoTests|FullyQualifiedName~JoinedEdge|FullyQualifiedName~ContactLeasedQueryTests'
```

Expected: exit 0, all original six boundary rows now pass acceptance and identity/feature goldens
are unchanged. Confirm capacity 256/257, witnesses 512, sentinel tails, hidden support, roof,
ring-only, finite curved ends, exact band/slope endpoints, stale/excluded/rebased reads and warm
allocation remain covered. Update every item in the 51-site disposition table. No full suite yet.

- [ ] **Step 6: Stage Files paths, run staged guards and commit the reviewed acceptance.**

Commit `test(support): pin represented geometry acceptance and golden changes`.

### Task 4: Add cumulative curved work enforcement and refusal diagnostics (CC-6, CC-7, F)

**Files:**
- Create: `KhaozEngine.Physics.Bepu/SupportCurvedWork.cs`
- Create: `KhaozEngine.Physics/SupportCurvedWorkDiagnostics.cs`
- Modify: `KhaozEngine.Physics/SupportQueryDiagnostics.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportCurvedSourceArithmetic.cs`, `KhaozEngine.Physics.Bepu/SupportCurvedRootRegions.cs`, `KhaozEngine.Physics.Bepu/SupportCurvedSurfaceAdmission.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintCurved.cs`, `KhaozEngine.Physics.Bepu/SupportTiltedCurved.cs`, `KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.cs`, `KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.Diagnostics.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintCollector.cs`, `KhaozEngine.Physics.Bepu/SupportFootprintCollector.Geometry.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprint.cs`
- Create: `KhaozEngine.Game.Tests/Physics/SupportCurvedWorkTests.cs`, `SupportFootprintTests.CurvedWork.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.CurvedSource.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.LyingLogs.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.TiltedRegions.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.TiltedSubdivisionDiagnostic.cs` (explicit work-owner construction)
- Modify: `docs/verification/2026-10-19-contact-followups-c.md`

**Interfaces:**
- Produces `SupportCurvedWork` with `internal const int MaximumWork = 1_048_576`, constructor `(int limit = MaximumWork)`, `Begin() -> void`, `TryCharge(SupportCurvedWorkKind kind) -> bool`, `Fail(SupportCurvedWorkFailure reason) -> void`, `ObserveStorage(int rootHighWater, int admissionHighWater) -> void`, `Failed -> bool`, `Snapshot -> SupportCurvedWorkObservation`.
- In Physics, produces `enum SupportCurvedWorkKind { New, Sign, Split, Compile, RootCell, Refine, AdmissionCell }` and `enum SupportCurvedWorkFailure { None, WorkLimit, RootStorage, TerminalRoots, AdmissionStorage }`.
- Produces `SupportCurvedWorkObservation(long Consumed, int Limit, long New, long Sign, long Split, long Compile, long RootCells, long Refine, long AdmissionCells, int RootStorageHighWater, int AdmissionStorageHighWater, SupportCurvedWorkFailure Failure, CapsuleFeatureStatus FootprintStatus)`.
- Produces `SupportQueryDiagnostics.SetCurvedWorkObserver(Action<SupportCurvedWorkObservation>? observer) -> void` and `ObserveCurvedWork(in SupportCurvedWorkObservation observation) -> void`. Extend ThreadState/Fresh/Exchange/Reset to preserve or clear observation state exactly like the existing Find observer.
- Produces collector constructor `SupportFootprintCollector(int curvedWorkLimit = SupportCurvedWork.MaximumWork)`. A smaller positive limit is internal test injection only. Begin calls work.Begin once. Public query signatures do not change.
- Arithmetic, root, surface, footprint and tilted owners take the same `SupportCurvedWork work` in their constructors. Standalone geometry tests explicitly create and Begin an owner. `Reset(Vector3 source)` retains Task 2's signature and never resets work. Task 5 consumes the diagnostic observations.

- [ ] **Step 1: Add budget, latch, storage and atomicity tests.**

Add `CurvedWorkPolicyDerivesFromExistingQueryAndGraphCeilings` and expose the existing graph
node constant internally without changing its value. Assert

```csharp
Assert.Equal(256, SupportFootprintResult.MaximumElements);
Assert.Equal(4096, SupportCurvedSourceArithmetic.MaximumNodes);
Assert.Equal(256 * 4096, SupportCurvedWork.MaximumWork);
Assert.Equal(1048576, SupportCurvedWork.MaximumWork);
```

`CurvedWorkChargesEachCategoryAndLatchesBeforeTheNextEvent` injects limit 7, performs one
charge of each kind and attempts an eighth. Assert Consumed 7, sum of categories 7, WorkLimit,
no eighth event execution and repeated failed charges leave the snapshot unchanged. Begin for
a genuinely new query clears it. Reject limits <= 0 and limits above MaximumWork.

`CurvedWorkExhaustionSurvivesAllSubsolvesWithDiagnosticsOff` exhausts a small shared owner with
diagnostics disabled, then attempts nested checkpoints/Restore, Reset, PolynomialSign, another
root solve, another leaf/part, both proposal contacts and BeginBelowBandPass. Assert no work
refund, no resumed successful arithmetic and the same latched reason. A new collector/query
and an independent nested scratch owner start separately. No thread-static enforcement budget.

`CurvedWorkRefusesBeforePublishingEitherSpan` injects limit 32 through the collector constructor,
uses an ordinary represented tilted fixture requiring more events, and exercises Collect then
the existing refusal path. Preserve complete member copies and join words 73UL as sentinels.
Assert Unsupported, Members/Witnesses 0, no member/join/tail writes and Find maps the corresponding
backend refusal to Refused. Keep the exact public mapping. Do not use a throwing counter hook.

`CurvedRootStorageFailureReportsItsOwnReason` drives the existing root-function seam with an
unresolved endpoint/derivative fixture and unexhausted work. Assert 1,024 storage high water,
RootStorage, Consumed < Limit and atomic zero output. Distinguish terminal-root and admission
storage fixtures too. First failure wins even after Restore and a later attempted work charge. Pin these outcomes

```csharp
Assert.Equal(7, exhausted.Consumed);
Assert.Equal(SupportCurvedWorkFailure.WorkLimit, exhausted.Failure);
Assert.Equal(exhausted, afterRestoreAndLaterSubsolves);
Assert.Equal(CapsuleFeatureStatus.Unsupported, refused.Status);
Assert.Equal(0, refused.Members);
Assert.Equal(0, refused.Witnesses);
Assert.Equal(memberSentinels, membersAfter);
Assert.Equal(joinSentinels, joinsAfter);
Assert.Equal(1024, storage.RootStorageHighWater);
Assert.Equal(SupportCurvedWorkFailure.RootStorage, storage.Failure);
Assert.True(storage.Consumed < storage.Limit);
```

- [ ] **Step 2: Run the new red work tests.**

```bash
build-slot --label fu-c-work-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CurvedWork|FullyQualifiedName~CurvedRootStorageFailureReportsItsOwnReason'
```

For new interfaces, introduce the inert owner/observation declarations first so these tests
compile and fail the charge/latch/atomicity assertions. Undefined symbols are not the red proof.
Do not run the historical 3.3-million-event query to establish this regression.

- [ ] **Step 3: Implement the owner and constructor wiring.**

Provision one owner per collector scratch and pass it to both arithmetic families, tilted
geometry, root regions and admission. Begin once for the entire complete request, outside both
passes and all raw parts. The old node/compile graph resets retain their own behavior but cannot
clear Failed or Consumed. A memo hit never enters this backend work path. Direct footprint
queries receive an independent owner. Reject invalid injected limits without a public knob.

- [ ] **Step 4: Charge bounded events and propagate first failure atomically.**

At each CC-6 entry point, TryCharge precedes work. A denied charge immediately returns the
existing unresolved/false result without executing that event. `Restore` must not turn a
work-exhausted owner into a usable expression graph. Explicitly check the sticky owner at
all arithmetic reads and early-success shortcuts, at later sub-solves and before Publish.
Record distinct root frontier, terminal root and admission storage failures when their existing
limits fire, while preserving public Unsupported/CapacityExceeded semantics. No exception abort.

In QuerySupportFootprintCore, observe the collector snapshot on every normal completion/refusal
before scratch restoration, setting FootprintStatus from the actual returned result. The owner
snapshot alone does not assert a completed footprint. Keep callbacks opt-in and outside geometry authority. Capture/restore
the observer with ThreadState, and include it in existing nested/concurrent diagnostic fixtures.
Enforcement and category counts remain active when observations are disabled.

- [ ] **Step 5: Assert certified corpus fit, parity and zero warm allocation.**

Add `CertifiedCurvedStrataFitTheCumulativeQueryCeiling`, covering existing sphere/upright,
exact-horizontal, tilted side/cap/round, slope-boundary, both proposal contexts and below-band
fixtures plus 256 admitted raw members with 512 witnesses. For each successful optimized and
reference row assert Complete, Failure None, Consumed <= 1,048,576 and category sum equality.
Retain the 257-member refusal and zero warm allocation. This is a fixed functional corpus,
not repeated invocations until failure. A real excess requires a proof/work fix, not a larger limit.

```bash
build-slot --heavy --label fu-c-work-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CurvedWork|FullyQualifiedName~CurvedRootStorage|FullyQualifiedName~CertifiedCurvedStrataFitTheCumulativeQueryCeiling|FullyQualifiedName~CurvedRawCapacityUsesTwoSlots|FullyQualifiedName~RawCapacityPublishesAtomically|FullyQualifiedName~SupportFootprintTests|FullyQualifiedName~SupportNeighborhoodEquivalenceTests'
```

Expected: exit 0. Record the seven category totals, maximum consumed corpus row and all refusal
reasons in the proof record. No full suite or timing run.

- [ ] **Step 6: Stage Files paths, run staged guards, inspect and commit.**

Commit `fix(support): cap cumulative curved certification work and expose exhaustion`.

### Task 5: Gate and conditionally certify the whole-capsule crown (CC-8, G)

**Files:**
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactFollowupEndWorkTests.cs`
- Modify only on certificate branch: `KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.Round.cs`
- Create only on certificate branch: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.CapsuleCrown.cs`
- Modify: `docs/verification/2026-10-19-contact-followups-c.md`

**Interfaces:**
- Consumes Task 2's represented curved geometry and Task 4's observer and category definition.
- Produces test-only `ContactFollowupEndWorkTests.Observe(bool capsule, float x) -> (SupportSample Sample, SupportCurvedWorkObservation Work)`, installing one fresh scene and lease and making exactly one Find.
- Only on the required branch, produces `SupportTiltedCurvedGeometry.TryGlobalCrown(double slope, out Witness witness, out GeometryInterval maximum) -> bool` in the Round partial. False means no certificate and requires the existing general path, never absence of support. Call only for the capsule footprint role before Side.
- Produces a committed capsule/cylinder six-row gate record. Task 6 consumes the recorded cylinder verdict. Neither branch changes axis/Lowest or whole-surface admission interfaces.

- [ ] **Step 1: Measure the exact new-contract Find and cylinder sibling before any certificate edit.**

Use CC-8's exact scene, x bits, FeetY, radius, reaches and cosine. Observe three x values for
each of two shapes, with a fresh scene/lease per row and no controller or traversal. Counters
are functional evidence. Assert Find count 1 and proposals 2 and record status, all work categories,
Consumed, Limit, Failure and cell high water. A budget refusal is a failed gate, never a pass.

```bash
build-slot --label fu-c-end-gate -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~RepresentedEndTangencyCounterGate'
```

The original gate selected the crown branch under an all-Walkable premise. FC-R10 retains
that branch and corrects the final gate. Capsule rows are all Walkable. Cylinder statuses
are independently derived as Walkable, None, None. Every row asserts Complete footprint
and Failure None. Status assertions are separate from integer cost. The capsule bound is
`critical.Consumed <= 10L * Math.Min(left.Consumed,right.Consumed)`.
The cylinder bound is per seam row against ten times its same-test interior x = -0.25f row.

- [ ] **Step 2: Retain the crown branch selected under FU-R4.**

The capsule certificate remains required by FC-R10. Task 6 owns the distinct expensive
cylinder admission search and preserves all public outputs. Do not treat capsule evidence
as cylinder evidence or accept WorkLimit as a cost repair.

- [ ] **Step 3: On the required branch, add source-proof crown tests and run them red.**

Add `CapsuleCrownAttainsTheGlobalHeightBeforeSideIsolation` at the critical Find. Assert the
independent global expression is attained within the original disc, correct hemisphere and
normal Y [1,1], with X/Z intervals containing zero. Do not demand exact-zero outward endpoints.
Assert global enclosure containment, HeightError <= 0.0005f, original raw key, role order and
optimized/reference equality. Include both axes and physical ends without assuming symmetry.

Add `CapsuleCrownDeclinesWhenGlobalAttainmentIsUnproved` with exact positive/negative Y, exact
zero Y (general/horizontal path), endpoint projection at the original disc boundary and its
adjacent represented query positions, and nonzero lateral displacement. Independently classify
each point with the rational/radical oracle. Outside or unresolved must decline. Because FU-R2
removed production source boxes, a synthetic interval spanning Y zero belongs only to generic
arithmetic tests, not a reintroduced SupportCurvedLeaf constructor.

```csharp
Assert.True(crownCertified);
Assert.Equal(1d, witness.Normal.Y.Lower);
Assert.Equal(1d, witness.Normal.Y.Upper);
Assert.True(witness.Normal.X.Lower <= 0 && witness.Normal.X.Upper >= 0);
Assert.True(witness.Normal.Z.Lower <= 0 && witness.Normal.Z.Upper >= 0);
Assert.False(outsideOriginalDiscCertified);
Assert.Equal(SupportStatus.Refused, independentRoofSample.Status);
```

```bash
build-slot --label fu-c-crown-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CapsuleCrown'
```

Introduce an inert false-returning signature first if needed. Expected: attainment test fails
behaviorally, while decline controls retain their meaning.

- [ ] **Step 4: Implement the global source certificate without skipping other obligations.**

Use the exact sign of normalized axis Y to choose the higher end, prove nearest horizontal gap
to the original disc is exactly zero, prove hemisphere and slope and reuse RoundEnd's attained
vertical radial vector. Establish the capsule-wide upper expression identity
`centre.Y + abs(axis.Y)*halfLength + radius`. Round only through existing publication.
Apply this canonical certificate on both optimized and reference paths. Do not compare outward
height endpoints for equality or replace complete admission, the axis role, lowest contacts,
proposals, roof checks or other members. A failed certificate falls through.

- [ ] **Step 5: Add the public roof/arrival proofs and run the capsule green selection.**

`CapsuleCrownNeedsOriginalDiscAttainmentAndIndependentRoofClearance` uses a high capsule and an
independent low roof. Assert public Find Refused, unchanged roof guard and no accepted lower
floor, on both branches. On the certificate branch also assert the helper declines outside
the original disc even if inside the admission ring. Keep genuine unresolved atomic cases.
Add `RepresentedCapsuleRoundedEndTraversalKeepsTheMillimetreBall` using the existing dry crest
walk/down sequence with default tuning, 240 ticks and exact Euclidean 0.001f arrival. Never
approach the 0.6 m crest from the floor as if it fit a 0.4 m step.

```bash
build-slot --heavy --label fu-c-crown-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CapsuleCrown|FullyQualifiedName~RepresentedCapsuleRoundedEndTraversal|FullyQualifiedName~CurvedOracle|FullyQualifiedName~FootSupportBoundaryTests'
```

Expected: capsule critical and both neighbors Walkable within the 10x integer bound.
FC-R10 retains the selected certificate branch. The combined gate is mandatory after Task 6
and asserts the independent cylinder statuses separately from the interior-reference cost bound.

- [ ] **Step 6: Stage only branch-produced Files paths, run staged guards and commit.**

No-certificate commit `test(support): pin represented end tangency work bounds`.
Required-branch commit `fix(support): certify attained whole-capsule crowns before side isolation`.
Record which branch ran and its exact six-row evidence. No owner choice is required.

### Task 6: Repair cylinder admission cost without changing exact statuses (CC-8, G, FC-R10)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/SupportCurvedSurfaceAdmission.cs`, `KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.Admission.cs`
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.CylinderAdmissionWitness.cs`
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactFollowupEndWorkTests.cs`, `docs/verification/2026-10-19-contact-followups-c.md`

**Interfaces:**
- Consumes Task 5's recorded cylinder gate and Task 4's shared work owner.
- Produces `SupportTiltedCurvedGeometry.HasFiniteAdmissionWitness(SupportCurvedShape shape, int capKind, double x, double z, double radius, double bottom, double top, bool requireUpward) -> bool` in the Admission partial. True certifies attained membership only, false leaves complete surface coverage unchanged.
- Reuses `SurfacePointInside(int kind, double a, double b, double x, double z, double radius, double bottom, double top) -> bool` and `CapUniformlyUp(int kind) -> bool`. No sampled maximum, negative exclusion or new member type.

- [ ] **Step 1: Apply the recorded gate without repeating unchanged measurements.**

FC-R10 supersedes the all-Walkable premise. The independent rational/radical oracle proves
Walkable, None, None for bf333332, bf333333 and bf333334. Production already agrees.
Preserve those statuses and record the admission cost of 1,696 cells per Find, comprising two
passes of 846 barrel search cells and one cell for each cap. Add
`CylinderEndTangencyHasAnAttainedFiniteAdmissionWitness`. Before production edits, serialize
all public result bits at a251a3bf4, including both roles, witnesses, normals, heights/errors,
XZ, owner and joins. Require each seam row's Consumed to be at most ten times the same
fixture's interior cylinder row at x = -0.25f, measured in the same test. Status and cost
assertions are separate. Capsule statuses and the smaller-adjacent-neighbor bound stay unchanged.

- [ ] **Step 2: Add safety tests and run the focused regression red for this cost repair.**

`CylinderAdmissionWitnessNeverCertifiesAbsenceOrTheWrongPart` includes both finite ends, both
quarter-turn axes, positive/negative Y, cap versus side, requireUpward, closed radius endpoints,
outside column and independent roof controls. A false helper result must retain the full path.
Only the matching cap/barrel may supply a witness. Assert no roof or final maximum/proposal bypass.

```csharp
Assert.True(actualPartAdmissionCertified);
Assert.False(outsideColumnCertified);
Assert.False(downwardCapAcceptedForUpwardCoverage);
Assert.Equal(SupportStatus.Refused, independentRoofSample.Status);
Assert.Equal(independentOracleStatus, row.Sample.Status);
Assert.True(row.Work.Consumed <= 10L * interior.Work.Consumed);
```

```bash
build-slot --label fu-c-cylinder-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CylinderEndTangencyHasAnAttainedFiniteAdmissionWitness|FullyQualifiedName~CylinderAdmissionWitnessNeverCertifiesAbsenceOrTheWrongPart'
```

Expected: the interior-reference cost assertion fails before the repair. Public status and bit comparisons pass. Use an inert helper
declaration first if needed, never a compiler error as evidence.

- [ ] **Step 3: Certify finite surface membership before subdivision.**

For the barrel, offer the four actual source-surface points with kinds 0/1, axial a -1/+1
and lateral b 0. For an individual cap, offer only that cap's centre and four cardinal rim
points `(a,b)=(0,0),(1,0),(-1,0),(0,1),(0,-1)`. Use existing SurfacePointInside source
expressions and exact inclusive inequalities, checking CapUniformlyUp for requireUpward.
Accept only a proved attained point inside the requested radius and vertical column.
These are sufficient existence certificates, never sampled absence, coverage or maximum claims.
If none proves membership, retain the complete original cell traversal and its work/refusal
policy. Charge all source work to the existing owner. No tolerance or new storage is needed.

If this evidence-grounded candidate set does not repair the captured sibling, preserve the
exact failing request and stop acceptance for proper geometric investigation. It is not permission
to enlarge limits, omit the sibling or replace its failure with a budget refusal.

- [ ] **Step 4: Strengthen and run the combined gate and admission protection selection.**

```bash
build-slot --heavy --label fu-c-cylinder-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CylinderAdmissionWitness|FullyQualifiedName~CylinderEndTangency|FullyQualifiedName~RepresentedEndTangencyCounterGate|FullyQualifiedName~CurvedOracle|FullyQualifiedName~FootSupportBoundaryTests'
```

Expected: exit 0, every footprint Complete with Failure None. Independent statuses are capsule
all Walkable and cylinder Walkable, None, None. Each cylinder seam row consumes at most ten
times the same-test interior reference, and all public bits equal the pre-edit capture. Capsule
smaller-neighbor cost, source/member/tail safety, zero warm allocation and SC-9 remain exact.
Record before/after counters. If the bound fails, report the subdividing part and stop without
changing any limit.

- [ ] **Step 5: Stage only required Files paths, run staged guards and commit if changed.**

Commit `fix(support): certify finite cylinder admission before surface subdivision`.
Record before/after costs, frozen public bits, safety controls and exact test commands in the proof record.

### Task 7: Publish contract dispositions and finish staged 21.5.1 verification (CC-12)

**Files:**
- Modify: `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md` (SG-23 dispositions)
- Modify: `docs/design/CONTACT-CONTROLLER-SUPPORT-CURVED-PROOFS-2026-10-11.md` (source/work and conditional certificates)
- Modify: `docs/design/CONTACT-CONTROLLER-FOLLOWUPS-2026-10-18.md`, `docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md` (current #1494 status, historical evidence retained)
- Modify: `KhaozEngine.Physics.Bepu/README.md`, `docs/USING-KHAOZENGINE.md`
- Modify: `docs/design/CONTACT-CONTROLLER-FOLLOWUPS-C-2026-10-19.md`, `docs/INDEX.md`, `docs/verification/2026-10-19-contact-followups-c.md`
- Modify: `CHANGELOG.md` (existing 21.5.1 entry and date only)

**Interfaces:**
- Consumes accepted Task 1 to 6 commits, the completed 51-site audit, per-row golden derivations and the final integer gate.
- Produces the living represented-support contract, SG-23 derived dispositions, honest staged history and once-only full verification record. No production API, version, tag or integration action.

- [ ] **Step 1: Run the final focused integration gate if Task 6 did not already run it after the last code change.**

```bash
build-slot --heavy --label fu-c-final-focused -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~RepresentedEndTangencyCounterGate|FullyQualifiedName~CapturedStairExplicitSweepTests|FullyQualifiedName~RepresentedCapsuleRoundedEndTraversal|FullyQualifiedName~SupportNeighborhoodEquivalenceTests'
```

Expected: exit 0. Do not repeat this unchanged check if the preceding accepted task already
ran the same coverage after the final production edit. Confirm all mandatory rows execute,
the conditional task disposition is explicit and every CC requirement has evidence.

- [ ] **Step 2: Update the current contracts and SG-23 without rewriting historical results.**

Explain represented affine source versus retained feature/sweeps source, represented normalized
Y for circular primitives, unchanged protections and atomic work refusal. Record CC-9's six
expectation classes before describing new acceptance. Include both conditional task dispositions,
1,048,576-event derivation and visible storage reasons. Leave SG-12/#1334, native G1b, adoption,
navigation and release separate. Search all Markdown for `real quaternion`, `source box`,
`SourceColumn`, `#1494`, `#1487`, `quarter-turn` and `curved` and correct invalid living claims.
List any additional affected living doc explicitly before staging it. Historical measurements stay.

- [ ] **Step 3: Append the 21.5.1 history and roll its date without a version bump.**

Re-read Directory.Build.props and newest tag. Expected staged version is 21.5.1. Preserve that
pin and every existing entry. Add the date line `2026-10-19` beneath its heading and one bullet
about represented-transform exact-edge support and bounded curved certification (#1494, #1487).
Replace the superseded staged bullet sentence that still calls exact tread tangency open.
Describe a crown only if it was actually implemented. No release/tag action follows.

- [ ] **Step 4: Run the full Release build, full Game.Tests project and format verification once.**

```bash
build-slot --label fu-c-final-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label fu-c-final-suite -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
build-slot --heavy --label fu-c-final-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: zero warnings/errors, full discovered Game.Tests selection with no failures and format
exit 0 without changes. Record actual counts/skips and exits. No test loop or retry of an unchanged
failure. A changed failure gets deterministic investigation, then only the necessary changed
checks. Neither design drafting nor intermediate tasks run these full finish commands.

- [ ] **Step 5: Stage explicit changed paths and run staged and tree governance checks.**

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

Expected: all exit 0. Inspect the complete diff and ensure no version, historical timing,
consumer, sweep/feature contract, tolerance or raw identity change slipped in.

- [ ] **Step 6: Commit and return the implementation evidence to the orchestrator.**

Commit `fix(support): record represented support and bounded Find acceptance`.
Return task commits, first red, focused green commands/exits, complete captured/golden/work
records, conditional dispositions and once-only full verification. No push, merge, GitHub write
or release by the worker. The orchestrator independently reviews and integrates.

## Plan self-review and handoff

CC-1 to CC-4 map to Task 1, CC-5 to Task 2, CC-9 to CC-11 and acceptance A to E to Tasks 1
to 3, CC-6/CC-7 and F to Task 4, CC-8/G to Tasks 5 and 6, and CC-12 to Task 7.
All five Review Focus items have named tests in their owning tasks. Internal interfaces preserve
the shared legacy door, point source signatures and query lifetime. The initial red precedes any
production edit or oracle update. Anchors freeze before golden answers change. The optional crown
is gated after the full FU-R2 conversion and counters, with no-certificate and required branches
specified. No final acceptance follows from a forced refusal or an unexplained golden change.

The design author commits only this plan, its spec and the index row. Implementation starts only
after the owning orchestrator accepts that design. This document grants no new execution, release,
push, merge, timing or load authorization.
