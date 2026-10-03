# NPC Movement Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an aquatic body follow baked routes and approach range while swimming, hold full pace along collinear route runs, and settle the 2.5 cm lip bake refusal, all opt-in on staged `20.20.0`.

**Architecture:** Capture records one medium water surface per column. An aquatic profile replaces swim-deep surfaces with float surfaces and proves them with a medium probe plus a static clearance check, so every guard reads the body's real feet. `MoveToRange` gains an options record that opts into swim steering and collinear carry, and the follower gains opt-in consumption of passed collinear waypoints. A fixture decides whether the lip refusal is a probe fault before any fix.

**Tech Stack:** C# on .NET 10, xUnit, BepuPhysics in tests.

**Spec:** `docs/design/NPC-MOVEMENT-FIXES-DESIGN-2026-10-03.md`. Read it whole before Task 1. Sections are cited as S1 to S8 and decisions as D1 to D6. Open questions Q1 to Q5 there need orchestrator rulings before the tasks they gate.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/grimhollow-npc-movement`, branch `feature/grimhollow-npc-movement`, from engine main `fefb78876`. Reconcile with current `origin/main` before the final verification.
- Ride staged `20.20.0`. No version bump, no new changelog heading, no tag, no pack, no push of main.
- Q1 gates Tasks 5 to 7: the KENB layout change lands only if the `20.20.0` tag waits for it. `FormatVersion` stays 1.
- Additive public API only. Existing constructors, `BuildProfile(tuning, areas)` and every default behave as today.
- No wire, `NetWorld` or `MoveState` change. No project reference change. Movement references exactly Locomotion, Navigation and Physics.
- Airborne non-swimming bodies and committed bodies return `Suspended` before `InRange` (ruling D0.2), with and without the new options.
- A mandatory turn is never consumed early (ruling D1.1). The strict accept radius stays `min(supplied, 1e-5 m)`.
- No Locomotion change. A #1253 cause in the core stops Task 4 for a ruling (Q4).
- Zero warnings. No `.filesize-baseline` growth. New concerns go in new files. Test namespaces under `KhaozEngine.Tests.*`.
- No em dashes, en dashes or prose semicolons in Markdown or comments. Record every departure, its reason and its cost in this plan's Outcome.
- Focused tests per task, one full Release verification in Task 9. No local repetition, stress runs or client launches. Builds serialize through the orchestrator's build slot even when lanes run in parallel.

Before any restore in this worktree:

```sh
mkdir -p /Users/antonio/KhaozEngine/.worktrees/grimhollow-npc-movement/local-feed
```

## File Boundaries and Ownership

| Area | Files | Task | Lane |
| --- | --- | --- | --- |
| Route geometry and follower | `KhaozEngine.Navigation/NavPath.cs` (made partial), new `NavPath.PassThrough.cs`, `PathFollower.cs` (config property), `PathFollower.Region.cs`, new `PathFollower.Passed.cs` | 1 | A |
| Driver options and carry | new `KhaozEngine.Movement/MoveToRangeOptions.cs`, `MoveToRange.cs`, new `MoveToRange.Carry.cs` | 2 | A |
| Lip fixture | new `KhaozEngine.Movement.Tests/LowLipTraversalTests.cs` | 3 | B |
| Lip fix (gated) | `KhaozEngine.Movement/GroundTraversalProbe.cs` except the `Near` visibility line, design S5 | 4 | B |
| Water capture | `KhaozEngine.Movement/PhysicsNavBake.cs`, `PhysicsNavColumns.cs`, new `PhysicsNavWater.cs`, `GroundNavigationBake.Writer.cs` and `.Reader.cs` capture section | 5 | C |
| Aquatic profile | `PhysicsNavBake.Profiles.cs`, new `AquaticColumns.cs`, `SwimTraversalProbe.cs`, `SwimPace.cs`, `GroundMoveContext.Swim.cs`, `GroundNavigation.cs`, the `Near` visibility line in `GroundTraversalProbe.cs` | 6 | C |
| Aquatic bake | `NavBakeProfile.cs`, `NavBakeIdentity.cs`, `GroundNavigationBake.cs`, `.Writer.cs`, `.Reader.cs` profile section | 7 | C |
| Swim steering | `MoveToRange.cs` (hold and constructor check), `MoveToRange.Approach.cs`, `MoveToRangeOptions.cs` (adds a property) | 8 | join |
| Living docs | `KhaozEngine.Movement/README.md`, `KhaozEngine.Navigation/README.md`, `docs/USING-KHAOZENGINE.md`, `CHANGELOG.md` 20.20.0 entry, `docs/INDEX.md`, design status line | 9 | last |

Lanes A, B and C touch disjoint files and may run in parallel. The one shared file is `GroundTraversalProbe.cs`: Task 6 changes only the visibility of `Near`, Task 4 owns the rest, and the orchestrator merges them. Inside a lane, tasks run in order. Task 8 needs Tasks 2 and 6. Task 9 runs last. The orchestrator owns integration, push and release.

## Review Focus

1. A body knocked airborne out of the water while swim steering is on must stay `Suspended`, not steer in the air. Task 8 `AirborneBodyStaysSuspendedWithSwimPermission`.
2. A hairpin route that doubles back must never consume its turning waypoint early. Task 1 `ReversalIsNeverAPassThrough` and `CornerIsNotConsumedBeforeItIsReached`.
3. A diagonal run near Hollowmere's 150 m coordinates must hold pace, not stall on float rounding at the 1e-5 m radius. Task 2 `DiagonalRunAtLargeCoordinatesHoldsPace`.
4. A bake written on one machine must load an aquatic profile with the same bits, since the float view is derived at load. Task 7 `LoadedAquaticProfileMatchesTheFreshBuild` and `RewritingALoadedAquaticBakeReproducesItsBytes`.
5. A game that already bakes with a medium context must get identical ground profiles after this change. Task 6 `GroundProfileIgnoresWaterEntries`.

---

### Task 1: Collinear pass-through and passed waypoint consumption (#1257, lane A)

**Files:**
- Modify: `KhaozEngine.Navigation/NavPath.cs:57` to `public sealed partial class NavPath`
- Create: `KhaozEngine.Navigation/NavPath.PassThrough.cs`, `KhaozEngine.Navigation/PathFollower.Passed.cs`
- Modify: `KhaozEngine.Navigation/PathFollower.cs` (`PathFollowConfig`), `KhaozEngine.Navigation/PathFollower.Region.cs:129-146`
- Test: `KhaozEngine.Game.Tests/Navigation/NavPathPassThroughTests.cs`, `KhaozEngine.Game.Tests/Navigation/PathFollowerPassedWaypointTests.cs`

**Interfaces:**
- Produces: `public bool NavPath.IsCollinearPassThrough(int index)`, rule in design S4. Throws `ArgumentOutOfRangeException` for an index outside the waypoint list. Index 0 and the last index return false.
- Produces: `public bool PathFollowConfig.ConsumePassedWaypoints { get; init; }`, default false, documented beside `AcceptRadius`.
- Produces: private `bool PathFollower.PassedActive(Vector2 feetXz, int? agentLayer)` in `PathFollower.Passed.cs`, rule in design S4 including the `AcceptRadius + 4 x u` lateral allowance with `u = MathF.BitIncrement(m) - m` for the largest absolute coordinate `m`.
- `AdvanceReachedWaypoints` loop condition becomes proximity as today, or `_config.ConsumePassedWaypoints && PassedActive(...)`. The terminal Complete region check is unchanged and runs first.

- [ ] **Step 1: Write the geometry tests.**

```csharp
[Fact] public void StraightAndDiagonalInteriorWaypointsArePassThrough()
// Waypoints (0,0),(0.25,0),(0.5,0),(0.75,0) on layer 0: indices 1 and 2 true, 0 and 3 false.
// Same for the diagonal (0,0),(0.25,0.25),(0.5,0.5).
[Theory] public void TurnsAreNeverPassThrough(float degrees)          // 45, 90, 135
[Fact] public void ReversalIsNeverAPassThrough()                     // (0,0),(1,0),(0,0): index 1 false
[Fact] public void LayerChangeOrHopSuccessorIsNotPassThrough()
[Theory] public void IndexOutsideTheListThrows(int index)            // -1 and Count
```

- [ ] **Step 2: Write the follower tests.** Use the `PathFollowerRegionTests` scripted planner pattern with a fixed `NavPath`, `AcceptRadius = 0.00001f`.

```csharp
[Fact] public void PassedCollinearWaypointIsConsumedWhenOptedIn()
// Route (0,0),(0.25,0),(0.5,0),(0.75,0). Feet at (0.3,0,0): ActiveWaypointIndex is 2 with the option, 1 without.
[Fact] public void ConsumptionIsOffByDefault()                       // PathFollowConfig.Default.ConsumePassedWaypoints is false
[Fact] public void CornerIsNotConsumedBeforeItIsReached()
// Route (0,0),(0.25,0),(0.25,0.25). Feet at (0.26,0,0.001), past the corner but not on it: index stays 1 with the
// option on, and advances only when the feet are within AcceptRadius of (0.25,0).
[Fact] public void LateralMissKeepsTheWaypoint()                     // feet (0.3,0,0.001): index 1
[Fact] public void LayerMismatchKeepsTheWaypoint()                   // two-layer space, feet resolve to layer 1
[Fact] public void LargeCoordinateAllowanceAdmitsOneFloatStep()
// Diagonal route offset by (150,150), feet one float step off the line: consumed. Two millimetres off: kept.
[Fact] public void FinalCompleteRegionWaypointStillNeedsMembership()
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~NavPathPassThroughTests|FullyQualifiedName~PathFollowerPassedWaypointTests"`. Expected: compile FAIL, CS1061 on `IsCollinearPassThrough` and CS0117 on `ConsumePassedWaypoints`.
- [ ] **Step 4: Implement** the interfaces above. All geometry in double.
- [ ] **Step 5: Run GREEN** with the Step 3 filter, then `--filter "FullyQualifiedName~KhaozEngine.Tests.Navigation"`. Expected: all pass with nonzero counts. Record both counts in Outcome.
- [ ] **Step 6: Commit.** `feat(navigation): consume passed collinear waypoints on request`

