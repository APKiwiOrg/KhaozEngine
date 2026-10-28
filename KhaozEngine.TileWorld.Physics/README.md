# KhaozEngine.TileWorld.Physics

Turns an authored [KhaozEngine.TileWorld](../KhaozEngine.TileWorld) world into static colliders for any
[KhaozEngine.Physics](../KhaozEngine.Physics) world, plus a floor sampler and a water medium sampler for
[KhaozEngine.Locomotion](../KhaozEngine.Locomotion).

It references exactly those three packages. It carries no renderer and no physics backend, so the caller picks the
`IPhysicsWorld` (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the shipped one).

The package is opt-in and in no umbrella. A tile world needs no physics to walk, render or serve.
Add it explicitly when a game needs physics under its tile world, together with its chosen backend.

## Use

```csharp
TileWorldColliders colliders = TileWorldColliders.Build(document, catalogs);   // options default when omitted
using var fullWorld = new BepuPhysicsWorld();
using TileColliderRegistration registration = colliders.AddTo(fullWorld);
using IPhysicsWorldQueryView movementView = registration.CreateMovementQueryView();

body = CharacterMovement.Step(body, command, dt, colliders.Ground.HeightDelegate, MoveTuning.Default,
                              colliders.Ground.NormalDelegate, movementView,
                              medium: colliders.Medium.MediumDelegate);
GroundMoveContext context = new(
    colliders.Ground.HeightDelegate,
    colliders.Ground.NormalDelegate,
    physics: fullWorld,
    clampXz: null,
    medium: colliders.Medium.MediumDelegate,
    movementQueries: movementView);
```

`TileWorldColliders.Build(document, catalogs, options)` describes the loaded regions as data and registers
nothing. `AddTo(fullWorld)` adds every collider as a static and returns a `TileColliderRegistration`, whose
`Remove()` or `Dispose()` takes them out again. The complete world retains its ground meshes for simulation,
general queries and navigation capture. `CreateMovementQueryView()` is lazy and creates a non-owning view that
excludes only this registration's analytic ground, so walls, blocked tiles, objects, walk surfaces and later
non-ground additions remain visible to movement. Use that view for `CharacterMovement.Step` and as
`movementQueries` in the explicit six-argument `GroundMoveContext`, with `physics: fullWorld` for capture.
`Ground` and `Medium` are the samplers, and their `HeightDelegate`, `NormalDelegate` and `MediumDelegate` are
the ground-height, ground-normal and medium arguments of the movement APIs, each created once.

`GroundHandles` is the cached read-only subset of installed ground in canonical registration order. Rejected
ground has no handle or exclusion. The subset retains its metadata
after `Remove`, just like `Handles`, but removed handles cannot be used to create a new view. If a backend does
not support the optional query factory, `AddTo` still registers successfully and the view request throws
`NotSupportedException`. For multiple registrations on one source, combine their live `GroundHandles` and call
the generic factory once with that union. When replacing a registration, dispose its view first, remove the old
registration, add the new registration and create a new view. Dispose movement contexts and query views before
the registration, and the caller-owned source world last. Query selection never removes installed ground from
the complete capture world.

After an edit, remove the old registration, build again and add the new colliders. The ground sampler reads the
document live, but its bounds were captured at build, and the medium sampler snapshots the water. Rebuild after edits.

For the ordinary contact controller, use `Build(document, catalogs, worldId)` and
`registration.CreateMovementRegistration(worldId)`. The same identity names the captured water field and
installed support owners. Pass `MovementGroundHeight`, `MovementGroundNormal` and `Medium` to
`EffectiveMovementBinding.Ordinary`, together with that registration and the playable boundary.
The binding creates its movement view with ground handles excluded. Ground meshes remain installed in the
complete physics world. Use `CreateMovementRegistrationBuilder(worldId, combinedExtent)` to retain tile owners
and exclusions while adding other installed statics. Build that combined registration under a fresh source lease.

