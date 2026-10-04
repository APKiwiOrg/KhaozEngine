# Playtest 1 Engine Round Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Grimhollow straight creature routes, a smooth locally predicted heading, a directional locomotion blend with distance-synced feet and a forgiving tap, all opt-in, plus the #1269 shore fix, on one new engine minor.

**Architecture:** An internal `RouteStraightener` wraps the planner inside `MoveToRange` and drops cell centres only where the full segment guard accepts the centre line and both side lines, with a one-plan raw fallback in the driver. `ClientPrediction` gains a yaw axis beside position and height, read through new `IPredictedState` default members. A GPU-free `DirectionalLocomotionBlend` in `KhaozEngine.Game.Render3D` turns body-frame velocity into weighted clip samples on one shared gait phase, and `AnimationSampler.SampleBlendInto` composes them. `PointerGesture` gains an opt-in straight-line tap tolerance with a time grace.

**Tech Stack:** C# on .NET 10, xUnit, BepuPhysics in Movement tests.

**Spec:** `docs/design/PLAYTEST-1-ENGINE-ROUND-DESIGN-2026-10-04.md`. Read it whole before Task 1. Decisions are cited as D1 to D9, sections as S1 to S5 and rulings as Q1 and Q2.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/playtest1-engine-round`, branch `feature/playtest1-engine-round`, at `811e246bc` on base `ee934bba5`. Merge current `origin/main` into the branch before Task 1 and again in Task 9 Step 3.
- Version: the next free minor with additive public API. `v20.22.0` shipped on main on 2026-10-04 (dot lane telegraphs), so the expected version is 20.23.0. Task 9 re-reads `Directory.Build.props` and the tags first. One bump, its `CHANGELOG.md` entry in the same commit.
- Every opt-in defaults off with byte- or bit-identical behaviour: `RouteApproachOptions.StraightenRoutes` (routes, commands and captures), `PredictionSettings.InterpolateYaw` (rendered state), the `PointerTapTolerance` constructor (gesture phases, taps and drag deltas). The #1269 fix is the only default behaviour change.
- Additive public API only. No wire, `MovementState`, Replication or snapshot change. No project reference change. Netcode keeps its single `KhaozEngine.Netcode.Abstractions` reference.
- Names: `RouteApproachOptions.StraightenRoutes`, `RouteStraightener`, `IPredictedState<TSelf>.HasYaw`, `.Yaw`, `.WithRenderState(Vector2, float, float)`, `PredictionSettings.InterpolateYaw`, `ClipSample`, `AnimationSampler.SampleBlendInto`, `GaitClip`, `DirectionalGaitSet`, `GaitSample`, `DirectionalLocomotionBlend`, `PointerTapTolerance`.
- Steady paths allocate nothing: `DirectionalLocomotionBlend.Advance`, `AnimationSampler.SampleBlendInto`, `ClientPrediction.RenderedState`, `PointerGesture.Advance`. `RouteStraightener` allocates one waypoint list per plan and nothing per tick.
- The strict accept radius `min(supplied, 1e-5 m)` (ruling D1.1) and Suspended before InRange (ruling D0.2) are unchanged with and without the options.
- Zero warnings. No `.filesize-baseline` growth. New behaviour in new types (KESIZE). Test namespaces under `KhaozEngine.Tests.*`. Allocation facts join `[Collection("AllocSensitive")]` and use the project's `AllocAssert.NoPerCallAllocation`.
- No em dashes, en dashes or prose semicolons in Markdown or comments.
- Every dotnet command runs from the worktree root through the shared lock: `/tmp/grimhollow-orch/slot-run.sh "<label>" /tmp/grimhollow-orch/<log> -- <command>`. Run `mkdir -p local-feed` once before the first restore. Labels below are `pt1:t<task>-<step>` and logs `pt1-t<task>-<step>.log`.
- Focused filters per task, one full Release run in Task 9. No local repetition, stress runs or client launches.
- Engine build and test: `dotnet build KhaozEngine.slnx -c Release` and `dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`.
- Guards: `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree`, `bash scripts/check-doc-versions.sh`.
- Each feature task sweeps every Markdown file for the names it adds or the behaviour it changes. On macOS use `git grep -w` or `git grep -P`, never `git grep -E` with `\b`.
- Commit subjects `area(scope): summary`. The release commit uses the version as scope. Stage explicit paths. Workers do not merge, push, pack, tag or file issues unless the task says so.

## Review Focus

1. A side line that lies exactly on a cell boundary resolves to one neighbour only, so an axis-aligned shortcut beside a wall would be refused on one side and admitted on the mirrored side. The plan offsets side lines by `0.499` cells (Task 2 Interfaces). Task 2 `SideLinesJudgeMirroredWallsAlike`.
2. A refused step on a route that is already raw must not reset the follower again, or a body pressed against a live obstacle replans every tick and never holds. Task 3 `RefusedRawStepKeepsTheRoute`.
3. The interpolated heading crosses the plus or minus pi seam, and a consumer comparing or quantising `FacingYaw` expects the `MoveState` range `[-pi, pi)` and the exact predicted bits at the tick end. Task 5 `RenderedYawStaysCanonicalAndLandsExactly`.
4. A consumer sizes the sample span at four because a steady blend has at most four clips, then a W to S crossfade needs more and throws mid-game. `Advance` refuses a short span on every call. Task 7 `AdvanceRefusesASpanShorterThanTheClipCount`.
5. A frame hitch on the release frame of a short click pushes the elapsed time past the grace, and the click becomes a drag or is lost. The release frame decides on the time and distance known before it. Task 8 `HitchOnTheReleaseFrameStillTaps`.

---

### Task 1: Corner partial tick fact (S1, ruled-out lead)

**Files:**
- Create: `KhaozEngine.Movement.Tests/MoveToRangeCornerTickTests.cs`
- Modify: `KhaozEngine.Movement.Tests/MoveToRangeCarryTests.cs:168` (`Surfaces` becomes `internal static`)

**Interfaces:**
- Consumes: `MoveToRangeTests.Tuning` (walk 2 m/s, radius 0.2, half height 0.75), `MoveToRangeTests.Body`, `MoveToRangeCarryTests.Surfaces(float originX, float originZ, Func<int, int, bool>? standable = null)`, `NpcGroundMovement.Step`. The class declares its own `const float Dt = 1f / 30f`.
- Produces: nothing public. The fact pins today's behaviour so straightening cannot hide a stall.

- [ ] **Step 1: Write the fact.**

```csharp
[Fact] public void StaircaseRouteMovesOnEveryTickAtLargeCoordinates()
// Surfaces(146f, 146f), Default options, open guard, walk 2 m/s, Dt = 1/30. Start on the centre of cell (2, 2),
// point target on the centre of cell (26, 14), range 0. Drive Tick then NpcGroundMovement.Step until InRange.
Assert.All(travelBeforeTheInRangeTick, t => Assert.True(t >= 0.019, $"tick moved {t} m"));  // diagonal partial 0.0203 m, straight partial 0.05 m
Assert.Contains(travelBeforeTheInRangeTick, t => t < bound - 1e-3);                           // corners are still landed on
Assert.Equal(RangeMoveStatus.InRange, last);
```

- [ ] **Step 2: Run.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t1-run" /tmp/grimhollow-orch/pt1-t1-run.log -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRangeCornerTickTests"`. Expected: PASS on unchanged source. A failure means the stall lead is live, which contradicts S1. Stop and report the failing tick, its waypoint index and its command.
- [ ] **Step 3: Commit.** `test(movement): pin that a staircase route moves on every tick`

