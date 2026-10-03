# NPC Movement Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an aquatic body follow baked routes and approach range while swimming, hold full pace along straight route runs, and settle the 2.5 cm lip bake refusal, all opt-in on staged `20.20.0`.

**Architecture:** Capture optionally records one medium water surface per column. An aquatic profile replaces swim-deep surfaces with float surfaces and proves them with a medium probe plus a static clearance check, so every guard reads the body's real feet. `MoveToRange` gains `RouteApproachOptions`, which opt into swim steering and straight-run carry, and the follower gains opt-in consumption of passed collinear waypoints. A fixture decides whether the lip refusal is a probe fault or a core fault before any fix.

**Tech Stack:** C# on .NET 10, xUnit, BepuPhysics in tests.

**Spec:** `docs/design/NPC-MOVEMENT-FIXES-DESIGN-2026-10-03.md`. Read it whole before Task 1. Sections are cited as S1 to S8, decisions as D1 to D6 and rulings as M1 to M11.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/grimhollow-npc-movement`, branch `feature/grimhollow-npc-movement`, from engine main `fefb78876`. Reconcile with current `origin/main` before the final verification.
- Ride staged `20.20.0`. The `20.20.0` tag waits for this round (M1). No version bump, no new changelog heading, no tag, no pack, no push of main.
- `FormatVersion` stays 1. Land-only bakes change KENB bytes and the golden fingerprint (M6).
- Additive public API only. Existing constructors, `BuildProfile(tuning, areas)`, capture output and every runtime default behave as today.
- Names (M10): `RouteApproachOptions`, `SteerWhileSwimming`, `CarryThroughStraightRuns`, `PathFollowConfig.ConsumePassedCollinearWaypoints`, `NavPath.IsCollinearPassThrough`, `GroundProfileOptions.Aquatic`, `GroundNavigation.Aquatic`, `NavBakeProfile.Aquatic`, `PhysicsNavBakeOptions.SampleWater`.
- No wire, `NetWorld` or `MoveState` change. No project reference change. Movement references exactly Locomotion, Navigation and Physics.
- Airborne non-swimming bodies and committed bodies return `Suspended` before `InRange` (ruling D0.2), with and without the new options.
- A mandatory turn is never consumed early (ruling D1.1). The strict accept radius stays `min(supplied, 1e-5 m)`.
- Locomotion changes only in Task 4b, and only when Task 3 proves the live core cannot mount the lip (M4).
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
| Driver options and carry | new `KhaozEngine.Movement/RouteApproachOptions.cs`, `MoveToRange.cs`, new `MoveToRange.Carry.cs` | 2 | A |
| Lip fixture | new `KhaozEngine.Movement.Tests/LowLipTraversalTests.cs` | 3 | B |
| Shared ground arrival and probe fix | `KhaozEngine.Movement/GroundTraversalProbe.cs`, `GroundTraversalProbeTests.cs`, design S5 | 4 | B |
| Core step-up fix (gated) | `KhaozEngine.Locomotion/CharacterMovement*.cs`, a new Locomotion test file in `KhaozEngine.Game.Tests/Locomotion`, design S5 | 4b | B |
| Water capture | `KhaozEngine.Movement/PhysicsNavBakeOptions.cs`, `NavBakeIdentity.cs` (options), `PhysicsNavBake.cs`, `PhysicsNavColumns.cs`, new `PhysicsNavWater.cs`, `GroundNavigationBake.Writer.cs` and `.Reader.cs` capture section | 5 | C |
| Aquatic profile | new `GroundProfileOptions.cs`, `AquaticColumns.cs`, `SwimTraversalProbe.cs`, `SwimPace.cs`, `GroundMoveContext.Swim.cs`, modified `PhysicsNavBake.Profiles.cs`, `PhysicsNavColumns.cs` (float flag), `GroundNavigation.cs` | 6 | C |
| Aquatic bake | `NavBakeProfile.cs`, `NavBakeIdentity.cs` (profiles), `GroundNavigationBake.cs`, `.Writer.cs`, `.Reader.cs` profile section, `BakeEquivalence.cs` | 7 | C |
| Swim steering | `MoveToRange.cs` (hold and constructor check), `MoveToRange.Approach.cs`, `RouteApproachOptions.cs` (adds a property) | 8 | join |
| Living docs | `KhaozEngine.Movement/README.md`, `KhaozEngine.Navigation/README.md`, `docs/USING-KHAOZENGINE.md`, `CHANGELOG.md` 20.20.0 entry, `docs/INDEX.md`, design status line | 9 | last |

Dependencies and lanes:

- Lane A: Task 1, then Task 2.
- Lane B: Task 3, then Task 4, then Task 4b only when Task 3 classified a core fault.
- Lane C: Task 5 may start at once. Task 6 starts after Task 4, and Task 4b when it runs, have merged, because the swim probe consumes Task 4's shared ground arrival helper (M8). Task 7 follows Task 6.
- Task 8 needs Tasks 2 and 6. Task 9 runs last.

Lanes A, B and C touch disjoint files. Task 5 and Task 7 both edit `NavBakeIdentity.cs`, `PhysicsNavColumns.cs` is edited by Tasks 5 and 6, and the bake writer and reader by Tasks 5 and 7. All of these are inside lane C and run in order.

## Execution Notes

- After Task 4 or Task 4b merges, root reruns the focused filters of every lane that bakes through the movement core (M9): the Task 2 Step 1 filter, and, once they exist, the Task 5 Step 2, Task 6 Step 3 and Task 7 Step 2 filters. Record the counts in Outcome.
- Task 5 and Task 7 each re-record the golden identity fingerprint once, with their inputs and reason in Outcome.
- Root files the follow-up issue named in Task 9 Step 2 (M5).

## Review Focus

1. A body knocked airborne out of the water while swim steering is on must stay `Suspended`, not steer in the air. Task 8 `AirborneBodyStaysSuspendedWithSwimPermission`.
2. A hairpin route that doubles back must never consume its turning waypoint early. Task 1 `ReversalIsNeverAPassThrough` and `CornerIsNotConsumedBeforeItIsReached`.
3. A diagonal run near Hollowmere's 150 m coordinates must hold pace, not stall on float rounding at the 1e-5 m radius. Task 2 `DiagonalRunAtLargeCoordinatesHoldsPace`.
4. A bake written on one machine must load an aquatic profile with the same bits, since the float view is derived at load. Task 7 `LoadedAquaticProfileMatchesTheFreshBuild` and `RewritingALoadedAquaticBakeReproducesItsBytes`.
5. A game that already bakes with a medium context must get byte-identical capture output without the opt-in. Task 5 `MediumWithoutTheOptInCapturesIdenticalOutput`.

---

### Task 1: Collinear pass-through and passed waypoint consumption (#1257, lane A)

**Files:**
- Modify: `KhaozEngine.Navigation/NavPath.cs:57` to `public sealed partial class NavPath`
- Create: `KhaozEngine.Navigation/NavPath.PassThrough.cs`, `KhaozEngine.Navigation/PathFollower.Passed.cs`
- Modify: `KhaozEngine.Navigation/PathFollower.cs` (`PathFollowConfig`), `KhaozEngine.Navigation/PathFollower.Region.cs:129-146`
- Test: `KhaozEngine.Game.Tests/Navigation/NavPathPassThroughTests.cs`, `KhaozEngine.Game.Tests/Navigation/PathFollowerPassedWaypointTests.cs`

**Interfaces:**
- Produces: `public bool NavPath.IsCollinearPassThrough(int index)`, rule in design S4. Throws `ArgumentOutOfRangeException` for an index outside the waypoint list. Index 0 and the last index return false.
- Produces: `public bool PathFollowConfig.ConsumePassedCollinearWaypoints { get; init; }`, default false, documented beside `AcceptRadius`.
- Produces: private `bool PathFollower.PassedActive(Vector2 feetXz, int? agentLayer)` in `PathFollower.Passed.cs`, rule in design S4 including the `AcceptRadius + 4 x u` lateral allowance with `u = MathF.BitIncrement(m) - m` for the largest absolute coordinate `m`.
- `AdvanceReachedWaypoints` loop condition becomes proximity as today, or `_config.ConsumePassedCollinearWaypoints && PassedActive(...)`. The terminal Complete region check is unchanged and runs first.

- [ ] **Step 1: Write the geometry tests.**

```csharp
[Fact] public void StraightAndDiagonalInteriorWaypointsArePassThrough()
// Waypoints (0,0),(0.25,0),(0.5,0),(0.75,0) on layer 0: indices 1 and 2 true, 0 and 3 false.
// Same for the diagonal (0,0),(0.25,0.25),(0.5,0.5): index 1 true, 0 and 2 false.
[Theory] public void TurnsAreNeverPassThrough(float degrees)          // 45, 90, 135
[Fact] public void ReversalIsNeverAPassThrough()                     // (0,0),(1,0),(0,0): index 1 false
[Fact] public void LayerChangeOrHopSuccessorIsNotPassThrough()
[Theory] public void IndexOutsideTheListThrows(int index)            // -1 and Count
```

- [ ] **Step 2: Write the follower tests.** Use the `PathFollowerRegionTests` scripted planner pattern with a fixed `NavPath` and `AcceptRadius = 0.00001f`. Each fact ticks first with the feet exactly on waypoint 0, which proximity consumes, so `ActiveWaypointIndex` is 1 before the fact's own tick.

```csharp
[Fact] public void PassedCollinearWaypointIsConsumedWhenOptedIn()
// Route (0,0),(0.25,0),(0.5,0),(0.75,0). Tick at (0,0,0), then at (0.3,0,0): ActiveWaypointIndex is 2 with the
// option and 1 without.
[Fact] public void ConsumptionIsOffByDefault()                       // PathFollowConfig.Default.ConsumePassedCollinearWaypoints is false
[Fact] public void WaypointZeroIsNeverConsumedByPassing()
// Same route, first tick at (0.1,0,0): ActiveWaypointIndex stays 0 with the option on.
[Fact] public void CornerIsNotConsumedBeforeItIsReached()
// Route (0,0),(0.25,0),(0.25,0.25). Tick at (0,0,0), then at (0.26,0,0.001), past the corner but not on it: index
// stays 1 with the option on, and advances only when the feet are within AcceptRadius of (0.25,0).
[Fact] public void LateralMissKeepsTheWaypoint()                     // after the start tick, feet (0.3,0,0.001): index 1
[Fact] public void LayerMismatchKeepsTheWaypoint()                   // two-layer space, feet resolve to layer 1
[Fact] public void LargeCoordinateAllowanceAdmitsOneFloatStep()
// Diagonal route offset by (150,150), feet one float step off the line past waypoint 1: consumed. Two millimetres
// off: kept.
[Fact] public void FinalCompleteRegionWaypointStillNeedsMembership()
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~NavPathPassThroughTests|FullyQualifiedName~PathFollowerPassedWaypointTests"`. Expected: compile FAIL, CS1061 on `IsCollinearPassThrough` and CS0117 on `ConsumePassedCollinearWaypoints`.
- [ ] **Step 4: Implement** the interfaces above. All geometry in double.
- [ ] **Step 5: Run GREEN** with the Step 3 filter, then `--filter "FullyQualifiedName~KhaozEngine.Tests.Navigation"`. Expected: all pass with nonzero counts. Record both counts in Outcome.
- [ ] **Step 6: Commit.** `feat(navigation): consume passed collinear waypoints on request`