Retain the returned `OrdinaryMovementRegistration` for `ApplyRuntimeStatics` batches between movement reads.
Use `CreateMovementRegistrationBuilder(worldId, combinedExtent)` to reserve possible runtime height and width
before binding, even for a flat tile world. The default tile extent does not reserve future walls automatically.
Runtime Set poses are absolute world metres. Existing Contact and Legacy selected views include these real
statics without rebuilding the tile document, authored colliders or `TileWorldHash`. Runtime entries never join
`Handles`, `GroundHandles` or `ColliderIndices`. Sanctioned removal releases the runtime shape. Baked installation
removal still owns only baked handles, and the complete physics world owns remaining runtime entries.

The contact delegates capture terrain layers at build. Resident islands may share a declared bounding extent.
An analytic query inside a non-resident hole answers unresolved, while physics and water remain available.
Outside the loaded bounding rectangle, captured analytic terrain extends the nearest edge like the legacy sampler.
`MovementExtent` contains the loaded terrain range, every transformed collider and finite water columns,
including objects outside their anchor region.

`TileMediumSampler` implements `IMovementWaterField` with immutable body identities, finite beds and free surfaces.
A point is in a column when the exact framed centre lies in X [minimum, maximum), Z (minimum, maximum] and its
feet in [bed, surface). Its trace certifies rounded capsule coverage of the framed sweep against column unions,
including exposed face patches and dry volume.
Point and trace queries receive the physics-local body plus its frame origin and compare their double sum
against the captured absolute column bounds. An immutable column index searches the actual rounded XZ bounds,
including large tile addresses whose rounded faces move across several nominal tiles. Candidate generation
does not assume a fixed neighbouring-tile radius and never turns a missing candidate into a false dry fact.
Equal-height bodies in different regions keep distinct identities. Internal same-body faces are suppressed.
Capacity refusals publish no prefix. `MediumAt` and `MediumDelegate` retain the legacy surface-only behavior,
including water below a finite certified bed. Legacy builders derive a stable content identity independent of
`DisplayName`. Explicit identities remain the adoption door for matching server and client worlds.

If the world refuses a static partway through `AddTo`, the statics already added are removed and the world's
exception is rethrown. If that rollback fails too, `AddTo` throws an `AggregateException` whose first inner
exception is the add failure. `Remove` tries every handle once even when one fails, then rethrows the one failure as it was
or several as an `AggregateException`. A second call retries only the handles that failed.

## Filtered installation

The overload `AddTo(IPhysicsWorld world, Func<TileCollider, bool> include)` selects whole colliders before
physical insertion. For example, this neutral policy omits object boxes:

```csharp
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Physics;

Func<TileCollider, bool> include = collider => collider.Kind != TileColliderKind.Object;
using TileColliderRegistration selected = colliders.AddTo(fullWorld, include);
for (int i = 0; i < selected.Handles.Count; i++)
{
    TileCollider original = colliders.Colliders[selected.ColliderIndices[i]];
    StaticHandle installed = selected.Handles[i];
}
```

The predicate runs synchronously once per collider in canonical order, with absolute document-space poses,
before reading `world.Origin` or inserting any static. Keep it pure: no mutation of the document, shapes or
world, reentrant installation or concurrently changing inputs. Decisions are snapshotted, and the registration
never calls the predicate again. Null arguments refuse. A predicate exception propagates unchanged with no
insertion. Selected adds share the default rollback transaction and failure ordering.

`Handles` is compact installed order. `ColliderIndices[i]` is its original `Colliders` index, strictly increasing.
Both lists are immutable metadata retained after removal. The default overload installs every collider, with
indices `0..Colliders.Count-1`. Default and all-true calls preserve add order, poses, owners, exclusions, extent,
semantic digest and same-build serialized bake bytes. Historical tests normalize only engine-version identity
and its framing lengths. Production engine-version load refusals still apply.

Selection copies the original canonical owners, aliases, feature ranges and absolute transformed bounds,
including partially selected placed owners. It retains the complete captured `MovementExtent`, even when
ground or everything is omitted. Analytic ground, water and resident coverage remain available. Only installed
ground enters `GroundHandles` and movement exclusions. `PhysicsNavBake.Capture` samples the installed physical
set, so omitted ground supplies no capture floor even though the analytic sampler remains available.