---

### Task 2: `RouteStraightener` and its rule (D1, D2, S1)

**Files:**
- Create: `KhaozEngine.Movement/RouteStraightener.cs`
- Test: `KhaozEngine.Movement.Tests/RouteStraightenerTests.cs`

**Interfaces:**
- Consumes: `IRegionPathPlanner` (both `FindPath` overloads), `NavSpace.Layers[layer].SurfaceHeightAt(int, int)`, `NavGrid.CellOf`, `NavGrid.CellSize`, `NavWaypoint.Kind`, `NavPath(NavPathStatus, IReadOnlyList<NavWaypoint>)`.
- Produces: `internal sealed class RouteStraightener : IRegionPathPlanner` with `RouteStraightener(IRegionPathPlanner inner, NavSpace space, Func<Vector3, Vector3, bool> allowsSegment)`, null arguments throw `ArgumentNullException`.
- Produces: `internal const float SideOffsetCells = 0.499f`. A side line is the centre line moved `SideOffsetCells x CellSize` of the anchor's layer along the unit perpendicular, computed in double. Just under half a cell keeps a side line off the cell boundary (Review Focus 1), so an axis-aligned segment judges both sides alike and an oblique one still enters both neighbour rows where the body can.
- Produces: `internal NavPath? LastStraightened { get; }`, the path most recently returned that dropped at least one waypoint, else null. A plan that drops nothing returns the inner path itself.
- Produces: `internal void FallBackOnce()`. The next `FindPath` returns the inner path unchanged, clears `LastStraightened`, and the plan after it straightens again (D3).
- Rule exactly as S1. `Unreachable` returns the inner path by reference. `Complete` and `Partial` keep their status. Every kept waypoint is the inner waypoint itself (position, layer, kind). Feet Y of waypoint `i` is `space.Layers[wp.Layer].SurfaceHeightAt(cellOf(wp))`, and the start anchor's Y is the query start's Y. Mandatory: the final waypoint, every `Hop` waypoint and its predecessor, and both ends of a layer change.

The scan, because the signature and tests leave its shape open:

```csharp
// a is the anchor's index (-1 = the query start feet). The next waypoint is a raw planner edge and is kept unchecked.
for (int a = -1; a < n - 1;)
{
    int best = a + 1;
    for (int j = a + 2; j < n && !Mandatory(best); j++)
    {
        if (wp[j].Kind != NavWaypointKind.Walk || wp[j].Layer != anchorLayer) break;
        if (!Clear(anchorFeet, Feet(j))) break;   // centre line, then +side, then -side
        best = j;
    }
    kept.Add(wp[best]);
    a = best; anchorFeet = Feet(best); anchorLayer = wp[best].Layer;
}
```

Fixtures: `MoveToRangeCarryTests.Surfaces` for grids, `MoveToRangeTests.ScriptPlanner` and `.Route` for scripted paths, and a test-local `Sampled(standable, originX, originZ)` guard that refuses a segment when any point at 1 mm spacing along it lies in an unstandable cell and counts its calls. Queries use `MoveToRangeTests.Tuning.CapsuleRadius` and `PathQueryBudget.Default`. Cells below are `(x, z)` grid indices, and "centre of" means the cell centre feet.

- [ ] **Step 1: Write the rule tests.**

```csharp
[Theory] public void OpenFieldCollapsesToOneSegment(float origin)               // -4, 128, 184 (coordinates up to 192 m)
// Start centre of (2, 2), goal centre of (26, 14), GridPathPlanner over Surfaces(origin, origin): raw has at least 13
// waypoints, the straightened path is exactly [raw.Waypoints[^1]] with status Complete.
[Fact] public void ConvexObstacleKeepsItsCorner()
// Unstandable block x 10..13, z 5..11, start (4, 8), goal (20, 8): 2 or 3 kept, an ordered subsequence of raw, last
// is the goal, every kept interior waypoint is a raw bend within two cells of the block (the radius pads one), and
// every kept segment passes Sampled.
[Fact] public void AreaMaskBlocksAShortcut()
// Flat bake as in MoveToRangeAreaTests whose classifier marks the column band 0 <= x < 0.5 as area 1 except a gap at
// z > 0.5, profile BuildProfile(tuning, new NavAreaFilter(0u, 1u)), straightener over nav.Planner and
// nav.AllowsSegment: from x -0.625 to x 1.125 at z -0.625 no kept segment's centre line crosses the masked band.
[Fact] public void SideLineRefusalBesideAWall()
// Start (2, 2), goal (10, 6), one unstandable cell: the first cell the -side line enters that the centre line does
// not, found by the test with the same 1 mm sampling. Control: Sampled(centre line start->goal) is true. Kept count > 1.
[Fact] public void SideLinesJudgeMirroredWallsAlike()                            // Review Focus 1
// Raw run along row z 8 from x 2 to x 12 with an unstandable row at z 9, then the mirror with it at z 7: equal kept
// counts (both 1). Same for an oblique start (2, 2) goal (12, 7) and its Z mirror with mirrored walls.
[Fact] public void MandatoryWaypointsAreKept()
// Two FromSurfaces layers at heights 0 and 2 over origin (-1, -1). Script route: (0,0),(0.25,0),(0.5,0) on layer 0,
// (0.75,0),(1,0),(1.25,0) on layer 1, Hop (2,0) on layer 1, (2.25,0),(2.5,0) on layer 1. Open guard. Kept is exactly
// [w2, w3, w5, w6, w8] with layers and kinds preserved.
[Fact] public void PartialStatusIsKept()
[Fact] public void UnreachablePassesThroughByReference()                          // Assert.Same(NavPath.Unreachable, ...)
[Fact] public void PointQueriesAreStraightenedToo()                               // IPathPlanner overload, open field as above
[Fact] public void FallBackOnceReturnsTheRawRouteForOnePlan()
// FallBackOnce, plan: Assert.Same(raw, first) and LastStraightened is null. Plan again: one waypoint, LastStraightened
// is that path.
[Fact] public void UnchangedRouteIsReturnedByReference()                          // straight 3-cell raw route beside walls on both sides
[Fact] public void ScanCostIsBounded()
// Open field at origin 146: Sampled call count <= 3 x (raw count + kept count).
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t2-red" /tmp/grimhollow-orch/pt1-t2-red.log -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~RouteStraightenerTests"`. Expected: compile FAIL, CS0246 on `RouteStraightener`.
- [ ] **Step 3: Implement** `RouteStraightener` per Interfaces. All segment geometry in double.
- [ ] **Step 4: Run GREEN** with the Step 2 filter. Expected: all pass, nonzero count.
- [ ] **Step 5: Commit.** `feat(movement): straighten cell routes where the segment guard allows`

