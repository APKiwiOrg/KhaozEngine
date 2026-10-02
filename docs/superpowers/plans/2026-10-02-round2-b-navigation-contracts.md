# Round 2 B: Grounded Traversal and Goal-Region Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plan and follow a route to a reachable goal region over a guarded ground graph, preserving existing point, clearance and hop behavior.

**Architecture:** Add pure Navigation contracts for immutable capsule-checked graphs and predicate goal regions. Reuse one search and follower state machine, with an explicit grounded layered bake and additive overloads. Navigation acquires no Physics or Locomotion reference.

**Tech Stack:** C#/.NET 10, xUnit, existing Navigation and Game.Tests. Rides A's selected version, nominally 20.18.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md`, section 4 and D5, D6, D10, D12 and D13. Approved 2026-10-02. Branch 2 of 4. Read A's verified Outcome before starting.

## Global Constraints

- Owner-authorized execution is complete for Tasks 1 through 4 and is in progress for Task 5. Scoped task
  reviews are approved. Full branch verification and integration remain with the controller.
- Execution worktree `/Users/antonio/KhaozEngine/.worktrees/round2-navigation-contracts`, branch `feature/round2-navigation-contracts`, based on reconciled engine main containing A. Read AGENTS.md, contributor rules and DEPENDENCY-SEAMS.md.
- Ride the exact 20.x version the round's integration owner selected in A, nominally 20.18.0. No independent bump. Extend its changelog. Only the owner starts a release tag.
- Navigation references Primitives, Collision and Terrain only. New goal and traversal types use numbers, immutable data and a pure region predicate. No physics query delegate in a graph.
- Keep legacy constructor, point-query, point-follower, clearance-radius and hop-bake behavior. Pin unchanged outputs before refactoring shared search code. Existing fixtures do not move.
- New code gets cohesive new files. GridPathPlanner is 579 lines and PathFollower 387 at authoring. Preserve interfaces through partials, not arbitrary splits or baseline growth.
- One building worker, focused tests per task, one full Release suite at finish. No local repetitions or stress. Workers do not integrate, push, pack or tag. Review each task and the whole branch.
- Zero warnings. Test namespaces under KhaozEngine.Tests.Navigation. No em/en dashes or prose semicolons. Record departures in Outcome before integration.
- The existing NavSpace mutable-container contract is tracked separately at https://github.com/APKiwiOrg/KhaozEngine/issues/1232. Own the new graph's Space snapshot here. Do not widen this branch into a legacy NavSpace API repair.

## Review Focus

1. A goal's nearest face can be blocked while a farther face is reachable. Task 3 `ReachableFarSideWinsWhenNearestRegionCellsAreBlocked`.
2. Node passability alone cannot authorize crossing a thin wall. Task 2 `PassableNodesWithARefusedEdgeNeverProduceACrossing`.
3. A 0.6 m follower accept radius can stop outside a small region. Task 4 `FinalWaypointIsNotConsumedUntilTheBodyEntersTheRegion`.
4. Partial corridor exhaustion must not steer toward the raw goal. Task 4 `PartialEndWaitsForTheCooldownWithoutRawGoalMotion`.
5. Adding layers can exceed memory even when XZ bounds fit. Task 1 `LayerCellBudgetFailsBeforeGridMaterialization`.

---

### Task 1: Grounded layered bake with an allocation bound

**Files:**
- Modify `KhaozEngine.Navigation/NavLayerBaker.cs`.
- Create `KhaozEngine.Navigation/NavLayerBaker.Grounded.cs`, making the existing class partial.
- Create `KhaozEngine.Game.Tests/Navigation/GroundedNavLayerBakerTests.cs`.
- Reuse existing INavColumnProvider, NavSurfaceSample and NavLayerExtractor.

