# Contact Controller Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair #1429 pitched-plate support, #1455 lying-log support and #1454 joined-edge pacing without weakening contact-controller safety.

**Architecture:** Certify irrelevant projected polygon strata before asking for their height or orientation, and retain source equations that prove tilted curved endpoints attained. Copy the selected member's direct neighbor keys from the authenticated footprint into the support sample, then apply the existing grade excess when either endpoint proves the pair. Keep selection, raw provenance, numerical bounds and unjoined accounting unchanged.

**Tech Stack:** C#/.NET 10, System.Numerics, Bepu installed geometry, existing interval/source-polynomial arithmetic, xUnit headless Game.Tests.

**Spec:** `docs/design/CONTACT-CONTROLLER-FOLLOWUPS-2026-10-18.md`, CF-1 to CF-7. Read it with SG-1 to SG-24, SC-17 and ruling 4a-9 before implementation.

## Global Constraints

- preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity.
- No tolerance, band, epsilon or limit may be widened.
- A proper fix beats a workaround even when larger.
- No sampled maximum, no snapping a quarter turn, no small-normal threshold.
- Relevant uncertainty still refuses atomically (SG-7).
- No version or CHANGELOG change in the plan's tasks except the final one, which adds a CHANGELOG bullet riding the staged version in Directory.Build.props.
- Work only in `/Users/antonio/KhaozEngine/.worktrees/contact-followups` on `fix/contact-followups`. Start each task with `pwd`, `git rev-parse --abbrev-ref HEAD` and `git status --short`. Stop on another branch or unexplained tracked edits. Preserve the prior accepted task commits.
- The starting commit is `dcb0599e3a0bcd881cd268ac3b3a72b3ff49ba82` plus this design commit. Never reset to it. Task order is #1429, #1455, #1454, traversal regressions. One writer owns shared files at a time.
- Every build and test uses build-slot. Wide filters and full suites use its heavy lane. A task's full KhaozEngine.Game.Tests suite runs once after its focused red/green work, preceded by the Release solution build. Do not run suites in loops or start timing, stress or hosted workflows.
- Preserve the 0.0005 m half-skin HeightError ceiling, 0.0001f contact band, 256 raw members, 512 witness slots and current root/cell/candidate limits. No new tolerance knob or dependency.
- A task worker commits explicit owned paths, never pushes, merges, tags, changes branches or edits a consumer. Integration and issue closure belong to the owning orchestrator after review. No release or navigation bake.

## Review Focus

- A projected sliver touching the closed admission disc or only the 0.1 mm ring must retain its context/cap/guard role. Task 1 adds `ProjectedSeparationKeepsClosedTangencyAndRingContext`.
- A tiny signed installed tilt and a stationary region straddling disc closure must not promote possible feasibility to attained support. Task 2 adds `LyingLogBoundaryIdentityDoesNotCertifyAClippedOrUnprovedRoot`.
- Coplanar or graph-connected surfaces separated by a crack must still charge their real crossing rise unless the selected pair has a direct geometric join. Task 3 adds `JoinedEdgeNeedsTheDirectSelectedPair`.
- Scratch reuse, an exact-repeat memo and a reacquired same-generation lease must not change or revive a predecessor certificate. Task 3 adds `JoinedEdgeEvidenceSurvivesScratchButNotItsInterval`.
- A dry-controller success must also work through the scoped leased route at the reported axis, including a slow final approach. Task 4 adds `LeasedPlankAndLogApproachesArriveWithinOneMillimetre`.

---

## File structure and proof conventions

`SupportProjectedPolygonSeparation.cs` owns orientation-independent whole-face exclusion. The existing
polygon extrema owner retains membership, height and witness enumeration. `SupportTiltedCurvedGeometry`
retains the new endpoint tag in its existing bounded candidate storage. `SupportContinuity.cs` owns
bounded value evidence for the selected member's direct joins. The exact-repeat memo copies that
value without owning geometry or changing its key. `GroundSeat` consumes it for pacing and discrete-step accounting.
Test files are split by these three concerns, with a fourth headless traversal fixture.

Use `SupportFootprintScene.Install` and `InstalledPoseOracle` to read installed float shapes/poses.
Polygon expectations use `SupportFootprintExactOracle` and `SupportFootprintOracle.Polygon`/`Select`.
Curved expectations use `SupportFootprintCurvedProofOracle.Installed` and rational enclosures from
`SupportFootprintTiltedOracle.Enclose`. Extend the independent oracle if its locator loses the same
endpoint identity, never call the production separation, clipping, root or selection implementation
to compute an expectation. Numerical locators do not certify attainment.

In the assertion snippets below, `expected` is the independent installed-geometry support, `sample`
is public Find's result and `member` is its raw footprint record. Local variable names do not name
new production APIs. Check `expected.HeightLower` and `expected.HeightUpper` are contained by
`sample.Height +/- sample.HeightError`. Compare witness and normal using their published component
errors and the rational oracle enclosure, never an extra comparison epsilon. For exact identical
installed inputs compare all result fields bitwise. Across partitions compare certified geometry
and the correct local raw owner, never demand equal handles from different worlds.

Prepare `local-feed` with `mkdir -p local-feed` before the first restore. Leave
`KHAOZ_SUPPORT_COST_ARTIFACT` unset so the ordinary suite does not start timing.

### Task 1: Certify pitched-plate exclusions (#1429, CF-1, CF-2, CF-7)