---

### Task 3: `StraightenRoutes` and the D3 fallback in `MoveToRange`

**Files:**
- Modify: `KhaozEngine.Movement/RouteApproachOptions.cs` (adds the property), `KhaozEngine.Movement/MoveToRange.cs:13-17` (field), `:45-56` (constructor), `:100-101` (refusal)
- Create: `KhaozEngine.Movement/MoveToRange.Straighten.cs`
- Modify docs: `KhaozEngine.Movement/README.md` (route options section), `KhaozEngine.Navigation/README.md` (region planner returns unsmoothed routes, straightening lives in Movement), `KhaozEngine.Movement/GroundNavigation.cs:37` (`Planner` summary names the opt-in), `docs/USING-KHAOZENGINE.md` "Body reach and physics-ground profiles"
- Test: `KhaozEngine.Movement.Tests/MoveToRangeStraightenTests.cs`

**Interfaces:**
- Consumes: Task 2 `RouteStraightener`, `.LastStraightened`, `.FallBackOnce()`.
- Produces: `public bool RouteApproachOptions.StraightenRoutes { get; init; }`, default false, documented with the S1 rule, the Q2 cost and the D3 fallback.
- The five-argument constructor builds `_straightener = new RouteStraightener(planner, space, allowsSegment)` when the option is set and hands the follower the straightener instead of `planner`, so the `GroundNavigation` constructor gets it too. `private readonly RouteStraightener? _straightener`.
- Produces: `private void FallBackToRawRoute()` in `MoveToRange.Straighten.cs`. When `_straightener?.LastStraightened` is `ReferenceEquals` to `_follower.ActivePath`, it calls `FallBackOnce()` then `_follower.Reset()`. Otherwise it does nothing. The refusal at `MoveToRange.cs:101` calls it before `Hold(RangeMoveStatus.Following)`.

- [ ] **Step 1: Write the driver tests.** Reuse `MoveToRangeCarryTests`' drive loop shape (`Tick` then `NpcGroundMovement.Step`) and its `RecordingPlanner`, which sits under the straightener and records raw plans.

```csharp
[Fact] public void DefaultOptionsLeaveCommandsUnchanged()
// Staircase route of Task 1 for 120 ticks: identical RangeSteering bits and plan count from the old constructor and
// from new RouteApproachOptions { StraightenRoutes = false }.
[Fact] public void OffAxisWalkTurnsOnlyAtKeptBends()
// Task 1 fixture with StraightenRoutes. Expected bends come from a RouteStraightener over the same planner. The command
// direction changes by more than 1e-4 rad only on ticks that start with the body's XZ exactly on a kept bend or on the
// final approach, every other tick travels the full bound within 1e-4 m, and InRange arrives within
// ceil(straight distance / bound) + 2 ticks.
[Fact] public void HeadingChangesOnceAroundAConvexObstacle()                       // Task 2 obstacle fixture, one kept corner
[Fact] public void RefusedStraightStepFallsBackForOnePlanAndArrives()
// Sampled-style guard, armed after the first plan, refuses any segment ending in a cell that no raw plan the recorder
// has seen visits. The straight line leaves the first raw route's cells within a few ticks: that step is refused with
// Hold(Following), the recorder sees exactly one extra plan, the body then lands on every waypoint of that raw plan in
// order and ends InRange.
[Fact] public void StraighteningResumesOnThePlanAfterTheFallback()
// Continue the test above with a moved target: the next plan is straightened (LastStraightened not null).
[Fact] public void RefusedRawStepKeepsTheRoute()                                   // Review Focus 2
// Guard refuses every step after the first plan. Over 30 ticks: one fallback, then the plan count stays at two and
// every status is Following with zero direction.
[Fact] public void CarryAndStraighteningArriveTogether()
// CarryStopsAtAMandatoryCorner's L route with both options: the body lands exactly on the kept corner, every other
// tick travels the full bound within 1e-4 m, InRange.
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t3-red" /tmp/grimhollow-orch/pt1-t3-red.log -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRangeStraightenTests"`. Expected: compile FAIL, CS0117 on `StraightenRoutes`.
- [ ] **Step 3: Implement** per Interfaces, then the docs listed under Files and the Markdown sweep for `StraightenRoutes`, `unsmoothed` and `cell-center routes`.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~MoveToRange|FullyQualifiedName~RouteStraightener|FullyQualifiedName~NpcRangeNavigation|FullyQualifiedName~PlayerPathMovement|FullyQualifiedName~NpcSwimNavigation` on Movement.Tests. Expected: all pass, nonzero counts, `FastScaledApproachCapsTheWaypointAndRangeTravel` and every carry and swim fact unchanged.
- [ ] **Step 5: Commit.** `feat(movement): opt-in straight routes with a raw fallback in MoveToRange`

---

### Task 4: #1269, a grounded step past the swim-enter line (D9)

**Files:**
- Modify: `KhaozEngine.Movement/DirectMoveToRange.cs:24` (admission delegate), `:43-53` (Tick stores the context), `:108-109` (`AllowsStep`)
- Modify docs: `KhaozEngine.Movement/README.md` route-free approach paragraph ("a walk into deep water ends Blocked" now holds on a sloped shore)
- Test: `KhaozEngine.Movement.Tests/DirectMoveToRangeDropTests.cs`

**Interfaces:**
- Consumes: `CharacterMovement.ResolveSwimming(bool wasSwimming, in MovementMedium medium, float feetY, in MoveTuning tuning)`, `GroundMoveContext.Medium` (absolute XZ and feet Y).
- `AllowsStep` becomes an instance method that also refuses when `context.Medium` is set and `ResolveSwimming(predicted.Swimming, medium(x, z, feetY), feetY, tuning)` is true at the predicted feet. The context comes from a `GroundMoveContext? _stepContext` field set at the top of `Tick`, and the static `Admits` becomes `private readonly StepAdmission _admits` bound once in the constructor, so steady ticks still allocate nothing.

- [ ] **Step 1: Write the fact.**

```csharp
[Fact] public void GroundedStepPastTheSwimEnterLineEndsBlocked()
// Height(x, z) = -0.2f * x, medium Water (surface 0.5), start feet at x 0 grounded, target ReachTarget.Point(new(6f, -0.45f, 0f)),
// Strict options, the class's Approach helper.
Assert.Equal(RangeMoveStatus.Blocked, walk.Last);
Assert.Equal(0, walk.SuspendedTicks);
Assert.False(walk.Body.Swimming);
Assert.True(0.5f - (walk.Body.Position.Y - 0.75f) < 0.975f);   // feet stay above the 0.975 m swim entry
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t4-red" /tmp/grimhollow-orch/pt1-t4-red.log -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~GroundedStepPastTheSwimEnterLineEndsBlocked"`. Expected: FAIL with `Last` Suspended.
- [ ] **Step 3: Implement** per Interfaces and update the README line.
- [ ] **Step 4: Run GREEN.** `FullyQualifiedName~DirectMoveToRange` on Movement.Tests, which includes the class's allocation fact. Expected: all pass.
- [ ] **Step 5: Commit.** `fix(movement): refuse a direct step that ends past the swim-enter line` with `Closes #1269` in the body.