---

### Task 2: Route approach options and straight-run carry (#1257, lane A)

**Files:**
- Create: `KhaozEngine.Movement/RouteApproachOptions.cs`, `KhaozEngine.Movement/MoveToRange.Carry.cs`
- Modify: `KhaozEngine.Movement/MoveToRange.cs:18-33` (constructors), `:60-72` (route tick), `:84-108` (`StrictConfig`)
- Test: `KhaozEngine.Movement.Tests/MoveToRangeCarryTests.cs`

**Interfaces:**
- Consumes: Task 1 `NavPath.IsCollinearPassThrough`, `PathFollowConfig.ConsumePassedCollinearWaypoints`.
- Produces: `public sealed record RouteApproachOptions` with `public static RouteApproachOptions Default { get; }` and `public bool CarryThroughStraightRuns { get; init; }`. Task 8 adds `SteerWhileSwimming`.
- Produces: `public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow, RouteApproachOptions options)` and `public MoveToRange(IRegionPathPlanner planner, NavSpace space, Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow, RouteApproachOptions options)`. Null options throw `ArgumentNullException`. The existing constructors pass `RouteApproachOptions.Default`. The instance keeps `private readonly RouteApproachOptions _options`.
- `StrictConfig(follow, carry)` sets `ConsumePassedCollinearWaypoints = carry || follow.ConsumePassedCollinearWaypoints`.
- Produces: private `bool TryCarry(in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context, Vector2 waypoint, float bound, out Vector2 command)` in `MoveToRange.Carry.cs`. Returns false unless `CarryThroughStraightRuns`, the remaining distance to `waypoint` is below `bound`, and `ActivePath.IsCollinearPassThrough(ActiveWaypointIndex)`. Aims at the run end per design S4, predicts, and returns true only when `AllowsStep` admits the prediction. The route tick uses it before today's capped command, then runs `StopAtRange` on whichever command it keeps.

