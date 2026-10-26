# KhaozEngine.MapDoc.Render3D

Optional native MapDoc rendering contracts. Reference this package explicitly. It is absent from every
umbrella and has exactly three direct engine edges, MapDoc, Render3D and Terrain.Render3D. It adds no
direct backend, editor, TileWorld or MapDoc.Physics dependency. Render3D already carries the three native
backends through Windowing. Terrain.Render3D carries the neutral Physics seam.

`MapRenderExtentValidator.Validate` streams verified geometry and checks every declared mesh tier and
typed light reach. Its `MapAssetExtentProofSet` retains bounds, source units and resource provenance only.
The encoded `MapAssetClosure` remains separate from decoded residency.

`MapPreparedInputLease.LoadSelected` loads selected mesh tiers and required native material dependencies
under one `GltfDecodeBudget`. Inner decoder reservations retain their named demand and allowance in
asset and prepared-input results. Residency maps a decoder peak refusal back to the scene, plan or CPU
allowance that bounded it. Retain the input for each accepted or pending plan and dispose each lease.
Only the last lease releases decoded ownership. `MapRenderAssets` is independently disposable and owns
mesh tiers only. Its exposed meshes and images are detached copies. Do not dispose a lease's Assets
separately. Standalone `MapRenderMaterials.Load` results implement `IDisposable` and release their charge
when disposed. Material objects and collection storage remain charged even with no texture maps.
Prefer the combined input lease for scene preparation.

Native render materials use the embedded closed `map-render-material.schema.json` contract. Every member
is required, including nullable texture resource IDs. Textures name verified Material dependencies.
Numeric terrain IDs need explicit caller mappings. Prop material authority stays in the GLB.

The strict glTF and bounded image APIs are opt-in. They admit model, expanded geometry, image and copy
peaks before decoding and preserve typed capacity refusals. Bounded images currently support PNG and
JPEG. Strict multipart loading supports core base-colour, normal and metallic-roughness textures.
Invalid primitive accessor references return a typed invalid-input refusal before model decoding.
Emissive/occlusion textures and nonempty material extensions refuse before model loading because the
returned maps cannot represent them. Compatibility glTF and flattened native loading keep their
released lenient behavior.

Public `MapPreparedResource` registrations, including `MapRenderResidency.Record` sink registrations, carry
detached immutable payloads. Internal plan recordings borrow generated foliage columns through live owners
instead of copying per-row transforms, ranks or identities.
The built-in `MapScene3DSink` frame path validates published references internally without receiving payloads
or allocating snapshots. Caller-owned public sinks receive detached foliage.
Public cover and identity snapshots remain detached and usable after batch disposal. Nonuniform imported sample
keys retain a complete charged key array, including each original policy and distribution version.
`MapDrawRecord` carries explicit
anchor-local transform and focus, channels and exactly one typed frame. `MapDrawRecorder` records these
without a device. Frozen water and foliage policies capture the caller's mutable objects. Foliage model
profiles and casting resolve into registration. Per-frame draw options exclude those stable values.
Each foliage instance owner hashes only its own rows and resolved model bindings. Overlapping windows share
one upload and submission for a complete generated chunk or explicit plant. Window stamps and batch witnesses
remain alias provenance, so changing another group does not replace unchanged instance owners.

Generated cover views retain range bounds over 128 rows and project their original instance values. Registration
binds immutable render policy separately from those columns. Full diagnostic samples and explicit plants retain
their public behavior. The latter keep precise anchor-local matrices and bypass generated density thinning.
Upload measurement borrows rows synchronously. After the full upload reservation is granted, `BeginUpload`
detaches transform, rank and model-index inputs. Renderer snapshots keep their existing F1 settlement and retirement
ownership independently of preparation columns.

`NativeMapTerrain.Prepare(scene, input, materialIds)` consumes the scene's acquired canonical faces and
effective paint regions. `MapTerrainRenderBatch.Register` registers typed `MapTerrainResource` payloads.
`Record` writes explicit rigid defaults and focus in each resource's anchor space. Keep the batch alive
while consuming its registrations. It retains the prepared input independently of the original caller.
`RetainedBytes` covers geometry and provenance. `InputRetainedBytes` reports the shared decoded charge,
and `PreparationChargeBytes` reports the retained owned receipt after preparation workspace is released.