---

### Task 5: Local heading interpolation (D4, S2, Q1)

**Files:**
- Modify: `KhaozEngine.Netcode/IPredictedState.cs:87-92` (three default members), `KhaozEngine.Netcode/PredictionSettings.cs:22-39` (init property), `KhaozEngine.Netcode/ClientPrediction.cs:52-53` (field), `:138-147` (`RenderedState`), `:162`, `:226`, `:277`, `:377` (the four collapse sites)
- Modify: `KhaozEngine.NetWorld/PlayerMoveState.cs:84-92` (yaw members)
- Modify docs: `KhaozEngine.Netcode/README.md`, `KhaozEngine.NetWorld/README.md` (local `EntityRenderState.FacingYaw`), `docs/USING-KHAOZENGINE.md` "Networked overworld"
- Test: `KhaozEngine.Server.Tests/Netcode/ClientPredictionYawTests.cs`, `KhaozEngine.Server.Tests/NetWorld/WorldClientLocalYawTests.cs`

**Interfaces:**
- Produces on `IPredictedState<TSelf>`: `bool HasYaw => false`, `float Yaw => 0f`, `TSelf WithRenderState(Vector2 position, float vertical, float yaw) => WithRenderState(position, vertical)`.
- Produces: `public bool PredictionSettings.InterpolateYaw { get; init; }`, default false. The positional record keeps its shape.
- `PlayerMoveState` implements the three explicitly over `Move.FacingYaw`. The three-argument `WithRenderState` sets position and `FacingYaw` and preserves the rest as the two-argument one does.
- `ClientPrediction`: `private float previousPredictedYaw`, set to the state's `Yaw` at Reset, Reseed, Predict and the hard-snap branch (which an epoch advance also takes). The non-snap branch leaves it alone, so the inter-tick phase holds and only the target moves (S2). `RenderedState` calls the three-argument wither only when `settings.InterpolateYaw && predictedState.HasYaw`, with `frac >= 1 ? current : WrapPi(previous + WrapPi(current - previous) * frac)`. `private static float WrapPi(float radians)` returns `[-pi, pi)`, the `MoveState.FacingYaw` range, since Netcode cannot reference Locomotion.

- [ ] **Step 1: Write the prediction tests.** Fake `YawState(Vector2 Position, float Heading, uint Epoch)` with `HasYaw` true and the three-argument wither, command `YawCmd(Vector2 Velocity, float Heading)` whose step sets the heading. `Tick = 1f / 30f`.

```csharp
[Fact] public void YawInterpolationIsOffByDefault()
// Predict 0 -> 1.0, AdvancePresentation(Tick / 2): rendered Heading is bitwise 1.0f, and rendered Position bits equal
// those of an InterpolateYaw = true predictor fed the same commands.
[Fact] public void HalfTickRendersHalfTheTurn()                    // 0 -> 0.6, half tick: 0.3 within 1e-6
[Fact] public void SeamTurnsTheShortWayRound()
// 3.0 -> -3.0, half tick: WrapPi(rendered - 3.0) is 0.14159 within 1e-5 (total turn 2 pi - 6 = 0.2832 rad).
[Fact] public void RenderedYawStaysCanonicalAndLandsExactly()      // Review Focus 3
// Across the seam at frac 0.25, 0.5, 0.75 rendered is in [-pi, pi). At a full tick it is bitwise -3.0f.
[Theory] public void CollapseSitesSnapThePreviousYaw(string site)  // "reset", "reseed", "hardsnap", "epoch"
// Predict to 1.0, AdvancePresentation(Tick / 2), then trigger the site with a basis at heading 2.0, acknowledging
// every command so nothing replays: rendered is bitwise 2.0f at once and after another AdvancePresentation(Tick / 2).
[Fact] public void NonSnapReconcileKeepsThePhase()
// Predict 0 -> 0.6, AdvancePresentation(Tick / 4) renders 0.15. Reconcile a matching basis acknowledging every command:
// still 0.15. Reconcile a basis at 0.8 within HardSnapDistance: 0.2 within 1e-6.
```

- [ ] **Step 2: Write the NetWorld tests.** Loopback fixture as `WorldClientLocalMovementTests.Connect`, client config `Prediction = PredictionSettings.Default with { TickSeconds = 1f / 30f, InterpolateYaw = true }`, `MoveTuning.Default` (infinite turn speed, so facing reaches the camera yaw in one tick).

```csharp
[Fact] public void PlayerMoveStateCarriesYawThroughRenderState()   // HasYaw true, Yaw == Move.FacingYaw, wither sets both
[Fact] public void LocalEntityFacingYawIsInterpolated()
// Joined and settled at facing 0 (asserted first), then SendInput(new MoveCommand(Vector2.Zero, false, 0.6f,
// faceCamera: true)) and AdvancePresentation(1f / 60f):
// Snapshot().Single(e => e.IsLocal).FacingYaw is 0.3 within 1e-5. With InterpolateYaw false it is bitwise 0.6f.
```

