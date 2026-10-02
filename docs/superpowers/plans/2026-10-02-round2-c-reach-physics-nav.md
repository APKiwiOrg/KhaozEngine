# Round 2 C: Shape Reach and Physics Navigation Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship an explicit Movement package with 3D reach, absolute-coordinate ground context and bounded physics navigation profiles for game-owned area policies.

**Architecture:** Reach is pure numeric geometry. Capture static columns and area tags once, then build immutable capsule-checked node and edge graphs using the existing ground movement core. The package composes Locomotion, Navigation and Physics without introducing a backend or lower-package reverse edge.

**Tech Stack:** C#/.NET 10, xUnit, PhysicsColumnProbe, CharacterMovement, Bepu in tests only. Rides the round's selected version, nominally 20.18.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md`, sections 1, 2 and 5, D1 to D4 and D10 to D13. Approved 2026-10-02. Branch 3 of 4. Read A and B's actual Outcome interfaces before starting.

## Global Constraints

- Owner-authorized execution, full verification and whole-branch review are complete. The integration owner
  merges and pushes the verified branch and refreshes the canonical local feed. Tagging remains owner-owned.
- Execution worktree `/Users/antonio/KhaozEngine/.worktrees/round2-reach-physics-nav`, branch `feature/round2-reach-physics-nav`, based on reconciled engine main containing A and B.
- Ride A's selected 20.x version, nominally 20.18.0. No independent version bump. Extend that entry. No tag.
- Movement references exactly Locomotion, Navigation and Physics, in no umbrella. No Bepu, TileWorld, NetWorld, ECS, render or input reference. The three tests homes below are deliberate.
- Reach and all public body/nav coordinates use absolute metres. Body position is capsule centre. Navigation position is feet, using that body's own half-height. Collision-world coordinates remain local to its Origin.
- CowPen and Water are game-owned uint tags. Required bits all match, excluded bits never match. No gameplay reach, speed, habitat or shape-height guesses.
- Capture/profile construction is bounded. No missing-column fill from a ground sampler. Exact outer-edge misses remain holes. Rebuild after statics/content changes.
- One building worker, focused tests, one full Release suite at finish. No local repetitions or stress. Workers do not integrate, push, pack or tag. Reviews per task and whole branch.
- Zero warnings, no file-size baseline growth, no em/en dashes or prose semicolons. Record every contract departure in Outcome for D.

## Review Focus

1. A yawed box corner is not its bounding-circle reach. Task 1 `RotatedBoxCornersUseExactShapeDistance`.
2. A radius larger than the nominal half-height breaks feet/collision agreement. Task 2 `GroundTuningRejectsInconsistentCapsuleDimensions`.
3. An outer-edge ray miss must not manufacture walkable ground. Task 3 `MissingOuterColumnStaysBlocked` and Task 5 bridge acceptance.
4. A floor beneath a solid box can look standable to a column probe. Task 4 `UnderSolidReportedFloorCannotAcceptABody`.
5. Two passable columns on opposite sides of a 0.1 m fence must not connect. Task 4 `ThinWallBetweenColumnsRefusesHorizontalEdges`, also proved through the TileWorld bridge in Task 5.

---

### Task 1: Package boundary and exact 3D reach

**Files:**
- Create `KhaozEngine.Movement/KhaozEngine.Movement.csproj`, `README.md`, `MovementBody.cs`, `ReachTarget.cs` and `ReachGeometry.cs`.
- Create `KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj` and `ReachGeometryTests.cs`.
- Modify KhaozEngine.slnx, root README package catalog and explicit-reference list, docs/DEPENDENCY-SEAMS.md.
- Modify `KhaozEngine.Tests/ArchitectureTests.cs` to be partial and add Movement to OptInBackends.
- Create `KhaozEngine.Tests/ArchitectureTests.Movement.cs` using the existing RepoRoot/LoadGraph helpers.

