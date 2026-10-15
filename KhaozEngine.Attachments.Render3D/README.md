# KhaozEngine.Attachments.Render3D

Attachment definitions bound to a posed `KhaozEngine.Render3D` skeleton. This renderer arm
references `KhaozEngine.Attachments` and `KhaozEngine.Render3D` and ships in `KhaozEngine.Game3D`.

`AttachmentSetCache.Get` loads rigid, skinned and mixed sets. The map-only `AttachmentSetBinding`
constructor and `LoadedAttachmentSet.Bind(map)` refuse sets containing skinned parts with
`InvalidDataException` explaining that an explicit host mesh is required.

Construct `AttachmentSetBinding(definition, map)` once with a `ContractJointMap`. Every rigid part must
name a contract joint in the skin. Unresolved references throw `InvalidDataException` naming the
part and joint. The binding retains the exact `Skeleton` instance and resolves node, skin-bone
index and body alignment at construction.

Scratch storage is allocated at binding construction. Valid `ComposeWorlds` calls allocate no
managed memory. Use each binding on its owning render thread. Calls on the same binding must not
overlap.

```csharp
var binding = new AttachmentSetBinding(definition, map);
var worlds = new Matrix4x4[binding.PartCount];
binding.ComposeWorlds(map.Skeleton, jointPalette, characterWorld, worlds);
```

Pass the body's current joint-world palette in skin-bone order. The binding does not advance
animation. `ComposeWorlds` writes canonical part-ID order using
`BoneSocket.ComposeRigid(part.Local * map.BodyAlignment(node), palette[bone], characterWorld)`.
Body alignment cancels the joint's base orientation while retaining its translation. Joint scale
and shear are removed, and reflected handedness is preserved.

For skinned or mixed definitions, construct `AttachmentSetBinding(definition, map, hostMesh,
skinnedMeshes)`. The dictionary maps each skinned mesh reference to its imported `SkinnedGltfMesh`.
The host mesh must carry the exact `map.Skeleton` instance. Garment skin joints resolve by ordinal
name against host skin bones, so a named node outside the skin cannot bind. A garment may use a
subset of the host and a different skin order. Its skin must contain 1 to 128 joints, with one
inverse bind and rest pose per joint. Every inverse-bind matrix must be finite and match the
corresponding actual host inverse bind with an inclusive absolute tolerance of `1e-4` over all 16
elements. Rest poses are never inverted to infer binds. Missing mesh metadata, unresolved joints or
mismatched binds throw contextual `InvalidDataException`.

`SkinnedJointCount(partId)` gives the destination capacity for a garment, or zero for a rigid part.
Allocate the destination once in the caller. `CopySkinnedPalette(skeleton, hostPalette, partId,
destination)` copies host joint model matrices into garment skin order. It does not multiply inverse
binds. The renderer alone applies the garment's inverse binds. The binding snapshots index tables
and owns no garment frame palettes. Later source-skeleton changes cannot reorder its copies.
An unknown part, a rigid palette-copy request, a foreign skeleton, a palette whose length differs
from the full host skin, insufficient destination capacity or invalid referenced joint matrices
throws `ArgumentException` before writing. The unused destination tail remains untouched. Valid
copies allocate no managed memory and perform no name searches.

`ComposeWorlds` still writes every canonical part. Rigid parts use the composition above, and
skinned parts receive the unchanged character model matrix.

Another skeleton instance, a palette whose length differs from the skin, insufficient output
space or invalid pose matrices throws `ArgumentException`. Matrices must be finite, affine and
invertible, with a recoverable joint basis. The entire result is validated before any output is
written. Output may overlap the input palette, and entries beyond `PartCount` are untouched.

Import verified, self-contained rigid GLB bytes with
`GltfLoader.LoadPartsWithMaterials(bytes, identity)`. Apply
`AttachmentGlb.ValidateRigidEmbedded(bytes, identity)` first for attachment asset policy. Each
returned `GltfMeshPart` carries its welded geometry and decoded material maps, with node transforms
already baked. Material groups retain first-use order. The importer creates no GPU resources.

## Loading and ownership

Create one `AttachmentSetCache` per scene with an `IAttachmentAssetSource`. `Get(key, expectedSha256)`
checks the exact definition bytes and every distinct mesh revision, applies the requested rigid or skinned
embedded GLB policy, and decodes all new geometry and material maps before uploading. The cache classifies
all references before using warm uploads. A reference requested under both kinds is refused, including
reuse after a different set warmed the cache. The cache copies source memory and shares each compatible
`(key, hash, kind)` upload across its parts and sets. A failure releases newly acquired resources and
preserves earlier cached assets. Skinned material groups retain their skin-order joint names, inverse
binds and rest poses as immutable metadata. Loading allocates no frame palettes.

```csharp
using var scene = new Scene3D(device, output);
using var cache = new AttachmentSetCache(scene, new DirectoryAttachmentAssetSource(assetRoot));
LoadedAttachmentSet loaded = cache.Get(definitionKey, definitionHash);
BoundAttachmentSet bound = loaded.Bind(map, hostMesh);
```

For a skinned or mixed set, use `loaded.Bind(map, hostMesh)`. It binds the cache's captured skin
metadata without reading assets or importing geometry again. It retains both rigid and skinned
material handles, and `DrawCount` includes both kinds. `BoundAttachmentSet` allocates one garment
palette per skinned part using `SkinnedJointCount`, shared by that part's material draws.

