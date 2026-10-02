# Continuous host, round 2: movement, reach and physics navigation

Status: approved by the owner on 2026-10-02. Plan A is complete and verified at staged 20.18.0 with wire generation 13.
Plan B is implemented, reviewed, fully verified and integrated on engine main at `4431781f6`.
Plan C code, bridge acceptance and living documentation are complete on `feature/round2-reach-physics-nav` at
`f7a1ec497`, with full Release verification, all five guards and whole-branch approval.
Plan D implementation, acceptance and living documentation are complete on the reconciled
`feature/round2-movement-drivers` branch. Its full Release verification passed against staged 20.18.0 with
23,580 passed, 0 failed, 1,275 skipped and 24,855 total cases across 29 assemblies, with zero build warnings and
errors. Whole-branch review approved after two comment and status corrections. Root merges and pushes this
verified branch and refreshes canonical packages. Engine tagging and consumer adoption remain owner-owned.
The owner authorized the whole round program.
Issue [#1238](https://github.com/APKiwiOrg/KhaozEngine/issues/1238) is resolved by the concurrent reconciled
`PhysicsColumnProbe` representable-progress fix outside Plan D. Issue [#1233](https://github.com/APKiwiOrg/KhaozEngine/issues/1233)
remains an open adoption prerequisite for steep meshes and filtered ground. No full Hollowmere startup guarantee
is claimed.

Consumer: Grimhollow continuous movement phase P4, before P5 NPCs and interactions and P6 combat.
Engine issue: [#1223](https://github.com/APKiwiOrg/KhaozEngine/issues/1223).
Consumer epic: https://github.com/APKiwiOrg/Grimhollow/issues/399.
The consumer contract is Grimhollow's
`docs/superpowers/specs/2026-10-01-continuous-movement-design.md`, especially O4, O11, T6 and T10.
Round 1's design and all four plans' Outcome blocks are required context for execution.

## Intent and success

The engine supplies how a continuous body approaches a target and how far its shape is from that target.
Grimhollow supplies why it moves, whom it targets, what it can do there and the catalog numbers.
An NPC uses the same collision core as a player. A player walk-up or chase produces ordinary client input,
which goes through prediction, the move codec, authoritative simulation and reconciliation.
There is no server-side player route, teleport-to-range or second movement authority.

The round is complete when headless acceptance tests drive both paths over real physics, arrive within
the requested shape range, route around thin walls, respect traversal areas and distinguish a failed
route from arrival. The owner then releases the engine. Grimhollow adopts that released pin on
`feature/continuous-movement`, never on its released tile `main` during the pivot.
Ruinborne adopting these helpers is optional. This round changes no game repository.

This document is a design artifact. The owner released Grimhollow P3's build slot and authorized Plan A execution.
Plan A's final full Release verification, including the precise-intent helper fix, built with zero warnings and ran
28 test assemblies with 23,042 passed, 0 failed and 1,269 skipped. Later plans, their bakes, engine tagging and consumer
adoption remain pending.

## Decisions

The owner approved the written design on 2026-10-02, after separately approving D2.
Existing O and T rulings are not reopened.

| # | Decision | Recommendation or fixed constraint |
| --- | --- | --- |
| D1 | Package boundary | Add explicit, GPU-free `KhaozEngine.Movement`, depending on Locomotion, Navigation and Physics. Keep the existing Navigation and Physics non-edge. |
| D2 | Does reach include height? | Owner approved true 3D edge distance between upright capsules, yawed footprint boxes and points on 2026-10-02. Sufficient height separation puts a target out of reach. |
| D3 | Position and shape conventions | Movement state is a capsule centre. Navigation receives feet. Each body subtracts its own half-height. Public kernel and baked-nav coordinates are absolute world metres. |
| D4 | Who sets distances and pace? | The game passes range, tolerance, tuning and run or walk choice. No Grimhollow numbers or wolf defaults in the engine. |
| D5 | What counts as arrival? | Shape reach on the current body and target. Follower arrival, a snapped endpoint and a partial route do not imply `InRange`. |
| D6 | Route to a solid target | Search a reachable goal region around the shape, rather than snapping the target centre or choosing one nearest face. |
| D7 | Final movement fraction | Add opt-in `MoveCommand.ScaleSpeedByAxis`, default false. Carry it in a free bit of the existing 18-byte move frame. Ordinary keyboard commands retain their behavior. |
| D8 | Wire compatibility | Advance the engine wire generation once for D7. Expect 12 to 13, subject to execution-time inspection. Persisted built-in payloads do not change. |
| D9 | NPC and player ground path | One range-steering core. An NPC adapter calls `StepTowards`. A player adapter emits `MoveCommand` and never changes `MoveState`. |
| D10 | Ground navigation | Add a grounded layered bake with walked links and no generated Hop links. Existing point planners and hop bakes retain their defaults and contracts. |
| D11 | Traversal areas | Game-owned bit tags and required/excluded filters. Bake a profile for each distinct capsule geometry and area policy from a shared column snapshot. |
| D12 | Thin walls and clearance | A physics-backed profile validates body occupancy and adjacency, not just downward columns. Its graph records already capsule-checked nodes and edges. Do not erode the radius twice. |
| D13 | Shortcuts and failed routes | Guard near-field approach, endpoint snapping, partial-path completion and any shortcut with the same traversal policy. A failed route holds and reports its state. No unconditional straight press. |
| D14 | Hops | Ground helpers return an unsupported-transition result for Hop waypoints supplied by an external planner. No wolf lunge or automatic player jump is lifted. |
| D15 | State lifetime | One follower per moving body or client automation request. Reset on target identity, shape, range or traversal-profile changes, teleport and cancellation. Position drift uses the follower's cooldown. |
| D16 | Facing and brains | Travel facing remains the mover's output. Target-facing holds, wander, leash, retaliation, conversation freeze and action dispatch stay game-owned. |
| D17 | Branches | Four plans and branches. Command fraction, navigation contracts, reach plus physics bake, then movement drivers and acceptance. One building worker at a time when authorized. |
| D18 | Version and release | The integration owner selected additive minor 20.18.0 once. Later branches ride it. Plan A records the version and wire generation 13 on its task branch. There is no release or consumer adoption yet. Only the owner starts tagging. |

## Approaches considered

The scores are design judgments, 1 to 10. Higher is better. They assess the full E4 scope, including the
deliberate Navigation/Physics separation in `docs/DEPENDENCY-SEAMS.md`.

| Approach | Existing boundaries | Consumer reuse | Contract clarity | Delivery simplicity | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| A. Explicit Movement composition package, additive lower-level navigation seams | 10 | 9 | 9 | 7 | 35 |
| B. Add Navigation and physics-bake orchestration directly to Locomotion | 6 | 9 | 7 | 9 | 31 |
| C. Small helpers plus game-side bake and range routing glue | 10 | 4 | 5 | 8 | 27 |

Recommend A. It preserves both lower packages' responsibilities and gives the consumer one documented
entry point. Its cost is another package and its integration documentation. B is fewer projects but
makes a caller that only wants character stepping acquire path planning. C leaves Grimhollow owning
the same movement policy and physics-to-nav glue this round is meant to move into the engine.

## 1. Package and dependency edges

`KhaozEngine.Movement` is an explicit composition package, in no umbrella initially. It references
only `KhaozEngine.Locomotion`, `KhaozEngine.Navigation` and `KhaozEngine.Physics`. No Bepu, TileWorld,
NetWorld, ECS, render, input or game package. Physics remains a zero-project-reference seam.
Navigation keeps exactly its Primitives, Collision and Terrain references.

```text
Movement -> Locomotion -> Primitives, Physics
Movement -> Navigation -> Primitives, Collision, Terrain
Movement -> Physics
Navigation -x Physics
Physics -x Navigation
NetWorld -> Locomotion   existing edge, carries the command flag
TileWorld.Physics -> TileWorld, Physics, Locomotion   unchanged
```

The kernel operates on numeric shapes and `MoveState`, not entity ids or archetypes. The game extracts
the body and target snapshot before calling it. NPC server composition publishes the accepted
`MoveState` once on `OnBeforeTick`. Client composition submits the returned command once per simulation
tick, not once per render update plus once per prediction update.

Create `KhaozEngine.Movement.Tests`, `IsPackable=false`, namespace `KhaozEngine.Tests.Movement`.
It references Movement and Physics.Bepu. Synthetic TileWorld bridge acceptance lives in
`KhaozEngine.TileWorld.Physics.Tests`, which references Movement for those tests. Navigation and
Locomotion unit tests remain in their current `KhaozEngine.Game.Tests` areas. The actual client/server
command-path acceptance lives in `KhaozEngine.Server.Tests`, with an explicit Movement reference.
No broad test umbrella reference is added.

Execution updates the package catalog in root README, solution, package README, consumer guide,
dependency-seams graph and its architecture guard. Extend the existing opt-in closure guard for
Movement even though it is a composition package rather than a third-party backend. A small new
architecture test file owns its exact references, keeping the existing file-size ratchet intact.

## 2. Shape reach

API sketches below are proposed public contracts. Plan A's command fraction APIs are implemented on its task branch at 20.18.0,
the remaining sketches are not claims that all names ship before Plans B, C and D start.

```csharp
public readonly struct MovementBody
{
    public MovementBody(Vector3 centre, float radius, float halfHeight);
    public Vector3 Centre { get; }
    public float Radius { get; }
    public float HalfHeight { get; }
}

public readonly struct ReachTarget
{
    public static ReachTarget Capsule(in MovementBody body);
    public static ReachTarget Box(Vector3 centre, Vector3 halfExtents, float yawRadians = 0f);
    public static ReachTarget Point(Vector3 position);
}

public static class ReachGeometry
{
    public static float Distance(in MovementBody body, in ReachTarget target);
    public static bool Within(in MovementBody body, in ReachTarget target,
        float range, float tolerance = 0f);
}
```

A capsule is upright. Half-height includes both hemispheres. Its axis segment extends
`max(0, HalfHeight - Radius)` above and below the centre. Radius is positive, half-height is at least
radius, positions and sizes are finite. Constructors validate, and public operations refuse an invalid
default struct. Ground movement tuning must additionally leave at least the existing CapsuleFor
minimum 0.01 m cylindrical length, so its collision half-height and navigation feet agree. In
particular, a wide cow radius cannot be paired with a shorter player half-height. P5 supplies valid
capsule dimensions and keeps the visual body separate from the collision body.
A footprint box rotates about Y only. X and Z half-extents
are positive, Y may be zero for a flat footprint. A ground-item point is an explicit point.
There is no mesh lookup, collider handle, physics query or allocation in reach measurement.

For capsule/capsule, find the distance between their vertical axis segments and subtract both radii.
For capsule/box, transform the capsule centre into the box's yaw frame, measure separation between
the upright axis segment and the box intervals and subtract the body radius. For capsule/point,
measure point-to-axis distance and subtract the body radius. Clamp overlaps to zero.
This is shape distance, not centre distance minus a box's bounding-circle radius.

`Within` is `Distance <= range + tolerance`. Both numbers are finite and nonnegative. There is no
hidden gameplay epsilon or baked server tolerance. Grimhollow alone supplies T10's 0.6 m melee range,
0.5 m server tolerance and 1.5 m interaction range. Client approach uses the requested range without
borrowing the server tolerance. A cow's radius and target-box height are consumer inputs, not guessed
from its mesh or copied from the player's body. Grimhollow P5 must define each target's numeric shape.

Reach does not authorize an attack or interaction. It does not check facing, line of sight, hostility,
ownership, action cadence or admission. Those remain game rules. Goal membership, client arrival and
server reach all use the same owner-approved 3D metric.

## 3. Ordinary commands can preserve the final fraction

`CharacterMovement.Horizontal.ResolveCameraRelative` currently returns fraction 1 for every nonzero
player axis. `ResolveWorldDir` retains an NPC direction's magnitude. A direct lift that scales an
NPC vector, then copies that vector to player input, therefore changes its speed at the player seam.

Keep the existing MoveCommand constructor and add an overload with a trailing required
`bool scaleSpeedByAxis`, exposing `bool ScaleSpeedByAxis`. Existing construction and default structs
leave it false. When false, keep today's resolver arithmetic and output. When true, normalize
direction and use the axis length clamped to [0,1] as the speed fraction. Precise commands treat only
zero length as idle, so a valid final fraction below the legacy input dead zone does not stall.
Directional movement scales and state speed scale compose in the existing core, once each.
A large finite axis never grants more than full speed.

Keep the existing StepTowards signature and add a distinct overload with required
`bool preserveSmallMagnitude` immediately after tuning, ahead of the existing optional providers.
The NPC kernel uses it with true, retaining tiny final fractions through the same StepCore. False
retains the old world-direction resolver. Validate and normalize without overflow on large finite
vectors or underflow on small ones. This does not change manual analog-stick behavior by default.

NetWorld encodes the flag as `0x04` in byte 12. Jump stays byte 17. Move frames stay 18 bytes, preserving
the move/control/game-message length demultiplexer. Decode forwards the flag into the command. All
copy, prediction and replay sites must preserve it. New commands are still time-free input, with dt
owned by the server. No component or persistence payload is added.

Use the engine's exact wire-generation handshake to reject peers that would ignore the new flag and
run at full speed. Advance its generation once. Test generation-12 persisted cell bodies normalizing
to the new generation byte for byte, since the snapshot layouts are unchanged. Unstamped inference
must treat equivalent candidate bytes as equivalent, as `CellBlobRewriter.Decide` already does.
The game protocol remains Grimhollow P8's decision. The engine wire gate is separate.

The new flag defaults off in old construction sites, including `default` idle commands. Capture
legacy move-byte fixtures and legacy fractional-axis movement before changing the resolver. Do not
refresh them to the new behavior.

## 4. Navigation contracts for range and guarded ground paths

### A goal region, not a target centre

An object's centre is often blocked. A nearest snapped cell can be farther from it than the requested
range. A single nearest-face point can also be inaccessible while another face is reachable.
The planner needs a goal region and must choose a reachable member, with one bounded search.

```csharp
public sealed class NavGoalRegion
{
    public NavGoalRegion(Vector3 anchor, float horizontalExtent, Func<Vector3, bool> contains);
    public Vector3 Anchor { get; }
    public float HorizontalExtent { get; }
    public bool Contains(Vector3 feetPosition);
}

public interface IRegionPathPlanner : IPathPlanner
{
    NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget);
}

// Additive members on the existing implementation and follower.
// GridPathPlanner implements IRegionPathPlanner.
// PathFollower.Tick(Vector3 feetPosition, NavGoalRegion goal, float agentRadius, float dt)
```

Movement builds the predicate by seating the moving body's capsule on each candidate feet position
and evaluating `ReachGeometry.Within` with zero tolerance. The anchor and extent provide a conservative
horizontal lower bound. A yawed box uses its horizontal bounding circle plus mover radius and range
for that bound only. A candidate succeeds only on the exact shape predicate. Region queries use
surface-height grids. They do not invent Y from the target centre for a grid without surface heights.

Grid search terminates at a passable member of the region, retaining fixed scan and tie order and
`PathQueryBudget` limits. A heuristic must underestimate cost to the region on every layer. Starting
with zero heuristic on cross-layer search is acceptable. Avoid one A* per sampled face.
An exhausted budget can return Partial, with the existing NavPath semantics. Complete means its
endpoint belongs to the region. Snapping cannot silently substitute a point outside it.

The region follower reuses the existing replan, cooldown and layer-aware state. Its arrival shortcut
checks region membership. It keeps the final waypoint until actual region entry, rather than consuming
it merely because its default 0.6 m AcceptRadius was met. Consuming a partial corridor holds for the
next replan instead of producing the point overload's current one-tick raw-goal press.
The point overload is unchanged. Region calls require an `IRegionPathPlanner`, with a clear failure
for an unsupported planner rather than an implicit point-query fallback.
Add a `PathFollowState.WaitingForPath` result for the region overload's exhausted partial corridor
while a replan is gated by cooldown. The point overload never returns that new state. Do not infer
waiting from a zero waypoint, which can be a real world coordinate.

### Guarded graph

Add an immutable `NavTraversalGraph` in Navigation. It stores a profile's accepted nodes, eight
directional adjacency bits per node and accepted cross-layer links. It also records the radius and
height for which those decisions were baked. Its constructor validates dimensions and endpoints and
copies mutable inputs. It contains no physics world or delegate that queries one.

An additive GridPathPlanner constructor accepts the graph with its NavSpace. Endpoint snapping,
same-layer shortcuts, A* edges, links and waypoint reconstruction use it consistently. The radius
argument must match the baked profile. Its node tests already include capsule clearance, so this path
uses raw grid passability without applying the agent radius a second time. Legacy clearance-grid
queries retain their existing radius-aware tests.

Keep physics-profile paths on their validated cell-centre edges initially. Disable string pulling
for that overload. A smoothed segment can cut an obstacle that all its crossed columns miss, even
when each graph edge is valid. Existing unguarded planners keep their smoothing. Near-field direct
movement has its separate, live checked seam below. This preserves correctness without introducing
a second approximate wall rasterizer.

Add `NavLayerBaker.BakeGroundedLayered(columns, bounds, cellSize, stepHeight, agentHeight,
maxSurfacesPerColumn, extraBlocked)` as a named API with the existing four bound floats in its actual
signature. Share column validation and layer extraction with the existing layered bake. Generate only
walked Stair links. It takes no unused jump-height or hop-cost argument. No height-map, hop-bake or
point-planner default changes in this branch.

## 5. Physics bake and game-owned area masks

```csharp
public readonly record struct NavAreaFilter(uint Required, uint Excluded);
public delegate uint NavAreaClassifier(Vector3 absoluteFeetPosition);

public sealed class PhysicsNavBake : IDisposable
{
    public static PhysicsNavBake Capture(GroundMoveContext context, PhysicsNavBakeOptions options,
        NavAreaClassifier classify);
    public GroundNavigation BuildProfile(in MoveTuning tuning, NavAreaFilter areas);
    public void Dispose();
}

public sealed class GroundNavigation
{
    public NavSpace Space { get; }
    public IRegionPathPlanner Planner { get; }
    public float AgentRadius { get; }
    public float AgentHeight { get; }
    public bool AllowsSegment(Vector3 fromFeet, Vector3 toFeet);
}
```

Capture requires a populated static physics world and the same ground and normal providers used by
movement. It freezes the bounds, cell lattice, origin, surface samples and area tags, sampling through
`PhysicsColumnProbe` in canonical Z/X order. Keep geometry capture separate from profile filtering so
player, cow and aquatic profiles share the expensive column pass. BuildProfile requires the same
unchanged statics and origin as Capture for its occupancy and edge validation. Dispose the capture
after constructing the profiles. GroundNavigation retains only baked data and needs no physics for
path queries. Callers rebuild after a world edit and replace whole profiles between ticks.

`PhysicsNavBakeOptions` has explicit finite absolute min/max bounds, positive cell size, probe height
and range, slope limit, sample cap and a maximum cell count. Validate dimensions with checked
arithmetic before allocating. A surface cap overflow fails with the column, rather than dropping a
deck silently. Detect overflow by sampling cap plus one, subject to the same bounded allocation.
Missing columns return no standable surface. They do not consult the ground sampler's nearest-tile
fallback to manufacture nav beyond drawn ground.

At a nonintegral final row or column, samples outside the supplied half-open bounds are blocked.
An exact outer-edge ray miss is accepted as an absent column, per round 1 B14. Do not nudge that ray
outward or fill from its neighbor. Negative coordinates and interior region seams still need proofs.
Converting to physics subtracts the frozen world origin at the probe seam and converts hit heights
back to absolute Y. Runtime ground contexts read the live origin on every step, per B11.

For a profile, require `(tags & Required) == Required` and `(tags & Excluded) == 0`. A zero tag is
ordinary unclassified ground. The engine defines no CowPen or Water enum. Grimhollow classifies its
authored pen and calls its medium sampler at the surface's feet height, so a bridge deck over water
can be dry while the river bed below is water. Cows require the pen tag and exclude water as game
policy. Aquatic policy requires water. Player policy permits the river and wades through normal
movement. The game retains any habitat padding needed beyond the physical capsule.

Column standability is only a candidate. Reject a node whose actual capsule cannot hold on that
surface, including a floor reported underneath a solid convex object. Validate each candidate edge
in each direction with the existing CharacterMovement ground core and no jump, so a fence between
columns and a nonclimbable rise cannot become an adjacency. The validator uses normalized geometric
probe tuning, dry medium and speed scale 1, leaving capsule, slope, step and gravity rules intact.
Use walk/run speed 1 m/s and 1/30 s steps, capped at 64 steps per edge. These are bounded probe
parameters, not NPC pace. Reject an edge that cannot reach its neighbor within 1 mm in that budget.
The options expose these bounds so a larger geometric step class can select them deliberately.
It also rejects every intermediate footprint that violates the
area policy. No direct position writes, no lift to the candidate's floor and no copied collide-and-slide.

Cache symmetric occupancy data, but never assume directed up/down edges are interchangeable.
Graph links receive the same validation. Query paths are cell-centre corridors, so every commanded
edge corresponds to one that was checked. `AllowsSegment` is the baked area/graph guard. The kernel
also resolves any direct movement through the live movement context before an NPC result is accepted.

Profile validation checks radius, half-height, slope and step compatibility with the movement tuning.
All profiles from a capture use its slope limit. Different slope classes require separate captures.
Validate the eventual layer-by-cell allocation budget before materializing profile grids as well as
the initial XZ cell count. A small sample cap does not bound a dense layer allocation by itself.
Clearance is tied to a capsule geometry class, not an individual NPC. Profiles can share surface
arrays. The consumer chooses the bake bounds and cell size from its content and doorway needs.
A 0.25 m cell is the proposed Hollowmere acceptance fixture, with a 0.3 m player radius and a 1 m
doorway. That is a test fixture, not an engine gameplay default. The full-world bake has a finite
cell budget. Report startup cost and memory on the authorized implementation run before adoption.

## 6. Shared range steering and two adapters

```csharp
public sealed class GroundMoveContext
{
    public GroundMoveContext(Func<float, float, float> groundHeight,
        Func<float, float, Vector3>? groundNormal = null, IPhysicsWorld? physics = null,
        Func<float, float, Vector2>? clampXz = null,
        Func<float, float, float, MovementMedium>? medium = null);
}

public enum RangeMoveStatus
{
    Following, InRange, WaitingForPath, Unreachable, UnsupportedTransition, Suspended
}
public readonly record struct RangeSteering(Vector2 WorldDirection, RangeMoveStatus Status);

public sealed class MoveToRange
{
    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow = null);
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow = null);
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target,
        float range, bool run, float dt, GroundMoveContext context);
    public void Reset();
}

public static class NpcGroundMovement
{
    public static MoveState Step(in MoveState body, in RangeSteering steering,
        bool run, float dt, in MoveTuning tuning, GroundMoveContext context);
    public static MoveState Hold(in MoveState body, float dt,
        in MoveTuning tuning, GroundMoveContext context);
}

public static class PlayerPathMovement
{
    public static MoveCommand Command(in RangeSteering steering, bool run, float cameraYaw);
}
```

The ground context forwards height, normal, physics, bounds and medium through StepTowards's new
precise-direction overload. Ruinborne's NpcGroundMove currently omits the medium argument despite its
documentation. That mismatch is tracked at https://github.com/APKiwiOrg/Ruinborne/issues/561.
Lift the useful shape, not that omission, its FrostChill tuning or its NpcAgent type.
The game passes per-archetype and per-effect tuning. Neither adapter mutates facing to a target.
Hold calls the core with zero steering and still settles gravity and support.

Tick receives an immutable target snapshot for this simulation tick. First validate inputs and the
profile, then validate and evaluate current shape reach. If an eligible grounded body is in range, return zero
requested horizontal motion and `InRange`. For an airborne or committed body, report `Suspended` in preference
to `InRange` with zero requested input after that validation and reach evaluation. Otherwise tick the region
follower with this body's feet. Route waypoints also use feet,
and a target capsule's feet come from its own half-height, not the mover's.

Far-field movement follows the validated corridor. Cap the requested travel to the active waypoint
so a fast tick cannot skip a required turn. Near-field movement approaches the closest target shape
along a straight segment only when its graph/area guard permits that segment. Test the segment's
actual ground-step resolution before using it. A blocked direct approach leaves the planned detour
active. A planner-less unrestricted press is not the default of this API.

Shrink the requested direction magnitude on the last approach to the nominal stop ring. Use a
conservative per-tick travel bound that includes the body's speed scale and the ground core's pace,
rather than copying Ruinborne's bare tuning-speed formula. Medium slowing may make a step shorter,
which is safe. Retest shape reach after movement at the consumer boundary. `InRange` describes the
observed position, not the proposed command's expected destination. A server check can still reject
a target that moved after the client stopped, using its catalog tolerance exactly once.

Automation is ground steering. An airborne or committed-movement body returns Suspended with zero
requested input until ground movement resumes. It does not cancel a server-authored
commitment or brake an airborne momentum arc by editing state. Gravity or a collision correction can
move a holding body. The game checks the resulting position and owns cancellation or recovery.
The no-overshoot stop-ring promise applies to commanded grounded approach, not external impulses.

PlayerPathMovement uses the inverse of the engine's existing camera basis, where yaw 0 forward is
world -Z. It preserves direction magnitude through `ScaleSpeedByAxis=true`, sets `Jump=false` and
`FaceCamera=false`, and passes the requested pace. Test the result through
`CharacterMovement.CameraRelativeDir`, real command encode/decode and both prediction and server
simulation. Do not use Ruinborne's separate visual-yaw convention as the command basis.

Waiting, unreachable, unsupported transition and exhausted partial corridors waiting on cooldown return zero
requested input and preserve normal follower cooldowns. A valid partial corridor continues bounded travel until
it is exhausted. The game chooses retry, leash, an error message or interaction cancellation. It also owns target
identity and lifecycle. On target replacement, teleport or manual steering, reset before the next tick. Shape,
dimensions, box yaw and range changes reset internally. A changed immutable profile requires a new follower
instance.
The kernel holds no target ids, world entities, wall clock, random generator or action queue.

## 7. What stays in the games

| Engine | Grimhollow |
| --- | --- |
| Numeric reach shapes and distances | ReachVerb mapping, target shapes, catalog reach and tolerance, action legality |
| Ground step, steering, route state and stop ring | Wander destinations, spread, leash, aggro, retaliation, conversation freeze |
| Physics-column snapshot and guarded nav profiles | Authored bounds, pen geometry, water classification and habitat padding |
| A command-producing client path adapter | Walk to targets setting, click intent, pending action, target death and manual steering cancellation |
| Travel facing from CharacterMovement | Turn to a target on swing, hold facing, animation selection and target selection |

Combat accuracy, damage, preparation, cadence, tab order and journal operations are untouched.
There is no ground click-to-move feature. This round neither adds quest content nor changes durable ids.

## 8. Test strategy and the round 1 lessons

Tests run only after the owner frees the build slot. Focused red/green runs belong to the relevant
task. One full Release build and suite run finishes each implementation branch, after reconciliation.
No local repetitions, stress load or concurrent builds. A future hosted stress test still needs
separate explicit permission. No windowed consumer launch is part of this design session.

| Proof | Required scenarios | Owner |
| --- | --- | --- |
| Shape distance | Capsule/capsule, rotated box faces and corners, point, overlap, exact threshold, just outside tolerance, differing heights and invalid values | Movement.Tests |
| Command contract | Legacy axis behavior and bytes unchanged with flag off, fractional final step survives codec, hostile axes clamp speed, idle and replay copy sites | Game.Tests Locomotion and Server.Tests NetWorld |
| Region query | Solid target centre blocked, nearest face inaccessible but far face reachable, snap outside range, zero range, partial budget, moving target and exact region membership | Game.Tests Navigation |
| Follower | Partial completion holds, final waypoint never implies range, small/large bodies use their own feet, target changes reset, replan cooldown, unsupported Hop | Navigation unit tests and Movement.Tests |
| Physics navigation | 0.1 m wall between sample columns, door passes for player but refuses larger body, under-solid false floor, step up/down, no Hop links, bridge over water, NoDraw/void and outer-edge miss | Movement.Tests and TileWorld.Physics.Tests |
| Mask consistency | Required and excluded bits, body footprint across a habitat edge, masked goal snapping, diagonal edges, direct final approach and consumed partial path cannot escape a pen | Movement.Tests |
| Coordinates | Negative bounds, origin with X/Y/Z offset, origin change detected during capture/profile build, runtime rebase wrappers and equivalent baked graph | Movement.Tests and bridge acceptance |
| NPC path | Around a wall to target range, settle on hold, wade with medium forwarded, stop-ring overshoot at high speed and speed scale, invalid route reports without teleport | Movement.Tests |
| Real player path | Client helper -> Predict/SendInput -> 18-byte codec -> normal server queue -> PlayerMoveSimulator -> reconciliation, small final fraction, moving target, idle stop | Server.Tests with Movement reference |
| Compatibility | Null graph and existing point/hop path fixtures unchanged, exact reference graph, no backend in kernel, generation-12 stamped and unstamped cell payloads preserved | Existing suites plus focused new tests |

No test needs Grimhollow assets or an authenticated service. Author small synthetic worlds using the
same TileWorldColliders, TileGroundSampler and TileMediumSampler APIs Grimhollow uses. Capture legacy
fixtures from unchanged base code first. Assert final position, feet, range and transmitted command,
not only an internal follow-state enum. A run reporting zero matching tests is a failure.

| Round 1 outcomes | Guard before the first implementation task |
| --- | --- |
| A1, A3, A5 | Verify test-project dependency direction and every actually shipped signature. No unused public arguments. Link downstream plans to the producing branch's Outcome. |
| A2, B7, C1 | Allocate one build slot, validate public options and dimensions, inspect touched file headroom and put each new concern in its own cohesive file. |
| A4, B1, B6, B14 | Exercise actual physics geometry, thin walls, doorway dimensions, exact ground triangles and edge misses. No parallel copy of collider or triangulation rules. |
| B9 to B13, B16 | Feet-based water checks, explicit absolute/local coordinate seams, rebuild snapshot lifetime, deterministic ordering and input immutability. |
| C3, C5 to C9 | Audit every command-copy site and count one steering command per simulation tick. State the camera basis and unit conversions. Prove the real consumer path. |
| D1 to D3 | Pin the unchanged baseline and exercise temporal transitions, including target motion and cancellation inside a replan cooldown. Verify overloaded APIs receive the right world/profile. |
| D4, D5 | Keep replication-baseline defects #1229 and #34 out of E4. New command flags need handshake and persistence compatibility proofs rather than assuming unchanged snapshots are enough. |

The implementation plans name exact test files and filters after design approval. A planned new test
is labelled new. Existing test names are verified against the repository before being used in a
verification command. No outcome says a test ran unless its output and nonzero count were inspected.

## 9. Sequencing, version and handoff

The owner approved this written design on 2026-10-02. Four implementation plans are written:

1. [Round 2 A](../superpowers/plans/2026-10-02-round2-a-command-fraction.md), branch `feature/round2-command-fraction`. Opt-in axis scaling, codec,
   wire handshake and persistence compatibility. Opens the round's selected version through its
   integration owner, nominally 20.18.0. No worker selects a different bump on its own.
2. [Round 2 B](../superpowers/plans/2026-10-02-round2-b-navigation-contracts.md), branch `feature/round2-navigation-contracts`. Goal-region search,
   region follower, guarded traversal graph and grounded layered bake. Rides the same version.
3. [Round 2 C](../superpowers/plans/2026-10-02-round2-c-reach-physics-nav.md), branch `feature/round2-reach-physics-nav`. Movement package and shape
   reach, physics capture, area filters, capsule profiles and bridge acceptance. Needs B and rides.
4. [Round 2 D](../superpowers/plans/2026-10-02-round2-d-movement-drivers.md), branch `feature/round2-movement-drivers`. Shared range steering, NPC
   and player adapters, complete real-command-path acceptance and consumer docs. Needs A, B and C.

Each branch starts from current reconciled engine main, consumes its prerequisite's verified Outcome,
and finishes with review, main reconciliation and one full verification. The implementation owner
records every departure, its reason and cost if wrong in that plan's Outcome before landing it.
Integration, feed packing and any later release are serialized with the build slot.

At design time engine main and v20.17.0 both point to `e585b8a01`. Before A started, the integration owner
reconciled current main and tags and selected 20.18.0, with the matching changelog and guarded version declarations.
The remaining plans ride that selected version and never bump independently.

Only the owner starts `scripts/tag-release.sh`. Do not tag because P5 is waiting, and do not repin a
consumer to untagged local packages. After release, Grimhollow's P4 adoption moves its engine pin and
both tools, refreshes the vendored feed and records the sweep on `feature/continuous-movement`.
P3 keeps its current engine API until that adoption. P5 defines and wires its target shapes and nav
profiles, replaces actor traversal maps, and implements client walk-up cancellation. P6 keeps combat
rules and server tolerance in the game. Report those integration inputs on epic #399.

## Out of scope

- Grimhollow P3, P5 or P6 implementation and any Grimhollow file change.
- Ruinborne brain, FrostChill, lunge, archetype or nav adoption changes.
- Dynamic avoidance, body-to-body collision, crowds and automatic hopping or jumping.
- Nav streaming, incremental edit-time rebakes, navigation persistence and large-world stress work.
- Unreliable deltas, replication baseline redesign, facing or line-of-sight combat rules.
- Release tags, a game version bump and any player changelog entry for this docs session.

## Review state

- The design is approved and four implementation plans are written. Plan A is complete and verified at staged
  20.18.0 with wire generation 13. Plan B code and docs are complete, its task and whole-branch reviews are
  approved, and full branch verification passed. Plan C code, bridge acceptance and docs are complete, with
  full Release verification, all five guards and whole-branch approval. Plan D implementation, scoped reviews and
  full Release verification are complete. Whole-branch review approved after its scoped comment and status fix.
  Root finishes delivery. Tagging and consumer adoption remain pending.
  The owner authorized the whole round program.
  Engine tagging and consumer adoption remain pending.
- D2 and the technical decisions are settled by written-design approval.
- Plan A focused evidence is 28, 99, and 6 focused plus 10 adjacent passing cases, with 76 game and 52 server cases
  for the final helper fix. Final full Release verification built with zero warnings and ran 28 test assemblies with
  23,042 passed, 0 failed and 1,269 skipped. Final whole-branch review and scoped fix review are approved.
- The owner released Grimhollow P3's build slot and authorized the whole round program. One building worker runs at a time.
- Plan B focused evidence is 107 grounded-bake cases, 120 traversal and legacy planner cases, 69 region and
  planner cases, and 63 region and legacy follower cases. The Task 4 RED briefly overlapped an unrelated
  Grimhollow build before the result was inspected. No external process was touched. The controller then
  cleared the focused GREEN separately, with 63 passed and zero failures. Branch full Release verification
  later built with zero warnings and ran 28 assemblies with 23,208 passed, 0 failed, 1,275 skipped, and
  24,483 total cases. Whole-branch review approved with no findings and all five guards passed. The branch
  is integrated on engine main at `4431781f6` with canonical packaging refreshed. Tagging remains owned by the owner.
- Plan C focused evidence is 73 reach cases, 31 architecture cases, 60 context cases, 54 capture cases, 45
  profile cases and 11 bridge cases. The normal bridge fixture recorded one 33.806 ms `BuildProfile` observation
  with 16 accepted nodes, 84 directed exits and zero accepted or candidate links. Root's full Release evidence
  against the exact committed source built with zero warnings and errors and ran 29 assemblies with 23,454 passed,
  0 failed, 1,275 skipped and 24,729 total cases. All five guards passed, whole-branch review approved without
  findings and a final current-main reconciliation required no changes. Existing issues 1233 and 1238 remain
  outside scope, and no NPC or player driver API is claimed by Plan C.
- Plan D focused evidence is 33 range and corridor cases plus 105 adjacent profile and reach cases for Task 1,
  26 NPC adapter and physics cases for Task 2, 38 player command cases for Task 3, and 7 acceptance plus 8
  adjacent reconciliation cases for Task 4. All four task reviews approved without findings. The full Release
  verification against the reconciled source built with zero warnings and errors and ran 29 assemblies with
  23,580 passed, 0 failed, 1,275 skipped and 24,855 total cases. All five final guards passed. First failed
  focused runs were fixture assumptions or expected missing API RED runs and were corrected without a runtime
  kernel fix. No stress or repeated full run was used.
- Plan D's reconciled main includes concurrent fixes for navigation containers, sharded slot lookup and
  `PhysicsColumnProbe` representable progress. Issue [#1238](https://github.com/APKiwiOrg/KhaozEngine/issues/1238)
  is resolved outside Plan D. Issue [#1233](https://github.com/APKiwiOrg/KhaozEngine/issues/1233) remains an
  adoption prerequisite for steep meshes and filtered ground. Small local physics coordinates or rebasing remain
  required for physics precision, and no full Hollowmere startup guarantee is claimed.
- Plan D whole-branch review approved through `9f4cb6da0` after one scoped fix wave. A final main reconciliation
  included only unrelated item-design documentation through `4ba0ee9e9`. Runtime source remains identical to the
  full verified source. Root merges, pushes and refreshes the canonical feed as the normal finish. Only the owner
  starts an engine tag, followed by released-pin game adoption.
