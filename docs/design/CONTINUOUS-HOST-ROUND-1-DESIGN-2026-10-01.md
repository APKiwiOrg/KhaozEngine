# Continuous host, round 1: tile world physics, pointer capture, fragments and visibility

Status: approved by the owner on 2026-10-01. Implementation in four branches, see "Sequencing and release".

Consumer: Grimhollow's move from tile movement to continuous movement, phase P2. The consumer spec is
`docs/superpowers/specs/2026-10-01-continuous-movement-design.md` in the Grimhollow repository, and its
epic is https://github.com/APKiwiOrg/Grimhollow/issues/399. The engine issues are #1219, #1220, #1221 and
#1222. #34 is discussed under "What leaves this round".

## Why this round exists

Grimhollow keeps its authored `KhaozEngine.TileWorld` document as the world and moves its movement, server
host and camera to the continuous stack Ruinborne already ships on (`NetWorld`, `Locomotion`, `Physics.Bepu`,
`Navigation`). That stack has no way to turn a tile world into colliders, no pointer capture for mouse-look,
no host-neutral home for message fragments, and no per-viewer visibility on NetWorld. Grimhollow's phase P3
(the continuous host replacing its tile host on an integration branch) needs all four. Each is useful to a second game, so each
belongs here.

## Decisions for the owner

| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | Where the tile physics bridge lives | A new opt-in package `KhaozEngine.TileWorld.Physics`, referencing only `KhaozEngine.TileWorld` and `KhaozEngine.Physics`. |
| D2 | One source for the drawn ground and for water | Move the ground triangle position rule and the water body rule from `TileWorld.Render3D` down into `TileWorld`, and make render consume them, proved by the existing goldens. |
| D3 | Where a server gets an object's collision height | A new optional archetype field `collisionHeight`, filled from mesh bounds by an authoring command. |
| D4 | `.coll` overrides per archetype | Not in this round. Boxes from footprint and height first. |
| D5 | #34 (unreliable deltas) | Moves out of round 1 into its own design before Grimhollow's Release switch. |
| D6 | #1221 shape | Move the fragment core into `KhaozEngine.Netcode`. No NetWorld host change. |
| D7 | Release | All four land on `main`, then one minor release (20.17.0 unless the number is taken), tagged by the owner. |

## 1. Tile world physics bridge (#1219)

### Package

`KhaozEngine.TileWorld.Physics`, opt-in and kept out of the umbrellas, on the same opt-in list as
`KhaozEngine.Physics.Bepu` and `KhaozEngine.Identity.Exchange`. It references `KhaozEngine.TileWorld`,
`KhaozEngine.Physics` and `KhaozEngine.Locomotion`, because the water sampler returns Locomotion's
`MovementMedium`. No Render3D and no Bepu reference. `KhaozEngine.TileWorld` grants it internals, as it does
Render3D, for the exact quarter-turn basis `TileObjectPlacement.PlanarBasis`.
`Sharding -> Physics` is the precedent for a simulation package depending on the physics seam. Its tests
live in a new `KhaozEngine.TileWorld.Physics.Tests` project that references `KhaozEngine.Physics.Bepu`, as
`KhaozEngine.Game.Tests` does for the terrain collision tests. `docs/DEPENDENCY-SEAMS.md` gains the edge.

### Output shape

The bridge first describes, then registers, so the description is testable without a physics backend and
hashable.

```csharp
public sealed class TileWorldColliders
{
    public static TileWorldColliders Build(TileWorldDocument document, TileWorldCatalogs catalogs,
        TileColliderOptions options);
    public IReadOnlyList<TileCollider> Colliders { get; }   // canonical order
    public byte[] Hash { get; }                              // SHA-256 over the canonical list
    public TileGroundSampler Ground { get; }                 // the floor CharacterMovement needs
    public TileMediumSampler Medium { get; }                 // water for wading
    public TileColliderRegistration AddTo(IPhysicsWorld world); // returns handles, removable
}

public readonly record struct TileCollider(TileColliderKind Kind, PhysicsShape Shape, Pose Pose);
public enum TileColliderKind : byte { Ground, Wall, Blocked, Object, WalkSurface }
```

