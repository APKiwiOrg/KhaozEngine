# KhaozEngine.Movement

Opt-in composition of Locomotion, Navigation and Physics. Add this package explicitly to a client or
server that needs body-aware movement geometry, absolute-coordinate ground movement, or a physics-backed
ground navigation profile. It stays outside every umbrella and carries no backend, input or rendering
dependency.

## Exact 3D reach

`MovementBody` is an upright capsule in absolute metres. `Centre` is the capsule centre, `Radius` is
positive, and `HalfHeight` includes the rounded ends and must be at least the radius. All values must be
finite. The capsule's vertical axis extends `HalfHeight - Radius` above and below its centre.

`ReachTarget.Capsule(in MovementBody)` preserves the target's own dimensions.
`ReachTarget.Box(Vector3 centre, Vector3 halfExtents, float yawRadians = 0f)` uses exact box faces and
corners. Its X/Z half-extents must be positive, its Y half-extent may be zero, and all inputs must be
finite. Positive yaw rotates local +X toward world -Z, matching
`Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians)` on a physics pose.
`ReachTarget.Point(Vector3 position)` accepts any finite position, including zero. Default bodies and
targets are invalid and operations refuse them with `ArgumentException`.

```csharp
using System.Numerics;
using KhaozEngine.Movement;

var actor = new MovementBody(new Vector3(0f, 0.75f, 0f), 0.25f, 0.75f);
var other = new MovementBody(new Vector3(1.5f, 0.75f, 0f), 0.25f, 0.75f);
var target = ReachTarget.Capsule(other);
float distance = ReachGeometry.Distance(actor, target); // 1 metre between edges
bool reached = ReachGeometry.Within(actor, target, 0.75f, tolerance: 0.25f);
```

`Distance(in MovementBody, in ReachTarget)` returns the shortest 3D edge distance, clamped to zero for
overlap. It retains compensated relative components and squared-product residuals until the signed
distance numerator has cancelled. This preserves local gaps beside large coordinates, dimensions and
radii, including tiny positive distances near a huge sphere's tangent. It refuses results above `float.MaxValue` with
`ArgumentOutOfRangeException` before returning a float. `Within(in MovementBody, in ReachTarget,
float range, float tolerance = 0f)` compares the signed squared-distance residual against the expanded
radii plus range and tolerance. The float-limit check uses the same metric before narrowing the result.
`Distance` rationalizes the retained numerator and rounds only its final float result. Use `Within` for
boundary decisions rather than comparing the rounded `Distance` result to a range.
Both threshold inputs must be finite and nonnegative. Addition is checked against the larger operand's
remaining headroom, including a tiny positive operand added to `float.MaxValue`. Their sum must fit the finite float range or
`Within` throws `ArgumentOutOfRangeException`. There is no gameplay epsilon. A distance above the
finite float range simply fails a valid `Within` query.

## Absolute ground movement context

`GroundMoveContext` adapts the shared `CharacterMovement.StepTowards` core to absolute world providers and a
caller-owned physics world. Its public constructor is:

```csharp
public GroundMoveContext(
    Func<float, float, float> groundHeight,
    Func<float, float, Vector3>? groundNormal = null,
    IPhysicsWorld? physics = null,
    Func<float, float, Vector2>? clampXz = null,
    Func<float, float, float, MovementMedium>? medium = null);
```

The read-only `GroundHeight`, `GroundNormal`, `Physics`, `ClampXz` and `Medium` properties retain those
providers. Ground height, normal, clamp bounds and medium coordinates are absolute. Physics poses and queries
are local to `IPhysicsWorld.Origin`. A step changes only `MoveState.Position` into that local frame and back.
Velocity, facing, effect scale, commitment and timers keep their carried values. A water surface returned by
`Medium` is converted from absolute Y to the local frame for the core.

The context reads and freezes the current origin for one sequential step, checks it at every provider boundary,
and rejects a rebase or recursive step during that step. Rebase only between steps. The context does not step,
dispose or rebase the physics world. The caller owns the world, statics and sequential lifetime. Delegate wrappers
are cached once when the context is constructed. The internal adapter validates the actual ground and medium
controls, including a positive finite radius, a half-height of at least `max(0.1, radius + 0.005)`, a finite
capsule length, a slope below `pi / 2`, ordered medium thresholds and finite nonnegative controls. The permitted
default `FacingTurnSpeed = float.PositiveInfinity` remains valid.

The context does not expose a public `Step` method. Its internal adapter is the shared frame seam used while a
profile is being built.

## Bounded static physics capture

`PhysicsNavBakeOptions` is an immutable record with this exact parameter list and defaults:

```csharp
public sealed record PhysicsNavBakeOptions(
    float MinX, float MinZ, float MaxX, float MaxZ, float CellSize,
    float ProbeHeight, float ProbeRange, float MaxSlopeRadians,
    int MaxCells, int MaxLayerCells, int MaxSurfacesPerColumn = 4,
    float EdgeProbeSeconds = 1f / 30f, int MaxEdgeProbeSteps = 64);
```

Bounds are absolute, half-open XZ bounds. `ProbeHeight` is absolute Y. Cell, layer, sample and edge controls
are finite, positive and checked before arrays are allocated. `MaxCells` bounds captured columns and
`MaxLayerCells` bounds the later grounded layer extraction. These storage and movement-slice budgets do not
promise a column query time bound.

Area policy is supplied by the immutable `NavAreaFilter(uint Required, uint Excluded)`. Every required bit must
be present, every excluded bit must be absent, and overlapping masks are refused. A
`NavAreaClassifier(Vector3 absoluteFeetPosition)` assigns caller-defined `uint` tags at absolute captured feet
positions.

```csharp
public sealed partial class PhysicsNavBake : IDisposable
{
    public static PhysicsNavBake Capture(
        GroundMoveContext context,
        PhysicsNavBakeOptions options,
        NavAreaClassifier classify);

    public void Dispose();
}
```

Capture requires a populated `GroundMoveContext.Physics` world and samples static physics through
`PhysicsColumnProbe`. It freezes the physics origin, queries local coordinates, converts hit heights back to
absolute Y, and samples in canonical Z then X order. Every captured surface stores its absolute height,
headroom and area tags. The probe uses a `MaxSurfacesPerColumn + 1` buffer so an over-cap column is refused.
Missing columns, padded centers, and exact outer-edge misses remain empty and therefore blocked. Capture never
fills a miss from the analytic ground provider. The classifier is not retained by the captured data.

Keep statics and the origin unchanged while constructing profiles. `Dispose` releases the retained context
reference only. It never disposes statics, providers or the physics world. A built profile owns immutable
captured columns and remains usable after the builder and its world are disposed.

## Capsule checked ground profiles

Build one immutable profile for a capsule geometry and an area policy:

```csharp
public sealed partial class PhysicsNavBake
{
    public GroundNavigation BuildProfile(
        in MoveTuning tuning,
        NavAreaFilter areas);
}
```

The resulting `GroundNavigation` exposes exactly this public surface:

```csharp
public sealed class GroundNavigation
{
    public NavSpace Space { get; }
    public IRegionPathPlanner Planner { get; }
    public float AgentRadius { get; }
    public float AgentHeight { get; }
    public bool AllowsSegment(Vector3 fromFeet, Vector3 toFeet);
}
```

`AgentRadius` and `AgentHeight` are the baked radius and full capsule height. Profile geometry must match
`CapsuleRadius`, `CapsuleHalfHeight`, `MaxSlopeRadians` and `StepHeight` exactly when a context validates a
tuning. Walk and run pace, climb pace and effect scale may differ at runtime. The directed proof uses unit
walk and run pace, dry medium, grounded state, no jump, no airborne momentum and no movement commitment.
The initial hold is slice one of the bounded probe. The default is `1 / 30` seconds and at most 64 core calls.
Arrival uses a 1 mm proof tolerance. This is a bounded proof tolerance, not a gameplay reach epsilon.

The profile filters the whole circular footprint against captured columns and their area tags. A centre point
or bounding square is not enough. It uses raw radius-zero grid checks because the capsule was already checked
by the physical hold and directed core proof, so the radius is not eroded twice. The grounded bake allocates
within `MaxLayerCells`, produces only `Stair` links, and never generates `Hop` links. Candidate links in
`Space.Links` are separate from the accepted links retained by the guarded planner. `Planner` and
`AllowsSegment` use the accepted graph and exact profile radius, and routes keep unsmoothed cell-centre
waypoints.

`AllowsSegment` is a pure endpoint and segment guard. It rejects unknown, padded, off-grid and incompatible
height endpoints, checks every footprint cell and directed crossed edge, and only admits an accepted cross-layer
Stair link. It checks a complete segment Y interval conservatively, so a direct sloped shortcut can be refused
even when a sequence of baked edges is eligible. Live collision, support and the final movement result still
come from the shared movement core.

## Bridge evidence and limits

The real TileWorld bridge covered 11 focused cases through static colliders, `PhysicsNavBake` and the public
`GroundNavigation` surface. One normal 4 by 4 metre flat fixture produced this single observation:

```text
BuildProfile: 33.806 ms
Bounds: X=[0,4), Z=[-4,0), cellSize=1, probeHeight=5, probeRange=10
Options: maxSlopeRadians=0.8, maxCells=128, maxLayerCells=512,
         maxSurfacesPerColumn=4, edgeProbeSeconds=1/30, maxEdgeProbeSteps=64
Tuning: radius=0.2, halfHeight=0.75, stepHeight=0.4, slope=0.8
Stored: one layer, 16 node slots, 16 accepted nodes, 84 directed exits,
        0 accepted links, 0 candidate links
```

