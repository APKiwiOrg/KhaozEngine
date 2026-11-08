# KhaozEngine.Attachments.Render3D

Attachment definitions bound to a posed `KhaozEngine.Render3D` skeleton. This renderer arm
references `KhaozEngine.Attachments` and `KhaozEngine.Render3D` and ships in `KhaozEngine.Game3D`.

`AttachmentSetCache.Get` loads rigid, skinned and mixed sets. The map-only `AttachmentSetBinding`
constructor and `LoadedAttachmentSet.Bind(map)` refuse sets containing skinned parts with
`AttachmentBindingDataException` with `AttachmentBindingRefusal.HostMeshRequired` and `PartId`.

Construct `AttachmentSetBinding(definition, map)` once with a `ContractJointMap`. Every rigid part must
name a contract joint in the skin. Unresolved references throw `AttachmentBindingDataException` with
`AttachmentBindingRefusal.RigidJointUnresolved`, `PartId`, `JointName` and the original `InnerException`.
The binding retains the exact `Skeleton` instance and resolves node, skin-bone index and body alignment
at construction.

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
The map keeps the alignment's homogeneous component exactly one despite general inverse rounding on x64.

For skinned or mixed definitions, construct `AttachmentSetBinding(definition, map, hostMesh,
skinnedMeshes)`. The dictionary maps each skinned mesh reference to its imported `SkinnedGltfMesh`.
The host mesh must carry the exact `map.Skeleton` instance. Garment skin joints resolve by ordinal
name against host skin bones, so a named node outside the skin cannot bind. A garment may use a
subset of the host and a different skin order. Its skin must contain 1 to 128 joints, with one
inverse bind and rest pose per joint. Every inverse-bind matrix must be finite and match the
corresponding actual host inverse bind with an inclusive absolute tolerance of `1e-4` over all 16
elements. Rest poses are never inverted to infer binds. Missing mesh metadata, unresolved joints or
mismatched binds throw `AttachmentBindingDataException`. `Reason` identifies the rule, and `PartId`,
`MeshKey` and `JointName` identify the garment and joint where applicable. `GarmentBindMismatch` also
carries `BindTolerance`. `GarmentJointCountOutsideRange` carries the actual and allowed joint counts.

`SkinnedJointCount(partId)` gives the destination capacity for a garment, or zero for a rigid part.
Allocate the destination once in the caller. `CopySkinnedPalette(skeleton, hostPalette, partId,
destination)` copies host joint model matrices into garment skin order. It does not multiply inverse
binds. The renderer alone applies the garment's inverse binds. The binding snapshots index tables
and owns no garment frame palettes. Later source-skeleton changes cannot reorder its copies.
An unknown part, a rigid palette-copy request, a foreign skeleton, a palette whose length differs
from the full host skin, insufficient destination capacity or invalid referenced joint matrices
throws `AttachmentBindingArgumentException` before writing. Its `Reason` and identifiers name the rule.
The unused destination tail remains untouched. Valid copies allocate no managed memory and perform no
name searches.

`ComposeWorlds` still writes every canonical part. Rigid parts use the composition above, and
skinned parts receive the unchanged character model matrix.

Another skeleton instance, a palette whose length differs from the skin, insufficient output
space or invalid pose matrices throws `AttachmentBindingArgumentException`. Matrices must be finite, affine and
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