**Files:**
- Create: `KhaozEngine.Physics.Bepu/SupportProjectedPolygonSeparation.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintPolygon.cs` (`Measure`, `Extrema`)
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintSourcePredicates.cs` (make its sealed declaration partial for the cohesive separation extension)
- Create: `KhaozEngine.Physics.Bepu/SupportFootprintSourcePredicates.ProjectedSeparation.cs`
- Test: create `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.PitchedPlates.cs` in the existing partial `SupportFootprintTests`
- Modify: `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md` (SG-23 derived #1429 disposition)

**Interfaces:**
- Consumes: `SupportFootprintPolygon.Measure(SupportPolygon, in SupportFootprintRequest, out SupportFootprintMember) -> GeometrySign` and its bounds overload, `SupportFootprintSourcePredicates`, existing private `Interior` and `OnPlane`.
- Produces: `SupportProjectedPolygonSeparation.Certify(SupportPolygon polygon, FeatureNumber axisX, FeatureNumber axisZ, FeatureNumber radius) -> GeometrySign`. Positive proves strict whole-projection separation, Zero proves closed tangency, Negative proves intersection, Unresolved supplies no exclusion. Only Positive skips the face. Other results continue through complete admission.
- Produces: `SupportFootprintSourcePredicates.ProjectedSeparation(SupportPolygon polygon, double nx, double nz, FeatureNumber axisX, FeatureNumber axisZ, FeatureNumber radius) -> GeometrySign`. Positive proves this proposed halfplane strictly separates all source vertices from the disc, Zero means its support bounds touch, Negative means it cannot separate, and Unresolved means the sign remains unproved. Only Positive proves whole-face exclusion. A failed proposal is not proof the face intersects.
- Public footprint and Find signatures remain unchanged. Task 4 relies on corrected ordinary plate support.

- [ ] **Step 1: Add the failing plate regressions and their independent expectations.**

Add `PitchedPlateSupportsAcrossPartitions(float yaw, float pitch, string placement, string partition)`.
Rows are yaw 0.37f with pitch -0.1f, -0.05f, 0.05f, 0.1f, and yaw 0.77f with pitch
-0.10471976f, -0.05235988f, 0.05235988f, 0.10471976f. Use all three partitions
`compound`, `split`, `standalone` and placements `floating`, `resting`, `intersecting`.
The compound has parent `(0,-1,0)`, floor half extents `(2,1,2)` at identity, and plate half
extents `(0.25f,0.05f,0.25f)` at child XZ `(0.1f,0.05f)` with
`Quaternion.CreateFromYawPitchRoll(yaw,pitch,0)`. Floating child Y is 1.1f, resting child Y is
`1 + cos(pitch)*0.05 + abs(sin(pitch))*0.25`, rounded once to float, and intersecting Y is 1.05f.
Split uses the same composed world pose with a separate floor. Standalone omits the floor.
Read the actual installed pose independently for every partition.
Query axis `(0,0)`, FeetY 0f, foot radius 0.2f, both reaches 0.4f, cosine `MathF.Cos(MathF.PI/4)`.

```csharp
Assert.Equal(SupportStatus.Walkable, sample.Status);
Assert.Equal(expected.Static, sample.Static);
Assert.Equal(expected.ElementId, sample.FeatureId);
Assert.True((double)sample.Height - sample.HeightError <= expected.HeightLower);
Assert.True((double)sample.Height + sample.HeightError >= expected.HeightUpper);
Assert.InRange(sample.HeightError, 0f, 0.0005f);
```

Run the same assertions with the reference evaluator, cold/warm scratch, an intervening different
query and a rebuilt world. Preserve `CompoundChildPosesKeyTheWorldForm` as a yaw-only control.

- [ ] **Step 2: Run the public reproduction before changing production.**

```bash
build-slot --label cf-1429-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~PitchedPlateSupportsAcrossPartitions'
```

Expected: failing Walkable assertions with actual Refused, including standalone rows. Preserve the
failing names/output in the task report. A compile failure does not establish this reproduction.

- [ ] **Step 3: Add branch and safety tests.**

Add `PitchedPlateDiscardsOutsideExtremumBeforePlaneHeight` for yaw 0.37f/pitch 0.05f,
0.1f and yaw 0.77f/pitch 0.05235988f. Capture the offending side from installed geometry,
prove its proposed disc extremum is outside the finite face, and assert the complete query is
Complete and Find Walkable. The side is raw element 256 in the compound and 0 standalone.
Add `PitchedPlateSeparatesOutsideFaceBeforeOrientation` for yaw 0.77f/pitch 0.10471976f
resting geometry. Assert a strictly positive separating margin from all exact projected vertices
to the expanded disc, and `GeometrySign.Positive` from the new separation owner even when the
captured orientation is Unresolved. Do not assert the research's rounded diagnostic margin as an
exact float result.

Add `ProjectedSeparationKeepsClosedTangencyAndRingContext`. Use exact projected segments at
X = `MathF.BitDecrement(0.25f)`, `0.25f`, `MathF.BitIncrement(0.25f)`, spanning Z [-1,1],
axis zero and exact radius 0.25. Expect Negative, Zero, Positive respectively. Include a projected
point, a collinear polygon and both vertex windings. For actual foot radius 0.2f, put a context face
at X `0.2f + 0.00005f`, joined to an admitted slope. Assert `HasDiscMaximum == false`,
`WitnessCount == 0`, its direct join bit remains 1 and the independently derived cap still applies.
Add `PitchedPlateRelevantUncertaintyStillRefusesAtomically`: use source-less interval fixtures
straddling orientation or retained candidate membership, sentinel member copies and join words
73UL. Expect Unsupported, Members 0, unchanged spans and Find Refused even above a lower floor.

- [ ] **Step 4: Implement certified whole-face exclusion and defer candidate height.**

Implement the two signatures above with outward arithmetic and source-polynomial signs. Check
every vertex against a candidate halfplane and compare the disc's entire projected support bound
with sign-guarded squared arithmetic. A separating line may be derived from projected edges or a
closest projected point/segment. Handle degeneracies without requiring normal Y. A failed line
or uncertain projection must continue into the original kernel, not discard the face.
Place whole-face exclusion before orientation-dependent admission in the common path and before
the orientation refusal in `Extrema`. Keep area/shape validity checks. Reorder axis and disc-extremum
processing so `Interior` precedes `OnPlane`, and evaluate height only for retained membership.
Retain vertex/edge coverage and reject any retained unresolved height. Update SG-23 with CF-7's
derived plate result before changing existing expectations. No join, guard or selection changes.

- [ ] **Step 5: Verify the focused geometry and safety selection.**

```bash
build-slot --heavy --label cf-1429-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~PitchedPlate|FullyQualifiedName~ProjectedSeparation|FullyQualifiedName~FootSupportBoundaryTests|FullyQualifiedName~FootSupportNeighborhoodTests|FullyQualifiedName~RemoteWholePolygonJoinsAndRingCapsMatchOracle|FullyQualifiedName~CompoundChildPosesKeyTheWorldForm'
```

Expected: exit 0, all selected tests pass, neither relevant uncertainty nor ring context is dropped.
Filter by actual test method/class names, never assume a filename is part of FullyQualifiedName.
The boundary classes retain the roof, raised-start, above-band, exact-reach and hidden-floor rows.

- [ ] **Step 6: Finish with Release verification once, then commit explicit paths.**

```bash
build-slot --label cf-1429-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label cf-1429-suite -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
```

Stage this task's listed paths before the staged guards. Expected: zero build warnings/errors, full
Game.Tests exit 0 and all three guards exit 0. Commit only after inspecting `git diff --cached`.

```bash
git add KhaozEngine.Physics.Bepu/SupportProjectedPolygonSeparation.cs KhaozEngine.Physics.Bepu/SupportFootprintPolygon.cs KhaozEngine.Physics.Bepu/SupportFootprintSourcePredicates.cs KhaozEngine.Physics.Bepu/SupportFootprintSourcePredicates.ProjectedSeparation.cs KhaozEngine.Game.Tests/Physics/SupportFootprintTests.PitchedPlates.cs docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md
git commit -m "fix(physics): certify irrelevant pitched-plate strata"
```

### Task 2: Preserve attained lying-log boundaries (#1455, CF-3, CF-6, CF-7)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.cs` (`Candidate`, `Keep`, `Side`, `SidePoint`)
- Modify if the shared candidate contract requires it: `KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.Stationary.cs`, `KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.Round.cs`
- Test: create `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.LyingLogs.cs` in partial `SupportFootprintTests`
- Test oracle: `KhaozEngine.Game.Tests/Physics/SupportFootprintTiltedOracle.cs` (only if the independent endpoint enclosure needs extending)
- Modify: `docs/design/CONTACT-CONTROLLER-SUPPORT-CURVED-PROOFS-2026-10-11.md` (source endpoint existence/feasibility proof), `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md` (SG-23)

