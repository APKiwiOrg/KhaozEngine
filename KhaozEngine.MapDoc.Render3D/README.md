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

Each selected HLOD layer merges its frozen placement inventory. Its cluster identity includes that inventory,
transforms, closure and weld cell, so differing scoped subsets cannot alias the same merged owner.
`NativeMapPlacements.PrepareCluster` validates the scene, frozen routes and roof exclusion before invoking
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
renderer confirms release. Renderer publication, residency and the actual scene sink are later R8a work.
`ScenePreparedFoliage` declares the immutable 96-byte packed product. Its producer is not implemented here.
