# Attachment lift: worn parts over lower layers

A skinned part fitted to the bare body can sink into a garment worn beneath it. At outfit bind, lift
moves the part's rest vertices along their baked directions to clear the visible layers below. The same
fit can sit directly on skin or over several layers. Every render pass draws the same lifted geometry.
The [consumer guide](../USING-KHAOZENGINE.md#worn-parts-over-lower-layers) covers fitting and publishing.

## Decisions

### 1. Heights live on body cells

A level-8 barycentric grid divides each body triangle into 64 cells. The grid follows the triangle's
topology, so cell identities can survive a reshape that preserves topology. A whole region can contain a
thin panel, a raised hem and a rigid plate. A regional maximum makes the outer layer clear the tallest
feature across all three. A whole body triangle can still span both the hem and the adjacent panel.
In a rest-pose study of fitted meshes, one height per triangle followed the shirt under straps better than
regional standoff, but left most of the stacked cape's gap. Sixteen cells per triangle still left more gap
under the stacked cape than 64. Going to 256 cells showed no measurable improvement and quadrupled the
number of cells in the grid.

### 2. The engine measures a footprint published beside the set

One offline measurement assigns each piece triangle its cells and, for fixed triangles, a height per cell.
A lower part's height and an upper part's footprint use the same projection and cell boundary rules.
Measuring height and footprint independently would require tools to agree at every boundary. Storing the
result beside the definition keeps the many triangle entries out of the JSON and keeps sampling out of bind.
Sampling just the corners would miss cells under a triangle's interior.

### 3. Lift follows drawing

A triangle contributes height only if the outfit draws it. The triangle's own region tag controls its
visibility, including when its footprint reaches into cells of another region. A shirt's collar band can
carry the panel's tag while standing over collar cells. Covering the panel hides the band too, so its height
vanishes from the collar cells. A height masked by the cell's region would still push the outer layer up there.

### 4. Cross sections lift as one

Fitting writes one direction and one underside bearing for all vertices of a cross section. The engine
welds coincident positions, then groups the welds of a part by bitwise equal lift direction and bearing,
treating both signs of zero alike. The whole section uses one distance, so its thickness survives where
its triangles carry different region tags or touch different cells. A coincidental equal pair merges two
sections and raises both to the larger required distance. Deriving the groups from the baked values
avoids a separate GLB attribute.

### 5. Displaced mesh views carry lift into every pass

A displaced view owns rest positions and uses the source mesh's materials, binds and skeleton. Colour,
depth, motion vectors, cascade and point shadows, outlines and dissolve all draw the view's own buffers.
This puts the work at bind, when the outfit changes. A fresh upload would duplicate materials and textures.
Shader displacement would repeat the work every frame and would also need to reach the CPU skinning path.

## Words

- **Standoff** is the declared maximum rest-pose height of the set above the bare skin in a region, in metres.
- **Bearing** is the baked rest-pose height of a lifting section's underside above the bare skin.
- **Lift direction** is the baked outward skin direction in the body's rest space. The engine normalizes
  it when applying the lift.
- **Clearance** is the authored gap a lifting part aims to keep above lower layers.
- **Body cell** is a barycentric subtriangle identified by the flattened body triangle and its cell index.
- **Footprint** is the measured cells under each triangle of a set, with heights for fixed triangles.

## The rule

Resolve hiding first, then process sets in ascending layer order. Every set on a layer reads the same
snapshot of strictly lower layers. An unlayered set neither lifts nor contributes.

1. A layered set uses its measured footprint when the footprint's map hash and body triangle count
   match the outfit's body. Otherwise its regional data applies. A fallback set supplies its standoff
   uniformly to the cells in each declared region visible for that set.
2. The height below a cell is the highest effective height among contributions from strictly lower layers.
3. A section gathers the cells of every triangle incident to any of its welded positions. When the
   section's set uses regional data, it gathers the body regions those triangles are tagged with and
   reads the maximum height in each. This can include measured height from a lower set.
4. A section with a contribution below lifts by
   `max(0, maxHeightBelow + clearance - bearing)` along its normalized direction. It stays at its fitted
   position when there is no contribution. Every corner of a triangle sees the tallest sampled height
   under that triangle, so a coarse facet crossing a hem rises as a whole to clear the highest point.
   Fitting can follow details resolved by the body surface and its cells. A coarse body still gives a
   coarse surface frame, even with smaller cells.
5. A drawn fixed triangle contributes its measured height in each cell plus the largest lift that moved
   any of its corners. The set's effective height in a cell is the maximum of those contributions.
   A hidden triangle or swinging triangle supplies no measured height. A visible rigid part can
   contribute but its corners stay at their fitted positions. A fallback set contributes its regional
   standoff plus the largest lift at a welded position incident to a triangle tagged with that region.

The earlier regional rule gives exactly the same result in an outfit without an applicable footprint
except where whole sections now share the highest lift their vertices need. The implementation keeps a
region-sized height field for this case. In a mixed outfit, a regional standoff represents the entire
region. It assumes a part's triangles lie in the regions they are tagged with. If a lifting triangle
tagged A also spans B, its lift is added to A and a measured part above B can sink into it. Measuring the
lower set accounts for where the triangle actually lies.

**Oblique directions.** Heights use the body triangle's outward normal. For lift distance `d` at angle
`theta` to that normal, the rise along the normal is `d * cos(theta)` and the lateral motion is
`d * sin(theta)`. In the rest-pose outfit measurement, 0.12% of the strap underside entered the shirt
by up to 0.67 mm where the directions were 18 to 31 degrees off normal. This was under a strap section
4 to 5 mm thick. Dividing the lift by the cosine used a single bearing for all those cells. For a large
clasp spanning faces up to 60 degrees off normal, that raised the cape's median gap over the shirt from
2.4 to 7.7 mm in the same comparison. Use directions close to the body normals and size clearance for
the remaining deviation. A per-cell height frame needs the section's underside height in each cell.

## Formats

[Format 5](../../KhaozEngine.Attachments/README.md#format-5) adds regional `standoff` and skinned part
`lift`. Fitting stores `_LIFT_DIRECTION` and `_LIFT_BEARING` in the part's GLB. The same attributes feed
regional and measured lift. [Format 7](../../KhaozEngine.Attachments/README.md#format-7) adds the optional
root `footprint` object with `key` and `sha256`. A footprint reference requires a layer. The writer emits
format 7 only when the reference is present, preserving the canonical bytes and hashes of formats 1
through 6. Publish format 7 after every reader supports it.

### Footprint file (format 1)

`LiftFootprintFile` reads and writes canonical little-endian bytes:

| Order | Bytes |
| --- | --- |
| Header | ASCII `KHLF`, unsigned 16-bit version `1`, byte level `8`, zero reserved byte, 32 raw bytes of the canonical body region map SHA-256, unsigned 32-bit body triangle count and part count. |
| Each part, by ascending positive ID | Unsigned 32-bit part ID and triangle count. |
| Each triangle, in upload order | Unsigned varint `(entryCount << 1) \| swingingBit`, then that many unsigned varint cell-key deltas from a starting key of zero. Cell keys increase strictly after the first. |
| Each fixed triangle, after all its deltas | One unsigned 16-bit height for each cell in the same order, in units of 0.01 mm. Swinging triangles have no height bytes. |

Varints use up to five bytes with seven payload bits per byte, low groups first, and the high bit
indicates continuation. Use the shortest encoding. A cell key is `triangleIndex * 64 + cellIndex`,
where triangles flatten in body region map part order. Piece triangle order is material upload order,
then triangle order within each material.

### Measuring and checking

`LiftFootprintMeasure.Measure` samples each piece triangle on a barycentric grid with spacing at most
1 mm, including its corners and edges. Supply meshes in the body's rest frame, including placed rigid
parts. Each sample finds the nearest nondegenerate body triangle using the same scalar double projection
as `StandoffClaimCheck`. Within 1 micrometre of the nearest distance the highest signed height wins,
with ties going to the first triangle in body part and triangle order. A bounding volume hierarchy
finds that result without a full scan. The closest point's barycentric coordinates choose the cell.
The signed height is measured from the triangle plane along its outward normal.

A fixed triangle stores its highest sampled height in each cell, clamped at zero and rounded upward to
0.01 mm. A fixed height over 0.25 m is refused. A triangle with any vertex weighted to a chain joint is
swinging. It records cells for lift and supplies no height. `LiftFootprint.RegionHeights(body)` reduces
the fixed heights to each region's maximum for authoring regional standoff.

`LiftFootprintCheck.Verify` repeats the measurement, checks body identity, part IDs, triangle counts and
swinging status, and reports missing cells or understated fixed heights. Extra cells and greater heights
are allowed. If the body differs, it reports the body mismatch and any structural part or triangle
findings without comparing the cells. Like the measurement, it checks sampled rest geometry.

## Binding and drawing

`AttachmentSetCache.Get` verifies the footprint file's exact bytes against the declared SHA-256, parses
it and matches its part IDs against the set before mesh upload. The loaded footprint travels with the
set into `BindOutfit`. A body map digest or total body triangle count mismatch selects regional fallback.
For a matching body, `BindOutfit` checks each part's triangle count against the skinned material index
lists or uploaded rigid geometry before acquiring any views. `BoundOutfit.FootprintApplied(wornIndex)`
and the headless `OutfitLift.FootprintApplied(wornIndex)` report applicability per worn set, including
sets that only contribute height.

The headless resolver checks an applicable footprint's part IDs and skinned triangle counts against
the coverage snapshot. It trusts that the file measures the set's own meshes. Use
`LiftFootprintCheck.Verify` to check that measurement and `BindOutfit` to check rigid triangle counts.

### Displaced mesh views

`BindOutfit` gives visible material groups that need displaced geometry a view via
`IAttachmentDisplacedViewScene.CreateSkinnedView(source, indices, restPositions)`. Both shipped
Scene3D attachment adapters implement the capability. Plain view scenes can bind outfits that need no
displacement. A capability refusal occurs before acquiring any views.

The view owns vertex and index buffers and a CPU rest copy. It retains the source normals and tangents
and grows the source bounds by the largest displacement. Views share storage by source, visible indices
and lifted positions. The last outfit release defers freeing the view to the frame boundary. The
[renderer reference](../../KhaozEngine.Attachments.Render3D/README.md#outfits) describes lifetime and draw calls.

### Chained parts

A chained part can lift at its fixed root while a swinging tail with enough bearing keeps its fitted
position. The displacement is applied to rest vertices and the existing chain joints and colliders
pose them as before. A footprint's swinging triangles can lift but do not hold up another layer.
Regional `StandoffClaimCheck` measures fixed vertices, including fixed vertices of a triangle with a
swinging corner. For fallback standoff on a chained part, check those claims separately.
A garment that should push a swinging panel aside needs authored worn set colliders.

## Cost

- Per frame, draw counts, vertex counts, shaders and passes are unchanged. Valid warm draws allocate no
  managed memory.
- Per bind, lift welds positions, groups sections and reads the height below a triangle's footprint.
  Visible fixed triangles scatter their heights into the running maximum. The cell path freezes that
  maximum at each layer boundary. Regional contributors in a mixed outfit fill the corresponding cells.
  A distinct displaced view needs one vertex and index upload.
- Per cached footprint, parts pack cell keys and height units into shared arrays. A fixed entry uses
  four bytes for its key and two for its height. A swinging entry uses only its key. Triangle ranges and
  part metadata add overhead. Cell scratch uses two pooled floats per body cell and one region byte per
  body triangle, independent of layer count. Each distinct displaced view holds its 96-byte vertices
  in both the GPU buffer and CPU rest copy, plus a shared position array at 12 bytes per vertex.

`LiftBindCostDiagnostics` reports 32 binds with measured liners and armour on a 1,584-triangle synthetic
body. Measurement, serialization and set upload happen before bind timing. The JSON report records
`footprintBodyTriangleCount`, `footprintBinds`, the regional bind and draw measurements, and the runner
environment. The draw comparison defaults to 120 warm frames and 600 measured frames. Default CI
excludes the diagnostic. Run the command on a GitHub-hosted runner after approval for timing evidence.
`KE_LIFT_COST_SMOKE_FRAMES` can set both frame counts to 1 through 119 for a harness check.

```sh
KE_GPU_TESTS=0 dotnet test KhaozEngine.Render.Tests -c Release --filter 'Category=Diagnostic&FullyQualifiedName~LiftBindCostDiagnostics' --logger 'console;verbosity=detailed'
```

## Refusals

- [Set refusals](../../KhaozEngine.Attachments/README.md#set-refusals) cover a footprint without a layer,
  an invalid key or hash, a key reused by a mesh, and a missing or mismatched asset in
  `AttachmentSetDefinition.ValidateReferences`. The cache's exact-byte read uses the same
  `InvalidDataException` path as a mesh read.
- [Footprint data refusals](../../KhaozEngine.Attachments/README.md#lift-footprint-refusals) cover invalid
  models, malformed or noncanonical files and a loaded footprint whose part IDs differ from its set.
  [Claim refusals](../../KhaozEngine.Attachments/README.md#coverage-claim-check) cover invalid measurement
  inputs, including missing piece IDs, an oversized sampling grid and heights past the fixed height limit.
- [Binding refusals](../../KhaozEngine.Attachments.Render3D/README.md#binding-refusals) include
  `FootprintTriangleCountMismatch` with the worn set source, worn index, part ID and both counts, and
  `OutfitLiftUnsupported` for a scene missing displaced views.
- [GLB refusals](../../KhaozEngine.Attachments/README.md#attachmentskinnedgeometry) cover missing, invalid
  or inconsistent lift attributes. `SkinnedMeshViewRefusal` identifies invalid displaced rest positions
  or a position count that differs from the source.

## Tests

Headless tests pin the format 7 and KHLF bytes and the hashes of older set formats. File and model
refusal cases have passing controls. Measurement tests cover a raised hem within one body triangle,
rigid parts, swinging triangles, tie-breaking at ridges and verification findings. Outfit tests cover
straps clearing a local hem, stacked lift, a rigid plate limited to its own cells, hidden triangles,
equal data lifting together across region tags and regional fallback. Binding tests verify application
on a matching body, fallback for other bodies, mismatched skinned and rigid triangle counts, shared
view lifetime and zero managed allocation in warm frame calls.

## Not in this release

- [Facet tilt](https://github.com/APKiwiOrg/KhaozEngine/issues/1893). A rigidly rising coarse facet
  spans nearby high and low features even when the cells resolve both. Tilting could follow that
  contour with the existing mesh.
- [Clearance in each cell's height frame](https://github.com/APKiwiOrg/KhaozEngine/issues/1895). This
  needs an underside height for every relevant cell, as described under the lift rule.
- Subdividing a coarse worn part at bind. This changes the topology shared by skinning, chains,
  outlines and motion vectors.
- Lift on rigid parts, displacement of lower layers and collision geometry grown from a footprint.
- [Live reshape](https://github.com/APKiwiOrg/KhaozEngine/issues/1869). Stable topology preserves cell
  identities. Applying existing heights and fitting directions to the reshaped surface needs its own path.

## Signatures

- `AttachmentSetDefinition(parts, layer, covers, colliders, standoff, footprint)` accepts a nullable
  `LiftFootprintRef(Key, Sha256)`. Existing constructors and `AttachmentSetJson` format constants retain
  their signatures and values.
- `LiftFootprintMeasure.Measure(bodyMeshes, body, piece, pieceMeshes)` returns a `LiftFootprint` for
  `LiftFootprintFile.Write(footprint)`. Use `LiftFootprintFile.Parse(bytes, sourceName)` to read it and
  `LiftFootprintCheck.Verify(bodyMeshes, body, piece, pieceMeshes, declared)` to get a
  `LiftFootprintReport`.
- `OutfitLift.Resolve(coverage, meshes, footprints)` accepts one optional footprint per worn index.
  The two-argument overload uses regional data. Both take one `LiftingPartMesh` per part declaring lift.
  `HeightBelow` and `EffectiveStandoff` summarize maxima by body region. `LiftedPositions` returns rest
  positions by material or null when no vertex moves in float. A coincident weld takes the first
  vertex's lift basis in upload order, which the GLB policy keeps within tolerance of the others.
- `BoundOutfit.FootprintApplied(wornIndex)` takes the zero-based index from the worn list given to
  `BindOutfit`. The new APIs are additive and require no new member on a public interface.