**Interfaces:**
- Add `NavSpace NavLayerBaker.BakeGroundedLayered(INavColumnProvider columns, float minX, float minZ, float maxX, float maxZ, float cellSize, float stepHeight, float agentHeight, int maxSurfacesPerColumn = 4, Func<float,float,bool>? extraBlocked = null, int maxLayerCells = int.MaxValue)`.
- Generate Stair links only. No jump-height or hop-cost parameter. Share validated column capture and layer extraction with BakeOverworldLayered, whose public signature and generated links stay unchanged.
- Validate finite bounds and sizes, checked dimensions and total layer-by-cell count before materializing grids. The empty-world result still has one blocked layer and counts against the budget.

- [x] **Step 1: Write tests.** Use synthetic columns for flat ground, a low step, separated deck/ground, a tall ledge and no surfaces. Add invalid finite/NaN bounds, provider overflow/order violations and Review Focus 5. Preserve the old hop bake's output over the same columns.
  ```csharp
  Assert.DoesNotContain(grounded.Links, link => link.Kind == NavLinkKind.Hop);
  Assert.Contains(grounded.Links, link => link.Kind == NavLinkKind.Stair);
  Assert.Single(empty.Layers);
  Assert.Throws<ArgumentOutOfRangeException>(() => bakeWithTooSmallBudget());
  Assert.Equal(oldHopWaypoints, unchangedHopWaypoints);
  ```
  Define the local bakeWithTooSmallBudget function inside its test. Use a provider with a known two-layer cell count, not a large allocation probe.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~GroundedNavLayerBakerTests'`. Expected new API compile failure.
- [x] **Step 3: Implement the named bake and common internals.** Keep the old public path's ordering, validation and link generation. Enforce maxLayerCells after extraction and before NavGrid allocation. No baseline refresh.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~NavLayerBakerTests|FullyQualifiedName~NavLayerLinksTests|FullyQualifiedName~NavHopLinksTests`. Expected nonzero and zero failures.
- [x] **Step 5: Commit.** Subject `feat(navigation): bake grounded layered paths without hop links`. Obtain task review.

### Task 2: Immutable directed traversal graphs

**Files:**
- Create `KhaozEngine.Navigation/NavTraversalLayer.cs`, node and edge storage.
- Create `KhaozEngine.Navigation/NavTraversalGraph.cs`, graph validation and queries.
- Modify `KhaozEngine.Navigation/GridPathPlanner.cs`, constructor and common blocked/edge decisions.
- Create `KhaozEngine.Navigation/GridPathPlanner.Traversal.cs`, guarded behavior as a partial.
- Create `KhaozEngine.Game.Tests/Navigation/NavTraversalGraphTests.cs` and `GuardedGridPathPlannerTests.cs`.

**Interfaces:**
- `NavTraversalLayer(int width, int height, ReadOnlySpan<bool> acceptedNodes, ReadOnlySpan<byte> exits)`. Copy both row-major inputs. Public Width, Height, IsAccepted(int x,int z), ExitMask(int x,int z).
- Exit bits 0 to 7 match `(1,0),(-1,0),(0,1),(0,-1),(1,1),(1,-1),(-1,1),(-1,-1)`, the existing planner neighbor order.
- `NavTraversalGraph(NavSpace space, float agentRadius, float agentHeight, IReadOnlyList<NavTraversalLayer> layers, IReadOnlyList<NavLink> links)`. Copy containers, validate matching dimensions, accepted endpoints and edge targets. Keep profile radius/height immutable and accepted links read-only. Expose immutable AgentRadius, AgentHeight and Space getters. Space is an owned snapshot with read-only layer/link containers, since the existing NavSpace constructor can retain a caller's mutable list.
- `bool IsNodePassable(int layer,int x,int z)` and `bool CanTraverse(int fromLayer,int fromX,int fromZ,int toLayer,int toX,int toZ)` return false outside bounds. Reject malformed input during construction.
- Add `GridPathPlanner(NavSpace space, NavTraversalGraph traversal, float hopCostCells = 4f)`. Query radius must equal the profile radius. Guarded node checks use raw NavGrid passability, radius 0, plus the graph. Keep the old constructor intact.