---

### Task 2: Driver options and collinear carry (#1257, lane A)

**Files:**
- Create: `KhaozEngine.Movement/MoveToRangeOptions.cs`, `KhaozEngine.Movement/MoveToRange.Carry.cs`
- Modify: `KhaozEngine.Movement/MoveToRange.cs:18-33` (constructors), `:60-72` (route tick), `:84-108` (`StrictConfig`)
- Test: `KhaozEngine.Movement.Tests/MoveToRangeCarryTests.cs`

**Interfaces:**
- Consumes: Task 1 `NavPath.IsCollinearPassThrough`, `PathFollowConfig.ConsumePassedWaypoints`.
- Produces: `public sealed record MoveToRangeOptions` with `public static MoveToRangeOptions Default { get; }` and `public bool CarryAlongRoute { get; init; }`. Task 8 adds `SteerWhileSwimming`.
- Produces: `public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow, MoveToRangeOptions options)` and `public MoveToRange(IRegionPathPlanner planner, NavSpace space, Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow, MoveToRangeOptions options)`. Null options throw `ArgumentNullException`. The existing constructors pass `MoveToRangeOptions.Default`. The instance keeps `private readonly MoveToRangeOptions _options`.
- `StrictConfig(follow, carry)` sets `ConsumePassedWaypoints = carry || follow.ConsumePassedWaypoints`.
- Produces: private `bool TryCarry(in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context, Vector2 waypoint, float bound, out Vector2 command)` in `MoveToRange.Carry.cs`. Returns false unless `CarryAlongRoute`, the remaining distance to `waypoint` is below `bound`, and `ActivePath.IsCollinearPassThrough(ActiveWaypointIndex)`. Aims at the run end per design S4, predicts, and returns true only when `AllowsStep` admits the prediction. The route tick uses it before today's capped command, then runs `StopAtRange` on whichever command it keeps.

