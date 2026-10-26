# KhaozEngine.MapDoc

The KhaozEngine zone/map document format: one JSON file per zone capturing what used to be world code
(terrain, procedural scatter, authored placements, spawns, regions), versioned and schema-validated, with
runtime builders that hand games back the exact objects they already consume. Human-diffable,
git-committed in the game repo. GPU-free.

## Consuming the document model

Native format-9 documents and GPU-free prefab, interior and foliage preparation arrive through
`KhaozEngine.Foundation`, including its `Game2D`, `Game3D` and `Server` consumers. Add
`KhaozEngine.MapDoc.Physics` and a physics backend such as `KhaozEngine.Physics.Bepu` explicitly when
colliders are needed. `KhaozEngine.MapEditor` and the `ke-mapedit` tool stay outside every umbrella.
See the [consumer contract](../docs/USING-KHAOZENGINE.md#free-buildings-and-effective-resources) and the
[native foliage consumer contract](#native-foliage-consumer-contract) for preparation and runtime boundaries.

## Bound native validation

`MapBoundDocumentValidation.Validate(document, assets, registry)` checks a complete native document
against a verified immutable `MapAssetClosure`, including exact roots, asset membership, identities and
finite transforms. `ValidateLocal(document, registry)` checks document-local validity only.
`MapAuthoredIdentity` and complete-document `MapAuthoredIdentityV2` reuse the bound checks and separately
validate builder/hash options. Editing callers
need no synthetic builder options and never resolve relative resource paths implicitly.
`MapResolver.Resolve` publishes an immutable `MapResolvedDocument`. Its `AssetClosure` is the exact verified
closure passed in, for binding and resource lookup without a second load. `MapNativeResolution.Resolve(document,
assets, options, legacySupportHeight)` routes a native document to its resolver by identity: `(1, 1)` resolves with
`MapResolver` over the caller's legacy support height and `(1, 2)` with `MapResolverV2` over the document's own
surfaces. Options with a different resolver version refuse. This package loads no meshes and has no render
dependency. `KhaozEngine.Terrain.Render3D` owns the one-way `MapAssetManifestAdapter` and `NativeMapAssetLoader`,
which loads a native mesh with its source units applied once.

`KhaozEngine.MapDoc.Identity.MapLegacyTerrainDigest` identifies a resolver 1 document's analytic terrain in two
parts: `TerrainBlock(document)` over the terrain block and sculpt cell size, and `SculptTile(tile)` per sculpt tile,
the same digest the resolver 2 content digest uses for that tile. Both are persisted identities, so a change to
either canonical form is an identity migration.

Native identity and resolution use the default registry only. Complete-document `MapAuthoredIdentityV2.Compute`,
`MapAuthoredIdentity.Compute` and
`MapResolver.Resolve` take no registry and validate with `MapDocRegistry.CreateDefault()`, and ke-mapedit's
`NativeDocumentService` builds its support field from the default registry too. A native document that uses
a terrain feature registered on a custom `MapDocRegistry` is therefore refused by them as an unregistered
feature type, even where `Validate` or `ValidateLocal` accept it when given that registry. Custom terrain
features are not supported in native documents yet.

## Native edit effects

The native edit contract shipped in 20.30.0.
`KhaozEngine.MapDoc.Editing.MapNativeEditEffects` describes an accepted document edit with old and new
`MapBox3` bounds, patch keys, space and dependency IDs, `MapDigestChange` values and `MapNativeInvalidation`
flags for Terrain, Physics, Nav, Material, Residency and Placements. `Unbounded` marks a geometry change whose
bounds are unknown. It is a full reset: discard all prior derived state for this world and rebuild from the
post-edit world. `Describe()` emits
compact canonical JSON with fixed field order, sorted patch keys and ordinal IDs, invariant numbers and explicit
nulls.
`OldBounds` and `NewBounds` keep the reported extents. `OldSpatialBounds` and `NewSpatialBounds` include optional
conservative installed geometry bounds for Physics, Nav and Residency. Without separate spatial values they follow
the reported bounds, including record copies that change them. Spatial values always include the report.
`Describe()` adds `oldSpatialBounds` and `newSpatialBounds` after the reported bounds only when they differ.
`MapNativeWriteSet` names patch payloads, surface declarations and placement state for publication.
Records, spaces, corner owners and material uses are carried by the named patch payloads.

The terrain API, new in 20.30.0, uses `MapTerrainEdits.Prepare(surfaces, edit)` to return a detached candidate,
its exact `MapNativeWriteSet` and effects without mutating the input. `MapSetCornerHeights`, `MapSmoothCorners`,
`MapSetCells` and `MapSetPresence` use patch-local coordinates (presence takes slot-cell indices).
Smoothing accepts 1 to 64 passes, averages a double-buffered 3 by 3 neighbourhood including the centre,
and rounds away from zero. Its unchanged halo must be available on this patch or a resident same-surface
neighbour. Neighbours share boundary corners, so a halo corner in several neighbours is read when they all
agree on its height. Missing halo geometry or disagreeing neighbours refuse the edit.

`MapReassignCornerOwner`, `MapReanchorRecords` and `MapReplaceTopology` make ownership and anchor changes
explicit. Upserts retain current shared-corner owners. Removing an owner or anchor requires reassignment
of its dependants or re-anchoring of its records, respectively. Re-anchoring updates every `MapRecordRef`,
including surface span metadata. `MapCompositeEdit` prepares its ordered edits as one candidate.
`MapConvertFinePatch` reuses `MapFinePatchConversion`, including its authored-difference policy.
Preparation validates references, seams and owners and refreshes affected surface semantic digests.
Heights, smoothing, presence and ownership invalidate Terrain, Physics, Nav and Residency. Topology and
conversion also invalidate Material. Cell material-ID changes alone invalidate Terrain and Material,
other cell fields invalidate all five, and re-anchoring alone invalidates Residency.

## Native asset roots and record digests

`MapNativeWriteSet` also carries a non-positional `NativeAssets` flag, so its released constructor is unchanged. When
set, publication takes the candidate's whole native asset root list. ke-mapedit's collider edits use it to swap a
rewritten root manifest in one native transaction. `MapSurfaceSemantics.RecordDigest(anchor, record)` is the record
digest edit effects report. It covers the record's canonical bytes and its anchor and nothing of the anchor patch's
terrain.

## Whole authored surface identity

Scheme 2, new in 20.30.0, is the resolver-2 whole-document identity.
`KhaozEngine.MapDoc.Identity.MapAuthoredIdentityV2.Compute(document, assets, options)` requires a complete
document with resolver identity `(1, 2)`, `AuthoredBindingsV2`, and options with `ResolverVersion: 2`, a
nonempty builder ID and options hash, and a positive builder version. A partial editing window refuses
before metadata validation with a `MapDocumentException` containing `window`. Complete unsaved edits hash
current patch semantics. Old tile baselines and surface digest fields do not certify corruption in an
editable complete document. Its native roots must exactly match the supplied verified closure.

The `Compute(MapStoredSurfaceSource, assets, options)` overload verifies the whole original pinned storage
closure. `Open(directory)` retains the original resolver, support recipe, roots and root digest. Identity
checks those metadata and closure roots before payload reads, then verifies every directory and index
address, reads and hashes one payload at a time, and checks patch and per-surface semantic digests. Index
metadata may be cached, but every identity call rereads payload bytes. A freshly opened lazy source is
valid input. A swept payload refuses with `Missing`, and mismatched bytes or semantics refuse with
`Corrupt`. Replacing `map.json` never switches an existing source to the new generation. Open a new source
to select that generation.

The stored overload also reads the tile files named by the generation pinned at `Open`, one tile at a time,
by their pinned names. Each tile's parsed content must hash to its pinned tile hash before any entry is
used. A removed tile file refuses with `Missing` and altered content with `Corrupt`. Identity never rereads
the manifest, lists directories or trusts a stored hash without reading the file. Stored identity validates
the same document rules as the loaded document, through the same code and with the same messages.

Both overloads hash exactly these inputs under `kemap/native-authored/2`: the root digest, the authored
content digest, ordered `(patchKey, semanticDigest)` facts, `kemap/effective-resources/1`, query policy 2,
the verified asset closure hash, builder ID and version, options hash and resolver version. The content digest
(`kemap/native-content/1`) covers
placements (including explicit `Y` and `SupportBinding`), spawns, player spawns and sculpt tiles. Each entry
is normalized as scheme 1 normalizes it, ordered by ordinal id or sculpt tile, with placement display names
stripped and an empty sculpt block equal to an absent one. `MapSurfaceSemantics.RootDigest` covers globals
and surface metadata only, computable from a manifest alone, so it and scoped identity never cover tile
content. Storage paths, tile and page packing, embedding and incident index bookkeeping do not enter the
whole identity. Equivalent complete in-memory, monolithic, tiled and repacked documents share one token.
Resolver 1 continues to use `MapAuthoredIdentity`.

## Bounded scoped acquisition

Every API in this section is new in 20.30.0.
`MapScopedSurfaces.Acquire(source, scope, assetSha256)` requires `IMapSurfaceAcquisitionSource`, implemented
by `MapStoredSurfaceSource` and `MapDocumentSurfaceSource.Capture(document)`. A producer implementing only
`IMapSurfaceSource` refuses with `surface source does not support bounded scoped acquisition` before its
legacy callbacks run. Custom producers must open one pinned `IMapSurfaceAcquisitionSession` for discovery,
record-anchor expansion and bound reads. Snapshot, root and copied request stay fixed for that session.

`ReservedPatchKeys` is a detached, ordered unique-key snapshot. Every discovery key and explicitly read
key consumes the same `MaxCandidatePatches` allowance before work, including known-empty and unavailable
keys. Discovery payloads must have reservations, and extra reservations must have an unavailable fact or
known-empty coverage. Later reservations must exactly match the coordinator's requested keys. Reads must
return the requested key, present payloads must match their semantic digests, and surface lookups must
return the requested declared surface. Violations refuse instead of publishing incoherent facts.

After a key is served, `TryGetIncidentRecords` returns true with a detached list when incidence is known.
A true empty list certifies no incidents. False with null means unknown incidence, including an unread
partial-window key, and makes acquisition incomplete. Incident-only remote anchors remain required even
when ordinary references and spatial payload selection do not discover them. Missing, corrupt and
unloaded data never become known-empty facts.

The session shares candidate, directory/index visit, metadata and range allowances across all three
phases. Stored and captured implementations charge visits before cache checks, metadata before inspection,
range bookkeeping before work, and decode attempts before I/O. Metadata and range allowances are separate,
each `1 + 4 * 256 * MaxPageReads`. `PagesRead` counts successful new directory/index decodes cumulatively
on the session and as a delta on each operation. A cached traversal can exhaust its allowance with zero
decodes. Record reads and expansion depth also obey the scope's limits. Capacity or exact-overflow refusal
publishes no acquired content and disposes the session.

Effective prefab composition opens one native session per pose group beside its generated part and admits
page visits, metadata inspections and rectangle additions of every part against one common ceiling. Producers
report that work before doing it through `IMapSurfacePageVisitSession` and `IMapSurfaceWorkSession`, and a public
adapter that copies the scope forwards both. A native session that cannot report work it would share with another
part makes composition refuse before traversal with `CapacityExceeded` and a detail naming the missing capability.
Raising the query limits cannot cure that refusal. A single unobservable native session that is the only part keeps
its own whole allowance.

The returned view, `MapCoverageWitness`, `MapReadWitness` and `MapScopedIdentity` are factory-only and
immutable. They detach payloads, nested record and span lists, scope filters and asset digests. `Patch`
returns a fresh clone and no access after publication rereads the producer. Scoped identity stays under
`kemap/scoped/1` and describes the acquired facts, frame, scope and policies. `RequireComplete()` refuses an
incomplete identity. A complete scoped acquisition from a partial editing window still cannot replace a
whole current-edit identity. `CompleteView(set)` captures all resident patches and records without bound
enumeration for complete in-memory surface sets.

## Scoped native scenes

`KhaozEngine.MapDoc.Scene.MapSceneSource.Capture(documentSource, assets, limits)` captures one source generation.
It verifies the placement tile hashes while streaming metadata, including skipped sculpt values, and retains a
reverse bounds hierarchy beside the pinned native surface and foliage producers. No index sidecar or format field
is written. Missing or unloaded owner metadata refuses capture rather than certifying an empty window.

`Acquire(new MapSceneScope(surfaceScope, drawBounds, channels, MotionPaddingMetres: 0))` publishes a neutral
`MapScopedScene` only when every selected resource and dependency is available within the captured allowances.
`Surfaces` contains detached selected surface payloads and `SurfaceView` carries the acquired physical query witness.
Placements, exact transforms, paint regions, water, building intent and foliage remain separate channels.
`MapEffectiveAnnotations.Lights(scene)` and `Markers(scene)` read the captured annotations.
The complete prefab, world and foliage builders still require complete documents.

Discovery includes each placement's effective origin, descriptor render bounds, every LOD, light reach, KECL collider
and selection reach, and explicit finite nonnegative motion padding. Shape discovery streams conservative local bounds
without decoding physics objects. Origin-owned cell queries remain conservative for offset geometry.
Generated foliage bounds cover source-unit model bounds, maximum scale, root offset and support-aligned orientations.
Y is unbounded during discovery until support seating. Padding participates in scope identity.
`RequiredAssetIds` names every placement and foliage model in the source, including off-window owners.
`WithRenderExtents(proofs)` attaches verified bounds/provenance only after exact closure and complete model coverage
checks. The original source remains unchanged. Its new witness carries `RenderExtentsDigest`, which native render
consumers must require before accepting a render plan. Neutral simulation acquisitions leave that digest null.

`MapSceneWitness` distinguishes required, present, known-empty and unavailable resources. A refusal has no usable
scene, no completeness claim and a finding. Whole-world completeness additionally requires checked source coverage.
Required-domain and simulation coverage leases preserve the refusal's `Capacity` and `Findings`, including the
capacity name, aggregate demand and declared allowance when physical preparation refuses.
`scene.AcquireDependencies(scope)` uses the same immutable source generation and ceilings. It cannot refresh or
increase the captured query budgets, including all six refinement ceilings. A null refinement policy means the
default ceilings for this comparison. A manifest replacement is Stale even when the requested payloads still exist.

Source ceilings bound tiles, metadata rows, memberships, scanned bytes and retained metadata before retention.
Acquisition ceilings separately bound selected resources, patch scopes, prepared faces and peak workspace plus retained inventory.
The internal admission allowance covers stored reads, resident/generated copies, support queries and paint compilation.
Native acquisitions retain selected copies and release discarded query facts before the next request. A seat query
retains its answer and witness after releasing its view and compiled geometry. Resolver construction and final capture
have separate owners. Final inventory includes each independent patch copy, acquired-facts containers and newly owned
effective/composed source indexes. Pinned producer indexes stay borrowed. Dependency replacement admits the old and new
snapshots together, and coverage keeps every distinct snapshot until its lease ends. `MaxPatchScopes` remains cumulative
logical work even after a query releases its bytes. Internal opt-in diagnostics expose stage byte and receipt high waters
and caller/repeated-source/key counts without publishing content identifiers. These are modeled bytes, not heap or RSS.
External surface producers cannot participate in this internal protocol and scene acquisition refuses them before reads.
Released surface calls without an admission allowance keep their existing behavior and refusal order.
`MapSceneWork.PayloadReads` counts logical acquisitions, including cache hits. `IndexNodesVisited` counts hierarchy
nodes tested, including rejected siblings. The stable median tree has leaves of at most eight rows. An isolated
separated-growth query visits at most `1 + 2 * ceil(log2(ceil(N / 8)))` nodes. Dependency discoveries report their own
additional visits. Copied/prepared byte charges include intermediate work and are separate from decoded render asset
retention, which is owned by the optional rendering package's selected input lease.
Scoped water compilation results are disposable owners. Dispose a `MapScopedWaterResult` after its last
consumer. Native render batches retain their selected canonical cells independently, sharing each cell's
allocation receipt with the compiler result until the last owner releases it.

## Frame-local compiled geometry

`MapFrameLocal.ToFrame` converts a compiled patch or strip into a `MapFrameMesh` in one `WorldFrame` with
world-datum heights, and `MapFrameLocal.CompileInFrame` places an exact local patch by a `MapTransform`,
composing in double before the one frame-local float rounding. Neither takes an absolute world-float round
trip. They are new in 20.30.0, and R5 consumes them.

## Authored surfaces, caves and support

Every API in this section is new in 20.30.0. It proves geometry inputs and legacy compatibility, not a
complete cave runtime.

**Lattices and patches.** A `MapSurfaceRef` declares a surface with a `MapLatticeFrame` (exact rational cell
and height units, row direction and the world Y 0 datum), a role (`SupportFloor`, `Ceiling` or
`PaintOverride`) and a presence policy (`Native` or `LegacyTileWorld`). `MapLatticeFrame.ImportedMetreCentimetre`
is the legacy recipe: one metre cells, centimetre heights and rows running toward negative Z. Authored
vertices are `MapLatticeAddress` values, reduced rational addresses whose denominator is at most 64. Vertices
that clipping generates are exact world `MapExactXz` values, never lattice addresses. `MapExactValue` (a
reduced `long` over `long` computed through `Int128`) is the only exact scalar. Patches are sparse: a
`MapSurfacePatch` covers at most one 64 by 64 slot of cells with int32 corner heights, per-cell paint,
flags, topology and presence bits, and only occupied patches are stored. A canonical patch payload is at
most 1 MiB, a compiled patch at most 65,536 faces, and an edge subdivision 2 to 64 segments. Index pages
hold at most 256 entries and 1 MiB. `MapSurfaceCompiler.Compile` emits canonical floor or ceiling faces
keyed by `MapFaceKey`, with exact triangles and the released float arithmetic only in compiled offsets and
legacy normals.

**Mixed-resolution bounds.** A `MapSpaceFootprint` names a lower and an upper `MapBoundRef` that may sit on
different rational lattices. `MapCommonRefinement` clips them to their exact common refinement per footprint
cell. `MapRefinementLimits` budgets each cell at 16,384 source faces per bound, 65,536 pair checks,
32,768 refinement faces and 131,072 refinement vertices. Every attempted lower and upper pair is charged as a
pair check before any bounding-box rejection. One compiled bound-face context per validation or query call
is limited to 256 unique bound patches and 262,144 compiled faces, counted from cell bytes before any
compile. Those two context values are proposed operational defaults, not world limits. A refinement ends
`Complete`, `CapacityExceeded` (with no partial result), `NotRepresentable`, `Invalid` or `MissingGeometry`.
Declared seam and strip vertex sequences stay strict. Refinement never waives a matching subdivision.

**Broad-phase budgets.** `MapPartialPaintCompiler` charges `MapRefinementLimits` pair checks per broad-phase
candidate, a target face and paint source whose exact bounds openly overlap, found by an exact bounds sweep. Its
slab, conflict and occlusion checks are charged to the same pair counter. Work therefore follows the painted area,
not the whole target surface. Paint patches are counted from cell bytes and compiled first. Only target cells whose
exact lattice rectangle openly overlaps some paint source's bounds are then counted against the source and context
face limits, from cell bytes before any target compile. The context patch limit refuses at the first hit target
slot past it, before that slot is marked. A posed target keeps every cell and relies on the exact face sweep alone.
`MapPrismCoverage` and the room stage of the occupied partition count the same way. A pair check is a parent piece
and room convex part, or two peer pieces, whose exact bounds openly overlap, so a room is charged only for the
pieces near it. The visiting order is unchanged, so findings and their sorted order are too.

**Legacy lower coverage.** Only a lower bound of kind `MapBoundKind.LegacyExteriorV1`, in an `Exterior`
space with an `OpenTop` upper bound, may cover a presence-1 `LegacyTileWorld` void or NoDraw cell.
`MapLegacyExteriorRecipe.Check` returns the first failing rule in the order `legacy recipe: space`,
`upper`, `surface` and `lattice`. The lower surface must be a `SupportFloor` with `LegacyTileWorld` presence on
exactly `ImportedMetreCentimetre`, and the footprint lattice must be that surface. The policy ID is
`kemap/legacy-exterior/1`. `MapLowerCellClassifier.Classify` is the one place a lower cell is classed as
`Physical`, `LegacyNonCapture`, `KnownHole`, `UntaggedLegacy`, `Unavailable` or `InvalidRecipe`. A
`LegacyNonCapture` cell is compatibility coverage tagged with a `MapLegacyCellTag`, never a physical face, a
filled hole or a capture support. Its height comes from `MapLegacyBilinear`, which reproduces the released
`TileWorldDocument.HeightAt` arithmetic bit for bit. Missing, unloaded or corrupt data and presence-0 holes
are never covered this way, and an ordinary `SupportFloor` lower bound never accepts the fallback.

**Membership, support and relations.** Queries take a `MapFramePoint` and run over one immutable
`MapScopedSurfaces` acquisition. `MapSpaceMembership.Query` returns `Resolved`, `Outside`, `MissingGeometry`,
`Ambiguous`, `CapacityExceeded`, `NotRepresentable` or `Invalid`, and never reads missing data as outside.
`MapSupportQuery.Select` is a column query at the requested XZ under `MapQueryLimits` (256 candidate
patches, 4,096 inspected faces and 64 support intersections). It returns `Supported`, `LegacyFallback`,
`NoSupport`, `MissingGeometry`, `Ambiguous`, `CapacityExceeded`, `NotRepresentable` or `Invalid`. A
`LegacyFallback` result has no face, no normal and `IsCaptureSupport` false. `MapSpaceRelations.Relate`
reports `SameSpace`, `ConnectedThroughPortals`, `ConnectedThroughVerticalLink`, `NotConnectedWithinBound` or
`Undetermined` from a bounded breadth-first search over acquired wall portals and horizontal openings. Its
`MapRelationStatus` is `Resolved`, `MissingGeometry`, `Ambiguous`, `CapacityExceeded`, `NotRepresentable` or
`Invalid`, and every status but `Resolved` carries `Undetermined` with an empty path. Each portal fact carries
exact aperture columns whose provenance is in the acquisition witness.
`NotConnectedWithinBound` is never a disconnection claim, and `Occlusion` and `Clearance` stay
`NotEvaluated`. Membership, support and relation results are factory-only and immutable.

**Exact representability.** A reduced value that does not fit a `MapExactValue` is not representable.
Queries and acquisition return `NotRepresentable`. Document operations refuse with a message containing
`not representable`. It is never `CapacityExceeded` and never rounded, snapped or toleranced.

**Resolver version 2.** `MapResolverV2.Resolve` resolves documents whose recipe is `AuthoredBindingsV2`. It
keeps explicit Y, refuses any binding whose support is not `Supported`, and refuses a space binding without a
reference height and bounded search. `MapResolverAdoption.ConvertToAuthoredSupport` returns a detached
version-2 copy. Resolver version 1, its options and its execution are unchanged.

**Fine-patch conversion.** `MapFinePatchConversion.Convert` turns coarse regions into exact finer patches.
Each coarse cell is classified before anything mutates as `ExactCoplanar`, `ExactDiagonal`,
`UnsupportedEncoding` or `NotRepresentable`. The last two refuse unless the request sets
`AcceptAuthoredDifferences`, which lists every changed face, removed fallback and removed flag. A footprint
over a converted bound moves its wholly inside cells to the fine surface, retargets in place when every cell
moves, and otherwise splits into `<footprintId>-<fineSurfaceId>`. A moved `LegacyExteriorV1` lower bound
becomes `SupportFloor`. A footprint cell that the region boundary crosses refuses the whole conversion
atomically with `footprint straddle: footprint '<id>' cell <slotCell> crosses the conversion region boundary`.
Conversion never subdivides a footprint automatically.

**Generation pinning and acquisitions.** A `MapStoredSurfaceSource` pins one manifest generation until it is
reopened. `MapScopedSurfaces` is a further step: it publishes a detached, immutable set of acquired facts,
so no later read touches the producer, even one pinned to the same generation.

**Later rounds.** R3 owns physics sidedness, floor support and headroom, picking, placement supports, movement
integration and tiled nav capture. R4 owns flooded and dry containment and water bed semantics over R2
space geometry. R5 provides free prefab transforms, interior domains and roof links over the R2 compiler
and queries in local space, described below. R8a supplies bounded scene and asset residency, native lighting, HLOD, roof channels and
render-origin adapters in the optional [MapDoc.Render3D package](../KhaozEngine.MapDoc.Render3D/README.md).
R8b retains water optics, cave policy, interior cuts and appearance acceptance. Each must keep legacy fallback cells non-capture and emit no physics,
nav or water faces for them. R2 claims no physics, nav, water or rendering capability.

## Bounded water

The optional root `water` section (format 6, `MapDocument.Water`, a `MapWaterSet`) holds named media and water
bodies. A body has one `surfaceY`, a medium, a render material, an XZ domain of lattice rects and rings in one
`MapLatticeFrame`, and the authored spaces it covers. A present section, even one with no bodies, means bounded
water: the world is dry wherever no body covers. An absent section preserves `MapTerrain.WaterLevel` for
resolver-1 legacy global water.

A water section requires a native resolver-2 document, meaning `ResolverIdentity` payload 1 and resolver 2 with
`SupportRecipe` `AuthoredBindingsV2`. Resolver-1 documents, native or analytic, keep legacy global water.
Document validation also requires unique non-empty medium keys with `wadeSpeedScale` in (0, 1], unique body ids,
a finite `surfaceY` within 1,000,000 m that converts exactly through `MapExactValue.FromSingle`, a known medium,
a non-empty render material, a non-empty domain whose rects are non-empty, whose rings have at least 3 vertices
and whose corners lie within 1,000,000 m of the origin on each world axis, and a non-empty list of distinct
covered spaces.

One canonical codec (`MapWaterSetJsonConverter` over `MapWaterJson`) writes the section in the monolithic file,
the tiled manifest and every identity. It writes media by key and bodies by id in ordinal order, each record's
members in a fixed order, and rects, rings and covers in stored order, so reordering media or bodies changes
neither bytes nor hash. A `surfaceY` of negative zero writes as zero, so one level has one identity. Every water
object is closed, and a null medium or body refuses as a document error. Lattice addresses are `{ "x", "z", "d" }`
objects whose coordinates, like rect bounds and cover slots, are canonical signed int64 decimal strings
(`MapSignedDecimal`, the grammar the topology codec uses), and they read back through `MapLatticeAddress.Create`.
The section enters `MapDocumentHash` through the manifest globals only when present, so documents without water keep
their document hash. Format 6 moves every native root digest and every resolver-2 authored identity once, including
documents without water. Rebuild caches and navigation captures keyed on them. `MapWaterSet.Digest`, `BodyDigest`
and `MediumDigest` hash the same canonical bytes under their own domain strings.

Rectangles include minimum X/Z and exclude maximum X/Z in world coordinates, including negative Z. Rings normalize
to counter-clockwise XZ winding. An edge owns its boundary when its direction has negative Z, or zero Z and positive
X. At a vertex, +X with an infinitesimal +Z tilt must point into the interior angle: both incident edges own a convex
vertex, either one owns a reflex vertex. Where a domain edge meets a space boundary, R2 space ownership governs.

`MapWaterQuery(view, water)` captures a deep copy and digest of the set. `Sample(point)` is wet only when the point's
occupied space is covered, its XZ lies in the body domain and `lowerY <= y < min(surfaceY, upperY)` in exact arithmetic.
`Column(point)` returns wet intervals at the XZ in ascending order, each sampled at its floor. `MapWaterSample` names
the body, space, medium and lower/upper boundary ids, carries the nominal level and says whether the upper boundary
is a free surface. A level strictly below the ceiling, or an open top, is free. A ceiling tie closes the top.
Missing geometry, ambiguity, capacity and narrowing failures keep their typed status, never become dry.

Prefab water uses the captured instance pose and the shared resource transform policy. Local levels and domains
transform once, and effective body IDs and covers are already binding-remapped. Covers name authored spaces.
Covering a parent leaves an uncovered room dry. Occupied fragments retain distinct identities and lower/upper
boundary provenance when the parent recurs below and above a room. `MapWaterRegion.Fragment` and
`MapWaterSample.Fragment` name those owners. `MapCompiledWaterBody.FragmentBounds` contains their conservative
wet bounds. `ClassifyFragment(fragmentId, point)` classifies one fragment. `Classify(spaceId, point)` returns a
unanimous fragment answer, or `Unresolved` when their kinds differ. Closed precedence applies within a fragment.
`SampleExact(world)` shares the sampling core with exact membership and retains the supplied `ExactPoint`.

Effective clipping, contours, classification, bounds and digests keep wide rational geometry. Effective region
triangles are a bounded drawing adapter, and do not feed semantic predicates or hashes. Unplaced R4 compilation
keeps its released exact triangles, digests and narrowing refusals. The existing interval, clipping and height-split
kernels supply both paths. A compiled digest includes each consumed hydraulic aperture's geometry and pose,
without including the containing prefab resource SHA or the whole scoped digest. An unrelated marker edit leaves
the body's digest and its navigation medium identities unchanged.

`MapWaterContainment.Validate(view, water)` checks authored spaces only, requires positive-area contact with a
covered footprint and connects multiple covered spaces through portal or vertical-link openings whose exact bottom
lies strictly below the body's level. No implicit exterior is added. Every editor publication with water validates
the complete set, including terrain and unrelated placement edits, and every compile and build checks containment.
Document-level water rules and render material closure run on both in-memory and stored native identity paths.
`MapBoundDocumentValidation.ValidateEffectiveWater(resources, assets)` is the complete effective publication gate
for prefab, placement and roof edits. It checks material closure and requires every body to compile completely.
Identity validation remains geometry-free. Prefab and interior editor publication uses this gate before
accepting history.

`MapWaterCompiler.Compile(view, water, limits)` captures the set and emits `MapCompiledWaterBody` values in id order,
with exact up-facing region triangles split into `FreeSurface`, `Closed` and `Unresolved` at every ceiling transition,
ties closed. Each body reports its deepest bed, bounds, status, `MapWaterWork` and a digest covering its definition,
medium and the patches it read. `MapWaterCompileLimits` bounds pieces, vertices, footprint cells, bound faces, pair
checks and triangles. `MaxBoundCells` caps the cumulative bound-lattice slot and cell candidates before reads and
sweeps, including absent, empty, skipped and face-free candidates. Negative limits throw at compile entry.
Incomplete geometry remains explicit, and a capacity refusal keeps its counted work and no regions.
Under a scene admission, water compilation releases each body's workspace and each consumed bound context
before advancing. Only the escaping compiled body inventory remains charged. Effective water validation
releases that inventory after checking the publication gate.

## Storage ownership

`MapDocumentStorage.ResourceRoot(storagePath, form)` is a monolithic file's directory or a tiled document's own
directory. `MapDocumentStorage.IsReserved(storagePath, form, path)` names the paths the storage writer owns:
for tiled storage the `tiles` subtree, `map.json`, its temp file and the save lock, all swept or replaced on
save, and for monolithic storage the document file. Paths are normalized with `Path.GetFullPath`, so dot
segments resolve first, and compared with the writer's case policy (ordinal on Linux, ignoring case elsewhere).
Symbolic links and other filesystem aliases are not resolved, so a link into reserved storage is not detected.
Writer-owned native surface storage (new in 20.30.0) lives under `tiles/surfaces/`, inside the existing `tiles`
reservation. An author-owned `surfaces/` resource directory is never reserved, swept or rewritten, and R2 leaves
`IsReserved` unchanged.
`MapDocumentStorage.HoldsTiledDocument(directory)` is true only when the directory holds a map manifest, unlike
`MapDocumentFile.DetectForm`, which reports any existing directory as tiled. Native conversion uses it to accept
a directory prepared with resources.
`MapStorageGuardedAssetSource(storagePath, form)` reads under that resource root and refuses a reserved
reference before reading it. Because `MapAssetClosure.Load` reads every root, declared and transitive resource
through its source, a closure loaded through it never contains a reference whose normalized path is writer-owned.

## Sections

A map document (`MapDocument`) has:

- **`terrain`** - seed, water level, biome blend, gentle/detail noise frequency and amplitude, biome
  bands, and an ordered list of parametric features (`lake`, `flatten`, `ridge`, `rim` built in),
  resolved through an extensible `MapDocRegistry` so a game can add its own without an engine change.
  A feature carries an optional `Name` (`MapFeature.Name`, default null (null or empty means unnamed), unique when non-empty within
  the features list): the base feature type is open in the schema (only `type` is required), so no
  schema change was needed to add it.
- **`scatterLayers`** - named procedural scatter layers (cell size, jitter, per-biome density and weighted
  kind mix), one per prop type (trees, rocks, ...).
- **`companionLayers`** - named layers that ring hosts from a scatter layer with small foliage (ferns
  around trees). `HostKinds` filters which host placements grow companions: an empty or absent list
  matches every host kind in the host layer, a populated list keeps the old exact ordinal filter. This is
  a behavior-visible semantics change from earlier versions, where an empty list meant no companions at
  all: a document authored against the old behavior with an accidentally-empty `HostKinds` now grows
  companions on every host, so re-check any existing companion layer that left `HostKinds` empty.
- **`exclusions`** (`MapExclusion`) - shapes (disc/rect/polygon) kept free of scatter, optionally scoped to
  specific layers via `Layers` (null means every layer, an explicit list names only those). Builds into
  `ScatterConfig.Exclusions`. Carries an optional `Name` too (default null (null or empty means unnamed), unique when non-empty),
  added to the closed exclusions item schema as `"name": {"type": ["string", "null"]}` since exclusions,
  unlike features, are a closed structure.
- **`scatterOverrides`** (`MapScatterOverrideDoc`) - shapes that tweak scatter density and/or kind mix
  inside a region, first matching override (document order) wins. Builds into `ScatterConfig.Overrides`.
  Carries an optional `Name` too (default null, null or empty means unnamed, unique when non-empty among
  scatter overrides), added to the closed scatter-override item schema as `"name": {"type": ["string",
  "null"]}` since scatter overrides, like exclusions, are a closed structure. This is a format
  forward-compat break: a document with a named scatter override fails schema validation on engines built
  before this version, since the item schema is closed (`additionalProperties: false`). The exclusions
  `Name` field above set the precedent for accepting that break when adding a name to a closed item.
- **`placements`** - authored props/buildings: stable id, kind, position (Y optional, ground-snapped if
  absent), yaw, scale, tags.
- **`spawns`** - NPC spawn markers (archetype id, position, enabled flag, tags), interpreted by the game.
- **`playerSpawns`** - player start markers (stable id, position, yaw, enabled flag, tags). No archetype:
  which start a game uses at runtime is game code's concern. Games read `doc.PlayerSpawns` directly, the
  same way they read `spawns` (no `MapRuntime` builder).
- **`regions`** - named, tagged shapes for quest areas, safe zones, triggers, interpreted by the game.
  `MapRuntime.BuildRegions` resolves them into a point-testable `MapRegionSet` (see "Load and build").
- **`terrainOverrides`** (`MapTerrainOverrides`, format v2) - the terrain sculpt/delta layer: a sparse map
  of 32x32 delta tiles (`MapSculptTile`) at a document-chosen sculpt cell size (the block header, default
  0.5 m), only touched tiles stored. Deltas are float meters added to the analytic height, bilinearly
  sampled between cell centers at runtime. `MapRuntime.BuildField` folds them into the `TerrainField`, so
  every terrain consumer inherits authored terrain with no signature change. Absent or null means no
  sculpting (byte-identical to the analytic field). The type has a code-level authoring API (`SetDelta` /
  `AddDelta` / `GetDelta` at a global cell, `TileCount`, `IsEmpty`, `TryGetTile`, ordered `Tiles`), and
  the validating writer refuses a tile whose extent leaves the document bounds. See the sculpt section
  below.

## Schema

The package embeds `mapdoc.schema.json` (JSON Schema draft 2020-12). `MapDocumentSchema.GetJson()` reads
it, and `MapDocumentSchema.WriteTo(path)` materializes it into a game's data directory so a document's
`$schema` reference resolves for editor/AI tooling and for `KhaozEngine.Content`'s build-time schema
validator.

The tiled form needs two more, and they are DERIVED from the authored one at runtime rather than
hand-maintained: `GetManifestJson()` is the document schema without the four bucketed content lists plus
`schemeVersion`, `sculptCellSize` and the tile index, and `GetTileJson()` references the same `$defs` item
shapes the document schema owns. `WriteAllTo(directory)` materializes all three under
`MapDocumentSchema.DocumentFileName` / `ManifestFileName` / `TileFileName`. Three schemas describing
overlapping content is exactly the shape that rots, so there is only one place to edit.

Every closed structure (the document root, `bounds`, `terrain`, scatter layers, companion layers,
placements, spawns, player spawns, regions, exclusions, overrides, and each concrete shape) sets
`additionalProperties: false`, so an unknown field anywhere on them fails validation. The one exception
is deliberate: a `terrain.features` item only requires its `type` discriminator, because the feature
union is registry-open (`MapDocRegistry.RegisterFeature`), and locking its fields to the built-in set
would defeat a game's own feature type.

## Example document

A small, complete `valley.map.json`:

```json
{
  "$schema": "./mapdoc.schema.json",
  "formatVersion": 3,
  "id": "valley",
  "displayName": "The Valley",
  "tileSize": 512,
  "bounds": { "minX": -120, "minZ": -120, "maxX": 120, "maxZ": 120 },
  "terrain": {
    "seed": 7345,
    "waterLevel": -0.5,
    "biomes": [
      { "biome": "Meadow", "baseHeight": 0, "hillAmplitude": 1.5 }
    ],
    "features": [
      { "type": "lake", "centerX": 34, "centerZ": -14, "radius": 22, "depth": 6 }
    ]
  },
  "scatterLayers": [
    {
      "name": "trees",
      "seed": 1337,
      "cellSize": 4.5,
      "rules": [
        { "biome": "Meadow", "density": 0.55, "kinds": [ { "id": "pine_a", "weight": 1 } ] }
      ]
    }
  ],
  "exclusions": [
    { "shape": { "type": "disc", "centerX": 0, "centerZ": 0, "radius": 26 } }
  ],
  "scatterOverrides": [
    {
      "shape": { "type": "rect", "minX": -10, "minZ": -10, "maxX": 10, "maxZ": 10 },
      "densityMultiplier": 0,
      "layers": ["trees"]
    }
  ],
  "placements": [
    { "id": "inn", "kind": "building_inn", "x": -30, "z": 20, "yaw": 1.2 }
  ],
  "spawns": [
    { "id": "wolf-1", "archetypeId": "wolf", "x": 20, "z": 20 }
  ],
  "regions": [
    { "name": "town", "shape": { "type": "disc", "centerX": -30, "centerZ": 20, "radius": 34 }, "tags": ["safe"] }
  ]
}
```

The scatter override above zeroes tree density in a 20x20 clearing around the origin (a plaza), on top of
the disc exclusion around `(0, 0)` that keeps the same area entirely free of trees regardless of density.

## Load and build

```csharp
var doc = MapDocumentFile.Load("assets/maps/valley.map.json");
var registry = MapDocRegistry.CreateDefault();
var field = MapRuntime.BuildField(doc, registry);
var trees = MapRuntime.BuildScatterConfig(doc, "trees");
var placements = MapRuntime.BuildPlacements(doc, field);
// Both heads run exactly this, so client and server agree by construction.
```

`MapRuntime` also has `BuildTerrainConfig` (the raw `TerrainConfig`, if you want to build the field
yourself), `BuildScatterConfigs` (every scatter layer, keyed by name), `BuildCompanionConfig`, and
`BuildRegions` (the authored regions, below).
`BuildPlacements` returns `PropPlacement` values. Placement Y is ground-snapped against the built field
whenever the document leaves it null, so every head that loads the same document agrees on where an
unpositioned placement sits. An exclusion or override
applies to a scatter layer when its `layers` list is null (every layer) or names that layer, and
`ScatterConfig.ClearingRadius` is always zeroed for document-built layers, since documents author
clearings as exclusion shapes instead of the legacy single disc.

**Authored regions at runtime.** `MapRuntime.BuildRegions(doc)` resolves the `regions` section into a
`MapRegionSet`, so a game can ask which named area a position is in without writing its own shape tests:

```csharp
MapRegionSet regions = MapRuntime.BuildRegions(doc);
string? area = regions.RegionAt(player.X, player.Z)?.Name;   // null outside every authored region
```

Shapes are converted to their `IArea2D` once at build time, `Regions` keeps document order, and an entry
with no shape is skipped the same way the scatter builder skips one. Where regions overlap, `RegionAt`
returns the containing region whose shape center is nearest the point, and a shape with no derivable center
(`MapShapeGeometry.TryCenter` returning false) scores distance zero. The optional
`RegionAt(x, z, filter)` predicate skips regions it rejects. The editor's overlay picking runs on this same
resolver, so an authored region resolves identically at edit time and at run time.

## Custom terrain features

A game registers its own feature type on a `MapDocRegistry` instead of changing the engine:

```csharp
var registry = MapDocRegistry.CreateDefault();
registry.RegisterFeature("crater", typeof(CraterFeatureDoc), f => ((CraterFeatureDoc)f).Build());
var doc = MapDocumentFile.Load(path, new MapDocumentLoadOptions { Registry = registry });
```

`CraterFeatureDoc` derives from `MapFeature`, returns `"crater"` from `Type` (must match the registration),
and exposes a `Build()` that constructs the game's `ITerrainFeature`. `MapDocumentValidator` rejects any
feature `type` the registry does not know, so a document referencing an unregistered feature fails to load
instead of silently dropping it.

`MapDocRegistry.FeatureTypes` enumerates the registered discriminators in registration order (the default
registry yields `lake`, `flatten`, `ridge`, `rim`), so a tool can list the feature types it can place.

## Terrain sculpt layer (`terrainOverrides`)

`MapTerrainOverrides` is the authored sculpt/delta layer (format v2): a sparse map of
`TerrainSculpt.TileSize` (32) square delta tiles at a chosen sculpt cell size, folded into the analytic
terrain by `MapRuntime.BuildField`. It is the direct code authoring surface; the editor's sculpt brushes
(`KhaozEngine.MapEditor`) and the `ke-mapedit` `sculpt_apply`/`sculpt_flatten_region`/`sculpt_clear` MCP
verbs write through it too, both riding `PutTile`/`RemoveTile`. Author cells by global cell coordinate,
save/load round-trips deterministic tile order:

```csharp
var doc = MapDocumentFile.Load("assets/maps/valley.map.json");
doc.TerrainOverrides ??= new MapTerrainOverrides(cellSize: 0.5f);   // header cell size, default 0.5 m
doc.TerrainOverrides.SetDelta(cellX: 12, cellZ: -4, delta: 2.5f);   // raise cell (12,-4) by 2.5 m
doc.TerrainOverrides.AddDelta(cellX: 13, cellZ: -4, delta: -1f);    // add a lower delta next to it
float d = doc.TerrainOverrides.GetDelta(12, -4);                    // 2.5, or 0 where no tile covers
MapDocumentFile.Save(doc, "assets/maps/valley.map.json");           // refuses a tile that leaves bounds

var field = MapRuntime.BuildField(doc, MapDocRegistry.CreateDefault());
float h = field.SampleHeight(6f, -2f);   // analytic height + the bilinear sculpt delta
```

A global cell `(cellX, cellZ)` has its center at world `(cellX * cellSize, cellZ * cellSize)`; deltas are
meters, bilinearly interpolated between cell centers so sculpts stay smooth at any query resolution. Only
touched tiles are stored, and an absent or empty block leaves terrain byte-identical to the analytic field
(the field keeps its pure-analytic fast path). `TileCount`, `IsEmpty`, `TryGetTile`, and the ordered
`Tiles` snapshot round out the read side. The composition itself lives in `KhaozEngine.Terrain`
(`TerrainSculpt`); see `docs/design/TERRAIN-SCULPT-LAYER-DESIGN.md`.

## Rigid prefab payloads and building intent

Format 7 adds optional root `Prefabs`, `BuildingIntent` and `NativeResourceTransforms` sections. Each present
section requires resolver identity (1, 2) and `AuthoredBindingsV2`. The sections live once in native root globals,
never in tile placements. Monolithic and tiled persistence, loaded identity and stored identity use the same
canonical codec. Null sections are omitted and preserve the released analytic projections.

In 20.40.0, `MapPrefabResolver.Expand` publishes one immutable `MapEffectiveResources` snapshot using
`kemap/effective-resources/1` and query policy 2. Support, physical geometry, paint, water, membership and
annotations consume that same captured closure and effective source. Authored, effective, scoped and build
identities carry the policy, so a consumer cannot reuse a pre-R5 capture as equivalent evidence.
Expansion and authored edit candidates use validated detached structural snapshots, sharing immutable foliage.
Complete tiled worlds retain the expansion resource budgets without inheriting monolithic embedding limits.

`MapPrefabSet` payload 1 and expansion version 1 retain instances, typed bindings, overrides and transfers.
Instance poses use free XYZ, yaw radians and finite positive uniform scale. `ParentSpace` may be absent for a
parent-independent rigid prefab. Rooms, water and `$parent` occurrences require explicit authored context.
Loading, validation and closure reads never reserve new identities. Bindings carry supplied world IDs and optional
placement-only numeric IDs as exact decimal int64 strings. Removed instances and retired transfers remain persisted.

A manifest `Prefab` resource carries payload 1. `MapAssetClosure.Load` verifies every resource digest once, checks
acyclic declared dependencies, decodes bounded prefab snapshots and validates every leaf resource edge before
publishing. `GetPrefab` returns a detached copy of that verified snapshot. Prefabs have no mesh of their own and
cannot contain nested instances. Mesh, collider, selection, LOD, material, support and light resources belong to leaves.

`MapPrefabFile.Read` and `Write` use a closed codec. The payload keeps its logical asset ID. A resource ID
may be that bare ID or exactly `prefab/<escaped-logical-id>/<sha256>`. The escape is the world-ID UTF-8
percent escape, and the version suffix is 64 lowercase hex characters. Read independently verifies the
suffix against the bytes, preserving the released ID-mismatch refusal for every other identity. Children have a typed stable key, one local pose and scoped
reference slots. Explicit Surface children contain geometry with empty `Records`. Topology is persisted in separate
Record children with declared anchors. Each reference occurrence requires its own supported typed slot. A local
anchor or geometry reference must agree with the record pose. A World reference retains its standalone meaning.
Placement children use their placement pose and retain identity `LocalTransform`. Ordered tags remain ordered.

`MapSetInterior` creates a missing standalone Room and its prism in one prepared edit. The room uses
`Domain.Space.Id` and anchor, its explicit `Domain.Parent`, and empty authored tags/walls/portals/links.
Both records enter the affected write set. An existing non-alias Room keeps its fields and replaces the
prism with the same record ID. Alias rooms and non-Room spaces refuse. `MapRemoveInterior` removes
the Room and prism together, with ordinary dependency validation still required.

`MapBuildingIntent` payload 1 owns explicit roof inventory, roof links/groups, local light and XYZ marker payloads,
and standalone `MapStoreyDoc` payload-1 IDs/names. Roof inventory does not create children or reservations.
Removed local keys cannot remove same-text World references. Retained inventory entries for removed placement
reservations are inactive unless a transferred standalone producer is live.
`MapStandaloneResourceTransform` retains precise binary64 poses for unpacked resources. The prefab transfer registry
also persists placement-attached support and light descriptors, including precise owner poses and retirement.
Empty attachment, roof and storey collections and false retirement flags are omitted. `Clone` detaches mutable
collections and `Digest` hashes the same canonical bytes used by persistence.

`MapRoofPolicy.Evaluate(resources, view, observer, mode)` and `Evaluate(scene, observer, mode)` return detached
intent per inventoried roof, including unlinked shelters. `Auto` uses acquired room membership and world height.
A link matches direct rooms and exactly its attached groups, including groups spanning instances. Unrelated groups,
portals and parents never widen a link. Any eligible link may conceal its roof. Missing or ambiguous membership
keeps Auto roofs visible. Always modes enumerate the inventory. Roof preparation refusals expose the
failed byte or face reservation in `Capacity`, including the active plan window and its declared allowance. `MapRoofEvaluationBuffer.Prepare(scene)` returns a
typed result. Its admitted `Buffer.Evaluate(observer, mode)` reuses exact membership scratch and a live `Roofs` list.
Preparation selects the union of cells touched by footprint bounds, flagged indoor spans and portal sources.
Within a buffer, selected cells are compiled once from the union mask, with bound associations only for that coverage.
Cells required by indoor spans or portals keep eager exact geometry even outside every footprint. Cold validation still checks
unrelated acquired cells. Planner, validation and compiler workspace have separate lifetimes under the same byte
allowance. `Counts` exposes only numerical construction and capacity evidence, including on header or later refusal.
Exact polygons, triangles, face keys and cells live in frozen column pages. Sixteen-byte coefficient tokens cover the
entire signed Int64 range inline. Larger canonical unsigned magnitudes use ledger-owned pages, with the same
1,925-bit ceiling and construction-time normalization. Queries retain exact boundary ownership, acquired-cell first-match
rank and precision refusals. They allocate no geometry and make no admission calls.
Matching footprint geometry can share physical pages within an admission attempt. Its full key includes source and
generation, closure, record and lattice versions, effective instance path, exact pose, lattice, ordered slots and
compilation policy. Equal counts never establish identity. Selected cell products and 32-byte frozen bound rows remain
private to their view. Bound status, failure detail, compatibility and opening side tables are fully inventoried.
Each buffer owns its mutable stamps, active rows and fixed-limb registers. Retiring one buffer preserves every other lease.
The final footprint lease releases its page receipts, window attribution and cache entry together.
`RetainedBytes` describes a buffer's reachable obligation, including borrowed scene inventory. `SharedCapacityBytes`
reports footprint leases and `PrivateCapacityBytes` reports private products and query scratch. Root accounting deduplicates
physical page identities and includes cache controls, so buffer totals must not be summed as root inventory.
A borrowing window carries a footprint's full obligation once across all buffers in that window and its descendants.
Nested and sibling borrowers share each ancestor's attribution. Each window releases it only after its last
borrowing consumer retires, independently of retirement order. Refusal and cancellation preserve earlier leases.
`GeometryArenaPages`, `GeometryArenaCapacityBytes`, `FrozenBoundRows`, `FrozenBoundCapacityBytes`, coefficient pool
capacities and side-table counts expose actual layouts, including array headers, directories and unused page capacity.
Borrowed-input preflight stays allocation free and runs before the interval when the header fits. An input or
precision refusal there returns only its typed result. A header refusal takes precedence and includes interval
evidence without creating product workspace or throwing a capacity exception.
`HeaderBytes` covers borrowed-input controls. `CompleteAggregatePredictionAvailable` is false. Each owner admits
its next justified capacity before allocation or growth, with old storage charged until its replacement is ready.
`Membership`, `Header`, `Validation`, `Planner`, `Construction` and `Publication` report interval observations.
They start before their reservations, and membership ends after publication or unwind. These observed peaks
describe admitted work and are not before-work aggregate predictions or additional reservations.
Each stage reports `Root` and `Windows`. Root totals include already live workspace, retained products and external
obligations. Window totals include that window's reused and referenced obligations, preserving shared attribution.
`BindingWindow` is the observed window with the least headroom at its largest requested or admitted total.
`BaselineBytes` records the live starting obligation. `RequestedBytes` is the largest pre-reservation total,
`AdmittedPeakBytes` excludes rejected increments, and `RefusedBytes` separately records the largest refused total.
`LargestIncrementBytes` records the largest numerical next-batch envelope. None of these interval values inherits
an earlier historical ledger peak. Planner, compiler and validation output counts describe the actual built outputs.
Patch face grouping admits both local and transformed copies plus the active grouping's growth and sort workspace
before allocation, retaining that owner through the final cell. Validation releases its compiled root context after
the raw pieces have copied their bound faces. Room candidates retain one sorted piece row per parent consumer.
Prism compilation admits domain outputs separately from arithmetic sized to its largest input piece. A room's
second compiled domain and convex descriptors remain charged until room construction ends. Clipping workspace
covers only its active parent and convex piece. Uncovered replacements admit old and new storage together,
then settle to the surviving polygons after replacement. Refusal preserves the earlier fragment generation.
`ValidationPeakBytes` records every successful ledger reservation during validation, including external obligations.

The four intent flags have separate consumers. Hidden roofs have `ColourVisible = false`, `CastShadows = true`,
`CameraBoomBlocks = false` and `SkyPickingBlocks = true`. Visible roofs set all four true. This policy never
changes collision, interaction geometry or static registration. `NativeMapRoofs` and `MapPresentationPicker`
consume those channels in MapDoc.Render3D. R8b adds cut-aware classification.

Typed Light and Support resource payloads begin with the discriminant `"resourceSchema"`, valued
`kemap/light-resource` or `kemap/support-resource`. Their writers emit it first and their closed decoders require it.
`MapAssetClosure.Load` decodes only bytes that carry it, so every other resource, JSON or not, stays opaque as
released. Prefab leaf lights, typed support and Physics support refuse an opaque resource with a message naming it.

`MapEffectiveAnnotations.Lights(resources)` and `Markers(resources)` return immutable records in stable ID order.
Both carry their own payload version. Light positions and radii are owner-frame metres, independent of leaf
`SourceUnitsToMetres`. Expansion composes the precise owner pose once, freezes the position once and scales the
radius once in binary64 before rounding to float. Colour, intensity and shadow intent remain unchanged.
`LightResourceIds` use the existing scoped and bound implicit Light children. Explicit children coexist under
distinct keys. Materialized transfers reuse the captured descriptor frame without composing the owner again.
Markers retain exact XYZ, yaw, name, role, enabled state, ordered tags and optional instance provenance. They do
not become `MapSpawn`. R8 renders lights.

Light payload 1 accepts optional `shadowExclusion` on `MapLocalLightDoc` and retains it on
`MapEffectiveLight`. It is an additive field in format 9, including monolithic and tiled building intent,
prefab Light children, typed leaf light resources and materialized attachments. No format or payload
migration is needed. Omitting it preserves released bytes, identities and the native owner render/LOD-bounds
box. An unowned light without a declaration has no exclusion.

The declaration is one closed choice:

- `{"kind":"None"}` requests a static shadow with no exclusion.
- `{"kind":"NearSphere","radius":0.625}` excludes a sphere around the emitter. Radius is finite,
  nonnegative world metres, independent of owner scale.
- `{"kind":"FixtureBox","min":{"x":-1,"y":-1,"z":-1},"max":{"x":1,"y":1,"z":1},"margin":0.125}`
  requires an owner placement and finite corners strictly ordered on every axis. Corners are metres in
  that owner's local frame, independent of asset source units and of any explicit light frame. Submission
  transforms all eight corners by the effective owner pose, then grows the world-axis-aligned envelope
  by the finite nonnegative margin in world metres. The margin does not scale with the owner.

Only the selected kind's fields are accepted. `CastShadows = false` always submits no shadow. Explicit
exclusions participate in annotation, render replacement and capture-lighting identity. Null declarations
are omitted and have the same fallback as an absent field. Record edits, prefab expansion and extraction
retain the declaration without converting its owner-local corners or scaling its world-metre settings.

### Marker payloads and height modes

Format 8 keeps marker payload 1 byte for byte and adds payload 2, used by world markers and prefab Marker children
alike. Payload 1 keeps its height in `Position.Y`. Payload 2 requires a closed `heightMode`. `Explicit` keeps
`Position.Y` as the only height authority and never resamples. `Support` carries a `supportBinding` and stores
`Position.Y` exactly +0, so an authored Support marker carries no height. A Surface binding names only `surfaceId`.
A Space binding names `spaceId`, a finite `referenceY` and finite nonnegative `searchBelow` and `searchAbove`. Nothing
is defaulted. `MapMarkerHeights.Apply(marker, height)` writes the one canonical encoding of a `MapMarkerHeight` choice.
An optional nonempty `archetypeId` is caller vocabulary.

Expansion resolves a Support height through the shared seat query that support-bound placements use, after the
placement seats are installed. A prefab-local or framed standalone binding is stored frame-local and passes through
its frame once. The effective Y is the exact value of the query's float `WorldY`, and only Supported resolves. A
removed or unsupported owner refuses the load, build or edit, disabled markers included. Effective markers carry
value-only `HeightProvenance`, with the mode, the world request, face, space, via record and float height for Support,
and the mode alone for Explicit. Payload-1 markers carry none. The witness-bearing answers live separately in
`MapEffectiveResources.MarkerSupports`, one row per Support marker in ID order. A document whose effective markers
are all payload 1 keeps the annotation digest scheme `kemap/effective-annotations/1`. Any payload-2 marker selects
`/2`. `MapMarkerSpaces.Query(view, marker)` answers occupied space and parent chain by point membership, Outside when
no authored space holds the point, and keeps every refusal status. Outside needs acquired coverage. A point beyond the
view's acquired XZ extent, for example a marker past every patch of `CompleteView()`, has no geometry to certify
Outside and returns MissingGeometry. Uncovered scope refuses, while covered ground that no authored space owns is
Outside.

The shared seat query acquires a 2 m scope around each point. A complete acquisition reads every patch that meets
that scope, so a floor it captures only through records incident to another patch, or through an empty neighbouring
slot, holds no cell at the point when its own slot was never read, and the query skips it. A recorded unavailable read
still refuses with MissingGeometry. Support-bound markers and placements on sparse floors therefore seat on their
named owner, and `MapPrefabResolver.Expand` and native resolution agree. `SeatSupportSlotTests` pins both cases.
Completeness is relative to the acquisition's own filters. The seat query acquires every role with no space or height
filter, so a skipped slot holds nothing at the point anywhere in the world. A caller that filters roles, spaces or
height gets an answer scoped to that filter and must acquire the geometry its support search needs.

### Marker names, lookup and typed views

Effective marker IDs and names are global, ordinal and case-sensitive. A name is nonempty, editable and unique across
world markers and every prefab-derived marker after current overrides. IDs are immutable. Disabled markers take part
in both checks and removed children do not. Closure-bound validation of a complete document, on the loaded and the
stored path, refuses a duplicate effective ID or name from root globals and verified prefab payloads before any patch
read, support query, callback or static installation. Names are never normalized or suffixed. A repeated
marker-bearing prefab gives each copy's marker its own name through an explicit replace override.

Compatibility: duplicate marker names were accepted before R6. A document whose repeated prefab instances share a
marker name now refuses to load and build until each repeated instance carries such an override. Format 8 moves every
native root identity once, as format 7 did, so identity-keyed caches and navigation captures rebuild after migration.

`MapPlacePrefab.InitialOverrides` lets one placement request carry keyed overrides, such as that marker rename, so a
second instance is accepted in a single placement with no intermediate duplicate. Duplicate keys refuse before any
binding is allocated. The overrides persist only as the instance's `Overrides`. The `prefab_place` tool accepts them as
the optional `overridesJson` array.

`MapMarkerRegistry(markers)` is an immutable lookup over effective markers with `All`, `Enabled`, `FindById`,
`FindByName`, `ByRole` and `ByTag`, every list in ordinal ID order. `InRect(rect, enabledOnly, membership)` returns the
markers whose exact XZ lies in the half-open `MapMarkerRect`, minima included and maxima excluded on both axes,
negative coordinates included. A median-split point hierarchy with outward-rounded node bounds narrows candidates
before the exact test, and the membership delegate runs once for each returned marker. Each `MapMarkerMapPoint` keeps
the delegate's status, refusals included, so a rectangle never claims complete space membership. Non-finite or
reversed bounds refuse and a zero width or depth holds no marker.

Roles are caller vocabulary and the engine defines none. `MapMarkerRoleCatalog(roles).Validate(registry)` reports one
`unknown-role` finding for every unlisted marker in ID order, disabled ones included, without making a document
invalid. `MapMarkerProjection.PlayerStarts`, `Spawns` and `Landmarks` wrap each effective marker of one caller role and
keep its exact XYZ, yaw, ordered tags, enabled state, name, ID and height provenance. A spawn carries a payload-2
archetype, or null for a tag-driven spawn. A landmark is a point and adds no region, disc or geometry.

### Marker window anchors and native readouts

`MapMarkerAnchors.Read(manifest, assets)` reads each marker's world XZ, enabled state and role from root metadata and
verified prefab bytes, through the same enumeration and frame kernel that expansion uses. It reads no support, terrain
or tile body, resolves nothing and certifies no whole-world identity. A missing prefab refuses.
`MapMarkerAnchors.Select(anchors, role, centre)` is pure. It keeps enabled anchors whose role equals the caller's
ordinally, orders them by squared distance to the centre over binary64 differences, then by ordinal ID, and returns
null when none match. The engine never guesses a role. `MapMarkerConsumerOptions(Roles, LegacySpawnSurfaceId)` carries
the caller's `MapMarkerConsumerRoles` and the one Surface owner for unadopted legacy spawns. Nothing is defaulted.

`MapNativePointReadout.SurfaceAt(resources, point, surfaceId)` samples the named Surface owner at an exact XZ through
the shared seat query, with no reference height and the default scoped budgets. Every status stays typed, and only
Supported carries a height. An exact overflow returns a typed NotRepresentable result with an incomplete witness. It
never falls back to another floor, a zero height or the analytic field.

`MapPrefabExpansionLimits` supplies finite collection and payload ceilings before codec materialization. Effective
geometry composition, aggregate expansion charges, remapping and editor lifecycle commands are separate stages.
This payload layer adds no rendering, physics or editor dependency.

## Pure prefab and interior preparation

`MapPrefabEdits.Prepare(document, assets, edit)` returns a detached `MapPrefabEditResult` with `After`, a named
`MapNativeWriteSet` and `MapNativeEditEffects`. Place, move, keyed override, child restore, remove and unpack prepare
one complete candidate against the matching captured closure. The input document and allocator never advance.
New numeric reservations belong to the candidate until publication accepts it. Remove overrides retain `Previous`,
so restore preserves customization and reuses the original typed and numeric bindings.

`PrepareAssetUpdate(document, beforeAssets, afterAssets, edit)` reads both pinned definitions explicitly and updates
every live referring instance in one candidate. It selects `afterAssets.Roots` in `After.NativeAssets` and marks the
native asset write. Removed source tombstones and their transferred descriptors are historical owners and remain
unchanged. The caller publishes immutable asset bytes before accepting the document and closure switch.

`MapUnpackPrefab` moves every live child to its standalone home with the same world ID and numeric ID. It retains
precise binary64 poses, authored nullable-Y dispositions, explicit storeys with standalone ownership, authored
roof inventory and scoped references. A standalone support request is always world space and resolves as released,
whatever the placement scale. Only a prefab-local request transforms by its placement's pre-seat frame, so unpack
persists that world-space request, never one derived from the seated floor height. Extraction inverts a standalone
request into the extracted child's frame at the asset origin. The source keeps its complete binding inventory and persists `unpacked: true`.
This optional flag distinguishes transferred inventory from ordinary deletion and is omitted otherwise. Removed bindings
are dormant reservations, while every other entry requires its matching transfer. Validation uses this persisted
inventory without enumerating the current prefab or dependency versions, and counts each identity once.
Transfer rows capture each support and light edge, its verified
resource pin, live children, record anchors and owner-relative poses. Empty rows suppress the whole advertised edge.
Standalone expansion validates and reuses these descriptors without reading or regenerating the attached payload.

`MapPrefabExtraction.Extract(document, assets, selected, origin, assetId)` refuses partial room, floor, roof or
materialized-owner selections. Selected references become explicit Local slots and external references remain World.
Explicit owned lights retain owner-relative transforms in either scope. Unowned lights rebase against the asset origin.
Captured attachments become asset-local materialization rows with precise rebased owner frames, captured definitions
and fresh local descriptor pins. Extraction preserves edited support heights and emissions rather than reading their
original leaf payloads. `MapPrefabAssetEdits.Replace(asset, child, remove)` prepares immutable add, replace and remove
edits through the same captured-frame reconciliation. Value-only replacements preserve precise frames, and removal
keeps an empty suppression row. Local history restores the captured asset snapshot and its mapping.

`MapInteriorEdits.Prepare` handles standalone prism domains and roof links/groups through the same complete candidate
gate. Preparation validates authored parents, room coverage and complete effective water containment. Effects name
instances, children and the effective roof, light and marker inventories. Empty additions are omitted from `Describe`,
and the released positional write-set and effect constructors remain unchanged. Physical bounds and editor publication
are later stages. These APIs have no editor, tool, rendering or physics dependency.

`MapMarkerEdits.Prepare(document, assets, edit)` prepares one standalone marker edit through the same detached gate
and returns a `MapPrefabEditResult`. `MapSetMarker(marker)` sets the marker whose immutable ID matches, or creates it.
The name is an editable member and never a key, so a rename keeps the ID. `MapRemoveMarker(id)` removes one standalone
marker. A set takes the marker in world terms, the form `MapEffectiveResources.BuildingIntent` publishes. An unframed
standalone marker stores it as given. A framed standalone marker, such as one transferred by unpack, keeps its precise
frame verbatim and stores the frame-local point, yaw, height and binding, so expansion applies the frame once. A
component whose world value is unchanged keeps its stored local bits, so a metadata edit never rounds them.

Preparation refuses a resolver (1, 1) document and names resolver adoption through
`MapResolverAdoption.ConvertToAuthoredSupport` as the prerequisite. A live prefab marker belongs to its instance, so
set and remove refuse with `prefab_override` guidance. An ID held by any marker reservation, live or dormant, refuses
unless it is the active transfer of the standalone marker being edited, so a set never seizes a retired reservation.
Removing a transferred marker retires its transfer and removes only its own frame. Payload rules, unique IDs and
names, binding owners and every support-bound height are validated before the result exists. `MapPrefabJson.ReadMarker`
is the closed decoder the tool uses, sharing the prefab child codec and binding converter.

Marker effects come only from `MapMarkerEffects.Describe` over the before and after expansions. A metadata edit names
the marker and any rename row with no bounds and no invalidation. A spatial edit adds both point boxes with Residency.
Markers never add Physics, Nav or Unbounded. The write set names the building intent, plus the resource frames or
prefab transfers only when a removal changed them.

`MapMarkerAdoption.ConvertSpawns(document, choices)` adopts both legacy lists on a detached, complete resolver-2
native document. Choices are keyed by `MapLegacySpawnKey(Kind, Id)`, where `Kind` is `Spawn` or `PlayerSpawn`, so the
same source ID in both lists stays distinct. Every source needs a `MapMarkerAdoptionChoice(MarkerId, Name, Role,
Height, YawRadians?)` and extra choices refuse. Destinations must be distinct and cannot reuse live or retained
marker IDs. Each `MapMarkerHeight` explicitly chooses Y or a support binding. The pure conversion encodes the height
without sampling support. Bound command preparation validates the complete closure, names and support before acceptance.

Adoption preserves XZ, enabled state, ordered tags and the NPC archetype. Without an explicit yaw choice, NPC yaw is
0 and player yaw is copied. Both source lists clear only after every conversion succeeds. Partial and analytic
documents refuse. Resolver (1, 1) requires `MapResolverAdoption.ConvertToAuthoredSupport` first. Old native legacy lists
still load and save, but native editing changes them only through adoption and its exact captured undo restoration.
`MapNativeWriteSet.LegacySpawns` publishes both lists together and does not itself authorize a legacy-list change.

## Native foliage consumer contract

Format 9 adds optional root `foliage` payload 1, independent of analytic scatter, companion layers and explicit
placements. `MapFoliageMigration.Upgrade` is a pure 8 to 9 version-only migration. Absent foliage stays absent and
released prefab payload 1 and annotation digests keep their contracts. The format advance moves native root digests
and resolver-2 authored identities once even without foliage. Rebuild identity-keyed caches and navigation captures.
Foliage edits subsequently change authored/whole identity and foliage dependencies, never physical world bounds,
statics, navigation captures or annotation identity.

### Payload, storage and refusal

Each `MapFoliageLayer` binds explicitly to a Surface or Space through `MapSupportBinding`, with seed, spacing,
scale range, root offset, ordered weighted models, visible materials, policy, clearance and a sparse byte raster.
Raster row 0 starts at `originZ`, X advances within a row and rows advance positive world Z. Each immutable
`MapFoliageChunk` is 32 by 32 bytes, indexed by `MapFoliageChunkKey`. Missing chunks read zero. Advertised unavailable
chunks refuse. Boundary padding is zero, including last-row padding, and an explicit all-zero chunk retains authored
identity. `CopyDensity()` returns detached bytes.

| Payload 1 ceiling | Value |
| --- | --- |
| Width and depth | 1,048,576 samples each |
| Stored chunks per layer and region memberships per chunk | 16,384 and 4096 |
| Layers and document decoded density bytes | 256 and 67,108,864 |
| Models and materials per layer | 256 and 65,535 |
| Clearance rules, exception masks and owner correspondences per layer | 4096, 4096 and 65,536 |
| Foliage JSON and base64 chunk token | 134,217,728 UTF-8 bytes and 1368 characters |

The closed codec checks counts, sizes and arithmetic before allocation. Monolithic storage embeds density.
Tiled storage writes one verified content-addressed pack per (layer, storage region), with logical chunk digests
independent of pack layout. The stored schema accepts pack references, the inline schema accepts inline bytes, and
`VerifyTiled` plus `map_validate` check every advertised pack and its memberships. Sparse addressability is not a
promise of full-area allocation. Partial storage saves preserve unloaded chunks and verify their advertised data.

`MapFoliageGenerationContext.Capture(document, assets, limits?)` admits construction separately, expands effective
resources once and detaches one complete snapshot. Reuse it across `MapFoliageDistribution.Generate` and `Query`.
`MapFoliageContextLimits` defaults to 134,217,728 source UTF-8 bytes, 65,536 chunk descriptors, 65,536 placements,
65,536 surface patches and 4096 paint targets. Construction is counted before expansion, cloning or index staging,
alongside the released prefab expansion limits. `MapFoliageContextWork` reports charges and actual expansion and
snapshot-serialization counts.
Complete capture requires all terrain, content and foliage resident. Partial snapshots return `PartialResidency`.
`MapFoliageGenerationContext.CaptureWindow(sceneSource, sceneScope, limits?)` instead publishes a distinct
`MapWindowFoliageContext` with immutable `SnapshotId`, `ClosureHash` and a complete dependency `Witness`. Its
`MapFoliageDistribution.Generate` and `Query` overloads share the released input and evaluation kernels. Generation
chunks include adjacent owners whose conservative model reach enters the draw bounds, including motion padding.
Native foliage preparation consumes that same admitted chunk set. Requests for other chunks refuse `Unavailable`.
Support scopes use the same derived read reach as complete generation. Actual rounded bilinear density reads include
advertised absent zero chunks, while unavailable or stale inputs refuse atomically. Off-raster chunks bind raster
facts alone. Context ceilings apply to selected input bytes and descriptors beside unchanged scene and point-query
limits. A window context retains its admitted inputs and never reconstructs a partial document for a complete builder.

Only `MapFoliageStatus.Complete` publishes usable samples. MissingSupport, Unavailable, PartialResidency, Ambiguous,
CapacityExceeded, NotRepresentable and Invalid refuse without truncation, a substituted floor or budget inflation.
Outside a known domain produces no sample. `MapFoliageLimits` retains per-chunk ceilings. Query also caps total samples
at 262,144 and retained bytes at 67,108,864 before retention. `MapFoliageDifferential.Compare` caps total candidates at
64,000,000 and changes at 4,000,000, with reconciliation bytes at 134,217,728 per stable 64 by 64 lattice-address block. Byte charges are
conservative accounting, not measured allocation. Generate, Query and Compare observer rows remain provisional until
the enclosing result is Complete. A refusal discards all usable output and issues no differential token.

Retained foliage inventory counts shared objects by reference identity. Full diagnostic samples include their
sample objects, reference arrays, material wrappers, policy and UTF-16 strings once per owned graph. Public sample
construction copies the material reference array. Native column inventory separates seven aligned instance arrays
and two ordinal reference tables from batch controls and metadata. Construction inventory additionally includes
the still-live builder page during freeze, rather than folding that overlap into the settled per-row price.

The internal compact generation product retains original matrix, position and thinning-rank columns, lattice X/Z,
and 32-bit support/model indexes. IDs receive ordinals on their first accepted occurrence. It shares the full
generator's candidate scan, support and clearance evaluator, decomposition check and dependency digest. Full
`Generate`, `Query` and observer APIs continue to expose complete diagnostic samples. A one-shot builder admits
accepted-row, ordinal-index growth and frozen-array overlap before allocating them, then releases its workspace.

`MapNativeFoliageBatch` owns resident foliage data for its prepared render plan. Residency disposes the old plan
when a replacement is accepted or its window unloads. After batch disposal, `GeneratedIdentities`, `CoverBatches`,
`Register` and `Record` throw `ObjectDisposedException`, even if another plan or the cache still owns the same
columns. Capture any comparison data before requesting replacement, for example
`var identities = plan.Inventory.Foliage.GeneratedIdentities.ToArray();`. Public identity and cover snapshots,
and foliage payloads emitted by public `Register`, are detached and remain usable after disposal. Retaining the
disposed batch does not retain a per-row identity copy or keep its resident columns alive.
`MapRenderResidency.Record` detaches foliage at the public sink boundary. A normal recording followed by
`MapDrawRecorder.Snapshot` remains usable after replacement or unload, without pre-registering the plans.

### Compatibility support and paint

Payload 1 supports `legacy-tile/1` with explicit boundary/material metadata, resolver identity (1, 2) and
`AuthoredBindingsV2`. It preserves legacy interpolation, smoothed global-corner central-difference normals,
and additive NoDraw triangles. NoDraw and Blocked flags do not veto density under this compatibility policy.
Full-request prepared support selection is shared with the
seat query. A later geometric policy needs its own version and differential. Equal-priority world paint chooses the
ascending ordinal ID winner. The native renderer preserves that winner independently of draw order.

Both binding kinds normalize to the selected support surface owner. Occupied Space is a separate field. Clearance
rules apply to effective placements after their effective transform once. Their seated support, not a roof's clearing
target or geometric Y, supplies applicability through the layer's compatibility ordinal table.

| Owner seated support | Solid and Door | RoofAbove |
| --- | --- | --- |
| Owner and layer have mapped ordinals | Equal ordinals only | Owner ordinal strictly greater than layer ordinal |
| Owner support is unmapped | Canonical support surface identity must match | Never matches |

`MapFoliageOwnerSupport` supplies exceptional typed seated bindings without clearing geometry. Asset ID and placement
tag rules share the same per-owner applicability even for mixed owners. Solid/roof clearing remains cell-based,
DoorClearance scales once and EdgeFade remains world metres. The one-metre interaction envelope is never foliage
geometry. Explicit trees, shrubs and flora retain placement IDs and collision independently of density.

### Edits and invalidation

`MapFoliageEdits` and `MapFoliageBrush` return pure proposals. A policy definition change needs its
`PreviewPolicyChange` token, bound to the exact committed candidate, so publish a policy change alone.
A new layer requires no differential and its tool dry run refuses. Same-layer advertisements can carry density only
when their keys and digests match the before document's resident chunks. Brush density is 0 to 255 with ToEven rounding,
hardness 0 to 1, at most 64 chunks and 65,536 addresses. Hardness 1 includes the radius at full strength. Lower hardness
has zero weight on that boundary. Refusal is atomic and allocates no accepted history entry.

Complete document transactions use public `MapDocumentEditFinalizer`, shared by pure consumers, MapEditor commands,
callbacks and wire paths. `MapNativeInvalidation.Foliage` is 256 and never adds Physics, Nav or Residency.
`Finalize(before, prepared, assets)` uses one closure. Its four-argument overload takes separate before/after closures
for an asset switch. Both admit the proposed foliage context against its target closure before expansion or stamp reuse,
including whole-layer and chunkless changes, then validate the candidate and preserve every other effect and write.
`MapFoliageEffects.Describe(before, after, oldContext, newContext, trigger)` returns
`MapFoliageEffectSet(Chunks, Layers)`, with contexts captured from those exact snapshots and closures.

`MapNativeEditEffects.FoliageChunks` contains per-chunk rows only when a side is added or removed, its bounds change
or its digest changes. When preparation completes, a side's digest equals `MapFoliageSampleBatch.DependencyDigest`,
including the raster-only digest off-raster. Deterministically refused preparation instead carries the refusal digest
from `MapFoliageDependents`. It binds `kemap/foliage-batch-refused/1`, the layer ID, batch chunk X/Z and refusal status,
plus the density chunks read by float bilinear sampling over points owned by the rounded binary32 generation rectangle.
This can include the preceding density chunk at a rounded lower edge. Each present read position contributes
a digest of its chunk address, density SHA-256 and canonical authored layer settings excluding the chunk list.
Missing positions contribute `absent`. A refused batch from `MapFoliageDistribution` carries an empty
`DependencyDigest`, so consumers must check status and never compare a refused effect side to a refused batch by digest
equality. Bounds are cosmetic XZ candidate extents, not padded model bounds. `FoliageLayers` names layers
whose every batch is stale. Entry presence is authoritative even when before/after digests are equal, since those
digests describe stored settings/chunks only and a dependency selection past the per-edit ceiling degrades to an entry.
Whole-layer settings are policy, seed, spacing, density settings, raster geometry, models, materials, clearance rules,
binding, clearance flags, edge fade and door clearance. Raster chunks, masks, compatibility ordinal rows and owner
correspondences are localized. A layer appears in at most one list.

### Padded immutable R8 handoff

`MapFoliageModelExtent(AssetId, BoundsInSourceUnits, SourceUnitsToMetres, MotionPaddingMetres)` takes
`MapResolvedAsset.RenderBounds` and `SourceUnitsToMetres`, plus explicit finite non-negative caller motion padding in
metres. Zero is valid only when supplied. `MapFoliageRenderHandoff.Create(batch, extents)` returns
`MapFoliageRenderBatch(Samples, Status, Bounds, DependencyDigest)`, without renderer or GPU types. Samples retain keys,
canonical support and occupied space, exact CPU matrices and poses, ordered material references, visible material,
model, scale, thinning rank and completeness. Lists and nested values are detached or immutable.

For each sample, multiply all eight source-box corners by source units exactly once, transform by the original CPU
matrix, then expand XYZ by that model's motion padding. Never rebuild a matrix from the decomposed quaternion or
apply the instance scale twice. Bounds union those padded boxes. Empty complete batches have null bounds. Missing or
duplicate extents, invalid units/bounds, nonfinite or negative padding and nonfinite transformed corners return Invalid
with null bounds and no usable render digest. Incomplete source status propagates. The render dependency digest binds
the distribution digest and the used model extents, units and padding in ordinal asset order. Unused model extents do
not broaden invalidation. It is separate from distribution identity and never resamples or changes CPU selection.

Styling remains caller policy. Preserve live blade/shrub motion profiles and shadow flags. Use dependency digest plus
generation to reject stale work. Keep one owned batch with every residency membership intersecting its padded bounds
and release it only after its last membership leaves. R8a supplies windowed generation, residency, upload,
viewport drawing and snapshots. R8b owns interior cuts and appearance, with G3 acceptance separate.
The bounds are cosmetic and never union into world/nav bounds or static descriptors.

### Public foliage acceptance and deferred ownership

| Evidence | Contract proved |
| --- | --- |
| `NativeFoliageAcceptanceTests.StackedLayerFoliageAndExclusions_RemainInOwnedDomain` | OA9 owned stacked support and exclusions |
| `NativeFoliageAcceptanceTests.SurfaceDensity_DoesNotPopulateDeepCave` | OA9 exterior density stays out of the released prefab cave topology |
| `NativeFoliageAcceptanceTests.CaveOwnerTransform_InvalidatesOnlyDependentSamples` | OA9 transformed cave ownership and narrow effects |
| `MapFoliageWorldTests` | Independent client/server heads in monolithic/tiled forms, unchanged statics/nav/world bounds and collider builder type boundary |
| `FoliageAcceptanceWorkflowTests.StackedDomains_WireEditsInvalidateOnlyTheirOwnLayer` | Shared commands, real MCP edits, owner isolation and history |
| `FoliageWireTests.FoliageWire_ContainsEveryByteQuaternionAndMatrixComponent` | Reachable generated pose and every wire component |
| `MapFoliageHandoffTests` and `MapFoliageHandoffBoundaryTests` | Exact CPU matrices, padded cosmetic bounds, immutability and physical isolation |
| [foliage-conformance](../.github/workflows/foliage-conformance.yml) | Fixed public platform pins and trace on ubuntu-26.04, macos-26 and windows-latest, nonzero totals, no skipped tests |

Scale/rank bits, keys, models and decisions compare exactly. Position tolerance is 0.00001 m per component and
orientation tolerance 0.000001 radians. The workflow does not regenerate literals or run the private harness.
The private comparison boundary and its eight test-only references are defined only in the
[oracle harness README](../KhaozEngine.MapDoc.OracleHarness.Tests/README.md). That harness is not an importer or actual
source acceptance. Public CPU proofs certify no pixels or windowed acceptance.

| Owner | Delivered capability or remaining proof |
| --- | --- |
| R8a, R7-R1 | Delivered `MapWindowFoliageContext`, immutable scoped closure/completeness and full-request support semantics |
| R8a | Delivered native residency/upload, viewport drawing, snapshots and independent source roster/submission comparison |
| R8b and [#1474](https://github.com/APKiwiOrg/KhaozEngine/issues/1474) | MapDoc interior-cut adapter |
| R8b and G3 | Appearance with live motion profiles, styling and shadow flags |
| R9 | Interactive painting and native GUI input |
| R9 and [#1516](https://github.com/APKiwiOrg/KhaozEngine/issues/1516) | Authoring decision for space footprints left over removed floor cells by presence edits |
| R10 | Complete verb parity and native placement ID rename wire verb |
| R11 and G2 | Actual-source refreeze, importer and source acceptance |

## Public synthetic village acceptance

`PrefabFixtures.Village()` in `MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs` is invented public data.
Four payload-1 definitions, hut, tower, bath and canopy, produce five instances with two huts. Free
XYZ, yaw 0.371 and scales 0.8 and 1.2 compose support-resource floors, two storeys, authored room
parents, AuthoredOpen portals, a cross-instance roof group and an inventoried unlinked canopy roof.
The bath supplies optional local water. One hut has an explicit extra-furniture override. Notched
ground paint subdivides unchanged canonical faces. Lights and XYZ markers retain versioned fields
and ordered tags. The canopy requires no Room or ParentSpace.

`MapPrefabVillageTests` checks tiled reload, strict closure pins, single leaf ownership, fixed wet/dry
and room probes, and complete scoped witnesses. Support acquisition captures known-empty local slots
for offsite floors introduced by incident records. Those reads share the existing patch allowance,
so missing geometry remains a refusal and a limit overflow publishes no partial owner. Format 7
moves native root identities once. Resolving and reloading allocate no new bindings.

The independent-head acceptance lives in `MapDoc.Physics.Tests`, and the shared command plus real
MCP workflow lives in `MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs`. These tests consume
verified bytes and separately loaded documents. They supply headless acceptance for these seams.

### Public marker village acceptance

`MarkerFixtures.Village()` in `MapDoc.Tests/Markers/MarkerVillageFixtures.cs` is an invented native village over
the released room prefab, placed at yaw 0.371 and scale 1.2, and stacked prefab-cave geometry. It holds six uniquely
named markers. `start` is Surface-bound to the authored exterior at negative X. `actor` is Explicit with an archetype,
above every static. `cave-explicit` sits in the deep room and `cave-bound` is Space-bound to the high cave. The
prefab's payload-1 child is renamed and raised above its roof as `roof-label` by a replace override, and the payload-2
local child `disabled-local` is disabled, Surface-bound to the prefab's local floor and carries ordered duplicate tags. Roles are caller values and neither legacy spawn list has a record.

`NativeMarkerAcceptanceTests` checks payload versions, the exhaustive and enabled registry views, roles, tags,
witness rows, half-open rectangles with markers on their minimum and maximum edges, point membership and identity on
monolithic and tiled reloads with independently verified closures, plus explicit adoption of both legacy lists.
`MapMarkerVillageAcceptanceTests` in MapDoc.Physics.Tests builds independent client and server heads, and
`MarkerVillageWorkflowTests` in MapEditor.Tests drives the shared commands and the MCP verbs. The stacked,
underground and local cave proofs stay `StackedCaveMarkerSupport_DoesNotSnapToSurface`,
`UndergroundMarker_ExplicitHeightAndLayerSurviveReload` and `LocalCaveMarker_TransformsOnceAndKeepsVolumeAssociation`.

Identity and saved text are different guarantees. A monolithic reload keeps authored surface declaration order, while a
tiled reload orders declarations by ID. Semantic and identity hashes agree across the two forms, but saved canonical
text matches only when declarations are already in ID order. The village sorts its declarations so its text
comparison holds, which proves that fixture only, not a general cross-storage byte guarantee.

Markers hand off to later rounds through these seams. They certify no native route, door, renderer pixel or game
policy.

| Consumer | Marker seam |
| --- | --- |
| R7 | Generic tagged points through `MapMarkerRegistry.ByTag` and the enabled view |
| R8 | Resolved annotations, `AnnotationDigest` and the `marker:{id}` residency entries |
| R9 | The full GUI host, marker inspectors and windowed native editing over `MarkerEditCommand` and `AdoptSpawnsCommand` |
| R10 | Whole-tool parity for every verb beyond the registered marker, prefab and interior commands |
| #1370 | Indexed half-open point queries through `MapMarkerRegistry.InRect` |
| #438 | Queried occupied space through `MapMarkerSpaces.Query` and its refusal statuses |
| R11 and G2 | Private source refreeze, the importer and final import and point-shift acceptance |

| Later owner | Remaining proof |
| --- | --- |
| R7 | Complete native foliage CPU contract, see [foliage acceptance](#public-foliage-acceptance-and-deferred-ownership) |
| R8 | Partial-face drawing, roof colour and shadows, camera boom, sky picking, local-light pixels, bounded streaming, eviction, HLOD and on-demand navigation |
| R9 and #1349 | GUI closure/bounds binding, prefab mode, inspectors and real pointer/key input |
| R10 | Whole verb-map wire parity beyond the registered prefab, interior and marker commands |
| R11 and G2 | Private lossless importer, exhaustive source comparison, actual source freeze and import-time acceptance of target, distance, occlusion and stance changes |
| G1b and #438 phase 5 | Native controller walking, body certificates and complete layered routes |
| #1363 | Live door state and generation-fenced collision, navigation and water swaps |
| #1327 | Compound contact provenance beyond canonical support surfaces |
| #1301 and #1358 to #1362 | Runtime residency and water indexing, huge exterior coverage and medium-aware seam capture |

## Format versioning

`MapDocumentFile.CurrentFormatVersion` is the version this engine build reads and writes, currently 9.
Format 9 adds optional foliage payload 1. Format 8 adds marker payload 2. Format 7 added rigid prefabs and building intent. Version 6 added bounded water, version 5 added the support recipe
and surface metadata and version 4 added native metadata. Loading a document with an
older `formatVersion` runs migrations
(`MapDocumentLoadOptions.RegisterMigration`, each a pure `JsonObject -> JsonObject` step from N to N+1)
until it reaches the current version. The engine's own steps are pre-registered by the
`MapDocumentLoadOptions` constructor: v1 -> v2 loads a v1 document (which had no sculpt layer) with an
empty layer and byte-identical terrain, v2 -> v3 stamps `MapDocumentFile.DefaultTileSize` (512 m), and
v3 -> v4 (`MapNativeMigration.Upgrade`) adds `playableBounds` from `bounds` when it is absent, and
v4 -> v5 (`MapSurfaceMigration.Upgrade`) stamps `LegacyXzCallbackV1` without changing resolver-v1
execution. The format advance changes authored identity once. It does not preserve the old token.
`MapSurfaceMigration.Upgrade` clones its input and leaves a format-5 input semantically unchanged.
v5 -> v6 (`MapWaterMigration.Upgrade`) adds no member, so a migrated document has no `water` section and keeps
legacy global water. It accepts only format 5 or 6 input.
v6 -> v7 (`MapPrefabMigration.Upgrade`) clones input and changes only `formatVersion`. It accepts format 6 or 7.
Format 7 moves every native root digest and resolver-2 authored identity once, including maps without prefabs.
v7 -> v8 (`MapMarkerMigration.Upgrade`) clones input and changes only `formatVersion`. It accepts format 7 or 8 and
reads no source, terrain, closure or role. Format 8 again moves every native root digest and resolver-2 authored
identity once. Rebuild identity-keyed caches and navigation captures after either migration. Absent prefab sections preserve
analytic execution and document hashing. Portals remain AuthoredOpen, with live state gated by #1363.
Headless doorway clearance and roof intent are available. Native controller routes and R8 rendering retain
the separate gates in the handoff table above.
v8 -> v9 (`MapFoliageMigration.Upgrade`) clones input and changes only `formatVersion`, accepting 8 or 9.
Absent foliage stays absent. Format 9 moves native root digests and resolver-2 authored identity once.

`MapDocument.SupportRecipe` pairs resolver 1 with `LegacyXzCallbackV1` and resolver 2 with
`AuthoredBindingsV2`. Resolver 1 refuses authored surfaces and placement support bindings. A placement
with `SupportBinding` cannot also provide explicit `Y`. `Surfaces` owns metadata and resident patches,
and snapshots clone its mutable collections. Monolithic documents embed canonical `surfacePatches`,
bounded to 256 patches, 1 MiB per encoded patch and 8 MiB including array brackets and inter-patch commas.
Embedded reads measure compact payload bytes before allocating each exact output buffer. General strings
use default JSON escaping and packed base64 retains the codec's ASCII spelling. Parsed DOM storage and
decoded scalar strings remain bounded by the supplied input. Larger worlds use tiled storage under
`tiles/surfaces/`, with SHA-256 payloads, index pages and directory pages. The manifest's `surfaceStorage`
commits their closure last.
Any default is as arbitrary as any other for a document that had no tile concept, so the rule is
"deterministic and documented" rather than "derived". A document newer than the engine, or an old one with
no migration path, fails to load. Saving always writes the current version.

**Version and layout are independent axes.** v3 means "the model that can be tiled", not "tiled": a v3
monolithic file is legal and is what `Save` writes. The version bumped because per-tile hashing needs a
`tileSize` even for a monolithic document, or a monolithic and a tiled copy of the same world would hash
differently.

A game can register additional steps for its own synthetic older versions:

```csharp
var options = new MapDocumentLoadOptions();   // the built-in chain is already registered
options.RegisterMigration(0, root =>
{
    root["displayName"] = root["name"]?.GetValue<string>();   // v0 called it "name"
    root.Remove("name");
    return root;
});
var doc = MapDocumentFile.Load(path, options);
```

## On-disk forms: one file or one directory

A document is **either a single file or a directory**, and both are first class and supported forever.
The tiled form is what a world too big to serialize as one string uses:

```
island.map/                        a directory, not a file
  map.json                         the root manifest, and the ONLY file a save ever mutates
  tiles/
    s_0_0/                         shard dir, shard = tile >> 4, a filesystem nicety and never a load unit
      t_0_0.<64 hex>.json          content-addressed: the suffix IS that tile's canonical hash
      t_3_-2.<64 hex>.json
```

The manifest carries the globals plus the occupied-tile index (`[{ "x", "z", "hash" }]`, ascending Z then
X) and the `tileSize` / `sculptCellSize` grid headers. Each tile file carries an optional `$schema`
annotation plus exactly four lists: `placements`, `spawns`, `playerSpawns`, `sculpt`. Those four are the
only content that scales with authoring. Exclusions, scatter overrides and regions stay global, decided
rather than deferred: `MapScatterOverrideDoc` is first-match-wins in document order, which is a global
ordering that bucketing would either break or have to reassemble.

The object model does not fork. A fully loaded tiled document is indistinguishable from a monolithic one
except that `MapDocument.Tiles` is non-null. Two serializations, one model.

**Which form a path holds comes from the path, and no entry point ever inspects an extension.**
`Path.GetExtension("island.map")` is `".map"`, not empty, so an extension heuristic sends a directory to a
file write.

```csharp
MapDocumentForm form = MapDocumentFile.DetectForm(path);   // Tiled | Monolithic | None
MapDocument doc = MapDocumentFile.Load(path);              // dispatches on the form
MapDocument window = MapDocumentFile.LoadTiled(dir, new MapTileRect(min, max));

MapDocumentFile.SaveAuto(doc, path);                       // back in the form it opened, None throws
MapDocumentFile.SaveAs(doc, path, MapDocumentForm.Tiled);  // the named form, whatever is there
IReadOnlyList<string> report = MapDocumentFile.VerifyTiled(dir);   // empty means clean
```

### The tile grid and region queries

`MapTileCoord` is a square of world XZ with edge `MapDocument.TileSize`, a DISTINCT type from `ChunkCoord`
so a 60 m chunk coord cannot be passed where a 512 m document tile coord is meant. `MapTileGrid.CoordOf`
delegates to `ChunkGrid.CoordOf`, so the floor rule has one implementation, and `AreaOf` is half-open on
both axes: a point exactly on a tile's max edge belongs to the next tile, which is what makes a partition
of rects reproduce the whole document exactly. A sculpt tile is owned by the document tile containing its
**origin corner** (`MapTileGrid.OwnerOfSculptTile`), single-owner for every sculpt cell size, including
the ones where the tile size is not an integer multiple of the sculpt span.

`MapSpatialIndex.Build(doc)` buckets a loaded document's point content by tile once, O(n) to build and
O(k) per query, and works on both forms, so a whole-document workflow keeps region queries.
`MapRuntime.BuildPlacements` grows a rect overload and two index overloads beside the untouched
whole-document one.

### Saving without materializing the document

`SaveTo(doc, stream)` serializes straight through a `Utf8JsonWriter`, and `Save` is reimplemented over it,
so peak managed memory is the writer's buffer rather than the document's serialized size. `SaveTiled`
writes one tile at a time and **does not rewrite a tile whose canonical hash is unchanged**, which is what
makes a windowed save over a huge world touch only what the author actually edited.

The write ordering is crash-consistent: changed tiles go to content-addressed names nothing points at yet,
a single `map.json` rename commits, and a best-effort sweep afterwards collects whatever the new manifest
does not name. Crash at any instant and the directory loads as entirely the old version or entirely the
new one, never a mixture. That is why the file name encodes the content hash: with a fixed name per
coordinate the manifest could not tell old content from new.

`SaveTiled` holds exclusive write access to the directory until its sweep and in-memory index refresh finish.
An overlapping save from another thread or process fails with `MapDocumentException` before changing tiles or
the manifest. Retry after the first save finishes. The empty `.mapdoc-save.lock` file remains in the directory
between saves, but the operating system releases its ownership when the writer closes or exits. Do not remove
that file while a writer is active. Writers targeting different directories remain independent.

`MapDocumentSaveOptions.Durability` defaults to `MapSaveDurability.Fast`, which defends against a process
kill, an unhandled exception or an editor crash, which is what actually happens on a dev box.
`PowerFail` opts into per-file flushes plus a directory fsync on the platforms that have one (Linux and
macOS do, Windows has no equivalent primitive and orders metadata through the NTFS journal instead).

### Windowed loading and the partial-save guard

`LoadTiled(directory, window)` loads the manifest plus the tiles in a `MapTileRect`. Unloaded tiles keep
their index entries, so the document knows they exist and a later `SaveTiled` back to the SAME directory
carries them through untouched. Surface reads map the window's world rectangle into each rational lattice,
expand it by one cell, and load intersecting patches through covering directory and index pages.
`MapTileIndex.IsPartial` is the unified partial flag. It includes unloaded tiles and unloaded surface pages
and patches. `HasUnloadedTiles` retains the tile-only meaning.

A window records the SHA-256 of the manifest it loaded. `SaveTiled` refuses every partial save, including a
surfaces-only window, with `stale window` when the current manifest cannot be read or has changed since the
window was loaded, so a window never overwrites another writer's commit
([#1310](https://github.com/APKiwiOrg/KhaozEngine/issues/1310)). The save lock alone does not give that
guarantee.

Raw partial surface saves permit payload edits only after outgoing references, reverse page knowledge,
reverse dependants, structure and record checks. A raw save certifies reference integrity, including exact
XZ agreement between each corner owner's address and its dependent corner, and never geometry. It refuses a
`dependency position` that disagrees or is not representable. A partial raw save also refuses an unloaded
reverse dependant (`unloaded: reverse dependant`) and an unsupported partial edit of a surface declaration,
patch structure or records. Geometry, seam heights and separation require complete native transactions.

`IMapSurfaceSource` reads report `MapPatchStatus` as `Present`, `KnownEmpty`, `Unloaded`, `Missing` or `Corrupt`,
and `FindPatches` reports `MapFindStatus` as `Complete`, `Incomplete` or `CapacityExceeded`. Only `Present` and
`KnownEmpty` are facts. The other read outcomes are never treated as empty.
`MapDocumentSurfaceSource.Capture` detaches resident patches and storage knowledge. `MapStoredSurfaceSource.Open`
pins one manifest generation and verifies named bytes and patch semantics without asset reads or directory
enumeration. Files swept by a newer save return `Missing`. `FindPatches` bounds local candidate and page
work through `MapQueryLimits`, and capacity refusal returns no partial patches. Returned patches are clones.
Index `SpaceIds` summarizes payload records and incidence only. Queries also consider the current surface's
`IndoorSpan.ParentSpace`, so metadata-only edits cannot leave space filtering tied to an older page generation.
`MapSurfaceSemantics.RootDigest` includes surface semantics and excludes storage names and page packing.

**Every save entry point refuses a partial document** (`Save`, `SaveText`, `SaveTo`, `SaveAuto`,
`SaveAs`), because the data-loss path is a windowed document reaching a whole-document writer: that write
silently drops every unloaded tile and looks like a successful save. The guard is stated on the DOCUMENT
(`MapTileIndex.IsPartial`), not on one writer, so a save path added later inherits it. `SaveTiled` also
throws when a bucketed item lands in a tile the index marks occupied but not loaded, naming the item and
the target tile, rather than replacing that tile's real content with just the moved item.

### World identity (`MapDocumentHash`)

```csharp
string world = MapDocumentHash.OfWorld(doc);        // SHA-256, lower hex, both forms agree
string tile  = MapDocumentHash.OfTile(index, coord);
```

Canonical bytes are a compact serialization distinct from the indented bytes on disk, so indentation and
hand reindenting never affect identity. The four bucketed lists are sorted (placements, spawns and player
spawns by ordinal id, sculpt tiles ascending) before hashing, so their half depends only on content: two
authoring sessions that added the same placements in a different order produce the same world hash. The
global shape lists are hashed in DOCUMENT ORDER, `scatterOverrides` necessarily so, since reordering it
changes the world.

`displayName` and `$schema` are excluded, because the hash answers "is the ground under this player the
same ground" and renaming a zone must not desync a live server from its clients. `tileSize` IS included,
so re-tiling a world changes its identity, and a conversion between the two forms must PRESERVE `tileSize`
rather than re-derive it. A null sculpt block hashes identically to an empty block at
`MapTerrainOverrides.DefaultCellSize`, and the monolithic writer collapses that empty block back to no
block, so a round trip through the tiled form never invents a `terrainOverrides` key.

Every integer in hash input and in every generated file name goes through `CultureInfo.InvariantCulture`.
Under ICU, `sv-SE` and `fi-FI` format a negative integer with U+2212 MINUS SIGN rather than U+002D, so a
world with any negative tile coordinate would otherwise hash differently and write differently named files
on a Swedish workstation. No code path ever parses an integer back OUT of a file name: the manifest is the
sole authority on which tiles exist and what each is called.

`MapDocumentHash.SchemeVersion` is folded into every composed digest and is recorded in the manifest.
Loading a whole document at a mismatched scheme is fine and the next save upgrades it. A WINDOWED load
refuses, because a partial save carries stored hashes through verbatim and cannot upgrade what it cannot
read.

### On-demand tile reads (`MapDocumentSource`)

```csharp
using var source = MapDocumentSource.OpenTiled("island.map");   // reads map.json and nothing else
MapTileContent tile = source.ReadTile(new MapTileCoord(3, -2)); // parses and validates one tile
```

`ReadTile` is free of shared mutable state, so a caller may run it on a worker thread.
`MapDocumentSource.FromDocument(doc)` wraps an in-memory whole document behind the same API.
`MapTileContent` is immutable once handed out, INCLUDING the delta arrays inside `SculptTiles`, because a
reader hands out the arrays it parsed and `TerrainSculpt` stores them by reference: clone before editing.
For `OpenTiled`, the occupied-tile index is a snapshot taken at open time, since a re-saved tile is
content-addressed and gets a new filename on every edit. Call `source.Refresh()` after an external save (an
editor, a generation tool) re-reads `map.json` and atomically swaps in the fresh index, which
`MapTileResidency.Invalidate` already does before it re-reads a tile. `Refresh` is a no-op for a
`FromDocument` source, since there is no `map.json` to re-read. Rebuild it over the updated document instead.

A tile read runs a per-tile validation subset, keeping the loud-fail stance for a read that cannot see the
whole document: ids non-empty and unique within the tile, placement kinds non-empty, delta counts exact,
and every item actually falling inside the tile it was read from. That last one is the check a
whole-document load can never make and a tiled load must, since it is what catches a hand-edited or
tool-generated file whose content does not match its name. Bounds and cross-tile checks stay with
`MapDocumentValidator` and `VerifyTiled`.

### Document residency (`MapTileResidency`)

Keeps a square ring of document tiles resident around one or more foci, reading each on demand through a
`MapDocumentSource` and handing arrivals and departures to an `IMapTileSink`. GPU-free and driven from a
position, so a client and a headless server run the same type.

```csharp
var config = MapResidencyConfig.Default;                       // LoadRadius 2, UnloadRadius 3, 2 per update
IReadOnlyList<string> errors = config.ValidateAgainst(streamerConfig, doc.TileSize, sculptCellSize);
if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));

using var residency = new MapTileResidency(source, config, mySink, dispatcher: null, field);
streamer.BuildGate = residency.GateFor(streamerConfig.ChunkSize, sculptCellSize);

// Every frame, in this order.
residency.Update(playerPos);
streamer.Update(playerPos, dt);

// Every teleport, zone change or camera jump, before the next streamer.Update.
residency.PrimeAround(newFocus);
streamer.UnloadAll();
```

Radii are in TILE units at CHEBYSHEV distance (a square ring), which is deliberately NOT `StreamerConfig`'s
Euclidean metric. The focus can sit anywhere in its own tile, including hard against a corner, and a square
ring guarantees exactly `LoadRadius * tileSize` of loaded world in every direction for every radius, where a
Euclidean ring guarantees 0 at radius 1 and an awkward 2.83 tiles at radius 4. `MapResidencyConfig` is a
distinct type from `StreamerConfig` so the two cannot be confused.

`ValidateAgainst` is the wiring-time check that a chunk can never build or REBUILD against a non-resident
tile: the data rule measures to the streamer's UNLOAD radius (chunks persist that far and `Invalidate`
rebuilds any of them), the collider rule keeps every gameplay chunk over Gameplay tiles, and both subtract
one sculpt span because a tile's low-X and low-Z edges are covered by sculpt owned by its neighbour. Run it
against the WIDEST render-distance profile, not the active one: the profile is a runtime setting, and a
config that only validates on Low is a hole in the world on Ultra.

`Update(ReadOnlySpan<Vector3>)` is the server form: the resident set is the union of the per-focus rings,
recomputed each update with nothing reference counted, and a tile contested between rings takes the
strongest any focus assigns it, so the answer does not depend on focus order. Cost is O(foci * ring area).
Past a few hundred foci a shard server should drive one residency per `CellSim` rather than one global
residency with a thousand foci. That guidance has two known holes (authored content in a region no player
ring covers, and a cell crossing arriving cold), tracked as
[#341](https://github.com/APKiwiOrg/KhaozEngine/issues/341) and not solved here.

The two seams a consumer wires:

- **`GateFor(chunkSize, sculptCellSize)`** returns an `IChunkBuildGate` for `TerrainStreamer.BuildGate`. A
  chunk builds only when no document tile touching its (sculpt-expanded) footprint is occupied-but-not-
  resident. An ABSENT tile is buildable, never blocking, because absence is the common case in a sparse
  world and gating on it would deadlock the streamer over empty terrain.
- **`MapTileResidency` implements `IPlacementSource`**, so `PropLayer.PlacementLayer(residency, meshes,
  drawRadius)` streams authored placements to the renderer with no glue. Pass a `TerrainField` to the
  constructor when any authored placement omits Y, since that is what ground-snaps it.

The sink is a notification seam and nothing more. `TileLoaded` / `TileRingChanged` / `TileUnloaded` fire on
the calling thread inside `Update` before it returns, so a consumer adds and frees per-tile physics bodies
with no lock, and the engine never registers, owns, or frees one. The file read and parse behind an arrival
ran on a worker thread. The handoff did not.

## Loud failures

Map documents are dev-authored content, not runtime state: `MapDocumentFile.Load`/`Save` throw
`MapDocumentException` on a read error, invalid JSON, a missing or out-of-range `formatVersion`, a
deserialization error, or any semantic validation failure (`MapDocumentValidator`, for example a duplicate
id, an unknown scatter layer reference, or a `terrainOverrides` tile that leaves the document bounds). A
game boots against a bad document and fails loudly with a precise error rather than quarantining it and
limping on, the opposite of the quarantine handling runtime cell blobs get. The tiled reads are the same:
a directory with no `map.json` has no form and says so, and a tile that fails the per-tile subset names the
directory, the tile coordinate and the file.

Loading also enforces the schema's closed structures, in every format version. After migration,
`MapDocumentFile.Load` and `LoadText`, the tiled manifest read (`LoadTiled`, `MapDocumentSource.OpenTiled` and
`Refresh`) and every tile file read refuse a member that a closed structure does not declare, and the error
names it. Member names match case-insensitively, as the serializer reads them. Before migration, a repeated or
case-variant member anywhere in a document, manifest or tile refuses as a document error, including open feature
payloads. Earlier engines let the last repeated value win. Unknown members of closed structures were previously
dropped, so a legacy file carrying one (a root `notes` field, say) now fails to load until the member is removed.
`terrain.features` items stay open, so a registered feature type keeps its own fields.

Depends on `KhaozEngine.Primitives`, `KhaozEngine.Serialization`, `KhaozEngine.Content`, and
`KhaozEngine.Terrain`. GPU-free. In the `Foundation` umbrella. The GUI editor (`KhaozEngine.MapEditor`)
and the `ke-mapedit` MCP tool are later frontends over this model, see
[`docs/design/MAP-EDITOR-DESIGN.md`](../docs/design/MAP-EDITOR-DESIGN.md).

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).

## Scoped native scene consumers

`MapSceneSource.Capture` scans verified metadata with explicit bootstrap ceilings. Defaults are 65,536
tiles, 65,536 metadata rows, 262,144 memberships, 4096 memberships per owner, 134,217,728 scanned
UTF-8 bytes and 67,108,864 retained metadata bytes. It streams one tile at a time and retains bounds and
provenance. `MapRenderExtentValidator` separately streams every mesh tier and light reach, retaining no
global decoded inventory. `MapPreparedInputLease` owns selected decoded materials and meshes only.

`MapScopedScene` is separate from complete `MapEffectiveResources`. Its witness names required,
present, known-empty and unavailable resources. `CompleteForScope` certifies the requested acquisition,
not the whole world. Scope defaults admit 4096 selected resources, 256 patch scopes, 262,144 prepared
faces and 67,108,864 prepared bytes before work. Point-query limits remain 256 patches, 4096 faces,
64 intersections, 32 page reads, 64 record reads and depth 8. Explicit `MapQueryLimits` propagate through
nested native acquisition, dependency closure and retained foliage, water, cluster and membership scopes.

The prepared-byte allowance bounds live workspace, unique retained inventory and applicable borrowed inputs.
It models owned storage, not cumulative allocation, CLR heap or RSS. Temporaries end at their last consumer.
Copied native-set metadata retires after merge. Remapping slack ends after enumeration. Scope hashes run sequentially.
Geometry copies stay charged through context capture. Returned arrays, scene/view filter backing, dependencies,
mesh copies and reusable scratch stay charged until release. Growth admits both old and replacement storage.
Failed policy closure preserves its input and releases intermediates. Failed reservations change no counters.
Peaks remain separate from current bytes. Reads, UTF-8 work, faces, scopes and resources keep cumulative work/count semantics.
Child windows attribute reused contexts and decoded inputs with one root charge. View leases survive product retirement and aggregation.
Generic dependencies borrow direct storage separately from shared contexts, keeping one context attribution per child window.
Foreign transfers adopt missing shared contexts from admitted destination workspace and retain their identities in its registry.
All destination storage and window obligations are admitted before the source owner retires. Refusal preserves the source.
Water footprint reads release their consumed bound and index scratch on return. Accumulated region
polygons and closed contours transfer their admitted storage into the body collector before each append,
remain charged through region emission, then settle into the compiled body's escaping inventory.
Published contour, region, triangle and cleaned wide-polygon lists retain their full backing capacity
in that inventory, including spare slots. Their read-only wrappers expose capacity to the inventory
without copying or releasing admitted storage during publication. Footprint and occupied-piece rows
use exact-sized arrays. The full body-list container and consumed bound-read lists charge capacity explicitly.
Query capacity results preserve the declared limit, demand and allowance. `IndexNodesVisited` counts rejected siblings.
Capacity refusals retain a typed `MapSceneCapacity` in the source or scoped result, with `LimitName`,
`Requested` and `Allowance`. Byte demand is current eligible live inventory plus the attempted reservation.
The declared allowance and decimal demand survive sums beyond int64.
The same values appear in `Findings`. `MapSceneCapacityException` exposes the same typed values. A bounded scan reports the first
excess demand rather than continuing work to count the rest.

`MapWindowFoliageContext` uses the shared derived read reach and immutable dependencies. Complete
constructors still refuse partial documents. No scoped consumer constructs a partial `MapDocument`
and passes it to a complete builder. Foliage motion and all-LOD/light bounds remain cosmetic and do
not widen navigation heights. See the [render contract](../KhaozEngine.MapDoc.Render3D/README.md).
