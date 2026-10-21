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

`IMapRenderDevice` measures upload demand before creation. An upload must claim its
`ISceneUploadReservation` before running any allocation factory. Retirement stays charged until the
renderer confirms release. Renderer publication, residency and the actual scene sink are later R8a work.
`ScenePreparedFoliage` declares the immutable 96-byte packed product. Its producer is not implemented here.

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
