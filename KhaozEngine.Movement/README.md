# KhaozEngine.Movement

Opt-in composition of Locomotion, Navigation and Physics. Add this package explicitly to a client or
server that needs body-aware movement geometry, absolute-coordinate ground movement, a physics-backed
ground navigation profile, a baked profile set loaded without physics, or a route-free approach. It stays
outside every umbrella and carries no backend, input or rendering dependency.

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

The original five-parameter constructor and its optional defaults remain available. The selected movement
overload takes explicit arguments:

```csharp
public GroundMoveContext(
    Func<float, float, float> groundHeight,
    Func<float, float, Vector3>? groundNormal,
    IPhysicsWorld? physics,
    Func<float, float, Vector2>? clampXz,
    Func<float, float, float, MovementMedium>? medium,
    IPhysicsWorldQueryView? movementQueries);
```

The read-only `GroundHeight`, `GroundNormal`, `Physics`, `ClampXz`, `Medium` and `MovementQueries` properties
retain those providers. `Physics` remains the complete caller-owned world. When `MovementQueries` is present,
only movement uses that selected view. Its `SourceWorld` must be the exact same reference as `Physics`, so a
missing or different source is rejected even when two worlds have equal origins. Ground height, normal, clamp
bounds and medium coordinates are absolute. Physics poses and queries are local to `IPhysicsWorld.Origin`. A
step changes only `MoveState.Position` into that local frame and back.
Velocity, facing, effect scale, commitment and timers keep their carried values. A water surface returned by
`Medium` is converted from absolute Y to the local frame for the core.

The context reads and freezes the current origin for one sequential step, checks it at every provider boundary,
and rejects a rebase or recursive step during that step. Rebase only between steps. The context does not step,
dispose or rebase the physics world. The caller owns the world, statics and sequential lifetime. Delegate wrappers
are cached once when the context is constructed. The internal adapter validates the actual ground and medium
controls, including a positive finite radius, a half-height of at least `max(0.1, radius + 0.005)`, a finite
capsule length, a slope below `pi / 2`, ordered medium thresholds and finite nonnegative controls. The permitted
default `FacingTurnSpeed = float.PositiveInfinity` remains valid.

Dry traversal contexts retain `MovementQueries` while omitting the medium provider, so navigation edge proofs
use the same selected movement queries. A view reads the source `Origin` live between steps, but a full or selected
origin change or a source identity change during a step is rejected at the existing provider boundaries.

The context does not expose a public `Step` method. Its internal adapter is the shared frame seam used while a
profile is being built.

## Bounded static physics capture

`PhysicsNavBakeOptions` is an immutable record with this exact parameter list and defaults:

```csharp
public sealed record PhysicsNavBakeOptions(
    float MinX, float MinZ, float MaxX, float MaxZ, float CellSize,
    float ProbeHeight, float ProbeRange, float MaxSlopeRadians,
    int MaxCells, int MaxLayerCells, int MaxSurfacesPerColumn = 4,
    float EdgeProbeSeconds = 1f / 30f, int MaxEdgeProbeSteps = 64)
{
    public bool SampleWater { get; init; } // default false
}
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
`PhysicsColumnProbe`. It always uses the complete `Physics` world, not `MovementQueries`, so authored ground
columns remain available for navigation capture. It freezes the physics origin, queries local coordinates,
converts hit heights back to absolute Y, and samples in canonical Z then X order. Every captured surface stores
its absolute height, headroom and area tags. The probe uses a `MaxSurfacesPerColumn + 1` buffer so an over-cap
column is refused. Missing columns, padded centers, and exact outer-edge misses remain empty and therefore
blocked. Capture never fills a miss from the analytic ground provider. The classifier is not retained by the
captured data.

`SampleWater` opts capture into recording water for aquatic profiles. It is off by default, and then capture never
calls the context's medium and its output is unchanged. With it on, capture refuses a context without a medium with
`ArgumentException` naming `context`. It samples the medium once per in-bounds column after the physics probe, with
the feet at the column's lowest captured surface, or at `ProbeHeight - ProbeRange` for an empty column. An in-water
sample whose finite water surface lies above those feet records one water entry for the column: its absolute
surface height and the area tags the classifier returns at the water surface point. A column holds at most one
entry, found from its lowest surface, so a pool on a deck above dry ground is not seen. The medium must be the same
provider runtime movement uses, and the context's ground height must lie at or below the captured bed in every water
column, because the swim step floors a swimmer at the ground height. A flat analytic ground at the water surface lifts
swimmers off their float line.

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
    public GroundNavigation BuildProfile(
        in MoveTuning tuning,
        NavAreaFilter areas,
        GroundProfileOptions options);
}

public sealed record GroundProfileOptions
{
    public static GroundProfileOptions Default { get; }
    public bool Aquatic { get; init; } // default false
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
    public bool Aquatic { get; }
    public bool AllowsSegment(Vector3 fromFeet, Vector3 toFeet);
}
```