- [x] **Step 1: Write tests.** Construct a small open grid with a refused edge, one directed Stair link and a narrow accepted corridor whose physical radius was already accounted for. Add mismatched space, radius and dimensions, out-of-bounds exits, caller mutation and a failed snap across a rejected node. Pin legacy unguarded point waypoints before production edits.
  ```csharp
  Assert.False(graph.CanTraverse(0, 1, 1, 0, 2, 1));
  Assert.False(graph.CanTraverse(1, 0, 0, 0, 0, 0));
  Assert.Equal(NavPathStatus.Unreachable, blockedPath.Status);
  Assert.Equal(NavPathStatus.Complete, alreadyClearedCorridor.Status);
  Assert.Equal(originalMask, layer.ExitMask(1, 1));
  Assert.Equal(legacyWaypoints, legacyPlannerResult.Waypoints);
  ```
- [x] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~NavTraversalGraphTests|FullyQualifiedName~GuardedGridPathPlannerTests'`. Expected new API failure.
- [x] **Step 3: Implement.** Validate the supplied space against the graph, then retain the graph's owned Space snapshot. Apply graph checks to snapping, neighbor expansion and links. Disable straight-line shortcuts and string pulling for the graph constructor, returning the validated cell-centre chain. Preserve diagonal companion checks. Do not double-erode by radius. Leave unguarded paths on their existing arithmetic and smoothing.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~GridPathPlanner|FullyQualifiedName~NavGrid`. Expected nonzero and unchanged legacy fixtures.
- [x] **Step 5: Commit.** Subject `feat(navigation): plan over immutable directed ground traversal graphs`. Obtain task review.

### Task 3: Bounded search to a goal region

**Files:**
- Create `KhaozEngine.Navigation/NavGoalRegion.cs`, `IRegionPathPlanner.cs` and `GridPathPlanner.Region.cs`.
- Create `KhaozEngine.Navigation/GridPathPlanner.Search.cs` for the shared search/reconstruction concern, extracting rather than duplicating the existing RunAStar.
- Modify `KhaozEngine.Navigation/GridPathPlanner.cs` to implement IRegionPathPlanner and call that shared search.
- Create `KhaozEngine.Game.Tests/Navigation/GridPathPlannerRegionTests.cs`.

**Interfaces:**
- `NavGoalRegion(Vector3 anchor, float horizontalExtent, Func<Vector3,bool> contains)`, getters Anchor and HorizontalExtent, `bool Contains(Vector3 feetPosition)`. Reject null predicate, nonfinite anchor or extent, negative extent. Predicate is pure for a query.
- `IRegionPathPlanner : IPathPlanner` adds `NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)`.
- Candidate points are real cell-centre feet heights. Reject region queries over grids without surface heights, rather than guessing Y. Complete ends inside Contains. No centre-goal snap substitute.

- [x] **Step 1: Write tests.** Include Review Focus 1, a blocked target centre, a predicate admitting only the far face, start already inside, no region cells, zero extent, a vertically separated layer and a node-budget partial path. A 3 m SnapRadius must not turn an outside cell into a successful region member. Use a positive explicit MaxExpandedNodes budget.
  ```csharp
  Assert.Equal(NavPathStatus.Complete, farFace.Status);
  Assert.True(region.Contains(endpointFeet));
  Assert.Equal(NavPathStatus.Unreachable, noMember.Status);
  Assert.Equal(NavPathStatus.Partial, budgetLimited.Status);
  Assert.Throws<ArgumentException>(() => heightlessPlanner.FindPath(start, region, radius, budget));
  ```
  Resolve endpointFeet through the returned waypoint's actual grid SurfaceHeightAt. Do not use the target's Y as the oracle.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~GridPathPlannerRegionTests'`. Expected new API failure.