- [ ] **Step 1: Record the base count.** On unchanged source run `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRange|FullyQualifiedName~NpcRangeNavigation|FullyQualifiedName~PlayerPathMovement"` and record the passed count in Outcome.
- [ ] **Step 2: Write the tests.** Flat analytic context `MoveToRangeTests.Flat`, tuning `MoveToRangeTests.Tuning` (walk 2 m/s), `dt = 1f / 30f`, an always-true guard. Region planning needs surface heights (`GridPathPlanner.Region.cs:20`), and `MoveToRangeTests.Space` is a `FromWalkable` grid without them, so these facts build their own heights-bearing space: `NavSpace.Single(NavGrid.FromSurfaces(32, 32, 0.25f, ox, oz, (_, _) => new NavSurfaceSample(true, 0f, float.PositiveInfinity), stepHeight: 0.4f, agentHeight: 1.5f))` with a `GridPathPlanner` over it. The straight fixture uses origin (-4, -4). The offset diagonal fixture uses origin (146, 146). The body starts on a cell centre. Loop `Tick` then `NpcGroundMovement.Step`.

```csharp
[Fact] public void StraightRunTravelsTheFullBoundEveryTick()
// Point target 4 m along +X, range 0, CarryThroughStraightRuns. From the tick after the body lands on waypoint 0 until
// the tick before the run end, every tick's horizontal travel equals the travel bound within 1e-5 m.
[Fact] public void StraightCellRouteHoldsFullWalkSpeed()
// Same route. After 30 ticks the body has travelled at least 1.97 m with the option (about 1.98 m expected, design
// S4). The same run with Default options travels at most 1.89 m: on main the first tick plans and moves, 28 ticks
// cover seven cells and two more add 0.133 m, about 1.883 m (design S4, 1.875 m/s is the steady rate).
[Fact] public void DiagonalRunAtLargeCoordinatesHoldsPace()
// Space and route offset by (150, 150), diagonal target 4 m away. After 30 ticks the body has travelled at least
// 1.94 m with the option (about 1.95 m expected), and every tick between waypoint 0 and the run end travels the full
// bound within 1e-4 m.
[Fact] public void CarryStopsAtAMandatoryCorner()
// L-shaped route through a wall gap. Some tick ends with the body's XZ exactly on the corner waypoint, and no tick's
// feet leave the route's cells.
[Fact] public void RefusedCarryFallsBackToTheWaypoint()
// Guard refuses any segment ending beyond x = 0.5. The body still lands on (0.5, 0) and the status stays Following.
[Fact] public void DefaultOptionsLeaveCommandsUnchanged()
// Equal RangeSteering bits over 60 ticks from the old constructor and the new one with Default.
[Fact] public void NullOptionsAreRejected()
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRangeCarryTests"`. Expected: compile FAIL, CS0246 on `RouteApproachOptions`. Then stub the record and constructors without carry, rerun, and record in Outcome that the pace facts fail near 1.875 m.
- [ ] **Step 4: Implement** carry as in Interfaces.
- [ ] **Step 5: Run GREEN** with the Step 3 filter, then the Step 1 filter. Expected: all pass, and the Step 1 filter's count equals the recorded base plus the new class's cases. `FastScaledApproachCapsTheWaypointAndRangeTravel` stays green. Record the measured first-second distances in Outcome.
- [ ] **Step 6: Commit.** `feat(movement): carry route travel through straight runs`

---

### Task 3: Low lip fixture (#1253, lane B)

**Files:**
- Test: `KhaozEngine.Movement.Tests/LowLipTraversalTests.cs`

**Interfaces:**
- Consumes: existing `GroundTraversalProbeTests.FlatWorld()`, `GroundTraversalProbeTests.Probe`, `PhysicsNavBake.Capture`, `BuildProfile`, `NpcGroundMovement.Step`.
- Fixture per design S5: `FlatWorld()` plus `new BoxShape(new Vector3(1f, 0.0125f, 2f))` at `Pose.At(new Vector3(1f, 0.0125f, 0f))`, so the deck spans x 0 to 2 with its top at 0.025. Options bounds x -1.5 to 1.5, z -1 to 1, cell 0.25, probe height and range as `PhysicsNavProfileTests.Options`. Tuning `GroundTraversalProbeTests.Tuning with { CapsuleRadius = 0.3f, CapsuleHalfHeight = 0.75f, StepHeight = 0.4f }`. Nodes exist from x -1.125 to 1.125 because the footprint refuses feet within one radius of the bounds.