- [ ] **Step 1: Record the base count.** On unchanged source run `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRange|FullyQualifiedName~NpcRangeNavigation|FullyQualifiedName~PlayerPathMovement"` and record the passed count in Outcome.
- [ ] **Step 2: Write the tests.** Flat analytic context `MoveToRangeTests.Flat`, tuning `MoveToRangeTests.Tuning` (walk 2 m/s), `dt = 1f / 30f`, space `MoveToRangeTests.Space` (0.25 m cells) with a `GridPathPlanner` and an always-true guard. Loop `Tick` then `NpcGroundMovement.Step`.

```csharp
[Fact] public void StraightCellRouteHoldsFullWalkSpeed()
// Point target 4 m along +X, range 0. After 30 ticks from rest the body has travelled at least 1.99 m with
// CarryAlongRoute. The same run with Default options travels at most 1.88 m (the 1.875 m/s RED evidence).
[Fact] public void CarryStopsAtAMandatoryCorner()
// L-shaped route through a wall gap. Some tick ends with the body's XZ exactly on the corner waypoint, and no tick's
// feet leave the route's cells.
[Fact] public void RefusedCarryFallsBackToTheWaypoint()
// Guard refuses any segment ending beyond x = 0.25. The body still lands on (0.25, 0) and the status stays Following.
[Fact] public void DefaultOptionsLeaveCommandsUnchanged()
// Equal RangeSteering bits over 60 ticks from the old constructor and the new one with Default.
[Fact] public void DiagonalRunAtLargeCoordinatesHoldsPace()
// Space and route offset by (150, 150), diagonal target. 30 ticks travel at least 1.99 m with CarryAlongRoute.
[Fact] public void NullOptionsAreRejected()
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRangeCarryTests"`. Expected: compile FAIL, CS0246 on `MoveToRangeOptions`. Then stub the record and constructors without carry, rerun, and record that `StraightCellRouteHoldsFullWalkSpeed` fails near 1.875 m in Outcome.
- [ ] **Step 4: Implement** carry as in Interfaces.
- [ ] **Step 5: Run GREEN** with the Step 3 filter, then the Step 1 filter. Expected: all pass, and the Step 1 filter's count equals the recorded base plus the new class's cases. `FastScaledApproachCapsTheWaypointAndRangeTravel` stays green.
- [ ] **Step 6: Commit.** `feat(movement): carry route travel along collinear runs`

