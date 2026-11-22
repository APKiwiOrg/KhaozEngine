# Attachment regions and layers

Status: implemented for 24.4.0 under [#1874](https://github.com/APKiwiOrg/KhaozEngine/issues/1874): skinned mesh
views, body region maps, set format 3, the outfit rule, outfit drawing and the offline coverage claim check. Current
use is in the [consumer guide](../USING-KHAOZENGINE.md#regions-layers-and-outfits) and the package READMEs. This note
keeps the decisions and the reasons for them.

A worn piece declares a layer and the body regions it covers. The engine hides covered body triangles and covered
triangles of lower layer pieces in every render pass, so skin never shows through a garment and an undershirt never
pokes through a breastplate. The hidden set is worked out when an outfit changes, never per frame.

## Decisions, checked against the renderer

### 1. Hiding uses mesh views

Every skinned pass draws a whole entry's index buffer. The draw records `GpuSkinnedDraw` and `CpuSkinnedDraw` copy
`Ib`, `IndexCount` and `IndexFormat` from the `SkinnedMeshEntry` when `PrepareSkinnedDraws` classifies a queued draw,
and the passes issue one indexed draw from that record:

| Pass | GPU skinning | CPU skinning |
| --- | --- | --- |
| Colour and motion | `ModelRenderer.DrawGpuSkinned` | `ModelRenderer.DrawCpuSkinned` |
| Cascade shadow | `ShadowMapRenderer.DrawGpuSkinnedCaster` | `ShadowMapRenderer.DrawSkinnedCaster` |
| Point shadow | `PointShadowRenderer.DrawGpuSkinnedCaster` | `PointShadowRenderer.DrawCpuSkinnedCaster` |
| Target outline | `TargetOutlineRenderer` queue draw over the rest buffer | the same queue draw over the deformed stream |

Dissolve variants use the same records. A view is therefore a second entry that shares the source's vertex buffer
and material sets and owns a filtered index buffer. Every pass draws exactly the view's triangles with no shader,
pipeline or draw call change. A shader mask would have touched every skinned shader variant and the CPU skinning
path. Range draws would have touched every pass's draw call and broken the one draw per record shape.

### 2. A region set is a 128 bit value

A body carries at most 128 regions. A full body wants about forty, and thin pieces want finer regions under them,
so 64 is too tight. Two 64 bit words keep the set a copyable value with no allocation.

### 3. Region tags are run length lists in triangle order

A body's region map and a skinned part's region tags list `(region, triangleCount)` runs in triangle order. The
fitting step sorts triangles by region, so a part has about one run per region and the list stays small.

### 4. The rule is a pure function in the GPU free package

`KhaozEngine.Attachments` works out what an outfit hides without a renderer, so a tool can explain an outfit offline.

### 5. A fully hidden mesh is skipped by the caller

`CreateSkinnedMeshView` refuses an empty index list. The outfit layer reports a fully hidden body part as having no
mesh to draw, so no pass ever records an empty view.

### 6. A view is released at the end of the frame in which it is unloaded

Queued skinned draws name a handle. `PrepareSkinnedDraws`, the outline pass and the coverage paths resolve that
handle through `_skinnedSlots.IsValid` when the frame is prepared, after the caller has queued everything. An
outfit change in the middle of a frame would otherwise drop draws already queued through the old view. So
`UnloadSkinnedMesh(view)` keeps the view's slot live until the next frame boundary (`Begin` or `BeginFrame`), where
the slot is freed and the index buffer goes to the scene's retire queue. The retire queue then destroys it behind a
fence like every other unload, so no extra GPU wait is added.

### 7. The capability is a minor, 24.4.0

Region maps, format 3, outfits and mesh views add types, members and overloads, and no public interface gains a
member, so the release is additive. The `InternalsVisibleTo` grant from `KhaozEngine.Render3D` to
`KhaozEngine.Attachments.Render3D` is package content but no public surface. See the `IAttachmentScene` correction
below for the one shape that would have made it a major.

## Skinned mesh views

```csharp
SkinnedMeshHandle view = scene.CreateSkinnedMeshView(source, indices);
bool isView = scene.IsSkinnedMeshView(view);
scene.UnloadSkinnedMesh(view);   // released at the next frame boundary
```

A view draws `indices` over the source's vertices with the source's materials, alpha cutoff, bounds and inverse
binds. It is drawn, outlined and shadowed through every existing skinned API exactly like a loaded mesh. The index
buffer uses the narrowest format that fits, as a loaded skinned mesh does. Bounds stay the source's rest bounds,
which is conservative for culling and costs nothing to compute.

`CreateSkinnedMeshView` refuses with `SkinnedMeshViewArgumentException`, an `ArgumentException`, carrying
`SkinnedMeshViewRefusal` and the source handle's index and generation:

| Reason | Rule |
| --- | --- |
| `SourceNotLive` | The source handle is default or stale. |
| `SourceIsView` | The source is itself a view. One level keeps ownership and release order simple. |
| `SourceHasCoverage` | The source carries host coverage evidence. Coverage keys on host handles and its tables index the source's triangles, so a view is never a coverage host. |
| `IndicesEmpty` | No indices. A fully hidden mesh is skipped by the caller. |
| `IndexCountNotTriangles` | The count is not a multiple of three. |
| `IndexOutOfRange` | An index reaches past the source's vertex count. `IndexPosition` and `VertexCount` name it. |
| `SourceHasLiveViews` | `UnloadSkinnedMesh(source)` while a view of it lives. `LiveViews` names the count. |

A refused call creates no entry and no GPU resource.

Unload rules:

- `UnloadSkinnedMesh(view)` keeps the view drawable until the frame ends, then frees it. A second unload of the
  same handle throws like any stale handle.
- `UnloadSkinnedMesh(source)` refuses while a view of it lives. A view already unloaded this frame no longer counts:
  unloading its source releases it at once, and its draws this frame are dropped with the source's own draws,
  exactly as a source unloaded mid frame always drops its queued draws. This lets a despawn release an outfit's
  views and then its body in one frame.
- A refused unload changes nothing it owns, and the same call succeeds once the view is gone.
  `UnloadSharedSkinnedMesh`, `SkinnedLimb.Dispose`, a skinned attachment upload lease and `AttachmentSetCache.Dispose`
  each ask the scene first, through the internal `Scene3D.RefuseSourcesWithLiveViews`, whether a live view draws from
  any handle they own. A refusal therefore comes before the first unload, before the cache invalidates its coverage
  hosts and before any field changes: the shared key stays, the limb stays live, every handle stays live, coverage
  stays loaded, and `Get` and draws work as before. The query is internal, granted to
  `KhaozEngine.Attachments.Render3D`, so the public surface and `IAttachmentScene` gain nothing.
- Past that check each release keeps the order it had before views existed, so a consumer that creates no views
  sees no change for any input. With a handle already freed elsewhere, `UnloadSharedSkinnedMesh` forgets the key and
  then throws, so a later load under the key uploads again. `SkinnedLimb.Dispose`, the lease and the cache mark
  themselves spent and then throw, so they never submit or free the dead handle again.
- A cache over a custom `IAttachmentScene` holds that scene's own leases, which cannot answer the query. Such a cache
  keeps the release behaviour it had before views existed: it marks itself disposed, then releases each lease, and
  whatever a lease's own release does when a view is live is that scene's behaviour. `Scene3DAttachmentScene` and
  `Scene3DTileWorldScene` hand out the renderer's own leases, so a cache over either refuses up front.
- `Dispose` frees every view's index buffer and never a shared resource twice.

`IsSkinnedMeshView` is true while the handle names a live view, including one unloaded during the current frame.
Views occupy skinned slots, so they count toward `LiveSkinnedMeshCount`.

## Per handle side tables a view touches

| Table | What it holds | What it does for a view |
| --- | --- | --- |
| `_skinnedSlots` | Slot generations | The view has its own slot and generation, so stale detection works unchanged. |
| `_skinnedMeshes` | `SkinnedMeshEntry` per slot | Shares `Vb`, the three material sets, the cutoff, inverse binds and bounds. Owns `Ib`, `IndexCount` and `IndexFormat`. `ViewOf` names the source slot. `Source` is a mesh over the source's vertices with the view's indices and no coverage. |
| `_skinnedCpuVerts` | Rest vertices for CPU skinning and CPU motion | The source's array. A CPU skinned view deforms every source vertex, the same cost as drawing the source. |
| `_skinnedMeshMaterials` | Owned textures and parameter buffers per handle | Nothing is recorded for a view, so its release is a no-op. The source owns them. |
| Live view counts | Live views per source slot | Raised on create, lowered on unload. Gates source unload. |
| Pending view releases | Views unloaded this frame | Freed at the next frame boundary, or at once when their source unloads. |
| `CollectLiveMaterialSets` | Receivers rebuilt by a shadow, caustic, interior cut or sampler replacement | Skips views. Their sets belong to the source, and collecting a set twice would rebuild it twice. |
| `CommitMaterialSets` | Rebinds every entry's sets | Each view takes its source's replacement sets after every source is rebuilt. |
| `Dispose` | Releases every entry | A view releases only its index buffer. |
| `_retired` | Deferred GPU destruction | Receives the view's index buffer at release. |
| `_coverageHosts` | Annotated hosts | A coverage source is refused, and a view's `Source` has no coverage, so `CreateHostCoverage` refuses a view as unannotated. |
| Motion history | Previous model and palette per `MotionKey` | Unchanged. A view drawn under the source's key has the same bone count. |
| `_sharedSkinnedMeshes` | Shared asset handles by key | Views are never shared assets. Releasing a shared source with live views refuses and keeps its key. |

The per frame records (`GpuSkinnedDraw`, `CpuSkinnedDraw`, outline parts) copy the entry's buffers and counts, so
the colour, motion, cascade, point and outline passes need no change.

## Package boundary for the coverage claim check

`KhaozEngine.Attachments` may not reference `KhaozEngine.Render3D`. It references only `KhaozEngine.Primitives`, and
`ArchitectureTests.Attachment_definitions_reference_only_the_shared_primitives` pins that edge so publishers and
tools stay GPU free. The coverage claim check takes plain positions, indices, a part ID and a vertex mask.

## Signatures corrected against the code

Each correction keeps the planned behaviour and changes only the shape the code requires.

Mesh views:

- The refusal type is a new family, `SkinnedMeshViewArgumentException` with `SkinnedMeshViewRefusal`, because each
  refusal family owns its reason enum and derives from the type thrown before. `UnloadSkinnedMesh` threw
  `ArgumentException` for a bad handle, so the source refusal derives from it as well.
- A view unloaded this frame does not hold its source. Counting it would force every despawn to wait a frame
  between releasing an outfit and unloading the body.
- "The end of the frame" is the next `Begin` or `BeginFrame`, because draw queues live until then.
- The coverage pass observer sees only coverage host draws, so the view tests read indexed draws through
  `RecordingGpuCommandList` and tell the passes apart by their pipelines.

Body region maps: no change.

Set format 3:

- `AttachmentSetJson.CurrentFormatVersion` stays 2 and `RegionsFormatVersion = 3` is added. A public constant is
  compiled into consumers, so changing its value would silently change what their code means.
- `RigidAttachmentPartDefinition.Region` and `SkinnedAttachmentPartDefinition.Regions` are init only properties with
  empty defaults, not positional parameters. Both are positional records, and a new positional parameter would
  break their constructors and deconstruction for every consumer.
- `AttachmentSetDefinition` gains a constructor overload taking the layer and the covered regions. The existing
  constructor keeps building a set with no layer.

Outfit rule: no change.

Drawing an outfit:

- `IAttachmentScene` does not gain members. A new interface member breaks every implementer, including
  `Scene3DTileWorldScene` and consumer adapters, which would make this release a major. A new optional capability
  `IAttachmentViewScene : IAttachmentScene` carries `CreateSkinnedView` and `ReleaseSkinnedView`, implemented by
  `Scene3DAttachmentScene` and `Scene3DTileWorldScene`, as `IAttachmentCoverageScene` already does for coverage.
  `BindOutfit` over a scene without it refuses with a new `AttachmentBindingRefusal.OutfitViewsUnsupported`,
  appended after the three planned members.
- `GetBodyRegions` takes the body's rig identity as a string and compares it with the map's `rig`. The identity is
  computed from GLB bytes and cannot be recomputed from uploaded meshes. Triangle counts are checked against the
  meshes.
- The renderer owns frame lifetime. `ReleaseSkinnedView` on the Scene3D adapter forwards to `UnloadSkinnedMesh`, and
  the outfit layer releases a view when its last outfit is disposed.
- View sharing is asserted through the handles two outfits return and the scene's live skinned mesh count, not
  through call counts on a fake, which the test rules forbid.

Coverage claim check:

- `CoverageClaimCheck.Verify` takes `IReadOnlyList<CoverageClaimMesh>` for the body and the piece, where
  `CoverageClaimMesh` takes `(ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices)` in its constructor.
  Its init-only `PartId` maps each mesh to a definition part when a piece declares motion. Its `Swinging` mask is
  required for a part with motion, as described in
  [Attachment motion](ATTACHMENT-MOTION-DESIGN.md#coverage-claims-and-swinging-triangles). Body parts follow the
  region map's part order. Piece meshes are already in the body's rest frame, so a rigid part is placed
  by its joint's rest transform and its local transform before the call. The outward normal comes from each
  triangle's winding, counter clockwise as glTF front faces.

Docs, version and verification: no change.