**Interfaces:**
- Consumes: `SupportTiltedCurvedGeometry.Begin(in SupportCurvedLeaf leaf, double x, double z, double disc) -> bool` and `Side(double slope, out Witness witness, out GeometryInterval maximum) -> GeometrySign`.
- Produces: private `enum DiscLateralBoundary : byte { None, Negative, Positive }` and `Candidate(GeometryInterval Parameter, double End, bool AtEnd, bool Slope, int Rim, DiscLateralBoundary Lateral = DiscLateralBoundary.None)`.
- Produces: private `bool Keep(GeometryInterval z, double end = 0, bool atEnd = false, bool slope = false, int rim = 0, DiscLateralBoundary lateral = DiscLateralBoundary.None)`. Keep `SidePoint(Candidate candidate, double slope, out Witness point, out bool certain) -> GeometrySign`.
- Positive/Negative tags mean the exact source equation `q + r*z = +disc`/`-disc`. None carries no such identity. Public interfaces and the existing 32-candidate storage remain unchanged. Task 4 relies on ordinary float lying poses, not a horizontal snap.

- [ ] **Step 1: Add failing public Find and raw-part log rows.**

Add `LyingLogFlankSupportsWithInstalledPoseOracle(bool capsule, float radius, bool aboutX, int sign)`
for capsule false/true, radius 0.1f/0.15f/0.3f, X/Z rotations and approach signs -1/+1.
Floor half extents `(2,0.1f,2)` at `(0,-0.1f,0)`, zero gravity, length 1f, centre
`(0,radius,0)`, orientation `Quaternion.CreateFromAxisAngle(UnitX or UnitZ, MathF.PI/2)`.
Capsule pose position is the centre. Cylinder uses centre minus
`Vector3.Transform(new Vector3(0,0.5f,0),q)` to account for base alignment.
Use query axis `(sign*0.25f,0)` about X or `(0,sign*0.25f)` about Z, FeetY 0f except
0.3f for the radius-0.3 row, radius 0.2f, reaches 0.4f and cosine `MathF.Cos(MathF.PI/4)`.
Derive the installed source axis/centre independently. Read its actual tiny Y, never force zero.