Terrain uses Render3D's ground pipeline, without a TileWorld dependency. Its ordered material sets map
numeric IDs to albedo layers. Vertex Color carries four underlay weights, Uv.xy and Tangent.xy carry their
slots, Tangent.z is one, and Tangent.w names the optional overlay slot plus one. The overlay takes the
remaining weight. Feather tessellation stays on the canonical face, with a 0.2 m inward smoothstep band.
Its world-space refinement demand is admitted before render tessellation. Faces that cannot retain that
sampling density within the captured scene allowance refuse with `CapacityExceeded`.
Canonical geometry never changes with observer distance. Ceilings retain inward normals and wall sides
retain their compiled winding and normals. Colour and shadows consume the same geometry.

Material zero has no invented layer. Missing selected materials refuse before registration. Sets split
deterministically at `TileGroundMaterialConfig.MaxMaterials`, keeping every canonical face's materials
together. A face that cannot fit refuses with `CapacityExceeded`. Albedo images within a set need equal
dimensions. A textureless layer is solid white at that size with the material's base-colour tint. Terrain
uses the ground pipeline's default tiling and base specular strength. Declared normal and roughness maps
produce typed `MapTerrainFinding` entries, since this pipeline consumes albedo only.

`MapRenderTransform` subtracts resource anchors before local pose and focus rounding. The format-neutral
`Scene3D.DrawAnchored` retains the integer `RenderAnchor` through rigid grouping, both shadow paths and
previous-frame motion. Its World matrix is local to the anchor. Existing absolute draws retain their
released queue, culling and zero-origin upload behavior.

`MapPlacementPolicy` freezes disjoint caller asset selections and increasing positive LOD thresholds.
An empty threshold list keeps base geometry. Otherwise each asset needs one threshold per declared far tier.
`MapPlacementRouting.Create` freezes one ordinary, cluster or animated route for every placement. Cluster and
animated selections cannot overlap. Animated roofs refuse. Roof and ordinary members of a shared asset
enter distinct canonical cohorts and retain Roof versus Placement resource kinds.
Invalid policies refuse before preparation. Pass the same partition object to the rigid and animated producers.
Explicit animated placements use `MapPlacementPolicy.DefaultDrawRadius`, independently of generated cover's
`DrawRadius`. Their frames retain the cover fade and wind settings with both density factors fixed to one.

`NativeMapPlacements.Prepare(scene, input, policy, routing)` retains the selected prepared input lease.
The `MapRenderAssets` overload instead owns detached copies and has no lease on the caller's input.
`MapPlacementRenderBatch` retains placement identities, numeric IDs, kinds and ordered tags. Its `Markers`
retain annotation identities without adding visible labels. Register every part of every declared tier before
recording. Geometry is already in metres. Effective placement poses compose once in anchor-local space.
The typed recorder and `PropRenderer.DrawNative` share N-tier selection and complementary coverage.

Each selected HLOD layer partitions its frozen inventory into native regions of 64 document tiles per edge.
The region edge is `64 * TileSize` metres. Regions exceeding 1024 m split into `n * n` equal sub-cells,
where `n = ceil(64 * TileSize / 1024)`, aligned with the region grid. Assignment uses the precise binary64
effective origin and half-open boundaries before any absolute float conversion. Keys are
`(layer, cell X, cell Z, plane 0)`, with cell indices counting subdivisions globally from the region grid origin.
The anchor floors the cell's X/Z origin and the minimum member origin Y to whole metres.
Fractional cell origins remain local. Before the internal builder enters, every transformed source vertex must
have an absolute anchor-relative coordinate strictly below 2048 m on every axis, checked in binary64 through
the same position kernel as the merge. Failure throws `MapRenderAssetRefusal` with `NotRepresentable` and refuses
preparation. It never silently drops the cluster or falls back to individual geometry.
Merged vertices round once after certification, within the 0.0001 m binary32 submission allowance.
Content digests retain inventory, transforms,
closure and weld settings separately from the stable spatial owner key.
Cell discovery acquires the complete layer inventory through the captured source and its existing admission
before selected mesh decode. Discovery bounds include every effective placement origin, so a cell query contains
every origin-owned member even when its geometry lies entirely outside the cell. Exact origin filtering selects
members from those conservative candidates. The complete cell supplies member poses and asset IDs for the merged
proxy and its individual fallback. Members are ordered by identity, and proxy payload identity excludes window witnesses.
Windows touching different parts of one cell prepare the same complete payload, share one upload and draw, and
release the owner after the last membership. A normal window edge does not refuse. Failed cell acquisition
refuses the plan with its typed source or capacity status. Partial proxies are never published.

