# Round 2 B: Grounded Traversal and Goal-Region Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plan and follow a route to a reachable goal region over a guarded ground graph, preserving existing point, clearance and hop behavior.

**Architecture:** Add pure Navigation contracts for immutable capsule-checked graphs and predicate goal regions. Reuse one search and follower state machine, with an explicit grounded layered bake and additive overloads. Navigation acquires no Physics or Locomotion reference.

**Tech Stack:** C#/.NET 10, xUnit, existing Navigation and Game.Tests. Rides A's selected version, nominally 20.18.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md`, section 4 and D5, D6, D10, D12 and D13. Approved 2026-10-02. Branch 2 of 4. Read A's verified Outcome before starting.

## Global Constraints

- Unexecuted plan. The owner must free the build slot before execution. This planning session does not implement it.
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

- [ ] **Step 1: Write tests.** Use synthetic columns for flat ground, a low step, separated deck/ground, a tall ledge and no surfaces. Add invalid finite/NaN bounds, provider overflow/order violations and Review Focus 5. Preserve the old hop bake's output over the same columns.
  ```csharp
  Assert.DoesNotContain(grounded.Links, link => link.Kind == NavLinkKind.Hop);
  Assert.Contains(grounded.Links, link => link.Kind == NavLinkKind.Stair);
  Assert.Single(empty.Layers);
  Assert.Throws<ArgumentOutOfRangeException>(() => bakeWithTooSmallBudget());
  Assert.Equal(oldHopWaypoints, unchangedHopWaypoints);
  ```
  Define the local bakeWithTooSmallBudget function inside its test. Use a provider with a known two-layer cell count, not a large allocation probe.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~GroundedNavLayerBakerTests'`. Expected new API compile failure.
- [ ] **Step 3: Implement the named bake and common internals.** Keep the old public path's ordering, validation and link generation. Enforce maxLayerCells after extraction and before NavGrid allocation. No baseline refresh.
- [ ] **Step 4: Run** the same filter plus `FullyQualifiedName~NavLayerBakerTests|FullyQualifiedName~NavLayerLinksTests|FullyQualifiedName~NavHopLinksTests`. Expected nonzero and zero failures.
- [ ] **Step 5: Commit.** Subject `feat(navigation): bake grounded layered paths without hop links`. Obtain task review.

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

- [ ] **Step 1: Write tests.** Construct a small open grid with a refused edge, one directed Stair link and a narrow accepted corridor whose physical radius was already accounted for. Add mismatched space, radius and dimensions, out-of-bounds exits, caller mutation and a failed snap across a rejected node. Pin legacy unguarded point waypoints before production edits.
  ```csharp
  Assert.False(graph.CanTraverse(0, 1, 1, 0, 2, 1));
  Assert.False(graph.CanTraverse(1, 0, 0, 0, 0, 0));
  Assert.Equal(NavPathStatus.Unreachable, blockedPath.Status);
  Assert.Equal(NavPathStatus.Complete, alreadyClearedCorridor.Status);
  Assert.Equal(originalMask, layer.ExitMask(1, 1));
  Assert.Equal(legacyWaypoints, legacyPlannerResult.Waypoints);
  ```
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~NavTraversalGraphTests|FullyQualifiedName~GuardedGridPathPlannerTests'`. Expected new API failure.
- [ ] **Step 3: Implement.** Validate the supplied space against the graph, then retain the graph's owned Space snapshot. Apply graph checks to snapping, neighbor expansion and links. Disable straight-line shortcuts and string pulling for the graph constructor, returning the validated cell-centre chain. Preserve diagonal companion checks. Do not double-erode by radius. Leave unguarded paths on their existing arithmetic and smoothing.
- [ ] **Step 4: Run** the same filter plus `FullyQualifiedName~GridPathPlanner|FullyQualifiedName~NavGrid`. Expected nonzero and unchanged legacy fixtures.
- [ ] **Step 5: Commit.** Subject `feat(navigation): plan over immutable directed ground traversal graphs`. Obtain task review.

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

- [ ] **Step 1: Write tests.** Include Review Focus 1, a blocked target centre, a predicate admitting only the far face, start already inside, no region cells, zero extent, a vertically separated layer and a node-budget partial path. A 3 m SnapRadius must not turn an outside cell into a successful region member. Use a positive explicit MaxExpandedNodes budget.
  ```csharp
  Assert.Equal(NavPathStatus.Complete, farFace.Status);
  Assert.True(region.Contains(endpointFeet));
  Assert.Equal(NavPathStatus.Unreachable, noMember.Status);
  Assert.Equal(NavPathStatus.Partial, budgetLimited.Status);
  Assert.Throws<ArgumentException>(() => heightlessPlanner.FindPath(start, region, radius, budget));
  ```
  Resolve endpointFeet through the returned waypoint's actual grid SurfaceHeightAt. Do not use the target's Y as the oracle.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~GridPathPlannerRegionTests'`. Expected new API failure.