The two-argument `BuildProfile` builds a ground profile and equals the overload with `GroundProfileOptions.Default`.

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

Profile building allocates no garbage per proof. Penetration queries reuse one overlap scratch list per physics
world, the footprint predicate and the dry probe context are built once per profile, and the movement core reuses
its step and slide capsules per thread. Engine tests in the allocation-sensitive collections gate these targets:

| Measured unit | Before | Measured | Target |
| --- | ---: | ---: | ---: |
| Warmed `ComputePenetration`, world and query view | 72 B per call | 0 B | 0 B |
| Warmed medium-bearing `TryEdge` with a footprint predicate | 6,848 B per call | 0 B | 0 B |
| `BuildProfile`, 16 m by 16 m flat world at 0.25 m, 4,096 columns | 58,774 B per column | 80 B per column | at most 1 KiB per column |

The remaining profile bytes are the result grids. A 48 m by 48 m flat world at 0.25 m (36,864 columns) allocated
98.6 B per column in one dev Mac observation. Elapsed time is not gated, because the physics query count is
unchanged.

Profile proofs on smooth sloped physics ground are limited, and so is route following there. Two causes were
measured. A grounded capsule on a physics slope rests above the captured centre height, by `r (1 / cos slope - 1)`.
A body under command also creeps down the slope by 1.25 to 3.77 mm, because the core's push-out shoves it downhill
each tick, and allowing for the height alone did not cure the miss. So uphill and sideways edges can fail the 1 mm
arrival tolerance and drop out of the graph, and at runtime `MoveToRange` can stall short of a waypoint on a smooth
slope. This is open as [#1265](https://github.com/APKiwiOrg/KhaozEngine/issues/1265)
and is not fixed in this release. Check routes over sloped physics ground in the game's own world.

### Aquatic profiles

An aquatic profile lets a swimming body route across water. Build it with `new GroundProfileOptions { Aquatic = true }`
from a capture with `SampleWater`. `BuildProfile` throws `ArgumentException` naming `options` when the capture did not
sample water, and naming `tuning` unless `SwimExitDepthFraction <= SwimSurfaceSubmersionFraction <=
SwimEnterDepthFraction`. `GroundNavigation.Aquatic` reports the flag. An aquatic profile's `ValidateTuning` also
requires the three swim fractions to equal the baked values, because they place the float nodes.

The profile reads a derived column view. In a column with water at `W`, with body height `H = 2 x CapsuleHalfHeight`,
let `s` be the highest captured surface below `W`. The column is swim-deep when there is no `s` or its depth
`(W - s) / H` reaches `SwimEnterDepthFraction`, and the float height `f = W - SwimSurfaceSubmersionFraction x H` lies
above `s` and below `W`. A swim-deep column replaces every captured surface below `W` with one float surface at `f`,
where a swimming body rests, carrying the water entry's areas. Its headroom is the headroom of `s` less the rise to
`f`, clamped at zero, so a submerged overhang between the bed and the float line refuses the node. Surfaces at or
above `W` are kept, and every other column is unchanged, so a wading body stands on the bed. The arithmetic is single
precision in a fixed order, so a load derives the same bits as a fresh build.

Float holds, and every edge or Stair link with a float endpoint, are proved by swim steps through the live context and
its medium, at unit walk pace. A float start swims at its float height and any other start stands grounded. Every
slice must stay finite and inside the footprint, and either stand grounded and not swimming, or swim and pass a static
clearance check. Each slice is capped at the capsule radius, so consecutive clearance samples overlap, and a slice
with a zero pace fails, so a zero swim speed refuses every float edge. A swimming slice arrives within 1 mm
horizontally and within `max(StepHeight, 1 mm)` vertically, because buoyancy rather than geometry sets its height.
Edges between two non-float nodes keep the dry proof, so an aquatic profile's dry graph equals the ground profile's.

The core does not collide a swimmer, so the clearance check asks the movement physics for the profile capsule's
penetration at each swimming pose. It passes with no overlap, or when the separating translation points up within the
walkable slope and is no longer than `StepHeight`, a float capsule grazing the bed near a shore. A deck above, a post
beside and a steep bank wall refuse it. The penetration query reports only the deepest contact, so a shallow side
contact under a deeper bed contact passes. Deep water has no bed contact, so this applies only within about one step
of the bed, where a route may clip a bank. The limit is open as
[#1266](https://github.com/APKiwiOrg/KhaozEngine/issues/1266).

Every proof shares `MaxEdgeProbeSteps`, and an edge that runs past it is refused, never partly accepted. A bank edge
between a wading node and a float node wades at unit pace slowed by the wade ramp and the medium's zone scale, then
swims. It needs about `wading length / (EdgeProbeSeconds x 1 m/s x WadeMinSpeedScale x zone scale)` steps while it
wades, plus `swimming length / min(CapsuleRadius, EdgeProbeSeconds x SwimSpeed x zone scale)` steps once it swims,
plus a few steps of final approach. The default 64 steps of 1/30 s cover banks between 0.25 m cells at a zone scale
of 1. Larger cells or slow zones must raise `MaxEdgeProbeSteps`, or the float layer is cut off from the land. The
budget applies to each directed edge, so in a slow zone a shoreward swim can fit while the wade out does not. Dry
bank edges on a smooth sloped physics shoreline are subject to #1265 like any slope.

## Baked profile sets

`GroundNavigationBake` persists one capture's columns and one or more named profiles in a versioned binary file,
so a client loads physics-checked profiles at startup without a physics world, a ground provider or any proof:

```csharp
public sealed class NavBakeSources
{
    public NavBakeSources Add(string label, ReadOnlySpan<byte> sha256);
    public NavBakeSources AddHashOf(string label, ReadOnlySpan<byte> content);
    public NavBakeSources AddHashOf(string label, Stream content);
    public IReadOnlyList<string> Labels { get; }
}

public sealed record NavBakeProfile(string Name, MoveTuning Tuning, NavAreaFilter Areas)
{
    public bool Aquatic { get; init; } // default false
}

public sealed record NavBakeExpectation(PhysicsNavBakeOptions Options, NavBakeSources Sources,
    IReadOnlyList<NavBakeProfile> Profiles);

public enum NavBakeLoadStatus
{
    Loaded, NotABake, UnsupportedFormat, Corrupt,
    EngineChanged, OptionsChanged, SourcesChanged, ProfilesChanged,
}

public sealed record NavBakeLoadResult(NavBakeLoadStatus Status, string Detail, GroundNavigationBake? Bake);

public sealed class GroundNavigationBake
{
    public static GroundNavigationBake Create(PhysicsNavBake capture, NavBakeSources sources,
        IReadOnlyList<NavBakeProfile> profiles);
    public static NavBakeLoadResult Load(Stream source, NavBakeExpectation expected);
    public void WriteTo(Stream destination);
    public ReadOnlySpan<byte> Fingerprint { get; }
    public IReadOnlyList<string> ProfileNames { get; }
    public GroundNavigation GetProfile(string name);
}
```

`Create` builds every profile through `capture.BuildProfile` with its `Aquatic` flag while the capture is live, so each
profile is exactly the fresh build. An aquatic profile from a capture without `SampleWater` is refused there.
`WriteTo` writes a little-endian `KENB` file, format version 1, and two writes of one bake are byte-identical. A
loaded profile is a `GroundNavigation` like any other, and its `Space`, graph, columns, `AllowsSegment` and `Planner`
answers equal the fresh build as bits. `GetProfile` throws `KeyNotFoundException` for
an unknown name. `Fingerprint` is the SHA-256 of the identity block, for logs and build manifests.

Profile names and source labels are 1 to 64 characters from `a` to `z`, `0` to `9`, `.`, `_`, `-` and `/`, and are
unique. A bake needs at least one source and 1 to 256 profiles, and `MaxSurfacesPerColumn` at most 255.
`NavBakeSources` is a mutable builder. `Create` and `Load` snapshot it, so later edits never change a stored identity.
`Add` takes an exact 32-byte digest, and `AddHashOf` digests content with SHA-256.

The identity covers the engine version, every capture option including `SampleWater`, the caller's labelled source
digests, and each profile's name, area filter, `Aquatic` flag and every `MoveTuning` field except `WalkSpeed`,
`RunSpeed` and `AirMomentum`, which the probe overwrites. So a bake sampled differently, or a profile baked aquatic
against a ground expectation, is refused as stale and never loads as the other kind. The identity is canonical, so
equal inputs give equal bytes in any insertion order and on any architecture. A bake is valid only for the engine
version that wrote it.

`Load` validates the expectation as `Create` would and throws `ArgumentException` naming `expected` for an invalid
one, including an aquatic profile without `SampleWater` or with its swim fractions out of order. It then
reads the header and identity block and compares them with the identity encoded from the expectation. A stale bake
is refused there, before any payload byte is read. Only a match reads and checks the payload. The first failing
check decides the status, in container, identity and payload order, and identity differences are reported in
engine, options, sources and profiles order. `Detail` names the first difference, such as the engine versions,
the option field, the missing, extra or changed source label, or the profile and its first differing field.
`Detail` is developer text and is never shown to players. `Loaded` carries the bake and an empty detail. Every
other status carries a null bake. Truncated, trailing, non-canonical or damaged bytes return `Corrupt` and never
throw. Stream errors such as `IOException` propagate. Short reads from decompressing streams are handled. `Load`
reads the stream to its end, so pass a stream that ends after the bake. A file, a memory stream, or a network stream
wrapped to the payload length all work. The payload checksum detects accidental damage. It is not tamper
protection, so ship the bake with the same trust as the client binary.

Bake from the game's own content pipeline with the same physics composition as runtime movement, including the
movement query view:

```csharp
using var capture = PhysicsNavBake.Capture(context, options, classify);
NavBakeSources sources = gameNavigation.BakeSources(); // the game's labelled input digests
NavBakeProfile[] profiles =
[
    new("player", gameTuning.Player, gameAreas.PlayerFilter),
    new("npc-wide", gameTuning.WideNpc, gameAreas.NpcFilter),
];
GroundNavigationBake bake = GroundNavigationBake.Create(capture, sources, profiles);
using (FileStream file = File.Create(outputPath))
    bake.WriteTo(file);
```

Load once at startup from the same options, tuning and digests the game would bake with, and branch on the status:

```csharp
var expected = new NavBakeExpectation(options, gameNavigation.BakeSources(), profiles);
NavBakeLoadResult result;
using (FileStream file = File.OpenRead(bakePath))
    result = GroundNavigationBake.Load(file, expected);
if (result.Status == NavBakeLoadStatus.Loaded)
    playerProfile = result.Bake!.GetProfile("player");
else
    gameDiagnostics.Developer($"navigation bake refused: {result.Status} {result.Detail}");
```

Consumer guidance:

- The engine names no paths, profiles, area bits or extension. Write the file as a generated artifact beside the
  world it was baked from, with an extension such as `.kenav`.
- Source digests must cover every input a proof reads, not only the colliders: the world documents in a canonical
  order, collider options, the catalog rows that drive colliders, heights and medium, the ground height and normal
  providers, the clamp bounds, and a value naming the classifier policy and its tables.
- Normalise text line endings to LF before digesting, so a Windows checkout with CRLF files digests equal to the
  machine that baked.
- Prefer authored inputs to `TileWorldColliders.Hash` when the world holds walk surfaces yawed off a quarter turn,
  because that hash is not promised equal across x64 and ARM64.
- Rebake whenever a source input, the capture options, a probed tuning field or the engine pin changes. Keep a game
  test that loads the shipped bake with the shipped inputs and expects `Loaded`, so CI catches a stale artifact.
- Any status other than `Loaded` means the bake does not describe this build. The engine never substitutes a fresh
  build or an analytic route. Disabling routed walk-up and reporting the reason in developer diagnostics is game
  policy.

The payload stores the captured water entries after the surfaces: a count, then each entry's cell, surface height and
area tags in ascending cell order. A count with `SampleWater` off, a cell out of order or outside the bounds, or a
surface that is not finite and above its column's lowest surface (or the probe floor for an empty column) is
`Corrupt`. An aquatic profile's layers, exits and links are stored like a ground profile's. Load rebuilds its derived
float view from the captured columns, the water entries and the expected tuning before building its footprint.

The payload carries the baking machine's float decisions. A client on another architecture uses them even where its
own build would differ in the last bits. Those differences sit inside the bounded proof tolerance, and the live
movement core still resolves every actual step. The ground profiles of a loaded set share one immutable column
instance, as a fresh build does. Each aquatic profile owns its derived view.

Measured on the dev Mac: a two-layer 20 by 3 cell deck fixture wrote 2,455 bytes and loaded in 0.070 ms. A 48 m by
48 m flat world at 0.25 m (36,864 columns, one profile) wrote 664,689 bytes and loaded in about 20 to 23 ms,
allocating 1,958,264 bytes against about 0.85 to 0.96 MB retained. Load allocates the payload buffer, the final
arrays, per-profile scratch of 7 bytes per cell shared by every profile, and per-layer clearance scratch of 5 bytes
per cell, about 2 to 2.3 times what it retains. These are single observations, not startup guarantees.

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
bridge uses small authored surfaces.

Three bake facts run the same bridge. A rebased world with a deck over water, a one metre door and a no-draw hole
loads `player` and `wide` profiles equal to a fresh build. A 30 cm collider edit returns `SourcesChanged` naming
`colliders`. A column centred on a drawn outer tile edge loads exactly as captured, and a miss stays empty after load.
The bake proof makes no claim about Stair links beyond comparing whatever lists the bridge produces. Issue [#1233](https://github.com/APKiwiOrg/KhaozEngine/issues/1233) remains
an open adoption prerequisite for steep meshes and filtered ground. Issue [#1238](https://github.com/APKiwiOrg/KhaozEngine/issues/1238)
is resolved by the reconciled `PhysicsColumnProbe` representable-progress fix. A filtered P3 world without
populated ground statics cannot supply capture ground, and small local physics coordinates or rebasing are
required for large absolute positions. There is no ground-sampler fallback and no claim of full Hollowmere or
steep-bank coverage from this profile.

## Range steering and movement drivers

`MoveToRange` is the shared ground follower for an NPC or a client automation request. It uses the current
body's capsule and the target's observed shape to decide whether the body is in range. The mover's
`MoveState.Position` is its capsule centre. Navigation and route waypoints use the mover's own feet, and a
capsule target supplies its own feet from its own half-height. Reach has zero tolerance in this API. The tick
validates and evaluates current reach, then an airborne or committed body reports `Suspended` in preference to
`InRange`, with zero requested input. A swimming body is not grounded, so it is suspended too unless the driver opts
into swim steering. `InRange` means the supplied current body already passes
`ReachGeometry.Within`, never that a waypoint or predicted endpoint would.

The public driver surface is:

```csharp
public enum RangeMoveStatus
{
    Following, InRange, WaitingForPath, Unreachable, UnsupportedTransition, Suspended, Blocked,
}

public readonly record struct RangeSteering(Vector2 WorldDirection, RangeMoveStatus Status);

public sealed class MoveToRange
{
    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow = null);
    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow, RouteApproachOptions options);
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow = null);
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow, RouteApproachOptions options);
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target,
        float range, bool run, float dt, GroundMoveContext context);
    public void Reset();
}

public sealed record RouteApproachOptions
{
    public static RouteApproachOptions Default { get; }
    public bool CarryThroughStraightRuns { get; init; } // default false
    public bool SteerWhileSwimming { get; init; }       // default false
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

`MoveToRange` never returns `Blocked`. That status belongs to the route-free `DirectMoveToRange` described
below. Both adapters treat every status other than `Following` as idle.

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

The constructors without options use `RouteApproachOptions.Default`, which keeps every behaviour above.

`CarryThroughStraightRuns` keeps full pace along a straight route run. A cell route puts a waypoint on every cell
centre, and the strict accept radius makes the body land on each one, so a plain walk loses pace at every centre.
When the active waypoint is a collinear pass-through closer than one tick of travel, the driver aims at the end of the
run instead, and the follower consumes the waypoints it passed, through
`PathFollowConfig.ConsumePassedCollinearWaypoints`, which the option turns on. A corner, a reversal, a layer change, a
hop, waypoint 0 and the final waypoint are never carried past, so every mandatory turn is still landed on. A carried
step the guard refuses falls back to the active waypoint. A caller can also turn on
`ConsumePassedCollinearWaypoints` in its own `PathFollowConfig` without the option.

`SteerWhileSwimming` steers a swimming body along an aquatic profile, from `PhysicsNavBake.BuildProfile` with
`GroundProfileOptions.Aquatic` or a baked `NavBakeProfile` with `Aquatic`. The `GroundNavigation` constructor throws
`ArgumentException` naming `options` when the profile is not aquatic. The planner constructor trusts its caller.
With the option:

- An airborne body that is not swimming, and a committed body, stay `Suspended` before `InRange`, as without it.
- A swimming body is also `Suspended` before `InRange` while it settles: when the context has no medium, the medium at
  its feet is not water, or its feet lie farther than `max(StepHeight, 1 mm)` from its float line
  `WaterSurfaceY - SwimSurfaceSubmersionFraction x 2 x CapsuleHalfHeight`. A body that fell into deep water holds
  until buoyancy brings it back into the band, as an airborne body holds until it lands.
- A settled swimmer follows the route with its real feet. When `CharacterMovement.ResolveSwimming` says the tick
  swims, the travel bound is `SwimSpeed x max(0, zone scale) x SpeedScale x dt`, so a swim tick lands on a waypoint
  rather than overshooting it. Otherwise the bound is the walk or run bound above.
- A step is admitted when its prediction stands grounded and not swimming, or swims, and the segment guard allows it.
  A prediction that is airborne and not swimming, such as a shallow exit that would fall, is refused with zero input.
- The option combines with `CarryThroughStraightRuns`, so a swimmer also holds full swim pace on straight runs.

A body crossing between wading and swimming can hold `Suspended` for a tick while the core lands it. The aquatic
profile's own limits, including the deepest-contact clearance limit and the bank edge budget, are described under
Aquatic profiles above.

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
misses stay blocked. Issue [#1233](https://github.com/APKiwiOrg/KhaozEngine/issues/1233) remains an open adoption
prerequisite for steep meshes and filtered ground. Issue [#1238](https://github.com/APKiwiOrg/KhaozEngine/issues/1238)
is resolved by the reconciled `PhysicsColumnProbe` representable-progress fix. This package has no full Hollowmere
proof or fix and makes no consumer adoption or release-tag claim.

## Route-free approach

`DirectMoveToRange` steers a body toward exact shape range without a planner. It shares `MoveToRange`'s exact reach,
travel bound, closest point and stop ring code, and returns the same `RangeSteering` for `PlayerPathMovement` and
`NpcGroundMovement`. It never steers a swimmer: a swimming body is not grounded, so it stays `Suspended`. The driver
has no graph guard and the core does not collide a swimmer, so a direct swim approach could pass through props at the
waterline. Steer swimmers with `MoveToRange` and `RouteApproachOptions.SteerWhileSwimming` on an aquatic profile.

```csharp
public sealed record DirectApproachOptions
{
    public DirectApproachOptions(int stallWindowTicks, float stallTravelMetres,
        int approachWindowTicks, float approachGainMetres);
    public int StallWindowTicks { get; }
    public float StallTravelMetres { get; }
    public int ApproachWindowTicks { get; }
    public float ApproachGainMetres { get; }
    public float MaxDropMetres { get; init; }
}

public sealed class DirectMoveToRange
{
    public DirectMoveToRange(DirectApproachOptions options);
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, bool targetMoves, float dt, GroundMoveContext context);
    public void Reset();
}
```

`DirectApproachOptions` takes a stall window in ticks with a travel distance and an approach window in ticks with a
reach gain. There are no defaults. Windows are 1 to 65,535 ticks and distances are finite and positive, otherwise
the constructor throws `ArgumentOutOfRangeException`. A window of N ticks spans N intervals between N + 1 counted
samples and is first eligible on the (N + 1)th counted tick. When either window shows too little progress the driver
latches `RangeMoveStatus.Blocked`, which both adapters treat as idle. The sample ring is allocated in the
constructor, so steady ticks allocate nothing.

`MaxDropMetres` is an opt-in drop allowance, finite and not negative, default zero. Zero keeps the strict rule: a
step whose preflight leaves the ground is refused. A positive allowance admits such a step when the predicted fall,
settled on a copy through the live context with zero input as the following `Suspended` ticks will, lands grounded
and dry with its feet no more than the allowance below the current feet. Props, slopes and water answer through the
same core as the walk. A fall that starts swimming, sinks past the allowance, or has not landed within 256 settle
steps is refused as before. Once airborne the body is `Suspended` and counts toward neither window. After landing
the approach resumes, and the drop's travel counts as progress from the first grounded tick.

Each tick validates like `MoveToRange`, then returns `Suspended` for an airborne or committed body, `InRange` when
the current body already passes `ReachGeometry.Within`, and `Blocked` while a block is latched. Otherwise it requests
bounded travel toward the closest horizontal point of the target, preflights the step on a copy through the live
context, and shrinks it with the same stop ring bisection. Keep range and target shape constant for a walk, and call
Reset to start a new one.

```csharp
var approach = new DirectMoveToRange(new DirectApproachOptions(
    stallWindowTicks: gameApproach.StallTicks, stallTravelMetres: gameApproach.StallMetres,
    approachWindowTicks: gameApproach.ApproachTicks, approachGainMetres: gameApproach.ApproachMetres)
{
    MaxDropMetres = gameApproach.DropMetres, // opt-in, zero refuses every drop
});

// Once per simulation tick while the walk is active.
RangeSteering steering = approach.Tick(
    in body, in playerTuning, in target, gameTarget.NominalRange,
    gameTarget.Run, targetMoves: gameTarget.IsBody, dt, gamePlayerGroundContext);
if (steering.Status == RangeMoveStatus.Blocked)
    gameTarget.EndWalk(); // game policy, the latch holds until InRange or Reset
command = PlayerPathMovement.Command(in steering, gameTarget.Run, gameCamera.Yaw);
```

Differences from a typical game-side walk-up rule:

- Direction uses the closest horizontal point of a box rather than its centre. For capsules and points they are the
  same. A box approach reaches its near face sooner.
- The final fraction comes from live bisection against exact reach, not from a minimum fraction, so a short last step
  cannot stall or overshoot.
- A step that would leave the ground or start swimming is refused and counts toward the stall window, so a walk off a
  ledge or into deep water ends `Blocked` instead of falling or swimming. Set `MaxDropMetres` to drop off a ledge,
  prop top or deck edge within that depth and land on dry ground instead.
- A zero travel bound, such as a rooted body, counts toward neither window, so a rooted body holds without ending its
  walk. Airborne and committed ticks return `Suspended` and also count toward neither window.
- `InRange` clears both windows, so a followed body that moves away starts fresh windows.
- Stall travel is net displacement across the window, not accumulated path length, so pacing in place is blocked.
- `Blocked` stays latched until `InRange`, `Reset`, or a change of target shape, range or capsule geometry. End the
  walk on the first `Blocked`.
- A change of target kind, shape, yaw, range or capsule geometry resets the windows. A change of `targetMoves` clears
  only the approach window. The driver holds no target identity, so replacing the target with another of the same
  shape needs `Reset`.
- Pass `targetMoves` true for any body target, such as a creature or player, and false for static objects and points.
  A moving target disables the approach window.
- Call `Tick` exactly once per simulation tick, since the windows count ticks.

The driver has no wall following or local avoidance. It walks straight and reports a stall.
