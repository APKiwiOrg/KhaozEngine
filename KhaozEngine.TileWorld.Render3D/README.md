# KhaozEngine.TileWorld.Render3D

The render arm of [KhaozEngine.TileWorld](../KhaozEngine.TileWorld): meshes a tile world's ground into
`Render3D` meshes for the tile-ground pipeline, builds that pipeline's material set from the ground catalog,
places its objects through the `Terrain.Render3D` prop path, puts its water bodies through the engine's water
pass, and owns the
per-region scene handles, region streaming and headless snapshot capture on top. Kept separate from the
render-free document so a server or tool never drags in `Render3D`. In the `Game3D` umbrella. Design:
[docs/design/TILE-WORLD-DESIGN-2026-08-15.md](../docs/design/TILE-WORLD-DESIGN-2026-08-15.md).

## World z is minus tile z

The document's convention is x east, z NORTH, y up. The engine renders right handed with y up, where a camera
facing +z has +x on its LEFT, so mapping north straight onto +world z draws the world mirrored against a compass.
`TileWorldSpace` (in `KhaozEngine.TileWorld`) is the one seam that negates it: **world z is minus tile z**, so
north is -z, which is also a right-handed camera's default forward, and (east, north, up) = (+x, -z, +y) stays a
right-handed triple. That is what lets one top-down view have north UP and east RIGHT at once instead of trading
one for the other. Every conversion here goes through `TileWorldSpace.WorldX/WorldZ/ToWorld`, and two consequences
fall out: a region-local ground mesh runs from 0 to MINUS 64 tiles on z, and an object's yaw is NEGATIVE per
quarter turn.

## Ground (`TileGroundMesher`, `ITileGroundSlotMap`, `TileColors`)

`TileGroundMesher.Build(doc, catalogs, region, plane, options)` returns the `GltfMesh` of one region-plane, or
null when the region-plane has no drawable tile (underlay 0, or `TileSettings.NoDraw`). The mesh is REGION LOCAL,
so draw it at `TileGroundMesher.WorldMatrix(doc, region)`, a pure translation to the region's lowest tile corner
with Y left at 0 because the vertices already carry absolute corner heights. Vertices are the existing
`ModelVertex`, with its fields repurposed for the tile-ground pipeline (`Scene3D.LoadTileGroundMaterial`), so
nothing in the upload path moves.

- **Four material slots per TILE, weights per vertex.** Every triangle of a tile carries the same four slots, one
  per corner in SW, SE, NW, NE order, as floats in `Uv.x`, `Uv.y`, `Tangent.x` and `Tangent.y`. `Color` is that
  vertex's four weights over them: one-hot at a corner, 0.5 and 0.5 at a mid-edge point of an overlay cut.
  `Tangent.z` is the brightness jitter and `Tangent.w` is 0. Slots per tile rather than per triangle is what keeps
  the ground continuous: a corner shared by four tiles is one-hot on the same material from all of them, and a
  shared edge interpolates the same pair from either side.
- **The corner material is the most-shared underlay.** `TileGroundMesher.CornerMaterial(doc, x, z, plane)` counts
  the up-to-four tiles sharing a lattice corner and takes the id most of them carry, ties broken by the LOWER id
  so every tile touching the corner picks the same one. Void tiles are the only exclusion: a `NoDraw` tile draws
  no ground of its own but still contributes its underlay, so the ground does not step at the edge of a hole
  punched for an object floor. `TileGroundMesherOptions.Slots` (an `ITileGroundSlotMap`) turns the id into the
  material set's layer slot, and an id the set does not carry lands on its reserved `MissingSlot`, whose layer is
  the magenta `TileGroundMesher.MissingMaterialColor`, so a dangling id is visible rather than invisible. The
  default `IdentitySlotMap` maps every id to itself, for a caller that has not built a set yet.
- **Jitter is per vertex, averaged at the corner.** `TileGroundMesher.CornerJitter` is the mean of
  `TileColors.Jitter` (a deterministic multiplier hashed from the world tile coordinate, plus or minus 4 percent
  by default, `JitterAmplitude` 0 disables it) over the same tiles, so the OSRS brightness variation stays soft
  across a corner instead of stepping per tile. It is a MULTIPLIER: no jitter is 1, and a vertex carrying 0
  renders black.
- **The colour path stays, and nothing in the mesh path reads it.** `TileGroundMesher.CornerColor` still blends
  the up-to-four sharing tiles' jittered material colours (`TileColors.Blend`, all-void blends to
  `TileColors.Void`), and `TileColors.Parse` still reads `#rrggbb` or `#rrggbbaa`. That pair is the GPU-free
  colour surface, for a caller that wants a tile's colour without the textured pipeline behind it: a minimap, a
  2D painter, a tool query. The vertices do not carry it any more, and the mesher itself only calls
  `TileColors.Jitter`.