The shared `PropLayer.PlacementLayer` native overload freezes the actual instance inventory, every tier and
caller thresholds. Both individual fallback and accepted proxies consume that shared layer. The released
per-cluster centre-distance coverage uses the complete cell area. Layer outer fade width equals
`HlodCrossfadeWidth`, including placement fades composed before LOD coverage. Each cell is independent.
`NativeMapPlacements.PrepareCluster` validates the scene, frozen routes, cell domain, layer inventory, homogeneous roof roles and vertex precision before invoking
the internal shared CPU builder. That lower builder has no building intent and makes no independent roof promise.
Standalone cluster construction uses the same admitted merge envelope as batch construction. Its
validation cell releases before merge begins, since the returned build retains independent inputs. The caller
disposes the returned `NativePropClusterCpuBuild` to release its retained receipt. Batch construction
transfers that receipt into the batch without a second debit.
Roof cohorts retain complete neutral membership geometry acquired and admitted before selected decode.
Closure follows admitted prism domains and their parent support, including native stored records.
Policy replacement admits the new scene before releasing its predecessor. The canonical evaluation keeps
its final scene charged for the cohort lifetime. Merge and contribution workspace release at last use.
Dependency attribution and construction peaks are admitted while earlier scene and cell receipts remain
intact. Refusal releases borrowed attribution, and successful output completion consumes those receipts.
CPU meshes, detached outline and colour copies, evaluation dependencies and frame lookup capacities
transfer into the batch's unique retained receipt.
Reachable roof inventories can include the same shared footprint pages. Cell, batch and plan receipts count
their actual shared membership dependencies once. Batch `RetainedBytes` keeps the reachable total, while
`PreparationChargeBytes` reports the deduplicated receipt.
Incomplete full-room coverage refuses preparation. Individual resources remain resident for mixed visibility fallback. Individuals remain active until `AcceptCluster` receives the exact
prepared build and the externally published mesh. It retains metadata only and never uploads or frees that handle.
An empty welded proxy cannot be accepted, so individuals remain active. Placement registration bounds are
finite in all three dimensions and include all declared tiers and light reach, with the 0.0001 m local submission
allowance. Discovery metadata remains conservative in height. HLOD colour and shadow descriptors use the released
shared emitter, including shadow inversion and the unchanged solid endpoint.