**Interfaces:**
- `MovementBody(Vector3 centre,float radius,float halfHeight)` with immutable Centre, Radius, HalfHeight getters. Finite centre, radius > 0, halfHeight >= radius. Refuse invalid default bodies at operations.
- ReachTarget factories exactly `Capsule(in MovementBody body)`, `Box(Vector3 centre,Vector3 halfExtents,float yawRadians = 0f)` and `Point(Vector3 position)`. An internal tagged value holds the data, with invalid default distinct from the valid point at zero. Box X/Z half-extents positive, Y nonnegative, all finite.
- `float ReachGeometry.Distance(in MovementBody body,in ReachTarget target)` and `bool Within(in MovementBody body,in ReachTarget target,float range,float tolerance = 0f)`.
- Capsule axes extend max(0,halfHeight-radius). Subtract radii after exact interval/point distance, clamp overlap to zero. Within compares to finite nonnegative range plus tolerance, with no hidden gameplay epsilon. Refuse an overflowing threshold instead of admitting everything.
- Package version uses `$(KhaozEngineVersion)`. New test project is nonpackable, namespace KhaozEngine.Tests.Movement, references Movement and Physics.Bepu only.

- [x] **Step 1: Write tests.** Assert capsule/capsule side and height separation, box face/corner and yaw, point distance, overlap, exact boundary and tolerance, and invalid/default values. Use representable quarter-unit dimensions for exact-boundary cases, then a 0.3 radius, 0.75 half-height and 0.6 range case with numeric distance assertions. Add the exact project-reference guard and opt-in closure check.
  ```csharp
  Assert.Equal(1f, ReachGeometry.Distance(body, target));
  Assert.True(ReachGeometry.Within(body, target, 1f));
  Assert.False(ReachGeometry.Within(body, target, 0.999f));
  Assert.True(ReachGeometry.Within(body, target, 0.75f, 0.25f));
  Assert.Equal(0f, ReachGeometry.Distance(body, overlappingTarget));
  Assert.Throws<ArgumentOutOfRangeException>(() => new MovementBody(Vector3.Zero, 1f, 0.5f));
  ```
  Define body as centre `(0,0.75,0)`, radius 0.25, half-height 0.75, target capsule centre `(1.5,0.75,0)` with the same dimensions. Test vertical separation with distinct half-heights, not a shared player's dimension.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ReachGeometryTests'`. Expected missing API/build failure after creating the project skeleton. Do not run before slot authorization.
- [x] **Step 3: Implement package and geometry.** Rotate capsule centre into box yaw coordinates and compare its vertical axis to box intervals. Keep bounding-circle math out of reach. Use finite-safe intermediate arithmetic. Extend architecture checks without broad project references or baseline growth.
- [x] **Step 4: Run** the same filter, then `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter 'FullyQualifiedName~ArchitectureTests'` and `bash scripts/check-doc-versions.sh`. Expected nonzero tests and no failures.
- [x] **Step 5: Commit.** Subject `feat(movement): add explicit 3D body reach geometry`. Obtain task review.

### Task 2: One absolute-coordinate ground movement context

**Files:**
- Create `KhaozEngine.Movement/GroundMoveContext.cs` and `GroundMoveContext.Coordinates.cs`.
- Create `KhaozEngine.Movement.Tests/GroundMoveContextTests.cs`.

**Interfaces:**
- Public constructor `GroundMoveContext(Func<float,float,float> groundHeight, Func<float,float,Vector3>? groundNormal = null, IPhysicsWorld? physics = null, Func<float,float,Vector2>? clampXz = null, Func<float,float,float,MovementMedium>? medium = null)` with read-only GroundHeight, GroundNormal, Physics, ClampXz and Medium properties.
- All supplied delegates speak absolute coordinates and heights.
- Internal `MoveState Step(in MoveState body,Vector2 worldDirection,bool run,float dt,in MoveTuning tuning)` is the one adapter to A's precise StepTowards overload. Internal `void ValidateTuning(in MoveTuning tuning)` checks finite nonnegative pace, radius > 0, half-height >= max(0.1,radius+0.005), finite slope/step/gravity and compatibility needed by the ground path.
- Translate only Position into the physics world's current local frame, wrap height by subtracting Origin.Y, wrap normal and clamp inputs/outputs, and translate Position back. Velocities, facing and other carried fields do not shift. No physics means zero origin. Read the current origin at every step and reject an origin change inside one step. Cache wrappers rather than allocating them per NPC tick.

- [x] **Step 1: Write tests.** Use one flat world at zero origin and an equivalent Bepu world rebased by `(100,20,-80)`. Supply absolute ground/medium providers, include a bridge support above the floor, walk then hold and rebase again between calls. Add Review Focus 2, null-provider validation and medium forwarding.
  ```csharp
  Assert.Equal(zeroOriginResult.Position, rebasedResult.Position);
  Assert.Equal(zeroOriginResult.Grounded, rebasedResult.Grounded);
  Assert.True(wetTravel < dryTravel);
  Assert.Equal(expectedAbsoluteFeet, sampledFeet, 5);
  Assert.Throws<ArgumentOutOfRangeException>(() => validateWideShortBody());
  ```
  Access internals through `InternalsVisibleTo` for Movement.Tests only. The small local validateWideShortBody function calls context Step with invalid tuning. Never normalize a bad radius/height silently.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~GroundMoveContextTests'`. Expected missing API or behavior failure.