- **Overlays are exact geometry, not an approximation.** The tile is cut by the shared
  `TileTriangulation.Triangulate` (two triangles for a plain tile or a diagonal half, four for a corner cut) and
  each triangle either names the overlay's slot in all four lanes at weight (1, 0, 0, 0) or is left to the tile's
  own corner materials. An overlay keeps the per-corner jitter, so a paved tile still varies softly rather than
  reading as one flat patch. The raycast in `KhaozEngine.TileWorld` calls the same function with the same inputs,
  so a click lands on the triangle that was drawn. A shape with no overlay material meshes as the plain pair.
- **Seamless by construction.** Normals come from the GLOBAL height lattice by central differences
  (`TileGroundMesher.CornerNormal`), which reads ACROSS region borders, so two regions meeting at a corner compute
  the identical normal and a border has neither a crack nor a lighting step. Set `SmoothNormals = false` for one
  flat normal per triangle instead. Vertices are never shared between triangles, because two triangles of one tile
  can carry different slots and weights (an overlay paints some of them and not others), so the mesher emits
  per-triangle vertices.

## Ground materials (`TileGroundMaterials`, `TileGroundMaterialSet`)

`TileGroundMaterials.Build(catalogs, load?)` turns a ground catalog into the `TileGroundMaterialSet` the meshes
are drawn with: one albedo layer per material in ASCENDING id order, then one reserved layer LAST filled with
`TileGroundMesher.MissingMaterialColor`. The set IS the `ITileGroundSlotMap` the mesher wants, so `SlotOf` answers
a catalog id with its layer and `MissingSlot` is that trailing layer, which is where a dangling id goes.

- **Textured or flat, one pipeline either way.** A material with a `Texture` is decoded (`load`, defaulting to
  `ImageRgba.Load`) and takes a WHITE tint, because the texture is the colour. A material with none becomes a flat
  layer of its catalog `Color` at the same size, so a colour-only world renders through the same textured pipeline
  and the same goldens, just without texture detail. The catalog colour stays what the headless readers use.
- **Paths resolve against the catalog FILE.** A relative `Texture` is combined with the directory of the catalog
  the material was declared in (`TileWorldCatalogs.MaterialSource`), the same rule `world.json` uses for its
  catalog paths. An absolute path is taken as written. A catalog built in memory has no directory to resolve
  against, so a relative texture on one throws a `TileWorldException` naming the material.
- **One size for the whole set.** Every layer is one slice of ONE texture array, so the first textured material
  decides the size and any other textured material of a different size throws, naming the material and both sizes,
  rather than being resampled behind your back. With nothing textured the flat fills are 1x1.
- `TilesPerMetre` is the material's own value or `TileGroundMaterials.DefaultTilesPerMetre` (0.5, a 2 m repeat).
  A catalog larger than `TileGroundMaterialConfig.MaxMaterials - 1` materials throws: the set is one uniform
  buffer, and splitting a catalog across several sets is not built.

Hand-build a `TileGroundMaterialSet` directly when the layers come from somewhere other than a catalog. The
constructor takes the size, the material id of each leading slot, and one layer per id PLUS the trailing reserved
one, and it refuses a layer that is not the size the set declares.

**A slot past the last layer clamps to the last real layer.** The tile-ground params carry the set's layer count,
and the fragment clamps each corner slot to `min(63, layerCount - 1)` before reading the uniform block or texture
array. A hand-built mesh naming a slot the set does not carry therefore shows the final material instead of
sampling a nonexistent array slice. Sets built by `Build` do not normally need the guard because the mesher only
emits slots supplied by the set.

**What a game does to get textures on the ground.** Two catalog fields and one rule about where the files live:

1. Give the material a `texture` in the catalog JSON. A relative path resolves against the DIRECTORY of the
   catalog file, so `"texture": "ground/grass.png"` beside `catalogs/ground.json` is
   `catalogs/ground/grass.png`. That means the catalog has to have been read from disk through
   `TileWorldCatalogs.Load(paths)`: one built by `LoadJson`, `Merge` or `Greybox` has no directory to resolve
   against and a relative texture on it throws.
2. Optionally give it a `tilesPerMetre` (texture repeats per world metre, floored at 0.01 by the schema). Omit it
   for `TileGroundMaterials.DefaultTilesPerMetre`, 0.5, which is a 2 m repeat and two tiles inside one copy of
   the texture.