- [ ] **Step 1: Write the tests.**

```csharp
[Theory] public void BakeAcceptsEdgesBesideTheLip(float fromX, float toX)
// (-0.375,-0.125), (-0.125,-0.375), (-0.125,0.125), (0.125,-0.125), z 0.125 (a cell centre row): the probe at deck or ground heights returns
// true, and nav.AllowsSegment returns true for the same feet.
[Fact] public void RouteCrossesOntoTheDeck()                         // -1.125 to 1.125 at z 0.125: Complete
[Fact] public void LiveBodyMountsTheLipAtWalkPace()
// Body standing at (-1.125, z 0.125), direction +X at walk, 60 ticks through the Bepu context: grounded every tick, final feet
// Y within 0.001 of 0.025 and X above 0.2.
```

- [ ] **Step 2: Run.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~LowLipTraversalTests"`. Record every case's result in Outcome, and the measured cap clearance over the deck corner at x -0.125 (design S5 gives 2.28 mm by formula).
- [ ] **Step 3: Classify** by design S5. Bake facts fail and the live fact passes: probe fault, record the probe's final feet and slice count for each failing edge, continue to Task 4. Live fact fails: core fault, record the body's final state, continue to Task 4 for the shared helper only, then Task 4b. All pass: add the same three facts over a TileWorld bridge fixture in `KhaozEngine.TileWorld.Physics.Tests/LowLipTileWorldTests.cs` with a 2.5 cm drawn deck, run with `--filter "FullyQualifiedName~LowLipTileWorldTests"`, and if they pass too, stop and report that the lead does not reproduce.
- [ ] **Step 4: Commit the fixture.** Reproducing facts carry `[Fact(Skip = "#1253 RED, fixed by Task 4")]` or `"... Task 4b"` so the branch stays green. `test(movement): reproduce the low lip bake refusal`

---

### Task 4: Shared ground arrival helper and probe fix (#1253, lane B)

Step 1 always runs, because Task 6 consumes the helper (M8). Steps 2 to 6 run only when Task 3 classified a probe fault.

**Files:**
- Modify: `KhaozEngine.Movement/GroundTraversalProbe.cs`, design S5
- Test: `KhaozEngine.Movement.Tests/GroundTraversalProbeTests.cs`, `LowLipTraversalTests.cs` (unskip)

**Interfaces:**
- Produces: `internal static bool GroundTraversalProbe.GroundArrived(in MoveState body, Vector3 feet, Vector3 target, in MoveTuning tuning)`, the one rule for whether a grounded slice has arrived. Today it is `body.Grounded` and a straight-line distance from `feet` to `target` within `ArrivalTolerance`. `TryEdge` calls it at both of today's arrival sites. `TryEdge` keeps its signature.

- [ ] **Step 1: Extract the helper** with behaviour unchanged. Run `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~GroundTraversalProbeTests|FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~GroundNavigationBake"` before and after and record equal counts. Commit `refactor(movement): share the ground arrival rule`. Stop here unless Task 3 classified a probe fault.
- [ ] **Step 2: Write the rule** into design S5 from Task 3's evidence, before code. It must say which slices may arrive under it and why a body stopped by a wall cannot.
- [ ] **Step 3: Add the guard fact** `StopShortAgainstAWallIsNeverArrival` in `GroundTraversalProbeTests` with tuning `GroundTraversalProbeTests.Tuning with { CapsuleRadius = 0.3f, CapsuleHalfHeight = 0.75f, StepHeight = 0.4f }`, since the shared tuning's radius is 0.2 (`GroundTraversalProbeTests.cs:15`): `FlatWorld()` plus a wall box whose face sits 0.295 m from the target cell centre along the edge, 5 mm inside the surface of the 0.3 m capsule standing there. `Probe` from 0.25 m back to the target returns false. Unskip the Task 3 facts.
- [ ] **Step 4: Run RED.** `--filter "FullyQualifiedName~LowLipTraversalTests|FullyQualifiedName~StopShortAgainstAWallIsNeverArrival"`. Expected: the lip bake facts fail, the wall fact and the live fact pass.
- [ ] **Step 5: Implement** the written rule inside `GroundArrived`.
- [ ] **Step 6: Run GREEN.** The Step 4 filter, then the Step 1 filter on Movement.Tests and `--filter "FullyQualifiedName~TileWorldMovementNavigationTests|FullyQualifiedName~TileWorldNavigationBakeTests"` on TileWorld.Physics.Tests. Expected: all pass. Record counts.
- [ ] **Step 7: Commit.** `fix(movement): accept low lip edges the core can mount`

---

### Task 4b: Core step-up fix, gated (#1253, lane B, ruling M4)

Runs only when Task 3 classified a core fault: the live body cannot mount the 2.5 cm lip.

**Files:**
- Modify: `KhaozEngine.Locomotion/CharacterMovement*.cs`, only the files the root cause names
- Test: Task 3's `LiveBodyMountsTheLipAtWalkPace` (unskip), new `KhaozEngine.Game.Tests/Locomotion/LowLipStepUpTests.cs`

**Interfaces:**
- No public signature change. The fix is a targeted step-up or skin rule for a lip below `StepHeight` whose top the capsule's bottom cap overhangs by less than `SkinWidth`.