`Pose` is absolute. `AddTo` subtracts `world.Origin`, so floating origin works as it does for terrain.

### What it builds

| Source | Collider |
| --- | --- |
| Ground on plane 0 | One `TriangleMeshShape` static per region, built from the shared ground triangle rule (D2) at full detail, with `NoDraw` tiles skipped exactly as the mesher skips them. Triangles are wound for Bepu's one-sided meshes (the b and c swap terrain uses), so downward rays and capsules hit the top face. A mesh never sits inside a compound, which Bepu refuses. |
| Blocked tiles (`underlay == 0` or `TileSettings.Blocked`) | A box over the tile from its lowest corner to its highest corner plus `options.BlockedHeight` (default the plane height, 3 m), so nothing walks onto void or authored blocks, even from above a steep tile (amended in execution, plan B ruling B15). |
| `Wall` and `WallCorner` objects | A thin box along each edge the object blocks (one edge for `Wall`, two for `WallCorner`, on the anchor tile, using the baker's `WallFacing` rule), `options.WallThickness` thick (default 0.1 m) and as tall as the archetype's `collisionHeight`. Walls are read from the placed objects, not from the collision map's mirrored edge flags, so each wall is built once and knows its height. Boxes, not quads, because one-sided quads only block from one side. Corner bits are ignored, because continuous movement has no diagonal step to forbid. |
| `Solid` and `Diagonal` objects | A box over the rotated footprint (`TileFootprint.Of`), from the anchor's ground height to `collisionHeight`, yawed with the exact quarter-turn basis `TileObjectPlacement` uses. `Diagonal` keeps today's meaning, the whole anchor tile. |
| Walk surfaces | A thin box (0.1 m) at each surface's height over its rectangle, so a body stands on a bridge deck while the drawn ground below stays the floor. |
| Roof objects | Nothing. Roofs are drawn, not walked or collided, as in the tile host. |

Plane 0 and walk surfaces only. Upper floors, stairs and ladders wait for a consumer that authors them.

### The floor: one ground, three uses

`CharacterMovement` treats `groundHeight(x, z)` as a floor that physics can only raise. So the floor must be
exactly the drawn ground, not the bilinear `HeightAt` and not walk surfaces. `TileGroundSampler` answers
height and normal at a point from the same triangles the ground mesh holds, analytically, without a ray.
`PhysicsGroundProbe` is not used for the floor, because it returns the highest static and would turn a deck
or a crate top into the floor.

Tests prove the three agree: at a grid of points across a test world, the sampler, a downward ray against the
registered meshes, and the shared rule's triangles give the same height within 1 mm.

### Water

`TileMediumSampler(x, z, feetY)` returns a `MovementMedium` from the shared water body rule (D2): a point
over a `Water` underlay tile is in water whose surface is the body's rim height less 0.02 m, as the drawn
water plane is. Outside water it returns `MovementMedium.Dry`. Bodies stay clipped per region, matching what
render draws. An in-water value is always built through the constructor, never `default`, whose wade scale
would be zero.

### Collision height (D3)

The server has no meshes, so archetypes gain an optional `collisionHeight` (metres) in the catalog. It is
part of the archetype record, so `TileWorldHash` covers it. `ke-tileedit` gains a verb that fills it from
each archetype's mesh bounds (through the existing `TileObjectBoundsCache` and a `GltfMeshResolver` rooted
at a kit directory the verb is given, with no greybox fallback). The tool has no catalog writer today, so the
verb writes through a new format-preserving writer that keeps property order and indentation. Building
colliders for a `Solid`, `Diagonal`, `Wall` or `WallCorner` archetype with no height fails with the archetype
id, rather than guessing.

### Determinism and the hash