3. Make every textured file the SAME pixel size. The set is one texture array, so the first textured material
   fixes the size for the whole catalog and any other of a different size throws rather than being resampled.

**Every layer costs that size, textured or not.** A flat colour layer is a full-size fill, because a texture
array's slices all match, so one 1024x1024 texture in a catalog makes each of its untextured materials a 4 MB
layer. Keep the ground textures modest (256 is plenty at this grain) or expect a 13-material catalog to cost tens
of megabytes for two real images.

## Water (`TileWaterPlanes`, `TileWaterLooks`)

Water is the engine's existing `Scene3D.DrawWater` pass laid over ground the author carved, so nothing new is
drawn: the depth tint, the shore foam band and the waterline feather all read the resolved scene depth. This
package only decides WHERE the planes go.

**Water is authored as ground.** A tile is water when its UNDERLAY material carries `GroundMaterialKind.Water`,
and the bed is sunk by lowering the corner heights, so the material's texture is the river BED and the surface is
computed rather than placed. Only the underlay counts: an overlay drawn in a water material is a puddle-shaped
decoration on ordinary ground and gets no surface, because an overlay cuts a fraction of a tile and a fraction of
a tile has no rim to take a height from. A `NoDraw` tile is not water either, since water over a hole has no bed
for the depth read to darken against. Collision, pathing, the raycast and the top-down painter are all unchanged:
`TileRaycast.Pick` still lands on the bed, which is what an editor click wants.

`TileWaterPlanes.Collect(doc, catalogs, region, plane, look?)` returns every `WaterPlane` one region-plane
contributes, in a deterministic order.

- **One surface height per body.** Water tiles are grouped into 4-connected components, discovered row by row from
  the region's south-west corner. Each component's surface is the MAXIMUM corner height over its tiles, which is
  the rim it shares with its bank, minus `SurfaceDropMetres` (2 cm). So a bed dug 70 cm down does not move the
  surface, and the bank sits 2 cm proud of it and stays dry. A river that DESCENDS is authored as separate bodies
  with a drop between them, the way OSRS does it, because one component is one height.
- **Disjoint rectangles, not one box.** Each component's tile mask is cut into maximal axis-aligned rectangles
  (row runs merged upward while their span is identical), one `WaterPlane` each. Rectangles rather than one plane
  per TILE because the pass draws a fixed 97 by 97 grid per plane whatever it covers, so a straight 3-wide river
  has to be one plane and a bend a few. Rectangles rather than one bounding BOX per component because a box
  over-covers at a bend, and the pass discards only once the ground is at or above the surface, so a ditch, a cave
  mouth or a sunk road cut inside that box would render as water. `Collect` asserts pairwise disjointness over
  everything a region-plane emits (two overlapping planes double-darken, the blend is depth-write off) and logs
  once past `PlaneCountWarnThreshold` (16), which is the signal that a river was drawn as a staircase of short
  runs where a few long ones would read the same. `Components` and `Rectangles` are public for a caller that wants
  the decomposition on its own, and `ToPlane` for one rectangle.
- **Region borders are safe by construction.** A component is clipped to its region and region rects are disjoint,
  so planes from neighbouring regions never overlap. A body crossing a border is two planes meeting at it, at the
  same height when the banks agree, and a mismatch shows as a 2 cm lip rather than being hidden.