- [ ] **Step 1: Root-cause.** Trace one failing tick of the live fact through `CharacterMovement.Collision.cs` step-up eligibility and `TryStepUp`. Write the cause and the intended rule change into design S5 and Outcome before code.
- [ ] **Step 2: Write the Locomotion fact** `CapsuleMountsALipInsideTheContactSkin` in `LowLipStepUpTests`: the same box lip as Task 3 against `CharacterMovement.StepTowards` directly, walk and run, both directions.
- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~LowLipStepUpTests"` and the Task 3 filter. Expected: the new fact and the live fact fail.
- [ ] **Step 4: Implement** the rule from Step 1.
- [ ] **Step 5: Run GREEN and the guards.**

```sh
dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion"
dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~NetWorld"
```

Expected: all pass with nonzero counts, including every Task 3 fact. Record counts in Outcome.

- [ ] **Step 6: Commit.** `fix(locomotion): mount a lip inside the contact skin`

---

### Task 5: Opt-in water capture (#1256, lane C)

**Files:**
- Create: `KhaozEngine.Movement/PhysicsNavWater.cs`
- Modify: `KhaozEngine.Movement/PhysicsNavBakeOptions.cs`, `NavBakeIdentity.cs` (options encoding, decoding and comparison), `PhysicsNavBake.cs:39-95`, `PhysicsNavColumns.cs`, `GroundNavigationBake.Writer.cs` and `GroundNavigationBake.Reader.cs` (capture section and payload bound)
- Test: `KhaozEngine.Movement.Tests/WaterCaptureTests.cs`, `NavBakeIdentityTests.cs`, additions to `GroundNavigationBakeRefusalTests.cs` and `GroundNavigationBakeRoundTripTests.cs`

**Interfaces:**
- Produces: `public bool PhysicsNavBakeOptions.SampleWater { get; init; }`, default false, an init property because the positional record shipped in v20.19.0.
- Produces: `internal readonly record struct PhysicsNavWater(int Cell, float SurfaceY, uint Areas)` and `internal ReadOnlySpan<PhysicsNavWater> PhysicsNavColumns.Water { get; }`, ascending by cell. Both constructors and `Own` take the water array. Ground reads (`GetColumn`, `SampleColumn`) are unchanged.
- Capture rule exactly as design S2 Capture. `SampleWater` with a null `context.Medium` throws `ArgumentException` naming `context`. The classifier is called once per water entry at `(x, SurfaceY, z)`.
- Identity: `SampleWater` encoded as one `uint8` after `MaxEdgeProbeSteps` (M7). `OptionsChanged` names it. A byte other than 0 or 1 is `Corrupt`.
- Payload: design S2 capture section, reader invariants and the bound term `4 + 12 x C`. A nonzero water count with `SampleWater` false is `Corrupt`.

- [ ] **Step 1: Write the tests.** Medium fixtures use `(x, z, feetY) => x > 0f ? new MovementMedium(1.5f, feetY < 1.5f) : MovementMedium.Dry` over `FlatWorld()`.

```csharp
[Fact] public void SampleWaterIsOffByDefault()                        // new PhysicsNavBakeOptions(...).SampleWater is false
[Fact] public void MediumWithoutTheOptInCapturesIdenticalOutput()
// Capture with and without the medium on the context, SampleWater false: every column's surfaces equal as bits, Water
// empty, the medium delegate never called, and Create plus WriteTo give identical bytes.
[Fact] public void SampleWaterWithoutAMediumIsRefused()               // ArgumentException
[Fact] public void WetColumnsRecordTheSurfaceAndClassifierAreas()
// SampleWater true: columns with x > 0 hold one entry at 1.5 with the classifier's bits for (x, 1.5, z). Columns with
// x < 0 hold none.
[Fact] public void MediumIsSampledAtTheLowestSurface()                // deck at 3 m over the pool: one entry from the bed sample
[Fact] public void EmptyColumnSamplesTheProbeFloor()                  // a hole in the floor still records water
[Fact] public void WaterSectionRoundTrips()                           // Create, WriteTo, Load: Water equal
// NavBakeIdentityTests: EveryOptionFieldChangesTheIdentity now covers 14 fields, the 13 constructor parameters plus
// SampleWater. SampleWaterByteOtherThanZeroOrOneIsCorrupt. IdentityBytesMatchTheGoldenFingerprint re-recorded with
// SampleWater false.
// Refusal additions: water count above C, unsorted cells, NaN surface, surface below the lowest surface, one water
// entry with SampleWater false. Each resealed and Corrupt. Every truncation of a wet bake is Corrupt or NotABake.
```

- [ ] **Step 2: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~WaterCaptureTests|FullyQualifiedName~NavBakeIdentityTests|FullyQualifiedName~GroundNavigationBake"`. Expected: compile FAIL on `SampleWater` and `Water`.
- [ ] **Step 3: Implement** options, capture, storage, identity, writer and reader. Re-record the golden constant and its inputs in Outcome with the reason: the unreleased v1 identity gained `SampleWater` (M6, M7).
- [ ] **Step 4: Run GREEN** with the Step 2 filter, then the whole Movement.Tests project. Expected: all pass. `FastScaledApproachCapsTheWaypointAndRangeTravel` keeps its output.
- [ ] **Step 5: Commit.** `feat(movement): opt-in water capture for baked navigation`

---

### Task 6: Aquatic profile build (#1256, lane C, after Task 4 and any Task 4b merge)

**Files:**
- Create: `KhaozEngine.Movement/GroundProfileOptions.cs`, `AquaticColumns.cs`, `SwimTraversalProbe.cs`, `SwimPace.cs`, `GroundMoveContext.Swim.cs`
- Modify: `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`, `PhysicsNavColumns.cs` (per-surface float flag), `GroundNavigation.cs`
- Test: `KhaozEngine.Movement.Tests/AquaticProfileTests.cs`, `KhaozEngine.Movement.Tests/SwimTraversalProbeTests.cs`

