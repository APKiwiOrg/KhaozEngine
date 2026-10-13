# KhaozEngine.Attachments.Render3D

Rigid attachment definitions bound to a posed `KhaozEngine.Render3D` skeleton. This renderer arm
references `KhaozEngine.Attachments` and `KhaozEngine.Render3D` and ships in `KhaozEngine.Game3D`.

Construct `RigidAttachmentBinding(definition, map)` once with a `ContractJointMap`. Every part must
name a contract joint in the skin. Unresolved references throw `InvalidDataException` naming the
part and joint. The binding retains the exact `Skeleton` instance and resolves node, skin-bone
index and body alignment at construction.

Scratch storage is allocated at binding construction. Valid `ComposeWorlds` calls allocate no
managed memory. Use each binding on its owning render thread. Calls on the same binding must not
overlap.

```csharp
var binding = new RigidAttachmentBinding(definition, map);
var worlds = new Matrix4x4[binding.PartCount];
binding.ComposeWorlds(map.Skeleton, jointPalette, characterWorld, worlds);
```

Pass the body's current joint-world palette in skin-bone order. The binding does not advance
animation. `ComposeWorlds` writes canonical part-ID order using
`BoneSocket.ComposeRigid(part.Local * map.BodyAlignment(node), palette[bone], characterWorld)`.
Body alignment cancels the joint's base orientation while retaining its translation. Joint scale
and shear are removed, and reflected handedness is preserved.

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

Create one `RigidAttachmentSetCache` per scene with an `IAttachmentAssetSource`. `Get(key, expectedSha256)`
checks the exact definition bytes and every distinct mesh revision, applies embedded rigid GLB policy,
and decodes all new assets before uploading. The cache copies source memory and shares each `(key, hash)`
upload across its sets. A failure releases newly acquired resources and preserves earlier cached assets.

```csharp
using var scene = new Scene3D(device, output);
using var cache = new RigidAttachmentSetCache(scene, new DirectoryAttachmentAssetSource(assetRoot));
LoadedRigidAttachmentSet loaded = cache.Get(definitionKey, definitionHash);
BoundRigidAttachmentSet bound = loaded.Bind(map);
```

`DefinitionSha256` records the exact source bytes. `Definition.SemanticSha256` records canonical content.
Binding resolves every joint before returning a view. `Skeleton` retains the exact bound skeleton and
`DrawCount` counts every material group of every part. Loaded and bound views borrow resources and expose
no disposal operation. Their properties and binding operations throw `ObjectDisposedException` after
cache disposal.

Dispose the cache before its scene and stop using its views first. The declaration order above enforces
that disposal order. Cache disposal is idempotent and releases each unique upload once, meshes before
textures, through Scene3D's deferred retirement APIs. Use the cache and its bindings on their owning
render thread, with no overlapping calls.

The `IRigidAttachmentScene` overload accepts an optional scene capability for recording or headless
consumers. `Scene3DAttachmentScene` uploads through Scene3D and forwards complete `RigidInstanceDraw`
descriptors. `UploadAttachment(parts)` returns an `IAttachmentMeshLease` whose immutable handles remain
borrowed until lease disposal. The shipped `Scene3DTileWorldScene` also offers this optional capability.
`ITileWorldScene` has no attachment member.