---

### Task 3: Low lip fixture (#1253, lane B)

**Files:**
- Test: `KhaozEngine.Movement.Tests/LowLipTraversalTests.cs`

**Interfaces:**
- Consumes: existing `GroundTraversalProbeTests.FlatWorld()`, `PhysicsNavBake.Capture`, `BuildProfile`, `NpcGroundMovement.Step`.
- Fixture per design S5: `FlatWorld()` plus `new BoxShape(new Vector3(1f, 0.0125f, 2f))` at `Pose.At(new Vector3(1f, 0.0125f, 0f))`, so the deck spans x 0 to 2 with its top at 0.025. Options bounds x -1 to 1, z -0.5 to 0.5, cell 0.25. Tuning `GroundTraversalProbeTests.Tuning with { CapsuleRadius = 0.3f, CapsuleHalfHeight = 0.75f, StepHeight = 0.4f }`.

- [ ] **Step 1: Write the tests.**

```csharp
[Theory] public void BakeAcceptsEdgesBesideTheLip(float fromX, float toX)
// (-0.375,-0.125), (-0.125,-0.375), (-0.125,0.125), (0.125,-0.125): the probe at deck or ground heights returns true,
// and nav.AllowsSegment returns true for the same feet.
[Fact] public void RouteCrossesOntoTheDeck()                         // -0.875 to 0.875: Complete
[Fact] public void LiveBodyMountsTheLipAtWalkPace()
// Body at x -0.875, direction +X at walk, 60 ticks through the Bepu context: grounded every tick, final feet Y within
// 0.001 of 0.025 and X above 0.2.
```

- [ ] **Step 2: Run.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~LowLipTraversalTests"`. Record every case's result in Outcome.
- [ ] **Step 3: Classify** by design S5. If the bake facts fail and the live fact passes, record the probe's final feet and slice count for the failing edge in Outcome and continue to Task 4. If the live fact fails, stop and report for Q4. If all pass, add the same three facts over a TileWorld bridge fixture in `KhaozEngine.TileWorld.Physics.Tests/LowLipTileWorldTests.cs` with a 2.5 cm drawn deck, run with `--filter "FullyQualifiedName~LowLipTileWorldTests"`, and if they pass too, stop and report that the lead does not reproduce.
- [ ] **Step 4: Commit the fixture** with the reproducing facts marked `[Fact(Skip = "#1253 RED, fixed in the next commit")]` only if Task 4 continues, otherwise as passing evidence. `test(movement): reproduce the low lip bake refusal`

---

### Task 4: Low lip probe fix, gated (#1253, lane B)

Runs only when Task 3 classified a probe fault.

**Files:**
- Modify: `KhaozEngine.Movement/GroundTraversalProbe.cs` (arrival judgment only), `docs/design/NPC-MOVEMENT-FIXES-DESIGN-2026-10-03.md` section 5
- Test: `KhaozEngine.Movement.Tests/LowLipTraversalTests.cs` (unskip), `GroundTraversalProbeTests.cs` (one new guard fact)

**Interfaces:**
- No signature change. `TryEdge` keeps its parameters and its 1 mm `ArrivalTolerance` for every edge the fix does not name.

