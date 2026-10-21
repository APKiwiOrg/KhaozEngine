# KhaozEngine.MapDoc.Render3D

Optional native MapDoc rendering contracts. Reference this package explicitly. It is absent from every
umbrella and has exactly three direct engine edges, MapDoc, Render3D and Terrain.Render3D. It adds no
direct backend, editor, TileWorld or MapDoc.Physics dependency. Render3D already carries the three native
backends through Windowing. Terrain.Render3D carries the neutral Physics seam.

`MapRenderExtentValidator.Validate` streams verified geometry and checks every declared mesh tier and
typed light reach. Its `MapAssetExtentProofSet` retains bounds, source units and resource provenance only.
The encoded `MapAssetClosure` remains separate from decoded residency.

`MapPreparedInputLease.LoadSelected` loads selected mesh tiers and required native material dependencies
under one `GltfDecodeBudget`. Retain the input for each accepted or pending plan and dispose each lease.
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
Emissive/occlusion textures and nonempty material extensions refuse before model loading because the
returned maps cannot represent them. Compatibility glTF and flattened native loading keep their
released lenient behavior.

`MapPreparedResource` carries copied immutable registration payloads. `MapDrawRecord` carries explicit
anchor-local transform and focus, channels and exactly one typed frame. `MapDrawRecorder` records these
without a device. Frozen water and foliage policies capture the caller's mutable objects. Foliage model
profiles and casting resolve into registration. Per-frame draw options exclude those stable values.

`NativeMapTerrain.Prepare(scene, input, materialIds)` consumes the scene's acquired canonical faces and
effective paint regions. `MapTerrainRenderBatch.Register` registers typed `MapTerrainResource` payloads.
`Record` writes explicit rigid defaults and focus in each resource's anchor space. Keep the batch alive
while consuming its registrations. It retains the prepared input independently of the original caller.
`RetainedBytes` covers geometry and provenance. `InputRetainedBytes` reports the shared decoded charge,
and `PreparationChargeBytes` includes intermediate work under the captured scene allowance.

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
animated selections cannot overlap. Every effective roof stays ordinary, including a shared roof/non-roof asset.
Invalid policies refuse before preparation. Pass the same partition object to the rigid and animated producers.

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

The shared `PropLayer.PlacementLayer` native overload freezes the actual instance inventory, every tier and
caller thresholds. Both individual fallback and accepted proxies consume that shared layer. The released
per-cluster centre-distance coverage remains unchanged and uses the complete cell area. Each cell is independent.
`NativeMapPlacements.PrepareCluster` validates the scene, frozen routes, cell domain, layer inventory, roof exclusion and vertex precision before invoking
the internal shared CPU builder. That lower builder has no building intent and makes no independent roof promise.
Ordinary roofs retain separate resources. Individuals remain active until `AcceptCluster` receives the exact
prepared build and the externally published mesh. It retains metadata only and never uploads or frees that handle.
An empty welded proxy cannot be accepted, so individuals remain active. Placement registration bounds are
finite in all three dimensions and include all declared tiers and light reach, with the 0.0001 m local submission
allowance. Discovery metadata remains conservative in height. HLOD colour and shadow descriptors use the released
shared emitter, including shadow inversion and the unchanged solid endpoint.

`NativeMapLights.Record` registers and records every effective resident emitter, with no nearest-N cap.
It carries the full descriptor, scaled reach, exact local position, shadow intent and fixture owner exclusion.
An optional stamp overload binds these registrations to a composite generation. Light shadow exclusion corners
are anchor-local. `Scene3D.AddLightAnchored` retains that space through receiver buffers, the light cluster grid
and point-shadow binding. Released absolute light overloads keep their existing behavior. Device light capacity
and overflow diagnostics remain authoritative.

`IMapRenderDevice` measures upload demand before creation. An upload must claim its
`ISceneUploadReservation` before running any allocation factory. Retirement stays charged until the
renderer confirms release. Render3D now supplies admitted upload, publication and `SceneResourceAccess`.
The composite residency owner is `MapRenderResidency`. The actual scene sink remains Task 12 work. Explicit plant resources convert to
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
orders dictionary keys. `MapRenderSourceState` binds that policy to a source, its matching closure, complete
render extent proofs and explicit windows.

`MapRenderPlan.Prepare` composes one acquired window with one routing partition and a shared preparation
admission. Terrain reads the passed input's selected materials. Placement and foliage batches share its selected
assets. Preparation succeeds by taking ownership of the input lease. A refusal leaves that lease with its caller.
Register the plan before recording frames, and dispose it when its CPU inventory is no longer required.

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
renderer-measured upload demand. `MapUploadReservation` is claimed before factories, and claimed credit returns
only on the renderer's final `Released` callback. Published and retiring allocations keep their original charge.
The staging allowance and decoded upload borrow remain held through retirement. `MaxRetiredGpuBytes` never
adds sharing credit. `MapRenderLifetime` reports the live charged ownership groups and actual transfer bytes.

A queue entry is one atomic composite replacement. Its windows share the source's bounded preparation allowance,
further narrowed by queue and plan budgets. Disposal cancels pending work without blocking a caller's scheduler.
Any still-running worker retains its readers and CPU charges until its completion continuation unwinds them.