- [x] **Step 3: Implement context and frame adapters.** Forward medium, unlike the optional Ruinborne lift source. Delegate collision and gravity to CharacterMovement. Do not dispose caller-owned physics or guess a ground height.
- [x] **Step 4: Run** the same filter. Expected nonzero and zero failures.
- [x] **Step 5: Commit.** Subject `feat(movement): share absolute ground context across movement and navigation`. Obtain task review.

### Task 3: Bounded immutable physics-column capture

**Files:**
- Create `KhaozEngine.Movement/PhysicsNavBakeOptions.cs`, `NavAreaFilter.cs`, `NavAreaClassifier.cs`, `PhysicsNavBake.cs` and `PhysicsNavColumns.cs`.
- Create `KhaozEngine.Movement.Tests/PhysicsNavCaptureTests.cs`.

**Interfaces:**
- `NavAreaFilter(uint Required,uint Excluded)` and delegate `uint NavAreaClassifier(Vector3 absoluteFeetPosition)`. Reject filters whose required/excluded bits overlap.
- `PhysicsNavBakeOptions(float MinX,float MinZ,float MaxX,float MaxZ,float CellSize,float ProbeHeight,float ProbeRange,float MaxSlopeRadians,int MaxCells,int MaxLayerCells,int MaxSurfacesPerColumn = 4,float EdgeProbeSeconds = 1f/30f,int MaxEdgeProbeSteps = 64)` as an immutable record.
- `PhysicsNavBake : IDisposable` with `static PhysicsNavBake Capture(GroundMoveContext context,PhysicsNavBakeOptions options,NavAreaClassifier classify)` and Dispose. Task 4 adds BuildProfile.
- Require context.Physics. Options' ProbeHeight and bounds are absolute. Sample in canonical Z/X order through PhysicsColumnProbe, subtracting the frozen origin before queries and adding its Y to hit heights. Snapshot area tags for each feet surface. Keep world/context references only until all profiles have been built or capture disposed.
- Validate finite/positive bounds, cell/sample/edge budgets and checked array arithmetic before allocation. Sample cap+1 to detect truncated columns. Out-of-bounds padded final-row samples and absent columns remain blocked.