**Interfaces:**
- Consumes: Task 5 `PhysicsNavColumns.Water`, `PhysicsNavBakeOptions.SampleWater`. Task 4 `GroundTraversalProbe.GroundArrived`.
- Produces: `public sealed record GroundProfileOptions` with `public static GroundProfileOptions Default { get; }` and `public bool Aquatic { get; init; }`.
- Produces: `internal bool PhysicsNavColumns.IsFloat(int x, int z, int surface)`, false for captured columns.
- Produces: `internal static PhysicsNavColumns AquaticColumns.Derive(PhysicsNavColumns captured, in MoveTuning tuning)`, rule exactly design S2 Aquatic columns, including the clamped headroom and the deck upper bound, single precision in the written order. The result carries the same water entries.
- Produces: `internal static float SwimPace.Bound(in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context)`. When `context.Medium` is not null and `CharacterMovement.ResolveSwimming(body.Swimming, medium(x, z, feetY), feetY, tuning)` is true, returns `SwimSpeed x max(0, WadeSpeedScale) x SpeedScale x dt` and throws `ArgumentOutOfRangeException` if non-finite. Otherwise returns `RangeApproachCore.TravelBound(...)`.
- Produces: `internal bool GroundMoveContext.SwimClear(in MoveState body, in MoveTuning tuning)`, rule design S2 Clearance check, using `CharacterMovement.CapsuleFor(tuning)` at the local pose through `MovementQueries ?? Physics`. True without physics.
- Produces: `internal static bool SwimTraversalProbe.TryEdge(GroundMoveContext context, in MoveTuning tuning, Vector3 fromFeet, bool fromFloats, Vector3 toFeet, bool toFloats, Func<Vector3, bool> acceptsFootprint, float stepSeconds, int maxSteps)`, rules design S2 Proofs. The hold slice of a float start passes `SwimClear`. Slice bound is `min(SwimPace.Bound(...), CapsuleRadius)`. Grounded arrival calls `GroundTraversalProbe.GroundArrived`.
- Produces: `public GroundNavigation PhysicsNavBake.BuildProfile(in MoveTuning tuning, NavAreaFilter areas, GroundProfileOptions options)`. The two-argument overload passes `GroundProfileOptions.Default`. With `Aquatic` true it throws `ArgumentException` when `Options.SampleWater` is false (no sampled water) or the swim fractions break `Exit <= Submersion <= Enter`. The footprint, candidates and graph read `AquaticColumns.Derive(Columns, tuning)`. After layering, a node is a float node when `IsFloat` holds for the derived surface of its column whose height is bit-equal to the node's layer height. Float holds and every edge or link with a float endpoint use `SwimTraversalProbe`. Others use `GroundTraversalProbe` as today.
- Produces: `public bool GroundNavigation.Aquatic { get; }` and an internal constructor overload carrying it. `ValidateTuning` on an aquatic profile also compares the three swim fractions.

- [ ] **Step 1: Write the probe tests.** The pool world is `FlatWorld()` with a 2 m deep box pit whose bed is at y -2, medium surface 0 inside the pit. The context's ground height function returns -2 inside the pit and 0 outside, matching the captured bed, as design S2 requires for the swim floor. Duck tuning: radius 0.2, half height 0.25, step 0.2.

```csharp
[Fact] public void FloatEdgeAcrossDeepWaterIsAccepted()
[Fact] public void FloatHoldIsRefusedWhenGroundHeightLiesAboveTheFloatLine()   // ground provider returns 0 in the pit
[Fact] public void NarrowDeckBetweenCellCentresIsRefusedByClearance()
// Deck 0.1 m wide in X, centred between two float cell centres 0.25 m apart, so its edges sit 0.075 m from each
// centre. Its underside is 0.49 m above the float feet. The duck capsule (radius 0.2, top 0.5 m above the feet) on
// either centre clears it: the top hemisphere centre at 0.3 m is sqrt(0.075^2 + 0.19^2), about 0.204 m, from the deck
// corner. Neither column captures the deck as a surface, so the headroom filter cannot refuse it. Assert both float
// holds pass, the float edge between the two cells is refused, and SwimClear is false at the edge midpoint, where the
// capsule top reaches 1 cm into the deck.
[Fact] public void PostBesideTheLineRefusesFloatEdges()
[Fact] public void ZeroSwimSpeedRefusesFloatEdges()
[Fact] public void SliceTravelNeverExceedsTheRadius()                 // SwimSpeed 30: every slice's horizontal travel <= 0.2 m
[Fact] public void ShoreEdgesAreProvenBothWays()                     // ramp bed from 0 to -1, wade node to float node and back
[Fact] public void BedGrazeNearTheShoreIsClear()                     // SwimClear true for an upward walkable MTV under StepHeight
```

- [ ] **Step 2: Write the profile tests.**

```csharp
[Fact] public void SteepChannelRoutesAcrossTheFloatLayer()
// A pit whose bed walls are steeper than MaxSlopeRadians. The ground profile's route bank to bank is not Complete.
// The aquatic profile's route is Complete and every interior waypoint's height equals the float height.
[Fact] public void GroundProfileIgnoresWaterEntries()
// Same world captured with and without SampleWater: ground profiles equal by BakeEquivalence.AssertEquivalent.
[Fact] public void AquaticProfileRequiresSampledWater()             // ArgumentException
[Fact] public void AquaticProfileRefusesSwimFractionsOutOfOrder()    // Submersion below Exit, and above Enter
[Fact] public void SubmergedOverhangClampsHeadroomToZero()           // no float node under an overhang between bed and float line
[Fact] public void FloatNodesAreIdentifiedAfterLayering()            // float cells use the swim probe, bed cells the dry probe
[Fact] public void FloatingFeetResolveOntoTheGraph()                 // AllowsSegment true between two floating feet
[Fact] public void SwimFractionMismatchIsRefused()                   // ValidateTuning throws for each of the three fractions
[Fact] public void SwimPaceUsesSwimSpeedWhileSwimming()              // bound equals 2.5 x zone x scale x dt, walk bound on land
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~AquaticProfileTests|FullyQualifiedName~SwimTraversalProbeTests"`. Expected: compile FAIL on `GroundProfileOptions` and the missing internals.
- [ ] **Step 4: Implement** the interfaces.
- [ ] **Step 5: Run GREEN** with the Step 3 filter, then `--filter "FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~GroundTraversalProbeTests|FullyQualifiedName~ProfileAllocationTests"`. Expected: all pass. `BuildProfileStaysWithinOneKibPerColumn` stays green for ground profiles.
- [ ] **Step 6: Commit.** `feat(movement): bake float layers for aquatic profiles`

