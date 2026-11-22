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
The host mesh must carry the exact `map.Skeleton` instance. Garment joints outside declared chains
resolve by ordinal name against host skin bones. A garment may use a subset of the host and a
different skin order. Its skin must contain 1 to 128 joints, including chain joints, with one
inverse bind and rest pose per joint. Each joint that follows the host must have a finite inverse
bind matching the actual host inverse bind within an inclusive absolute tolerance of `1e-4` over
all 16 elements. Missing mesh metadata, unresolved joints or
mismatched binds throw `AttachmentBindingDataException`. `Reason` identifies the rule, and `PartId`,
`MeshKey` and `JointName` identify the garment and joint where applicable. `GarmentBindMismatch` also
carries `BindTolerance`. `GarmentJointCountOutsideRange` carries the actual and allowed joint counts.

`SkinnedJointCount(partId)` gives the destination capacity for a garment, or zero for a rigid part.
Allocate the destination once in the caller. `CopySkinnedPalette(skeleton, hostPalette, partId,
destination)` copies host joints and any chain rest poses into garment skin order. The renderer
alone applies the garment's inverse binds. The binding snapshots index tables
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
binds and rest poses as immutable metadata. At the first successful chained bind, the cache also
captures the parent nodes and local rest transforms from the verified GLB bytes. Loading allocates
no frame palettes.

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
that disposal order. Dispose every `BoundOutfit` before its cache, as [Outfits](#outfits) explains. Cache
disposal releases each unique upload once, meshes before textures, through Scene3D's deferred retirement
APIs, and a completed disposal is idempotent. A cache disposal and a skinned lease disposal refuse with
`SkinnedMeshViewArgumentException` and `SkinnedMeshViewRefusal.SourceHasLiveViews` while a mesh view lives
over any of their handles. The refusal comes first and changes nothing: every handle stays live, coverage
stays loaded and `Get` works as before, so the same call succeeds once the view is gone. Use the cache and
its bindings on their owning render thread, with no overlapping calls.

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
parts receive the unchanged character model and joint model matrices in garment skin order.
Scene3D alone applies inverse binds. The entire pose and template are prepared before
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

## Outfits

An outfit is a body and the sets worn on it, drawn with the outfit rule applied. A set with a layer (set
formats 3 and 4) hides the body regions it covers, and the same regions of every set in a lower layer. The body
draws only its uncovered triangles, a lower piece draws only what no higher piece covers, and a rigid part
whose region is covered draws nothing. A set with no layer (formats 1 and 2) covers nothing, nothing hides
it, and it draws exactly as `BoundAttachmentSet.WriteDraws` draws it. The rule itself is
`OutfitCoverage.Resolve` in the GPU free package.

```csharp
LoadedBodyRegions body = cache.GetBodyRegions(regionMapKey, regionMapHash, bodyRigIdentity,
    bodyHandles, bodyMeshes);
using BoundOutfit outfit = cache.BindOutfit(body, [shirt.Bind(map, bodyMesh), plate.Bind(map, bodyMesh)]);

for (int part = 0; part < outfit.BodyPartCount; part++)
    if (outfit.TryBodyMesh(part, out SkinnedMeshHandle mesh))
        scene.DrawSkinned(new SkinnedInstanceDraw(mesh, characterWorld), bodyPalette);
var draws = new AttachmentDraw[outfit.DrawCount(0)]; // Allocate once after binding.
int count = outfit.WriteDraws(0, map.Skeleton, jointPalette, characterWorld, visualIncarnationKey, template, draws);
outfit.QueueOutline(0, map.Skeleton, jointPalette, characterWorld, outlineGroup);
```

`GetBodyRegions` reads and verifies a body region map like any cached asset and keeps the parsed map. Map
part `i` tags `bodyHandles[i]`, uploaded from `bodyMeshes[i]`, the body GLB's skinned material parts in
order. The caller supplies the body's `rig-v1:` identity, because it is computed from the body's GLB bytes
and cannot be recovered from uploaded meshes. The body's handles stay the caller's.

`BindOutfit` works everything out when the outfit changes and nothing per frame. It checks every worn set,
resolves the rule, and then builds each view it needs. A refused view releases the ones built before it.
`TryBodyMesh` returns the body part's own handle when nothing covers it, a view of its uncovered triangles
when something does, and false when every triangle is covered, so a pass never draws an empty view.
`WriteDraws` and `QueueOutline` take a worn index into the list given to `BindOutfit` and keep the set's
part-ID and material order, motion keys and template fields. `DrawCount(wornIndex)` is the set's draw count
less its covered rigid parts and fully covered material groups. Frame calls only read, and valid calls
allocate no managed memory.

A skinned part's region runs cover its triangles in the mesh's material order, then each material group's
triangle order. Each material group is its own skinned handle, so a run that crosses from one group into the
next splits at the boundary, and each handle gets its own view of its visible triangles. `BindOutfit` reads
each tagged mesh's index lists when it binds, borrowed from the renderer for the shipped adapters, so a cache
that never binds an outfit keeps no extra copy. A custom scene's lease cannot lend them, so the geometry of its
meshes is decoded again from the verified bytes the cache keeps, with no material map, once per bind.

Hidden triangles are cut through skinned mesh views (see the
[regions and layers design](../docs/design/ATTACHMENT-REGIONS-AND-LAYERS-DESIGN.md#skinned-mesh-views)).
A view shares its source's vertices and materials and owns a filtered index buffer, so the colour, motion,
shadow and outline passes all draw the same triangles. This needs the optional `IAttachmentViewScene`
capability beside `IAttachmentScene`. `Scene3DAttachmentScene` and `Scene3DTileWorldScene` supply it.
`BindOutfit` over any other scene refuses with `OutfitViewsUnsupported`. A view is never made over a
coverage host, because coverage tables index the host's own triangles, so binding an outfit that partly
covers a body uploaded with host coverage evidence refuses with `SkinnedMeshViewArgumentException` and
`SkinnedMeshViewRefusal.SourceHasCoverage`.

Views are shared. Two outfits that hide the same triangles of one mesh draw through one view, keyed by the
source handle and the exact triangles it keeps. A view is counted by user and released once, when the last
outfit using it is disposed. The renderer owns frame lifetime: a released view keeps drawing until the frame
ends and is freed at the next frame boundary, so an outfit can change in the middle of a frame.

Dispose order matters, because an outfit's views draw from the cache's skinned meshes and from the body's.
Dispose the outfit first. `AttachmentSetCache.Dispose`, a skinned attachment lease and the body's own
`UnloadSkinnedMesh` each refuse with `SkinnedMeshViewArgumentException` and
`SkinnedMeshViewRefusal.SourceHasLiveViews` while a view lives over one of their meshes, before changing
anything, and succeed once the outfit is gone. Outfit disposal is idempotent. It gives back every view even
when the scene refuses one, for example a view the caller unloaded itself, and then rethrows the first
refusal. A refused view is forgotten and never handed to another outfit.

## Hanging chains and wearer motion

Bind a format 4 skinned part with chains through `loaded.Bind(map, hostMesh)`. The first chain
joint's garment parent must name a host skin joint, the anchor. Each later chain joint must be a
direct child of the preceding one. Chain joints belong to the garment skin and have names distinct
from the host's skin joints. Each chain joint's inverse bind must match the inverse of its rest
transform composed on the host anchor's bind frame, within the same per-element tolerance. Binding
validates the chain before allocating draw scratch. Ordinary draws pose each chain at its authored
rest hang on the current anchor, so they work as soon as a chained set binds.

Load a body's collider file with `cache.GetBodyColliders(key, expectedSha256, bodyRigIdentity, hostMesh)`,
using the same host mesh as the binding and the full rig identity from its body GLB. The mesh must
carry a skeleton. The cache verifies the exact bytes and returns the same `LoadedBodyColliders` for
the same key, SHA-256 and skeleton instance. `Set` gives the verified `BodyColliderSet` while its cache
is alive. A bound set or outfit refuses colliders loaded for another skeleton instance with
`BodyCollidersMismatch`. An outfit also compares the file's rig with its body region map.

Call `outfit.CreateMotion(bodyColliders, options)` or `bound.CreateMotion(bodyColliders, options)`
once per wearer. The motion combines the visible chains on their distinct host anchors. It uses
capsules from the body file and all worn sets, with a limit of 64 per wearer. Passing null for body
colliders still includes worn set capsules. For each step, the host inverse bind, current joint
matrix and model compose the rest-to-world transform. It places the endpoints and scales the radius
by its largest axis scale. The bind pose preserves the authored radius before wearer scaling.
Collider motion needs a set bound with the explicit host mesh, even if its parts are rigid.
An outfit uses the inverse binds already held by
its first such worn set. A collider index names its position in the normalized source list.

Animate the host, call `motion.Step(skeleton, palette, model, deltaSeconds)` once with its current
pose, then draw and outline as often as needed. Pass the motion first to the set's mixed
`WriteDraws`, `Queue` or `QueueOutline`, or after `wornIndex` to the outfit's `WriteDraws` or
`QueueOutline`. These calls only read the motion. If a frame skips the step, each chain keeps its
latest shape and follows its current anchor. Overloads without motion draw the rest hang.

`Step` uses the binding's exact skeleton and full host skin palette. It validates the model and
the joint matrices it reads for anchors and capsules. Each draw validates the matrices it reads.
Valid steps and draws allocate nothing once warm. Use `MotionLevels.Assign` to select simulated wearers
by distance and count and pass each level to `Step`. `MotionLevel.Held` blends toward rest. `Reset()`
restores the rest hang immediately and clears velocity on the next valid step. `Tune(wornIndex, partId, chain, settings)`
changes settings on the next substep. Worn and chain indices are zero-based and a bare set uses
worn index zero. A fully hidden part has no tunable chains. Wearers can share a binding and have
independent motions. Each motion belongs to its originating set or outfit and owns no disposable
scene state. Passing it to another binding raises `MotionMismatch` before submitting any draw.

When the cache loads a chained set through a shipped `Scene3D` upload lease, it expands the
chained meshes' rest bounds for culling. Each chain contributes a sphere at its first joint's
rest position. The radius covers the rest segments and tip, plus the farthest weighted vertex
from its chain joint. This growth covers the chain's swing around the anchor. An anchor animated
far from rest by translation or scale can take the whole chain outside the culling bounds.
Ordinary skinned meshes share this limit
([#1492](https://github.com/APKiwiOrg/KhaozEngine/issues/1492) and
[#1538](https://github.com/APKiwiOrg/KhaozEngine/issues/1538)).
A shared mesh keeps the union of every loaded definition's reach. Existing and future views use
the grown source bounds. A custom `IAttachmentScene` lease keeps its own culling policy.

## Table coverage preparation

See the living [equipment coverage guide](../docs/EQUIPMENT-COVERAGE.md) and its
[compiled consumer example](../docs/examples/EquipmentCoverageConsumer.cs) for complete submission,
explicit uncovered-current-pose fallback and teardown order. The submission owner drains first, then
bindings dispose before their attachment caches and scenes. One scene-owned aggregate participant
commits every actor's history. Native correctness and cost remain separately gated.

`IAttachmentCoverageScene` is an optional capability beside `IAttachmentScene`.
`Scene3DTileWorldScene` in `KhaozEngine.TileWorld.Render3D` forwards this capability to its existing
attachment delegate. Caches and bindings over the wrapper share the underlying Scene3D resource owner.
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
| `HostSkeletonMismatch` | a host mesh using a different contract skeleton instance, or a worn set using a different skeleton from the wearer's | `ParamName` for mesh binding, `SourceName` and `WornIndex` for motion creation |
| `HostJointPoseInvalid` | an invalid host joint matrix or composed pose before palette publication or a motion step | `BoneIndex`, `ParamName`, `PartId` for a garment palette |
| `GarmentPaletteOutputTooSmall` | output shorter than the garment joint count | `PartId`, `ActualCapacity`, `RequiredCapacity`, `ParamName` |
| `BodyRegionMapMismatch` | a body region map for another rig, another part count or other part triangle counts | `SourceName` and `ExpectedRigIdentity` with `ActualRigIdentity`, or `ExpectedPartCount` with `ActualPartCount`, or `BodyPartIndex` with `ExpectedTriangleCount` and `ActualTriangleCount` |
| `PartRegionTagsMismatch` | a skinned part whose region runs do not sum to its mesh's triangles | `SourceName`, `PartId`, `MeshKey`, `ExpectedTriangleCount`, `ActualTriangleCount` |
| `OutfitRegionUnknown` | a worn set naming a region the body's region map does not declare | `SourceName`, `WornIndex`, `PartId`, `RunIndex`, `RegionName`, `InnerException` |
| `OutfitViewsUnsupported` | an outfit over a scene that is not an `IAttachmentViewScene` | none |
| `ChainJointMissing` | a declared chain joint absent from the garment skin | `PartId`, `MeshKey`, `ChainIndex`, `JointName` |
| `ChainJointIsHostJoint` | a chain joint whose name resolves to a host skin joint | `PartId`, `MeshKey`, `ChainIndex`, `JointName` |
| `ChainNotParentLine` | a chain joint that is not a direct child of its predecessor | `PartId`, `MeshKey`, `ChainIndex`, `JointName` |
| `ChainAnchorNotHostJoint` | a first chain joint whose parent does not name a host skin joint | `PartId`, `MeshKey`, `ChainIndex`, `JointName` (the parent name when present, otherwise the first chain joint) |
| `ChainBindMismatch` | a chain joint whose inverse bind does not match its rest frame on the host anchor | `PartId`, `MeshKey`, `ChainIndex`, `JointName`, `BindTolerance` |
| `BodyCollidersMismatch` | body colliders for another rig identity or skeleton instance | `SourceName`, `ActualRigIdentity`, `ExpectedRigIdentity` when loading or creating outfit motion |
| `ColliderJointUnresolved` | a collider whose name is absent from the host skin | `SourceName`, `ColliderIndex`, `JointName` |
| `WearerCollidersTooMany` | more than 64 colliders across the body and worn sets | `ColliderCount`, `MaximumColliderCount` |
| `ColliderHostMeshRequired` | colliders whose motion binding has no host inverse binds | `SourceName` |

## Submission refusals

`AttachmentSubmissionArgumentException` derives from `ArgumentException` and keeps `ParamName`.
`AttachmentSubmissionStateException` derives from `InvalidOperationException`. Both carry
`AttachmentSubmissionRefusal`. Pose errors keep their AttachmentBinding reason. Shadow-only
eligibility uses the shared [DrawAdmission family](../KhaozEngine.Render3D/README.md#draw-admission-refusals).

| Reason | Refused input or access | Identifiers |
|---|---|---|
| `OutputTooSmall` | Output shorter than the attachment draw count | `ActualCapacity`, `RequiredCapacity`, `ParamName` |
| `MixedDrawRequired` | Rigid descriptors for a set containing skinned parts | `SkinnedPartCount` |
| `MotionMismatch` | Motion created by another bound set or outfit | `ParamName` |