- [x] **Step 3: Implement one bounded search.** Region membership is the success condition. Use Euclidean XZ distance to Anchor minus HorizontalExtent, clamped at zero, on a compatible layer, and zero where a cross-layer lower bound is uncertain. Preserve fixed expansion/tie order and node budget. Reconstruct a guarded cell chain without smoothing. Legacy point queries retain their previous heuristic and exact-goal behavior through the shared search.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~GridPathPlanner`. Expected nonzero, all previous point and hop fixtures unchanged.
- [x] **Step 5: Commit.** Subject `feat(navigation): find reachable members of bounded goal regions`. Obtain task review.

### Task 4: Region-aware follower state

**Files:**
- Create `KhaozEngine.Navigation/PathFollower.Region.cs`, shared state through a partial class.
- Modify `KhaozEngine.Navigation/PathFollower.cs`, overload dispatch and new enum member only where needed.
- Create `KhaozEngine.Game.Tests/Navigation/PathFollowerRegionTests.cs`.

**Interfaces:**
- Add `PathFollowOutput Tick(Vector3 feetPosition, NavGoalRegion goal, float agentRadius, float dt)`.
- Add PathFollowState.WaitingForPath, returned only by the new region path when a partial corridor is exhausted and a replan is cooling down.
- Reuse path, index, cooldown, planned anchor and origin fields, ActivePath, ActiveWaypointIndex and Reset. Require IRegionPathPlanner, otherwise throw NotSupportedException for this overload.

- [x] **Step 1: Write tests with a counting scripted IRegionPathPlanner.** Implement both FindPath overloads in the test fake and expose an integer Calls counter. Add `FinalWaypointIsNotConsumedUntilTheBodyEntersTheRegion` and `PartialEndWaitsForTheCooldownWithoutRawGoalMotion`, a real waypoint at `(0,0)`, vertical layer changes, goal drift inside/outside 1.5 m and 0.8 m tolerances, reset during the 0.5 s cooldown, and a Hop waypoint. Assert final membership independently of follower state.
  ```csharp
  Assert.Equal(PathFollowState.Following, outsideFinalRegion.State);
  Assert.Equal(PathFollowState.WaitingForPath, exhaustedPartial.State);
  Assert.Equal(Vector2.Zero, exhaustedPartial.WorldDir);
  Assert.Equal(1, planner.Calls);
  Assert.Equal(PathFollowState.Arrived, actualMember.State);
  Assert.Null(follower.ActivePath);
  ```
- [x] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~PathFollowerRegionTests'`. Expected new API failure.
- [x] **Step 3: Implement.** Region membership owns initial and final arrival. Keep the last waypoint until membership. Intermediate consumption remains layer-aware. Partial exhaustion never yields a raw goal vector. The point overload continues to use its old semantics and never returns WaitingForPath. Keep follower configuration handling shared.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~PathFollowerTests|FullyQualifiedName~PathFollowerVerticalTests`. Expected nonzero and unchanged legacy results.
- [x] **Step 5: Commit.** Subject `feat(navigation): follow goal regions without false arrival or unsafe partial motion`. Obtain task review.

### Task 5: Documentation and branch verification

**Files:**
- Modify `KhaozEngine.Navigation/README.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/USING-KHAOZENGINE.md` and the staged CHANGELOG entry.
- Update this plan's Outcome with the actual interfaces C must consume.

- [x] **Step 1: Sweep docs for IPathPlanner, PathFollower, NavLayerBaker, clearance and string pulling.** Document the explicit guarded exception to smoothing and radius handling, goal-region feet convention, new waiting state and grounded bake. Confirm Navigation's three project references remain unchanged.
- [ ] **Step 2: Fetch and merge current main into this branch, then verify once, only after slot authorization.**
  ```bash
  mkdir -p local-feed
  dotnet build KhaozEngine.slnx -c Release
  dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
  sh scripts/check-dashes.sh --tree
  sh scripts/check-prose.sh --tree
  sh scripts/check-file-size.sh --tree
  bash scripts/check-doc-versions.sh
  ```
  Expected zero warnings, no failures, nonzero test counts and passing guards. Obtain whole-branch review. Record every departure and its cost before landing.
- [x] **Step 3: Commit.** Subject `docs(navigation): document guarded region routes and round B outcome`. Integration owner merges and pushes main and serializes any pack. No new version or tag. C reads this Outcome before creating its package.

## Outcome

Owner-authorized execution replaced the stale design-only notice. The branch started from reconciled
engine main `4a0c3aa9f` with staged version 20.18.0. Main was fetched and merged with no changes. Tasks 1
through 4 are implemented, their scoped reviews are approved, and Task 5 has completed the living-document
sweep. No version bump, tag, pack, dependency edge, baseline change, or Grimhollow edit was made.
Navigation has no Physics or Locomotion reference. This branch makes no Movement package claim. C creates
that package.

The task commits are `3b3a8eeca` and `37551021d` for grounded baking, `547e4597e` for traversal graphs,
`b3200e269` and `33897fa39` for region search, and `2ff1effea` and `2a08495e8` for region follower state.
Focused evidence is 107 passed grounded-bake cases, 120 passed traversal and legacy planner cases, 69
passed region and planner cases, and 63 passed region and legacy follower cases. The Task 4 focused GREEN
included 24 region cases and 39 unchanged point follower cases.

The interfaces C must consume are the implemented contracts below. `NavGoalRegion(Vector3 anchor,
float horizontalExtent, Func<Vector3, bool> contains)` exposes `Anchor`, `HorizontalExtent`, and
`Contains(Vector3 feetPosition)`. `IRegionPathPlanner.FindPath(Vector3 start, NavGoalRegion goal,
float agentRadius, PathQueryBudget budget)` extends `IPathPlanner`. `PathFollower.Tick(Vector3
feetPosition, NavGoalRegion goal, float agentRadius, float dt)` requires `IRegionPathPlanner` and throws
`NotSupportedException` otherwise. Region completion is `goal.Contains(feetPosition)` on actual feet.
The final Complete waypoint remains active until membership. `PathFollowState.WaitingForPath` is appended
after existing enum values. `PathFollowOutput` exposes `WorldDir`, `State`, `ActiveWaypoint`, and
`HopStart`. `PathFollower.ActivePath`, `ActiveWaypointIndex`, and `Reset` remain available. An exhausted
region Partial returns WaitingForPath with zero direction while cooldown is positive. A fresh exhausted
Partial with zero cooldown returns zero Unreachable and retries on the next tick. Existing region routes
advance before query eligibility, terminal Complete endpoints outside membership make replanning due, and
each tick issues at most one query.

The guarded traversal contracts consumed by later work are `NavTraversalLayer(int width, int height,
ReadOnlySpan<bool> acceptedNodes, ReadOnlySpan<byte> exits)`, with copied row-major masks and exit bits
0 through 7 ordered `(1,0)`, `(-1,0)`, `(0,1)`, `(0,-1)`, `(1,1)`, `(1,-1)`, `(-1,1)`, `(-1,-1)`,
`NavTraversalGraph(NavSpace space, float agentRadius, float agentHeight,
IReadOnlyList<NavTraversalLayer> layers, IReadOnlyList<NavLink> links)`, and
`GridPathPlanner(NavSpace space, NavTraversalGraph traversal, float hopCostCells = 4f)`. The graph owns
the source Space snapshot, copied masks, and read-only accepted Links subset. The guarded planner requires
matching source topology and the exact profile radius, uses raw radius-zero grid checks plus graph admission,
requires admitted raw-passable own cells even with positive SnapRadius, and returns cell-centre routes
without shortcuts or string pulling. `NavLayerBaker.BakeGroundedLayered` and
`NavLayerLinks.GenerateGrounded` provide the finite, pre-allocation-budgeted Stair-only bake. Existing hop
baking and unlimited legacy extraction remain available.

Ruling B1.1: `NavLayerLinks.cs` shares grounded generation with the existing scan instead of passing a
dummy jump height. Reason: grounded links must be Stair-only while preserving the old valid-input ordering.
Cost: a legacy nav or link regression if ordering or hop output changed, covered by old link tests and
literal hop parity fixtures.

Ruling B1.2: `NavLayerExtractor.cs` enforces `maxLayerCells` immediately after layer assignment and before
dense arrays. Reason: a guard after extraction would allow dense allocations first. Cost: extraction
regression or premature rejection, covered by legacy tests and the 12-cell two-layer boundary fixture.

Ruling B2.1: the existing `Blocks` XML cref uses its `NavGrid` signature. Reason: the new layer-aware
overload makes the shorthand ambiguous under CS0419. Cost: incorrect API documentation or a warnings-as-
errors build failure if left unqualified.

Ruling B2.2: guarded snapping requires an admitted raw-passable own cell and returns its centre. Reason:
candidate-only rings can cross rejected nodes and report Complete without an admitted route. Cost: nearby
accepted alternatives are refused. Legacy rings remain unchanged.

Ruling B3.1: shared search separates safe priority from region partial progress toward actual member feet.
Reason: zero safe priority otherwise pins partials to the start. Cost: one comparison per member for each
expanded node, while legacy point arithmetic remains unchanged.

Ruling B3.2: actual admitted members are captured once before search instead of inventing Y or substituting
the anchor. Reason: region queries must reject empty membership and compare partial progress to real feet.
Cost: topology scan and member storage outside `MaxExpandedNodes`.

Ruling B3.3: cross-layer links and same-layer links cheaper than their XZ displacement disable region
spatial priority. Reason: either can undercut the lower bound. Cost: Dijkstra ordering and additional
expansions.

Ruling B3.4: exact member discovery is limited to the conservative anchor and extent XZ AABB. Reason: the
region already requires members within that horizontal bound. Cost: two double difference comparisons per
passable discovery cell. The topology scan remains and the AABB reduces predicate calls, not M for valid
predicates.

Ruling B3.5: region progress and distance arithmetic use finite-safe double differences and squared
distances. Reason: float squared distances can overflow and suppress useful Partial progress. Cost: wider
private arithmetic and one double square root for region distance. Legacy point priority and arithmetic
remain unchanged.

Ruling B4.1: the old point-arrival Tick cref and genuinely shared XML descriptions are qualified. Reason:
the additive overload makes shorthand method crefs ambiguous under CS0419. Cost: documentation accuracy
only, with no point behavior change.

Ruling B4.2: a retained region waypoint with zero horizontal offset emits finite zero direction while
remaining Following. Reason: same XZ on the wrong floor is outside actual membership and zero normalization
would return NaN. Cost: no horizontal progress until membership or replan, with point arithmetic unchanged.

Ruling B4.3: a reached retained Complete endpoint outside membership makes a replan due. Reason:
sub-threshold target movement could otherwise leave Following zero forever at the old endpoint. Cost:
queries may recur once per cooldown while the body remains near an endpoint outside the region. The radius
and layer tests establish query eligibility only. They do not establish region arrival.

Ruling B4.4: existing region corridors are advanced before query eligibility with the shared helper.
Reason: endpoint exhaustion must be visible when cooldown drains this tick. Cost: region corridor checks use
remaining segments. A fresh exhausted Partial with zero cooldown returns zero Unreachable for that tick,
then one query is allowed on the next tick, preventing an unbounded loop.

Ruling B5.1: the root README Navigation catalog row and the round 2 design Status and Review metadata were
updated. Reason: the public catalog and program status were stale after owner-authorized execution. Cost:
documentation accuracy only.

Task 4's RED fix briefly overlapped an unrelated Grimhollow Release build because the worker combined its
process check and test launch before inspecting the result. The worker attempted to interrupt only its own
already-finished test and touched no external process. The controller held further build and test work,
checked the slot separately, and cleared the focused GREEN. This workflow incident is recorded here as a
departure, not as evidence of serialized RED execution.

The controller's branch Release build and test evidence is available at `2a08495e8`: build exit 0 with zero
warnings and errors, and full test exit 0 across 28 assemblies with 23,208 passed, 0 failed, 1,275 skipped,
and 24,483 total cases. Final Task 5 guards, whole-branch review, integration, and packaging remain pending
with the controller. This Outcome is ready for C to consume after that gate.