- [ ] **Step 1: Write the rule** into design S5 from Task 3's evidence, before code. It must say which slices may arrive under it and why a wall stop cannot.
- [ ] **Step 2: Add the guard fact** `StopShortAgainstAWallIsNeverArrival` in `GroundTraversalProbeTests`: a 0.3 m wall whose face sits 0.005 m short of the target cell centre refuses the edge. Unskip the Task 3 facts.
- [ ] **Step 3: Run RED.** `--filter "FullyQualifiedName~LowLipTraversalTests|FullyQualifiedName~StopShortAgainstAWallIsNeverArrival"`. Expected: the lip facts fail, the wall fact passes.
- [ ] **Step 4: Implement** the written rule.
- [ ] **Step 5: Run GREEN.** The Step 3 filter, then `--filter "FullyQualifiedName~GroundTraversalProbeTests|FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~GroundNavigationBake"` on Movement.Tests and `--filter "FullyQualifiedName~TileWorldMovementNavigationTests|FullyQualifiedName~TileWorldNavigationBakeTests"` on TileWorld.Physics.Tests. Expected: all pass. Record counts.
- [ ] **Step 6: Commit.** `fix(movement): accept low lip edges the core can mount`

---

### Task 5: Water capture (#1256, lane C, gated by Q1)

**Files:**
- Create: `KhaozEngine.Movement/PhysicsNavWater.cs`
- Modify: `KhaozEngine.Movement/PhysicsNavBake.cs:39-95`, `KhaozEngine.Movement/PhysicsNavColumns.cs`, `GroundNavigationBake.Writer.cs` and `GroundNavigationBake.Reader.cs` (capture section and payload bound)
- Test: `KhaozEngine.Movement.Tests/WaterCaptureTests.cs`, additions to `GroundNavigationBakeRefusalTests.cs` and `GroundNavigationBakeRoundTripTests.cs`

**Interfaces:**
- Produces: `internal readonly record struct PhysicsNavWater(int Cell, float SurfaceY, uint Areas)`.
- Produces: `internal ReadOnlySpan<PhysicsNavWater> PhysicsNavColumns.Water { get; }`, ascending by cell, and `internal bool PhysicsNavColumns.HasMedium { get; }`. Both constructors and `Own` gain the water array and the flag. Ground reads (`GetColumn`, `SampleColumn`) are unchanged.
- Capture rule exactly as design S2 Capture. The classifier is called once per water entry at `(x, SurfaceY, z)`.
- Payload: design S2 format bullets for the capture section, reader invariants and the bound term `5 + 12 x C`, including the `uint8` medium flag that stores `HasMedium`.

- [ ] **Step 1: Write the tests.** Medium fixtures use `(x, z, feetY) => x > 0f ? new MovementMedium(1.5f, feetY < 1.5f) : MovementMedium.Dry` over `FlatWorld()`.

```csharp
[Fact] public void NoMediumRecordsNoWater()                          // HasMedium false, Water empty, surfaces unchanged
[Fact] public void WetColumnsRecordTheSurfaceAndClassifierAreas()
// Columns with x > 0 hold one entry at 1.5 with the classifier's bits for (x, 1.5, z). Columns with x < 0 hold none.
[Fact] public void MediumIsSampledAtTheLowestSurface()                // deck at 3 m over the pool: one entry from the bed sample
[Fact] public void EmptyColumnSamplesTheProbeFloor()                  // a hole in the floor still records water
[Fact] public void WaterSectionRoundTrips()                           // Create, WriteTo, Load: Water and HasMedium equal
// Refusal additions: water count above C, unsorted cells, NaN surface, surface below the lowest surface,
// HasMedium byte 2, one water entry with HasMedium 0. Each resealed and Corrupt. Every truncation of a wet bake is Corrupt or NotABake.
```

- [ ] **Step 2: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~WaterCaptureTests|FullyQualifiedName~GroundNavigationBake"`. Expected: compile FAIL on `Water` and `HasMedium`.
- [ ] **Step 3: Implement** capture, storage, writer and reader.
- [ ] **Step 4: Run GREEN** with the Step 2 filter, then the whole Movement.Tests project. Expected: all pass. Existing golden identity unchanged, since identity is not touched here.
- [ ] **Step 5: Commit.** `feat(movement): capture medium water surfaces for baked navigation`

---

### Task 6: Aquatic profile build (#1256, lane C)

**Files:**
- Create: `KhaozEngine.Movement/AquaticColumns.cs`, `SwimTraversalProbe.cs`, `SwimPace.cs`, `GroundMoveContext.Swim.cs`
- Modify: `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`, `KhaozEngine.Movement/GroundNavigation.cs`, `KhaozEngine.Movement/GroundTraversalProbe.cs:62` (`Near` becomes `internal`, nothing else)
- Test: `KhaozEngine.Movement.Tests/AquaticProfileTests.cs`, `KhaozEngine.Movement.Tests/SwimTraversalProbeTests.cs`