---

### Task 7: Aquatic profiles in baked sets (#1256, lane C)

**Files:**
- Modify: `KhaozEngine.Movement/NavBakeProfile.cs`, `NavBakeIdentity.cs` (profile entry, decoded profile record and comparison), `GroundNavigationBake.cs:83`, `GroundNavigationBake.Writer.cs`, `GroundNavigationBake.Reader.cs`
- Test: `KhaozEngine.Movement.Tests/NavBakeIdentityTests.cs`, `GroundNavigationBakeRoundTripTests.cs`, `GroundNavigationBakeRefusalTests.cs`, `BakeEquivalence.cs` (also compares `Aquatic` and the derived columns with their float flags)

**Interfaces:**
- Consumes: Task 6 `BuildProfile(tuning, areas, options)`, `AquaticColumns.Derive`, `GroundNavigation.Aquatic`.
- Produces: `public bool NavBakeProfile.Aquatic { get; init; }`, default false.
- Identity: design S2 Bake format identity profiles bullet. Decoded profiles carry `Aquatic`. `ProfilesChanged` detail names `Aquatic`.
- `Create` calls `BuildProfile(profile.Tuning, profile.Areas, new GroundProfileOptions { Aquatic = profile.Aquatic })`, and its refusal for a capture without sampled water reaches the caller. `Load` derives each aquatic profile's columns before building its footprint. Ground profiles keep sharing one column instance.

- [ ] **Step 1: Write the tests.**

```csharp
[Fact] public void IdentityBytesMatchTheGoldenFingerprint()          // existing fact, constant re-recorded once at GREEN
[Fact] public void AquaticFlagChangesTheIdentity()
[Fact] public void AquaticFlagByteOtherThanZeroOrOneIsCorrupt()
[Fact] public void StaleAquaticFlagIsRefusedNamingIt()               // ProfilesChanged with "Aquatic" in Detail
[Fact] public void LoadedAquaticProfileMatchesTheFreshBuild()        // steep channel fixture, BakeEquivalence over both profiles
[Fact] public void RewritingALoadedAquaticBakeReproducesItsBytes()
[Fact] public void GroundProfilesStillShareOneColumnSnapshot()
[Fact] public void AquaticProfileWithoutSampledWaterIsRefusedAtCreate()
```

- [ ] **Step 2: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~NavBakeIdentityTests|FullyQualifiedName~GroundNavigationBake"`. Expected: compile FAIL on `NavBakeProfile.Aquatic`.
- [ ] **Step 3: Implement.** Re-record the golden constant and its inputs in Outcome with the reason: the unreleased v1 identity gained the aquatic flag.
- [ ] **Step 4: Run GREEN** with the Step 2 filter, then the whole Movement.Tests project and `--filter "FullyQualifiedName~TileWorldNavigationBakeTests"` on TileWorld.Physics.Tests. Expected: all pass.
- [ ] **Step 5: Commit.** `feat(movement): store aquatic profiles in baked navigation sets`

---

### Task 8: Swim steering in MoveToRange (#1256, joins lanes A and C)

**Files:**
- Modify: `KhaozEngine.Movement/RouteApproachOptions.cs`, `MoveToRange.cs` (hold rule, bound call, constructor check), `MoveToRange.Approach.cs:24-26`
- Test: `KhaozEngine.Movement.Tests/MoveToRangeSwimTests.cs`, `KhaozEngine.Movement.Tests/NpcSwimNavigationAcceptanceTests.cs`

**Interfaces:**
- Consumes: Task 2 `RouteApproachOptions` and constructors. Task 6 `SwimPace.Bound`, `GroundNavigation.Aquatic`, `GroundProfileOptions`.
- Produces: `public bool RouteApproachOptions.SteerWhileSwimming { get; init; }`, default false.
- Hold, settling, admission and bound rules exactly design S3. With the option false, `Tick` calls `RangeApproachCore.TravelBound` as today, so default behaviour is unchanged. With it true, `Tick` calls `SwimPace.Bound`.
- The `GroundNavigation` constructor throws `ArgumentException` naming `options` when `SteerWhileSwimming` is true and `navigation.Aquatic` is false.

- [ ] **Step 1: Record the base count** with the Task 2 Step 1 filter on the current branch.
- [ ] **Step 2: Write the tests.** Unit facts use scripted states over a flat analytic context with a constant deep medium. The acceptance facts bake the Task 6 steep channel world with an aquatic duck profile and drive `Tick` then `NpcGroundMovement.Step` through the Bepu context with the medium and the matching ground height function.