- [ ] **Step 3: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t5-red" /tmp/grimhollow-orch/pt1-t5-red.log -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~ClientPredictionYawTests|FullyQualifiedName~WorldClientLocalYawTests"`. Expected: compile FAIL, CS0117 on `InterpolateYaw`.
- [ ] **Step 4: Implement** per Interfaces, then the docs and the sweep for `WithRenderState`, `FacingYaw` and `InterpolateYaw`.
- [ ] **Step 5: Run GREEN.** The Step 3 filter, then `FullyQualifiedName~KhaozEngine.Tests.Netcode|FullyQualifiedName~KhaozEngine.Tests.NetWorld` on Server.Tests and `FullyQualifiedName~ReconcileParity|FullyQualifiedName~StepOffset|FullyQualifiedName~ReplicatedCharacterAnimators` on Game.Tests. Expected: all pass, nonzero counts.
- [ ] **Step 6: Commit.** `feat(netcode): opt-in inter-tick interpolation of the predicted heading`

---

### Task 6: `ClipSample` and `AnimationSampler.SampleBlendInto` (S3 pose helper)

**Files:**
- Create: `KhaozEngine.Render3D/Animation/ClipSample.cs`
- Modify: `KhaozEngine.Render3D/Animation/AnimationSampler.cs` (after `SampleInto`, line 44)
- Modify docs: `KhaozEngine.Render3D/README.md`
- Test: `KhaozEngine.Render.Tests/Render3D/Animation/AnimationSamplerBlendTests.cs`

**Interfaces:**
- Produces: `public readonly record struct ClipSample(AnimationClip Clip, float Time, float Weight)`, namespace `KhaozEngine.Render3D`. `Time` is clip seconds.
- Produces: `public static void SampleBlendInto(Skeleton skel, ReadOnlySpan<ClipSample> samples, JointPose[] into, JointPose[] scratch)`. Both arrays have `skel.NodeCount` entries and are distinct, else `ArgumentException`. A null clip on a positive weight, or a negative or nonfinite weight, throws. Zero-weight samples are skipped.
- Running normalised lerp, which keeps the one-sample case bit-identical:

```csharp
float total = 0f;
foreach (ClipSample s in samples)
{
    if (s.Weight == 0f) continue;
    if (total == 0f) { SampleInto(s.Clip, skel, s.Time, into); total = s.Weight; continue; }
    SampleInto(s.Clip, skel, s.Time, scratch);
    total += s.Weight;
    for (int n = 0; n < into.Length; n++) into[n] = JointPose.Lerp(into[n], scratch[n], s.Weight / total);
}
// total == 0 leaves into untouched.
```

- [ ] **Step 1: Write the tests.** Two-node skeleton and two single-track clips built as in `AnimationSamplerTests.Chain2`.

```csharp
[Fact] public void OneFullWeightSampleIsBitIdenticalToSampleInto()     // Assert.Equal per JointPose field bits
[Fact] public void TwoSamplesEqualJointPoseLerp()
// Weights 0.3 and 0.9: every node equals JointPose.Lerp(a[n], b[n], 0.9f / 1.2f) exactly.
[Fact] public void ZeroTotalWeightLeavesTheBase()                        // into pre-filled with a marker pose, unchanged
[Fact] public void ZeroWeightSamplesAreSkipped()                         // [0, a@1, 0] equals SampleInto(a) bitwise
[Fact] public void InvalidBuffersAndWeightsAreRefused()                  // wrong length, into == scratch, -1, NaN, null clip
```

Allocation class `AnimationSamplerBlendAllocationTests` in `[Collection("AllocSensitive")]`: `SampleBlendIntoAllocatesNothing` runs a two-sample blend through `AllocAssert.NoPerCallAllocation`.

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t6-red" /tmp/grimhollow-orch/pt1-t6-red.log -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AnimationSamplerBlend"`. Expected: compile FAIL, CS0246 on `ClipSample`.
- [ ] **Step 3: Implement** per Interfaces and the README entry.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~KhaozEngine.Tests.Render3D.Animation`. Expected: all pass.
- [ ] **Step 5: Commit.** `feat(render3d): sample a weighted set of clips into one pose`

---

### Task 7: `GaitClip`, `DirectionalGaitSet` and `DirectionalLocomotionBlend` (D6, D7, D8, S3)

**Files:**
- Create: `KhaozEngine.Game.Render3D/GaitClip.cs` (with `GaitSample`), `KhaozEngine.Game.Render3D/DirectionalGaitSet.cs`, `KhaozEngine.Game.Render3D/DirectionalLocomotionBlend.cs`
- Modify docs: `KhaozEngine.Game.Render3D/README.md`, `docs/USING-KHAOZENGINE.md` "Animated characters" (consumer loop with `SampleBlendInto`, `TravelWeight` as the locomotion layer weight, keep advancing while airborne)
- Test: `KhaozEngine.Game.Tests/Game/DirectionalLocomotionBlendTests.cs`, `KhaozEngine.Game.Tests/Game/DirectionalLocomotionBlendAllocationTests.cs`

**Interfaces:**
- Produces, namespace `KhaozEngine.Game`, exactly the S3 signatures: `GaitClip(int ClipId, float FullWeightSpeed, float StrideMetres, float SyncPhase)`, `GaitSample(int ClipId, float Phase, float Weight)`, `DirectionalGaitSet(ReadOnlySpan<GaitClip> forward, backward, left, right)` with `ClipCount`, and `DirectionalLocomotionBlend(DirectionalGaitSet gaits, float blendSeconds = 0.15f, float movingSpeed = 0.05f)` with `static Vector2 BodyFrame(Vector3 worldVelocity, float facingYaw)`, `Phase`, `TravelWeight`, `int Advance(Vector2 bodyVelocity, float dt, Span<GaitSample> samples)`, `Reset()`.
- `DirectionalGaitSet` copies the spans into one slot list in the order forward, backward, left, right. Each family is non-empty, `FullWeightSpeed` finite, positive and strictly increasing, `StrideMetres` finite and positive, `SyncPhase` finite in `[0, 1)`, else `ArgumentException` naming the family. Duplicate clip ids are allowed (slots, not ids, carry weights).
- Blend arguments: `blendSeconds` finite and positive, `movingSpeed` finite and not negative. `Advance`: `dt` finite and not negative, `bodyVelocity` finite, `samples.Length >= gaits.ClipCount` on every call, else `ArgumentOutOfRangeException` or `ArgumentException`.
- `BodyFrame` uses the `MoveCommand.CameraYaw` basis: right `(cos y, -sin y)`, forward `(-sin y, -cos y)` in world XZ, so `X = v.X cos y - v.Z sin y` and `Y = -v.X sin y - v.Z cos y`.

Weights and phase, because D7 and D8 fix the rule but not its arithmetic:

```csharp
float speed = bodyVelocity.Length();
bool moving = speed >= _movingSpeed;
if (moving)
{
    // D7: angle clockwise from forward in degrees, sectors F(0) R(90) B(180) L(270).
    double a = Math.Atan2(bodyVelocity.X, bodyVelocity.Y) * (180d / Math.PI);
    if (a < 0d) a += 360d;
    int sector = Math.Min(3, (int)(a / 90d));
    double far = (a - sector * 90d) / 90d;
    // Target(near) = 1 - far, Target(next) = far, others 0. Within a family the bracketing members split
    // linearly by FullWeightSpeed, clamped to the slowest and fastest. Target(slot) = direction x member.
}
// Below movingSpeed the targets keep their last values.
float step = dt / _blendSeconds;
// Every slot weight and TravelWeight (target 1 when moving, else 0) moves toward its target by at most step.
// D8: total = sum of weights. When total > 0:
//   stride = sum((w / total) x StrideMetres), Phase = Frac(Phase + speed x dt / stride)
//   each slot with w > 0 writes GaitSample(ClipId, Frac(Phase + SyncPhase), w / total), in slot order.
// Frac(x) = x - floor(x), and a result of 1 becomes 0. Returns the number written.
```

Test set (six clips): forward walk 1.4 m/s stride 1.2 sync 0.25 and run 4.0 stride 2.4 sync 0.25, backward walk 1.0 stride 0.9 sync 0.5, left walk 1.2 stride 0.8 sync 0.6, right walk 1.2 stride 0.8 sync 0.1 and run 3.0 stride 1.8 sync 0.1. "Settled" means ten `Advance(v, 1f / 60f)` calls at the same velocity from `Reset`. Timing tests use `dt = 0.025f` so `blendSeconds / 2` is three calls.

- [ ] **Step 1: Write the weight tests.**

```csharp
[Theory] public void EachCardinalIsOneClip(float x, float y)        // (0,1.4),(0,-1),(-1.2,0),(1.2,0) settled: one sample, weight 1
[Theory] public void DiagonalsSplitHalfAndHalf(float x, float y)    // (±1,±1)/sqrt2 x 1.2 settled: two samples, 0.5 each within 1e-6
[Fact] public void WeightsAreContinuousAroundTheCircle()
// Settled targets sampled every 0.5 degree over 360 at 1.2 m/s, including both sides of 180: no slot changes by more
// than 0.5 / 90 + 1e-6 between neighbours.
[Fact] public void SpeedBracketsAndClamps()
// Forward settled at 0.7: walk only. At 2.7: walk 0.5, run 0.5 within 1e-6. At 6.0: run only.
[Fact] public void WeightsSumToOneAndSteadyStateHasAtMostFour()     // forward-right at 2.7 settled: exactly 4 samples, sum 1 within 1e-6
[Fact] public void AdvanceRefusesASpanShorterThanTheClipCount()      // Review Focus 4: a span of 4 with 6 clips throws on the first call, even standing still
```

- [ ] **Step 2: Write the phase, timing and frame tests.**

```csharp
[Fact] public void PhaseAdvancesByDistanceOverBlendedStride()
// Settled diagonal forward-right at 1.0 m/s (forward stride 1.2, right 0.8, blended 1.0), then 15 Advance(1/60):
// Phase - p0 is 0.25 within 1e-5 (mod 1).
[Fact] public void PureStrafeReproducesItsOwnStride()                // right at 0.8 m/s for 0.5 s settled: phase advance 0.5 within 1e-5
[Fact] public void SyncPhasesKeepContactsAligned()
// Settled diagonal: for both samples Frac(sample.Phase - SyncPhase) equals blend.Phase within 1e-6.
[Fact] public void ZeroTravelHoldsThePhase()                         // Advance(Vector2.Zero, 1/60) x 10: Phase unchanged
[Fact] public void SplitDtIsInvariantWhenSettled()                   // Advance(1/30) vs two Advance(1/60): Phase and weights within 1e-6
[Fact] public void ReversalCrossfadesForBlendSeconds()
// Settled forward walk, then (0,-1): after blendSeconds / 2 forward and backward are 0.5 each within 1e-5, after
// blendSeconds backward only.
[Fact] public void StopFadesOutOfTheLastGait()
// Settled forward walk, then zero velocity: TravelWeight is 0.5 after blendSeconds / 2 and 0 after blendSeconds, and the
// samples still name the forward walk with weight 1.
[Theory] public void BodyFrameUsesTheCameraYawConvention(...)
// yaw 0, v (0,0,-1) -> (0,1). yaw 0, v (1,0,0) -> (1,0). yaw pi/2, v (-1,0,0) -> (0,1). yaw pi/2, v (0,0,-1) -> (1,0). Within 1e-6.
[Fact] public void InvalidSetsAndArgumentsAreRefused()               // empty family, unsorted speeds, sync 1, stride 0, blendSeconds 0, NaN dt
```

Allocation class in `[Collection("AllocSensitive")]`: `AdvanceAllocatesNothing` runs a settled diagonal and a reversal through `AllocAssert.NoPerCallAllocation`.

- [ ] **Step 3: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t7-red" /tmp/grimhollow-orch/pt1-t7-red.log -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~DirectionalLocomotionBlend"`. Expected: compile FAIL, CS0246 on `DirectionalGaitSet`.
- [ ] **Step 4: Implement** per Interfaces and the docs, then sweep for `ContinuousLocomotion`, `directional blend` and `LocomotionSpeedSync` mentions that the blend changes.
- [ ] **Step 5: Run GREEN.** The Step 3 filter, then `FullyQualifiedName~Locomotion` on Game.Tests. Expected: all pass.
- [ ] **Step 6: Commit.** `feat(game): directional locomotion blend with distance-synced gait phase`