GLB preflight propagates `AttachmentGlbContainerDataException`, `AttachmentAccessorDataException`,
`AttachmentHierarchyDataException`, `AttachmentRigidGeometryDataException` and
`AttachmentSkinnedGeometryDataException` from `Get`, including their `Reason`, `SourceName` and identifiers.
Catch `MalformedDataException` for these families. The [GLB refusal tables](../KhaozEngine.Attachments/README.md#glb-refusals)
list their members. Definition JSON admission propagates `AttachmentSetDataException` with
`AttachmentSetRefusal`, `SourceName` and its identifiers. It also derives from `MalformedDataException`.
The [set refusal table](../KhaozEngine.Attachments/README.md#set-refusals) lists its members.
Binding data refusals also derive from `MalformedDataException`. Catch that base for malformed data
from either `AttachmentSetBinding` constructor or either `LoadedAttachmentSet.Bind` overload.
Argument refusals retain `ArgumentException` as their base and preserve `ParamName`.

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
throws `AttachmentSubmissionStateException` (an `InvalidOperationException`) with
`AttachmentSubmissionRefusal.MixedDrawRequired` and `SkinnedPartCount` before writing anything.
An insufficient buffer throws `AttachmentSubmissionArgumentException` (an `ArgumentException`) with
`AttachmentSubmissionRefusal.OutputTooSmall`, `ActualCapacity`, `RequiredCapacity` and `ParamName`.
A shadow-only template without casting throws the shared Render3D `DrawAdmissionArgumentException`
with `DrawAdmissionRefusal.ShadowOnlyWithoutCasting`, `ShadowOnly`, `CastsShadows` and `ParamName`.
An invalid pose or template throws before any descriptor is written, and
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

## Table coverage preparation

`IAttachmentCoverageScene` is an optional capability beside `IAttachmentScene`.
`Scene3DAttachmentScene` supplies it through its actual Scene3D resource owner. Ordinary attachment
adapters remain usable for ordinary equipment and empty coverage configurations.

Load the exact companion with `cache.GetCoverage(loaded, key, sha256)`. The returned
`LoadedEquipmentCoverage` borrows its cache, retains the exact `CompanionSha256`, and compiles support
from the cache's verified displayed mesh bytes without another upload. Companion load errors retain
the public equipment or geometry refusal family. Coverage requires rigid commanded parts and every
material slice of their complete displayed solid. Uncommanded skinned parts retain the existing
host inverse-bind and garment palette checks.

Create one `HostCoverageBinding(scene, skeleton, hosts, actorKey, table)` per actor incarnation.
The host handles must be issued by that exact scene and include every annotated material slice.
The host, semantic map, fixed refinement and arithmetic digests must match the table.
`TrySetEquipment` accepts a complete span of `CoveredAttachmentInstance` values. It verifies exact
cache ownership, host skin, regions, unique authored command IDs and the canonical complete
configuration digest. A changed incarnation or transition weight can reuse the table. A changed
attachment, companion or static binding cannot. Refusal preserves active equipment and borrowed
descriptors. Empty equipment bypasses the table identity check and selects ordinary presentation.

`TryPrepare(skeleton, palette, model, stateKey, equipmentTemplate, out frame, out failure)` freezes
the raw host palette, model, current command frames and complete material descriptors. It uses only
the opaque consumer key to select a row. It never chooses an entry from pose geometry or performs
runtime certification. Forward `PreparedEquipmentDraws` synchronously and unchanged. The borrowed
descriptors and tokens expire on the next accepted preparation, successful replacement, Begin, resource
unload or disposal. Prepared garment palettes are isolated from another actor using the same loaded set.
Each actor accepts one preparation between Begin calls. A repeated preparation returns `StaleFrame`
before changing the accepted preparation, its borrowed descriptors, tokens or equipment participation.
Unloading any registered host slice expires host and equipment tokens for both drawing and outlining.

A missing or explicit-hole key returns `TableHole`, an empty token and no equipment descriptors.
The caller explicitly submits the current uncovered host with `Motion = actorKey` and ordinary
active equipment. Empty equipment succeeds with the same empty token. The scene registers actor
and exact host handles even for fallback, so later submission history can associate that raw pose.
Staging and registration alone never record a presented surface or commit history.

`SkinnedHostCoverage` is the lower-level actor owner. `BindActor` fixes its actor incarnation,
`IsPreparedThisFrame` reads the existing accepted-frame state without reserving another frame slot,
`MatchesConfiguration` checks the canonical configuration, and `ReserveEquipment` reserves bounded
material snapshot storage at cold equip. Frame preparation calls the retained carry uniform
interface with the exact palette, inverse binds, model and commands. Descriptor-aware outline
overloads preserve the same tokens. A changed or stale descriptor throws `CoverageFrameStateException`
with public `CoverageFailureCode Reason` before modifying queues or palettes.

## Binding refusals

`AttachmentBindingRefusal` spans `AttachmentBindingDataException` for malformed binding data and
`AttachmentBindingArgumentException` for frame or access arguments. Data refusals derive from
`MalformedDataException`, replacing the former sealed `InvalidDataException` surface. Messages remain
developer prose. Rigid frame errors carry `PoseCompositionFailed` with the original error as
`InnerException`. A binding joint or world validation error there retains its own structured reason.

| Reason | Refused input or access | Identifiers |
|---|---|---|
| `HostMeshRequired` | map-only binding for a set containing skinned parts | `PartId` |
| `RigidJointUnresolved` | a rigid part whose joint cannot bind to the contract skin | `PartId`, `JointName`, `InnerException` |
| `PartHasNoGarmentPalette` | a garment-palette request for a rigid part | `PartId`, `ParamName` |
| `WorldOutputTooSmall` | output shorter than the attachment-part count | `ActualCapacity`, `RequiredCapacity`, `ParamName` |
| `ModelTransformInvalid` | a non-finite non-affine or singular model transform | `ParamName` |
| `JointPoseInvalid` | an invalid rigid joint pose before publishing any output | `PartId`, `JointName`, `ParamName` |
| `WorldTransformInvalid` | an invalid composed rigid world transform | `PartId`, `JointName`, `ParamName` |
| `PoseCompositionFailed` | rigid pose composition rejected by the socket validator | `PartId`, `JointName`, `InnerException`, `ParamName` |
| `PartMissing` | a part id absent from this binding | `PartId`, `ParamName` |
| `SkeletonMismatch` | frame data using a different skeleton instance | `ParamName` |
| `HostPaletteLengthMismatch` | a host palette with the wrong bone count | `ActualCount`, `RequiredCount`, `ParamName` |
| `GarmentBindCountsMismatch` | garment bind and rest counts not matching its joint table | `PartId`, `MeshKey`, `JointCount`, `InverseBindCount`, `RestCount` |
| `GarmentJointUnresolved` | a garment joint that cannot bind to the host skin | `PartId`, `MeshKey`, `JointName`, `InnerException` |
| `GarmentBindMismatch` | non-finite garment binds or binds outside the host tolerance | `PartId`, `MeshKey`, `JointName`, `BindTolerance` |
| `HostBindCountsMismatch` | a host without one bind and rest pose per skin bone | `JointCount`, `InverseBindCount`, `RestCount` |
| `HostInverseBindNonFinite` | a non-finite host inverse bind | `BoneIndex` |
| `GarmentMetadataMissing` | a garment without captured or supplied skin metadata | `PartId`, `MeshKey` |
| `GarmentSkeletonMissing` | garment metadata without a skeleton | `PartId`, `MeshKey` |
| `GarmentNodeMissing` | a garment joint referring outside its skeleton | `PartId`, `MeshKey`, `BoneIndex`, `NodeIndex`, `NodeCount` |
| `GarmentJointCountOutsideRange` | a garment joint count outside the renderer capacity | `PartId`, `MeshKey`, `JointCount`, `MinimumJointCount`, `MaximumJointCount` |
| `HostSkeletonMismatch` | a host mesh using a different contract skeleton instance | `ParamName` |
| `HostJointPoseInvalid` | an invalid host joint model matrix before palette publication | `PartId`, `BoneIndex`, `ParamName` |
| `GarmentPaletteOutputTooSmall` | output shorter than the garment joint count | `PartId`, `ActualCapacity`, `RequiredCapacity`, `ParamName` |

## Submission refusals

`AttachmentSubmissionArgumentException` derives from `ArgumentException` and keeps `ParamName`.
`AttachmentSubmissionStateException` derives from `InvalidOperationException`. Both carry
`AttachmentSubmissionRefusal`. Pose errors keep their AttachmentBinding reason. Shadow-only
eligibility uses the shared [DrawAdmission family](../KhaozEngine.Render3D/README.md#draw-admission-refusals).

| Reason | Refused input or access | Identifiers |
|---|---|---|
| `OutputTooSmall` | Output shorter than the attachment draw count | `ActualCapacity`, `RequiredCapacity`, `ParamName` |
| `MixedDrawRequired` | Rigid descriptors for a set containing skinned parts | `SkinnedPartCount` |
