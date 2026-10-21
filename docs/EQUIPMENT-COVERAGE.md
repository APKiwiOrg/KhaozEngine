# Equipment coverage consumer guide

Equipment coverage recesses an explicitly authored host patch beneath the actual inward surface of rigid
attached equipment. It retains closed host topology and permits a visible collar contour change. It does
not deform the equipment, erase crossing limbs or resolve equipment against equipment. The implementation
is on the task branch. Hosted correctness is complete. Performance acceptance, final integration, release
and consumer adoption remain separate gates. The [approved design](design/EQUIPMENT-COVERAGE-DESIGN-2026-10-16.md)
and [GPU amendment R](design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md) retain the rationale.

## Author the two sidecars

Use a repeatable exporter from the editable model. Do not infer eligibility from materials, UVs, bone
names, dominant influences or camera direction. Keep exact self-contained host and equipment GLB bytes.
Hashes are lowercase SHA-256. Hash downloaded assets separately from canonical coverage identities.

`HostCoverageMapJson.Read(utf8, identity)` reads strict format 1. Unknown or duplicate members, invalid
ranges, nonfinite numbers and over-limit arrays refuse with `InvalidDataException` naming the asset.
Constructors validate programmatic data as well. `HostCoverageMapJson.Write` writes canonical bytes.

| Host-map member | Authored meaning |
| --- | --- |
| `formatVersion` | Integer 1. |
| `hostSha256` | Exact host GLB byte hash. |
| `topology` | Entries with `mesh`, `primitive`, `vertex`, `surfaceId`. Ordinals are source ordinals, before material grouping. |
| `regions` | Unique ordinal `key`, `triangles` ranges with `role` equal to `core` or `collar`, and `strengths` entries with `surfaceId` and `strength`. |
| `refinement` | Source ranges with fixed `level`. Each range uses `mesh`, `primitive`, `firstTriangle`, `count`. |
| `authoring` | `exporter` and source `revision`, for diagnostics rather than compatibility. |

A core triangle's strengths are all exactly 1. Collar strengths are finite in `[0,1]`. The boundary and
all unselected surface points have strength 0 and remain protected. Positive regions cannot overlap.
Welded material or shading seams must agree in rest position, normalized influences and strength.
Coincident independently deforming surfaces are separate. Fixed refinement skins source vertices first,
then interpolates the posed triangles. Protected neighbors may gain conforming edge splits, but their
piecewise-linear posed surface stays unchanged. Refinement is never adaptive per frame.

Call `GltfLoader.LoadSkinnedPartsWithMaterials(glb, identity, map)` and upload those exact annotated parts.
Keep every material slice and their shared exact `Skeleton`. Upload snapshots the annotated source and
inverse binds. Caller mutation cannot rebind the upload. Passing another mesh, incomplete material list,
another map or a lookalike skeleton does not establish identity.

`EquipmentCoverageJson.Read(utf8, identity)` reads the separate strict format-1 companion in the GPU-free
Attachments package. It does not change attachment JSON v1/v2 or rigid-only canonical format-1 output.

| Equipment-companion member | Authored meaning |
| --- | --- |
| `formatVersion` | Integer 1. |
| `attachmentSha256` | Exact attachment-definition byte hash, including its declared equipment mesh hashes. |
| `hostMapSha256` | `HostCoverageMapJson.SemanticSha256(map)`, not a downloaded sidecar byte hash. |
| `commands` | Unique positive `id`, rigid `partId`, ordinal `hostRegion`, `coverageLocal`, `inwardSupport`, `collarWidth`, `minZ`, `maxZ`, `clearance`, `maxWorldRecession`. |

`coverageLocal` has 16 finite row-major numbers, positive Z outward. `inwardSupport` contains actual
source mesh/primitive/triangle ranges of the displayed rigid part. Its projection must be a single-valued
height field with a simple convex boundary and no holes or overhangs, on the inward face of the complete
opaque closed solid. All material slices of that physical source must participate. A proxy plane, alpha-cut
seal or invented convex-hull fill is unsupported. The collar continues the actual boundary height.
`minZ`, `maxZ` and `collarWidth` are coverage-space values. `clearance` and `maxWorldRecession` are world
distances. Caps must clear the eligible patch, rather than slicing it. Exceeding recession refuses instead
of clamping to an uncleared surface.

Regenerate the host map after **any host GLB change**, including re-export, order, skin or embedded material
changes. Regenerate the attachment definition's mesh hash and companion after any equipment GLB change.
Regenerate the companion when attachment bytes or the host-map semantic identity change. Revalidate the
actual source ranges and run the consumer oracle again. A matching label never substitutes for this work.