---

### Task 8: #1272, an opt-in tap tolerance on `PointerGesture` (D9)

**Files:**
- Create: `KhaozEngine.Windowing/PointerTapTolerance.cs`
- Modify: `KhaozEngine.Windowing/PointerGesture.cs:45-58` (constructor and property), `:87-157` (`Advance`)
- Modify docs: `KhaozEngine.Windowing/README.md`, `docs/USING-KHAOZENGINE.md:1086` "Tap or drag"
- Test: `KhaozEngine.Render.Tests/Windowing/PointerGestureTapToleranceTests.cs`

**Interfaces:**
- Produces: `public sealed record PointerTapTolerance(float DistancePoints, float GraceSeconds, float GraceDistancePoints)` with `public float CatchUpLimitPoints { get; init; } = float.PositiveInfinity`. Valid when `DistancePoints` is finite and positive, `GraceSeconds` finite and not negative, `GraceDistancePoints` finite and at least `DistancePoints`, and `CatchUpLimitPoints` not negative and not NaN.
- Produces: `public PointerGesture(MouseButton button, PointerTapTolerance tapTolerance) : this(button, tapTolerance.DistancePoints)`, validating as above with `ArgumentException`, and `public PointerTapTolerance? TapTolerance { get; }`, null for the existing constructor.
- Produces: `public void Advance(in InputState input, bool uiBlocked, float elapsedSeconds)`. `elapsedSeconds` must be finite and not negative. Without a tolerance it ignores `elapsedSeconds` and behaves exactly as the two-argument overload. With one, the two-argument overload throws `InvalidOperationException`, because the grace needs time.
- Tolerance rule. Blocking, suppression and the release edge are unchanged. The press frame starts the clock at 0, and each later held frame adds its `elapsedSeconds` before it is judged. A release is judged on the state before its own frame, so the release frame's elapsed time never decides it. While pending, `distance = |_pendingDelta|` (straight line from the press, not path length) and the limit is `GraceDistancePoints` while the clock is below `GraceSeconds`, else `DistancePoints`. The press crosses when `distance > limit`. On the crossing frame `DragDelta = _pendingDelta`, clamped to length `CatchUpLimitPoints` when the frame is the first one at or past the grace (the grace decided it). Zero drops the catch-up.

- [ ] **Step 1: Write the tests.** Helpers as `PointerGestureTests`, with a frame time argument. Tolerance `new PointerTapTolerance(4f, 0.25f, 8f)`. Frames are `1f / 32f` s unless stated, exact in binary, so the clock reaches the 0.25 s grace on frame 8 (the press frame is frame 0) without rounding.