Keep the source alive through contexts, leases and capture, and unchanged within each read interval. The ordinary
registration remains bound to its exact source. Equal handle values in another world cannot authenticate it.
Raw static mutation invalidates authentication. Sanctioned runtime batches publish their own provenance and live
revision while retaining authored identity. Bepu steps and supported rebases preserve static provenance.
Keep navigation capture authored-only and fixed throughout its reads. Removing any selected baked handle prohibits new
movement registration from that installation. Dispose contexts and views before removing the registration,
then dispose the source last. Rollback can still advance source generation history.

### Persist the installation policy

`colliders.Hash` always identifies the complete built sequence, independent of selection. The ordinary live
semantic digest distinguishes selected provenance, but a persisted bake also needs an explicit policy source:

```csharp
using System.Text;
using KhaozEngine.Movement;

// Canonical UTF-8 with explicit LF, naming both policy version and inputs.
byte[] policyBytes = Encoding.UTF8.GetBytes("omit-kind/v1\nkind=Object\n");
var sources = new NavBakeSources().AddHashOf("world", canonicalWorldBytes);
sources.AddHashOf("policy/colliders", policyBytes);
GroundNavigationBake bake = GroundNavigationBake.Create(capture, sources, profiles);

// Runtime reconstructs the same canonical inputs, including the policy entry.
var runtimeSources = new NavBakeSources().AddHashOf("world", canonicalWorldBytes);
runtimeSources.AddHashOf("policy/colliders", policyBytes);
var expected = new NavBakeExpectation(options, runtimeSources, profiles);
NavBakeLoadResult loaded = GroundNavigationBake.Load(stream, expected);
```

Include every other proof input in both source builders too. Change the policy digest whenever its semantics
or inputs change. Contact registration, live environment identity, format 2 and runtime profile validation do
not insert this entry or replace it. The bounded integration proof loads each policy's bake with its matching
expectation, then changes only `policy/colliders` and receives `SourcesChanged` naming that source, with the
complete collider hash and tuning unchanged. Consumer material policies belong in the consumer.

## Types

- **`TileWorldColliders`**: `Build`, `Colliders` (every `TileCollider` in canonical order), `Hash`, `Ground`,
  `Medium` and `AddTo`.
- **`TileCollider(Kind, Shape, Pose)`**: one static. `TileColliderKind` is `Ground`, `Wall`, `Blocked`, `Object` or
  `WalkSurface`, and its declaration order is the canonical order.