```csharp
Assert.Equal(SupportStatus.Walkable, sample.Status);
Assert.Equal(owner, sample.Static);
Assert.Equal(expected.ElementId, sample.FeatureId);
Assert.True((double)sample.Height - sample.HeightError <= expected.HeightLower);
Assert.True((double)sample.Height + sample.HeightError >= expected.HeightUpper);
Assert.InRange(sample.HeightError, 0f, 0.0005f);
Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
```

Check each raw part, independent normal/witness enclosures, Axis then Footprint role order and the
absence of an axis witness where the axis misses. Keep the exact quaternion `(0.5f,0.5f,0.5f,0.5f)`
and radius-0.15 sphere controls. Their contribution is derived by the horizontal oracle, about
0.22071071 m for the supplied flank, never asserted as the 0.3 m crest or reused for tilted poses.

- [ ] **Step 2: Run the reproduction before production edits.**

```bash
build-slot --label cf-1455-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~LyingLogFlankSupportsWithInstalledPoseOracle'
```

Expected: Walkable assertions fail with Refused for standard float lying poses. Exact-horizontal
controls remain controls, not substitutions for failed rows.

- [ ] **Step 3: Pin source feasibility, boundary coverage and atomic negatives.**

Add `LyingLogBoundaryIdentityDoesNotCertifyAClippedOrUnprovedRoot`. Capture the actual leaf, call
`Begin`/`Side` at disc 0.2 and the supplied cosine, and assert Positive with finite maximum bounds
containing the independent attained point. Add nearby signed rotations at
`MathF.BitDecrement(MathF.PI/2)` and `MathF.BitIncrement(MathF.PI/2)`, plus
`MathF.PI/2 - 0.000001f` and `MathF.PI/2 + 0.000001f`. Check roots outside [-1,1] are excluded,
not clipped into a tagged endpoint. Use a genuinely unresolved source-family fixture with
sentinel spans and join words 73UL: Unsupported, Members 0, all spans unchanged, no lower-floor
fallback. Preserve `CurvedTiltedUncertifiedSourceFamilyRefusesAtomicallyAtCellBound` unless a new
source proof explicitly changes its SG-23 disposition. No increase to that bound is permitted.

Add `LyingLogRolesAndEndSeamsKeepRawParts` with radius 0.15f/0.3f, axis positions 0f and
signed 0.25f across the barrel, and longitudinal positions at both finite ends and their adjacent
represented floats. Assert the oracle's exact raw leaf/part IDs, 0 to 2 witness count and role order,
including cap/side ties and capsule hemisphere restrictions. Exercise cold, warm, reference,
interleaved and rebuilt worlds. Retain the existing 256/257 raw-part capacity rows and unchanged
closed-disc, contact-ring, above-band and raised-start tests.

- [ ] **Step 4: Carry source endpoint equations through the bounded candidate pipeline.**

Tag only the two candidates constructed directly from `(-disc-q)/r` and `(disc-q)/r`.
Prove the tagged source root remains in the original parameter domain before any interval clipping.
Use its defining relation to establish zero transverse extent, then certify finite-axis feasibility
and slope for every represented source. Keep the original normal and installed tilt enclosures.
Only a proved attained point establishes the lower maximum bound. Retain possible stationary
regions for the enclosing upper bound and all end/hemisphere constraints. An unproved tag still
refuses where relevant. Retain exact pole identities only when source-proved. Update the curved
proof and SG-23 before expectation changes. No changes to caps, guards, floor fallback or limits.

- [ ] **Step 5: Run focused curved and protection tests.**

```bash
build-slot --heavy --label cf-1455-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~LyingLog|FullyQualifiedName~CurvedTilted|FullyQualifiedName~FootSupportBoundaryTests|FullyQualifiedName~FootSupportCompletenessTests|FullyQualifiedName~CurvedRawCapacityUsesTwoSlots|FullyQualifiedName~RawCapacityPublishesAtomically'
```

Expected: exit 0, every new row executes and passes, the genuinely unresolved/capacity rows still
refuse atomically. Do not claim that passing these tests satisfies #1334 timing or aggregate adoption.

- [ ] **Step 6: Finish with Release verification once and commit the owned files.**

```bash
build-slot --label cf-1455-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label cf-1455-suite -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
```

Expected: zero warnings/errors and full Game.Tests exit 0. Stage the Files list paths, including
unchanged optional existing files harmlessly, run the staged guards and inspect the diff.

```bash
git add KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.cs KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.Stationary.cs KhaozEngine.Physics.Bepu/SupportTiltedCurvedGeometry.Round.cs KhaozEngine.Game.Tests/Physics/SupportFootprintTests.LyingLogs.cs KhaozEngine.Game.Tests/Physics/SupportFootprintTiltedOracle.cs docs/design/CONTACT-CONTROLLER-SUPPORT-CURVED-PROOFS-2026-10-11.md docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
git diff --cached --check
git commit -m "fix(physics): certify attained lying-log side boundaries"
```

No version or CHANGELOG edit.