```csharp
[Fact] public void DefaultGestureIsUnchanged()
// Four scripted paths (still click, 3 right then 2 back wobble, 5 point drag, press while blocked) through a default
// gesture's two- and three-argument overloads side by side: identical Phase, TapThisFrame, TapPosition and DragDelta
// on every frame.
[Fact] public void WobbleEndingNearThePressIsATap()
// Frames of 0.1 s: +3, -3, +3, -3, +3, -3, +2 points (20 points of path, never more than 3 from the press), then
// release: tap, TapPosition is the press point, DragDelta zero every frame.
[Fact] public void ShortMoveReleasedWithinTheGraceIsATap()   // 2 points on frames 0 to 2 (6 points), release on frame 3, inside 150 ms
[Fact] public void SlowDragCrossesWhenTheGraceEnds()
// 0.875 points per frame for 18 frames (15.75 points): pending through frame 7 (7 points, under the grace limit of
// 8), Dragging on frame 8, the first frame at the grace, never a tap.
[Fact] public void NothingReachesTheConsumerBeforeTheCrossing()  // DragDelta is zero on every pending frame of the cases above
[Fact] public void GraceDecidedCatchUpIsCapped()
// Slow drag with CatchUpLimitPoints = 2: the crossing frame's DragDelta is (2, 0) within 1e-6. With 0: zero on the
// crossing frame, and the next frame passes its own 0.875 point delta.
[Fact] public void FastMoveCrossesInsideTheGraceWithFullCatchUp()  // 3 points on frames 0 to 2: Dragging on frame 2, DragDelta is (9, 0)
[Fact] public void HitchOnTheReleaseFrameStillTaps()               // Review Focus 5: 2 points on frames 0 to 2, release frame elapsed 0.3 s: tap
[Fact] public void TwoArgumentAdvanceThrowsWithATolerance()
[Theory] public void InvalidTolerancesAreRefused(...)             // 0 or NaN distance, negative grace, grace distance below distance, negative cap
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "pt1:t8-red" /tmp/grimhollow-orch/pt1-t8-red.log -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointerGestureTapToleranceTests"`. Expected: compile FAIL, CS0246 on `PointerTapTolerance`.
- [ ] **Step 3: Implement** per Interfaces and the docs. The "Tap or drag" section keeps the strict path rule as the default and adds the tolerance with its numbers.
- [ ] **Step 4: Run GREEN.** `FullyQualifiedName~PointerGesture` on Render.Tests. Expected: all pass, including both existing gesture classes unchanged.
- [ ] **Step 5: Commit.** `feat(windowing): opt-in tap tolerance from the press point with a time grace` with `Closes #1272` in the body.

---

### Task 9: Round docs, D5 follow-up, version and full verification

**Files:**
- Modify: `docs/INDEX.md:47` (status), the design's status line, `Directory.Build.props:25`, `CHANGELOG.md`, every declaration `scripts/check-doc-versions.sh` names, this plan's Outcome
- Root files the D5 issue

**Interfaces:**
- Consumes: every public name in Global Constraints.

- [ ] **Step 1: Sweep.** `git grep -w` each new name and `StraightenRoutes`, `InterpolateYaw`, `SampleBlendInto`, `PointerTapTolerance` across all Markdown, package READMEs and `AGENTS.md`. Correct any stale claim that routes are always cell-centre staircases, that the local heading snaps per tick, or that a direct walk on a sloped shore suspends.
- [ ] **Step 2: D5 follow-up.** Root runs `scripts/ledger.sh search "heading interpolation"` and `scripts/ledger.sh search FacingYaw`, then files a `kind/roadmap`, `confidence/authored` issue: per-field remote interpolation of `MovementState.FacingYaw` in Replication, citing D5 and Grimhollow ruling PT.2. Record its number in Outcome.
- [ ] **Step 3: Version.** Fetch and merge current `origin/main` into the branch, then re-read `<KhaozEngineVersion>`, `CHANGELOG.md` and `git tag --sort=-v:refname | head -3`. Take the next free minor (expected 20.23.0). Bump it, add one `CHANGELOG.md` entry naming the four opt-ins with their defaults and the two fixes, update the guarded declarations and the INDEX and design status lines. Commit `release(20.23.0): straight routes, smooth local facing, directional gait blend` with the bump and the entry together.
- [ ] **Step 4: Verify once.**

