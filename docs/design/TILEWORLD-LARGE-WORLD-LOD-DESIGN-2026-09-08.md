# Shared large-world LOD and HLOD for TileWorld

Status: Complete. Implemented in `18.38.0`. Program issue
[855](https://github.com/APKiwiOrg/KhaozEngine/issues/855). Grimhollow
[158](https://github.com/APKiwiOrg/Grimhollow/issues/158) is the active consumer.

This design extends the completed [LOD/HLOD design](LOD-HLOD-DESIGN.md) and the TileWorld renderer described in
[TILE-WORLD-DESIGN-2026-08-15.md](TILE-WORLD-DESIGN-2026-08-15.md). It does not create a second LOD stack.

## Problem

TileWorld currently loads one synchronous Chebyshev ring and submits full prop meshes through a reduced scene
seam. Props hard-cull at 96 metres by default even when the camera sees 600 metres. The current Hollowmere world
already places 949 live trees. Its authored map is expected to grow to thousands or tens of thousands of tiles,
where increasing the full-mesh radius is not a viable answer.

The engine already owns the proven Ruinborne machinery in `KhaozEngine.Terrain.Render3D`:

- full and authored LOD prop parts in `PropLayer` and `PropRenderer`
- merged coarse meshes through `PropHlod`
- background CPU builds and frame-thread GPU application in `Scene3DChunkSink`
- one retained HLOD handle per loaded chunk and layer
- complementary individual-to-cluster crossfades
- unload disposal and render statistics

TileWorld does not consume those owners. `ITileWorldScene.DrawProps` carries only full parts and a radius, its
catalog has only `meshRef`, and `TileRegionResidency` has no render-only decor class. Copying the Ruinborne logic
into `TileWorldView` would create two implementations of resource lifetime, invalidation, failure fallback, and
crossfade policy.

## Decision

Extract the prop clustering parts of `Scene3DChunkSink` into a reusable engine component and make both terrain
streaming and TileWorld delegate to it. TileWorld adds the region snapshots and residency adapter required by its
authored document model. It does not replace authored tile ground with `TerrainField` and does not construct a
second scene sink.

TileWorld also gains a boundary-preserving coarse ground representation. Long-range prop HLOD must never appear
over unloaded ground, and loading full 64 by 64 tile meshes across a 576 metre decor horizon would move the same
scaling problem from trees to terrain.

## Shared prop-cluster owner

Add `PropClusterRenderer` to `KhaozEngine.Terrain.Render3D`. It owns the behavior that is independent of terrain
height generation:

```csharp
public sealed class PropClusterRenderer : IDisposable
{
    public PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request);
    public void Apply(PropClusterKey key, PropClusterCpuBuild build);
    public void Invalidate(PropClusterKey key);
    public void Unload(PropClusterKey key);
    public void Draw(Vector3 focus);
}
```

The concrete request and key are immutable values. A request carries one cluster area, a content generation,
placement snapshots, `PropLayer` definitions, and the full, LOD, and flattened HLOD source parts those layers
name. CPU builds contain no GPU handle.

The component owns:

- per-cluster and per-layer LOD0, LOD1, and HLOD draw decisions
- `PropHlod.BuildMergedMesh` calls
- retained HLOD GPU handles and replacement generations
- frame-thread upload and unload
- cache reuse across focus movement and pure ground re-LOD
- bounded retry after an initial build failure
- render counters and failure logging
- LOD and HLOD crossfade values

`Scene3DChunkSink` retains terrain, physics, water, and chunk orchestration. It delegates its prop build, apply,
draw, invalidation, and unload work to `PropClusterRenderer`. Ruinborne changes no placement format or game code
in this round and still consumes `PropLayer.WithHlod` through the sink. This makes it a regression consumer of
the extracted owner.

## LOD0 to LOD1 handoff

`PropRenderer` already selects one optional LOD mesh past `LodDistance`, but the swap is hard. Add
`LodCrossfadeWidth` to `PropLayer`, defaulting to zero so existing consumers remain byte-for-byte compatible.

An opted-in crossfade uses complementary dissolve coverage. LOD0 fades out while LOD1 fades in over the same
band. The color and shadow paths use the same transition value. Two ordinary overlapping dissolves are not
sufficient because their independent noise can create a bright or double-shadow interval. Extend the rigid
instance data with an explicit complementary phase that the model and shadow depth shaders read identically.

The transition is distance-driven and deterministic. It never depends on frame time, which keeps a stopped
camera stable and makes cross-backend images reproducible.

## TileWorld catalog and resolver

Add one optional LOD tier to `TileObjectArchetype` and the catalog schema:

```json
{
  "meshRef": "models/tree.glb",
  "lodMeshRef": "models/lod/tree.glb"
}
```

One authored LOD tier is enough before HLOD and matches the shared renderer that already exists. More catalog
tiers are not added until a measured consumer needs them.

`GltfMeshResolver` gains cached full, LOD, and flattened resolution. The flattened HLOD source loads LOD1 when it
exists and falls back to LOD0. A missing or malformed LOD logs once and keeps LOD0. A missing flattened source
keeps individual geometry and marks that cluster ineligible for HLOD-only decor. It never creates a visual hole.

Catalog hashing follows the existing cosmetic mesh policy, so `lodMeshRef` participates in the canonical
catalog digest and advances its scheme. Collision and pathing still derive from the full authored objects, not
from either render mesh reference.

## TileWorld prop-layer definitions

Add an opt-in `TilePropLayerDefinition` to `KhaozEngine.TileWorld.Render3D`. It names a set of archetype IDs and
the shared renderer profile:

```csharp
public sealed record TilePropLayerDefinition
{
    public required string Id { get; init; }
    public required IReadOnlySet<string> ArchetypeIds { get; init; }
    public required float DrawRadius { get; init; }
    public required float LodDistance { get; init; }
    public float LodCrossfadeWidth { get; init; }
    public float HlodDistance { get; init; }
    public float HlodCrossfadeWidth { get; init; }
    public float HlodWeldCell { get; init; }
    public bool CastsShadows { get; init; } = true;
}
```

`TileWorldViewOptions.PropLayers` defaults empty. Existing TileWorld consumers continue through ordinary
`DrawProps`. A selected archetype is removed from that ordinary submission path and appears in exactly one
cluster layer. Construction rejects duplicate selection across definitions so an object cannot double-draw.

## Immutable region snapshots

TileWorld publishes `TileRegionProps` snapshots keyed by region, plane, and content generation. A snapshot is
built from the authored objects plus presentation overrides. Background workers receive only the detached
snapshot and resolved CPU mesh data. They never read `TileWorldDocument`, override dictionaries, resolver caches,
or a live scene.

Object transforms keep the existing TileWorld anchor, footprint-center, height, yaw, scale, and handedness rules.
The LOD and HLOD path changes representation only.

`OverrideArchetype` replaces the affected region snapshot and invalidates that cluster generation. A chopped
tree can therefore enter the HLOD as a stump. A stale worker result carries its old generation and is discarded
without replacing the current handle.

## Region residency

Replace the one-class residency decision with three states:

- Gameplay: full ground, ordinary props, LOD0 and LOD1, picking bounds, and every current view behavior.
- Decor: coarse ground and HLOD-only prop clusters. No picking cache, ordinary prop batches, collision, or game
  object construction is added.
- Unloaded: no CPU snapshot or GPU handle remains after hysteresis.

The source document still streams whole authored regions. Residency decides which representation is built from a
loaded region. It does not change file format or server residency.

CPU work runs on background workers. Completed results enter a generation-tagged queue. The scene thread applies
nearest results first with independent per-frame caps for full ground, coarse ground, and HLOD uploads. Teleport
priming may drain the gameplay ring synchronously, but decor remains budgeted so a far horizon cannot stall one
frame.

Dirty authored regions remain resident under the editor rule. The map editor may opt out of asynchronous decor
and keep its deterministic synchronous profile. This program does not silently change editor interaction timing.

## Boundary-preserving ground LOD

Add `TileGroundLod.Full` and `TileGroundLod.Coarse4`. Full is the existing mesh.

`Coarse4` considers a four-by-four tile cell. It emits one coarse pair from global lattice corners only when the
whole cell is compatible:

- every tile exists and is non-void
- water and non-water do not mix
- no shaped overlay crosses the cell
- the material choice is uniform enough to preserve the authored boundary

An incompatible cell falls back to the existing per-tile triangulation inside that cell. Roads, river edges,
void boundaries, shaped overlays, bridge approaches, and material changes therefore keep their authored shape.
Large meadow and forest interiors collapse to the coarse grid.

Global corner sampling keeps adjacent region seams bit-identical. The existing central-difference normal rule is
evaluated at the retained global corners. Coarse ground uses the same material arrays and water-plane ownership as
full ground.

This is one coarse tier for the 600 metre camera. A later continent-scale camera may add another tier through the
same enum and mesher contract after measurement.

## First shared profile

The default remains unchanged. Grimhollow opts into this tree profile for one-metre tiles and 64-metre regions:

| Stage | Range | Representation |
|---|---:|---|
| LOD0 | 0 to 56 m | Full textured parts |
| LOD crossfade | 56 to 72 m | Complementary LOD0 and LOD1 dissolve |
| LOD1 | 72 to 176 m | Authored simplified parts |
| HLOD crossfade | 176 to 208 m | Individual LOD1 to region HLOD |
| HLOD | 208 to 544 m | One welded mesh per region and layer |
| Exit fade | 544 to 576 m | HLOD dissolve to empty |

Configuration values:

- draw radius 576 m
- LOD distance 64 m with 16 m crossfade
- HLOD distance 192 m with 32 m crossfade
- HLOD weld cell 1.5 m
- gameplay radius 4 regions
- decor radius 10 regions
- unload radius 12 regions

The HLOD handoff completes inside the gameplay ring, where individual LOD1 and HLOD geometry are both available.
The decor ring then carries only coarse ground and retained HLOD. The final fade completes inside the decor ring
and before the 600 metre camera far plane.

## Picking, collision, and authority

- Picking continues against full object bounds and stable document IDs.
- An object inside interaction range is in the gameplay ring and has individual representation.
- HLOD is never a target and creates no hover or click identity.
- Collision, navigation, object footprints, server simulation, replication, and world hashes remain derived from
  the authored full objects.
- Render LOD never adds or removes a gameplay object.

## Failure and disposal

- Missing LOD1 retains LOD0 through the full gameplay ring.
- Missing HLOD source keeps LOD1 and prevents decor-only transition for that cluster.
- An initial HLOD build retries a bounded number of times, then logs once and keeps individual geometry where it
  is resident.
- A rebuild failure retains the last accepted HLOD handle.
- Stale generations are discarded before GPU upload.
- Region unload disposes individual batches, HLOD handles, coarse ground, and snapshots exactly once.
- Scene disposal cancels workers, drains or rejects completed builds, then frees live handles.

## Tests

Terrain and shared renderer tests:

- Existing `Scene3DChunkSink` LOD and HLOD behavior remains unchanged after extraction.
- LOD crossfade is complementary for color and shadows and zero width preserves the hard swap.
- HLOD uses flattened LOD1 first and LOD0 as fallback.
- Cache reuse, replacement generations, retry, unload, and disposal are deterministic.
- Frame stats prove many individual placements collapse to one cluster instance.

TileWorld headless tests:

- Catalog parsing, schema validation, and cosmetic hash behavior for `lodMeshRef`.
- Full, LOD, and flattened resolver cache behavior with missing-input fallback.
- Gameplay, decor, and unload state transitions with hysteresis.
- Background results apply nearest first under their budgets.
- Selected prop layers never double-submit through ordinary `DrawProps`.
- An archetype override invalidates one region cluster and rejects stale work.
- Full-object picking remains exact while HLOD is present.
- `Coarse4` collapses uniform interiors and preserves road, water, overlay, material, void, and region boundaries.

GPU tests:

- LOD0 to LOD1 and LOD1 to HLOD boundaries retain visible coverage and shadow continuity.
- A far TileWorld forest draws over coarse ground with no empty annulus.
- Native Metal, Direct3D 11, and Vulkan resource-lifetime and golden gates pass.
- Repeated focus movement and unload do not increase live mesh handles after the ring settles.

## Documentation and release

Update the Terrain.Render3D and TileWorld.Render3D package READMEs, `docs/USING-KHAOZENGINE.md`, dependency seams,
and the existing LOD/HLOD usage references. The release closes issue 855 and is tagged automatically only because
Grimhollow is pinned and waiting on it.

## Non-goals

- Billboard impostors or an atlas packer.
- Offline HLOD artifacts or a bake tool.
- Changing TileWorld collision or server streaming.
- Adding more than one authored prop LOD tier.
- Requiring Ruinborne game-code migration in this release.
- Fog or distance hiding as a correctness mechanism.