- [x] **Step 1: Write tests.** Small real Bepu ground and deck columns, tags 0x01/0x02 defined only in test fixtures, nonintegral and negative bounds, finite option rejection, checked overflow, cap overflow, origin change during capture, independent classifier mutation after capture and Review Focus 3.
  ```csharp
  Assert.Equal(expectedAbsoluteHeight, capturedHeight, 5);
  Assert.Equal(2, capturedSurfaceCount);
  Assert.False(missingColumnHasSurface);
  Assert.Throws<InvalidOperationException>(() => captureTooManySurfaces());
  Assert.Throws<ArgumentOutOfRangeException>(() => captureTooManyCells());
  ```
  Assertions may inspect the immutable internal column store via the test friendship. Include no large-world or load fixture.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~PhysicsNavCaptureTests'`. Expected missing API or behavior failure.
- [x] **Step 3: Implement snapshot and lifetime.** No ground-sampler fallback on misses. Dispose releases retained builder references and subsequent BuildProfile throws ObjectDisposedException. Do not dispose the world's statics or providers. Build operations reject changed origin and require caller-owned statics to remain unchanged.
- [x] **Step 4: Run** the same filter. Expected nonzero and zero failures.
- [x] **Step 5: Commit.** Subject `feat(movement): capture bounded absolute physics navigation columns`. Obtain task review.

### Task 4: Capsule and area checked profiles

**Files:**
- Create `KhaozEngine.Movement/GroundNavigation.cs`, `PhysicsNavBake.Profiles.cs`, `GroundTraversalProbe.cs` and `NavAreaFootprint.cs`.
- Create `KhaozEngine.Movement.Tests/PhysicsNavProfileTests.cs` and `GroundTraversalProbeTests.cs`.

**Interfaces:**
- Add `GroundNavigation PhysicsNavBake.BuildProfile(in MoveTuning tuning,NavAreaFilter areas)`.
- GroundNavigation has public Space, Planner (`IRegionPathPlanner`), AgentRadius, AgentHeight and `bool AllowsSegment(Vector3 fromFeet,Vector3 toFeet)`. Internal immutable profile tuning records radius, half-height, slope and step. Internal `ValidateTuning(in MoveTuning tuning)` rejects geometry mismatch while permitting different pace and effect scale.
- Consume B's BakeGroundedLayered with MaxLayerCells, NavTraversalLayer and NavTraversalGraph signatures exactly. Construct its guarded GridPathPlanner using the accepted profile radius and height.
- Internal `bool GroundTraversalProbe.TryEdge(GroundMoveContext context,in MoveTuning tuning,Vector3 fromFeet,Vector3 toFeet,Func<Vector3,bool> acceptsFootprint,float stepSeconds,int maxSteps)` validates one directed edge. Use tuning with walk/run 1, dry medium, state SpeedScale 1, no airborne momentum or movement commitment, grounded start and no jump. Keep geometry, slope, step and gravity rules. Stop within 1 mm or refuse after 64 steps by default, with dt 1/30. Never write a position directly to reach an endpoint.

- [x] **Step 1: Write tests.** Check actual hold occupancy, Review Focus 4 and 5, small player and wider creature at a 1 m doorway, directed step up/down, no generated Hop path, required/excluded tags and caller mutation. Area eligibility examines the whole footprint against captured cells, not just the centre. Test a profile on a narrow accepted corridor to prove radius is not eroded twice.
  ```csharp
  Assert.False(underSolid.IsNodePassable(0, x, z));
  Assert.False(acrossFence.CanTraverse(0, leftX, z, 0, rightX, z));
  Assert.Equal(NavPathStatus.Complete, playerDoorRoute.Status);
  Assert.NotEqual(NavPathStatus.Complete, wideBodyDoorRoute.Status);
  Assert.False(pen.AllowsSegment(insideFeet, outsideFeet));
  Assert.DoesNotContain(pen.Space.Links, link => link.Kind == NavLinkKind.Hop);
  ```
  Graph assertions use its internal retained graph through the test friendship. Seed no already-baked hand mask for the thin-wall proof. It must derive from real physics.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~GroundTraversalProbeTests'`. Expected new profile behavior fails.