- **`TileColliderOptions`**: `WallThickness` (0.1 m), `BlockedHeight` (null means the document's plane height) and
  `WalkSurfaceThickness` (0.1 m). `Build` refuses a value that is not a finite number above 0 with an
  `ArgumentOutOfRangeException` naming the option.
- **`TileColliderRegistration`**: compact `Handles`, original `ColliderIndices`, selected `GroundHandles`,
  `Remove` and `Dispose`.
- **`TileGroundSampler`**: `HeightAt(worldX, worldZ)` and `NormalAt(worldX, worldZ)`.
- **`TileMediumSampler`**: `MediumAt(worldX, worldZ, feetY)`.

## What becomes a collider

- **Ground:** one triangle mesh per region with any drawable tile, the triangles `TileGroundTriangles.Build` gives
  the renderer. Backend meshes are one-sided, so each triangle's second and third index are swapped to turn its
  front face up, the same swap the terrain's collision mesh makes.
- **Wall:** one box per edge a placed `Wall` or `WallCorner` blocks, as the collision baker reads its rotation,
  `WallThickness` thick and centred on the edge.
- **Blocked:** one box per tile with no underlay or marked `TileSettings.Blocked`, from the tile's lowest corner to
  its highest corner plus `BlockedHeight`. The tile collision map refuses the whole tile, so the box stands above
  every point of its ground, and a body cannot step onto a steep blocked tile from the high side.
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
Func<float, float, float> height = (x, z) =>
{
    Vector3 o = fullWorld.Origin;
    return colliders.Ground.HeightAt(x + o.X, z + o.Z) - o.Y;
};
Func<float, float, Vector3> normal = (x, z) =>
{
    Vector3 o = fullWorld.Origin;
    return colliders.Ground.NormalAt(x + o.X, z + o.Z);
};
Func<float, float, float, MovementMedium> medium = (x, z, feetY) =>
{
    Vector3 o = fullWorld.Origin;
    return colliders.Medium.MediumAt(x + o.X, z + o.Z, feetY + o.Y) is { InWater: true } m
        ? new MovementMedium(m.WaterSurfaceY - o.Y, inWater: true, m.WadeSpeedScale)
        : MovementMedium.Dry;
};

body = CharacterMovement.Step(body, command, dt, height, MoveTuning.Default, normal, movementView, medium: medium);
```

Each delegate reads `fullWorld.Origin` when it is called, so the wrapped delegates stay right across later rebases.
Movement keeps using the selected view while the complete source is rebased between steps.

## Samplers

`HeightAt` and `NormalAt` answer in three cases, with no search and no allocation per call. On a drawable plane-0
tile they take the height and up normal of the ground triangle under the point. A point outside the rectangle
bounding the loaded regions is first moved just inside it, so the floor carries on level past the world's edge. On a
tile with no ground (no underlay, `TileSettings.NoDraw`, or a missing region inside the rectangle) they answer the
document's lattice height, `TileWorldDocument.HeightAt`, with a straight up normal. They never answer NaN.

Triangle heights interpolate differences from one lattice vertex. A level triangle keeps that exact lattice
height, so certified support and the water field use the same bed boundary.

`MediumAt` is in water only when the point is over a water body and `feetY` is below that body's surface, so a body
standing on a deck above a river is dry. The surface is the body's rim less `TileWaterBodies.SurfaceDropMetres`. An
in-water answer is always built through the `MovementMedium` constructor and leaves the wade speed to the tuning.

The legacy samplers have no shared scratch. The ground sampler needs its document to remain unedited while read.
The water field and certified ground delegates retain immutable captured data. Water tracing uses bounded
per-thread scratch and precomputed exact rectangular unions, so independent readers do not share scratch.

These providers were audited for a parallel contact navigation bake, `NavBakeExecution` above degree 1 in
[KhaozEngine.Movement](../KhaozEngine.Movement/README.md#parallel-contact-bake), and are endorsed by method:
`MovementGroundHeight` and `MovementGroundNormal`, `Ground.HeightAt`, `NormalAt`, `HeightDelegate` and
`NormalDelegate` while the document stays unedited, `Medium` as a water field (`SampleCentreWater`, `TraceWater`,
`SemanticIdentity`, `Generation`, `Extent`) and through `MediumAt` and `MediumDelegate`, and the registration that
`CreateMovementRegistration` or `CreateMovementRegistrationBuilder` builds.

## Hash

`Hash` is SHA-256 over every collider in order: its kind byte, its shape as `PropCollisionFormat.Write` writes it,
and its pose as seven little-endian floats (position x, y, z, then orientation x, y, z, w). Filtering installation
does not change this complete-set hash or create an installed-subset hash. The same world and
catalogs hash equal on every rebuild, whatever order the regions were loaded in. Ground meshes and every box on a
quarter turn use no trigonometry, so they hash exactly across architectures. A walk surface on a yaw that is not a
quarter turn takes its centre and orientation from `MathF` trigonometry, which can differ in the last bits between
x64 and ARM64, and so can the hash of a world that has one.

## Limits

- Plane 0 only: its ground, its objects and their walk surfaces. Anything on another plane, every roof and an object
  whose archetype the catalogs do not define get no collider. A plane-0 `Solid` roof is still blocked by the tile
  collision map, so the two disagree there.
- A `TileSettings.NoDraw` tile has no ground triangle and no blocked box, so it is a hole in the physics floor.
- Nothing collides at the edge of the loaded world or across a missing region inside it. The floor sampler carries on
  level there, so a body keeps walking on an invisible floor. Author a blocked border around the playable area.
- A down ray exactly on an outer edge of the drawn ground, the loaded world's boundary or a drawn tile beside an
  undrawn one, may miss under the backend's half-open triangle edges. Interior lattice lines and region seams hit.
  The movement floor is the sampler, not a ray, so a walking body never depends on that line, and a void tile
  beside it carries a blocked box.
- No `.coll` prop shapes. Every object collider is a box.