### Task 3: Carry direct joins into seat and signal accounting (#1454, CF-4 to CF-7)

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/SupportContinuity.cs`
- Modify: `KhaozEngine.Locomotion/Contacts/SupportSample.cs`, `FootSupport.cs`, `SupportRequestMemo.cs` in that directory
- Modify: `KhaozEngine.Locomotion/Contacts/GroundSeat.cs`, `GroundSignals.cs`
- Test: create `KhaozEngine.Game.Tests/Locomotion/Contacts/JoinedEdgeContinuityTests.cs`
- Test: create `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactLeasedQueryTests.JoinedEdges.cs` in partial `ContactLeasedQueryTests`
- Modify: `docs/design/CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md`, `docs/design/CONTACT-CONTROLLER-PHASE-4A-GROUND-SIGNALS-2026-10-11.md`, `docs/design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md` (SC-17 limitation), `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md` (SG-23)

**Interfaces:**
- Consumes: the existing authenticated `SupportFootprintResult`, raw `SupportFootprintMember` span, its direct bit matrix and `FootprintContribution.MemberIndex`. No backend join, query-adapter or public Physics API change.
- Produces in `SupportContinuity.cs`: `internal readonly record struct SupportMemberKey(StaticHandle Static, int FeatureId)` and `SupportContinuitySource(SupportFootprintResult Result, FootSupportQuery Query)`.
- Produces `[InlineArray(SupportFootprintResult.MaximumElements)] internal struct SupportJoinedMemberKeys` with one private `SupportMemberKey` element field, and `internal readonly struct SupportJoinedMembers` with private readonly inline key storage, `Selected -> SupportMemberKey`, `Count -> int` and `Contains(SupportMemberKey key) -> bool`. No mutable storage escapes this owner. Count is 0 to 255, in raw-member order, excluding self.
- Produces `SupportSample.Source { get; init; } -> SupportContinuitySource` and `SupportSample.Joined { get; init; } -> SupportJoinedMembers`, preserving its seven-argument primary constructor and defaulting to no evidence. Analytic/None/Refused samples have no source or joins.
- Preserve geometric record equality with `public bool Equals(SupportSample other)` and `public override int GetHashCode()` over the original seven fields only. Evidence is internal and excluded from wire state and geometric replay equality. Tests compare its source/query/raw-key fields separately.
- Produces `SupportContinuity.Capture(in SupportFootprintResult result, in FootSupportQuery query, ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins, int selectedMember) -> SupportJoinedMembers` and `IsContinuous(in SupportSample start, Vector2 startAxis, in SupportSample next, Vector2 axis, float cosMaxSlope) -> bool`.
- `GroundSeat.Resolve` and `DiscreteStep` keep their signatures. Both use `IsContinuous` instead of only `SameMember`. Make existing `StepPart(in SupportSample support, in SupportSample start, Vector2 startAxis, float startFeetY, Vector2 axis, float cosMaxSlope) -> float` internal for direct arithmetic tests. Preserve `MemberExcess` arithmetic and rounding.
- Existing Find and memo signatures/keys remain unchanged. Task 4 consumes unchanged controller/query interfaces. The larger value sample must be copied intact on a memo hit and cleared with its entry on Close.

- [ ] **Step 1: Reproduce the actual crossing through GroundSeat.**

Create `JoinedEdgeWithinWalkableGradeDoesNotCharge(string partition, bool reverse)`. Use the
walkable plane `y = 0.7f*x + 0.7f*z` ending at X 0. Build triangle A from XZ
`(-1,0),(0,-1),(0,0)` and triangle B from `(-1,0),(0,0),(0,1)`, with upward winding and
each vertex Y computed once as `0.7f*x + 0.7f*z`. Partitions are `one-polygon` (a convex
quad covering X [-1,0], Z [-1,1]), `one-mesh` (A/B) and `split-statics` (A/B installed
separately). Query from axis `(0.18f,-0.125f)` to `(0.18f,-0.025f)`, and reverse.
Foot radius is 0.2f, reaches 0.4f and cosine `MathF.Cos(MathF.PI/4)`. Start feet equal the
independent start support height. For mesh/split rows independently prove different winning
raw keys and a direct released-predicate join. The disc maximum is at the X=0 sloped edge,
with longitudinal coordinate `axis.Z + sqrt(radius*radius - axis.X*axis.X)`.
Use installed float geometry to enclose the height, about 0.07 m rise over the forward move.

```csharp
Assert.Equal(SeatOutcome.Seated, seat.Outcome);
Assert.Equal(SupportStatus.Walkable, seat.Support.Status);
Assert.Equal(0f, seat.StepPart);
Assert.Equal(0f, seat.DiscreteStep);
Assert.True(Math.Abs((double)seat.FeetY - expected.Height) <= seat.Support.HeightError);
```

For split/mesh also assert the target's raw feature differs from the start and matches the oracle.
The one-polygon control must already pass. No fabricated positive join certificate is used.

- [ ] **Step 2: Run the crossing reproduction before production edits.**

```bash
build-slot --label cf-1454-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~JoinedEdgeWithinWalkableGradeDoesNotCharge'
```

Expected: split/mesh rows fail zero StepPart or zero DiscreteStep with the residual crossing rise,
while the unchanged one-member control passes. Retain exact failing values in the report.

- [ ] **Step 3: Add negative, endpoint-source, grade and lifetime tests.**

Add `JoinedEdgeNeedsTheDirectSelectedPair`. Separate the two pieces along Z by 0.001f, exceeding
the unchanged 0.0001f join band, and assert neither endpoint lists the other's selected raw key.
For a transitive control, keep the same plane and X range [-1,0], and make three coplanar pieces
with Z ranges [-1,-0.05f], [-0.05f,0.05f] and [0.05f,1]. Move from `(0.18f,-0.2f)` to
`(0.18f,0f)`. Independently assert selected endpoints A/C, joins A-B and B-C set and A-C clear.
`IsContinuous` must be false, even with equal normals or matching mesh adjacency. At these
overhung endpoints the old level residual remains the oracle:

```csharp
Assert.False(start.Joined.Contains(new(seat.Support.Static!.Value, seat.Support.FeatureId)));
Assert.False(seat.Support.Joined.Contains(new(start.Static!.Value, start.FeatureId)));
Assert.Equal(GroundPlacement.NotBelow((double)seat.Support.Height - start.Height), seat.StepPart);
Assert.True(seat.StepPart > 0f);
Assert.True(seat.DiscreteStep > 0f);
```

Add `JoinedEdgeUsesPrecedingFootprintWhenTheTargetOmitsIt`. In the A/B scene move from
`(0.18f,-0.075f)` to `(0.18f,-0.275f)`. Independently verify the start selects B and target A,
the start footprint directly joins A/B, and B lies outside the target's expanded admission disc.
Assert the target footprint omits B, `start.Joined.Contains(targetKey)` is true, the reverse
membership in the target's evidence is false, StepPart is 0f and DiscreteStep is 0f. Reverse the
move to prove the target footprint can supply the same pair instead. No extra production query.

Add `JoinedEdgeGradeExcessAndDropsKeepTheClimbBudget`. Keep a zero-error same-member unit
control using the same installed static/feature key, axis move 0.125f, cosine 1f, heights
0f/0.25f and unit-Y normals. The grade is exactly zero, so StepPart and DiscreteStep are 0.25f.
Reverse gives StepPart 0f and DiscreteStep -0.25f. At signed rise 0.001f DiscreteStep is 0f,
and at `MathF.BitIncrement(0.001f)` it is that signed rise. Retain nonzero-error equality rows.
For the real joined A/B scene move from `(0.199f,-0.03f)` to `(0.18f,-0.03f)` and reverse.
The edge witness crosses from A to B and rises about 0.047 m over about 0.019 m, beyond the
45-degree grade. Independently certify its join and installed support enclosures. Define
`delta = (double)next.Height - start.Height`, `distance = sqrt(dx*dx + dz*dz)`,
`grade = sqrt(1-c*c)/c*distance` with represented cosine promoted to double, and
`e = max(0,abs(delta)-grade-start.HeightError-next.HeightError)`. Assert StepPart equals
`delta > 0 ? GroundPlacement.NotBelow(e) : 0f` and DiscreteStep equals
`e > 0.001f ? (float)Math.CopySign(e,delta) : 0f`. Do not manufacture its join.
Retain the 0.18f tread riser, analytic ramp, sphere-flank excess and #1437 crate tests.

Add `JoinedEdgeEvidenceSurvivesScratchButNotItsInterval`. Capture a valid pair, issue 16 unrelated
Finds in the same lease and reuse the original samples. Assert raw keys, exact queries and zero
step remain unchanged. The exact-repeat memo returns an identical complete neighbor value with
its existing query key and a memo hit. Its selected raw row must list only direct neighbors,
including relevant context-only neighbors, never a prefix or graph closure. Test a 256-member
fixture whose selected row directly joins 255 distinct walkable polygons, with Count 255 and
every raw key present, then preserve the 257-member atomic refusal. Dispose the original lease,
acquire a same-generation replacement and assert old evidence cannot be used. Retain the backend's
ObjectDisposedException for a disposed original lease and InvalidOperationException for receiver
or interval mismatch. Include another selected view, wrong thread, changed rebase/handle reuse
after disposal and a genuinely unresolved relevant join. That join returns Refused, never a
continuous-ground fallback. Assert sample geometric equality ignores transient evidence, while
explicit certificate comparisons include its source, query and every direct raw key.

In partial `ContactLeasedQueryTests`, add `JoinedEdgeLeasedSeatAndLookAheadKeepThePair` using
existing `QueryScene`. Assert zero false step, unchanged canonical binding/raw key and one physics
acquisition. Force `GroundSignals.AlwaysSample()` to exercise look-ahead. Test a pushed-axis
revalidation, excluded member and undersized scope. Each new supported sample captures its own
winning row. The scope failure remains Unresolved before a producer/backend call and cannot widen
scope. After provisioning, the borrowed support read and copied evidence allocate 0 bytes.

- [ ] **Step 4: Copy the selected direct-neighbor row as authenticated value evidence.**

Implement the Interfaces block. Keep the selected candidate's raw member index until both proposal
guards and final terrain/physics selection are settled, while current scratch is still in use.
Preserve the existing candidate offer order and tie rule. Only after `AssertFootprintCurrent` and
complete certification, copy the winning polygon's direct joined Walkable raw keys, excluding
self, into the inline value. Include context-only polygons, which may become support at the next
axis. Source metadata travels with every supported physics sample. Curved witnesses have no joined
keys. A terrain winner, None or refusal clears all physics evidence. Store no span or array reference.

`IsContinuous` keeps the raw same-member path unchanged. For different members authenticate both
source results through their issuer, require the same receiver/source/lease/origin/generation,
match both selected raw keys and exact query axes, radius and traction gate, then check direct
membership in either endpoint's copied row. Query reaches may differ and retain their own exact
source bits. No transitive inference, added probe, query, lease or result-cache key is needed.
A stale source is invalid evidence, never a new interval's permission. If neither authenticated
endpoint lists the other, retain the existing across-member residual calculation.

Keep memo signatures and exact keys unchanged, document the larger value payload and verify its
existing entry copy and Close clearing retain/release evidence correctly. Record the 2,048-byte
key payload, actual enlarged sample/memo storage and any increased managed frame footprint. Do not
claim a stack or timing budget passed from bounded size alone. Retain zero warm managed allocation.

- [ ] **Step 5: Apply the grade rule and adapt signal storage without changing query doors.**

Use `IsContinuous` in StepPart and DiscreteStep and retain MemberExcess, owed debt and placement
rounding. Each pushed-axis revalidation already earns a fresh Find and therefore fresh evidence,
without copying the unpushed target's row. Preserve its clearance and budget policy.

The new sample holds managed lease references, so GroundSignals cannot `stackalloc SupportSample`.
Replace that four-element support array with one `SupportSample previousSupport` local, updated
only after a Walkable sample is accepted. Keep its stack-allocated Vector2 height/distance array,
sample count, fit, optional failure omission and carried-ascent skip. Neither adapter signatures
nor look-ahead query behavior changes. Update phase 2, 4a-9, SC-17 and SG-23 with the direct-pair
rule, proof from either endpoint and unchanged unjoined limit.

- [ ] **Step 6: Run the focused accounting, authentication and signal selection.**

```bash
build-slot --heavy --label cf-1454-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~JoinedEdge|FullyQualifiedName~GroundSeatTests|FullyQualifiedName~PacedCrateDescentTests|FullyQualifiedName~ExactRepeatMemoTests|FullyQualifiedName~ContactLeasedQueryTests|FullyQualifiedName~ContactControllerTests'
```

Expected: exit 0, all endpoint/lifetime rows pass, valid joined edges produce zero false step,
real risers and excess remain charged, and scope/exclusion/refusal rows retain their outcomes.

- [ ] **Step 7: Finish with Release verification once and commit.**

```bash
build-slot --label cf-1454-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label cf-1454-suite -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
```

Expected: zero warnings/errors and full Game.Tests exit 0. Stage only the listed files and inspect
the complete staged diff before committing.

```bash
git add KhaozEngine.Locomotion/Contacts/SupportContinuity.cs KhaozEngine.Locomotion/Contacts/SupportSample.cs KhaozEngine.Locomotion/Contacts/FootSupport.cs KhaozEngine.Locomotion/Contacts/SupportRequestMemo.cs KhaozEngine.Locomotion/Contacts/GroundSeat.cs KhaozEngine.Locomotion/Contacts/GroundSignals.cs KhaozEngine.Game.Tests/Locomotion/Contacts/JoinedEdgeContinuityTests.cs KhaozEngine.Game.Tests/Locomotion/Contacts/ContactLeasedQueryTests.JoinedEdges.cs docs/design/CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md docs/design/CONTACT-CONTROLLER-PHASE-4A-GROUND-SIGNALS-2026-10-11.md docs/design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
git diff --cached --check
git commit -m "fix(contacts): preserve continuity across certified joined members"
```

No version or CHANGELOG edit.

### Task 4: Prove headless traversal and record the staged fix (CF-6, CF-7)

**Files:**
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactFollowupScenes.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactFollowupTraversalTests.cs`
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactLeasedQueryTests.FollowupTraversal.cs` in partial `ContactLeasedQueryTests`
- Modify: `CHANGELOG.md` (one bullet under the staged version)
- Modify: `docs/design/CONTACT-CONTROLLER-FOLLOWUPS-2026-10-18.md`, `docs/INDEX.md` (implementation/evidence status only)

**Interfaces:**
- Consumes: corrected public Find, unchanged `GroundCore.Step`, `DryQueries.For`, `GroundSignals.Resolve` and the existing leased `QueryScene` fixture.
- Produces test-only `ContactFollowupScenes.Plate(float pitch, bool compound) -> (FootSupportScene Scene, StaticHandle Obstacle, Vector2 Direction)` and `ContactFollowupScenes.Log(bool capsule, float radius, bool aboutX) -> (FootSupportScene Scene, StaticHandle Obstacle, Vector2 Direction)`. Each scene has a level floor at Y 0, zero gravity and an obstacle installed before the first lease. Dispose its scene once the test ends.
- Produces deterministic headless regression methods listed below. No new production movement API or tuning. Reuse installed-geometry oracle readers for the exact expected destination height.

- [ ] **Step 1: Add the headless regressions before any further repair.**

Add `PlankTraversalArrivesWithinOneMillimetre(float pitch, bool compound, int sign, bool stopOnProp)`
with pitch 0.05235988f/0.10471976f, yaw 0.77f, standalone/compound, both approach signs and
both stop modes. Use Task 1's resting plate. Direction is its rotated local +Z projected into XZ
and normalized. Start 0.75f from its centre along negative direction. The through target is
0.75f beyond the centre, the on-prop target is at its centre. Derive the exact supported target Y.

Add `LogTraversalArrivesWithinOneMillimetre(bool capsule, float radius, bool aboutX, int sign, bool stopOnProp)`
for both shapes, radii 0.1f/0.15f/0.3f, X/Z quarter turns, both signs and both stop modes.
Use Task 2's actual standard float poses. Direction is across the log, UnitX for an X quarter
turn and UnitZ for a Z quarter turn. Start at -0.75f, through target +0.75f or on-prop target
0f along that direction. Do not use a sphere substitution or an exactly horizontal quaternion.

Use MoveTuning.Default, GroundCoreSettings(0.5f), dt 1f/60f and speed 1f m/s. At each tick command
`min(remaining horizontal distance, speed*dt)` toward the target. Once the axis is reached, zero
displacement may pay existing climb debt. Stop within 240 ticks, a single bounded simulation
sequence, not repeated test invocations. Keep every result and ensure an intermediate result's
support raw owner is the plate/log. These tests must traverse the defect, not pass along the floor.

```csharp
Assert.Equal(GroundFooting.Walkable, result.Footing);
Assert.False(result.Blocked);
Assert.InRange(result.Support.HeightError, 0f, 0.0005f);
Assert.True(ticks <= 240);
Assert.True(sawObstacleSupport);
Assert.True(Vector2.Distance(new(result.Feet.X, result.Feet.Z), targetAxis) <= 0.001f);
Assert.True(Vector3.Distance(result.Feet, expectedTarget) <= 0.001f);
```

Plank ticks already seated on their continuous surface have StepRise 0f and no charged crossing
climb. Log ticks may pay genuine above-grade excess. Assert no support-height undershoot after
full debt payment beyond the certified HeightError and no extra placement lift in the dry head.
Replay each fixed recorded input sequence from its copied initial state and compare every result
bitwise over the original geometric fields, as deterministic behavior testing rather than a timing
or flake loop. Freshly acquired evidence must authenticate to its own interval instead of comparing
lease reference identity between two replays.

In partial `ContactLeasedQueryTests`, add
`LeasedPlankAndLogApproachesArriveWithinOneMillimetre(string shape, float size, int sign)`
with plank pitches 0.05235988f/0.10471976f and cylinder/capsule radii 0.1f/0.15f/0.3f.
Use the same target and bounded input sequence under the fixture's adequate composed scope.
Assert raw binding matches the selected support and one physics acquisition per composed interval.
The leased body's expected centre uses the existing 0.001f placement offset plus CapsuleHalfHeight.
Compare geometric kernel feet with the same 0.001f arrival and 0.0005f error limits.

- [ ] **Step 2: Run the new traversal selection and retain any red result.**

```bash
build-slot --heavy --label cf-traversal-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~PlankTraversalArrivesWithinOneMillimetre|FullyQualifiedName~LogTraversalArrivesWithinOneMillimetre|FullyQualifiedName~LeasedPlankAndLogApproachesArriveWithinOneMillimetre'
```

Expected after Tasks 1 to 3: exit 0. These are integration regressions against completed repairs,
so a manufactured red is unnecessary. If a row fails, preserve its first failing tick and
independent geometric cause, return to its owning task and fix the proper cause test-first.
Do not relax 240 ticks, arrival, slope, reach, shell or uncertainty handling to make it pass.

- [ ] **Step 3: Record the actual implementation and perform the Markdown sweep.**

Re-read the current `KhaozEngineVersion` in Directory.Build.props and newest release tag. The
baseline has staged 21.4.1. Ride the staged version actually present, never create a new heading,
version bump or tag for this batch. Add one CHANGELOG bullet stating that the contact controller
supports slightly pitched planks and lying logs and crosses certified joined sloped edges without
a false climb hitch, with #1429, #1455 and #1454 references. Keep historical cost evidence unchanged.

Update this spec's and docs/INDEX.md's status with actual accepted commits/evidence, never claim
timing, #1334 closure, navigation or consumer adoption. Search Markdown for `StepPart`,
`DiscreteStep`, `same member`, `same-member`, `overhung`, `SupportSample`, `SG-23`, `lying` and
`pitched` and correct only descriptions invalidated by these repairs. Add any affected living API
doc path to the task report and explicit staging list. Do not rewrite unrelated historical reports.

- [ ] **Step 4: Finish with the Release solution build and full Game.Tests suite once.**

```bash
build-slot --label cf-final-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label cf-final-suite -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: zero build warnings/errors, full Game.Tests exit 0 and all guards exit 0. No stress,
timing artifact, engine release, consumer client or navigation bake is run by this task.