- [x] **Step 3: Implement.** Filter each surface by area and footprint, then candidate hold occupancy. Generate grounded layers within the allocation bound. Check each neighbor direction and each candidate Stair link with the shared core and area footprint at every slice. Keep output canonical and immutable. Reject changed origin or incompatible slope class. Cache shared snapshot data but do not assume reverse edges succeed. AllowsSegment must reject unaccepted cells/edges and wrong-layer or off-grid endpoints. A path query retains no world or classification callback.
- [x] **Step 4: Run** the same filter. Also assert that disposing capture and replacing or disposing its physics world does not affect an already-built pure path query. Expected nonzero and no failures.
- [x] **Step 5: Commit.** Subject `feat(movement): bake capsule checked ground traversal profiles`. Obtain task review.

### Task 5: Real TileWorld bridge acceptance and documentation

**Files:**
- Modify `KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj`, adding an explicit Movement reference.
- Create `KhaozEngine.TileWorld.Physics.Tests/TileWorldMovementNavigationTests.cs`, small authored synthetic documents only.
- Modify Movement README, root README, Navigation README where its composition seam is named, docs/DEPENDENCY-SEAMS.md, docs/USING-KHAOZENGINE.md and staged CHANGELOG.
- Update this plan's Outcome and final interfaces for D.

- [x] **Step 1: Write acceptance tests.** Use TileWorldColliders.Build/AddTo, Ground and Medium, with explicit collision heights. Prove blocked border, NoDraw hole, a 0.1 m fence, a 1 m doorway, sloped drawn ground, bridge deck above water and document-coordinate origin rebase. Classify water from surface feet through the actual medium sampler. Keep the B14 exact outer-edge miss legal while interior seams stay navigable.
  ```csharp
  Assert.Equal(NavPathStatus.Complete, throughDoor.Status);
  Assert.NotEqual(NavPathStatus.Complete, throughClosedFence.Status);
  Assert.True(deckIsDry);
  Assert.True(bedIsWet);
  Assert.Equal(zeroOriginWaypoints, rebasedWaypoints);
  Assert.False(voidHasAcceptedNode);
  ```
  Measure one normal fixture bake's elapsed time and stored node/edge counts for Outcome, without a repeat loop or stress workload. Full Hollowmere startup cost is a P4 adoption proof, not an engine test requiring private game assets.
- [x] **Step 2: Run** `dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'FullyQualifiedName~TileWorldMovementNavigationTests'`. Expected nonzero and no failures before treating bridge use as supported. Correct only kernel/bridge fit necessary for this task and report unrelated findings as issues.
- [x] **Step 3: Write and sweep API docs.** Include lifetime, origin, profile dimensions, caps, area filter semantics, no Hop links, guarded cell corridors and explicit package installation. No claim that the column ray alone proves wall clearance.
- [x] **Step 4: Fetch and merge current main, then full verification once after slot authorization.**
  ```bash
  mkdir -p local-feed
  dotnet build KhaozEngine.slnx -c Release
  dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
  sh scripts/check-dashes.sh --tree
  sh scripts/check-prose.sh --tree
  sh scripts/check-file-size.sh --tree
  bash scripts/check-doc-versions.sh
  ```
  Expected zero warnings, no failures and passing guards. Obtain whole-branch review and record Outcome, geometry/probe costs and all API departures before integration.
- [x] **Step 5: Commit.** Subject `docs(movement): document physics profiles and round C outcome`. Integration owner merges and pushes main and serializes pack work. No new version or tag. D consumes this Outcome.

## Outcome

The owner authorized the whole round program. Plan C code, scoped reviews and the real TileWorld bridge are
complete on `feature/round2-reach-physics-nav` at `f7a1ec497`. This documentation task rides staged version
20.18.0 and makes no independent bump or tag. The branch started from reconciled engine main at `4431781f6`.
No Grimhollow file, dependency edge, baseline or unrelated document changed.