**Interfaces:**
- Consumes: Task 5 `PhysicsNavColumns.Water` and `HasMedium`.
- Produces: `internal static PhysicsNavColumns AquaticColumns.Derive(PhysicsNavColumns captured, in MoveTuning tuning)`, rule exactly design S2 Aquatic columns, single precision in the written order. The result carries the same water entries and flag.
- Produces: `internal static float SwimPace.Bound(in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context)`. When `context.Medium` is not null and `CharacterMovement.ResolveSwimming(body.Swimming, medium(x, z, feetY), feetY, tuning)` is true, returns `SwimSpeed x max(0, WadeSpeedScale) x SpeedScale x dt` and throws `ArgumentOutOfRangeException` if non-finite. Otherwise returns `RangeApproachCore.TravelBound(...)`.
- Produces: `internal bool GroundMoveContext.SwimClear(in MoveState body, in MoveTuning tuning)`, rule design S2 Clearance check, using `CharacterMovement.CapsuleFor(tuning)` at the local pose through `MovementQueries ?? Physics`. True without physics.
- Produces: `internal static bool SwimTraversalProbe.TryEdge(GroundMoveContext context, in MoveTuning tuning, Vector3 fromFeet, bool fromFloats, Vector3 toFeet, bool toFloats, Func<Vector3, bool> acceptsFootprint, float stepSeconds, int maxSteps)`, rules design S2 Proofs. Grounded arrival uses `GroundTraversalProbe.Near`.
- Produces: `public GroundNavigation PhysicsNavBake.BuildProfile(in MoveTuning tuning, NavAreaFilter areas, bool swims)`. The two-argument overload calls it with false. With `swims` true and `Columns.HasMedium` false it throws `ArgumentException`. With true, the footprint, candidates and graph read `AquaticColumns.Derive(Columns, tuning)`. A node or link endpoint is a float when its surface came from a water entry. Float holds and every edge or link with a float endpoint use `SwimTraversalProbe`. Others use `GroundTraversalProbe` as today.
- Produces: `public bool GroundNavigation.Swims { get; }` and an internal constructor overload carrying it. `ValidateTuning` on an aquatic profile also compares the three swim fractions named in design S2 Runtime profile.

- [ ] **Step 1: Write the probe tests.** Bepu `FlatWorld()` lowered to a bed at y = -2 for the pool, medium surface 0, tuning radius 0.2, half height 0.25, step 0.2.

```csharp
[Fact] public void FloatEdgeAcrossDeepWaterIsAccepted()
[Fact] public void LowDeckAtTheWaterlineRefusesFloatEdges()          // box bottom at float feet + 0.3 over the edge
[Fact] public void PostBesideTheLineRefusesFloatEdges()
[Fact] public void ZeroSwimSpeedRefusesFloatEdges()
[Fact] public void ShoreEdgesAreProvenBothWays()                     // ramp bed from 0 to -1, wade node to float node and back
[Fact] public void BedGrazeNearTheShoreIsClear()                     // SwimClear true for an upward walkable MTV under StepHeight
```

- [ ] **Step 2: Write the profile tests.**

```csharp
[Fact] public void SteepChannelRoutesAcrossTheFloatLayer()
// A pool whose bed walls are steeper than MaxSlopeRadians. The ground profile's route bank to bank is not Complete.
// The aquatic profile's route is Complete and every interior waypoint's height equals the float height.
[Fact] public void GroundProfileIgnoresWaterEntries()
// Same world captured with and without a medium: ground profiles equal by BakeEquivalence.AssertEquivalent.
[Fact] public void AquaticProfileRequiresACapturedMedium()           // ArgumentException
[Fact] public void FloatingFeetResolveOntoTheGraph()                 // AllowsSegment true between two floating feet
[Fact] public void SwimFractionMismatchIsRefused()                   // ValidateTuning throws for each of the three fractions
[Fact] public void SwimPaceUsesSwimSpeedWhileSwimming()              // bound equals 2.5 x zone x scale x dt, walk bound on land
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~AquaticProfileTests|FullyQualifiedName~SwimTraversalProbeTests"`. Expected: compile FAIL on the missing overload and types.
- [ ] **Step 4: Implement** the interfaces.
- [ ] **Step 5: Run GREEN** with the Step 3 filter, then `--filter "FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~GroundTraversalProbeTests|FullyQualifiedName~ProfileAllocationTests"`. Expected: all pass. `BuildProfileStaysWithinOneKibPerColumn` stays green for ground profiles.
- [ ] **Step 6: Commit.** `feat(movement): bake float layers for swimming profiles`

---

### Task 7: Aquatic profiles in baked sets (#1256, lane C)