Colliders are emitted in a canonical order (kind, then plane, region, tile, object id). The hash digests each
collider's kind, its shape bytes through `PropCollisionFormat.Write` and its pose. Quarter-turn yaws use
`PlanarBasis`, never trigonometry. A client and a server that build from the same document and catalogs get
the same hash, and the consumer compares them at join if it wants to.

### Navigation

The bridge adds no navigation code. `NavLayerBaker` already bakes from any physics world through
`PhysicsColumnProbe`. Area masks for a game's pens or water are round 2 work with the movement kernel
(#1223). Until then a consumer uses the existing `extraBlocked` delegate.

## 2. Shared ground and water rules (D2)

Two rules move from `KhaozEngine.TileWorld.Render3D` into `KhaozEngine.TileWorld`, GPU-free:

- **Ground triangles.** `TileGroundTriangles.Build(document, region, plane)` returns the
  full-detail triangle positions (with the `NoDraw` skip, the overlay-present rule and mid-edge corner
  averaging) that `TileGroundMesher` uses today. The mesher keeps UVs, normals, materials and the coarse
  level of detail, and takes its full-detail positions from the shared rule.
- **Water bodies.** `IsWater`, `RimHeight`, `SurfaceDropMetres` and the 4-connected body search move into a
  `TileWaterBodies` type. `TileWaterPlanes` keeps its render output and consumes it.

The existing mesher and water goldens must pass unchanged. A failing golden means the move changed
geometry, which is a defect, not a golden to refresh.

## 3. Pointer capture and the camera gestures (#1220)

### Capture

`AppWindow` stays the only reader of raw input. A new `AppWindow.PointerCapture.cs` partial adds
`void SetPointerCaptured(bool captured)`, forwarded as `GameApp.SetPointerCaptured`, exactly as rumble is.

- It sets GLFW's cursor mode directly (`SetInputMode(Cursor, Disabled)`), and raw motion only when
  `RawMouseMotionSupported()` is true. It does not use Silk's `CursorMode` setter, which writes raw motion
  even when unsupported and raises a GLFW error on macOS.
- Capture drops on focus loss, on the same edge that already releases held buttons.
- The frame capture ends reports a zero `MouseDelta`, so the cursor's restore does not spike a camera.
- While captured, `MouseDelta` is in window points, not framebuffer pixels, so mouse-look sensitivity is the
  same on a 2x display as on a 1x display.
- `InputState` gains `bool PointerCaptured`.

### Gestures

Ruinborne's `RightMouseGesture` becomes the engine's `PointerGesture` (in `KhaozEngine.Windowing`), per
button, unchanged in behaviour: travel is path length over the press, crossing the threshold replays the
accumulated delta with no dead zone, a release below the threshold is a tap at the press origin, and a press
that starts while the UI owns the pointer stays inert.

`FollowCameraController` gains two optional gestures and three outputs:

- `OrbitGesture` (for example the left button) orbits without turning the body.
- `LookGesture` (for example the right button) orbits and reports `TurnBodyActive`, which the game copies
  into `MoveCommand.FaceCamera` with `Camera.Yaw` as `CameraYaw`. The engine already turns a body toward
  `CameraYaw` when `FaceCamera` is set.
- `WantsPointerCapture` is true while either gesture is dragging. The game forwards it to
  `SetPointerCaptured`.
- `TapThisFrame` and `TapPosition` per gesture, for a default action on a click without a drag.

With neither gesture set, the controller behaves exactly as today: `OrbitButton` orbits from the first frame
of the press. Grimhollow's middle-button orbit, the Showcase rooms and Ruinborne's masked snapshot all keep
working unchanged. Ruinborne can drop its own gesture later. "Both buttons run forward" is a game rule and
stays in the game.

### Verification

Gesture, controller and capture policy logic are headless-tested, lifting Ruinborne's gesture tests. The
GLFW behaviour (macOS raw motion, cursor restore, focus loss, Retina scale) cannot be tested headlessly. It
gets one manual check per desktop platform, recorded in the release notes.

## 4. Fragment core in Netcode (#1221)

NetWorld already delivers server-to-client game messages of any size over the reliable channel, and its
client-to-server cap is a configurable `MaxGameMessageBytes`. What a consumer moving off TileWorld actually
loses is the fragmenter and reassembler, because they live in `KhaozEngine.TileWorld.Netcode`.

- The core moves into `KhaozEngine.Netcode` as `MessageFragmenter` and `MessageReassembler`, parameterised
  by chunk payload width, because each host's envelope differs (TileWorld 4 bytes, NetWorld client to server
  5, server to client 3).