The implemented public Movement contracts are the following. `MovementBody(Vector3 centre, float radius,
float halfHeight)` exposes immutable `Centre`, `Radius` and `HalfHeight`. `ReachTarget` exposes
`Capsule(in MovementBody)`, `Box(Vector3 centre, Vector3 halfExtents, float yawRadians = 0f)` and
`Point(Vector3 position)`. `ReachGeometry.Distance(in MovementBody, in ReachTarget)` returns a finite float
or refuses an unrepresentable result. `ReachGeometry.Within(in MovementBody, in ReachTarget, float range,
float tolerance = 0f)` compares the retained wider metric with caller-owned tolerance and no gameplay epsilon.
Default body and target values are invalid, while the finite point at zero is valid. Capsule half-height
includes its radius. Box yaw matches the positive +Y physics pose convention.

`GroundMoveContext` takes absolute `groundHeight`, optional absolute `groundNormal`, caller-owned `IPhysicsWorld`,
absolute `clampXz` and absolute `medium` delegates. It exposes those read-only properties and has no public
Step method. The internal adapter caches delegates, changes only the position frame, converts water surface Y,
freezes the origin per sequential step and refuses rebase or recursive entry. The context never steps, disposes
or rebases the caller's world. Tuning validation retains the actual capsule dimensions and permits the documented
positive-infinity facing default.

`PhysicsNavBakeOptions` is the immutable record
`(MinX, MinZ, MaxX, MaxZ, CellSize, ProbeHeight, ProbeRange, MaxSlopeRadians, MaxCells, MaxLayerCells,
MaxSurfacesPerColumn = 4, EdgeProbeSeconds = 1f / 30f, MaxEdgeProbeSteps = 64)`. `NavAreaFilter` requires all
required bits and none of the excluded bits, and overlapping masks are refused. `NavAreaClassifier` receives
absolute feet. `PhysicsNavBake.Capture(GroundMoveContext context, PhysicsNavBakeOptions options,
NavAreaClassifier classify)` requires populated static physics, captures absolute heights, headroom and tags in
Z then X order, uses cap plus one, checks storage budgets before arrays, and leaves missing, padded and
outer-edge samples blocked. There is no ground-sampler fallback. `Dispose` releases retained context references
only. A completed profile is pure owned data and outlives the builder and physics world.

`PhysicsNavBake.BuildProfile(in MoveTuning tuning, NavAreaFilter areas)` returns `GroundNavigation`, whose public
surface is `Space`, `Planner`, `AgentRadius`, `AgentHeight` and `AllowsSegment(Vector3 fromFeet,
Vector3 toFeet)`. The profile matches radius, full height, slope and step exactly. Pace and effect scale may
differ. Physical holds and independently directed edges use unit and dry no-momentum no-commitment no-jump
proofs, with the initial hold counted as slice one, `1 / 30` slices, 64 total core calls and a 1 mm proof
tolerance. The profile checks whole-footprint area tags, uses raw radius zero without double erosion, applies
`MaxLayerCells` before dense arrays, emits Stair-only topology with no Hop links, and keeps accepted graph links
separate from candidate `Space.Links`. `Planner` and `AllowsSegment` are the guarded admission surface with
unsmoothed cell-centre waypoints. Segment Y checks are conservative and live support and collision remain in the
shared core. No NPC or player driver API is claimed here.

The focused C evidence is 73 reach cases, 31 architecture cases, 60 context cases, 54 capture cases, 45 profile
cases and 11 bridge cases. The bridge's one normal fixture observation was `33.806 ms` for `BuildProfile` only.
It used bounds X `[0,4)`, Z `[-4,0)`, cell size 1, probe height 5, range 10, slope 0.8, `MaxCells=128`,
`MaxLayerCells=512`, four surfaces per column, `1 / 30` edge slices and 64 edge steps. Tuning was radius 0.2,
half-height 0.75, step 0.4 and slope 0.8. It stored one 4 by 4 layer, 16 node slots, 16 accepted nodes, 84
directed exits, 0 accepted links and 0 candidate links. This is one observation, not a benchmark or startup
guarantee. Existing issues 1233 and 1238 remain outside scope. Small local physics coordinates or rebasing are
required for large absolute requests. No full Hollowmere or steep-bank support claim was made.