**Files:**
- Modify: `KhaozEngine.Movement/NavBakeProfile.cs`, `NavBakeIdentity.cs:95-105` (profile entry), its decoded profile record and comparison, `GroundNavigationBake.cs:83`, `GroundNavigationBake.Writer.cs`, `GroundNavigationBake.Reader.cs`
- Test: `KhaozEngine.Movement.Tests/NavBakeIdentityTests.cs`, `GroundNavigationBakeRoundTripTests.cs`, `GroundNavigationBakeRefusalTests.cs`, `BakeEquivalence.cs` (also compares `Swims` and the derived columns)

**Interfaces:**
- Consumes: Task 6 `BuildProfile(tuning, areas, swims)`, `AquaticColumns.Derive`, `GroundNavigation.Swims`.
- Produces: `public bool NavBakeProfile.Swims { get; init; }`, default false.
- Identity: design S2 Bake format identity bullet. Decoded profiles carry `Swims`. `ProfilesChanged` detail names `Swims`.
- `Create` calls `BuildProfile(profile.Tuning, profile.Areas, profile.Swims)` and its refusal for a capture without a medium reaches the caller. `Load` derives each aquatic profile's columns before building its footprint. Ground profiles keep sharing one column instance.

- [ ] **Step 1: Write the tests.**

```csharp
[Fact] public void IdentityBytesMatchTheGoldenFingerprint()          // existing fact, constant re-recorded once at GREEN
[Fact] public void SwimFlagChangesTheIdentity()
[Fact] public void SwimFlagByteOtherThanZeroOrOneIsCorrupt()
[Fact] public void StaleSwimFlagIsRefusedNamingIt()                  // ProfilesChanged with "Swims" in Detail
[Fact] public void LoadedAquaticProfileMatchesTheFreshBuild()        // steep channel fixture, BakeEquivalence over both profiles
[Fact] public void RewritingALoadedAquaticBakeReproducesItsBytes()
[Fact] public void GroundProfilesStillShareOneColumnSnapshot()
[Fact] public void AquaticProfileWithoutACapturedMediumIsRefusedAtCreate()
```

- [ ] **Step 2: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~NavBakeIdentityTests|FullyQualifiedName~GroundNavigationBake"`. Expected: compile FAIL on `NavBakeProfile.Swims`.
- [ ] **Step 3: Implement.** Re-record the golden constant and its inputs in Outcome, with the reason: the unreleased v1 identity gained the swim flag.
- [ ] **Step 4: Run GREEN** with the Step 2 filter, then the whole Movement.Tests project and `--filter "FullyQualifiedName~TileWorldNavigationBakeTests"` on TileWorld.Physics.Tests. Expected: all pass.
- [ ] **Step 5: Commit.** `feat(movement): store swimming profiles in baked navigation sets`

---

### Task 8: Swim steering in MoveToRange (#1256, joins lanes A and C)

**Files:**
- Modify: `KhaozEngine.Movement/MoveToRangeOptions.cs`, `MoveToRange.cs` (hold rule, bound call, constructor check), `MoveToRange.Approach.cs:24-26`
- Test: `KhaozEngine.Movement.Tests/MoveToRangeSwimTests.cs`, `KhaozEngine.Movement.Tests/NpcSwimNavigationAcceptanceTests.cs`

**Interfaces:**
- Consumes: Task 2 `MoveToRangeOptions` and constructors. Task 6 `SwimPace.Bound`, `GroundNavigation.Swims`, `BuildProfile(..., swims: true)`.
- Produces: `public bool MoveToRangeOptions.SteerWhileSwimming { get; init; }`, default false.
- Hold, admission and bound rules exactly design S3. With the option false, `Tick` calls `RangeApproachCore.TravelBound` as today, so default behaviour is unchanged. With it true, `Tick` calls `SwimPace.Bound`.
- The `GroundNavigation` constructor throws `ArgumentException` naming `options` when `SteerWhileSwimming` is true and `navigation.Swims` is false.

- [ ] **Step 1: Record the base count** with the Task 2 Step 1 filter on the current branch.
- [ ] **Step 2: Write the tests.** Scripted states over a flat analytic context with a constant deep medium for the unit facts. The acceptance fact bakes the Task 6 steep channel world with an aquatic duck profile (radius 0.2, half height 0.25) and drives `Tick` then `NpcGroundMovement.Step` through the Bepu context with the medium.

