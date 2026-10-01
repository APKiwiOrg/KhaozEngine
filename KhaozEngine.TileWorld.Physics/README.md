# KhaozEngine.TileWorld.Physics

Turns an authored [KhaozEngine.TileWorld](../KhaozEngine.TileWorld) world into static colliders for any
[KhaozEngine.Physics](../KhaozEngine.Physics) world, plus a floor sampler and a water medium sampler for
[KhaozEngine.Locomotion](../KhaozEngine.Locomotion).

It references exactly those three packages. It carries no renderer and no physics backend, so the caller picks the
`IPhysicsWorld` (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the shipped one).

The package is opt-in and in no umbrella. Add it explicitly.

## Use

```csharp
TileWorldColliders colliders = TileWorldColliders.Build(document, catalogs);   // options default when omitted
using var world = new BepuPhysicsWorld();
using TileColliderRegistration registration = colliders.AddTo(world);

body = CharacterMovement.Step(body, command, dt, colliders.Ground.HeightDelegate, MoveTuning.Default,
                              colliders.Ground.NormalDelegate, world, medium: colliders.Medium.Delegate);
```

`TileWorldColliders.Build(document, catalogs, options)` describes the loaded regions as data and registers
nothing. `AddTo(world)` adds every collider as a static and returns a `TileColliderRegistration`, whose `Remove()`
or `Dispose()` takes them out again. `Ground` and `Medium` are the samplers, and their `HeightDelegate`,
`NormalDelegate` and `Delegate` are the ground-height, ground-normal and medium arguments of
`CharacterMovement.Step`, each created once.

After an edit, remove the old registration, build again and add the new colliders. The ground sampler reads the
document live, but its bounds were captured at build, and the medium sampler snapshots the water. Both answer for
the world as it was built, so rebuild both.

If the world refuses a static partway through `AddTo`, the statics already added are removed and the world's
exception is rethrown. `Remove` tries every handle once even when one fails, then rethrows the one failure as it was
or several as an `AggregateException`. A second call retries only the handles that failed.

## Types

- **`TileWorldColliders`**: `Build`, `Colliders` (every `TileCollider` in canonical order), `Hash`, `Ground`,
  `Medium` and `AddTo`.
- **`TileCollider(Kind, Shape, Pose)`**: one static. `TileColliderKind` is `Ground`, `Wall`, `Blocked`, `Object` or
  `WalkSurface`, and its declaration order is the canonical order.
