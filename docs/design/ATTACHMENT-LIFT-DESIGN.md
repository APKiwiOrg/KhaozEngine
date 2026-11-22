# Attachment lift: worn parts over lower layers

Planned under [#1880](https://github.com/APKiwiOrg/KhaozEngine/issues/1880) for the next minor release. This note
describes the system to be built and keeps the decisions and the reasons for them.

A worn skinned part is fitted to the bare body. It sits a small distance outside the skin and copies the skin's
weights, so it follows the body in every pose. Worn over a garment that stands off the skin, the same part lies
under the garment's surface wherever it bears on the body, and the garment draws over it. Lift raises such a part
clear of the layers beneath it when an outfit is bound. One fit serves bare skin and any garment, every render pass
draws the same lifted part, and nothing is computed per frame.

## Decisions

### 1. The engine lifts the higher part when the outfit is bound

Lift moves a skinned part's rest vertices outward along the skin's normal, once, when an outfit is bound. A part over
bare skin does not move, so its fit on the bare body is unchanged. Over a garment it rises just enough to keep its
clearance above the garment.

Rejected:

- **A second fit per part over a standard garment allowance**, picked when a garment covers the part's regions. It
  needs one more fit per allowance and per body. A garment thicker than the allowance still sinks and a thinner one
  floats. A garment that covers only some of the part's regions, such as a vest under a harness that also crosses
  the shoulders, has no right mesh to pick. Stacked layers multiply the fits.
- **Pressing the lower garment toward the skin under the higher piece.** A low poly garment has no vertices under a
  thin strap to press, and flattening whole regions changes the garment's own look.
- **Fitting every armour part over a standard garment.** Worn on bare skin it shows a gap, which fails a look.
- **Moving vertices in the skinning shaders every frame.** See decision 4.

### 2. Each worn set declares how far it stands off the skin per region

A worn set declares its **standoff**: for each region where it is worn, how far its outer surface stands off the
skin at the rest pose, as fitted on the bare body. It is a few numbers beside the layer and the covered regions,
resolving it costs nothing, and a tool can explain it. `StandoffClaimCheck` measures a piece against the body
offline and reports any region where the piece stands higher than it declares. A garment that understates its height
is caught on the garment, not on every armour part that later sinks into it.

Measuring the garment's surface at bind, by casting a ray from every higher vertex into the lower meshes, was
rejected. It costs tens of milliseconds per bind, misses at hems and thin features, and each layer would need the
lifted geometry of the layer below it first.

### 3. A lifting part bakes its lift direction and bearing per vertex

The fitting step already finds the skin under each vertex to copy its weights. It also writes two vertex attributes
into the part's GLB: the **lift direction**, the skin's outward normal under the vertex in the body's rest space, and
the **bearing**, how far the part's underside stands off the skin under that vertex. A vertex lifts by
`max(0, heightBelow + clearance - bearing)` along its lift direction.

Every vertex of one cross section shares a footprint and so a bearing, which keeps the section's thickness. A span
that already bridges a hollow above the garment, as a taut strap does, keeps its place, because its bearing is
already higher than the garment.

One uniform lift per part, with no per vertex data, was rejected: a taut span standing well off a hollow would rise
by the full lift and float. Taking directions from the part's own normals was rejected: the inner and side faces of a
solid strap point at the body or sideways.

### 4. Lifted vertices reach every pass through displaced mesh views

A **displaced view** is a skinned mesh view that carries its own rest positions. It owns a vertex buffer and a CPU
rest copy, shares the source's materials, alpha cutoff, inverse binds, rest pose and skeleton, and draws a full or a
filtered index list. Every pass draws an entry's own buffers from the record it copies when the frame is prepared,
so colour, depth, motion vectors, cascade and point shadows, outlines and dissolve draw the lifted part with no
shader, pipeline or draw call change. Displaced views are shared between outfits by content and released one frame
late, exactly like the views that hide covered triangles.

A fresh upload per lifted part was rejected because it duplicates textures and material sets. A lift in the shaders
was rejected because it touches every skinned shader variant on three backends and the CPU skinning path, and would
compute every frame what changes only when an outfit does.

### 5. The capability is a minor

Format 5, the lift rule, displaced views, the optional scene capability and the standoff check add types, members
and overloads. No public interface gains a member, and refusal enums are append only.

## Words

- **Standoff**: how far a worn set's outer surface stands off the skin over one region, at the rest pose, as fitted
  on the bare body.
- **Bearing**: how far a lifting part's underside stands off the skin under one of its vertices.
- **Lift direction**: the skin's outward unit normal under a vertex, in the body's rest space.
- **Clearance**: the gap a lifting part keeps above the layers beneath it.
- **Height below**: for one worn set and one region, the highest effective standoff among the visible worn sets on
  lower layers there.

## The rule

An outfit is resolved against a body's region map as it is for hiding. Then, in ascending layer order:

1. A worn set **contributes** at region `r` when it has a layer, declares a standoff at `r`, and `r` is not hidden
   for it, which means no worn set on a strictly higher layer covers `r`. A hidden region draws none of the set's
   triangles there, so nothing needs to clear it.
2. For a worn set `k`, the **height below** at `r` is the largest **effective standoff** at `r` among the sets that
   contribute there from layers strictly below `k`'s. With none, nothing needs clearing at `r`.
3. A vertex of a lifting part takes the regions of every triangle that uses it or any vertex of the same part at the
   same position. Coincident vertices from split normals or material seams therefore always move together. Its
   height below is the largest over those regions.
4. The vertex lifts by `max(0, heightBelow + clearance - bearing)` along its lift direction, and not at all when no
   region it touches has a height below.
5. A set's **effective standoff** at `r` is its declared standoff there plus the largest lift any vertex of its parts
   took at `r`. A layer above therefore clears a part that was itself lifted. The bound is conservative: where a
   set's highest point over a region did not lift, a layer above clears it by more than it needs.

Sets on one layer never lift over each other, as they never hide each other. A set with no layer never lifts and
never contributes. The body is the floor, so a part over bare skin keeps its fit. Rigid parts do not lift (see "Not
in this release"), but a rigid part's height counts through its set's declared standoff, so a part on a higher layer
clears it.

Lift ends at a garment's hem in a ramp one triangle wide on the side away from the garment, the part rising over
the hem as a strap does.

`OutfitLift` in `KhaozEngine.Attachments` runs the rule without a renderer, and `LiftedPositions` applies it to a
part's rest positions, so a pose check or a fitting tool lifts exported poses exactly as the game draws them.

### Example

A garment on the garment layer declares a standoff of 8 mm over `chest` and `back`. A strap on the armour layer bears
at 2 mm over `chest` with a 3 mm section, so its set declares a standoff of 5 mm there. Over `back` it bridges a
hollow at 14 mm. Its clearance is 1 mm. Worn together, the strap's vertices over `chest` lift by 7 mm, so its
underside sits at 9 mm and its outer face at 12 mm. The bridging span does not move, because it already stands more
than 9 mm off. A cloak collar on the outerwear layer bears at 2 mm over `chest` with a clearance of 1 mm. The armour
set's effective standoff there is 5 + 7 = 12 mm, so the collar lifts by 11 mm and clears the strap by 1 mm. Without
the garment the strap does not move, and the collar lifts by 4 mm to clear the strap's 5 mm.

## Format 5

`AttachmentSetJson.LiftFormatVersion` is 5. Format 5 is format 4 with two optional members: `standoff` at the root
and `lift` on a skinned part. `Write` emits format 5 only for a set that has either, so every format 1 to 4
definition keeps its bytes and `SemanticSha256`. A set with a standoff or a lifting part needs a layer.

```json
{
  "formatVersion": 5,
  "layer": "garment",
  "covers": ["belly", "chest"],
  "standoff": [["belly", 0.008], ["chest", 0.008], ["upperArmL", 0.006]],
  "parts": [
    { "id": 1, "kind": "skinned",
      "mesh": { "key": "parts/tunic.glb", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
      "regions": [["belly", 30], ["chest", 44], ["upperArmL", 12]] }
  ]
}
```

```json
{
  "formatVersion": 5,
  "layer": "armour",
  "covers": [],
  "standoff": [["chest", 0.006], ["shoulderL", 0.006]],
  "parts": [
    { "id": 1, "kind": "skinned",
      "mesh": { "key": "parts/harness.glb", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
      "regions": [["chest", 40], ["shoulderL", 18]],
      "lift": { "clearance": 0.001 } }
  ]
}
```

- `standoff` lists `[region, metres]` pairs, at most one per region and at most 128 in all. Each value is finite, 0
  or more and at most 0.25 m. The writer sorts the pairs by region name, so listing order never changes the
  revision. A region may have a standoff without being covered: an open vest stands off the chest without hiding it.
- `lift.clearance` is finite, 0 or more and at most 0.05 m. The file states it, as chains state every setting, so a
  piece never changes look when an engine default moves.
- One part may carry both `lift` and `motion`.
- A lifting part's mesh must carry the lift attributes below.

## Lift attributes in the part GLB

A lifting part's GLB carries two custom attributes on every primitive, one element per vertex:

| Attribute | Accessor | Meaning |
| --- | --- | --- |
| `_LIFT_DIRECTION` | VEC3, FLOAT | The skin's outward unit normal under the vertex, in the body's rest space. |
| `_LIFT_BEARING` | SCALAR, FLOAT | How far the part's underside stands off the skin under the vertex, in metres. |

The skinned attachment policy refuses a lifting part when a primitive lacks either attribute, stores it with another
type or element count, has a direction that is not finite or not unit length within 0.001, or has a bearing that is
not finite or is negative. Vertices of one part at the same position must carry the same direction and bearing
within 0.0001, because they move as one and different data would open a crack. A part that does not lift may carry
the attributes, and they are ignored.

The renderer groups a part's vertices by material in first use order when it uploads them, and region runs follow
that grouping. `AttachmentGlb.LiftBasis` reads the attributes in flattened mesh, primitive and vertex order, as
`SwingingVertices` reads its mask, and names each vertex's material part and its index there, so a tool lines the
data up with the upload by the same rule. The renderer reads the attributes in upload order.

## Displaced mesh views

```csharp
SkinnedMeshHandle lifted = scene.CreateSkinnedMeshView(source, indices, restPositions);
```

- `restPositions` holds one position per source vertex. Every other vertex attribute is the source's. Normals and
  tangents are kept: a lifted surface runs parallel to the fitted one except along a hem ramp, which lights as it did
  before.
- The view owns a vertex buffer, an index buffer and a CPU rest copy, which CPU skinning, CPU motion vectors and the
  outline pass read. It shares the source's materials, alpha cutoff, inverse binds, rest pose and skeleton.
- Its bounds are the source's grown on every side by its largest displacement, which contains the lifted positions
  and, for a chained part, the swing of its lifted vertices. Growth carried from the source later keeps that margin.
- It is a view in every other respect: `IsSkinnedMeshView` is true, it cannot be viewed again, it counts as a live
  view of its source, it is released at the next frame boundary, and its buffers go to the retire queue.
- New `SkinnedMeshViewRefusal` reasons: `PositionCountMismatch` with `VertexCount` and the supplied count, and
  `PositionNonFinite` with `VertexIndex`.

## Binding and drawing

- `IAttachmentDisplacedViewScene : IAttachmentViewScene` adds
  `CreateSkinnedView(source, indices, restPositions)`. `Scene3DAttachmentScene` and `Scene3DTileWorldScene` implement
  it. A displaced view is released through `ReleaseSkinnedView`.
- `BindOutfit` resolves coverage, then lift. For each lifting part with a vertex that moves, it builds the lifted
  positions for each material handle and acquires a displaced view of the part's visible triangles, keyed by source,
  indices and positions, so outfits and wearers that lift a part alike share one view. A part with no moving vertex
  draws exactly as before.
- A lift over a scene without the capability refuses with `AttachmentBindingRefusal.OutfitLiftUnsupported`, naming
  the worn set and the part. An outfit that lifts nothing binds over any view scene.
- The cache reads a part's lift attributes from its verified bytes the first time a bind needs them, and keeps them.
- Draws, outlines, motion and every draw overload are unchanged. An outfit draw names a handle, and a lifted part's
  handle is its displaced view.
- Binding allocates. `WriteDraws`, `Queue`, `QueueOutline` and `WornMotion.Step` allocate nothing once warm.

## Chained parts

- A lifting part may carry chains. Its vertices lift at rest by their own bearing, so a strap tail hanging off the
  body keeps its place while its root on the body rises. Chain joints, rest hangs and colliders are unchanged, and
  the swing turns the lifted vertices with their joints.
- A chained part's culling reach grows by its largest lift, through the displaced view's bounds.
- A standoff claim rests only on fixed triangles. `StandoffClaimCheck` ignores vertices weighted to chain joints, as
  `CoverageClaimCheck` does, because a swinging hem holds no height. A chained garment under armour therefore lifts
  the armour by the height of its fixed panels.
- Chains on a higher layer collide with the body's colliders and with worn set colliders, never with a garment's
  standoff. A garment that must push a cloak's panels out declares worn set colliders, as format 4 allows.

## Standoff claim check

`StandoffClaimCheck.Verify(bodyMeshes, body, piece, pieceMeshes)` takes the meshes `CoverageClaimCheck` takes, in the
body's rest frame, and validates them by the same rules with the same refusals. For each fixed vertex of the piece
it finds the closest point on the body, and takes that body triangle's region and the vertex's height along the
triangle's outward normal. The report gives, for each region, the highest vertex and the declared standoff, and
names:

- **understated** regions, where a vertex stands higher than the declared standoff,
- **undeclared** regions, where the piece stands but declares no standoff, so a part above would not clear it there.

It runs offline and suits low poly meshes. A gate in the game decides what fails.

## Room for live reshape

Standoff, bearing and clearance are heights above the skin. A live reshape
([#1869](https://github.com/APKiwiOrg/KhaozEngine/issues/1869)) that keeps each piece at its height above the skin
keeps every one of them valid, so only the lift direction has to follow the reshaped skin. When reshape arrives, the
same data drives the lift wherever reshape evaluates a piece, with no format change. Cloth
([#1870](https://github.com/APKiwiOrg/KhaozEngine/issues/1870)) is untouched: a cloth part's fixed rows can declare a
standoff like any other.

## Cost

The wearables limits are 32 wearers, at most 2 ms added CPU preparation, no managed allocation once warm and at most
1.3 ms added GPU time at 32 characters on a T4.

- Per frame: nothing. Draw counts, vertex counts, shaders and passes are unchanged.
- Per bind: one pass over each lifting part's vertices and triangles, and one vertex and index buffer upload for each
  displaced view not already shared.
- Memory: each distinct displaced view holds its vertices twice, 96 bytes each in the GPU buffer and in the CPU copy.
- `LiftBindCostDiagnostics` reports the bind cost of 32 wearers with distinct garment and armour pairs and the per
  frame draw cost with lifted parts, as report only timing excluded from default CI.

## Refusals

Each new reason is appended to its enum and carries the identifiers that locate the fault.

- `AttachmentSetRefusal`: a standoff or a lift without a layer, a standoff region name that is empty, malformed or
  repeated, a standoff value out of range, too many standoff entries, and a clearance out of range.
- `AttachmentSkinnedGeometryRefusal`: a lift attribute missing from a primitive, a lift attribute with the wrong
  type or count, a lift direction that is not finite or not unit length, a bearing that is not finite or negative,
  and coincident vertices with different lift data.
- `SkinnedMeshViewRefusal`: a position count that does not match the source, and a position that is not finite.
- `AttachmentBindingRefusal`: a lift over a scene that cannot create displaced views.

## Tests

All headless, on synthetic bodies and pieces built in code.

- **Format.** Format 5 round trips. Formats 1 to 4 keep their bytes and hashes. Each refusal sits beside a passing
  control that differs only in the broken rule.
- **Lift attributes.** A lifting part with the attributes loads. Each attribute refusal is reached beside a control.
  The flattened read lines up with the upload order across two materials.
- **Rule.** A part over a garment lifts by `heightBelow + clearance - bearing`. A bridging span above the garment
  does not move. A section keeps its thickness. With no lower set nothing moves. A region a higher layer hides does
  not lift the part over it. Three layers stack through the effective standoff. Sets on one layer do not lift each
  other. Coincident vertices move together, and a hem ramps over one triangle.
- **Displaced views.** Colour, cascade and point shadow, outline and CPU skinning draws read the view's own vertex
  buffer or rest copy. Bounds contain the lifted positions. Release waits for the frame boundary, and each refusal is
  reached beside a control.
- **Binding.** An outfit with a garment lifts a following part on a higher layer. Two wearers of one outfit share one
  displaced view. Taking the garment off draws the source again. A chained lifting part binds, steps and draws. A
  scene without the capability refuses only when something lifts. Draws allocate nothing once warm.
- **Standoff claim.** An honest claim passes. A piece standing higher than it declares is reported understated for
  that region, and one standing where it declares nothing is reported undeclared. Swinging vertices are ignored.

## Not in this release

Rigid parts that lift, pressing lower layers toward the skin, chain colliders grown by a garment's standoff, normals
recomputed along a hem ramp, and live reshape.

A rigid part stands clear of the moving skin by its own fit, and its set usually covers the region it sits over,
which hides the lower layers there. A rigid lift, a translation fixed at bind, can be added later without a format
break.

## Signatures corrected against the code

Each task records here any shape the code required that differs from this note, keeping the planned behaviour.

- Worn set format. `AttachmentSetDefinition(parts, layer, covers, colliders, standoff)` is a new overload.
  The existing constructors keep their signatures. The new overload accepts a nullable layer so the
  constructor reports standoff or lift without a layer through the same refusal family as the JSON reader.
  `SkinnedAttachmentPartDefinition.Lift` is an init-only property, preserving the positional constructor and
  deconstruction. Standoff entry refusals carry `StandoffIndex` in input order and `RegionName` when available.
  An unpaired escaped surrogate has no decoded region name, so its JSON refusal carries the index and path.
- Displaced mesh views. The supplied position count on a `PositionCountMismatch` refusal is `PositionCount`, beside
  the source's `VertexCount`. A `PositionNonFinite` refusal names `VertexIndex`. Both carry the source index and
  generation, as every view refusal does. A displaced view owns its vertex buffer even when no vertex moves.
- Lift rule. `OutfitLift.Resolve(OutfitCoverage coverage, IReadOnlyList<LiftingPartMesh> meshes)` takes the resolved
  coverage as its one source of truth: the coverage captures layers, hidden sets, standoffs, region runs, each
  skinned part's clearance and the body's region count, so a snapshot of another outfit cannot be paired with it.
  A `LiftingPartMesh(WornIndex, PartId, Materials)` holds one `LiftMaterialMesh(Positions, Indices, Directions,
  Bearings)` per material part in upload order. `LiftedPositions(wornIndex, partId)` is a query on the result and
  returns every material part's positions, or null when no vertex of the part moves. A vertex counts as moved only
  when its position changes in float, so a lift that rounds away asks for no displaced view. A weld of coincident
  vertices takes the first vertex's direction and bearing in upload order, which the GLB policy keeps equal to the
  others within its tolerance. A standoff region the body lacks refuses as `OutfitRefusal.RegionUnknown` when the
  coverage resolves.
