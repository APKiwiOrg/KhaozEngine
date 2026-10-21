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
The map keeps the alignment's homogeneous component exactly one despite general inverse rounding on x64.

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

## Optional equipment coverage

The [living coverage guide](../docs/EQUIPMENT-COVERAGE.md) contains the complete
[compiled consumer example](../docs/examples/EquipmentCoverageConsumer.cs), host/companion authoring,
protected-region semantics, transforms, capacities and refusal codes. Load maps strictly and regenerate
both sidecars after any referenced GLB or attachment definition changes. The adapter does not infer regions.

`AttachmentSetCache.GetCoverage(loaded, key, expectedSha256)` verifies the exact companion bytes and its
attachment-definition byte hash. It resolves rigid part IDs and support ranges against the cache's owned,
verified displayed GLB bytes. It never reads mesh bytes again or uploads another copy. Companions, sources
and immutable compiled supports belong to the cache. Missing or malformed companions, unknown or skinned
target parts and unsupported support geometry throw `InvalidDataException` with asset context. Attachment
JSON v1/v2 is unchanged.

`HostCoverageBinding` owns one actor conformer and borrows equipped sets and coverage from their cache.
Construct it with an `IAttachmentScene`, the exact bound `Skeleton`, every host material handle, a positive
actor `MotionKey` and the required `CoverageFallback.UncoveredCurrentPose`. The host handles must belong
to one complete compatible host coverage source and map. `TrySetEquipment` takes a span of
`CoveredAttachmentInstance(ulong InstanceId, BoundAttachmentSet Set, LoadedEquipmentCoverage Coverage,
float TransitionWeight = 1)`. Instance IDs are positive and unique. Transition weight is finite in `[0,1]`.
The exact set/cache, skeleton instance, scene resource owner, host map semantic hash and named regions
must match. No identity is inferred from labels or matching geometry.

A mixed or skinned set captures one private host inverse-bind snapshot at `Bind` time, shared by all
garment validation in that binding. Covered replacement requires that snapshot to exactly match the
actual uploaded host skin through `SkinnedHostConformer.MatchesHostSkin`. This exact host identity check
is separate from the existing garment-to-host `1e-4` tolerance. Later caller changes to the supplied host
arrays cannot change the evidence or silently rebind the garments. Rigid-only `Bind(map)` remains valid
without a host-skin snapshot.

Equipment replacement is transactional. Every replacement is validated and its scratch acquired before
the active list changes. A refusal retains the previous equipment. An empty replacement unequips all.
Command IDs are distinct across combined equipment, stable while a command remains equipped, and never
reused within the actor binding. Reordering retained equipment preserves those IDs. Re-equipping a
removed incarnation gets fresh command IDs, providing a state-change boundary for later history use.

After `Scene3D.Begin`, call `TryPrepare(skeleton, palette, model, equipmentTemplate, out frame, out failure)`.
It copies the current palette, composes each set once through its existing contract alignment and joint
composition, snapshots every material descriptor and garment palette, and stages each covered rigid part.
Multiple commands for one part register in one atomic batch and share its exact physical source and
complete material snapshot. Each physical material appears once in `PreparedEquipmentDraws`.
No draw is queued by preparation, and valid warm preparation allocates no managed memory.

On success, assign `frame` to every host material's `SkinnedInstanceDraw.Coverage`, use the same captured
palette/model and actor key, and submit the returned equipment descriptors unchanged. Rigid equipment
uses `DrawMesh(in RigidInstanceDraw)` and the descriptor `DrawMeshOutline` overload. Garments use their
returned `BoneMatrices`. Consume the borrowed `PreparedEquipmentDraws` before the next preparation,
state change or disposal. Do not compose another pose through `BoundAttachmentSet.Queue` for this frame.
An empty equipped list succeeds with an empty token and ordinary current-pose host submission.

On refusal, the token is empty and no prepared equipment descriptors are exposed. Present the current
uncovered pose explicitly. Preparation refuses identity, capacity, command-frame and origin-magnitude failures.
`TrySetEquipment` reserves the complete support replacement before publishing its new identity. An acquisition
failure retains the previous equipment and GPU reservation. Staging snapshots inputs without constructing CPU
vertices. Complete material participation and live resources admit GPU dispatch, reported as `Dispatched` by
`GetCoverageOutcome`. Missing participation selects a CPU `UncoveredFallback`. Numerical GPU results arrive
asynchronously and never block drawing. No compute or a kernel-build failure uses ordinary vertex skinning
independently of `UseGpuSkinning`, with one capability report per scene. Finish all submissions before
`PrepareFrame(displayWidth, displayHeight)`, outside the frame recording.
A new `Begin` requires fresh preparation. Failed replacement keeps the active equipment identity.
Host passes pull the GPU-selected final surface. Motion uses the previous actually submitted final surface,
with GPU-selected current-position seeding and local rejection on replacement, refusal and recovery.
A failed replacement keeps the previous equipment identity and its motion correspondence.
The shared submission owner commits history independently of asynchronous diagnostic availability.
Independent equipment fading must change `TransitionWeight` or disable its coverage.
Whole-actor dissolve keeps conformation and ordinary, complementary and inverted-shadow dissolve behavior.

`IAttachmentCoverageScene` is a separate opt-in interface. It adds conformer creation, single-command and
batch equipment staging, cold support reservation, descriptor outlines, and a non-null stable
`CoverageResourceOwner` reference to
the actual scene that owns uploads. `Scene3DAttachmentScene` and `Scene3DTileWorldScene` return their
underlying `Scene3D` and forward opaque descriptors unchanged. Separate wrappers around that same scene
are compatible. Another scene is incompatible even when handle values and geometry match. Existing
`ReserveCoverageSupports` has a default implementation forwarding to the conformer, so existing coverage
adapters remain compatible. `IAttachmentScene` adapters need no new members. Requested coverage through an unsupported adapter
returns `UnsupportedAdapter`, and uncovered submission keeps its existing behavior.

Dispose the actor binding before the cache and scene. Binding disposal retires only its conformer and
leaves shared equipment uploads owned by the cache. Cache disposal invalidates borrowed coverage,
bound sets and their actor bindings. Both disposals are idempotent. Pending history is discarded on abandonment.
Submitted GPU history and cold migration sources retain their storage until their final use completes.

For the version-21 set-level rename and its rationale, see the
[skinned attachment design](../docs/design/SKINNED-ATTACHMENT-PARTS-DESIGN-2026-10-15.md).
