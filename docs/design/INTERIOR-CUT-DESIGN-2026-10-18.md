# Interior cut for props inside buildings

Status: design for [#1470](https://github.com/APKiwiOrg/KhaozEngine/issues/1470), written 2026-10-18 under owner
rulings IC1 to IC6. Not built. It ships as one engine minor after 21.4.0, and this document picks no version
number. Items marked **Open for owner or orchestrator** are decisions this design had to make beyond its brief.
Each carries options and a recommendation, and the build waits on them.

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

## Orchestrator calls approved with the design

These are recorded as approved. Two are questioned below with evidence, and neither is changed without a ruling.

- **Cells of 25 cm.** See Open item OC3 for snapping the cell to the tile lattice.
- **A window of about 384 m square centred on the observer**, covering the full-detail tree range (Grimhollow
  trees go HLOD at 192 m). Re-centre when the observer moves about 64 m from the centre. Rebuild when the world's
  interiors change. Build on the CPU off the render thread and upload as one texture. See Open item OC1, which
  shows the drift leaves only 128 m of cover on the far side.
- **One span per cell**, from the lowest indoor floor up to the roof's top surface. The roof surface is the
  LOWEST roof sample in the cell, so the cut never rises above the roof. A sliver of leaves just under the roof
  may remain, visible only with the roof hidden. Stacked storeys under one roof are one span. An indoor area with
  no roof above it (a courtyard) is cut up to the top of its top storey.
- **Reuse the 21.2.0 mesh reading and surface sampling** (`ClearanceMesh`, the sampling behind `ClearanceSolid`,
  `ITileMeshGeometry`, `GltfTileMeshGeometry`) for the roof surface, with no second mesh path. The choice between
  a lowest-sample `ClearanceSolid` and a separate rasterizer is made in [Roof surface sampling](#roof-surface-sampling).
- **A scene in which no layer opts in renders byte-identical**, so every existing GPU golden holds. The precedent
  is the instanced dissolve's appended field and gated branch ([design](INSTANCED-DISSOLVE-DESIGN-2026-07-20.md)).
- **A GPU cost budget of 0.1 ms** on a tree-heavy village view.
- **One engine minor after 21.4.0.** No version is chosen here.

## Shape of the solution

Three layers, each in the package that already owns its concern:

1. **Core, `KhaozEngine.Primitives`, namespace `KhaozEngine.Primitives`, folder `InteriorCut`.** Interior
   volumes in, a packed height field out. It knows polygons, heights and roof samples, never a document type. It
   sits beside the `Clearance` core for the same reason that core sits there: `KhaozEngine.TileWorld` and a later
   MapDoc adapter both reach it without a reference cycle, and it reuses `ClearanceMesh` and `ClearanceSamples`
   directly.
2. **Adapters.** `TileWorldInteriors` in `KhaozEngine.TileWorld` turns a TileWorld document into volumes. A MapDoc
   adapter does the same in R8.
3. **Render, `KhaozEngine.Render3D`.** `Scene3D.InteriorCut` takes a built field, uploads it and binds it. A
   per-instance flag opts a draw in. The colour, temporal, cascade shadow and point shadow passes discard cut
   fragments.

`KhaozEngine.TileWorld.Render3D` drives it: it owns the window, re-centring, change detection and the background
build, and it carries the layer opt-in down to the draws.

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
  failed to load and the cells a sampled roof misses at its edges. It is never above the roof, because a roof
  stands on the plane whose floor is CeilingY.
- **Roofs** carry `ClearanceSamples`, the public 21.2.0 sample set, so the core never loads a mesh and each format
  brings its own mesh reader.

## The height field

### Layout and texel format

One 2D texture, one texel per cell, rows along +world Z and columns along +world X from the window's minimum
corner. Each texel holds one span as two 16-bit quantized heights, floor and top, measured in steps above the
field's base height.

The texel format is `R8G8B8A8UNorm` with the floor's high and low bytes in R and G and the top's in B and A.
`GpuPixelFormat` (`KhaozEngine.Gpu/GpuEnums.cs`) offers `R8G8B8A8UNorm`, `R32Float`, `R8UNorm`,
`R16G16B16A16Float` and `R16G16Float`, and nothing with two 16-bit integer or normalized channels.

- `R16G16Float` keeps 11 bits of mantissa, so a span 100 m above the base resolves only to 6 cm and gets worse
  with height. A cut boundary that moves 6 cm with altitude is visible on a roof line.
- Two `R32Float` textures, or one `R32Float` with interleaved rows, double the memory and add a binding or an
  address calculation for no gain over 16 bits.
- Adding `R16G16UNorm` to the GPU seam is engine work on three backends for a format only this feature uses.
- Packed `R8G8B8A8UNorm` is exact: each byte decodes as `round(v * 255)`, so the shader rebuilds both integers
  bit for bit on every backend. That is Open item OC7.

The step is 1 cm, giving 655.35 m of vertical range inside one window, which covers any plausible set of
interiors within 384 m. When a window's spans need more, the step grows to `range / 65534` and the build reports
it. Heights are quantized conservatively: the floor rounds down (the cut reaches at most one step below a floor,
where nothing is visible) and the top rounds down (the cut never rises above the roof).

A texel whose top is not above its floor is empty. A zero texel is empty, so a cleared texture and the 1x1 zero
default both cut nothing.

The field is read with `texelFetch`. Filtering would blend a span with an empty neighbour into a height that
belongs to neither, so the read is unfiltered by construction. The engine's shaders write a fetch as
`texelFetch(sampler2D(Tex, Samp), coord, 0)` (`ShaderSources.Decal.cs`), and the colour pass reuses its shared
sampler for that.

### Window and re-centring

- The window is square, `Cells` cells per side, and its minimum corner snaps to the cell lattice anchored at
  world origin. Re-centring therefore never moves a cell boundary in the world. Where the old and new windows
  overlap the new field is identical, so a re-centre never pops the cut.
- With the approved figures the window is 384 m, 1536 cells per side.
- The centre is the focus the prop draw radius uses (`ITileWorldScene.DrawProps` takes `focus`), so the cut and
  the full-detail draw share one centre. That is Open item OC9.
- Re-centre when the focus is 64 m or more from the centre along either axis.

### Threading

- The view thread captures what the adapter reads into a detached copy, following
  `TileWorldBuildSnapshot.CaptureGround` (`KhaozEngine.TileWorld.Render3D/TileWorldBuildSnapshot.cs`), because
  the document is mutable under an editor.
- A new `TileWorldBuildKind.InteriorCut` on the existing `TileWorldBuildQueue` runs collection and rasterization
  off the render thread. At most one interior build is in flight, and a newer request supersedes a queued one.
- The finished field is applied on the view thread through `ITileWorldScene`, and `Scene3D` uploads it once at
  the next frame with one `UpdateTexture` of the whole texture.
- Until the first build lands, nothing is cut. A late field leaves branches visible for a few frames, never cuts
  the wrong thing.

### Memory

- GPU: `Cells * Cells * 4` bytes, 9 MiB at 1536 cells (16 MiB at 2048, see OC1).
- CPU: the applied field stays resident for upload and for `InteriorHeightField.Contains`, plus one buffer under
  construction, so twice the GPU figure at peak.
- A scene with no field allocates only the 1x1 default.

### Rasterization

For each volume, the builder fills every cell that lies wholly inside the outline. A cell straddling an edge is
left uncut. Cutting it would remove branch pixels outside the wall, which IC2 forbids, so the error always falls
inside. With an outline on the cell lattice (the TileWorld case under OC3) there is no error at all. A free-yaw
MapDoc edge leaves at most one cell diagonal, 35 cm at 25 cm cells, inside the outline uncut.

Each filled cell takes `[FloorY, top]`, where top is the lowest roof sample in that cell or `CeilingY` when no
sample landed. Where two volumes fill one cell, the spans merge to the lowest floor and the highest top (Open
item OC4).

## Roof surface sampling

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
  (`mesh.ForEachSample(cellSize / 2f, solid.Mark)`), and the cut samples roofs at half the field's cell, 12.5 cm.
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
  per archetype. Its volumes keep their `CeilingY` top (Open item OC5).
- `interior.step`: a window's spans exceed 655.35 m and the step grew.

The view logs each new issue once. Surfacing them in ke-tileedit is Open item OC6.

### Change detection

A rebuild is due when the window re-centres or when any region-plane in the window changes what the adapter reads:
an Indoors flag, a wall or roof object, or a corner height on an indoor or roof plane. The driver keeps a digest of
those inputs per region-plane, compared on the view's existing region rebuild path, so an unrelated edit (a tree
moved, a ground material) queues nothing.

## Render integration

### The opt-in route

`TilePropLayerDefinition.CutInsideInteriors` flows to `PropLayer.CutsInsideInteriors` in
`KhaozEngine.Terrain.Render3D`, which `PropClusterRenderer.SceneBackend.DrawProps` passes into `Scene3D.DrawProps`,
which sets `RigidInstanceDraw.CutInsideInteriors` on every queued instance, the authored LOD parts included. The
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
| Window minimum (x, z), render-relative | Frame UBO tail, `InteriorCutWindow.xy` | Computed on the CPU in double against `RenderOrigin` |
| 1 / cell size | `InteriorCutWindow.z` | |
| Cells per side, 0 when no field | `InteriorCutWindow.w` | 0 turns the test off in every pass |
| Base height, render-relative | `InteriorCutHeights.x` | |
| 1 / step | `InteriorCutHeights.y` | |

- The colour and temporal passes read the frame block `U`. The two vec4 append after the cluster tail, which is
  how every tail since the shadow tail was added: `UboBytes` goes from 1344 to 1376 and every existing offset is
  unchanged (`ModelRenderer.cs`, `ClusterTailOffset`). This spends no new uniform buffer, and the heaviest shipped
  pipeline stays at six of the eight dynamic uniform descriptors Vulkan guarantees
  (`VulkanDescriptorLimitTests.TheHeaviestShippedPipeline_SpendsSixDynamicUniformDescriptors`).
- The cascade shadow pass reads its own 256-byte slot per cascade: the matrix at 0, `RenderOrigin` at 64 and
  `DissolveParams` at 80 (`ShadowMapRenderer.cs`, `CascadeSlotBytes`). The two vec4 go at 96 and 112.
- The point shadow pass reads a 256-byte slot per light face with a 144-byte payload (`PointShadowRenderer.cs`,
  `FaceSlotBytes`). The two vec4 go at 144 and 160.

All positions are render-relative, like every GPU-bound position, so the test runs on `vWorldPos` directly and
precision follows camera-relative rendering.

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
  between rebuilds and only its contents change. The first non-empty field, and any later change of `Cells`,
  allocates the texture and rebuilds every live material set through the existing transaction in
  `ModelRenderer.ShadowLayoutReplacement.cs`, which already swaps the cascade and point atlases into the same
  sets. Until then every set binds the 1x1 zero default.
- **Cascade shadow casters.** The dissolve and cutout depth pipelines gain one set after their existing ones,
  holding the cut texture and a sampler: set 1 for the dissolve pair, set 2 for the cutout pair, whose set 1 is
  the albedo. The opaque depth pipeline does not change.
- **Point shadow casters.** The rigid dissolve pair gains the same set, under Open item OC2.

### The discard

One GLSL helper, `InteriorCutGlsl`, shared by every fragment that cuts:

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
- **Point shadow.** `PointShadowRigidDissolveFrag` and its inverted twin, the same way.

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
- **Storeys.** One prism per storey. Stacked prisms in one column merge to one span in the field (OC4).
- **Mesh reading.** R8 supplies `ClearanceSamples` for prefab meshes. The MapDoc clearance adapter
  ([#1433](https://github.com/APKiwiOrg/KhaozEngine/issues/1433)) needs the same reader, and the two should share it.
- **Edges off the lattice.** A rotated room's walls do not fall on cell edges, so the inward rule leaves up to one
  cell diagonal uncut inside the outline. R8 may want a finer cell for MapDoc worlds and can choose one per window.
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
    public ReadOnlyMemory<byte> Texels { get; }                 // RGBA8, rows along +z
    public long Version { get; }
    public bool TryGetSpan(int cellX, int cellZ, out float floorY, out float topY);
    public bool Contains(Vector3 world);                         // the shader's test, on the CPU
}
public static class InteriorHeightFieldBuilder
{
    public static InteriorHeightField Build(IReadOnlyList<InteriorVolume> volumes, InteriorWindow window);
}

// KhaozEngine.Render3D
public readonly struct RigidInstanceDraw { public bool CutInsideInteriors { get; init; } /* default false */ }
public sealed partial class Scene3D { public InteriorHeightField? InteriorCut { get; set; } }

// KhaozEngine.Terrain.Render3D
public sealed class PropLayer { public bool CutsInsideInteriors { get; } }  // plus the factory parameter
// Scene3D.DrawProps overloads gain an optional cutInsideInteriors parameter.

// KhaozEngine.TileWorld
public sealed class TileWorldInteriors : IInteriorSource
{
    public TileWorldInteriors(TileWorldDocument detached, TileWorldCatalogs catalogs, ITileMeshGeometry meshes);
    public IReadOnlyList<TileWorldIssue> LastIssues { get; }
}

// KhaozEngine.TileWorld.Render3D
public sealed record TilePropLayerDefinition { public bool CutInsideInteriors { get; init; } }
public interface ITileWorldScene { void SetInteriorCut(InteriorHeightField? field) { } }  // default no-op
public sealed partial class TileWorldView { public ITileMeshGeometry? InteriorMeshes { get; set; } }
```

`Scene3D.InteriorCut = null` turns the cut off, binds the default and sets `Cells` to 0. Assigning a field
uploads it at the next frame. `InteriorHeightField.Contains` is the shader's test on the CPU. Tests use it as the
oracle, and a later picking change can use it to stop clicks landing on cut branches.

## Tests

### CPU, headless

`KhaozEngine.Primitives.Tests` or the matching area project:

- An axis-aligned rect fills exactly its cells, and a cell straddling the edge stays empty.
- A polygon rotated 30 degrees fills only cells wholly inside it. `Contains` agrees with a point-in-polygon oracle
  at cell centres, and no cell outside the polygon is filled.
- A gable roof read back: a two-slope mesh over a rect gives per-cell tops at or below the analytic roof height,
  within one cell's slope drop of it, and never above it. The gable-end row stops at eave height.
- Stacked storeys: two volumes in one column merge to one span from the lower floor to the higher top.
- A roofless volume tops out at `CeilingY`.
- Re-centring: a field built at one window and one built after a 64 m shift agree texel for texel over their
  overlap, and the window minimum snaps to the lattice.
- Quantization: floor and top round down, a 655.35 m range keeps 1 cm steps, a larger one grows the step, and the
  RGBA8 pack decodes bit for bit.
- An empty world, and a window with no volumes, produce an all-zero field that cuts nothing.

`KhaozEngine.TileWorld.Tests`:

- One-plane building: volumes, FloorY, CeilingY and roof assignment from a small fixture.
- A two-storey building under one roof is one span, and a roof on an unrelated plane is not assigned.
- Eave samples outside the indoor rect never reach the field.
- **Wall-edge check.** A boundary wall on the inner edge of its tile reports `interior.wall-inner-edge` and its
  tile leaves the volumes. The same building with the wall on the outer edge reports nothing. The test asserts the
  issue, so a regression fails loudly instead of cutting outside.
- A diagonal boundary wall reports `interior.diagonal-wall`.
- A roof with an unreadable mesh reports `interior.roof-mesh` once and keeps its `CeilingY` top.
- Change detection: an Indoors flag edit queues a rebuild and a tree move does not.

`KhaozEngine.Render.Tests`, GPU-free:

- `InstanceGrouping` writes `SpecParams.w = 1` for an opted-in draw and 0 otherwise.
- `ClassifyCaster` sends an opted-in solid caster to `Dissolving`, and `ForRigidSpan` keeps a MASK mesh on
  `Cutout`.
- `UboLayoutTests` pins the new tail offsets and the 1376-byte size.
- `VulkanDescriptorLimitTests` and `D3D11RegisterNumberingTests` carry the new elements, and the heaviest pipeline
  still spends six dynamic uniform descriptors.
- Shader contract tests assert the gated discard line in `ModelFrag` and the six shadow fragments, as
  `ShadowShaderContractTests` does for the cutout. `ShaderValidation.ValidatePair` compiles every changed pair.
- A field version change dirties the cascade pass and the point caster signatures.

### GPU, on Metal, Direct3D 11 and Vulkan

Pixel-presence `GpuFact`s, not goldens, run on the three legs of the cross-platform GPU workflow:

- **Colour.** An orthographic side view of a tall box prop that opts in, crossing a rect interior set directly
  through `Scene3D.InteriorCut` with no building drawn. The pixels whose world position `Contains` reports inside
  show the background, every other box pixel shows the box, within one pixel at the boundary.
- **Shadow.** The same box under a sun straight above, half of it inside a rect interior that reaches past its
  top. The ground shadow covers exactly the outside half, within the PCF kernel at the edge.
- **No opt-in.** The same scene with the flag off is pixel-identical to the scene with no field set, and the full
  golden suite runs with zero drift.

### GPU timing

One capture, `InteriorCutCostPerfGpuTests` with `RequiresRealGpu`, in the shape of
`TemporalResolveCostPerfGpuTests`: a tree-heavy village view (cutout trees around box buildings), blocks with the
cut off and on in rotating order, the median block difference printed against the 0.1 ms budget and not asserted.
The engine's GPU seam has no timestamp queries (`KhaozEngine.Diagnostics/PassTimings.cs`), so frame blocks are the
measure, as in the temporal record. It runs once through `gpu-hosted-bench` on the hosted T4 (Direct3D 11 and
Vulkan). That workflow is billed per minute, so the dispatch waits for owner approval (OC8).

## Docs and release notes

At build time, in the same change:

- `CHANGELOG.md`: one minor entry describing the cut, the opt-in, the shadow behaviour and the exclusions.
- `docs/USING-KHAOZENGINE.md`: the Tile world section (opt-in, `InteriorMeshes`, the issues) and the
  `Scene3D.DrawProps` section (the flag).
- Package READMEs: `KhaozEngine.Primitives`, `KhaozEngine.Render3D`, `KhaozEngine.Terrain.Render3D`,
  `KhaozEngine.TileWorld` and `KhaozEngine.TileWorld.Render3D`.
- `docs/INDEX.md`: this row's status.
- A full Markdown sweep for `SpecParams`, the frame UBO size and the Model layout's binding list, which several
  design records quote.

## Risks

- **Coverage at the window edge.** With 64 m of drift a 384 m window covers only 128 m on the far side, short of
  the 192 m HLOD distance (OC1).
- **Courtyards.** An Indoors-flagged courtyard with a tree in it is cut up to the top of its storey, trunk
  included, as approved. Content that wants a courtyard tree leaves those tiles outdoors, which also keeps the
  roof rule honest.
- **The leaf sliver and the gable row.** Leaves under the roof surface stay visible with roofs hidden, worst at the
  gable ends. If the owner look (IC6) finds it noticeable, the fix is a finer cell or reading the top surface
  rather than the lowest sample, which risks cutting above the roof.
- **Shadow pipeline cost.** Opted-in trunks move from the depth-only pipeline to one with a discard, which can
  lose early depth on some hardware. The timing capture measures the shadow pass inside the frame block.
- **Upload hitch.** A re-centre uploads 9 MiB in one frame. If it shows, the lattice snap already allows a
  toroidal window that uploads only the new strip.
- **Animated foliage.** A tree moved into the animated foliage path is not cut, because that path writes `w = 0`.
- **Disjoint storeys.** Two interiors stacked in one column with outdoor space between them merge into one span
  and cut the space between (OC4).

## Out of scope

- Authoring-time prevention. `TileWorldClearance` is unchanged and optional.
- The MapDoc adapter, beyond its contract.
- Cutting HLOD merged meshes, grass, foliage, skinned meshes or ordinary props.
- Picking, the target outline and the silhouette pass, which still see uncut meshes (OC10).
- Dissolving the cut edge, or any soft transition at the wall.
- Interiors the observer is in deciding anything. The cut is static.

## Alternatives

Owner scores from the IC3 ruling.

- **Interior height field (40, chosen).** CPU-built, one texture, testable headless through a CPU mirror, and the
  same shape for TileWorld and MapDoc.
- **Top-down GPU roof depth map (35).** Render roofs from above each time interiors change and cut below that
  depth inside the outline. It needs a new pass on three backends, its result is only observable on a GPU, and
  the outline still has to come from the CPU.
- **A box per building (33).** One oriented box per building, sent as a uniform array. A box cannot follow a
  pitched roof: set to the ridge it cuts the crown above a sloped roof, set to the eaves it leaves branches under
  the slope.

## Open for owner or orchestrator

- **OC1 Window size against re-centre drift.** The approved 384 m window centred on the focus covers 192 m in
  every direction only at the moment of a re-centre. After 64 m of travel it covers 128 m ahead, so trees between
  128 and 192 m on that side are drawn at full detail but not cut. Options: (a) keep 384 m and 64 m and accept it,
  (b) re-centre at 32 m, doubling builds, (c) size the window to twice the cut range plus the drift, 512 m and
  2048 cells, 16 MiB. **Recommendation: (c).** Memory is modest and builds stay rare.
- **OC2 Point-light shadows.** IC5 says the shadow pass cuts. The cascade pass is certain. A lantern inside a
  building would otherwise throw a cut branch's shadow onto its floor. Options: (a) cut cascade and point shadows,
  (b) cascade only. **Recommendation: (a).** It is the same fold into the point dissolve pair, two fragments.
- **OC3 Snap the cell to the tile.** At 25 cm cells and a tile size that is a multiple of 25 cm, tile edges fall on
  cell edges and a TileWorld outline is exact. Options: (a) the TileWorld adapter picks the largest cell at or
  below 25 cm that divides the tile size, (b) a fixed 25 cm everywhere, which loses up to a cell inside the wall
  on any other tile size. **Recommendation: (a).** It is 25 cm itself for a 1 m tile.
- **OC4 Disjoint interiors in one column.** The field holds one span per cell. Options: (a) merge to the lowest
  floor and highest top and count merges in the build result, (b) keep the lower interior only, (c) a second span
  per texel, doubling memory. **Recommendation: (a)** this round, with (c) only if a world needs it.
- **OC5 A roof with no readable mesh.** Options: (a) fall back to `CeilingY` and report `interior.roof-mesh`,
  (b) refuse to build the field. **Recommendation: (a).** It matches the clearance check, which reports a mesh
  issue and keeps going, and it fails toward leaving branches.
- **OC6 Where the adapter's issues surface.** Options: (a) returned and logged once by the view only, (b) also
  appended by ke-tileedit's `world_validate`, (c) also refusing a save. **Recommendation: (a)** in this minor
  and (b) as a follow-up, since an inner-edge wall is a content defect the author should see. Not (c): a cut
  falling back to nothing is safe.
- **OC7 Texel format.** Options: (a) packed `R8G8B8A8UNorm`, (b) add `R16G16UNorm` to the GPU seam on three
  backends, (c) `R16G16Float`. **Recommendation: (a).** Exact and free. (c) loses centimetres with height.
- **OC8 The timing capture.** The hosted bench is billed and runs on dispatch. Options: (a) one dispatch of the
  cost test filter on the T4 legs after build approval, (b) also a native Metal run on the dev Mac.
  **Recommendation: (a)** only. (b) adds GPU load on the shared Mac and Metal correctness is already proven by the
  GPU workflow.
- **OC9 The window's centre.** The brief says the observer. Options: (a) the prop draw focus, (b) the observer's
  tile. **Recommendation: (a).** The cut range and the full-detail draw range then share one centre by
  construction. In Grimhollow the two differ only by the camera's orbit.
- **OC10 Picking through a cut.** A click through a doorway can still pick a cut tree, because
  `TileObjectRaycast` picks against each model's drawn body and knows nothing of the cut. Options: (a) leave it and file a backlog issue, (b) test picks against
  `InteriorHeightField.Contains` in this round. **Recommendation: (a).** It is separate behaviour with its own
  tests, and this design cannot file the issue itself.