```sh
mkdir -p local-feed
/tmp/grimhollow-orch/slot-run.sh "pt1:t9-build" /tmp/grimhollow-orch/pt1-t9-build.log -- dotnet build KhaozEngine.slnx -c Release
/tmp/grimhollow-orch/slot-run.sh "pt1:t9-test" /tmp/grimhollow-orch/pt1-t9-test.log -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: zero warnings, zero failures, nonzero counts and every guard exit 0. Record build time, assembly count, passed, failed, skipped and total in Outcome. A failure goes back to its owning task, not a rerun.
- [ ] **Step 5: Outcome and handoff.** Record per-task commits and counts, every departure with reason and cost, and the D5 issue. Root reviews the whole branch, merges and pushes main, then runs `scripts/pack-local-feed.sh` from main. Only the owner starts the tag. Grimhollow adopts a released pin on `feature/continuous-movement` and opts in there. This plan edits no Grimhollow file.

## Outcome

Implemented and verified on `feature/playtest1-engine-round`, staged as 20.23.0 with no tag. Setup merged
`origin/main` (v20.22.0, the dot lane telegraph) at `3f13725a5`. Task 9 fetched again and `origin/main` had not moved
past the branch, so its merge was a no-op. Root reviewed each task and pushed the branch after every clean review.
Fix rounds went out as fresh dispatches carrying the brief, the report and the findings, because a thread
continuation raised no completion notice (cost a context rebuild per round).

### Per task

- **Task 1, corner partial tick fact.** `72f2fe917`, fix round 1 `6d4f3f5ac`. The fact passes on unchanged source as
  planned, so the corner stall lead is ruled out. Measured minimum partial 0.0202413 m at tick 116 of 117 steps.
  Departure: the review moved the start to cell (2, 2) and added a `Contains(t < 0.025)` check, one fix round.
- **Task 2, `RouteStraightener`.** `caf031b6b`, 15/15, RED CS0246 and CS0103, a 0.5 offset mutant fails only
  `SideLinesJudgeMirroredWallsAlike`. Departures, each costing nothing: `PointQueriesAreStraightenedToo` uses a flat
  physics bake because the grid planner's point query already string-pulls. `UnchangedRouteIsReturnedByReference`
  uses a walled diagonal because a straight walled row legitimately collapses. A waypoint with no surface height ends
  the scan, so a heightless route stays raw and is documented as such (a heightless 2D consumer gets no shortcuts
  until an anchor Y fallback exists).
- **Task 3, `StraightenRoutes` and the D3 fallback.** `7d8e4f7cc` (Task 2 minors), `0e75ebbc3`. RED CS0117,
  straighten 7/7, Movement 166/166. Departures, each costing nothing: the obstacle fact asserts one heading change at
  each kept corner, two on the fixture, renamed `HeadingChangesOnceAtEachKeptCornerOfAConvexObstacle`, because the
  plan miscounted the corners. The fallback fact's guard admits every cell along each raw segment, modelling a
  walkable raw route. `MoveToRange.Straightener` is internal for tests.
- **Task 4, #1269.** `664ae2035` (Task 3 minor, a `StraightenRoutes` mover that must differ, mutation checked),
  `0a3515bc0`. RED `Suspended`, `DirectMoveToRange` 41/41, server `PlayerDirectApproach` 2/2. No departure.
  `_stepContext` outliving `Tick` was judged not a defect.
- **Task 5, `InterpolateYaw`.** `5b434d8ce` (Task 4 minors), `aea3a5cd7`. RED CS0539, Task 5 facts 12/12, Netcode and
  NetWorld 1713/1713, Game.Tests parity, step offset and animators 71/71, `DirectMoveToRange` 41/41. Departures,
  costing nothing: the version is 20.23.0, since v20.22.0 is the dot lane release. `CollapseSitesSnapThePreviousYaw`
  pins the rendered heading only, because every collapse site also moves the tick fraction to 1.
- **Task 6, `ClipSample` and `SampleBlendInto`.** `bdbf6d7f5` (Task 5 minors, the Netcode README heading sentence and
  a `RenderedState` allocation fact), `130d50df5`. RED CS0246, `ClientPredictionYaw` 10/10, `AnimationSamplerBlend`
  7/7 with one extra fact, Render3D.Animation 214/214. Finding: a default interface member boxes a struct, and Task 5
  read `Yaw` unconditionally, so every headingless state boxed once per `Predict`. Ruled a regression in this round
  and fixed in Task 7 before any release.
- **Task 7, directional gait blend.** `e1fe39f0e` (yaw reads gated on `InterpolateYaw && HasYaw`, RED 2400 bytes per
  pass then 0, ClientPrediction 43/43), `ddf988daa` (Task 6 minors, 7/7), `06b6466f5`. RED CS0246, blend 26/26,
  Locomotion 1085 passed with 1 pre-existing skip. Departures: `Ease` snaps a weight to its target within step + 1e-5
  because six float steps of 0.025 / 0.15 sum to 0.99999994 and land one call late (costs at most 1e-5 extra travel
  on the last call). Phase equality in facts is circular distance on [0, 1) (costs nothing).
- **Task 8, #1272 tap tolerance.** `d23a4d345` (Task 7 game minors, 27/27), `5a828054c` (Task 7 netcode minor,
  43/43), `2384306ca` (`PointerTapTolerance`, RED CS0246, `PointerGesture` 42/42), fix round 1 `d56d62d35`, fix round
  2 `816c03070` and `692ce3c9d`, fix round 3 `b238e43ad`. Final focused run `PointerGesture|FollowCamera` 163 passed
  with 5 pre-existing skips, ClientPrediction 43/43. Departures, three fix rounds in all: `FollowCameraController`
  called the two-argument `Advance`, which throws for a tolerance gesture, a plan gap fixed by timing its gestures
  with `dt` (one fix round). Without a tolerance the timed `Advance` neither uses nor checks the time (costs
  nothing). With a tolerance the press frame's own delta does not count, because it is motion before the press and
  let a settle plus a still hold launch the camera (costs the brief's literal numbers, ShortMove nets 4 points, slow
  drag catch-up 7.0, the fast move needs four frames). Grimhollow reads camera input with `Update(cameraInput, 0f)`
  and advances the camera clocks later, so `UpdateInput(in InputState, float elapsedSeconds)` was added and `Update`
  became `UpdateInput` plus the clocks (costs one public method).
- **Task 9, docs, version and verification.** `a71344d27` docs sweep, `2944fc877` format fix, `7a093324b` release,
  then this Outcome. The sweep added the #1269 shore rule to the guide, `UpdateInput` to the `UiBlocked` and tap
  sentences, the round's types to the root README rows, and rewrapped two over-long lines. Departures: the
  solution-wide `dotnet format` gate was replaced by `dotnet format whitespace KhaozEngine.slnx --verify-no-changes
  --include` over the 34 `.cs` files the branch changed, because the engine's CI runs no format step and the solution
  has pre-existing findings in 299 files (costs nothing). That check found object initializers on shared lines in
  `FollowCameraControllerTests.cs`, two lines from `b238e43ad` and one from `1083d1cfc` that predates the round,
  fixed whitespace-only in `2944fc877` (the full suite ran before it, and a whitespace change cannot alter the
  build).

### Verification

One Release run on the release tree, through the shared lock. `dotnet build KhaozEngine.slnx -c Release`: 43 s, 149
assemblies, 0 warnings, 0 errors. `dotnet test KhaozEngine.slnx -c Release --no-build --filter
"Category!=LiveSocket"`: 29 test assemblies, 25,181 passed, 0 failed, 1,328 skipped, 26,509 total, 72 s.
`check-dashes`, `check-prose`, `check-file-size` and `check-agent-instructions` with `--tree`, `check-doc-versions`
and the scoped whitespace format check all exit 0.

### Follow-ups and deferred minors

- D5 follow-up, filed by root: [#1280](https://github.com/APKiwiOrg/KhaozEngine/issues/1280), per-field remote
  interpolation of `MovementState.FacingYaw` in Replication (`kind/roadmap`, `confidence/authored`).
- Lead [#1278](https://github.com/APKiwiOrg/KhaozEngine/issues/1278): a raw fallback from an off-lattice body may
  start with a refused leg. Rare, since the plan-time guard must first admit a step the live guard refuses.
- Lead [#1279](https://github.com/APKiwiOrg/KhaozEngine/issues/1279): `PlayerMoveState` boxes through the default
  `PredictionTarget`, about 32 bytes per frame for a NetWorld local player. Older than this round.
- Deferred minor, Task 1: a stricter form of the `t < 0.025` check that excludes the final arrival step.
- Deferred minor, Task 4: `AllowsDrop` and `AllowsStep` answer the swim question two ways. Route both through one
  `LandsSwimming` helper when the drop path is next touched.
- [#1269](https://github.com/APKiwiOrg/KhaozEngine/issues/1269) and
  [#1272](https://github.com/APKiwiOrg/KhaozEngine/issues/1272) are resolved by this round and close when it merges.

### Grimhollow adoption

Adopt the released pin on `feature/continuous-movement`. This plan edits no Grimhollow file.

- Opt creatures into `RouteApproachOptions.StraightenRoutes`, and set `PredictionSettings.InterpolateYaw` on the
  client's prediction settings.
- Replace `ContinuousLocomotion`'s hard clip pick with `DirectionalLocomotionBlend`, fed from the replicated velocity
  through `BodyFrame` and sampled with `AnimationSampler.SampleBlendInto`.
- Build the orbit gesture with a `PointerTapTolerance`.
- In `PlayerRig.PrepareInput`, call `Controller.UpdateInput(cameraInput, dt)` in place of `Update(cameraInput, 0f)`,
  since a zero time freezes the grace.
- Watch creature arrival on the shipped world for #1278.

Root reviews the whole branch, merges and pushes main, then runs `scripts/pack-local-feed.sh` from main. Only the
owner starts the tag.