- [ ] **Step 3: Implement one bounded search.** Region membership is the success condition. Use Euclidean XZ distance to Anchor minus HorizontalExtent, clamped at zero, on a compatible layer, and zero where a cross-layer lower bound is uncertain. Preserve fixed expansion/tie order and node budget. Reconstruct a guarded cell chain without smoothing. Legacy point queries retain their previous heuristic and exact-goal behavior through the shared search.
- [ ] **Step 4: Run** the same filter plus `FullyQualifiedName~GridPathPlanner`. Expected nonzero, all previous point and hop fixtures unchanged.
- [ ] **Step 5: Commit.** Subject `feat(navigation): find reachable members of bounded goal regions`. Obtain task review.

### Task 4: Region-aware follower state

**Files:**
- Create `KhaozEngine.Navigation/PathFollower.Region.cs`, shared state through a partial class.
- Modify `KhaozEngine.Navigation/PathFollower.cs`, overload dispatch and new enum member only where needed.
- Create `KhaozEngine.Game.Tests/Navigation/PathFollowerRegionTests.cs`.

**Interfaces:**
- Add `PathFollowOutput Tick(Vector3 feetPosition, NavGoalRegion goal, float agentRadius, float dt)`.
- Add PathFollowState.WaitingForPath, returned only by the new region path when a partial corridor is exhausted and a replan is cooling down.
- Reuse path, index, cooldown, planned anchor and origin fields, ActivePath, ActiveWaypointIndex and Reset. Require IRegionPathPlanner, otherwise throw NotSupportedException for this overload.

- [ ] **Step 1: Write tests with a counting scripted IRegionPathPlanner.** Implement both FindPath overloads in the test fake and expose an integer Calls counter. Add `FinalWaypointIsNotConsumedUntilTheBodyEntersTheRegion` and `PartialEndWaitsForTheCooldownWithoutRawGoalMotion`, a real waypoint at `(0,0)`, vertical layer changes, goal drift inside/outside 1.5 m and 0.8 m tolerances, reset during the 0.5 s cooldown, and a Hop waypoint. Assert final membership independently of follower state.
  ```csharp
  Assert.Equal(PathFollowState.Following, outsideFinalRegion.State);
  Assert.Equal(PathFollowState.WaitingForPath, exhaustedPartial.State);
  Assert.Equal(Vector2.Zero, exhaustedPartial.WorldDir);
  Assert.Equal(1, planner.Calls);
  Assert.Equal(PathFollowState.Arrived, actualMember.State);
  Assert.Null(follower.ActivePath);
  ```
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~PathFollowerRegionTests'`. Expected new API failure.
- [ ] **Step 3: Implement.** Region membership owns initial and final arrival. Keep the last waypoint until membership. Intermediate consumption remains layer-aware. Partial exhaustion never yields a raw goal vector. The point overload continues to use its old semantics and never returns WaitingForPath. Keep follower configuration handling shared.
- [ ] **Step 4: Run** the same filter plus `FullyQualifiedName~PathFollowerTests|FullyQualifiedName~PathFollowerVerticalTests`. Expected nonzero and unchanged legacy results.
- [ ] **Step 5: Commit.** Subject `feat(navigation): follow goal regions without false arrival or unsafe partial motion`. Obtain task review.

### Task 5: Documentation and branch verification

**Files:**
- Modify `KhaozEngine.Navigation/README.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/USING-KHAOZENGINE.md` and the staged CHANGELOG entry.
- Update this plan's Outcome with the actual interfaces C must consume.

- [ ] **Step 1: Sweep docs for IPathPlanner, PathFollower, NavLayerBaker, clearance and string pulling.** Document the explicit guarded exception to smoothing and radius handling, goal-region feet convention, new waiting state and grounded bake. Confirm Navigation's three project references remain unchanged.
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
- [ ] **Step 3: Commit.** Subject `docs(navigation): document guarded region routes and round B outcome`. Integration owner merges and pushes main and serializes any pack. No new version or tag. C reads this Outcome before creating its package.