## Transforms and bounded capacity

System.Numerics uses row vectors. The rigid draw frame is
`BoneSocket.ComposeRigid(partLocal * contractBodyAlignment, currentJointModel, actorModel)`. It preserves
joint translation while stripping joint scale and shear. Coverage uses
`coverageLocal * equipmentWorld`, and host-to-coverage is `actorModel * inverse(coverageWorld)`.
Use one captured palette/model for host, equipment and outlines. Positive nonuniform scale and affine
shear in local, body-alignment, actor and coverage transforms are supported through the full inverse.
Actor, effective rigid and coverage transforms must be finite,
affine, invertible, positive-determinant and well-conditioned. Projective transforms, mirrors, singular or
ill-conditioned frames refuse `UnsupportedTransform`. Coverage also admits the conservative current-palette
bound against the latched render origin. Excessive origin magnitude refuses with that same code.

| Limit | Capacity |
| --- | ---: |
| Host regions | 32 |
| Active disjoint commands across the actor | 4 |
| Inward support triangles per command | 1,024 |
| Support boundary edges per command | 256 |
| Fixed refinement level | 1 to 4 |
| Refined host vertices and triangles, each | 65,536 |

Source and refinement imports enforce their bounds before allocation. Runtime capacity refusals carry
`Count` and `Limit` where applicable. Larger limits require new geometry, memory and performance evidence.
Material grouping still determines draw count. Regions do not introduce separate draws.

## Consumer lifetime and complete example

[EquipmentCoverageConsumer.cs](examples/EquipmentCoverageConsumer.cs) is the complete consumer example.
The existing Render test project compiles that exact file and exercises covered submission, explicit
uncovered refusal and a legacy custom adapter with no coverage methods. It shows strict sidecar loading,
exact annotated host upload, shared cache and actor binding lifetime, transactional equip, `TryPrepare`,
host tokens, borrowed equipment descriptors, covered outlines and disposal. It uses public APIs only.

Create one `AttachmentSetCache` per scene/resource owner and asset source. Share it across actors in a
production asset owner. Each actor keeps its own `HostCoverageBinding`, stable nonzero incarnation key and
complete uploaded host handle list. The example owns its cache to make disposal self-contained.
`GetCoverage` verifies exact companion and attachment bytes against the cache's already verified displayed
meshes. It compiles supports without another mesh read or upload.

`TrySetEquipment` validates the complete replacement and reserves supports before publishing it. Refusal
preserves the active list and reservation. Instance IDs are positive, unique and stable for the equipped
incarnation. Commands remain distinct across combined equipment. An empty replacement unequips everything.
Mixed garments also require the bound host inverse-bind snapshot to exactly match the actual uploaded skin.
`CoveredAttachmentInstance.TransitionWeight` is finite in `[0,1]`, default 1. Partial weights permit partial
recession and do not claim full core clearance. Independent equipment fading changes that weight or disables
coverage. Whole-actor dissolve retains the shared final surface and ordinary dissolve rules.

After `Scene3D.Begin`, call `TryPrepare` once per actor and view. It queues nothing. On success, put the token
on every host `SkinnedInstanceDraw.Coverage`, including descriptor outlines, with that same actor/model/palette.
Forward every `PreparedEquipmentDraws` descriptor unchanged, using its captured garment palette if skinned.
Consume borrowed memory before another preparation, equipment change or disposal. Do not call the bound
set's ordinary `Queue` on a successful covered frame. Tokens expire at the next `Begin`, unload or disposal.

On preparation refusal the token and prepared draws are empty. Explicitly submit the uncovered current host
pose and the ordinary active equipment. Do not reuse a prior token or silently hide the host. Finish all
actor submissions before `PrepareFrame(width, height)` outside recording. `Render3DSurface` records with
`Frame.Submission`. A new view requires a new `Begin` and preparation.

Dispose actor bindings before caches and scenes. Bindings retire actor scratch, while the cache owns shared
supports and equipment uploads. Borrowed bound sets and companions become invalid on cache disposal.
The example unloads its host handles and surface maps too. Submitted resources remain retained until their
last GPU use completes. Scene and host teardown follow the ordinary retirement contract.

## CPU, GPU and offline routes