The timing covers `BuildProfile` only and is one observation, not a startup or wall-clock guarantee. The
bridge uses small authored surfaces. Existing issues [#1233](https://github.com/APKiwiOrg/KhaozEngine/issues/1233)
and [#1238](https://github.com/APKiwiOrg/KhaozEngine/issues/1238) remain outside this scope. A filtered P3
world without populated ground statics cannot supply capture ground, and small local physics coordinates or
rebasing are required for large absolute positions. There is no ground-sampler fallback and no claim of full
Hollowmere or steep-bank coverage from this profile.

## Range steering and movement drivers

`MoveToRange` is the shared ground follower for an NPC or a client automation request. It uses the current
body's capsule and the target's observed shape to decide whether the body is in range. The mover's
`MoveState.Position` is its capsule centre. Navigation and route waypoints use the mover's own feet, and a
capsule target supplies its own feet from its own half-height. Reach has zero tolerance in this API. The tick
validates and evaluates current reach, then an airborne or committed body reports `Suspended` in preference to
`InRange`, with zero requested input. `InRange` means the supplied current body already passes
`ReachGeometry.Within`, never that a waypoint or predicted endpoint would.

The public driver surface is:

```csharp
public enum RangeMoveStatus
{
    Following, InRange, WaitingForPath, Unreachable, UnsupportedTransition, Suspended,
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

`MoveToRange` copies the supplied `PathFollowConfig` and clamps `AcceptRadius` to at most `0.00001f`,
including when the supplied value is zero. Its near-ring preflight resolves copies of `MoveState` through the
live context, with a bounded bisection of up to 32 candidates plus endpoint and final checks. It performs no
world step and writes no entity state. The command cap includes the requested walk or run pace, the body's
`SpeedScale`, and medium boosts above one. A slowing medium only shortens the core's resulting travel. The
follower uses `GroundNavigation` or an equivalent guarded planner, so an area or graph refusal, an exhausted
partial route waiting on cooldown, an unreachable or refused route, or a `Hop` waypoint returns zero requested
input and never becomes an unrestricted press. A valid partial corridor still requests bounded travel until it is
exhausted. A blocked near-field shortcut keeps the detour. Target translation follows the follower's configured
drift and replan cooldown while the route remains valid.

`NpcGroundMovement.Step` and `Hold` call the shared `GroundMoveContext` once. A consumer publishes the returned
state once per simulation tick and rechecks current shape reach after the step. The adapters contain no NPC
brains, archetype values, target-facing rules, action queue, combat rules or server-side player following. The game
chooses nominal ranges, target validity, cancellation and any server tolerance, and applies that tolerance once
at its authoritative boundary. Shape kind, dimensions, box yaw and range changes reset internally. A changed
immutable profile requires a new `MoveToRange` instance. Call `Reset` for a target identity change, teleport,
manual input, target death or invalidity, or cancellation. The strict `AcceptRadius` cap can hold at float
resolution, including when the caller supplies zero. The profile must match radius, half-height, slope and step
for the tuning it serves. Walk and run pace, climb pace and effect scale can differ.

An NPC keeps the physics world, providers, tuning, body and target snapshots. The area mask is caller-owned
`uint` policy:

```csharp
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;

// These are the game's existing world providers and caller-owned physics lifetime.
IPhysicsWorld physicsWorld = gamePhysics.World;
Func<float, float, float> groundHeight = gameGround.Height;
Func<float, float, Vector3> groundNormal = gameGround.Normal;
Func<float, float, Vector2> clampXz = gameBounds.Clamp;
Func<float, float, float, MovementMedium> medium = gameMedium.Sample;
Func<Vector3, uint> classifyAreas = gameAreas.ClassifyAbsoluteFeet;

var context = new GroundMoveContext(
    groundHeight, groundNormal, physicsWorld, clampXz, medium);
var options = gameNavigation.ProfileOptions;
uint requiredAreas = gameAreas.NpcRequiredMask;
uint excludedAreas = gameAreas.NpcExcludedMask;
MoveTuning npcTuning = gameTuning.Npc;
GroundNavigation profile;
using (PhysicsNavBake capture = PhysicsNavBake.Capture(
    context, options, feet => classifyAreas(feet)))
{
    profile = capture.BuildProfile(
        npcTuning, new NavAreaFilter(requiredAreas, excludedAreas));
}

var follower = new MoveToRange(profile);
MoveState npcState = gameNpc.InitialMoveState;
const float dt = 1f / 30f;

// Run this once for each simulation tick. The consumer owns the current snapshots.
MovementBody npcBody = new(npcState.Position,
    npcTuning.CapsuleRadius, npcTuning.CapsuleHalfHeight);
Vector3 npcFeet = npcBody.Centre
    - new Vector3(0f, npcTuning.CapsuleHalfHeight, 0f);
MovementBody targetBody = gameTarget.BodySnapshot;
ReachTarget target = ReachTarget.Capsule(in targetBody);
RangeSteering steering = follower.Tick(
    in npcState, in npcTuning, in target, gameNpc.NominalRange, gameNpc.Run, dt, context);
MoveState next = steering.Status == RangeMoveStatus.Following
    ? NpcGroundMovement.Step(in npcState, in steering, gameNpc.Run, dt, in npcTuning, context)
    : NpcGroundMovement.Hold(in npcState, dt, in npcTuning, context);
npcState = next;
gameNpc.PublishMoveState(npcState); // one publication for this tick
MovementBody acceptedBody = new(npcState.Position,
    npcTuning.CapsuleRadius, npcTuning.CapsuleHalfHeight);
bool acceptedReach = ReachGeometry.Within(
    in acceptedBody, in target, gameNpc.NominalRange); // game applies tolerance once on authority

// When target identity, teleport, manual input, target death or invalidity, or cancellation changes:
follower.Reset();
```

The capture can be disposed after `BuildProfile`. The profile owns its immutable navigation data. The game
still owns `context`, `physicsWorld`, static providers and their sequential lifetime for each live step.

For a client path, use the predicted simulation state for geometry and the render state only for presentation.
`PlayerPathMovement.Command` uses the engine camera basis where yaw zero faces world negative Z. It sets
`ScaleSpeedByAxis` true, `Jump` false and `FaceCamera` false. `CharacterMovement.CameraRelativeDir` reports the
unit world heading represented by the axes. The actual step consumes the preserved axis fraction through the
ordinary command path. `run` remains caller-owned. Every stopped, unknown, unsupported or nonfinite request is a
finite idle command. A nonfinite camera yaw is encoded as zero, and a finite direction longer than one is clamped
to unit length before its fraction is projected into the camera basis.

```csharp
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.NetWorld;

WorldClient client = gameClient; // connected normal WorldClient
MoveTuning playerTuning = gameTuning.Player;
GroundNavigation playerNavigationProfile = gameNavigation.PlayerProfile;
GroundMoveContext gamePlayerGroundContext = gameWorld.PlayerGroundContext;
MoveToRange follower = new MoveToRange(playerNavigationProfile);
const float dt = 1f / 30f;

// These flags and the manual command are caller-supplied game state.
bool manualInputWins = gameInput.ManualMoveActive;
MoveCommand manualCommand = gameInput.ManualCommand;
bool automationCancelled = gameTarget.Cancelled || gameTarget.Dead || !gameTarget.IsValid;
MoveCommand command;
if (manualInputWins)
{
    follower.Reset();
    command = manualCommand;
}
else if (automationCancelled)
{
    follower.Reset();
    command = PlayerPathMovement.Command(
        new RangeSteering(Vector2.Zero, RangeMoveStatus.InRange), false, gameCamera.Yaw);
}
else
{
    // Automated branch. It is mutually exclusive with manual input and cancellation.
    PlayerMoveState predicted = client.LocalPredictedState;
    MoveState body = predicted.Move; // absolute capsule centre for this tick
    MovementBody bodyShape = new(body.Position,
        playerTuning.CapsuleRadius, playerTuning.CapsuleHalfHeight);
    Vector3 bodyFeet = body.Position
        - new Vector3(0f, playerTuning.CapsuleHalfHeight, 0f);
    ReachTarget target = gameTarget.CurrentShapeSnapshot;
    bool observedReach = ReachGeometry.Within(
        in bodyShape, in target, gameTarget.NominalRange);
    RangeSteering steering = follower.Tick(
        in body, in playerTuning, in target, gameTarget.NominalRange,
        gameTarget.Run, dt, gamePlayerGroundContext);
    command = PlayerPathMovement.Command(
        in steering, gameTarget.Run, gameCamera.Yaw);
}

client.SendInput(in command); // exactly one normal submission per simulation tick

PlayerMoveState presentation = client.LocalRenderState;
gameAvatar.DrawAt(presentation.Move.Position); // presentation only
```

The game owns target identity and validity, nominal range, run choice, cancellation and the authoritative
server reach check. Player automation emits ordinary client commands, and the authority simulates those commands.
There is no server-side player following or action queue. A server consumer reads the authoritative body, applies
its game tolerance once, and decides whether an action is still legal. Missing capture columns and exact outer-edge
misses stay blocked. Existing issues [#1233](https://github.com/APKiwiOrg/KhaozEngine/issues/1233)
and [#1238](https://github.com/APKiwiOrg/KhaozEngine/issues/1238) remain caveats. This package has no full
Hollowmere proof or fix and makes no consumer adoption or release-tag claim.