```csharp
[Fact] public void SwimmingBodyStaysSuspendedWithoutSwimPermission()
[Fact] public void AirborneBodyStaysSuspendedWithSwimPermission()    // Grounded false, Swimming false: Suspended, also when in range
[Fact] public void CommittedBodyStaysSuspendedWithSwimPermission()   // active commitment, grounded or swimming: Suspended before InRange
[Fact] public void SettlingSwimmerIsSuspendedUntilItReachesTheFloatBand()
// Duck tuning: radius 0.2, half height 0.25, step 0.2, so the band is 0.2 m. MoveToRangeTests.Tuning's step of 0.4
// would put the body inside the band. Swimming body 0.3 m below its float line with downward settle velocity: Suspended with zero input, also in range.
// After holds bring the feet within max(StepHeight, 0.001) of the float line, Following.
[Fact] public void PermittedSwimRefusesAnAirborneExitStep()          // prediction airborne and not swimming: zero command
[Fact] public void SwimTravelBoundCapsTheWaypointAtSwimSpeedAboveWalk()
// Swimming body 0.05 m from a waypoint, SwimSpeed 2.5, walk 2: the step lands on the waypoint within 1e-5 m.
[Fact] public void SwimPermissionRequiresAnAquaticProfile()          // ArgumentException
[Fact] public void DefaultOptionsLeaveSwimmingBodiesSuspended()      // regression on the old constructor
[Fact] public void SwimmingDuckCrossesDeepWaterIntoRange()
// Duck starts swimming on one side of the channel, point target on the far bank, range 0.3. Within 600 ticks the
// status is InRange and ReachGeometry.Within holds for the real body.
[Fact] public void SwimmingDuckRoutesAroundALowDeck()
// A deck at the waterline covers half the channel width across the direct line. The duck reaches range and no tick's
// pose fails SwimClear.
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRangeSwimTests|FullyQualifiedName~NpcSwimNavigationAcceptanceTests"`. Expected: compile FAIL on `SteerWhileSwimming`.
- [ ] **Step 4: Implement** the rules.
- [ ] **Step 5: Run GREEN** with the Step 3 filter and the Step 1 filter. Expected: all pass, and the Step 1 filter's count equals the recorded base plus the new cases it matches. Then `--filter "FullyQualifiedName~DirectMoveToRangeTests"` passes unchanged.
- [ ] **Step 6: Commit.** `feat(movement): steer swimming bodies on aquatic routes`

---

### Task 9: Living documentation and full verification

**Files:**
- Modify: `KhaozEngine.Movement/README.md` ("Bounded static physics capture" for `SampleWater` and water entries, "Capsule checked ground profiles" for aquatic profiles and the deepest-contact limit, "Baked profile sets" for the identity fields and water section, "Range steering and movement drivers" for `RouteApproachOptions`, swim steering and carry, "Route-free approach" for the swim limit)
- Modify: `KhaozEngine.Navigation/README.md` ("Following a path" for `ConsumePassedCollinearWaypoints` and `IsCollinearPassThrough`)
- Modify: `docs/USING-KHAOZENGINE.md` ("GroundNavigation contract", "Baked profile sets", "Range steering, NPC stepping and client path commands", "Route-free approach (`DirectMoveToRange`)")
- Modify: `KhaozEngine.Locomotion/README.md` only if Task 4b ran
- Modify: `CHANGELOG.md`, extend the existing `## 20.20.0` entry. No new heading.
- Modify: `docs/INDEX.md` row and the design status line, after verification

- [ ] **Step 1: Add the release note bullets** to the `20.20.0` entry. Keep the first lip bullet when only Task 4's fix ran, the second when Task 4b ran, and neither when the lead did not reproduce.

```markdown
- Aquatic navigation. `PhysicsNavBakeOptions.SampleWater`, default off, makes `PhysicsNavBake.Capture` record one medium
  water surface per column. `BuildProfile(tuning, areas, new GroundProfileOptions { Aquatic = true })` and
  `NavBakeProfile.Aquatic` bake float surfaces at the height a swimming body rests, proven by swim steps through the
  live medium with a static clearance check. `RouteApproachOptions.SteerWhileSwimming` steers a swimming body on such a
  profile. Airborne, committed and still-settling bodies stay `Suspended`. Baked sets gain `SampleWater` in the
  identity and a water section in the payload, so every 20.20.0 bake written before this change must be rebaked.
- `RouteApproachOptions.CarryThroughStraightRuns` keeps full pace along straight route runs and still stops on every
  corner. `PathFollowConfig.ConsumePassedCollinearWaypoints` and `NavPath.IsCollinearPassThrough` back it.
- The ground bake accepts a step onto a low lip that the movement core mounts.
- A capsule now mounts a lip below its step height whose corner sits inside the contact skin. Bake proofs and
  live movement both cross a 2.5 cm deck edge for a 0.3 m capsule.
```

- [ ] **Step 2: Document the limits.** In the Movement README "Route-free approach" section and the matching `docs/USING-KHAOZENGINE.md` section, state that `DirectMoveToRange` does not steer swimmers, because it has no graph guard and a swimmer has no prop collision, so a direct swim approach could pass through props at the waterline (M3). In the Movement README aquatic profile text, state the deepest-contact limit of design S2 Proofs and its effect near the bed (M5). Report to root the follow-up issue to file: title "Swim traversal proof: capsule sweep per slice instead of deepest-contact penetration", labels `kind/backlog` and `confidence/authored`, body citing design S2 Proofs and the 6.4 s creature bake cost.
- [ ] **Step 3: Sweep.** `git grep -n -w -e MoveToRange -e PathFollowConfig -e BuildProfile -e NavBakeProfile -e PhysicsNavBakeOptions -e Swimming -e Suspended -- '*.md'` and correct every stale description. Reread `KhaozEngine.Movement/README.md` end to end.
- [ ] **Step 4: Reconcile.** `git fetch origin && git merge origin/main`. Resolve on this branch. Keep both changelog sections.
- [ ] **Step 5: Full verification, once.**

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

- [ ] **Step 6: Update status** in the design status line and `docs/INDEX.md` to implemented and verified, with no release claimed.
- [ ] **Step 7: Commit.** `docs(movement): document swimming routes and route carry`. The worker stops at its verified commit. The orchestrator merges, pushes and selects the release.

## Outcome

Not started.