Rulings and costs:

1. C1.1 keeps `Distance` finite by refusing a wider result above `float.MaxValue`, while `Within` compares the
   wider metric. The cost is an exception for extreme-coordinate `Distance` callers and a possible difference
   between a rounded distance reused as a range and the exact comparison.
2. C1.2 retains stable relative interval and component terms plus norm-minus-radius arithmetic. The cost is
   private numeric work and regression coverage, with no gameplay epsilon or public math API.
3. C1.3 validates range and tolerance against remaining float headroom before summation. The cost is stricter
   rejection at the declared limit, while zero tolerance remains valid.
4. C1.4 preserves residuals through result-limit and range comparisons. The cost is bounded private comparison
   arithmetic, with no epsilon or public API change.
5. C2.1 uses only the Movement.Tests friendship in a cohesive AssemblyInfo. The cost is test assembly-name
   coupling, with no production friend or public test seam.
6. C2.2 allows a `3e-5` metre position comparison across frames while carried velocity, facing, effect scale and
   timers remain exact. The cost is a test tolerance for float contact skin, not production geometry or gameplay
   reach.
7. C3.1 keeps exact sample count, order and XZ coordinates, with native physics height assertions at five
   decimals. The cost is accepting sub-precision Y differences in tests, with no production epsilon.
8. C3.2 preflights padded maximum centres and local bounds and probe arithmetic before arrays. The cost is
   refusing extreme requests whose intermediate float coordinates overflow.
9. C4.1 adds a real 0.1 metre lower approach tread to the tiny Stair fixture. The cost is fixture geometry, with
   an independent core replay and no weakened acceptance.
10. C4.2 stops each DDA axis at its owning target cell. The cost is one target-cell comparison per crossing and
    the preserved half-open endpoint regression coverage.
11. C4.3 counts the initial hold as slice one of `MaxEdgeProbeSteps`. The cost is 63 remaining default travel
    slices after the hold, so long otherwise-valid edges can be conservatively refused.
12. C4.4 selects nearest-height tags with support reach derived from step, grounded epsilon and
    `radius * tan(slope)`. The cost is conservative refusal for absent or ambiguous data and possible refusal of
    a full-Y sloped shortcut.
13. C4.5 wraps the guarded planner with pure endpoint checks. The cost is refusal of height gaps over the profile
    step class, including the existing 1 mm tolerance for zero-step profiles, with the four-field geometry match
    unchanged.
14. C5.1 adds exact friendship for the bridge test assembly to count stored nodes, exits and accepted links. The
    cost is test assembly-name coupling, with no public diagnostics API, production friend or production edge.
15. C5.2 removes only temporary timing logging after the one successful normal fixture observation. The cost is
    no repeated timing proof, while stored behavior and counts remain in the committed test evidence.

Root's full Release evidence against the exact committed post-timing-removal source is build exit 0 with zero
warnings and errors in 49.43 seconds, followed by test exit 0 across 29 assemblies with 23,454 passed, 0 failed,
1,275 skipped and 24,729 total cases. Historical timing-removal diff evidence is unavailable, so this exact-source
full suite supersedes the earlier bridge GREEN for committed behavior. All five whole-tree guards exited 0.
The fresh whole-branch reviewer approved all 35 changed files with no findings. A final fetch and merge found
main unchanged at `4431781f6`, so the verified runtime source remains current. Root integrates and pushes this
verified branch, then refreshes the canonical local feed as the normal finishing step. No tag is authorized.