`TileWaterLooks.River` is the shipped per-plane `WaterLook`: procedural waves off the shared FFT ocean, no swell,
a small normal strength, no surf, foam only in a narrow shore band, silty colours and a 0.8 m `ShallowDepth` so a
60 to 80 cm bed still darkens. It is modelled on Ruinborne's inland lake, and every value that differs from it
differs because of SCALE (the sea's 1.6 m foam band and 2.5 m ripple wavelength would each consume a river whole).
**It is one shared instance of a class of public fields, so do not mutate it**: copy it, or pass a look of your
own through `TileWorldViewOptions.WaterLook`.

## Objects (`TileObjectProps`, `ITileMeshResolver`, `GreyboxMeshResolver`, `GltfMeshResolver`)

`TileObjectProps.Build(doc, catalogs, region, plane, archetypeOverride)` turns a region-plane's `TileObject`s into
`TileRegionProps(Ground, Roofs)`, two `PropPlacement` lists for the existing prop path, split so the roof rule can
hide one and keep the other, plus `RoofFootprints`, the world tile rect each roof covers in the same order and
the same length, and `GroundObjectIds` / `RoofObjectIds`, the `TileObject.Id` behind each placement in the same
order again. Those id lists are what makes a per-object change findable at all: a placement names an ARCHETYPE
and carries nothing that says which object it came from. `archetypeOverride` is optional and is covered under
the view below. A placement carries a position and no extent, and the roof rule has to know which TILES a roof
sits over to tell whose building it is, so the footprints ride alongside. The property is init-only and empty by
default, so a record built by hand still compiles: a roof the list does not reach is never hidden by the interior
rule. Objects on another plane, and objects whose archetype the catalogs do not define, are
skipped rather than thrown on, because content outlives a catalog edit. `AnchorPosition` puts a placement at the
centre of the ROTATED footprint at the document's ground height there, so a mesh is authored centred on its own
footprint with its base at y 0. `YawRadians` is NEGATIVE per quarter turn
(`-(rotation * 90 + YawOffsetDegrees)` in radians). That sign is what makes `Matrix4x4.CreateRotationY` turn
CLOCKWISE seen from above with north up, the tile-world rotation convention (0 west, 1 north, 2 east, 3 south),
and the archetype's yaw offset folds in under the same sign.

`ITileMeshResolver.Resolve(archetype)` is where a game hands over its own meshes, keyed off the archetype's mesh
reference. Returning null means "no mesh for this archetype", which the view answers with a placeholder box and
one log line rather than a throw. `Resolve(meshRef)` is the same question for everything a game draws that is NOT
a tile object (a player avatar, an NPC, a dropped item), so reaching a resolver's cache and its fallback no longer
means synthesizing a fake archetype at the call site. It has a DEFAULT implementation that does that wrap once, so
an existing implementer gains it without changing, and a resolver that can do better overrides it. `GreyboxMeshResolver` is the shipped stand-in: one procedural vertex-coloured
box per archetype, sized from the footprint and shaped by the collision kind (a wall hugs the west edge, a corner
wall adds the north edge, a diagonal stands a post, a roof is a slab over the whole footprint), coloured from a
deterministic grey/brown/green palette by archetype id. Only the footprint extent scales with the tile size: every
thickness is absolute metres. It caches per archetype in a plain dictionary, so resolve on one thread.

Every greybox shape is PLANE-LOCAL: y 0 is the floor of the plane the object stands on, because `AnchorPosition`
already lifts a placement to that plane. A roof is placed on the plane ABOVE the walls it covers (that is what the
roof rule keys on), so its slab starts at y 0 too and the PLANE supplies the height, which is why
`GreyboxMeshResolver(float tileSize = TileWorldDocument.DefaultTileSize, float wallHeight = TileWorldDocument.DefaultPlaneHeight)`
makes a wall exactly one plane tall. Pass `doc.PlaneHeight` for `wallHeight` whenever a document is at hand, so a
world with a non-default plane height still has its walls meet its roofs.

### Picking the models (`TileObjectRaycast`, `TileObjectBoundsCache`)

`TileObjectRaycast.Pick(doc, catalogs, plane, origin, direction, maxDistance, bounds, hits)` names every
object whose drawn MODEL a ray passes through, nearest first (exact ties to the lower object id), so what a
player can click is what they can see, a well's roof included. It is the picture-side counterpart of
`TileRaycast` (the ground) and the footprint join (the tiles): the placement is derived through
`TileObjectProps.AnchorPosition`/`YawRadians`, the same transform a prop draw uses, and the box is slab-tested
in the object's local frame through `RayMath.IntersectObbY` (`KhaozEngine.Primitives`), so the oriented-box
math is shared rather than copied here. It decides nothing about clickability: hits carry the archetype id so the caller
applies its own gates (a hidden roof, a non-interactive archetype). `TileObjectBoundsCache` is the
`BoundsSource` to hand it: the per-archetype vertex AABB measured once from the SAME `ITileMeshResolver` the
view draws through, greybox fallback included.

### Real meshes (`GltfMeshResolver`)

`GltfMeshResolver(string rootDirectory, ITileMeshResolver? fallback = null, Action<string>? log = null)` is the
content-backed resolver. It maps an archetype's `MeshRef` to `Path.GetFullPath(Path.Combine(rootDirectory,
meshRef))`, loads that glb once through `GltfLoader.LoadPartsWithMaterials`, and caches the parts per MESH
REFERENCE, which is the entry both `Resolve` overloads share: an avatar drawn through `Resolve(meshRef)` and an
archetype pointing at the same glb parse it once between them, as do two archetypes sharing one `MeshRef`.
A `MeshRef` is authored RELATIVE with forward slashes (`kit/wall.glb`), normalized to the platform separator
here, and an already-absolute one is used as it stands. `PathFor(meshRef)` is public, for a tool that wants to
check a kit before a world is built. The cache is a plain dictionary, so resolve on one thread, as with the
greybox resolver.

Chain it over the greybox one and a half-authored kit still renders:

```csharp
var resolver = new GltfMeshResolver(kitRoot, new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight), log);
```

- **A missing file or a loader throw falls back and logs ONCE.** The line names the archetype (the path alone
  through `Resolve(meshRef)`, which has no archetype behind it), the resolved path and the reason, and then the
  answer is `fallback?.Resolve(archetype)`: the greybox box where the glb is not there yet, the real mesh
  everywhere else. With no fallback the answer is null, which the view already draws as its placeholder box with a
  line of its own.
- **The failure is cached like any other result**, so the second call for that reference re-logs nothing and does
  not go near the disk again. One line per mesh reference is the whole budget, however many times a world reloads
  the region it stands in, and however many archetypes point at it. What is NOT cached is the fallback's answer,
  which belongs to the ARCHETYPE rather than the reference, so a missing glb asks the fallback again per call.
  Both shipped fallbacks cache, so the same list still comes back.
- **An empty `MeshRef` is not a failure.** It goes straight to the fallback, silently, without building or
  probing a path, because an archetype nobody has modelled yet is an ordinary authoring state.
- **A `MeshRef` no path API will accept falls back too.** `Path.GetFullPath` throws on an embedded NUL, which a
  JSON catalog can carry, so the path mapping runs inside the same guard as the load. Nothing about bad content
  throws out of `Resolve`. `PathFor` is the unguarded form, for a tool that wants the exception.
- **The cache is keyed by archetype id, not by resolved path.** Two archetypes sharing one `MeshRef` parse that
  glb twice and hold two copies of it, and one missing file logs once per archetype rather than once per file.
- **There is no eviction.** Every cached part holds its decoded RGBA8 `GltfMaterialMaps` pixels for the
  resolver's lifetime, still resident after the view has uploaded them to the GPU, and a glb regenerated while
  the app is running is never picked up.

**A glb is drawn exactly as authored: nothing here scales, rotates or re-centres it.** So a kit piece has to meet
the same local-space contract `GreyboxMeshResolver.BuildMesh` builds to:

- **The origin sits at the footprint CENTRE, on the piece's own floor.** `AnchorPosition` puts the placement at
  the centre of the rotated footprint at ground height, so a mesh is centred in x and z with its base at y 0. A
  piece modelled with its origin at a corner lands half a tile off.
- **x is east, minus z is north, and 1 unit is 1 metre.** World z is minus tile z (`TileWorldSpace`), which is
  why the north face of a footprint is at `-z`.
- **y 0 is the floor of the piece's OWN plane, not the ground.** A wall is one plane tall (`doc.PlaneHeight`) so
  it meets the roof above it, and a roof sits at y 0 on its own plane, which is the plane above the walls it
  covers.
- **A wall hugs the `-x` face of its footprint at rotation 0**, and a corner wall adds the `-z` face, matching
  the greybox shapes, so a kit and the greybox stand-in read as the same world.

Colours come from the glb's own materials, or from per-vertex `COLOR_0`, which `GltfLoader` multiplies into the
material base colour. So a palette-painted kit needs neither textures nor a material per shade.

## The scene seam (`ITileWorldScene`, `Scene3DTileWorldScene`)

Everything the view does to a scene goes through `ITileWorldScene`: `LoadMesh`, `UnloadMesh`, `DrawMesh`,
`DrawOverlayMesh`,
`LoadPropMeshes`, `UnloadPropMeshes`, `DrawProps`, plus the ground-material trio `LoadTileGroundMaterial`,
`UnloadTileGroundMaterial` and the `LoadMesh(mesh, material)` overload that binds a mesh to the tile-ground
pipeline, `DrawWater(in WaterPlane)` for the water surfaces, and `DrawMeshSilhouette(handle, world, color,
widthMetres)` for the per-entity highlight rim (18.3.0). `DrawMeshDissolved(handle, world, dissolve, edgeWidth,
edgeColor)` queues a rigid mesh through `Scene3D`'s existing dissolve path with a white tint and opaque material.
`DrawOverlayMesh(handle, world)` reaches `Scene3D`'s translucent, unlit overlay pass. Its default implementation
throws `NotSupportedException`, so a legacy scene reports that it cannot provide translucency instead of silently
drawing an opaque mesh.
These six ship as DEFAULT interface implementations (an invalid handle, a no-op, a fall-through to the
material-free upload, two no-ops, and a fall-through to the solid mesh draw), so an implementation written before
textured ground, water, silhouettes or rigid dissolves existed keeps compiling. It draws untextured ground, no
water, no rims, and a solid body where a dissolve was requested. The view's own door is
`TileWorldView.SetSilhouettedObject(long objectId, Color color, float widthMetres = 0.05f)` /
`ClearSilhouettedObject()`: the flagged object's parts re-draw as hulls at the exact prop transform every
frame, resolved by id per frame from the loaded regions, and an id nothing loaded holds is a quiet no-op that
self-corrects when its region streams in. The hull reads the per-object archetype override below, since one built
on the authored archetype while the prop draws an overridden one sits on a different anchor and floats beside the
mesh it is outlining. It is shaped exactly on what `Scene3D` and the prop renderer
already offer, because its job is to let the view's bookkeeping run without a device, not to add an abstraction of
its own. `Scene3DTileWorldScene` is the shipped implementation and forwards straight through, and a test drives a
recording fake, which is how every view and residency rule is covered headless.

## The view (`TileWorldView`, `TileWorldViewOptions`)

`new TileWorldView(scene, doc, catalogs, resolver, options)` uploads the ground material set and one mesh set per
catalog archetype up front, so a region load is placements alone.

- **The ground material set is uploaded once and shared.** `TileWorldViewOptions.GroundMaterials` is the hook, and
  null builds one from the catalogs with no texture loader, which is every material as a flat colour layer. The
  view points `Options.Mesher.Slots` at whichever set it ends up with, because the slots a vertex names only mean
  anything against the set the mesh is drawn with, and it reads back as `GroundMaterials`. Every region-plane mesh
  goes up bound to it, and `Dispose` frees it.
- **Water is queued every frame, collected once per mesh.** `Draw` calls `DrawWaterPlanes()` after the ground and
  the props unless `TileWorldViewOptions.DrawWater` is false, and
  `TileWorldViewOptions.WaterLook` (default `TileWaterLooks.River`, null for the scene's own water settings) is
  the look every plane carries. Both knobs are on the OPTIONS rather than on the view, so a caller that never sees
  the view (`TileWorldSnapshot`, the `ke-tileedit` render verbs) can still set them. The planes of a region-plane
  are collected once and cached against the MESH handle they were collected with, so a frame that changed nothing
  is a walk over the loaded regions and one submit per plane: a remesh always comes back under a fresh handle
  generation, and every edit that can move a water tile or a corner height is exactly an edit that remeshes. Turn
  `DrawWater` off and call `DrawWaterPlanes()` yourself to put the surfaces at another point in your frame.

- **Authored foliage is cached per resident region.** `TileFoliageSurface` bilinearly samples a layer's
  world-metre density raster. Height and normal come from the same `TileTriangulation` triangle and interpolated
  lattice values as the rendered ground, then the surface rejects absent regions, disallowed visible materials,
  water, configured interiors and upper roofs, same-plane solid footprints and same-plane tagged door clearances.
  Shaped overlays use `TileTriangulation`, so only the painted part changes the visible material. Non-solid
  decorative objects remain eligible. `LoadRegion` builds `GroundCoverInstance` values once, dirty flushes
  rebuild affected caches and `UnloadRegion` drops them. `GeneratedCoverCount` and `LastDrawnCover` expose the
  cached and submitted counts. Live `TileWorldViewOptions.GroundCover` settings control distance, quality,
  distant thinning and shadow policy without regenerating positions. A world with no foliage performs no
  distribution work.

- `LoadRegion` / `UnloadRegion` build and free every plane of one region. Loading a region that is already loaded
  rebuilds it, so it doubles as a whole-region refresh. `LoadedRegions` is a snapshot, safe to walk while loading
  or unloading, and `LoadedRegionCount` is the count.
- `MarkDirty(region, plane)` queues one region-plane. `MarkDirty(worldRect, plane)` is the edit-facing overload: it
  grows the rect by `DirtyRegionMargin` (2 tiles) before turning it into region marks, because a corner height is
  shared by four tiles, a lattice normal is a central difference reading one corner further, and a corner colour
  averages the tiles meeting there, so an edit one tile inside a border genuinely changes the NEIGHBOUR's mesh.
  Active same-plane door clearances extend the ground-cover reach beyond that fixed mesh margin when needed.
  Marks coalesce, so a stroke that touches the same tiles a hundred times remeshes each region-plane once.
- `Flush()` rebuilds queued region-planes oldest first, up to `MaxRebuildsPerFlush` (default 16) of them, and
  `PendingRebuilds` counts what the budget deferred to the next call. `Flush(int)` overrides the budget for one
  call, and `int.MaxValue` is the settle-now form a loading moment wants. The budget bounds UPLOADS, not mesher
  CPU: a rebuild that produces no mesh does not spend it. A rebuild that THROWS is dropped along with everything
  completed ahead of it, so that region-plane keeps its previous mesh instead of being retried every frame.
- `Draw(focus)` flushes, then queues every loaded region: each plane's ground mesh at its world transform, that
  plane's ground props, and its roofs. `focus` is the point the prop draw radius (`PropDrawRadius`, 96 m) is
  measured from, which is the camera subject rather than the observer tile.
- **The roof rule, one building at a time.** `Observer` is the tile the rule is judged from and `ObserverIndoors`
  reports whether that tile carries `TileSettings.Indoors`. Standing indoors hides the roofs that sit on a plane
  ABOVE the observer's own AND cover the observer's own INTERIOR, which is the 4-connected flood fill of indoor
  tiles seeded from the observer's tile. So the storey the observer is on keeps its props, the ceiling between
  them and the camera goes, and the building next door keeps its roof. `IsRoofHidden(footprint, plane)` is the
  predicate itself, and `InteriorTileCount` reports the size of the interior the answer came from.
- **`RoofMode`** picks between `RoofVisibility.Interior` (the default above), `AlwaysVisible` (nothing is ever
  hidden, the map-authoring view) and `AlwaysHidden` (every roof on every plane goes, the OSRS "roofs off"
  setting, and the pre-18.10.0 indoor behaviour applied unconditionally). Wire it to the player's setting.
- **The interior fill is bounded** at `MaxInteriorTiles` (4096). A world that flags a whole region indoors by
  mistake would otherwise cost tens of thousands of settings reads on the frame the observer walks into it, so
  the walk stops there and every tile it did not reach is outside the interior: the failure direction is a roof
  left visible, never a stalled frame and never a throw. `InteriorTruncated` says it happened and
  `TileWorldViewOptions.Log` gets one line for the view's life.
- The interior is refilled lazily, when the observer's TILE changes, when the indoor flag under a stationary
  observer flips, or when anything is marked dirty. `MarkDirty` is the only edit channel the document has (it
  raises no events), so an editor painting `Indoors` gets the new interior on the next draw for free.
- **Per-object archetype overrides.** `OverrideArchetype(objectId, archetypeId)` draws one placed object as a
  different archetype, `ClearOverride(objectId)` puts it back, `ClearOverrides()` drops the lot,
  `TryGetOverride` reads one and `ArchetypeOverrideCount` counts them. The DOCUMENT is untouched, which is the
  point as much as the cost is: a client that swapped the archetype on its own world copy would answer every
  later pick, reach test and save out of the edit. Wire it straight to
  `TileWorldClient.ObjectStateChanged` / `ObjectStateCleared` to draw a chopped tree as a stump, and note it is
  not woodcutting-shaped: a damage state, a seasonal or day-night look and an editor's preview of a swap all
  ride it.
- **An override rebuilds one placement, never the ground.** `TileObjectProps.TryReplaceObject` recomputes the one
  object's anchor and splices it into the region-plane's placement list, with no remesh and no upload, because an
  archetype swap moves no vertex and no height. On a 931 object region-plane that is 0.016 ms, against 0.58 ms
  for a full prop rebuild and 31.6 ms for the region-plane remesh a `MarkDirty` plus `Flush` would have paid. It
  falls back to rebuilding that region-plane's PROPS (still no remesh) for the three ORDER questions it cannot
  settle: the object has no entry in this region-plane, the new archetype does not resolve, or the swap changes
  whether the object is a roof.
- An override for an object no loaded region holds is RECORDED and applies when the region streams in, the
  `SetSilhouettedObject` contract, so a server message that beat its region is not lost. One naming an archetype
  the catalogs do not hold draws nothing for that object, the same answer an unresolvable authored archetype
  already gets.
- **An override changes what is DRAWN and nothing else.** The document is untouched, so the server's target
  resolution and its baked collision map still read the AUTHORED archetype: an object drawn as a stump still
  answers an `Interact` as the tree and still blocks what the tree blocked. Refusing the action server side and
  suppressing the menu row client side are the game's,
  https://github.com/APKiwiOrg/KhaozEngine/issues/823 is the engine follow-on.
- The splice is O(placements on the region-plane) and copies the affected list, so N changes in one snapshot
  cost N splices rather than one. Fine at the rate a game depletes resource nodes, worth knowing before driving
  a whole-region seasonal swap through it: call `TileObjectProps.Build` once instead. There is no batch door.
- `Dispose` frees every region and every archetype mesh set the view uploaded. The scene is not owned.

## Streaming (`TileRegionResidency`, `TileResidencyConfig`)

`TileRegionResidency(source, view, config)` keeps a square ring of REGIONS resident around one observer tile,
materialising each through a `TileWorldSource` and handing it to the view. Radii are in regions at Chebyshev
distance, so `LoadRadius` 1 is the 3x3 block around the observer's own region. `UnloadRadius` must exceed
`LoadRadius` by at least one region: that band is the hysteresis that stops an observer pacing a border from
loading and unloading the same column on alternate frames. Use `TileResidencyConfig.Default`, because a defaulted
struct is all zeroes and the constructor refuses it.

- `Update(observer)` drops what has fallen past `UnloadRadius`, then loads up to `MaxLoadsPerUpdate` (default 2)
  of what is missing, nearest first with a deterministic tie-break.
- `PrimeAround(observer)` fills the whole ring IGNORING the budget and finishes with an unbudgeted flush, so a
  teleport settles in one call instead of drawing cracked borders for several frames.
- **Streaming a region dirties its dependants** in both directions. Ground meshes rebuild across their fixed
  border reach. Ground-cover caches also rebuild where a streamed solid footprint, upper roof or tagged door can
  change eligibility, including regions reached by the door clearance.
- A DIRTY region is never dropped: unloading one with unsaved edits would throw, so it stays resident past the
  ring and says so once through `Log`. Regions the manifest does not list are skipped and do not spend the budget.
- A torn region file throws `TileWorldException` straight out of `Update`. A world whose files no longer match
  what wrote them is not something a streaming loop should paper over by drawing a hole.

## Headless capture (`TileWorldSnapshot`)

Two RGBA8 captures over `Render3DSnapshot`, both building a throwaway view, loading the regions the shot can see,
settling every queued rebuild before the first frame, and rendering `CaptureFrames` (2) frames so nothing that
warms up over a frame is read back cold. Needs a headless GPU device.

- `CaptureTopDown(doc, catalogs, resolver, rect, plane, pxPerTile, options, configureScene)` is the orthographic
  map shot: the image is exactly `rect.Width * pxPerTile` by `rect.Height * pxPerTile` and one tile is exactly
  that many pixels square, set outright rather than framed, because a fit margin turns an exact scale into an
  approximate one. North is UP and east is RIGHT (`TopDownAzimuth` 0). `plane` chooses whose corner heights size
  the clip band, not what is drawn: every plane of every loaded region is drawn, and the observer stands on the
  top plane so the roof rule shows every roof.
- `CapturePerspective(doc, catalogs, resolver, eye, target, width, height, observer, options, configureScene)`
  shoots from an eye toward a target in world metres at a 60 degree vertical field of view, loading every region
  within `PerspectiveRegionRadius` (3) of the target's region. `observer` defaults to the tile under the target on
  plane 0, so a shot aimed inside a house hides that house's roof.
- `configureScene` runs LAST inside the capture's setup, so a caller's lighting, post or camera changes win over
  everything the helper set.
- Both need the document's regions MATERIALISED first, through `TileWorldFile.Load` or
  `TileWorldSource.EnsureLoaded`. A region the document does not hold is skipped rather than loaded, so a lazily
  opened world captures only the regions resident at the time and the rest come out as void.

## Tests and goldens

CPU tests and the four `[GpuFact]` goldens both live in `KhaozEngine.Render.Tests/TileWorld/`, the repo norm
(`GoldenCompare` is internal to that assembly). `tileworld_greybox` is the perspective shot with the observer
INSIDE the house, so it locks the roofs-hidden half of the rule, and `tileworld_topdown` is the map shot with the
observer above the world, so it locks the roofs-drawn half. `tileworld_textured` is the greybox world again under
a generated two-colour checker set, which is what holds the texture array, its mip chain and the per-layer tiling
rate, and `tileworld_river` is a carved channel with dirt banks, which holds the water pass (with `DrawWater` off
the same shot renders the channel as a flat blue ground strip). All four pass a flat background through
`configureScene`
(no starfield, no outline), so the comparison grid spends its cells on the tile renderer rather than on a
procedural sky. Golden images live in `KhaozEngine.Render.Tests/Gpu/goldens/`, one file per backend, and a new or
rebaked golden must be baked on D3D11 and Vulkan through the `cross-platform-gpu.yml` `bake=true` dispatch before
it can gate, or a Metal-only bake turns `main` red on every other backend.
