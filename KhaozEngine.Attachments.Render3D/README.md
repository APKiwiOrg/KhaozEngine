# KhaozEngine.Attachments.Render3D

Rigid attachment definitions bound to a posed `KhaozEngine.Render3D` skeleton. This renderer arm
references `KhaozEngine.Attachments` and `KhaozEngine.Render3D` and ships in `KhaozEngine.Game3D`.

Construct `RigidAttachmentBinding(definition, map)` once with a `ContractJointMap`. Every part must
name a contract joint in the skin. Unresolved references throw `InvalidDataException` naming the
part and joint. The binding retains the exact `Skeleton` instance and resolves node, skin-bone
index and body alignment at construction.

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
