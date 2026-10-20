# Interior cut for props inside buildings

Status: implemented for [#1470](https://github.com/APKiwiOrg/KhaozEngine/issues/1470), correct on Metal,
Direct3D 11 and Vulkan, with its measured cost accepted under IC8. Approved on 2026-10-18 under owner rulings
IC1 to IC6, amended by IC7 and IC8 on 2026-10-19, orchestrator calls OC1 to OC10 and OC8 (a).
Consumer captures remain the IC6 adoption gate.

## As built

The rulings and API sketch below preserve the approved design history. The shipped contracts differ here:

- Captures configure `InteriorMeshes` and `RoofMode` through `TileWorldViewOptions`. Offline settling
  completes and publishes the requested interior field before the first frame and after camera re-centring.
- H1 sizes a 512 m window with `InteriorWindow.CellsFor(512f, cellSize)`, capped at 4096. The view reports
  `interior.window` once when the cap reduces coverage. H2 puts source-wide `InteriorEncoding` in
  `InteriorSourceResult.Encoding`. `Build(volumes, window, encoding)` takes it explicitly, and the adapter
  reports `interior.step` at collection, not after building a window.
- Membership uses `InteriorCutFrame` (Final-3), whose double render origin reduces the point, window minimum
  and height base before rounding to float32. Uniforms and CPU queries share its exact vectors. Addressing
  multiplies by the rounded reciprocal cell size, and height uses `q = (relativeY - relativeBase) * (1f / StepY)`,
  with `hi > lo && q >= lo && q < hi`. Frameless overloads use origin zero. `TryGetSpan` remains approximate.
  `FirstUncut` confirms cell and height crossings with that same predicate. TileWorld picks use the scene's
  current frame. Opted-in rigid outline masks interpolate render-relative positions directly, with no absolute
  coordinate round trip. Unopted dissolve draws keep their original absolute varying.
- TileWorld columns retain only their lowest contiguous run of retained indoor planes, never spanning a
  rejected storey or an outdoor gap (F3-1 and follow-up). Roofs apply only when that run reaches the column's
  highest indoor plane. Rectangle grouping also separates roof eligibility. Core supplied-volume merging
  still follows OC4. A highest-plane interior uses corner heights plus `PlaneHeight` for its fallback ceiling.
- Core tests live in `KhaozEngine.Foundation.Tests` (D1). `PropLayer` remains a public readonly struct (D2),
  and `DrawProps` remains a Terrain extension (D3). The existing public `TileCollisionBaker.WallFacing` is
  retained (D4). Skinned-only shadow fragments preserve the excluded paths (D5).
- Only the two terminal filtered `PropRenderer.DrawProps` overloads gain `cutInsideInteriors`, after
  `drawFilter` and `layerIdentity`. Their four convenience overloads forward false (D8). `Queue` and
  `DrawShadowOnlyProps` stay uncut. `PropLayer.CutsInsideInteriors` is preserved through its factories and copies.
- `TileWorldView.CutArchetypeIds` is the internal opted-in archetype union, refreshed with `PropLayers` (P2).
  The view publishes `AppliedInteriorCut` and the scene field together. Both picks and highlights consume it.
- `InteriorCutBinding` is the shared texture/sampler binding helper. The frame UBO is 1376 bytes with tails
  at 1344/1360, Model set 0 binding 14 follows atmosphere 11 to 13 and reuses sampler 6. Rigid outline and
  silhouette payloads are 192 bytes in 256-byte slots, tails at 160/176, flags in `Params.w`/`Params.y`.
- Contract assertions live in `InteriorCutContractTests` where the plan listed existing suites, and the
  separate motion vertex block remains 144 bytes (D7). The timing source admits only hosted NVIDIA T4
  adapters on direct3d11-native and vulkan-native. Local source tests do not execute it.

MapDoc remains [#1474](https://github.com/APKiwiOrg/KhaozEngine/issues/1474), and world validation remains
[#1473](https://github.com/APKiwiOrg/KhaozEngine/issues/1473). Living API use is documented in
[USING-KHAOZENGINE](../USING-KHAOZENGINE.md) and the five affected package READMEs.

## The problem

Props that cross into a building show their inside parts. In Grimhollow, trees reach through roofs and walls, and
the branches inside show in three cases:

- The observer is indoors and the roof rule hides the roofs above them (`RoofVisibility.Interior` in
  `KhaozEngine.TileWorld.Render3D/TileWorldView.Roofs.cs`).
- Roofs are off everywhere (`RoofVisibility.AlwaysHidden`).
- Through doorways and windows, with every roof drawn.

The owner wants trees beside doors and big trees screening buildings. Moving the trees, which is what the 21.2.0
clearance check (`TileWorldClearance`, [design](TILEWORLD-CLEARANCE-DESIGN-2026-10-16.md)) enforces at save
time, is therefore not the fix. That check stays available as an optional authoring tool.

## Owner rulings (2026-10-18)

- **IC1 Render-time cut, not authoring prevention.** The 21.2.0 clearance check stays optional.
- **IC2 Cut what is inside the walls and below the roof's top surface, nothing else.** A crown above the roof line
  stays, also while the roof is hidden indoors.
- **IC3 An interior height field around the observer.** Chosen over a top-down GPU roof depth map and a box per
  building, with owner scores of 40, 35 and 33. See [Alternatives](#alternatives).
- **IC4 A world-format-agnostic core plus one adapter per world format.** This round ships the core and the
  TileWorld adapter. The MapDoc adapter belongs to world authoring R8 (native MapDoc rendering, not started), and
  this document only writes its contract.
- **IC5 Opt-in per prop layer.** A new `TilePropLayerDefinition` setting, `CutInsideInteriors`. Grimhollow turns
  it on for its trees layer. The shadow pass cuts too, so a cut part casts no shadow. HLOD merged meshes and
  grass or foliage batches are never cut.
- **IC6 Owner look before the Grimhollow adoption lands.** Before and after captures on the Grimhollow world from
  before its four trees moved (the chapel oak through the chapel roof), with roofs hidden, roofs on and a doorway
  view. That is consumer work. The engine release enables it.
- **IC7 Gate by per-instance relevance (2026-10-19).** Hosted T4 run 39160690665 measured 0.73 to 0.84 ms
  per frame against 0.1 ms, with routing accounting for the entire cut cost. Only opted-in instances whose
  drawn bounds can reach an interior take the cut path. Cache by world transform and field version. The
  0.1 ms budget applies to TypicalVillage, with 3600 trees, 49 buildings and about 2 percent reaching trees.
  DenseVillage retains the original layout and is recorded without a budget gate.
- **IC8 Accept the measured cost (2026-10-19).** After three tuning rounds, hosted run 39196852937 measured
  TypicalVillage at 0.079 ms on Linux Vulkan, 0.116 ms on Windows Vulkan and 0.203 ms on Windows Direct3D 11,
  with the GPU part near zero on every leg. The owner accepted this cost for the release. The remaining CPU
  cost of about 10 to 19 ns per opted-in instance, highest on Direct3D 11, is tracked in
  [#1502](https://github.com/APKiwiOrg/KhaozEngine/issues/1502).

## Orchestrator calls approved with the design

- **Cells of 25 cm**, snapped to the tile lattice under OC3.
- **A window centred on the observer**, built on the CPU off the render thread and uploaded as one texture,
  re-centred when the observer moves 64 m from the centre and rebuilt when the world's interiors change. The
  approved size was about 384 m. OC1 replaced it with 512 m so the full 192 m tree range stays covered through the
  drift.
- **One span per cell**, from the lowest indoor floor up to the roof's top surface. The roof surface is the
  LOWEST roof sample in the cell, so the cut never rises above the roof. A sliver of leaves just under the roof
  may remain, visible only with the roof hidden. Stacked storeys under one roof are one span. An indoor area with
  no roof above it (a courtyard) is cut up to the top of its top storey.
- **Reuse the 21.2.0 mesh reading and surface sampling** (`ClearanceMesh`, the sampling behind `ClearanceSolid`,
  `ITileMeshGeometry`, `GltfTileMeshGeometry`) for the roof surface, with no second mesh path. The choice between
  a lowest-sample `ClearanceSolid` and a separate rasterizer is made in [Roof surface sampling](#roof-surface-sampling).
- **A scene in which no layer opts in renders byte-identical**, so every existing GPU golden holds. The precedent
  is the instanced dissolve's appended field and gated branch ([design](INSTANCED-DISSOLVE-DESIGN-2026-07-20.md)).
- **A GPU cost budget of 0.1 ms** on a tree-heavy village view, covering the colour pass and the shadow pass
  together.
- **One engine minor after 21.4.0.** No version is chosen here.

## Orchestrator rulings on the open items (2026-10-18)

- **OC1 (c)** A 512 m window, 2048 cells per side, re-centred at 64 m. The window is twice the 192 m cut range
  plus the drift, so full-detail trees stay covered on every side between re-centres.
- **OC2 (a)** Cascade and point shadows both cut.
- **OC3 (a)** The TileWorld adapter picks the largest cell at or below 25 cm that divides the tile size exactly.
- **OC4 (a)** Interiors that share a cell merge to the lowest floor and highest top, and the build counts merges.
- **OC5 (a)** A roof with no readable mesh falls back to the flat ceiling and reports `interior.roof-mesh`.
- **OC6 (a)** Adapter issues are returned and logged once by the view in this minor. Surfacing them in
  ke-tileedit's `world_validate` is tracked in #1473.
- **OC7 (a)** Packed `R8G8B8A8UNorm` texels.
- **OC8 (a)** Approved by the owner. See [Owner decision on OC8](#owner-decision-on-oc8-2026-10-18).
- **OC9 (a)** The window centres on the prop draw focus.
- **OC10 reversed to (b) and widened.** A cut part is not there for the player, so everything in this round that
  reacts to an opted-in prop's pixels honours the cut: object picking and the camera boom that uses it, walk
  surface picking, the target outline and the silhouette. See
  [Cut-aware picking, outlines and silhouettes](#cut-aware-picking-outlines-and-silhouettes).

## Shape of the solution

Three layers, each in the package that already owns its concern:

1. **Core, `KhaozEngine.Primitives`, namespace `KhaozEngine.Primitives`, folder `InteriorCut`.** Interior
   volumes in, a packed height field out, plus the CPU queries picking needs. It knows polygons, heights and roof
   samples, never a document type. It sits beside the `Clearance` core for the same reason that core sits there:
   `KhaozEngine.TileWorld` and a later MapDoc adapter both reach it without a reference cycle, and it reuses
   `ClearanceMesh` and `ClearanceSamples` directly.
2. **Adapters.** `TileWorldInteriors` in `KhaozEngine.TileWorld` turns a TileWorld document into volumes. A MapDoc
   adapter does the same in R8.
3. **Render, `KhaozEngine.Render3D`.** `Scene3D.InteriorCut` takes a built field, uploads it and binds it. A
   per-instance flag opts a draw in. The colour, temporal, cascade shadow, point shadow, target outline and
   silhouette passes discard cut fragments.

`KhaozEngine.TileWorld.Render3D` drives it: it owns the window, re-centring, change detection and the background
build, carries the layer opt-in down to the draws, and applies the same field to its picks.

## The interior description and the producer seam

```csharp
namespace KhaozEngine.Primitives;

/// One interior to cut: a simple polygon in world XZ, a floor, a flat ceiling and the roofs over it.
public sealed record InteriorVolume
{
    public required IReadOnlyList<Vector2> Outline { get; init; }   // world (x, z), simple, either winding
    public required float FloorY { get; init; }                     // lowest indoor floor, world metres
    public required float CeilingY { get; init; }                   // flat top where no roof sample lands
    public IReadOnlyList<InteriorRoof> Roofs { get; init; } = [];   // roof surfaces over this interior
}

/// One roof surface: its mesh samples in mesh-local metres and where it is drawn.
public readonly record struct InteriorRoof(ClearanceSamples Samples, Matrix4x4 LocalToWorld);

/// A producer of interiors for one window. One implementation per world format.
public interface IInteriorSource
{
    /// Every volume whose outline can touch window, plus issues the source found while reading.
    InteriorSourceResult Collect(InteriorWindow window);
}
```

- **Outline** is a polygon rather than a rect list because MapDoc rooms are polygons with free yaw. A TileWorld
  building becomes a few rects, each a four-point polygon. A polygon with holes is not accepted. MapDoc pieces are
  simple rings (`MapWaterPiece.Ring` in `KhaozEngine.MapDoc/Water/MapWaterDomain.cs`), and a TileWorld courtyard
  hole is just tiles left out of the rects.
- **FloorY** and **CeilingY** are flat per volume. That matches a MapDoc prism (`LowerY`, `UpperY`), and for a
  TileWorld building on a slope FloorY is the lowest floor corner under the volume. Cutting below a floor removes
  only geometry the floor already hides.
- **CeilingY** is the top wherever no roof sample lands in a cell. It covers the courtyard rule, a roof mesh that
  failed to load (OC5) and the cells a sampled roof misses at its edges. It is never above the roof, because a
  roof stands on the plane whose floor is CeilingY.
- **Roofs** carry `ClearanceSamples`, the public 21.2.0 sample set, so the core never loads a mesh and each format
  brings its own mesh reader.

## The height field

### Layout and texel format

One 2D texture, one texel per cell, rows along +world Z and columns along +world X from the window's minimum
corner. Each texel holds one span as two 16-bit quantized heights, floor and top, measured in steps above the
field's base height.

The texel format is `R8G8B8A8UNorm` (OC7) with the floor's high and low bytes in R and G and the top's in B and A.
`GpuPixelFormat` (`KhaozEngine.Gpu/GpuEnums.cs`) offers `R8G8B8A8UNorm`, `R32Float`, `R8UNorm`,
`R16G16B16A16Float` and `R16G16Float`, and nothing with two 16-bit integer or normalized channels.

- `R16G16Float` keeps 11 bits of mantissa, so a span 100 m above the base resolves only to 6 cm and gets worse
  with height. A cut boundary that moves 6 cm with altitude is visible on a roof line.
- Two `R32Float` textures, or one `R32Float` with interleaved rows, double the memory and add a binding or an
  address calculation for no gain over 16 bits.
- Adding `R16G16UNorm` to the GPU seam is engine work on three backends for a format only this feature uses.
- Packed `R8G8B8A8UNorm` is exact: each byte decodes as `round(v * 255)`, so the shader rebuilds both integers
  bit for bit on every backend.

The step is 1 cm, giving 655.35 m of vertical range inside one window, which covers any plausible set of
interiors within 512 m. When a window's spans need more, the step grows to `range / 65534` and the build reports
it. Heights are quantized conservatively: the floor rounds down (the cut reaches at most one step below a floor,
where nothing is visible) and the top rounds down (the cut never rises above the roof).

A texel whose top is not above its floor is empty. A zero texel is empty, so a cleared texture and the 1x1 zero
default both cut nothing.

The field is read with `texelFetch`. Filtering would blend a span with an empty neighbour into a height that
belongs to neither, so the read is unfiltered by construction. The engine's shaders write a fetch as
`texelFetch(sampler2D(Tex, Samp), coord, 0)` (`ShaderSources.Decal.cs`), and each pass pairs the texture with a
sampler it already binds or one bound beside the texture.

### Cell size

**As built (Final-2).** `InteriorLattice` owns the represented cell edges. Snapped window corners, roof
clipping and whole-cell polygon coverage reconstruct the integer world lattice index before multiplying
by the cell size. The TileWorld adapter emits tile edges as tile index times cells per tile through that
same helper, including negative Z. It never reconstructs them with `tileIndex * TileSize`. Off-lattice
polygons keep strict inward coverage, without an epsilon. Adapter-to-field tests retain all 25 cells of a
1.2 m tile and all 16 cells of a 1 m tile, at both signs and across a 64 m re-centre.

The core takes any cell size. The TileWorld adapter picks the largest cell at or below 25 cm that divides the
document's `TileSize` exactly (OC3): 25 cm for a 1 m tile (`TileWorldDocument.DefaultTileSize`), and for example
`1.5 / 6 = 25 cm` or `1.2 / 5 = 24 cm`. Tile edges then fall on cell edges and a TileWorld outline is exact.

### Window and re-centring

- The window is square, 2048 cells per side at 25 cm, 512 m (OC1). Its minimum corner snaps to the cell lattice
  anchored at world origin, so re-centring never moves a cell boundary in the world. Where the old and new windows
  overlap the new field is identical, so a re-centre never pops the cut.
- The centre is the focus the prop draw radius uses (`ITileWorldScene.DrawProps` takes `focus`), so the cut and
  the full-detail draw share one centre (OC9).
- Re-centre when the focus is 64 m or more from the centre along either axis. The window's half-width of 256 m is
  the 192 m tree range plus that drift, so trees at full detail stay inside the window on every side.

### Threading

- The view thread captures what the adapter reads into a detached copy, following
  `TileWorldBuildSnapshot.CaptureGround` (`KhaozEngine.TileWorld.Render3D/TileWorldBuildSnapshot.cs`), because
  the document is mutable under an editor.
- A new `TileWorldBuildKind.InteriorCut` on the existing `TileWorldBuildQueue` runs collection and rasterization
  off the render thread. At most one interior build is in flight, and a newer request supersedes a queued one.
- The finished field is applied on the view thread through `ITileWorldScene`. `Scene3D` uploads it once at the
  next frame with one `UpdateTexture` of the whole texture, and the view's picks switch to the same field at the
  same moment, so a click never disagrees with the picture.
- Until the first build lands, nothing is cut and nothing is skipped by picking. A late field leaves branches
  visible for a few frames, never cuts the wrong thing.

### Memory and upload

- GPU: `2048 * 2048 * 4` bytes, 16 MiB.
- CPU: the applied field stays resident for upload and for picking, plus one buffer under construction, so
  32 MiB of packed texels at peak, plus a 0.25 MiB coarse overlap summary per 2048-cell field.
- Each re-centre and each interior rebuild uploads the whole 16 MiB in one frame.
- A scene with no field allocates only the 1x1 default.

### Rasterization

For each volume, the builder fills every cell that lies wholly inside the outline. A cell straddling an edge is
left uncut. Cutting it would remove branch pixels outside the wall, which IC2 forbids, so the error always falls
inside. With the cell snapped to the tile (OC3) a TileWorld outline has no error at all. A free-yaw MapDoc edge
leaves at most one cell diagonal, 35 cm at 25 cm cells, inside the outline uncut.

Each filled cell takes `[FloorY, top]`, where top is the lowest roof sample in that cell or `CeilingY` when no
sample landed. Where two volumes fill one cell, the spans merge to the lowest floor and the highest top, and the
build result counts the merged cells (OC4). One span per texel stays the format. A second span per texel waits
until a world needs it.

## Roof surface sampling

**As built (Final-1).** The lowest discrete sample rule below is superseded. The rasterizer reads the
validated `ClearanceMesh` triangles retained by `ClearanceSamples`, through the same `ITileMeshGeometry`
path. It transforms each triangle, clips it against each cell's XZ rectangle and takes the minimum linear
height over the clipped polygon's vertices. The result is rounded downward before height packing. Unlike
an unaligned sample grid, this bounds the roof everywhere in the cell. Closed slab undersides and vertical
gable faces still lower the cut. A cell with no triangle intersection, including an unreadable roof,
keeps `CeilingY`. The shifted 1 cm sloped slab regression checks every cell's low side.

The roof surface reuses the 21.2.0 sampling. `ClearanceSamples.Build(mesh, pitch)` samples every triangle in rows
along its longest edge, no coarser than the pitch, and keeps the points in mesh-local metres
(`KhaozEngine.Primitives/Clearance/ClearanceSamples.cs`, `ClearanceMesh.ForEachSample`). The core transforms each
point through the roof's `LocalToWorld` and lowers the top of the cell it lands in.

**Decision: a separate small rasterizer over the same samples, not a lowest-sample variant of `ClearanceSolid`.**

- `ClearanceSolid` is a grid in the mesh's LOCAL frame, axis-aligned to the mesh and built once per mesh
  (`ClearanceSolid.Build`, `SampleCell`). The cut needs cells on the world window lattice after the roof's yaw.
  Reading a local grid into a rotated world grid quantizes twice and widens the error band at every roof edge.
- `ClearanceSolid` also computes a chamfer distance and a base the cut never reads. Changing its top rule, or
  adding a mode flag, would put a second contract on a shipped type to save about thirty lines.
- The rasterizer keeps the solid's sampling density: `ClearanceSolid.Build` samples at half its cell size
  (`mesh.ForEachSample(cellSize / 2f, solid.Mark)`), and the cut samples roofs at half the field's cell.
- Samples are built once per roof mesh and cached by the adapter, so a re-centre only re-transforms them.

Error band of the roof surface, with c the cell size:

- The lowest sample in a cell over a sloped roof sits up to about c times the slope's tangent below the roof
  surface at the cell's high side. At 45 degrees and 25 cm cells that is 25 cm of leaves left under the roof at
  most, hidden whenever the roof is drawn. This is the approved sliver.
- A roof mesh is a closed slab, so the lowest sample in a cell is usually its underside, a roof's thickness below
  the top surface. Same direction, same visibility.
- A vertical face in the roof mesh (a gable end, a chimney, a dormer cheek) pulls its cells down to the face's
  lowest point, so the gable-end row of cells stops at eave height. Leaves in that row of attic stay visible with
  the roof hidden.
- A roof mesh that also models a flat ceiling caps the cut at the ceiling, leaving the attic uncut.
- A cell a sloped triangle covers only partly can receive no sample. It falls back to `CeilingY`, which is below
  the roof.

All five fail toward leaving branches, never toward cutting above the roof.

## The TileWorld adapter

`TileWorldInteriors` in `KhaozEngine.TileWorld` implements `IInteriorSource` over a detached document copy, its
catalogs and an `ITileMeshGeometry`.

### Facts it builds on

- `TileSettings.Indoors` is a per-tile, per-plane flag (`KhaozEngine.TileWorld/TileLayers.cs`). The roof rule
  floods it from the observer's tile (`TileInteriorFill`) but the cut reads it everywhere in the window and never
  looks at the observer. The cut is static.
- Plane heights come from the lattice: `TileWorldDocument.HeightAt(worldX, worldZ, plane)` and `CornerHeight`
  (`TileWorldDocument.Heights.cs`), with `PlaneHeight` defaulting to 3 m (`TileWorldDocument.DefaultPlaneHeight`).
- A roof is an archetype with `IsRoof` (`TileWorldCatalogs.cs`), placed one plane above the walls it covers, with
  its shape only in the mesh. Its world transform is `TileObjectPlacement.LocalToWorld`, the one the prop renderer
  draws with, and its footprint is `TileFootprint.Of`.
- A wall is an archetype whose `CollisionKind` is `Wall` ("blocks one tile edge, the one its rotation faces") or
  `WallCorner` (two edges). `TileCollisionBaker` maps rotation to that edge with `WallFacing` and rotation 0 is the
  west edge. The greybox resolver documents the same convention for its wall slab, 0.15 m thick on the west edge
  of its footprint (`GreyboxMeshResolver.WallThickness` in `TileMeshResolver.cs`).

### Volumes

1. For every column (x, z) in the window, find the lowest and the top indoor plane.
2. Group indoor columns into maximal rects that share both planes, scanning rows. Each rect is a volume with a
   four-point outline in world metres through `TileWorldSpace`.
3. FloorY is the lowest corner height of the rect on its lowest indoor plane. CeilingY is the lowest corner
   height of the rect on the plane above its top indoor plane, which is where that storey's roof stands.
4. Roofs are the `IsRoof` objects on the plane above the top indoor plane whose footprint intersects the rect,
   each with its `LocalToWorld` and the cached samples of its `MeshRef`.

Two storeys under one roof are one rect running from the ground floor to that roof, as approved. A roof whose
glb eaves overhang the footprint (up to 0.30 m in Grimhollow) contributes samples outside the rect, which the
rasterizer never reads, so eaves are never cut.

### Walls and the outline

Grimhollow flags each building's whole footprint, wall tiles included, and puts walls on the outer edge of the
boundary tiles. The outline is the outer edge of the boundary tiles, which is then the outside face of the wall,
and the cut stops there.

A wall on the INNER edge of a boundary tile breaks that: the outline is still the tile's outer edge, almost a
whole tile outside the wall, and the cut would remove branches outdoors. The adapter refuses to do that silently:

- For each `Wall` or `WallCorner` edge on an indoor tile T, take the edge of T opposite it. When the tile across
  that opposite edge is not indoors on the same plane and no wall owns the opposite edge, the wall is on the
  inner edge of a boundary tile.
- The adapter reports `interior.wall-inner-edge` naming the object, tile and plane, and removes T from the
  volumes, so the cut there falls back to nothing rather than to the wrong side of the wall.
- A tile carrying a `Diagonal` wall on the boundary is reported as `interior.diagonal-wall` and removed the same
  way. Splitting the tile along its diagonal is deferred.

The rule shares `WallFacing` with the collision baker (made internal to the package, not public) so the cut and
collision always agree on which edge a wall owns.

### Issues

Issues reuse `TileWorldIssue`, as the clearance check does:

- `interior.wall-inner-edge` and `interior.diagonal-wall`, above.
- `interior.roof-mesh`: a roof archetype in the window has no `MeshRef`, or its mesh cannot be read. Reported once
  per archetype. Its volumes keep their `CeilingY` top and the build goes on (OC5), which matches the clearance
  check's per-archetype mesh issue and fails toward leaving branches.
- `interior.step`: a window's spans exceed 655.35 m and the step grew.

The adapter returns its issues and the view logs each new one once (OC6). Surfacing them in ke-tileedit's
`world_validate` is tracked in #1473. A save is never refused over them, because a cut that falls back
to nothing is safe.

### Change detection

A rebuild is due when the window re-centres or when any region-plane in the window changes what the adapter reads:
an Indoors flag, a wall or roof object, or a corner height on an indoor or roof plane. The driver keeps a digest of
those inputs per region-plane, compared on the view's existing region rebuild path, so an unrelated edit (a tree
moved, a ground material) queues nothing.

## Render integration

**As built (IC7).** `Scene3D` gates an opted-in rigid submission through one shared relevance function,
also used by rigid outlines and silhouettes. It tests the same transformed mesh sphere as the prop cull
against `InteriorHeightField.MayIntersect`. Rigid meshes have no sway displacement, and animated foliage
stays excluded. The immutable field builds minimum-floor and maximum-top code spans per 8 by 8 cells.
The query expands its footprint by one cell and bounds height in the shader's float32 code space using
`InteriorCutFrame`, widening endpoints by one represented code value. It checks blocks instead of cells.
Answers are cached by mesh generation and world transform for the field version and render origin.
Stable submission slots retain the per-placement flag without hashing the transform again each frame.
Changed slots consult the placement cache, so reordered or temporarily absent placements keep their answers.
The field version and render origin are checked once per `Begin` and in the `InteriorCut` setter, not per draw.
A retained slot answers before any mesh validation or bounds read, comparing handle index and generation
directly and reading the stored transform in place, so an unchanged opted-in draw allocates nothing.
Colour, shadows and outlines use zero outward margin. Silhouettes expand the transformed sphere by their
world-metre hull width and cache that separate answer with the margin in its key.
Static placements retain their answer across absent frames. Unloading a mesh removes its cached placements.
Requests are retained separately from effective flags. Replacing the field after submission refreshes all
three queues before packing, preserving the setter's next-render contract. An unchanged field and origin
do not walk those queues again.
No field or a missed field leaves `SpecParams.w` zero and opaque casters on their original depth-only path.
Colour, cascaded and point shadows consume that same gated submission. Outlines reuse its cached query.
Instance packing stably partitions reaching instances first within each mesh. All-miss streams preserve
their original order and bytes. Relevance therefore adds at most one caster span per mesh before visibility
culling, rather than splitting a span at every alternating flag. Motion keys and caster policies follow
the same scatter as the instance data.
With visibility culling enabled, the run-count limit is asserted for TypicalVillage under IC7.
DenseVillage culled counters are recorded. Coherence-preserving packing is tracked in
[#1498](https://github.com/APKiwiOrg/KhaozEngine/issues/1498).
Picks keep the archetype-set route, since an instance whose drawn bounds miss cannot lose a picked point.

### The opt-in route

`TilePropLayerDefinition.CutInsideInteriors` flows to `PropLayer.CutsInsideInteriors` in
`KhaozEngine.Terrain.Render3D`, which `PropClusterRenderer.SceneBackend.DrawProps` passes into `Scene3D.DrawProps`,
which requests `RigidInstanceDraw.CutInsideInteriors` for individual instances, the authored LOD parts included.
The scene keeps the flag only where the drawn bounds may reach the applied field. The
merged HLOD draw (`SceneBackend.DrawMerged`) never sets it.

**Decision: the flag rides in `InstanceData.SpecParams.w`.** Options weighed:

| Route | Byte-identical with no opt-in | Batching | Cost | Verdict |
|---|---|---|---|---|
| `SpecParams.w`, 0 or 1 | Yes, every packer writes 0 today | Unchanged | None | **Chosen** |
| A new instance attribute | Grows the 128-byte stream | Unchanged | Breaks the attribute budget | Refused |
| A packed bit in another lane | Yes | Unchanged | Bit tricks on floats in every shader that reads the lane | Rejected |
| A per-draw uniform | Yes | Splits mixed runs | A uniform update per run | Rejected |
| A pipeline variant | Yes | A switch per run, and mixed runs split | Another pipeline per pass | Rejected |

- `SpecParams.w` is unread by every shader. `x` is specular strength, `y` shininess and `z` the alpha-cutout
  threshold (`ModelRenderer.InstanceData`, `ShaderSources.Model.cs`). The rigid and skinned packers write `w = 0`
  (`Scene3D.InstanceGrouping.cs` and `Scene3D.SkinnedSubmission.cs`), the foliage vertex writes
  `vSpecParams = vec4(0.0, 32.0, FoliageParameters.w, 0.0)` (`ShaderSources.Foliage.cs`), and
  `TileWorldSceneSkinnedTests` already pins it at 0.
- `InstanceData` is exactly 128 bytes and full (`ModelRenderer.cs`, `SizeInBytes = 128`). A new attribute is not
  available either: the temporal vertex puts `IMotionSlot` at location 15, "the one attribute Vulkan guarantees
  above the instance stream's 5 to 14" (`ShaderSources.MotionModel.cs`).
- The instanced dissolve design kept `SpecParams.z` and `w` out of its own field to avoid the cutout lane. The
  `w` lane was free then and is free now.

Grass, ground cover and animated foliage draw through `DrawGroundCover` (`TileWorldView.Foliage.cs`,
`TileWorldView.AnimatedFoliage.cs`). Its GPU path draws a `FoliageBatch` whose vertex writes `w = 0`, and nothing on
its CPU path sets the flag, so they are never cut by construction, which is IC5's exclusion.

### Frame data

| Value | Where | Notes |
|---|---|---|
| Window minimum (x, z), render-relative | `InteriorCutWindow.xy` | Computed on the CPU in double against `RenderOrigin` |
| 1 / cell size | `InteriorCutWindow.z` | |
| Cells per side, 0 when no field | `InteriorCutWindow.w` | 0 turns the test off in every pass |
| Base height, render-relative | `InteriorCutHeights.x` | |
| 1 / step | `InteriorCutHeights.y` | |

The two vec4 ride in each pass's existing per-frame or per-draw uniform block:

- **Colour and temporal.** The frame block `U`. The two vec4 append after the cluster tail, which is how every
  tail since the shadow tail was added: `UboBytes` goes from 1344 to 1376 and every existing offset is unchanged
  (`ModelRenderer.cs`, `ClusterTailOffset`). This spends no new uniform buffer, and the heaviest shipped pipeline
  stays at six of the eight dynamic uniform descriptors Vulkan guarantees
  (`VulkanDescriptorLimitTests.TheHeaviestShippedPipeline_SpendsSixDynamicUniformDescriptors`).
- **Cascade shadow.** The 256-byte slot per cascade holds the matrix at 0, `RenderOrigin` at 64 and
  `DissolveParams` at 80 (`ShadowMapRenderer.cs`, `CascadeSlotBytes`). The two vec4 go at 96 and 112.
- **Point shadow.** The 256-byte slot per light face has a 144-byte payload (`PointShadowRenderer.cs`,
  `FaceSlotBytes`). The two vec4 go at 144 and 160.
- **Target outline.** The 256-byte draw slot holds `ViewProj` at 0, `World` at 64, `Params` at 128 and
  `RenderOrigin` at 144 (`TargetOutlineRenderer.DrawUbo`, `DrawSlotBytes`). The two vec4 go at 160 and 176.
- **Silhouette.** The 160-byte payload in a 256-byte slot (`SilhouetteRenderer.cs`, `PayloadBytes`, `SlotBytes`)
  grows to 192 with the two vec4 at 160 and 176.

All positions are render-relative, like every GPU-bound position, so the test runs on the render-relative world
position and precision follows camera-relative rendering.

### Bindings

- **Colour and temporal passes.** The Model layout gains one texture, `InteriorCut`, at set 0 binding 14, after
  the atmosphere block at 11 to 13 (`AtmosphereBindingsAt11Glsl`, appended by `AtmosphereBindings.AppendLayout`).
  Appending after the atmosphere leaves every existing binding number alone. The fetch reuses `Samp` at binding 6.
  The foliage pipelines share this layout (`ModelRenderer.Foliage.cs`) and the temporal fragment is derived from
  `ModelFrag` (`ShaderSources.Motion.cs`), so both carry it with no extra work.
- **Every backend.** Since 18.0.0 the Metal argument index is authored from the reflected layout in ascending
  `(set, binding)` (`MslBindingOrderGuardTests`), so binding 14 needs no first-sample ordering. Direct3D 11 flattens
  registers in layout order (`D3D11RegisterNumberingTests`, named in `VulkanDescriptorLimitTests`), which adds
  one t register. Vulkan adds one sampled image to the fragment stage. Both shipped-layout sweep tables gain the
  element.
- **Material sets.** Set 0 is built per material, like the shadow atlas binding. The texture handle is stable
  between rebuilds and only its contents change. The first non-empty field allocates the texture and rebuilds every
  live material set through the existing transaction in `ModelRenderer.ShadowLayoutReplacement.cs`, which already
  swaps the cascade and point atlases into the same sets. Until then every set binds the 1x1 zero default.
- **Cascade shadow casters.** The dissolve and cutout depth pipelines gain one set after their existing ones,
  holding the cut texture and a sampler: set 1 for the dissolve pair, set 2 for the cutout pair, whose set 1 is
  the albedo. The opaque depth pipeline does not change.
- **Point shadow casters.** The rigid dissolve pair gains the same set after its existing ones (OC2).
- **Target outline.** The rigid mask pipelines gain set 2 with the cut texture and a sampler (see below). The
  skinned mask pipelines keep their layouts.
- **Silhouette.** The pipeline gains set 1 with the cut texture and a sampler.
- The outline, silhouette and shadow cut sets are one set object per renderer, rebuilt when the texture is
  allocated, never per mesh.

### The discard

One GLSL helper, `InteriorCutGlsl`, shared by every fragment that cuts. Each pass splices it with its own names for
the texture, sampler and the two vec4:

```glsl
bool interiorCut(vec3 p) {                            // p render-relative
    if (InteriorCutWindow.w <= 0.0) return false;
    vec2 c = (p.xz - InteriorCutWindow.xy) * InteriorCutWindow.z;
    if (any(lessThan(c, vec2(0.0))) || any(greaterThanEqual(c, vec2(InteriorCutWindow.w)))) return false;
    uvec4 b = uvec4(round(texelFetch(sampler2D(InteriorCut, Samp), ivec2(c), 0) * 255.0));
    float lo = float(b.r * 256u + b.g), hi = float(b.b * 256u + b.a);
    float q = (p.y - InteriorCutHeights.x) * InteriorCutHeights.y;
    return hi > lo && q >= lo && q < hi;
}
```

- **Colour.** `ModelFrag` adds `if (vSpecParams.w > 0.5 && interiorCut(vWorldPos)) discard;` beside the
  alpha-cutout test, after the three material samples, which is where `ModelFrag` keeps every discard so the
  implicit-LOD derivatives stay defined. The fetch itself has no derivatives. The per-instance gate comes first, so
  an instance that did not opt in never fetches.
- **Temporal.** `ModelMotionFrag` is built from `ModelFrag`, so a cut pixel writes no colour and no motion.
- **Cascade shadow.** The dissolve and cutout depth vertices already compute world position and pass a varying to
  the fragment. They add a `vCut` varying (`vec4(world.xyz, ISpecParams.w)`), and each of the four fragments
  (`ShadowDepthDissolveFrag`, its inverted twin, `ShadowDepthCutoutFrag` and its inverted twin) adds the same gated
  discard before writing depth. Varyings stay affine in world space, so interpolating the position is exact.
- **Point shadow.** `PointShadowRigidDissolveFrag` and its inverted twin, the same way. A lantern inside a building
  therefore throws no shadow from a cut branch onto its floor.
- **Target outline and silhouette.** See the next section.

### Caster routing

`Scene3D.ClassifyCaster` (`Scene3D.ShadowCasters.cs`) sends an opted-in caster that is not dissolving to
`ShadowCastKind.Dissolving` instead of `Opaque`. `ShadowDepthSelection.ForRigidSpan` then picks `Dissolve`, or
`Cutout` for a MASK mesh, exactly as for a fading caster. The dissolve fragment at threshold 0 writes depth
unconditionally ("Gated exactly like ModelFrag: threshold 0 writes depth unconditionally" in
`ShaderSources.Shadow.cs`), so the cut is the only discard it adds. The point shadow pass reads the same kinds
(`PointShadowRenderer`), so it follows.

**Decision: fold the cut into the existing dissolve and cutout casters rather than add cut variants.** The
alternative is four new rigid depth pipelines and two point ones (opaque cut, and cut beside each existing
variant), each needing its own `ShadowDepthVariant` member, shader pair and caster-signature case. Folding costs a
cut tree trunk the move from the depth-only pipeline to the dissolve one, which samples nothing when the gate is
off and discards only cut fragments. The plain `ShadowDepthFrag` path and every non-opted caster are untouched.
The cost of that move is a stated risk with its own measurement, below.

### Combinations

| With | Colour | Shadow |
|---|---|---|
| Alpha cutout (leaf cards) | Both tests run, either discards | Cutout pipeline runs both |
| Rigid dissolve and fade band | Independent discards, the union is removed | Same, in the dissolve pipeline |
| Full to authored-LOD crossfade (`DissolveComplement`) | Both halves are the layer's own parts and both carry the flag | Same |
| Individual to HLOD crossfade | The individual half is cut, the merged half never is | Same |
| Shadow-only draws | Not drawn in colour | Cut like any caster |
| Hidden roofs | Unaffected, roofs never opt in | Still cast (`TileWorldView.Roofs.cs`, issue #974) |

### Shadow caching

The cascade pass skips frames whose casters and cascades are unchanged (`Scene3D.ShadowDirtyState.cs`), and point
lights keep per-slot caster signatures (`Scene3D.PointShadows.cs`). A new field changes what an unchanged caster
writes, so `Scene3D` keeps an interior field version and both dirty checks compare it. A re-centre whose
overlapping cells are unchanged still dirties once, which costs one pass.

### Excluded by construction

- HLOD merged meshes: `DrawMerged` never sets the flag.
- Grass, ground cover and animated foliage: the foliage vertex writes `w = 0` and the CPU path never sets it.
- Skinned meshes: their packers write `w = 0`, and IC5 covers prop layers only.
- Ordinary TileWorld props outside an opt-in layer, roofs included.

## Cut-aware picking, outlines and silhouettes

A cut part is not there for the player, so every path that reacts to an opted-in prop's pixels reads the same
field as the colour pass (OC10). Three were found, and all three reach opted-in props.

### What reaches opted-in props

- **Object picking.** `TileObjectRaycast.Pick` (`KhaozEngine.TileWorld.Render3D/TileObjectRaycast.cs`) tests a ray
  against every object's model box, carried into the object's frame and slab-tested with `RayMath.IntersectObbY`,
  and returns the entry distance. `TileWorldView.PickObjects` (`TileWorldView.Picking.cs`) wraps it for Gameplay
  regions, and it reads every object on the plane whatever its layer, so a tree in an opted-in layer is picked
  like any prop. Grimhollow's woodcutting click reaches trees this way.
- **The camera boom.** `TileWorldCameraProbe` stops a boom at the first object hit from `PickObjects` that the
  consumer's filter accepts, with every box grown by the boom radius. It already skips hidden roofs, "because an
  invisible ceiling must never stop a camera". A cut branch is the same case.
- **Walk surface picking.** `TileWorldView.PickSurface` includes object walk surfaces through
  `TileWalkSurfaces.Raycast` (`KhaozEngine.TileWorld/TileWalkSurfaces.cs`), which reads every archetype carrying
  `WalkSurfaces`. Nothing stops an opted-in archetype from carrying one.
- **The target outline.** `TileWorldView.SetOutlinedObject` draws through `TileWorldPropClusters.DrawOutline`
  (`TileWorldPropClusters.Outline.cs`) for a layer object. It outlines the individual parts with
  `Scene3D.DrawMeshOutlineDissolved` and, past the HLOD distance, a placement mesh built from the merged sources. The
  mask pass is `TargetOutlineMaskVert` with `TargetOutlineFullMaskFrag` and `TargetOutlineVisibleMaskFrag`
  (`ShaderSources.TargetOutline.cs`), and `TargetOutlineRenderer` composites the border from those coverage
  targets.
- **The silhouette.** `TileWorldView.SetSilhouettedObject` draws the object's parts from the view's `_propMeshes`,
  which holds every archetype including the layer ones (`TileWorldView.cs`, where `TileWorldPropClusters` takes
  its full meshes from the same dictionary), as an inverted hull through `Scene3D.DrawMeshSilhouette`
  (`SilhouetteVert`, `SilhouetteFrag`, `SilhouetteRenderer`).

`EditorPicking`, `MapWorldQueries` and the physics raycasts serve MapDoc, the editor and collision, not TileWorld
prop pixels, and are out of this round.

### Rays

The core answers rays exactly, in the same cells and spans the shader reads:

- `InteriorHeightField.FirstUncut(origin, direction, tStart, tEnd, out t)` walks the cells the segment crosses in
  the window (a 2D digital differential analyser over the cell lattice). In each cell the ray's height is linear
  in t, so the part inside that cell's span is one interval, solved exactly. It returns the first t in
  `[tStart, tEnd]` that is outside every span, or false when the whole segment is cut. Points outside the window
  are uncut.
- `RayMath.IntersectObbY` gains an overload with `out float tFar`. The existing slab test already computes the far
  distance and drops it.

`TileObjectRaycast.Pick` gains an optional `TileObjectCut` argument, the applied field and the set of opted-in
archetype ids. For an object whose archetype is in the set, the box segment `[tNear, min(tFar, maxDistance))` goes
through `FirstUncut`. A segment that is wholly cut drops the hit. Otherwise the hit's distance becomes the first
uncut t, so nearest-first ordering stays honest: a ray through a doorway that crosses cut branches and then meets
the floor reports the floor first, and a ray that continues through the back wall to the crown outside reports the
wall first. Picking stays box-based, exactly as coarse as before outside the cut.

`TileWorldView.PickObjects` passes the view's applied field and the union of the archetype ids of its layers that
set `CutInsideInteriors`. The camera boom inherits it through `PickObjects`, so a cut branch inside a building
never shortens a boom.

`TileWalkSurfaces.Raycast` gains an optional point filter, consulted before a candidate replaces the best hit.
`PickSurface` passes one that rejects a point `InteriorHeightField.Contains` reports inside, for an opted-in
archetype only, so the next candidate wins.

### Target outline

- The rigid mask fragments gain the gated cut. The skinned mask pipelines bind the bone palette at set 2 and share
  today's fragments, so the rigid pair becomes two new fragment constants, the existing ones plus the cut, and the
  skinned pair keeps the existing ones unchanged.
- `Params.w` in the draw slot is the flag. The skinned writer already puts 0 there
  (`TargetOutlineRenderer.Skinned.cs`, `WriteDrawPayload`), and the rigid writer does too until a cut part asks.
- `vWorldPos` in the mask vertex is absolute (`world.xyz + RenderOrigin.xyz`), so the fragment calls
  `interiorCut(vWorldPos - RenderOrigin.xyz)`. That subtraction rounds at the scale of the absolute coordinate,
  about a millimetre at 10 km, which is far inside one cell.
- Both coverage targets cut. A cut part is absent, so neither the through-geometry border nor the visible border
  traces it.
- `Scene3D.DrawMeshOutlineDissolved` gains an overload with `cutInsideInteriors`, carried on the queued part.
  `TileWorldPropClusters.DrawIndividualOutline` passes the layer's `CutsInsideInteriors`. `DrawMergedOutline`
  passes false, because the merged mesh is never cut in colour and the outline follows the drawn picture.

### Silhouette

- `SilhouetteVert` hands the fragment the extruded render-relative position, and `SilhouetteFrag` adds the gated
  cut. `SilhouetteMotionFrag` is built from `SilhouetteFrag` (`ShaderSources.MotionPreserve.cs`), so the temporal
  variant follows.
- `Params.y` in the draw payload is the flag. `Params.x` is the width today.
- `Scene3D.DrawMeshSilhouette` gains an overload with `cutInsideInteriors`. `TileWorldView.DrawSilhouettedObject`
  passes true when the object's archetype is in an opted-in layer.
- The view's own comment explains why this matters: "a hull whose model is not drawn has nothing to eat its middle
  and reads as a solid blob". The hull is cut by the same field at its own extruded position, so it disappears with
  the model, offset at most by the hull width (5 cm by default) at the interior's boundary.

## The MapDoc adapter contract for R8

R8 implements `IInteriorSource` over the effective MapDoc scope. Nothing below is built in this round.

- **Rooms.** Each `MapPrismDomain` (`KhaozEngine.MapDoc/Spaces/MapPrismDomain.cs`) becomes one volume per piece of
  `MapPrismGeometry.Compile(view, prism)`. Compile applies the instance transform, free yaw included, and
  normalizes world winding, so each `MapWaterPiece.Ring` is the outline as is.
- **Floor and ceiling.** `MapPrismGeometry.Bounds(view, prism)` gives the world `LowerY` and `UpperY` after the
  transform. They become FloorY and CeilingY.
- **Roofs.** Each `MapRoofLinkDoc` (`KhaozEngine.MapDoc/Prefabs/MapPrefabRecords.cs`) names a `Roof` prefab and the
  `Rooms` and `Groups` it covers, with groups expanding through `MapRoofGroupDoc.Rooms`. R8 resolves the roof's mesh
  and world transform and adds one `InteriorRoof` to each covered room's volume. Whether the link's `AboveY` bears
  on the cut is for R8 to settle from its definition.
- **Storeys.** One prism per storey. Stacked prisms in one column merge to one span in the field, as in OC4.
- **Mesh reading.** R8 supplies `ClearanceSamples` for prefab meshes. The MapDoc clearance adapter
  ([#1433](https://github.com/APKiwiOrg/KhaozEngine/issues/1433)) needs the same reader, and the two should share it.
- **Edges off the lattice.** A rotated room's walls do not fall on cell edges, so the inward rule leaves up to one
  cell diagonal uncut inside the outline. R8 may want a finer cell for MapDoc worlds and can choose one per window.
- **Picking.** R8's prop picks take the same `FirstUncut` and `Contains` queries.
- **Issues.** R8 reports its own codes through `InteriorSourceResult`.

## Public API sketch

```csharp
// KhaozEngine.Primitives, folder InteriorCut
public sealed record InteriorVolume { /* Outline, FloorY, CeilingY, Roofs, as above */ }
public readonly record struct InteriorRoof(ClearanceSamples Samples, Matrix4x4 LocalToWorld);
public readonly record struct InteriorWindow(Vector2 Min, float CellSize, int Cells)
{
    public static InteriorWindow Centred(Vector2 focus, float cellSize, int cells); // snapped to the cell lattice
    public bool NeedsRecentre(Vector2 focus, float distance);
}
public interface IInteriorSource { InteriorSourceResult Collect(InteriorWindow window); }
public sealed record InteriorSourceResult(IReadOnlyList<InteriorVolume> Volumes, IReadOnlyList<string> Issues);
public sealed class InteriorHeightField
{
    public InteriorWindow Window { get; }
    public float BaseY { get; }
    public float StepY { get; }
    public int MergedCells { get; }                              // OC4
    public ReadOnlyMemory<byte> Texels { get; }                 // RGBA8, rows along +z
    public long Version { get; }
    public bool TryGetSpan(int cellX, int cellZ, out float floorY, out float topY);
    public bool Contains(Vector3 world);                         // the shader's test, on the CPU
    public bool MayIntersect(Vector3 worldMin, Vector3 worldMax);
    public bool MayIntersect(Vector3 worldMin, Vector3 worldMax, InteriorCutFrame frame);
    public bool FirstUncut(Vector3 origin, Vector3 direction, float tStart, float tEnd, out float t);
}
public static class InteriorHeightFieldBuilder
{
    public static InteriorHeightField Build(IReadOnlyList<InteriorVolume> volumes, InteriorWindow window);
}
public static class RayMath   // existing
{
    public static bool IntersectObbY(Vector3 origin, Vector3 direction, Vector3 center, float yaw,
        Vector3 min, Vector3 max, out float tNear, out float tFar);   // new overload
}

// KhaozEngine.Render3D
public readonly struct RigidInstanceDraw { public bool CutInsideInteriors { get; init; } /* default false */ }
public sealed partial class Scene3D
{
    public InteriorHeightField? InteriorCut { get; set; }
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world,
        float dissolve, bool dissolveComplement, bool cutInsideInteriors);
    public void DrawMeshSilhouette(MeshHandle mesh, Matrix4x4 world, Color color, float widthMetres,
        bool cutInsideInteriors);
}

// KhaozEngine.Terrain.Render3D
public sealed class PropLayer { public bool CutsInsideInteriors { get; } }  // plus the factory parameter
// Scene3D.DrawProps overloads gain an optional cutInsideInteriors parameter.

// KhaozEngine.TileWorld
public sealed class TileWorldInteriors : IInteriorSource
{
    public TileWorldInteriors(TileWorldDocument detached, TileWorldCatalogs catalogs, ITileMeshGeometry meshes);
    public IReadOnlyList<TileWorldIssue> LastIssues { get; }
}
public static class TileWalkSurfaces   // existing
{
    public static TileHit? Raycast(TileWorldDocument doc, TileWorldCatalogs catalogs, int plane, Vector3 origin,
        Vector3 direction, float maxDistance, Func<TileObject, bool>? include = null,
        Func<TileObject, Vector3, bool>? acceptPoint = null);    // new optional filter
}

// KhaozEngine.TileWorld.Render3D
public sealed record TilePropLayerDefinition { public bool CutInsideInteriors { get; init; } }
public readonly record struct TileObjectCut(InteriorHeightField? Field, IReadOnlySet<string> ArchetypeIds);
public static class TileObjectRaycast  // existing
{
    public static int Pick(TileWorldDocument document, TileWorldCatalogs catalogs, int plane, Vector3 origin,
        Vector3 direction, float maxDistance, BoundsSource bounds, List<TileObjectHit> hits, TileObjectCut cut);
}
public interface ITileWorldScene
{
    void SetInteriorCut(InteriorHeightField? field) { }                                    // default no-op
    void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world,
        float dissolve, bool complement, bool cutInsideInteriors) { /* forwards */ }
    void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, Color color, float widthMetres,
        bool cutInsideInteriors) { /* forwards */ }
}
public sealed partial class TileWorldView
{
    public ITileMeshGeometry? InteriorMeshes { get; set; }
    public InteriorHeightField? AppliedInteriorCut { get; }   // the field the picture and the picks share
}
```

`Scene3D.InteriorCut = null` turns the cut off, binds the default and sets `Cells` to 0. Assigning a field
uploads it at the next frame. `TileWorldView.PickObjects`, `PickSurface`, the outline and the silhouette apply the
cut themselves, so a consumer that opts a layer in needs no other call. The new overloads default to no cut where
an existing overload forwards to them.

## Tests

### CPU, headless

`KhaozEngine.Primitives.Tests` or the matching area project:

- An axis-aligned rect fills exactly its cells, and a cell straddling the edge stays empty.
- A polygon rotated 30 degrees fills only cells wholly inside it. `Contains` agrees with a point-in-polygon oracle
  at cell centres, and no cell outside the polygon is filled.
- A gable roof read back: a two-slope mesh over a rect gives per-cell tops at or below the analytic roof height,
  within one cell's slope drop of it, and never above it. The gable-end row stops at eave height.
- Stacked storeys: two volumes in one column merge to one span from the lower floor to the higher top, and
  `MergedCells` counts the cell.
- A roofless volume tops out at `CeilingY`.
- Re-centring: a field built at one window and one built after a 64 m shift agree texel for texel over their
  overlap, and the window minimum snaps to the lattice.
- Quantization: floor and top round down, a 655.35 m range keeps 1 cm steps, a larger one grows the step, and the
  RGBA8 pack decodes bit for bit.
- An empty world, and a window with no volumes, produce an all-zero field that cuts nothing.
- `FirstUncut` agrees with `Contains` sampled densely along random rays, returns false for a segment wholly inside
  a span, and treats the window's outside as uncut.
- `RayMath.IntersectObbY` with `tFar` agrees with the existing overload's `tNear` and with an exit point on the box.

`KhaozEngine.TileWorld.Tests`:

- One-plane building: volumes, FloorY, CeilingY and roof assignment from a small fixture.
- A two-storey building under one roof is one span, and a roof on an unrelated plane is not assigned.
- Eave samples outside the indoor rect never reach the field.
- The cell size divides the tile size: 25 cm at 1 m, 24 cm at 1.2 m.
- **Wall-edge check.** A boundary wall on the inner edge of its tile reports `interior.wall-inner-edge` and its
  tile leaves the volumes. The same building with the wall on the outer edge reports nothing. The test asserts the
  issue, so a regression fails loudly instead of cutting outside.
- A diagonal boundary wall reports `interior.diagonal-wall`.
- A roof with an unreadable mesh reports `interior.roof-mesh` once and keeps its `CeilingY` top.
- Change detection: an Indoors flag edit queues a rebuild and a tree move does not.
- A walk surface of an opted-in archetype whose hit point lies in the field is skipped, and a lower candidate wins.

`KhaozEngine.Render.Tests`, GPU-free:

- `InstanceGrouping` writes `SpecParams.w = 1` for an opted-in draw and 0 otherwise.
- `ClassifyCaster` sends an opted-in solid caster to `Dissolving`, and `ForRigidSpan` keeps a MASK mesh on
  `Cutout`.
- `UboLayoutTests` pins the new tail offsets and the 1376-byte size.
- `VulkanDescriptorLimitTests` and `D3D11RegisterNumberingTests` carry the new elements, and the heaviest pipeline
  still spends six dynamic uniform descriptors.
- Shader contract tests assert the gated discard line in `ModelFrag`, the six shadow fragments, the two rigid
  outline mask fragments and `SilhouetteFrag`, as `ShadowShaderContractTests` does for the cutout, and that the
  skinned outline mask fragments are unchanged. `ShaderValidation.ValidatePair` compiles every changed pair.
- A field version change dirties the cascade pass and the point caster signatures.
- The outline and silhouette payload writers put the flag and the two vec4 at their offsets.

`KhaozEngine.TileWorld.Render3D` tests, through the recording scene:

- **A ray through a doorway at a cut branch.** A building with an open doorway, an opted-in tree box reaching
  inside, and a field applied to the view. A ray through the doorway that meets the tree's box only inside the
  interior and then the floor: `PickObjects` drops the tree, and `PickSurface` returns the floor. A ray that
  crosses the cut branches and continues to the back wall reports the wall before the tree. The same rays with the
  layer not opted in still name the tree.
- The camera boom inside the building is not shortened by the cut branch.
- `DrawIndividualOutline` and `DrawSilhouettedObject` pass the flag for an opted-in layer, and the merged outline
  passes false.

### GPU, on Metal, Direct3D 11 and Vulkan

Pixel-presence `GpuFact`s, not goldens, run on the three legs of the cross-platform GPU workflow:

- **Colour.** An orthographic side view of a tall box prop that opts in, crossing a rect interior set directly
  through `Scene3D.InteriorCut` with no building drawn. The pixels whose world position `Contains` reports inside
  show the background, every other box pixel shows the box, within one pixel at the boundary.
- **Shadow.** The same box under a sun straight above, half of it inside a rect interior that reaches past its
  top. The ground shadow covers exactly the outside half, within the PCF kernel at the edge. A point light under
  the same interior shows the same split, in the shape `AlphaCutoutShadowGpuTests` uses for the cutout.
- **Outline.** The same box outlined in both occlusion modes. The full and visible coverage targets hold no pixel
  inside the interior's projection, and the composited border runs along the cut boundary rather than around the
  whole box.
- **Silhouette.** The same box silhouetted. No hull pixel lies inside the interior's projection.
- **No opt-in.** The same scenes with the flag off are pixel-identical to the scenes with no field set, and the
  full golden suite runs with zero drift.

### GPU timing

One capture, `InteriorCutCostPerfGpuTests` with `RequiresRealGpu`, in the shape of
`TemporalResolveCostPerfGpuTests` and `ShadowCascadeCullPerfGpuTests`: a tree-heavy village view of cutout trees
around box buildings, the sun advancing every frame so the cascade pass is dirty and the dirty-skip cannot hide it,
and four modes in rotating order:

1. Shadows off, the baseline that isolates the shadow pass, as `ShadowCascadeCullPerfGpuTests` does.
2. Shadows on, no layer opted in.
3. Shadows on, the layer opted in with an empty field. Relevance rejects every instance, so casters retain
   their original pipelines and nothing is cut. Mode 3 minus mode 2 records the empty-field gate overhead.
4. Shadows on, opted in, with the village's interiors.

Both DenseVillage (the original dense scatter) and TypicalVillage (72 of 3600 trees reach the 49 interiors)
use this rotating four-mode order. Mode 4 minus mode 2 is the feature's cost, colour plus shadow, printed
against the 0.1 ms budget only for TypicalVillage and never asserted. DenseVillage is recorded without a budget.
The engine's GPU seam has no timestamp queries (`KhaozEngine.Diagnostics/PassTimings.cs`), so frame blocks are the
measure, as in the temporal record. It runs through `gpu-hosted-bench` on the hosted T4 (Direct3D 11 and Vulkan),
once, as OC8 approves.

**As recorded.** The owner approved each later run. TypicalVillage mode 4 minus mode 2 measured 1.9 to 2.4 ms
before the relevance counters were fixed, 0.56 to 0.63 ms after them, 0.23 to 0.31 ms once the per-draw boxing
and copies were removed, and 0.079 to 0.203 ms in the final run 39196852937. In every later run the GPU part,
read from the submit-and-drain delta, stayed near zero, and EmptyField cost the same as VillageInteriors, so the
remainder is the CPU cost of opting in. IC8 accepts it.

## Docs and release notes

At build time, in the same change:

- `CHANGELOG.md`: one minor entry describing the cut, the opt-in, the shadow behaviour, cut-aware picking, outline
  and silhouette, and the exclusions.
- `docs/USING-KHAOZENGINE.md`: the Tile world section (opt-in, `InteriorMeshes`, the issues, the picks honouring
  the cut), the `Scene3D.DrawProps` section (the flag), and wherever target outlines, silhouettes and the camera
  boom probe are described.
- Package READMEs: `KhaozEngine.Primitives` (the field and its ray queries, `RayMath`'s new overload),
  `KhaozEngine.Render3D`, `KhaozEngine.Terrain.Render3D`, `KhaozEngine.TileWorld` (`TileWalkSurfaces`'s filter)
  and `KhaozEngine.TileWorld.Render3D` (`TileObjectRaycast`, `PickObjects`, `PickSurface`, outlines, silhouettes).
- `docs/INDEX.md`: this row's status.
- A full Markdown sweep for `SpecParams`, the frame UBO size, the Model layout's binding list, and the outline and
  silhouette payload layouts, which several design records quote.

## Risks

- **Early depth on reaching casters.** Discard-capable pipelines can lose early depth, including for solid
  trunks. Hosted run 39160690665 measured this routing cost at about seven times the budget when every tree
  opted in. IC7 keeps missed instances on their original path, and the final record puts the GPU part near
  zero. The CPU cost of opting in remains and is tracked in [#1502](https://github.com/APKiwiOrg/KhaozEngine/issues/1502).
- **Upload hitch.** A re-centre or a rebuild uploads 16 MiB in one frame. If it shows, the lattice snap already
  allows a toroidal window that uploads only the strip the re-centre exposes.
- **Courtyards.** An Indoors-flagged courtyard with a tree in it is cut up to the top of its storey, trunk
  included, as approved. Content that wants a courtyard tree leaves those tiles outdoors, which also keeps the
  roof rule honest.
- **The leaf sliver and the gable row.** Leaves under the roof surface stay visible with roofs hidden, worst at the
  gable ends. If the owner look (IC6) finds it noticeable, the fix is a finer cell or reading the top surface
  rather than the lowest sample, which risks cutting above the roof.
- **Animated foliage.** A tree moved into the animated foliage path is not cut, because that path writes `w = 0`.
- **Disjoint storeys.** Two interiors stacked in one column with outdoor space between them merge into one span
  and cut the space between (OC4). `MergedCells` makes it visible.
- **Hull at an open cut.** Looking along a cut branch into its open end from inside, the silhouette hull's own
  back faces just outside the interior can show through the opening, a rim no wider than the hull width.

## Out of scope

- Authoring-time prevention. `TileWorldClearance` is unchanged and optional.
- The MapDoc adapter, beyond its contract.
- Cutting HLOD merged meshes, grass, foliage, skinned meshes or ordinary props.
- MapDoc, editor and physics ray queries (`EditorPicking`, `MapWorldQueries`, `BepuPhysicsWorld.Raycast`), which do
  not serve TileWorld prop pixels.
- Dissolving the cut edge, or any soft transition at the wall.
- Interiors the observer is in deciding anything. The cut is static.

## Alternatives

Owner scores from the IC3 ruling.

- **Interior height field (40, chosen).** CPU-built, one texture, testable headless through a CPU mirror, the
  same shape for TileWorld and MapDoc, and the same field answers picking exactly.
- **Top-down GPU roof depth map (35).** Render roofs from above each time interiors change and cut below that
  depth inside the outline. It needs a new pass on three backends, its result is only observable on a GPU, and
  the outline still has to come from the CPU.
- **A box per building (33).** One oriented box per building, sent as a uniform array. A box cannot follow a
  pitched roof: set to the ridge it cuts the crown above a sloped roof, set to the eaves it leaves branches under
  the slope.

## Owner decision on OC8 (2026-10-18)

- **OC8 (a) approved.** `gpu-hosted-bench` runs on dispatch on larger GPU runners billed per minute. Options:
  (a) one dispatch of the `InteriorCutCostPerfGpuTests` filter on the T4 legs once the build is approved, (b) also
  a native Metal run on the dev Mac. **Recommendation: (a)** only. (b) adds GPU load on the shared Mac, and Metal
  correctness is already proven by the cross-platform GPU workflow.