- `TileFragmentedMessage` and `TileFragmentReassembler` stay as byte-identical wrappers at today's 1015-byte
  width, so TileWorld consumers and their goldens do not change.
- Fragments ride the reliable ordered channel only. The reassembler keeps its four-assembly cap, its
  least-recently-fed eviction and its refusal tokens.

No NetWorld host API changes.

## 5. Per-viewer visibility on NetWorld (#1222)

`ShardedWorldServerConfig` and `WorldServerConfig` gain
`Func<int, long, bool>? EntityVisibleToSlot` (viewer slot, net id), the same shape as
`TileWorldServerConfig.GroundItemVisibleToSlot`.

- It filters the interest set after the interest query and before both the delta writer and the snapshot
  writer. The sharded snapshot path gets a `ShardHost` overload that takes the filtered set, because its
  interest index is private today.
- A viewer's own player is never filtered.
- The predicate is keyed on game-owned state by (slot, net id). It must not read components, because a ghost
  in a neighbouring cell lacks owner-only and server-only components.
- Hiding an entity sends it as a removal and showing it sends it as new, which is the same path as leaving and
  entering interest.
- Cost is one call per viewer per entity in interest per tick. The design note for #34 has to account for a
  visibility flip inside its ack window.

Tests mirror `TileGroundItemVisibilityTests`: owner only, policy flip, entering range later, reconnect, a
null predicate, and players are never filtered.

## What leaves this round (D5)

#34 asks for position deltas on the unreliable sequenced channel. The survey for this round found that it is
larger than its placement assumed, and that it has a correctness hazard:

- Applying a delta from an older baseline is not idempotent. An entity spawned and gone between the baseline
  and the new state, or a component that changed and reverted, is never mentioned. Today that heals within
  one round trip, because every applied sequence is acked reliably. Under loss it becomes a permanent ghost.
- LiteNetLib does not fragment unreliable sends, so a full snapshot or a large delta must stay reliable, and
  the transport has no MTU query yet.
- A baseline ahead of the client's state is a terminal disconnect today and must become a rebuild.

Grimhollow's continuous skeleton and every later Debug phase work on reliable deltas. #34 gets its own design
note and lands before Grimhollow's Release switch (its P8), when production loss behaviour starts to matter.

## Sequencing and release

Four feature branches, each with its own plan and review, landing on engine `main` in this order:

1. Shared ground and water rules (D2), because the bridge builds on them and the render goldens guard them.
2. The physics bridge, with `collisionHeight` and its authoring verb.
3. Pointer capture and gestures.
4. The fragment move and per-viewer visibility, independent of the rest.

Each branch follows the version ritual in `docs/CONTRIBUTOR-RULES.md`: the first package-bearing branch
opens the next version and its changelog entry, and the rest ride it. The owner tags the release. Grimhollow
then bumps its pin, moves `ke-tileedit` and `ke-sfxbake`, refreshes the vendored feed and records the swept
range in its `docs/ENGINE-INTEGRATION.md`. That closes Grimhollow's P2.

## Out of scope

- Upper floors, stairs and ladders in the bridge.
- `.coll` shapes per archetype (D4). Boxes cover Grimhollow's props for the swap.
- Navigation area masks, which move to the movement kernel round (#1223).
- #34 (D5).
- Retiring `KhaozEngine.TileWorld.Netcode`, which is #1224.