`DefinitionSha256` records the exact source bytes. `Definition.SemanticSha256` records canonical content.
Binding resolves every joint before returning a view. `Skeleton` retains the exact bound skeleton and
`DrawCount` counts every material group of every part. Loaded and bound views borrow resources and expose
no disposal operation. Their properties and binding operations throw `ObjectDisposedException` after
cache disposal.

Dispose the cache before its scene and stop using its views first. The declaration order above enforces
that disposal order. Cache disposal is idempotent and releases each unique upload once, meshes before
textures, through Scene3D's deferred retirement APIs. Use the cache and its bindings on their owning
render thread, with no overlapping calls.

The `IAttachmentScene` overload accepts an optional scene capability for recording or headless
consumers. `Scene3DAttachmentScene` uploads through Scene3D and forwards complete `RigidInstanceDraw`
and `SkinnedInstanceDraw` descriptors, including material, dissolve, shadow and motion fields. Its
rigid and skinned dissolved outline methods forward into the caller's existing outline group.
`UploadAttachment(parts)` returns an `IAttachmentMeshLease` with material-ordered
`MeshHandle` values. `UploadSkinnedAttachment(parts)` returns an `ISkinnedAttachmentMeshLease` with
material-ordered `SkinnedMeshHandle` values. Both uploads are transactional, and their immutable handles
remain borrowed until lease disposal. The shipped `Scene3DTileWorldScene` also offers this optional capability.
`ITileWorldScene` has no attachment member.

## Submission

Use the body's current palette and the exact bound skeleton each frame:

```csharp
bound.Queue(map.Skeleton, jointPalette, characterWorld, visualIncarnationKey);
var template = new RigidInstanceDraw(default, Matrix4x4.Identity)
{
    Tint = tint,
    CastsShadows = false,
};
bound.Queue(map.Skeleton, jointPalette, characterWorld, visualIncarnationKey, template);
```

The convenience overload constructs opaque white, matte, solid, visible descriptors with shadow
casting enabled. Both overloads submit rigid, skinned and mixed sets. The template overload replaces
`Mesh`, `World` or `Model`, and `Motion`. It preserves every other field, including tint, material,
dissolve and edge, casting, shadow-only, inverted shadow dissolve and dissolve complement. Skinned
parts receive the unchanged character model and host joint model matrices reordered into garment
skin order. Scene3D alone applies inverse binds. The entire pose and template are prepared before
any scene call or output write. An invalid later joint or shadow-only template without casting
cannot publish a prefix.

For synchronous descriptor consumers, allocate the mixed buffer once after binding:

```csharp
var draws = new AttachmentDraw[bound.DrawCount]; // Allocate once after binding.
Span<AttachmentDraw> destination = draws.AsSpan();
int count = bound.WriteDraws(map.Skeleton, jointPalette, characterWorld,
    visualIncarnationKey, template, destination);
for (int i = 0; i < count; i++)
{
    AttachmentDraw draw = draws[i];
    if (draw.IsSkinned) scene.DrawSkinned(draw.Skinned, draw.BoneMatrices.Span);
    else scene.Draw(draw.Rigid);
}
```

`WriteDraws` returns `DrawCount` descriptors in ascending part-ID order and stable material order.
`PartId` and `MaterialIndex` identify each draw. `IsSkinned` selects `Rigid` or `Skinned`. Each
`BoneMatrices` borrows its part's preallocated palette, shared by all its materials. Consume it
synchronously before the bound view's next preparation, submission or cache disposal. The rigid
`WriteDraws` overload taking `Span<RigidInstanceDraw>` remains supported for rigid-only sets and
throws `InvalidOperationException` for a set containing skinned parts before writing anything.
An insufficient buffer or invalid pose or template throws before any descriptor is written, and
the unused buffer suffix stays untouched.

Create the outline group through the scene's `BeginMeshOutline` API, then queue the complete set:

```csharp
var outlineGroup = scene.BeginMeshOutline(outlineColor, widthPixels, occlusion);
bound.QueueOutline(map.Skeleton, jointPalette, characterWorld, outlineGroup,
    dissolve: .5f, dissolveComplement: true);
```

`QueueOutline` prepares the same pose and uses the existing rigid and skinned dissolved outline
paths for every material group. Dissolve defaults to zero and complement to false. Occlusion and
target settings belong to the caller's group. Queue, descriptor writing and outlines all throw
`ObjectDisposedException` after cache disposal.

Temporal keys nest the caller key, the semantic revision's first 64 bits as two little-endian `uint`
values, stable part ID and material ordinal with `MotionKey.Combine`. Source reordering preserves
keys. Mesh replacement changes keys. `MotionKey.None` stays untracked. Keep the caller key stable
while one visual incarnation lives and change it when replacing that incarnation, even if the
replacement uses the same asset bytes.

Binding allocates all submission scratch. Valid frame calls perform no loading, hashing or managed
allocation. One cache belongs to one scene, and its views belong to that scene's render thread.
Calls on the same bound view must not overlap.

For the version-21 set-level rename and its rationale, see the
[skinned attachment design](../docs/design/SKINNED-ATTACHMENT-PARTS-DESIGN-2026-10-15.md).