```csharp
[Fact] public void SwimmingBodyStaysSuspendedWithoutSwimPermission()
[Fact] public void AirborneBodyStaysSuspendedWithSwimPermission()    // Grounded false, Swimming false: Suspended, also when in range
[Fact] public void CommittedBodyStaysSuspendedWithSwimPermission()   // active commitment, grounded or swimming: Suspended before InRange
[Fact] public void PermittedSwimRefusesAnAirborneExitStep()          // prediction airborne and not swimming: zero command
[Fact] public void SwimTravelBoundCapsTheWaypointAtSwimSpeedAboveWalk()
// Swimming body 0.05 m from a waypoint, SwimSpeed 2.5, walk 2: the step lands on the waypoint within 1e-5 m.
[Fact] public void SwimPermissionRequiresAnAquaticProfile()          // ArgumentException
[Fact] public void DefaultOptionsLeaveSwimmingBodiesSuspended()      // regression on the old constructor
[Fact] public void SwimmingDuckCrossesDeepWaterIntoRange()
// Duck starts swimming on one side of the channel, point target on the far bank, range 0.3. Within 600 ticks the
// status is InRange, ReachGeometry.Within holds for the real body, and no tick's capsule overlaps a static beyond the
// clearance rule.
[Fact] public void SwimmingDuckRoutesAroundALowDeck()                // deck at the waterline across the direct line
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRangeSwimTests|FullyQualifiedName~NpcSwimNavigationAcceptanceTests"`. Expected: compile FAIL on `SteerWhileSwimming`.
- [ ] **Step 4: Implement** the rules.
- [ ] **Step 5: Run GREEN** with the Step 3 filter and the Step 1 filter. Expected: all pass, and the Step 1 filter's count equals the recorded base plus the new cases it matches. Then `--filter "FullyQualifiedName~DirectMoveToRangeTests"` passes unchanged.
- [ ] **Step 6: Commit.** `feat(movement): steer swimming bodies on aquatic routes`

---

### Task 9: Living documentation and full verification

**Files:**
- Modify: `KhaozEngine.Movement/README.md` ("Bounded static physics capture" for water entries, "Capsule checked ground profiles" for aquatic profiles, "Baked profile sets" for the swim flag and water section, "Range steering and movement drivers" for `MoveToRangeOptions`, swim steering and carry)
- Modify: `KhaozEngine.Navigation/README.md` ("Following a path" for `ConsumePassedWaypoints` and `IsCollinearPassThrough`)
- Modify: `docs/USING-KHAOZENGINE.md` ("GroundNavigation contract", "Baked profile sets", "Range steering, NPC stepping and client path commands")
- Modify: `CHANGELOG.md`, extend the existing `## 20.20.0` entry. No new heading.
- Modify: `docs/INDEX.md` row and the design status line, after verification

- [ ] **Step 1: Add the release note bullets** to the `20.20.0` entry. Drop the swim bullets if Q1 removed Tasks 5 to 8, and the lip bullet if Task 4 did not run.

```markdown
- Aquatic navigation. `PhysicsNavBake.Capture` records one medium water surface per column when its context carries a
  medium. `BuildProfile(tuning, areas, swims: true)` and `NavBakeProfile.Swims` bake float surfaces at the height a
  swimming body rests, proven by swim steps through the live medium with a static clearance check. `MoveToRangeOptions`
  with `SteerWhileSwimming` steers a swimming body on such a profile. Airborne and committed bodies stay `Suspended`.
- `MoveToRangeOptions.CarryAlongRoute` keeps full pace along collinear route runs and still stops on every corner.
  `PathFollowConfig.ConsumePassedWaypoints` and `NavPath.IsCollinearPassThrough` back it.
- The ground bake accepts a step onto a low lip that the movement core mounts.
```

- [ ] **Step 2: Sweep.** `git grep -n -w -e MoveToRange -e PathFollowConfig -e BuildProfile -e NavBakeProfile -e Swimming -e Suspended -- '*.md'` and correct every stale description, including the direct driver's swim refusal note, which stays true. Reread `KhaozEngine.Movement/README.md` end to end.
- [ ] **Step 3: Reconcile.** `git fetch origin && git merge origin/main`. Resolve on this branch. Keep both changelog sections.
- [ ] **Step 4: Full verification, once.**

```sh
cd /Users/antonio/KhaozEngine/.worktrees/grimhollow-npc-movement
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: build exit 0 with zero warnings, test exit 0 with zero failures and nonzero totals per assembly, every guard exit 0. Record exit codes, totals and elapsed times in Outcome.

- [ ] **Step 5: Update status** in the design status line and `docs/INDEX.md` to implemented and verified, with no release claimed.
- [ ] **Step 6: Commit.** `docs(movement): document swimming routes and route carry`. The worker stops at its verified commit. The orchestrator merges, pushes and selects the release.

## Outcome

Not started.