`NativeMapLights.Record` registers and records every effective resident emitter, with no nearest-N cap.
It carries the full descriptor, scaled reach, exact local position, shadow intent and fixture owner exclusion.
An optional stamp overload binds these registrations to a composite generation. The optional authored
`shadowExclusion` selects none, a world-metre near sphere, or an owner-local metre fixture box with a
world-metre margin. No declaration retains the owner render/LOD-bounds box. See the
[light payload contract](../KhaozEngine.MapDoc/README.md#rigid-prefab-payloads-and-building-intent).
Submitted light shadow exclusion corners are anchor-local. `Scene3D.AddLightAnchored` retains that space through receiver buffers, the light cluster grid
and point-shadow binding. Released absolute light overloads keep their existing behavior. Device light capacity
and overflow diagnostics remain authoritative.

`IMapRenderDevice` measures upload demand before creation. An upload must claim its
`ISceneUploadReservation` before running any allocation factory. Retirement stays charged until the
renderer confirms release. Render3D now supplies admitted upload, publication and `SceneResourceAccess`.
`MapScene3DSink` measures a borrowed `SceneUploadDescription`. Mesh/image copies, synthesized white
terrain images and detached `SceneUploadData` are created only after the aggregate reservation is
claimed. Measurement does not perform those retained allocations.
The composite residency owner is `MapRenderResidency`. `MapScene3DSink` consumes the same typed registrations and frames in viewport and capture. Explicit plant resources convert to
`SceneFoliageSource` with `SceneFoliageTransformSpace.AnchorLocal`. Generated world rows use `World`.
`ScenePreparedFoliage` declares the immutable 96-byte packed product. It is produced by `Scene3D.PrepareResourceUpload`.

`MapWaterScopedCompiler.Compile(scene)` selects complete world-locked 2 m water cells, split at document
storage borders. Requested coverage and complete-cell dependency coverage have separate witnesses.
Containment, bed and ceiling reads use the pinned scene source and shared admission. Missing or unresolved
columns refuse the whole result. Overlapping draw windows retain identical cell keys and payload bytes.
Canonical payloads remain look-free. Draw membership expands the window by explicit
`MapSceneScope.MotionPaddingMetres`, retaining whole cells whose displaced surfaces can reach the window.
The same allowance then acquires separate clip dependencies around those complete draw cells, including
their portions outside the padded window. Clip dependencies do not become draw memberships on their own.

`NativeMapWater.Prepare(water, look, settings)` resolves every inherited `WaterLook` field against the caller's
scene `WaterSettings` through the renderer's `WaterLook.ResolveInto` rule. It freezes that explicit effective
look and prepares typed `MapWaterResource` registrations plus `MapWaterFrame` submissions. Later changes to
the look or scene settings require preparation again. `MapWaterRenderBatch` exposes the canonical cell inventory,
retained bytes and preparation charge. Register once per resource generation, then call `Record` for each
frame. Closed water has no draw. Meshes deduplicate vertices within their canonical cells and retain an
original-domain clip payload, so displaced fragments cannot fill dry notches.
Dispose the compilation result after preparation. The batch keeps a shared lease for its selected canonical
cells. `RetainedBytes` includes those cells, while `PreparationChargeBytes` reports the batch's independent
containers, mesh and clip inventory. Shared cells constrain each borrowing window and count once in the
attempt ledger.
The clip payload is a render derivation over the same body's neighbouring free-surface regions within the
look's certified displacement halo. It is tested at displaced world position. Internal cell borders never
become shorelines. The render digest includes every consumed neighbour region digest.

Call `NativeMapWater.RequiredMotionPadding(look, settings)` before acquiring the scene, passing the same look
and scene settings to `Prepare`. It returns the conservative procedural Gerstner reach plus
`ClipEpsilonMetres`, fixed at 0.001 m. The scene's motion padding must cover
that whole value or `Prepare` refuses with `MapRenderAssetStatus.Invalid`. Default and partially overridden
procedural looks inherit before certification. Active component generation uses the effective speed, seed
and directions, and is validated even at zero horizontal steepness because vertical waves remain active.
Valid zero-steepness waves retain zero horizontal reach. Nonfinite generated components refuse with `Invalid`,
including overflow from finite effective inputs.
FFT remains an explicit `Unsupported` refusal for native MapDoc preparation. R8b owns FFT certification.
Existing water-plane FFT rendering retains its released behavior. Resource bounds include the certified
horizontal and vertical motion allowance. No body-wide shared clip resource is retained.

The format-neutral `WaterSurfaceMesh` carries a published `MeshHandle`, integer `RenderAnchor`, local
transform and look. `Scene3D.LoadWaterMesh` uploads a mesh and original domain together. A resource upload
producer may instead attach the domain with `LoadWaterDomain` after uploading its mesh. Both creation
paths belong behind the caller's upload reservation. `Scene3D.DrawWater(in WaterSurfaceMesh)` only queues
the existing resource and refuses stale handles. `UnloadMesh` retires the domain with the mesh.
The bounded shader alone binds domain clip data. Existing `WaterPlane` submissions retain their released
programs and queue behavior. R8b replaces optical inputs behind the same bounded geometry/domain contract,
including body-local optical depth. R8a adds no camera-driven water level, global sea or horizon extension.
The current optical packing seam is `WaterRenderer.PackBounded`. R8b #1300 replaces its optical inputs
while retaining original-domain clipping. Point sampling remains the separate #1358 consumer dependency.


## Composite residency and replacement

`MapRenderPolicy` freezes placement routing, animated asset selection, stable foliage registration, water look
and terrain material identities. It retains no decoded images. Its canonical digest includes vector fields and
orders dictionary keys. Both the placement default radius and generated cover radius participate in that
identity. Replacing either updates its frame radius while reusing unchanged cover samples.
`MapRenderSourceState` binds that policy to a source, its matching closure, complete
render extent proofs and explicit windows. The selected-input closure must be the same immutable closure
instance captured by the source. A hash-equal separate load is rejected before scheduling.

`MapRenderPlan.Prepare` composes one acquired window with one routing partition and a shared preparation
admission. Terrain reads the passed input's selected materials. Placement and foliage batches share its selected
assets. Preparation succeeds by taking ownership of the input lease. A refusal leaves that lease with its caller.
Material discovery releases its defensive surface copies before decode. Only the selected material-ID array
remains admitted through the following preparation stages.
Register the plan before recording frames, and dispose it when its CPU inventory is no longer required.

`MapRenderTransition.Findings` preserves refusal details. Its optional `Capacity` carries the limit name,
requested amount and allowance from scene acquisition and residency admission.
`MapRenderAdmission.TryReserve(demand, out capacity)` also reports CPU or GPU admission pressure.
A waiting upload retains `Incomplete` status and exposes the limiting demand in its transition.

`MapRenderResidency` prepares replacement generations on a bounded worker queue. `RequestReplacement`,
`Invalidate` and `SetWindows` share one epoch and cancellation owner. Validation and preparation refusals retain
the accepted draws and witness, and restore the accepted desired state. A pending replacement reports Incomplete.
Source, closure, proof, policy and scope identities are checked before decode, upload and publication. Every
resource must finish its incremental upload before one atomic accepted-plan publication. Ready resources wait
until the next update, allowing a completed unpublished generation to be superseded safely.

Resource owners are keyed by stable logical identity, verified payload and bounds. Intersecting windows borrow
one owner, including complete canonical water cells and all placement tiers. Removing the first membership keeps
the handle. Removing the last queues its release under the disposal allowance. HLOD keeps individual geometry
until the composite's merged registration is published. Recorded aliases retain their window stamps and resolve
to the same owner through `TryGetHandle`.

`MapRenderAssetCache` shares immutable selected mesh and material pieces by verified content, including across
closure changes that preserve the selected bytes. It retains an owner lease and returns independent caller leases.
Idle composition entries and pieces are evicted after the last plan or upload reader ends. A material change can
replace its image while retaining an identical mesh. Frames borrow admitted input and never decode again.

`MapRenderAdmission` charges source metadata, encoded closure retention, decoder peaks, prepared plans and the
renderer-measured upload demand. Settled plans include the unique canonical water allocations retained by their
batch readers, alongside the plan and scene receipts. `MapUploadReservation` is claimed before factories with
the unchanged complete CPU and GPU peak. GPU staging credit returns only after transfer completion on the
device. After publication drops detached inputs and transfer storage, CPU settles to the renderer's independent
retained inventory. Final `Released` returns only the remaining CPU and owned GPU payload, once. Cancellation
before publication returns all unsettled credit at destruction. The decoded upload borrow remains held through
retirement. `MaxRetiredGpuBytes` never
adds sharing credit. `MapRenderLifetime` reports the live charged ownership groups and actual transfer bytes.

A queue entry is one atomic composite replacement. Its windows share one live preparation ledger,
further narrowed by queue and plan views. Live workspace, retained inventory and borrowed input obligations
share the allowance. Sequential temporary work reuses credit. Growth admits old and new arrays together.
Each plan view includes reused context and selected decoded pieces, while shared input counts once in the
attempt's ownership ledger. Decoder reservations mirror actual owned and temporary demand before allocation.
Captured cell inventory entering another attempt is admitted there before its source receipt releases.
Within one ledger, a handoff adds only the newly owning child-view demand. Common ancestors retain one
charge for the same inventory.
An operation observes new reservations through its own view of a shared decoder budget. Earlier payloads
remain covered by their identity receipts and selected-input references.
Original tighter decoder limits and their diagnostic identity remain binding. Disposal cancels pending work
without blocking the scheduler. A running worker retains its readers and CPU reservation until unwind.

A captured foliage context has a retained preparation lease. Policy-only replacements share that lease, including
its density and effective dependency product. The native cache retains compact immutable instance columns,
not full diagnostic samples. Builder pages, ordinal indexes and freeze overlap are admitted before allocation.
Cache, accepted plan and pending replacement readers share the same column owner. Invalidation drops cache
ownership only. Last release detaches the backing payload before returning its receipt and CPU lease.
Strings and generation policies have a separate metadata owner counted by object identity. It retains no context,
support geometry or decoded assets. Context charge wrappers clear their context references when capture ownership ends.
Cache-index and metadata growth reserve through the current preparation's root and binding window. Earlier
retained slices keep their original receipts until release. Refusal returns only the new reservation before
allocation, and capacity settlement releases the newest slices first.
Accepted-plan replacement and window unload dispose the old `MapNativeFoliageBatch`. Its `GeneratedIdentities`
and `CoverBatches` getters, `Register` and `Record` throw `ObjectDisposedException` after disposal. Capture detached
public snapshots before requesting replacement. Those snapshots and foliage payloads from public `Register` survive disposal,
while retaining the batch shell keeps no per-row identity copy. See the
[foliage lifetime contract](../KhaozEngine.MapDoc/README.md#native-foliage-consumer-contract).
Settled CPU bytes are the unique retained source, input, context, column, metadata,
plan, recording and upload inventory. In-flight CPU additionally includes queue reservations and staging.
At publication, zero recording references or demand below half the retained capacity replaces both dictionaries
and the draw set with storage sized to current demand. Credit returns after the old backing arrays are no
longer reachable. Exactly half retains capacity for reuse. An empty accepted set returns to source-only CPU
after device retirement releases its last upload and decoded-input readers. Disposal also drops recording storage.
Preparation peaks and cumulative allocation are not retained-size authorities. GPU handle owners retain
identity and handle only. They do not retain obsolete CPU registrations,
including while waiting in the bounded disposal queue.

Each window narrows the shared allowance before acquisition and preparation. Foliage generation derives support
limits and staged column admission from that remaining allowance. Count ceilings are checked before scanning.
Capacity can refuse at the first control, accepted-row growth, ordinal growth or freeze allocation that would
exceed the allowance. A sparse layer never keeps a full candidate-lattice reservation. Query limits and generated
addresses are unchanged.

The foliage inventory proof independently counts actual array lengths, index capacities, object identities and
pinned 64-bit controls. It covers empty, tiny, dense, sparse, maximal-table and replacement cases, including
index and metadata growth after invalidation while earlier readers remain live. Root and binding
window interval peaks stay within twice the simultaneous domain inventory plus a fixed 4096-byte kernel allowance
for ledger and callback machinery. That allowance never scales with rows. Old builder/index arrays and frozen
columns coexist during freeze. Old and pending cover views count their shared column store once. Renderer-owned
F1 temporal and point-shadow snapshots remain charged after source columns are collected and until retirement.

## Roof channels and presentation picks

`MapRenderPlan` prepares `PresentationGeometry` and `RoofEvaluation` against the same aggregate admission
as its terrain, placement and foliage products. `Update`, `Record` and `MapPresentationPicker.Pick` reuse
that buffer for observer motion and roof mode changes. No detached roof result is built in a frame.
Truncated collider or selection payloads refuse pending preparation with `Invalid`. Accepted draws and
their witness stay live through the normal replacement refusal path.

`NativeMapRoofs.Record` alone emits unclustered roofs and roof cohorts. Ordinary placement recording excludes them.
Roof cohorts share the legacy distance and visibility kernels. All-hidden proxies submit shadow-only.
Mixed cohorts suppress merged colour and use retained individuals at zero HLOD floor.
Shadow intent is conjoined with the configured layer's `CastsShadows`. A non-casting hidden roof emits no draw.
Hidden roofs retain their registered references, bounds, meshes, draw ranges and shadow coverage. Their
colour, selection and camera channels are absent. Sky and physical channels remain present. The device
sink maps a roof with shadow and no colour to `ShadowOnly`. Neither concealment nor picking changes
registration, uploads or accepted non-roof HLOD coverage.

`MapPlacementRenderBatch.RecordRoofOutline` exposes effective clustered-roof colour descriptors to an
optional consumer. Proxy descriptors cache the selected roof's surviving triangles from the full cohort merge and weld,
including cross-member averaged vertices. Their anchor matches the cohort, and cache identity includes
all members and the weld setting. Empty contributions have no proxy outline.
`MapScene3DSink.RecordRoofOutline` also handles unclustered roofs through the plan's admitted roof policy
and submits them through the shared anchored mesh-outline primitive. Hidden roofs have no outline.
Selection, camera, sky and physical queries continue to use original per-placement shapes.

`MapPresentationPicker` selects raised interaction envelopes, preferring an explicit selection resource
to the collider. The shared minimum height is 1 m. Camera, sky and physical queries use unchanged
colliders and acquired canonical faces. Assets with neither shape remain cosmetic. Prepared vertices,
ordered candidate storage and visited flags are admitted before frame queries. Equal distances resolve
by ordinal owner ID. Rejected hidden or cut hits continue to the next candidate.

`MapPresentationHit.LocalPoint` is relative to the supplied frame's horizontal origin, with world-datum
Y. `IMapPresentationCut` defaults to accepting every hit. R8b owns its cut classification and frame
synchronization. Each plan and its pickers are serial frame consumers sharing one prepared scratch area.

## Native view and snapshot contract

`IMapNativeRenderHost.Resolve` returns a `MapNativeRenderContext` with verified closure, proof-bound
source, extent proofs, frozen render policy, capture lighting and style. The editor and ke-mapedit use
this host contract. Native input failure refuses before any terrain-only success. The adapter adds no
root field. MapDoc stays format 9, prefab payload 1 and foliage payload 1.

`NativeMapView` drives `MapRenderResidency` and `MapScene3DSink`. `MapDrawRecorder` records the same
complete registrations and typed frame values without a device, window or backend. A draw set contains
resources as well as draws. Registration carries multipart geometry, material maps, every LOD, light
descriptors, water domains and foliage model-part tables. Foliage rows carry model indices, original
matrices and ranks. Stable profile/casting changes require replacement. Dynamic frame values carry
dissolve, complementary phase, shadow inversion, light colour/radius/intensity/exclusion, frozen water
look, local focus, foliage quality, wind, interactors, time and seed.

`MapRenderResidency.RequestReplacement`, `Invalidate` and `SetWindows` retain accepted and pending
witnesses separately. Source, closure, scope, policy and generation fence preparation, completion,
upload and publication. A completed unpublished generation can still be superseded. Each head checks
its own stamp, process-local generation counters are not identities shared between independent heads.
Every logical owner retains all intersecting memberships, all LODs, light radius and explicit motion
padding. Last-membership release retires the resource, retaining its charge until renderer completion.

`MapSnapshotRequest` freezes closure, scope, framing, observer, roof mode, time, wind seed, style, AA,
lighting and completeness requirement. Explicit focus is a `MapFramePoint`. Omitted View focus uses the
eye. TopDown focus uses `MapCaptureFocus.Resolve` over the declared support domain. No frame consumer
infers focus from the observer. A refused unresolved focus stays null in the transport result.
`MapCaptureDependencies.Compute` expands directional and point-light caster coverage from frozen lighting.
Light discovery releases each probe scene after its final read. Only matched light rows and lookup backing
remain admitted through dependency expansion and hashing.
Its witness keeps requested, required and acquired scopes separate. `MapCapturePreparation.Prepare`
proves dependency and focus admission without a device. Its temporary probe scenes are not resident CPU.
`NativeMapSnapshot` uses the same residency and
submission path. Resources must be ready before the default 16 temporal warm-up frames. The default
settle allowance is 1024 operations. Temporal warm-up never repairs incomplete dependency coverage.

`MapCaptureRequirement.CompleteScope` refuses missing coverage. Preview can return an image with typed
findings, but a capped result sets both `CompleteForScope` and `WholeWorldComplete` false. A scoped
witness does not certify the whole world or simulation residency. `MapSimulationResidency` remains a
separate GPU-free owner of required statics and navigation domains.

Sparse verification covers X/Z from -32,000 to +32,000 m, Y from -500 to +500 m and 64 m padding.
That envelope is not a dense allocation requirement or a content limit. Anchor subtraction precedes
binary32 rounding with at most 0.0001 m additional local submission error. Independent generation pins
belong to each location. An origin-only precision comparison reuses one absolute realization.
Steady-state frame work reuses admitted geometry, membership and roof buffers. Decode, preparation,
upload, accepted, pending and retirement retention remain separately budgeted, with no RSS or timing SLA.

R8b consumes bounded-water optical inputs, including body-local depth, and the scoped room/roof/mesh
inventory for `IInteriorSource`. `IMapPresentationCut` is the explicit classification seam. Cave lighting,
sky/camera domain policy and appearance acceptance remain R8b work. R9 consumes the viewport and commands,
R10 adds render findings and complete tool parity, and R11 consumes the independent draw-set comparison.
G3 uses this view/snapshot contract and also needs the separate native CPU world-map source
[#1370](https://github.com/APKiwiOrg/KhaozEngine/issues/1370). None of these interfaces claims that gate passed.

Static shadow keys are derived from the effective light ID by `NativeMapLights`. The same derived key
travels through capture preparation, typed submission, the scene sink and capture diagnostics. There
is no persisted light-key field and no fixed numeric key promised by a fixture label.
`NativeMapSnapshot.Capture(request, afterSubmit)` optionally exposes a synchronous diagnostic callback
after each actual submission, including warm-up, before rendering. The callback borrows the scene and
must neither retain nor dispose it. Ordinary capture and `CaptureSequence` use the same path.

## Native scene verification

| Evidence home | Contract exercised |
| --- | --- |
| `FarFoliageGenerationPinsTests` | Independent released generation literals at each location, with full and windowed generation |
| `NativeMapPrecisionTests` | Exact anchor subtraction, per-location realization and origin-only submission precision |
| `NativeMapAllocationTests`, `NativeResourceUploadAllocationTests` | Warm CPU work and upload admission before retained copies |
| `NativeMapFoliageEvidenceTests` | Complete keyed source rows, model parts, patch membership and casting |
| `NativeRoofHlodGpuTests` | Roof-HLOD visible, hidden, mixed, midpoint and shadow-omission goldens, plus aged scene parity |
| `NativeMapGoldenGpuTests` | Original native families with omission controls, origin pairs, prepared counts and fresh versus aged scene checks |
| `NativeMapSnapshotGpuTests`, `NativeMapCaptureWireTests` | Actual viewport/capture submission, derived light keys and foliage focus through the real wire |

The full scene oracle consumes keyed source submissions one-to-one and rejects leftovers. Its source
foliage roster includes expected empty generations and complete per-generation row counts. Missing
terrain/light/water/foliage writes, all four roof flags and every foliage part material map are checked.
CPU runs with GPU skips do not certify a backend. Required native facts must execute without skips
on Metal, Direct3D 11 and Vulkan at the exact integration candidate. These tests do not claim R8b
appearance or R11 import acceptance.