The CPU runtime performs identity/capacity admission, command-frame composition, pinned palette composition
and conservative bounds/origin admission. It constructs no covered vertices and runs no exact runtime
certifier. The GPU skins, refines, conforms, corrects core faces, applies transition weight, computes normals
and selects one final surface. Colour, depth, motion, full/visible outline, directional cascades and point
faces all read that selection. A GPU numerical refusal writes the uncovered current pose into the selected
buffer, so drawing never waits for an outcome readback. Covered actors use this route independently of
`UseGpuSkinning`. Ordinary uncovered actors retain their chosen CPU or vertex-shader skinning route.
If compute is unavailable or kernel creation fails, requested hosts use ordinary vertex-shader skinning
regardless of `UseGpuSkinning`, with one capability report per scene.

`GetCoverageOutcome(token)` reports pending, CPU fallback or `Dispatched`. Dispatch is not proof of coverage.
`TryReadCoverageOutcome` later yields `CoverageGpuOutcome(FrameId, ViewId, Actor, Outcome)` with completed
`Covered` or `UncoveredFallback` and command attribution. Full diagnostic rings skip a new copy without
changing drawing or history. No result poll waits or flushes.

The headless `CoverageOracle` runs the pinned CPU reference plus exact geometry certification over the
consumer's declared sampled pose domain. It grants no draw permission and proves no unsampled blends.
The consumer owns its real sampler/grid and blocking CI. `ConfigureCoverageVerification(actor, everyNFrames)`
is an optional asynchronous device/reference comparison, disabled by default. It checks binary32 construction
conformance on a real device. It supplies neither a geometric certificate nor a performance result.

## Refusals and completion ownership

| `CoverageFailureCode` | Meaning |
| --- | --- |
| `None` | Success. |
| `UnknownRegion` | Command does not name an authored host region. |
| `IdentityMismatch` | Invalid/changed actor, equipment, command, upload, source, palette or binding identity. |
| `UnsupportedSupport` | Unsupported support geometry or scalar domain. |
| `UnsupportedTransform` | Unsupported frame, transition scalar or origin magnitude. |
| `CoreEscaped`, `CapContact`, `RecessionExceeded` | Core/domain, cap or displacement-budget guard refused. |
| `FoldedPatch`, `EquipmentIntersection`, `HostIntersection` | Construction or offline geometry predicate refused. Exact intersection certification is offline. |
| `OverlappingConformers` | Positive regions overlap. |
| `CapacityExceeded` | An admitted bounded capacity was exceeded. |
| `StaleFrame` | Staging/token lifecycle or frozen frame mismatch. An unsupported cast fallback value still returns this code pending API policy review. |
| `UnsupportedAdapter` | Adapter/resource capability or GPU coverage capability unavailable. |

Malformed authoring throws during load rather than returning a frame refusal. `CoverageFailure` carries
host, instance, command and count/limit context. The only supported fallback is
`CoverageFallback.UncoveredCurrentPose`. Do not cast other enum values. HostCoverageBinding rejects them in
its constructor. Low-level unsupported fallback classification remains a separately reported API question.

Custom `IAttachmentScene` implementations remain valid without new members. Requested coverage requires
`IAttachmentCoverageScene`, a stable non-null `CoverageResourceOwner`, complete descriptor forwarding and
support reservation. Wrappers around one Scene3D share its owner. Another scene is incompatible even with
identical handle values. `ReserveCoverageSupports` has a default conformer-forwarding implementation.

`GpuFrameSubmission` owns fixed shared fences and registration storage. Begin it before recording, register
`IGpuFrameSubmissionParticipant` independently of diagnostic slots, retain completion consumers with
`TryRetain`, record copies in that same command list and submit exactly once. Successful retention returns
a non-null fence. Participants publish history only on `Submitted` and discard pending state on `Abandoned`.
If participant registration fails, abandon that participant's pending state explicitly.
`IGpuFrameCompletionConsumer.HasPending` must remain true while any staging entry retains the exact fence,
even after disposal was requested. `DiscardUnsubmitted` releases only never-submitted recordings.
`ReleaseCompleted` is teardown-only after a proven signal or host drain. Callback/query failures quarantine
ambiguous ownership. Fences reset only after signaling and release by all consumers. Dispose the submission
owner before its command list/device. Its teardown drain releases consumers, including disposal-requested
staging. Ordinary frames never wait. See the [full submission contract](USING-KHAOZENGINE.md#shared-frame-submission-and-coverage-outcomes).

Warm paths with reserved capacities and warmed final generations allocate no managed bytes. Cold load,
equip, capacity growth and opt-in verification can allocate. Native synthetic correctness, zero-allocation
proofs and software GPU results do not establish CPU latency or native GPU overhead. No measured throughput
or maximum actor count is promised here. Timing uses the separately approved harness and its recorded
hardware/profile. Do not start measurements from a documentation example.