- **`TileColliderOptions`**: `WallThickness` (0.1 m), `BlockedHeight` (null means the document's plane height) and
  `WalkSurfaceThickness` (0.1 m). `Build` refuses a value that is not a finite number above 0 with an
  `ArgumentOutOfRangeException` naming the option.
- **`TileColliderRegistration`**: the static handles, index for index with `Colliders`, with `Remove` and `Dispose`.
- **`TileGroundSampler`**: `HeightAt(worldX, worldZ)` and `NormalAt(worldX, worldZ)`.
- **`TileMediumSampler`**: `MediumAt(worldX, worldZ, feetY)`.

## What becomes a collider

- **Ground:** one triangle mesh per region with any drawable tile, the triangles `TileGroundTriangles.Build` gives
  the renderer. Backend meshes are one-sided, so each triangle's second and third index are swapped to turn its
  front face up, the same swap the terrain's collision mesh makes.
- **Wall:** one box per edge a placed `Wall` or `WallCorner` blocks, as the collision baker reads its rotation,
  `WallThickness` thick and centred on the edge.
- **Blocked:** one box per tile with no underlay or marked `TileSettings.Blocked`, from the tile's lowest corner up
  `BlockedHeight`.
- **Object:** a box over a `Solid` object's rotated footprint, or over a `Diagonal` object's anchor tile.
- **WalkSurface:** a box `WalkSurfaceThickness` thick under each walk surface's top, over its rectangle as
  `TileWalkSurfaces` resolves it.

A wall or object box's top is the height the model stands at, `TileObjectPlacement.AnchorPosition`, plus the
archetype's `CollisionHeight`. Its bottom is the lower of the lowest ground corner under the box and that standing
height, so it never inverts on a steep slope. Solid, Diagonal and wall boxes are axis-aligned over the tiles the
collision baker blocks, so they stand where the editor's and the server's tile collision says. Walk surfaces are
the only yawed boxes. On a quarter turn they stay axis-aligned with their half extents swapped as the turn needs.

**The floor is the drawn ground only.** `Ground` answers from the ground triangles and never from a walk surface or
an object top. A deck, a crate or a bridge holds a body up only as a physics static, so a body can walk under a
high deck on the ground and stand on it from above.

**Every Solid, Diagonal, Wall and WallCorner archetype needs a `collisionHeight`.** `Build` throws a
`TileWorldException` naming the archetype when a placed one has none. `ke-tileedit` fills them from the kit:
`archetype_measure_heights` reads each model's top and `archetype_set_collision_heights` writes the heights into the
catalog files.

## Coordinates

Colliders and samplers speak document world coordinates, absolute metres from `TileWorldSpace`. `AddTo` subtracts
`world.Origin` from each pose once, at registration, and a later `Rebase` is the backend's job. The samplers stay
absolute, so a caller stepping a body in a rebased world adds the origin to the coordinates it hands them and takes
it off the heights they answer:

```csharp
Vector3 o = world.Origin;
Func<float, float, float> height = (x, z) => colliders.Ground.HeightAt(x + o.X, z + o.Z) - o.Y;
Func<float, float, Vector3> normal = (x, z) => colliders.Ground.NormalAt(x + o.X, z + o.Z);
Func<float, float, float, MovementMedium> medium = (x, z, feetY) =>
    colliders.Medium.MediumAt(x + o.X, z + o.Z, feetY + o.Y) is { InWater: true } m
        ? new MovementMedium(m.WaterSurfaceY - o.Y, inWater: true, m.WadeSpeedScale)
        : MovementMedium.Dry;
```

## Samplers

`HeightAt` and `NormalAt` answer in three cases, with no search and no allocation per call. On a drawable plane-0
tile they take the height and up normal of the ground triangle under the point. A point outside the rectangle
bounding the loaded regions is first moved just inside it, so the floor carries on level past the world's edge. On a
tile with no ground (no underlay, `TileSettings.NoDraw`, or a missing region inside the rectangle) they answer the
document's lattice height, `TileWorldDocument.HeightAt`, with a straight up normal. They never answer NaN.

`MediumAt` is in water only when the point is over a water body and `feetY` is below that body's surface, so a body
standing on a deck above a river is dry. The surface is the body's rim less `TileWaterBodies.SurfaceDropMetres`. An
in-water answer is always built through the `MovementMedium` constructor and leaves the wade speed to the tuning.

## Hash

`Hash` is SHA-256 over every collider in order: its kind byte, its shape as `PropCollisionFormat.Write` writes it,
and its pose as seven little-endian floats (position x, y, z, then orientation x, y, z, w). The same world and
catalogs hash equal on every rebuild, whatever order the regions were loaded in. Ground meshes and every box on a
quarter turn use no trigonometry, so they hash exactly across architectures. A walk surface on a yaw that is not a
quarter turn takes its centre and orientation from `MathF` trigonometry, which can differ in the last bits between
x64 and ARM64, and so can the hash of a world that has one.

## Limits

- Plane 0 only: its ground, its objects and their walk surfaces. Anything on another plane, every roof and an object
  whose archetype the catalogs do not define get no collider. A plane-0 `Solid` roof is still blocked by the tile
  collision map, so the two disagree there.
- A `TileSettings.NoDraw` tile has no ground triangle and no blocked box, so it is a hole in the physics floor.
- A down ray exactly on an outer edge of the drawn ground, the loaded world's boundary or a drawn tile beside an
  undrawn one, may miss under the backend's half-open triangle edges. Interior lattice lines and region seams hit.
  The movement floor is the sampler, not a ray, so a walking body never depends on that line, and a void tile
  beside it carries a blocked box.
- No `.coll` prop shapes. Every object collider is a box.
