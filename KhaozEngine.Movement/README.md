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