- [ ] **Step 5: Stage, guard and commit the final regression/evidence change.**

```bash
git add KhaozEngine.Game.Tests/Locomotion/Contacts/ContactFollowupScenes.cs KhaozEngine.Game.Tests/Locomotion/Contacts/ContactFollowupTraversalTests.cs KhaozEngine.Game.Tests/Locomotion/Contacts/ContactLeasedQueryTests.FollowupTraversal.cs CHANGELOG.md docs/design/CONTACT-CONTROLLER-FOLLOWUPS-2026-10-18.md docs/INDEX.md
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
git diff --cached --check
git commit -m "fix(contacts): pin plank and log traversal regressions" -m "Closes #1429" -m "Closes #1455" -m "Closes #1454"
```

Include additional changed living-doc paths explicitly if Step 3 found any. Return the four task
commits, focused red/green evidence, full verification exits, SG-23 dispositions and any genuine
blocker to the orchestrator. The orchestrator independently reviews before integration/push.

## Plan self-review and handoff

CF-1/CF-2 map to Task 1, CF-3 to Task 2, CF-4/CF-5 to Task 3, and CF-6/CF-7 to all four tasks
and Task 4's traversal/changelog finish. Every Review Focus entry has a named owning test.
Query and memo signatures/keys remain unchanged, copied direct-join evidence remains internal,
and GroundSignals no longer stack-allocates a sample containing lease references. Either endpoint
can prove a pair, including when its predecessor has left the target admission volume.
The task writer stops at the reviewed commit. This plan supplies no push, merge, release or owner
approval on behalf of the orchestrator. Review this spec and plan before starting implementation.
