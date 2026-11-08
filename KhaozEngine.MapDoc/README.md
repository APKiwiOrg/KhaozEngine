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
validate builder/hash options. These option guards select the first failed field in order: an unsupported
`ResolverVersion` uses `ResolverContractMismatch`, blank `BuilderId` uses `SchemaValueRequired`, nonpositive
`BuilderVersion` uses `ValueOutsideRange`, and blank `OptionsHash` uses `SchemaValueRequired`. Refusals retain
`FieldPath`, submitted and expected resolver versions, and the failed text or integer. Builder version 1 is
accepted. Missing playable bounds and a missing native placement asset use `SchemaValueRequired`, their
`FieldPath` and the known document or placement identity. Nonfinite native storage bounds use
`ValueNonfinite`, the first failed component in `FieldPath` and `ActualNumber`. Editing callers
need no synthetic builder options and never resolve relative resource paths implicitly.
`MapResolver.Resolve` publishes an immutable `MapResolvedDocument`. Its `AssetClosure` is the exact verified
closure passed in, for binding and resource lookup without a second load. `MapNativeResolution.Resolve(document,
assets, options, legacySupportHeight)` routes a native document to its resolver by identity: `(1, 1)` resolves with
`MapResolver` over the caller's legacy support height and `(1, 2)` with `MapResolverV2` over the document's own
surfaces. Options with a different resolver version refuse. A missing legacy callback uses
`MapDocumentRefusal.SupportLegacyHeightRequired` with `ResolverVersion` and `FieldPath` `legacySupportHeight`.
A nonfinite resolved support height uses `ValueNonfinite` with `PlacementId`, leaving `ActualNumber` null.
Placement lookup failures use `ReferenceMissing` with the requested `PlacementId`, `EntityId` and
`SubjectKind` `placement`. This package loads no meshes and has no render
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
valid input. A swept payload refuses with `Missing`, and mismatched payload bytes or patch semantics refuse with
`Corrupt`. A per-surface semantic aggregate mismatch carries `DigestMismatch`, `DigestKind` `surface semantics`,
`SubjectKind` `surface`, `SurfaceId`, `EntityId`, the pinned `ExpectedHash` and computed `ActualHash`.
Replacing `map.json` never switches an existing source to the new generation. Open a new source
to select that generation.

The stored overload also reads the tile files named by the generation pinned at `Open`, one tile at a time,
by their pinned names. Each tile's parsed content must hash to its pinned tile hash before any entry is
used. A removed tile file carries `PinnedContentUnavailable`, `PatchStatus` `Missing`, `Tile` and
`ExpectedHash`. Altered content carries `DigestMismatch` with both hashes and `PatchStatus` `Corrupt`.
Known inner document refusals retain their reason and identifiers through the common copy operation.
Pinned directory, index and payload failures retain their storage `SubjectKind`, available `Patch`,
`PatchStatus` and `ExpectedHash`. `MapPatchRead.Finding` exposes a known structured payload failure
beside its unchanged status and detail. Both payload reads and stored identity attach the known patch to
a schema refusal that lacks one, preserving any existing patch and all other inner identifiers. A semantic mismatch uses `DigestMismatch`. Mismatched index
cells, height extrema, dependency keys or record IDs use `AuthoredValueMismatch` and the failed `FieldPath`.
Unsupported pinned tile schemes use `DocumentHashSchemeUnsupported`, `ActualVersion` and `SupportedVersions`.
Identity never rereads
the manifest, lists directories or trusts a stored hash without reading the file. Stored identity validates
the same document rules as the loaded document. Ownership-registry refusals expose the same `Reason` and typed
identity, child-kind, instance and anchor context on both paths. Aggregate validators expose ordered typed
findings alongside their existing prose projection.

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

Native vectors, feature discriminators and numeric identity text use `MapNativeVectorJsonException`,
`MapFeatureJsonException` and `MapNumericIdJsonException`, each derived from `JsonException` with a required
`MapDocumentRefusal`. Document and prefab wrappers preserve their reason and identifiers. Vector admission
uses `SchemaObjectRequired`, `SchemaMemberUnknown`, `SchemaMemberDuplicate`, `SchemaMemberMissing`,
`SchemaValueRequired` or `ValueNonfinite` with the relative `FieldPath` and parsed `ActualNumber` when known.
Feature admission distinguishes missing `SchemaValueRequired` from `DocumentTerrainFeatureUnknown`, with
`FieldPath` and discriminator `ActualText`. A consumer converter returning no feature uses `SchemaValueRequired`
with its discriminator and root field. Non-string or noncanonical numeric identity text uses
`SchemaInt64Malformed`, relative `FieldPath`, submitted `ActualText` when known and inclusive
`MinimumInteger` and `MaximumInteger`. Invalid digits use the same reason. Out-of-range numeric identities
use `ValueOutsideRange` with those limits, submitted text on reads and `ActualInteger` only when representable.
Native migration distinguishes absent `SchemaMemberMissing`,
incorrectly typed `SchemaValueRequired` and unsupported `DocumentMigrationSourceVersionInvalid`, with
`FieldPath`, known `ActualVersion` and an owned `SupportedVersions` snapshot.

Document load, validation and whole-save adapters retain the first typed finding and the complete ordered
`Findings` snapshot. A nested codec that already throws `MapDocumentException` gains the document's
`SourceName` through the same-type copy method, preserving its message, reason, identifiers and original cause. A future format version uses `DocumentVersionUnsupported` with `ActualVersion`,
`SupportedVersions`, `SourceName` and `FieldPath`. Surface JSON setters use `MapSurfaceJsonException`
(derived from `JsonException`) with `SchemaMemberNull` and `FieldPath` for null collections. Duplicate
embedded keys use `IdentityDuplicate`, `Patch` and zero-based `Index`, all preserved by document wrapping.
Placement transform guards select the first nonfinite X, Z, optional Y, yaw or scale before nonpositive
scale, missing tags or a null tag. They report `ValueNonfinite`, `ValueOutsideRange` or `SchemaValueRequired`
with `PlacementId`, the failed `FieldPath`, numeric value when applicable and null-tag `Index` when known.
Scale remains strictly positive. Registry build failures use `DocumentTerrainFeatureUnknown` with the
captured discriminator in `ActualText` and `FieldPath` `Type`. World hash ordering uses `OrderInvalid`,
`PreviousTile` and the refused `Tile`, for reversed and repeated coordinates alike.

Authored content duplicates report `IdentityDuplicate`, `SubjectKind` and `EntityId`, or `SculptTile` for
sculpt content. The digest's existing string result remains a prose projection of that typed finding.
Whole authored identity over a window reports `CompleteDocumentRequired` with `DocumentId` and `Operation`
`ComputeWholeAuthoredIdentity`. Native validation uses the same reason with `Operation`
`ValidateNativeDocument`. Asset-root comparison reports `DocumentAssetRootsMismatch` with submitted
`ResourceIds` and closure `ExpectedResourceIds`. Both lists are owned snapshots and remain stable when the
caller edits its root list.

## Bounded scoped acquisition

Every API in this section is new in 20.30.0.
`MapScopedSurfaces.Acquire(source, scope, assetSha256)` requires `IMapSurfaceAcquisitionSource`, implemented
by `MapStoredSurfaceSource` and `MapDocumentSurfaceSource.Capture(document)`. A producer implementing only
`IMapSurfaceSource` refuses with `AcquisitionUnsupported` and `SourceType` before its legacy callbacks run. Custom producers must open one pinned `IMapSurfaceAcquisitionSession` for discovery,
record-anchor expansion and bound reads. Snapshot, root and copied request stay fixed for that session.

`ReservedPatchKeys` is a detached, ordered unique-key snapshot. Every discovery key and explicitly read
key consumes the same `MaxCandidatePatches` allowance before work, including known-empty and unavailable
keys. Discovery payloads must have reservations, and extra reservations must have an unavailable fact or
known-empty coverage. Later reservations must exactly match the coordinator's requested keys. Reads must
return the requested key, present payloads must match their semantic digests, and surface lookups must
return the requested declared surface. Violations refuse instead of publishing incoherent facts.

Acquired fact admission uses `SchemaValueRequired` for missing surface metadata, frame, patch, key
identity or empty range, and `SchemaEnumUnsupported` for undefined role, presence or patch status.
`FieldPath` names the first failed branch. Negative `PagesRead` uses `ValueOutsideRange` with
`ActualInteger` and `MinimumInteger = 0`. Repeated patch keys or record identities use `IdentityDuplicate`.
A present patch with a different key uses `AuthoredValueMismatch`, `Patch` and `ExpectedPatch`, while a
semantic digest mismatch uses `DigestMismatch`, `ExpectedHash` and `ActualHash`. A non-present status
carrying a payload uses `AcquisitionContractMismatch`, `Patch`, `PatchStatus` and `FieldPath`.
A missing bound surface identity also carries the footprint's `RecordId` and the lower or upper field.
Sources without bounded acquisition, including composed native sources, use `AcquisitionUnsupported` and `SourceType`.
These carriers preserve identifiers through source-context wrapping and typed finding projection.

Ordinary and effective acquisition's discovery snapshot mismatch uses `AcquisitionSnapshotChanged` with
`ExpectedSnapshot`, `ActualSnapshot` and `FieldPath`. Its session pin, scope, reservation, payload and
incidence contract mismatches use `AcquisitionContractMismatch`. Snapshot pins carry both snapshot
identities, root and scope pins carry `ExpectedHash` and `ActualHash`, and payload checks retain
`Patch` and `PatchStatus`. A null scope or known incidence without a list uses `SchemaValueRequired`.
Unsupported discovery or unavailable statuses use `SchemaEnumUnsupported` with the numeric value and
enum type. A composed read whose surface has no owner uses `ReferenceMissing`, `SurfaceId` and `Patch`.
Ordinary and effective explicit-read key mismatches use `AcquisitionContractMismatch` with `Patch`, `ExpectedPatch`
and `FieldPath`. Their surface lookups distinguish an undeclared surface (`ReferenceMissing`), a
successful lookup returning null (`SchemaValueRequired`) and a different identity (`AuthoredValueMismatch`).
They retain the requested `SurfaceId`, the field and the returned identity as `ActualText` when known.
Ordinary and effective reservation snapshots use `WorkCapacityExceeded` with `Budget = "MaxCandidatePatches"`,
`ActualCount` and `Limit` before checking canonical ordering with `OrderInvalid`. Later snapshot, root
or reservation changes use `AcquisitionSnapshotChanged`, retaining the changed field and both pins
when applicable. Negative page deltas use `ValueOutsideRange` with a zero minimum. Backward or
inconsistent page observations use `AcquisitionContractMismatch`. Backward observations retain
`ActualInteger`, `PreviousInteger` and `MinimumInteger`. Inconsistent deltas retain the reported
`ActualCount` and observed `ExpectedCount`. Ordinary and effective session admission use the same pin identifiers.
A collapsed effective cell uses `GeometryNotRepresentable`, `SurfaceId`, `Patch`, `CellX` and `CellZ`.
The resident snapshot merge and post-capture dictionary lookup treat impossible failures as internal
invariants. Complete resident views distinguish a mismatched dictionary/payload key
(`AuthoredValueMismatch` with both patch keys) from an undeclared surface (`ReferenceMissing` with its
surface and patch). Public scope-coordinate admission remains in the shared surface query.

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
incomplete identity with `AcquisitionIncomplete` and `Operation` `RequireCompleteIdentity`. A complete scoped acquisition from a partial editing window still cannot replace a
whole current-edit identity. `CompleteView(set)` captures all resident patches and records without bound
enumeration for complete in-memory surface sets.

## Frame-local compiled geometry

`MapFrameLocal.ToFrame` converts a compiled patch or strip into a `MapFrameMesh` in one `WorldFrame` with
world-datum heights, and `MapFrameLocal.CompileInFrame` places an exact local patch by a `MapTransform`,
composing in double before the one frame-local float rounding. Neither takes an absolute world-float round
trip. They are new in 20.30.0, and R5 consumes them.
`ToFrame` reports `FrameRadiusExceeded` with `Limit`, the patch when available and the checked local
`PositionX`/`PositionZ` when anchor subtraction succeeded. Nonfinite positions instead report
`ValueNonfinite` with the first failed `FieldPath` and `ActualNumber`. Anchor overflow does not invent a
local position.

Support resource size refusal uses `WorkCapacityExceeded`, `Budget = "MaxPayloadBytes"`,
`ActualByteCount` and `Limit`, plus `ResourceId` on reads. Null or blank surface identities use
`SchemaValueRequired`, duplicate identities use `IdentityDuplicate`, and undefined role or presence values
use `SchemaEnumUnsupported`. Defined values outside the native support-floor policy use
`SurfaceSupportRoleInvalid`. These surface findings retain the surface index and failed field.

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
legacy normals. Compiler admission forwards the first `ValidateLocalFindings` reason and its identifiers
with the complete ordered `Findings` snapshot. Metadata checks distinguish a mismatching surface identity
(`AuthoredValueMismatch`), a missing frame (`SchemaValueRequired`) and unsupported role or presence
(`SchemaEnumUnsupported`). A legacy policy on a different frame reuses `SurfaceLegacyRecipeRequired`.
Surface identity calculation distinguishes missing metadata, unsupported enums, a patch with the wrong
surface owner (`AuthoredOwnershipMismatch`), a duplicate patch (`IdentityDuplicate`) and a malformed
semantic digest (`DigestMalformed`). Opening compilation distinguishes wrong patch ownership, an empty
cell list, an out-of-slot cell, an out-of-patch address, duplicate cells and a present cell. It retains the
opening `RecordId`, requested `Patch`, selected `SlotCell` and the actual count or numerical limits when
checked. Opening exact geometry failures retain their record and patch. Patch cloning retains the patch and record type when refusing an
unsupported topology record, or `SchemaValueRequired` for a null record.

**Mixed-resolution bounds.** A `MapSpaceFootprint` names a lower and an upper `MapBoundRef` that may sit on
different rational lattices. `MapCommonRefinement` clips them to their exact common refinement per footprint
cell. `MapRefinementLimits` budgets each cell at 16,384 source faces per bound, 65,536 pair checks,
32,768 refinement faces and 131,072 refinement vertices. Every attempted lower and upper pair is charged as a
pair check before any bounding-box rejection. One compiled bound-face context per validation or query call
is limited to 256 unique bound patches and 262,144 compiled faces, counted from cell bytes before any
compile. Those two context values are proposed operational defaults, not world limits. A refinement ends
`Complete`, `CapacityExceeded` (with no partial result), `NotRepresentable`, `Invalid` or `MissingGeometry`.
`MapLowerCellClassification.Finding` retains the concrete failed rule and its typed context alongside
the existing class and `Detail`. Missing and wrong-kind record reads keep `Unavailable`, while a legacy
recipe refusal keeps `InvalidRecipe`. A caught patch refusal retains its original reason and identifiers.
Legacy height refusals identify the first failed `h00`, `h10`, `h01` or `h11` corner, its `ActualInteger`
and the inclusive signed-short range. Internal bound plans carry the same finding without changing status
or detail, including source-face and context-patch budgets with their measured counts and inclusive limits.
`MapBoundFaceSet.Finding` and `MapCellRefinement.Finding` retain those findings through bound preparation,
wide refinement and bounded conversion. A caught degenerate-triangle refusal still projects to `Invalid`,
even though its concrete reason is `GeometryNotRepresentable`. Exact arithmetic and narrowing overflow
retain their existing `NotRepresentable` projection. No refusal returns partial faces or vertices.
Refinement capacity findings name `MaxSourceFacesPerBound`, `MaxPairChecks`, `MaxRefinementFaces`,
`MaxRefinementVertices`, `MaxContextPatches`, `MaxContextFaces` or `MaxFacesPerPatch` in `Budget`, with
`ActualCount` and the inclusive `Limit`. Source-face findings identify the failed `lower` or `upper` input.
An unrepresentable measured count leaves `ActualCount` null rather than substituting a truncated value.
Declared seam and strip vertex sequences stay strict. Refinement never waives a matching subdivision.

`MapPrismCoverage.ValidateFindings(view, limits)` and
`MapSpaceCoverageValidator.ValidateFindings(view, limits?)` return complete ordered read-only snapshots.
Their existing `Validate` APIs retain the same string projection. Their `Require` adapters use the
first concrete reason and attach every ordered finding as `MapDocumentException.Findings`.
Prefab edit preparation and the effective physics-world gate use these adapters.
Occupied partition adapters retain both the concrete finding and their original refinement projection.
An invalid refinement projected as missing geometry stays distinct from arithmetic overflow or capacity,
even when two failures have the same public reason. Classifiers do not interpret words inside user IDs.

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

Partial paint refusals carry `MapDocumentRefusal` on `MapDocumentException`. `WorkCapacityExceeded`
retains the actual `MapRefinementLimits` field in `Budget`, its `Limit` and `ActualCount` when measured.
A bounded producer that stops before returning its candidate list leaves `ActualCount` null.
Exact intersection overflow uses `GeometryNotRepresentable`, the target `SurfaceId` and
`Operation = "paint intersection"`. Neither failure publishes partial regions.

**Legacy lower coverage.** Only a lower bound of kind `MapBoundKind.LegacyExteriorV1`, in an `Exterior`
space with an `OpenTop` upper bound, may cover a presence-1 `LegacyTileWorld` void or NoDraw cell.
`MapLegacyExteriorRecipe.CheckFinding` returns the first failing rule. `LegacyExteriorSpaceRequired`
precedes `LegacyExteriorOpenTopRequired`, then the lower surface recipe and lattice ownership checks.
A missing lower surface uses `SchemaValueRequired`, an incompatible one uses `SurfaceLegacyRecipeRequired`,
and a mismatched lattice uses `AuthoredOwnershipMismatch`. Findings retain the footprint `RecordId`,
`SpaceId`, known `SurfaceId`, lattice `Patch` and first failed `FieldPath`. `Check` remains the string projection. The lower surface must be a `SupportFloor` with `LegacyTileWorld` presence on
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
Queries and acquisition return `NotRepresentable`. Surface compilation reports `GeometryNotRepresentable`
with its `Patch`, `SurfaceId` and `Operation` when known. It is never `CapacityExceeded` and never rounded,
snapped or toleranced. The developer message retains its representability explanation without claiming a
capacity or budget failure.

Effective coordinate freezing reports `ValueNonfinite` for a nonfinite value and `ValueOutsideRange`
for a finite value outside the inclusive installed metre range. Both carry `Operation = "Freeze"`
and `ActualNumber`, with `MinimumNumber` and `MaximumNumber` for the finite range refusal.
Effective frame conversion reports `FrameRadiusExceeded` with the checked local position and inclusive
`Limit`, or `ValueNonfinite` for a nonfinite local height. Support-seat arithmetic overflow reports
`GeometryNotRepresentable` with `MarkerId` or `PlacementId` and `SupportStatus = NotRepresentable`.
Relation classification uses typed refusals and retains the originating chain or aperture status separately,
so an invalid chain and a missing chain keep distinct projections even when they share a refusal reason.

**Resolver version 2.** `MapResolverV2.Resolve` resolves documents whose recipe is `AuthoredBindingsV2`. It
keeps explicit Y, refuses any binding whose support is not `Supported`, and refuses a space binding without a
reference height and bounded search. `MapResolverAdoption.ConvertToAuthoredSupport` returns a detached
version-2 copy. Seating failures carry `SupportUnresolved`, `PlacementId` and the typed `SupportStatus`.
Missing bindings, bounded space searches and adoption choices use their distinct reasons in the refusal table.
A partial-window adoption uses `CompleteDocumentRequired`, the `DocumentId` and
`Operation = "ConvertToAuthoredSupport"`. An effective placement whose scale or yaw cannot be presented in
binary32 uses `GeometryNotRepresentable`, `PlacementId`, the component's `FieldPath` and binary64 `ActualNumber`,
with `Operation = "ResolveEffectivePlacements"`.
A missing-height provider carries both `PlacementId` and `ProviderPlacementId` before any physics installation.
Resolver version 1, its options and its execution are unchanged.

**Fine-patch conversion.** `MapFinePatchConversion.Convert` turns coarse regions into exact finer patches.
Each coarse cell is classified before anything mutates as `ExactCoplanar`, `ExactDiagonal`,
`UnsupportedEncoding` or `NotRepresentable`. The last two refuse unless the request sets
`AcceptAuthoredDifferences`, which lists every changed face, removed fallback and removed flag. A footprint
over a converted bound moves its wholly inside cells to the fine surface, retargets in place when every cell
moves, and otherwise splits into `<footprintId>-<fineSurfaceId>`. A moved `LegacyExteriorV1` lower bound
becomes `SupportFloor`. A footprint cell that the region boundary crosses refuses the whole conversion
atomically with `TerrainFootprintStraddlesRegion`, `RecordId` and `SlotCell`.
Conversion never subdivides a footprint automatically.

`MapCellConversion.Finding` retains the typed rule requiring authored-difference acceptance, with
`ConversionClass` and `PresencePolicy` preserved through exception and finding forwarding. Its
existing `Class` and `Reason` prose are unchanged. A legacy arithmetic policy can require acceptance
even when the cell's class is exact. World cell coordinates use the signed 64-bit `Address`, while
`CellX` and `CellZ` remain patch-local coordinates. Conversion's authored-difference adapter uses its
first refused cell's finding and retains every refused cell in ordered `Findings`. Topology, seam and
corner-dependency admission likewise forwards the first concrete finding and its complete ordered list.

| Reason | Refused conversion rule | Identifiers when known |
| --- | --- | --- |
| `TerrainAuthoredDifference` | Legacy arithmetic or non-capture fallback requires explicit acceptance | `SurfaceId`, `Patch`, `Address`, `ActualInteger` subdivision |
| `TerrainCellMissing` | Source cell is absent | `SurfaceId`, `Patch`, `Address` |
| `TerrainCornerCutCreaseUnsupported` | A corner-cut crease crosses the regular fine lattice | `SurfaceId`, `Patch`, `Address`, `ActualInteger` subdivision |
| `TerrainCornerCutSubdivisionOdd` | Corner-cut paint requires an even subdivision | `SurfaceId`, `Patch`, `Address`, `ActualInteger` subdivision |
| `TerrainFootprintStraddlesRegion` | A region crosses part of a footprint cell | `RecordId`, `SlotCell` |
| `TerrainOwnerRetargetUnavailable` | No fine vertex preserves the exact owner point and height | Dependent `Patch`, patch-local `CellX` and `CellZ`, owner's `ExpectedPatch` and `Address` |
| `TerrainConversionRegionsMissing` | No conversion region was selected | `FieldPath`, `ActualCount` |
| `TerrainConversionRegionsOverlap` | Conversion regions overlap | `FieldPath`, later region's `Index`, earlier `OtherIndex` |
| `SurfacePaintPhysicalCompileUnsupported` | Conversion source is paint-only | `SurfaceId`, `FieldPath` |
| `TerrainBoundaryRecordIntersects` | A conversion region intersects a boundary record | `SurfaceId`, `RecordId` |
| `InteriorAliasUnsupported` | Standalone interior replacement targets an alias Room | `RecordId`, `SpaceId`, alias target's `RelatedRecordId` |

The shared `ValueOutsideRange` reason names `Subdivision` through `FieldPath`, `ActualInteger` and
inclusive `MinimumInteger` 2 and `MaximumInteger` 64. Required source and destination identity text
uses `SchemaValueRequired`, while equal identities use `AuthoredValueMismatch`. Both retain the failed
`FieldPath` and known surface identities. Geometric representation refusals use `GeometryNotRepresentable`
with the known surface, patch, address or patch-local corner. Duplicate footprint and seam identities use
`IdentityDuplicate` with `RecordId` and `SubjectKind`.
Empty conversion rectangles use `BoundsInvalid` and the region's `Index`. Interior editing reuses
`AuthoredOwnershipMismatch` for a wrong anchor, `ReferenceKindMismatch` for a non-Room target,
`AuthoredValueMismatch` for a replacement ID change, `ReferenceMissing` for a missing standalone
interior and `OperationKindUnsupported` for an unknown edit. Known record and space identities remain
structured on each refusal. The original messages and admission points are unchanged.

Terrain preparation also forwards complete topology, seam and patch-local findings. Its direct guards
preserve the same source set on refusal. Edit rectangles use `TerrainEditRegionOutOfBounds` with
`Patch`, patch-local `CellX` and `CellZ`, `Width` and `Depth`. Slot-domain failures carry `ActualInteger`
and `FieldPath = "SlotCells"`, while an in-domain slot outside the authored patch retains
`SurfaceAddressOutOfPatch`. Mismatched height or cell arrays use `SchemaArrayLengthMismatch`, the
failed field and actual and expected counts.

| Reason | Refused terrain rule | Identifiers when known |
| --- | --- | --- |
| `TerrainEditRegionOutOfBounds` | An edit rectangle or presence slot lies outside its domain | `Patch`, `CellX`, `CellZ`, `Width`, `Depth`, `ActualInteger`, `FieldPath` |
| `TerrainOwnerChangeStale` | The expected old corner owner no longer matches | Dependent `Patch`, `CellX`, `CellZ`, expected owner's `ExpectedPatch`, actual owner's `Address` |
| `TerrainSmoothingHaloUnavailable` | A smoothing halo corner is outside the surface or unloaded | `Patch`, world-corner `Address` |
| `TerrainTopologyWritesConflict` | The same topology identity is both upserted and removed | `FieldPath` |
| `TerrainAnchorStillInUse` | A removed patch still anchors a topology record | `Patch`, `RecordId` |
| `TerrainCornerOwnerAmbiguous` | Incident corners declare different existing owners | `SurfaceId`, world-corner `Address`, conflicting `Patch` and `ExpectedPatch` |

Owner self-address disagreement reuses `SurfaceSeamPositionMismatch`, with the dependent patch,
local corner and submitted owner `Address`. Disagreeing incident smoothing heights reuse
`GeometryVertexHeightMismatch`, with `Patch`, world-corner `Address` and smoothing `Operation`.
Duplicate owner changes or topology writes use `IdentityDuplicate`, selecting the first failed
predicate branch before a conflicting-write reason. Missing edit patches use `ReferenceMissing`
with `Patch` and `SurfaceId`. Unknown edit types use `OperationKindUnsupported` with `Operation`.
Surface upsert and removal conflicts follow the same branch-selection contract as patch writes.
Collection-count guards retain the count that failed the predicate in their diagnostic identifiers,
even if a faulty producer later reports a different value.
Reanchoring duplicate moves or duplicate destination records uses `IdentityDuplicate`, while an absent
source record uses `ReferenceMissing`, retaining the known `RecordId` and anchor `Patch`.

**Generation pinning and acquisitions.** A `MapStoredSurfaceSource` pins one manifest generation until it is
reopened. `MapScopedSurfaces` is a further step: it publishes a detached, immutable set of acquired facts,
so no later read touches the producer, even one pinned to the same generation.

**Later rounds.** R3 owns physics sidedness, floor support and headroom, picking, placement supports, movement
integration and tiled nav capture. R4 owns flooded and dry containment and water bed semantics over R2
space geometry. R5 provides free prefab transforms, interior domains and roof links over the R2 compiler
and queries in local space, described below. R8 owns bounded surface and asset residency, lighting, occlusion,
HLOD, render-origin
adapters and backend water captures. Each must keep legacy fallback cells non-capture and emit no physics,
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

`MapWaterDomain.Compile` throws `MapDocumentException` with the first concrete domain finding's
`Reason` and identifiers, plus the full ordered, owned `Findings` snapshot. Domain findings distinguish
missing components, empty domains, empty rectangles, ring cardinality, collinearity, self-intersection
and failed triangulation. Piece findings retain their stored `Index`, `SubjectKind` and `FieldPath`.
Placement admission uses `ValueNonfinite` or `ValueOutsideRange` with the failed component and
`ActualNumber`. Unrepresentable placed corners use `GeometryNotRepresentable`, with the corner's
`OtherIndex` when known. Effective water adapters also retain the body id as `EntityId`.
Count and pair-check refusals use `WorkCapacityExceeded`, `ActualCount`, `Limit` and a `Budget`
of `WaterDomainPieces`, `WaterDomainVertices` or `WaterDomainPairChecks`.
Forwarded lattice and placement refusals retain the original exception as the inner cause.

One canonical codec (`MapWaterSetJsonConverter` over `MapWaterJson`) writes the section in the monolithic file,
the tiled manifest and every identity. It writes media by key and bodies by id in ordinal order, each record's
members in a fixed order, and rects, rings and covers in stored order, so reordering media or bodies changes
neither bytes nor hash. A `surfaceY` of negative zero writes as zero, so one level has one identity. Every water
object is closed, and a null medium or body refuses as a document error. Lattice addresses are `{ "x", "z", "d" }`
objects whose coordinates, like rect bounds and cover slots, are canonical signed int64 decimal strings
(`MapSignedDecimal`, the grammar the topology codec uses), and they read back through `MapLatticeAddress.Create`.
Codec schema guards use the shared object, array, member, required-value, enum and canonical-int64
reasons. `FieldPath` identifies the actual water-relative JSON location, while writer required-value
guards retain their local field and a known body `EntityId`. Decimal guards retain `ActualText` and
the signed integer limits. Enum guards retain `ActualText` and `EnumType`. A numeric value that cannot
be a finite float uses `ValueNonfinite`, retaining `ActualNumber` when representable as a double.
The converter throws `MapWaterJsonException`, a `JsonException` subtype with the concrete `Reason`
and scalar identifiers. It retains the original cause, and document readers preserve every known
identifier through their common source-context copy operation. Other scalar conversion failures use
`SchemaMalformed` without inventing a JSON location.
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
The body's additive `Finding` carries the original failed rule and body ownership without changing
`Status`, `Detail` or its digest. A complete body has no finding. Compile capacity findings name
`MaxPieces`, `MaxPieceVertices`, `MaxFootprintCells`, `MaxBoundCells`, `MaxBoundFaces`, `MaxPairChecks`
or `MaxTriangles`, with measured `ActualCount` and inclusive `Limit`. A single-cell source-face refusal
names `MaxSourceFacesPerBound` and the measured face count. Unverified cover or footprint discovery
uses `PatchUnavailable` or `AcquisitionIncomplete`, and uncovered bound area uses `SpaceCoverageMissingBound`.
`MapBoundDocumentValidation.ValidateEffectiveWater` forwards the first refused body's finding and its
typed context, retaining the original detail and the gate's first-body admission order.

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
The refusal is `AssetStoragePathReserved`, with `ResourceId`, normalized `Path`, `StoragePath` and `DocumentForm`.

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

Sculpt shape guards throw `MapTerrainOverridesJsonException`, derived from `JsonException` and sharing
`MapDocumentRefusal`. They distinguish `SchemaObjectRequired`, `SchemaArrayRequired`, `SchemaMemberMissing`
and `SchemaArrayLengthMismatch`. Identifiers include relative `FieldPath`, tile array `Index` and `SculptTile`
only after both coordinates were read. Delta length refusals retain observed `ActualCount` and exact
`ExpectedCount`. Document wrappers preserve those fields.

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

`MapPrefabSet` payload 1 and expansion version 1 retain instances, typed bindings, overrides and transfers.
Instance poses use free XYZ, yaw radians and finite positive uniform scale. `ParentSpace` may be absent for a
parent-independent rigid prefab. Rooms, water and `$parent` occurrences require explicit authored context.
Loading, validation and closure reads never reserve new identities. Bindings carry supplied world IDs and optional
placement-only numeric IDs as exact decimal int64 strings. Removed instances and retired transfers remain persisted.

A manifest `Prefab` resource carries payload 1. `MapAssetClosure.Load` verifies every resource digest once, checks
acyclic declared dependencies, decodes bounded prefab snapshots and validates every leaf resource edge before
publishing. `GetPrefab` returns a detached copy of that verified snapshot. Prefabs have no mesh of their own and
cannot contain nested instances. Mesh, collider, selection, LOD, material, support and light resources belong to leaves.
Missing closure lookups use `ReferenceMissing`, while uncaptured payload borrows use `AssetPayloadNotCaptured`.
Both identify the requested asset or resource. A missing dependency carries its source `ResourceId` and missing
`DependencyId`. A cycle uses `AssetDependencyCycle` and an ordinal read-only `ResourceIds` snapshot of all resources
left unresolved, including resources blocked behind the cycle. Duplicate closure identities use `IdentityDuplicate`
and the `asset` or `resource` namespace in `SubjectKind`.

A resource digest mismatch uses `DigestMismatch`, `DigestKind` `resource bytes`, `ResourceId`, the reference `Path`,
`ExpectedHash` and `ActualHash`. Source read failures use `AssetPayloadUnreadable` with `ResourceId`, `ExpectedHash`
and the reference `Path`. The original error remains the inner exception. This resource-read boundary also wraps
`MalformedDataException` from an `IMapAssetSource` callback, preserving its concrete cause and the existing
`MapDocumentException` catch contract.

`MapPrefabFile.Read` and `Write` use a closed codec. The payload keeps its logical asset ID. A resource ID
may be that bare ID or exactly `prefab/<escaped-logical-id>/<sha256>`. The escape is the world-ID UTF-8
percent escape, and the version suffix is 64 lowercase hex characters. Read independently verifies the
suffix against the bytes. Every other identity uses `PrefabPayloadIdMismatch`, with `ResourceId` and
the payload's `EntityId`. Children have a typed stable key, one local pose and scoped
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

`MapRoofPolicy.Evaluate(resources, view, observer, mode)` returns one `MapRoofIntent` per inventoried roof,
including unlinked shelters. `Auto` uses the acquired R2 room membership and world height. A link matches its
direct rooms and the union of exactly its attached groups. Groups may join rooms in different instances, but
unrelated groups, portals and parent membership do not widen a link. Any eligible link may conceal its roof.
Missing or ambiguous membership retains its status and witness and keeps all Auto roofs visible.
`AlwaysVisible` and `AlwaysHidden` still enumerate the complete inventory.

The four intent flags have separate consumers. Hidden roofs have `ColourVisible = false`, `CastShadows = true`,
`CameraBoomBlocks = false` and `SkyPickingBlocks = true`. Visible roofs set all four true. This policy never
changes collision, interaction geometry or static registration. R8 owns colour, shadows, camera boom and sky-ray
integration.

Typed Light and Support resource payloads begin with the discriminant `"resourceSchema"`, valued
`kemap/light-resource` or `kemap/support-resource`. Their writers emit it first and their closed decoders require it.
`MapAssetClosure.Load` decodes only bytes that carry it, so every other resource, JSON or not, stays opaque as
released. Prefab leaf lights and typed support refuse an opaque resource with `AssetTypedPayloadOpaque`,
`ResourceId`, `ExpectedSchema` and the `light` or `support` role in `SubjectKind`. A missing or misplaced typed
discriminant uses `SchemaValueRequired`, `FieldPath` `$.resourceSchema`, `ResourceId` and `ExpectedSchema`.
Physics support also refuses an opaque resource with `AssetTypedPayloadOpaque`, `ResourceId` and `ExpectedSchema`.

`MapEffectiveAnnotations.Lights(resources)` and `Markers(resources)` return immutable records in stable ID order.
Both carry their own payload version. Light positions and radii are owner-frame metres, independent of leaf
`SourceUnitsToMetres`. Expansion composes the precise owner pose once, freezes the position once and scales the
radius once in binary64 before rounding to float. Colour, intensity and shadow intent remain unchanged.
`LightResourceIds` use the existing scoped and bound implicit Light children. Explicit children coexist under
distinct keys. Materialized transfers reuse the captured descriptor frame without composing the owner again.
Markers retain exact XYZ, yaw, name, role, enabled state, ordered tags and optional instance provenance. They do
not become `MapSpawn`. R8 renders lights.

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
removed or unsupported owner refuses the load, build or edit, disabled markers included. The refusal carries
`MarkerSupportUnresolved`, `MarkerId` and the typed `SupportStatus`. Marker payload validation keeps its string
API and also produces typed findings for every marker and support-binding rule. Its throwing adapter takes the
first finding's `Reason` and identifiers and attaches the full ordered read-only `Findings` snapshot.
Effective markers carry
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
The metadata uniqueness guard carries `IdentityDuplicate` or `MarkerNameDuplicate`, the repeated `MarkerId` or
`MarkerName`, and colliding `MarkerIds` as a read-only snapshot. Its ordered `Findings` retain every collision, IDs
first and then names, each in ordinal order. Aggregate validation adapters forward the first finding's
reason and identifiers and retain the complete ordered `Findings` snapshot.

Compatibility: duplicate marker names were accepted before R6. A document whose repeated prefab instances share a
marker name now refuses to load and build until each repeated instance carries such an override. Format 8 moves every
native root identity once, as format 7 did, so identity-keyed caches and navigation captures rebuild after migration.

`MapPlacePrefab.InitialOverrides` lets one placement request carry keyed overrides, such as that marker rename, so a
second instance is accepted in a single placement with no intermediate duplicate. Duplicate keys refuse before any
binding is allocated, with `PrefabOverrideDuplicate`, the `InstanceId`, typed `Child`, `Index` and
`FieldPath`. Missing lists or null entries use `AuthoredValueRequired` with the known instance, path and index.
The overrides persist only as the instance's `Overrides`. The `prefab_place` tool accepts them as
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
Blank configured roles or legacy Surface owners use `SchemaValueRequired` with the offending `FieldPath`.

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

Preparation refuses a resolver (1, 1) document with `MapDocumentRefusal.ResolverContractMismatch`, its
`PayloadVersion`, `ResolverVersion` and `ExpectedResolverVersion`. Adopt authored support through
`MapResolverAdoption.ConvertToAuthoredSupport` before editing markers. A live prefab marker belongs to its instance, so
set and remove refuse with `MarkerPrefabOwnershipProtected`, `MarkerId` and `InstanceId`, and retain the
`prefab_override` guidance. An ID held by any marker reservation, live or dormant, refuses with `IdentityReserved`
and `MarkerId` unless it is the active transfer of the standalone marker being edited, so a set never seizes a
retired reservation.
A missing standalone removal target uses `ReferenceMissing` and `MarkerId`. A non-native document uses
`MarkerNativeDocumentRequired` and `Operation`. Removing a transferred marker retires its transfer and removes
only its own frame. Payload rules, unique IDs and names, binding owners and every support-bound height are
validated before the result exists. `MapPrefabJson.ReadMarker`
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
A choice-set mismatch carries `MarkerAdoptionChoiceSetMismatch`, `ActualCount`, `ExpectedCount` and a read-only
`SourceIds` snapshot in Spawn then PlayerSpawn order, retaining equal IDs from different lists. A missing destination
or height choice carries `MarkerAdoptionChoiceMissing`, `SourceId` and the source kind in `SubjectKind`. Repeated
destinations use `IdentityDuplicate` with `MarkerId`, and repeated names use `MarkerNameDuplicate` with `MarkerName`
and the destination `MarkerId`.

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

Foliage converter admission throws `MapFoliageJsonException`, a `System.Text.Json.JsonException` subtype with a
required `MapDocumentRefusal Reason`. Existing JSON catches still apply. The document reader preserves the reason
and available identifiers on its outer `MapDocumentException`.

| Foliage reason | Refused rule | Identifiers when known |
| --- | --- | --- |
| `FoliageSectionNull` | An explicit null section instead of omission | `FieldPath` |
| `FoliageChunkUnavailable` | Reading advertised nonresident density | `LayerId`, `FoliageChunk` |
| `FoliageChunkOutOfDomain` | An advertised or resident chunk outside its layer's sample domain | `LayerId`, `FoliageChunk`, `FieldPath`, `Index`, `Width`, `Depth`, checked axis value and integer limits |
| `FoliageNestedSequenceUnsupported` | A sequence containing another sequence | `Operation`, `RecordType` |
| `FoliageEffectiveContextRequired` | Foliage generation or finalization without captured effective surfaces | `Operation`, `SourceType` |
| `FoliagePolicyPreviewIncomplete` | A policy change whose preview cannot issue a complete digest | `LayerId`, `FoliageStatus` |
| `FoliagePolicyDigestStale` | A differential token for another document snapshot or proposal | `LayerId`, `ExpectedHash`, `ActualHash` |
| `FoliagePackOffsetMismatch` | A stored chunk offset differing from canonical pack order | `LayerId`, `FoliageChunk`, `Index`, `FieldPath`, `ActualInteger`, equal `MinimumInteger` and `MaximumInteger` for the required offset |
| `FoliageSaveSnapshotChanged` | Publication after foliage changed from the prepared save | `ExpectedHash`, `ActualHash` |
| `FoliageRegionMembershipMismatch` | Stored owner region or memberships differing from the density lattice | `LayerId`, `FoliageChunk`, submitted owner `Tile` |
| `PinnedContentUnavailable` | A required pinned foliage pack whose read did not succeed | `FoliageReadStatus`, `LayerId`, `Tile`, `ExpectedHash` |
| `FoliagePartialSaveUnsafe` | Dropping or replacing unavailable advertised density during a partial save | `LayerId`, `FoliageChunk`, `ExpectedHash` |
| `FoliageRectangleOutOfBounds` | A density edit rectangle outside the layer raster | `LayerId`, `FieldPath`, `CellX`, `CellZ`, raster `Width`, `Depth`, checked endpoint and integer limits |
| `FoliagePaddingNonzero` | Nonzero resident density outside the layer domain | `LayerId`, `FoliageChunk`, `FieldPath`, byte `Index`, chunk-local `CellX`, `CellZ`, `ActualInteger`, `Width`, `Depth` |
| `FoliageSectionIncomplete` | A section that is not fully buffered | `FieldPath`, with `ActualByteCount` unset because the complete size cannot be measured |
| `FoliageSectionByteLimitExceeded` | A complete section beyond its UTF-8 byte ceiling | `FieldPath`, `ActualByteCount`, `Limit` |
| `SchemaPayloadVersionUnsupported` | A layer payload or known policy version other than version 1 | `LayerId`, `FieldPath`, `ActualVersion`, `SupportedVersions` |
| `ValueNonfinite` | A nonfinite layer scalar or model weight | `LayerId`, `FieldPath`, `ActualNumber`, model `Index` and `AssetId` when applicable |
| `ValueOutsideRange` | A nonpositive positive-only scalar or weight, negative clearance or fade, invalid extent, empty model list or material ID 0 | `LayerId`, `FieldPath`, actual value or count and checked numeric limits |
| `BoundsInvalid` | A finite maximum scale below the positive minimum, or an empty or reversed clearance footprint | `LayerId`, `FieldPath`, `ActualNumber`, `MinimumNumber`, `MaximumNumber` |
| `SchemaEnumUnsupported` | Density rows that do not advance positive world Z, or an undefined material, support binding or clearance kind | `LayerId`, `FieldPath`, `EnumType`, `ActualInteger`, material `NumericId` when applicable |
| `ReferenceMissing` | A missing bound resource, asset or surface, or an undeclared allowed material | `LayerId`, `FieldPath`, `Index` where present and the checked identity |
| `ReferenceKindMismatch` | A bound material resource or declared support surface of the wrong kind | `LayerId`, `FieldPath`, `Index` where present, identity and actual kind |
| `FoliagePolicyUnsupported` | A sampling policy identity other than `legacy-tile` | `LayerId`, `FieldPath`, `PolicyId`, `ExpectedPolicyId` |
| `SchemaValueRequired` | A missing policy, model, material, ordinal, rule, mask, binding or owner field | `LayerId`, `FieldPath`, known item identity and index |
| `IdentityDuplicate` | A repeated model, material, ordinal surface, rule, mask, owner support or resident chunk | `LayerId`, `FieldPath`, `Index`, known identity and owner kind/scope where checked |
| `AuthoredOwnershipMismatch` | A foliage owner outside World scope, or a support binding carrying fields of another kind | `LayerId`, `FieldPath`, `Index`, known owner or binding identity and reference scope |
| `AuthoredReferenceKindInvalid` | A foliage owner outside Placement, Surface, Record or Marker | `LayerId`, `FieldPath`, `Index`, `EntityId`, `ChildKind`, `ReferenceScope`, `ExpectedReferenceScope` |
| `OrderInvalid` | An allowed material ID no greater than the previous ID | `LayerId`, `FieldPath`, `Index`, `ActualInteger`, `PreviousInteger` |
| `WorkCapacityExceeded` | Model, material, rule, mask, owner-support or chunk count capacity, or a nonfinite accumulated model weight | `LayerId`, `FieldPath`, `Budget`, actual count and limit, or accumulated `ActualNumber` and `MaximumNumber` |

The [schema refusal table](#loud-failures) describes the codec's shared reasons. Foliage `FieldPath` starts at
`$` for the section and retains real member names and array positions. `LayerId` is available only after the ID has
been read, and `FoliageChunk` only after both coordinates have been read. `Index` names the innermost array position
known at the guard. Repeated exact member names use `SchemaMemberDuplicate` with the member path.
Foliage required-member failures use `SchemaValueRequired`.
Storage helpers supplied only a prose label leave unavailable paths and identities unset. Known inner document
refusals retain their reason, identifiers and original cause through the converter. Untyped converter failures
use `SchemaMalformed` with the reader's path. Enumeration refusals use `SchemaEnumUnsupported` with `EnumType`
and the submitted `ActualText` when it is a string. Binding writers distinguish a missing own member
(`SchemaValueRequired`), a member of another binding kind (`AuthoredOwnershipMismatch`) and an undefined kind
(`SchemaEnumUnsupported`), carrying the known `LayerId`, local `support` member path and surface or space identity.
Capacity checks use `WorkCapacityExceeded` with `Budget`, `ActualCount` or `ActualByteCount`, and inclusive `Limit`.
The section byte guard shares `FoliageSectionByteLimitExceeded` with the converter admission guard.
`SupportedVersions` is an owned read-only snapshot on the exception and finding types. Numeric identifiers describe
only representable input values, while integer bounds describe the domain the guard actually checks.

Stored layer references distinguish `AuthoredValueMismatch` for a chunk naming another layer from
`ReferenceMissing` for a pack naming an undeclared layer. The former carries the containing `LayerId` and submitted
`ActualText`, the latter the missing `LayerId` and pack `Tile`. Both identify `FieldPath = "layerId"`.
Storage collection capacities carry the first failed `Chunks` or `Packs` field, its `Budget`, `ActualCount` and
inclusive `Limit`. Pack references distinguish missing `LayerId`, inconsistent byte length and duplicate layer/region
identity. Byte-length refusals retain `ActualByteCount` and `ExpectedByteCount`.
Chunk references select the first invalid X or Z key, empty or oversized region list, duplicate chunk identity,
missing owner pack or mismatched pack digest. Key limits are inclusive at 0 and 32767. Region-count limits are
inclusive at 1 and 4096. Count failures retain `ActualCount` and the numeric domain, with `Budget` and `Limit`
for excess work. Missing packs retain the owner `Tile`, and digest mismatches retain both hashes. Pack row-count
checks retain `ActualCount` and `ExpectedCount`, including zero when no rows reference the pack. Resident admission
selects `ReferenceMissing` for an unadvertised key and `DigestMismatch` for different resident bytes.
Region-range guards distinguish unrepresentable coordinates, reversed bounds and the 4096 membership cap, retaining
`LayerId` and `FoliageChunk`. Coordinate failures identify `RegionX` or `RegionZ`, the actual integer and signed
32-bit bounds. Capacity failures retain `Budget = "RegionMemberships"`, `ActualCount` and `Limit`.
Complete-density checks retain missing resident and advertised counts. A mismatched pair retains the resident
`FoliageChunk`, `ExpectedFoliageChunk` and row `Index`, selecting `AuthoredValueMismatch` for different keys or
`DigestMismatch` with expected/actual hashes for different density. Storage validation forwards the first concrete
finding and its complete ordered snapshot. Save publication retains both foliage snapshot hashes, and required pack
read failures retain their exact status and pin.

An advertised nonresident density read uses `FoliageChunkUnavailable` with `LayerId` and `FoliageChunk`.
Repeated advertised chunk keys use `IdentityDuplicate` with those identifiers and `SubjectKind`.
The version-only migration uses `SchemaValueRequired` for a missing or noninteger `formatVersion` and
`SchemaPayloadVersionUnsupported` with `ActualVersion` and `SupportedVersions` for an unsupported integer.
Effective owner validation retains typed findings beside its unchanged string projection. Findings carry
`LayerId`, local `FieldPath` and the surface, space or owner identity. Missing surfaces use `ReferenceMissing`,
while a declared surface of the wrong role uses `ReferenceKindMismatch`. A missing space ID uses
`SchemaValueRequired`. World owner checks distinguish `AuthoredOwnershipMismatch` for scope,
`AuthoredReferenceKindInvalid` for a forbidden child kind and `ReferenceMissing` for an absent owner, carrying
`ChildKind`, `ReferenceScope` and `ExpectedReferenceScope`. Aggregate adapters retain prose until their full
producer is typed.

Region-pack admission distinguishes invalid key coordinates (`ValueOutsideRange`), duplicate encoded keys
(`IdentityDuplicate`), decoded key order (`OrderInvalid`), short or inconsistent lengths
(`SchemaArrayLengthMismatch`), byte or chunk capacity (`WorkCapacityExceeded`) and invalid magic
(`SchemaMalformed`). Key refusals retain `FoliageChunk`, `Index` and the failed `Key.X` or `Key.Z` domain.
A raw density digest mismatch retains `ExpectedHash` and `ActualHash`. A malformed digest name retains
`ActualHash`. Set-level version, layer identity, layer-count and density-count findings also retain their typed
fields alongside the string API. `MapFoliageValidation.ValidateFindings` and `ValidateBoundFindings` return
complete owned read-only snapshots in the same order as their unchanged string projections.
Layer scalar findings select the first failing branch and field without changing their prose projection.
Bound material findings retain the material ID as `ActualInteger`, the `ResourceId`, `ExpectedResourceKind`
and available `ActualResourceKind`. Clearance asset findings retain the rule `EntityId` and `AssetId`.
Bound support findings retain `SurfaceId`, including owner supports, exception masks and compatibility
ordinals. A wrong declared surface role also carries `EnumType` and `ActualInteger`.
Policy admission checks the ID before its version. A missing ID uses `SchemaValueRequired`, another
policy uses `FoliagePolicyUnsupported`, and an unsupported version of `legacy-tile` uses
`SchemaPayloadVersionUnsupported`. `PolicyId` and `ExpectedPolicyId` survive finding projection and source
wrapping. Count findings distinguish `Materials` from `AllowedVisibleMaterials`. The supported accumulated
model-weight maximum is `float.MaxValue` and is carried as `MaximumNumber`.
Compatibility ordinals retain `SurfaceId` and a nonnegative ordinal domain. Rule and mask findings retain
their `EntityId`, while rule asset and placement-tag findings also carry `AssetId` or `ActualText`.
Repeated owner supports retain the owner's `EntityId`, `ChildKind` and `ReferenceScope`. Advertised chunk
domain findings retain the key and the first invalid axis with its inclusive domain, without loading density.
Resident domain checks carry the same axis metadata. Nonzero resident padding uses `FoliagePaddingNonzero`
with the byte index and chunk-local coordinates of the first nonzero padding byte. Local binding findings
retain their containing layer and field path, including owner supports and masks. Owner admission checks
missing value, World scope, permitted child kind and nonempty ID in that order. Scope failures reuse
`AuthoredOwnershipMismatch`, and forbidden kinds reuse `AuthoredReferenceKindInvalid`. Clearance footprint
findings select the first nonfinite coordinate or finite collapsed dimension and retain the item identity.
The document-level foliage collector forwards every local finding. Whole-document and native-bound
adapters now forward the first typed finding and its ordered snapshot. Editing adapters keep their
own publication boundaries.
Brush admission uses `AuthoredValueMismatch` for a submitted `LayerId` different from the target layer,
with the target in `LayerId` and submitted ID in `ActualText`. Nonfinite brush and raster scalars use
`ValueNonfinite`, finite nonpositive cell size or radius and out-of-range hardness use `ValueOutsideRange`.
Empty raster or rectangle dimensions use `BoundsInvalid`. Hardness accepts 0 and 1. Rectangle domain
failures use `FoliageRectangleOutOfBounds`, retaining the raster dimensions, submitted cell and first
failing coordinate or endpoint. Raster work uses `WorkCapacityExceeded` with `Budget` equal to
`addresses per raster edit` or `chunks per raster edit`, `ActualCount` and inclusive `Limit`.
Required edit lists, steps, layers and density values use `SchemaValueRequired` with `FieldPath` and the
document identity or step index when available. Missing layer lookup uses `ReferenceMissing` with
`LayerId`. Wrong density lengths use `SchemaArrayLengthMismatch` with the single captured `ActualCount`
and `ExpectedCount`. Values outside 0 through 255 use `ValueOutsideRange` with the row-major `Index`,
rectangle-local `CellX` and `CellZ`, `ActualInteger` and inclusive limits. These refusals precede density writes.
A missing differential token uses `SchemaValueRequired` with `FieldPath = "ExpectedDifferentialDigest"`.
An incomplete preview retains its actual `FoliageStatus`, and a stale token retains the expected and
submitted digests. Unsupported edit kinds use `OperationKindUnsupported` with `RecordType`, while a
nested sequence uses `FoliageNestedSequenceUnsupported`.
Negative context options throw `MapFoliageContextArgumentException`, still catchable as
`ArgumentOutOfRangeException`, with `Reason = ValueOutsideRange`, `FieldPath`, `ActualInteger` and
`MinimumInteger = 0`. Its released `ParamName = "limits"` and null BCL `ActualValue` are retained.
Zero is admitted for each option, with excess document demand still reported through the existing status.
Stored JSON work limits retain `Budget = "stored foliage UTF-8 bytes"`, `Limit`, measured attempted
`ActualByteCount` or membership `ActualCount` when available. Negative membership count or byte limit
uses `ValueOutsideRange` with the first failed field. A save that materializes nonresident chunks forwards
the first chunk-validation finding and its full ordered snapshot, including domain or padding identifiers.

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
R7 capture requires all terrain, content and foliage resident. Partial snapshots return `PartialResidency` before
capture. R8 must add a windowed context constructor with an immutable closure and explicit completeness contract
before claiming streamed native generation. Sparse storage and partial save/verification are not partial-generation
proof. That constructor must preserve public finalization, full-request support selection, canonical surface identity
and the observer atomic-completion contract described here.

Only `MapFoliageStatus.Complete` publishes usable samples. MissingSupport, Unavailable, PartialResidency, Ambiguous,
CapacityExceeded, NotRepresentable and Invalid refuse without truncation, a substituted floor or budget inflation.
Outside a known domain produces no sample. `MapFoliageLimits` retains per-chunk ceilings. Query also caps total samples
at 262,144 and retained bytes at 67,108,864 before retention. `MapFoliageDifferential.Compare` caps total candidates at
64,000,000 and changes at 4,000,000, with reconciliation bytes at 134,217,728 per stable 64 by 64 lattice-address block. Byte charges are
conservative accounting, not measured allocation. Generate, Query and Compare observer rows remain provisional until
the enclosing result is Complete. A refusal discards all usable output and issues no differential token.

### Compatibility support and paint

Payload 1 supports `legacy-tile/1` with explicit boundary/material metadata, resolver identity (1, 2) and
`AuthoredBindingsV2`. It preserves legacy interpolation, smoothed global-corner central-difference normals,
and additive NoDraw triangles. NoDraw and Blocked flags do not veto density under this compatibility policy.
Full-request prepared support selection is shared with the
seat query. A later geometric policy needs its own version and differential. Equal-priority world paint chooses the
ascending ordinal ID winner. R8 must preserve that winner rather than choose paint by draw order.

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
and release it only after its last membership leaves. Actual residency, upload, viewport drawing, captures and appearance
remain R8/G3. The bounds are cosmetic and never union into world/nav bounds or static descriptors.

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
The private comparison boundary and its seven test-only references are defined only in the
[oracle harness README](../KhaozEngine.MapDoc.OracleHarness.Tests/README.md). That harness is not an importer or actual
source acceptance. Public CPU proofs certify no pixels or windowed acceptance.

| Later owner | Remaining contract or proof |
| --- | --- |
| R8, R7-R1 | Windowed context constructor with immutable closure/completeness and full-request support semantics before streamed native generation |
| R8 and [#1474](https://github.com/APKiwiOrg/KhaozEngine/issues/1474) | MapDoc interior-cut adapter, actual residency/upload and viewport rendering/captures |
| R8 and G3 | Appearance with live motion profiles, styling and shadow flags |
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
It distinguishes missing `SchemaMemberMissing`, incorrectly typed `SchemaValueRequired` and unsupported
`DocumentMigrationSourceVersionInvalid`, with `FieldPath`, known `ActualVersion` and owned `SupportedVersions`
containing 4 and 5.
v5 -> v6 (`MapWaterMigration.Upgrade`) adds no member, so a migrated document has no `water` section and keeps
legacy global water. It accepts only format 5 or 6 input.
Missing or non-integer water migration versions use `SchemaValueRequired` with `FieldPath`
`formatVersion`. Unsupported integer versions use `SchemaPayloadVersionUnsupported`,
`ActualVersion` and an owned `SupportedVersions` snapshot containing 5 and 6.
v6 -> v7 (`MapPrefabMigration.Upgrade`) clones input and changes only `formatVersion`. It accepts format 6 or 7.
Format 7 moves every native root digest and resolver-2 authored identity once, including maps without prefabs.
v7 -> v8 (`MapMarkerMigration.Upgrade`) clones input and changes only `formatVersion`. It accepts format 7 or 8 and
reads no source, terrain, closure or role. Format 8 again moves every native root digest and resolver-2 authored
identity once. Rebuild identity-keyed caches and navigation captures after either migration. Absent prefab sections preserve
analytic execution and document hashing. Portals remain AuthoredOpen, with live state gated by #1363.
Marker migration distinguishes missing or non-integer `formatVersion` (`SchemaValueRequired`) from an
unsupported integer (`DocumentMigrationSourceVersionInvalid`), retaining `FieldPath`, known `ActualVersion`
and owned `SupportedVersions` containing 7 and 8. Spawn adoption on a partial document uses
`CompleteDocumentRequired`, `DocumentId` and `Operation = "ConvertSpawns"` before copying records.
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
Embedding byte and nesting refusals use `WorkCapacityExceeded`, with `Budget` `MaxEmbeddingBytes` or
`MaxEmbeddingDepth` and an inclusive `Limit`. Byte refusals carry the minimum required `ActualByteCount`
known at the failed token. Depth counts containers, admitting 16 and refusing 17. A negative byte budget
uses `ValueOutsideRange`, `FieldPath` `maxBytes`, `ActualInteger` and minimum 0.
An allocator returning another byte length uses `SchemaArrayLengthMismatch`, `SubjectKind` `embedding allocation`,
`ExpectedByteCount` and available `ActualByteCount`. Encoding a payload that shrank after measurement uses
`StorageEmbeddingChanged` with both byte counts. Unsupported scalar types or undefined JSON values use
`SchemaMalformed`, with known `RecordType` or `ValueKind`. Nonfinite scalar values use `ValueNonfinite`,
their `RecordType` and `ActualNumber`. Numeric formatting and default encoder progress remain internal invariants.

Embedding array admission uses `SchemaArrayRequired` with `FieldPath` `$.surfacePatches`. The patch-count
budget uses `WorkCapacityExceeded`, `Budget` `MaxEmbeddedPatches`, `ActualCount` and inclusive `Limit`.
Invalid configured patch or aggregate byte budgets use `ValueOutsideRange`, naming `maxPatchBytes` or
`maxEncodedBytes` with `ActualInteger` and `MinimumInteger`. A non-object DOM patch uses
`SchemaObjectRequired` with its item path and `Index`. Repeated embedded keys reuse `IdentityDuplicate`
with `Patch`, `Index` and the key's path.
Streaming converter guards throw `MapSurfaceEmbeddingJsonException`, a `JsonException` subtype sharing
`MapDocumentRefusal`. It retains its producer as the inner exception and document readers preserve the
producer's reason and identifiers. Consumer-built message-only causes retain the JSON fallback.

Surface page envelope refusals keep `MapDocumentException` and the released developer prose.

| Reason | Refused input | Identifiers |
| --- | --- | --- |
| `SchemaObjectRequired`, `SchemaArrayLengthMismatch` | Wrong object shape or member count | `FieldPath`, known `ActualCount` and `ExpectedCount` |
| `SchemaMemberMissing`, `SchemaMemberDuplicate`, `SchemaMemberNull` | Missing, repeated or null member | `FieldPath` |
| `AuthoredValueMismatch`, `BoundsInvalid` | Different surface identity or cover | `FieldPath`, decoded `SurfaceId` |
| `SchemaArrayLengthMismatch` | Index count differing from its declared count | `FieldPath`, `SurfaceId`, `ActualCount`, `ExpectedCount` |
| `ValueOutsideRange` | Empty page bytes | `FieldPath`, `ActualByteCount`, minimum 1 |
| `WorkCapacityExceeded` | Page bytes beyond their inclusive capacity | `FieldPath`, `Budget` `MaxPageBytes`, `ActualByteCount`, `Limit` |
| `SchemaArrayRequired`, `SchemaValueRequired` | A non-array page field or missing required page, entry, identity or metadata collection | `FieldPath`, known `SurfaceId`, `Patch`, `Index` |
| `ValueOutsideRange`, `WorkCapacityExceeded` | Empty or over-cap page and entry counts | `FieldPath`, submitted integer and minimum 1, or `Budget` `MaxEntries`, `ActualCount`, inclusive `Limit` |
| `WorkCapacityExceeded` | Decoded dependency, record, incidence or space arrays beyond their metadata cap | `FieldPath`, `Budget` `MaxMetadataEntries`, `ActualCount`, inclusive `Limit` |
| `StorageDirectoryPagesOverlap` | Index page covers overlapping within a directory | `FieldPath`, `Index`, `OtherIndex` |
| `AuthoredOwnershipMismatch`, `BoundsInvalid`, `OrderInvalid` | An entry for another surface, outside its page or slot, with inverted heights, or outside canonical key order | `FieldPath`, `SurfaceId`, known `Patch`, `Index` |
| `IdentityDuplicate` | Repeated dependency keys, record IDs, incident references or space IDs | `FieldPath`, `SurfaceId`, `Patch`, `Index`, actual and distinct counts |

Tiled surface packing uses `ValueOutsideRange` with the first failed `FieldPath` (`IndexBlockSlots` or
`DirectoryBlockPages`), `ActualInteger` and inclusive `MinimumInteger` 1 and `MaximumInteger` 16.
Checked page-block overflow uses `GeometryNotRepresentable`, `Operation` `PackSurfaces` and `FieldPath`
`Covers`. Window arithmetic overflow keeps that reason with `Operation` `LoadSurfaces` and `FieldPath`
`Window`. A whole-surface semantic mismatch reports `DigestMismatch`, `SurfaceId`, `DigestKind`
`surface semantics` and both hashes. An unavailable directory or index reports `PatchUnavailable`,
`SurfaceId`, `PatchStatus` and its pinned `ExpectedHash`. Payloads also identify `Patch` and preserve a
known inner refusal's reason and identifiers instead of replacing them with the status.

Page cardinality guards capture interface-backed counts once. Their reported counts describe the failed
comparison even when a faulty producer returns a different count on a later read. A page holds at most
256 entries and at most 1,048,576 encoded bytes. Decoded metadata arrays admit at most `MaxPageBytes / 24`
items. Encoding and decoding exercise these inclusive boundaries through the same page operations.
Whole surface saves forward the first `MapTopologyReferenceValidator.ValidateFindings` result as their
reason and identifiers, retaining the complete ordered read-only `Findings` snapshot and the first finding's
developer prose. Exact owner-position overflow uses `GeometryNotRepresentable`, `Operation` `SaveSurfaces`
and `FieldPath` `CornerDependencies`. Surface scope conversion overflow uses the same reason with
`Operation` `FindPatches` and `FieldPath` `scope`. Raw saves continue to certify references and exact owner
positions without certifying geometric seams or equal heights.
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
the ones where the tile size is not an integer multiple of the sculpt span. Ownership refusals report
the same float origin coordinates used by the ownership check in `PositionX` and `PositionZ`.

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
surfaces-only window, with `DocumentStaleWindow` when the current manifest cannot be read or has changed since the
window was loaded. It carries manifest `Path`, pinned `ExpectedHash` and `ActualHash` only when the bytes were read,
so a window never overwrites another writer's commit
([#1310](https://github.com/APKiwiOrg/KhaozEngine/issues/1310)). The save lock alone does not give that
guarantee.

Raw partial surface saves permit payload edits only after outgoing references, reverse page knowledge,
reverse dependants, structure and record checks. A raw save certifies reference integrity, including exact
XZ agreement between each corner owner's address and its dependent corner, and never geometry. It refuses a
dependency position that disagrees with `SurfaceSeamPositionMismatch`, the dependent `Patch`, owner
`ExpectedPatch` and `Address`, and dependent corner `CellX` and `CellZ`. An address absent from its owner
uses `SurfaceSeamVertexUndeclared` with the same context. A missing owner surface uses
`ReferenceMissing` instead. An unrepresentable owner position carries `GeometryNotRepresentable`,
`Operation` `SaveSurfaces` and `FieldPath` `CornerDependencies`. Partial-save refusals distinguish unloaded
outgoing patches, unknown reverse knowledge, unloaded reverse dependants and unsupported surface, structure
or record edits through the storage reasons below. Missing outgoing patches or indoor-span parents carry
`ReferenceMissing` with their known patch, record and surface. A parent of the wrong topology type carries
`ReferenceKindMismatch` with `RecordType` and `ExpectedRecordType`. A refused save leaves existing files
byte-identical. Geometry, seam heights and separation require complete native transactions.

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

`MapSurfaceScopeArgumentException` derives from `ArgumentException` and carries `MapDocumentRefusal`.
Scope admission selects the first failed predicate in its original order. Nonfinite coordinates carry
`ValueNonfinite`, `FieldPath` and `ActualNumber`. Unordered bounds carry `BoundsInvalid` with the refused
maximum and inclusive `MinimumNumber`. Null roles or limits and blank space IDs use `SchemaValueRequired`.
An undefined role uses `SchemaEnumUnsupported`, `EnumType` and `ActualInteger`. Negative query allowances
use `ValueOutsideRange`, the nested `Limits` path, `ActualInteger` and inclusive `MinimumInteger` zero.
Equal bounds and zero allowances are valid. Record copies are readmitted when used by a query or digest.

Surface storage directory references select the first failed object, member-count or required-value rule,
carrying `FieldPath` and the row `Index`. Count mismatches expose `ActualCount` and `ExpectedCount`.
An unknown storage surface uses `ReferenceMissing` with `SurfaceId` and the directory `ExpectedHash`.
Overlapping directory covers use `StorageDirectoryPagesOverlap` with that surface and the original
`surfaceStorage` array positions in `Index` and `OtherIndex`, preserved through sorting and grouping.
A patch key repeated across index pages uses `IdentityDuplicate` with `Patch` and the page `ExpectedHash`.
Storage names are admitted as lowercase SHA-256 before any file is addressed. Malformed names use
`DigestMalformed`, `FieldPath` `sha256` and the submitted `ActualHash`. Empty storage files or buffers
use `ValueOutsideRange`, `ActualByteCount` and inclusive `MinimumInteger` one. Larger-than-page input
uses `WorkCapacityExceeded`, `Budget` `MaxPageBytes`, the observed byte count and inclusive `Limit`.
Both read and write paths accept the exact upper limit. A byte mismatch uses `DigestMismatch` with
both hashes. A read with an additional byte after its captured length uses `StorageReadChanged`,
`ExpectedByteCount` and the minimum observed `ActualByteCount` (the captured length plus one), retaining
its expected digest. Overflow during surface storage preparation uses `GeometryNotRepresentable`,
`Operation` `SaveSurfaces` and `FieldPath` `Surfaces`.


**Every save entry point refuses a partial document** (`Save`, `SaveText`, `SaveTo`, `SaveAuto`,
`SaveAs`), because the data-loss path is a windowed document reaching a whole-document writer: that write
silently drops every unloaded tile and looks like a successful save. The guard is stated on the DOCUMENT
(`MapTileIndex.IsPartial`), not on one writer, so a save path added later inherits it. `SaveTiled` also
uses `DocumentUnloadedDestinationTile` when a bucketed item lands in a tile the index marks occupied but not
loaded. It carries the target `Tile`, the item's `SubjectKind` and known `EntityId`, or its `SculptTile` coordinates.
The refusal happens before tile writes. Widen the window before saving the moved content.

Tiled save admission uses `DocumentStorageFormConflict` for a file at the target `Path`, carrying actual
`DocumentForm` and `ExpectedDocumentForm`. `DocumentLoadedIndexRequired` refuses overwriting an existing
manifest without its tile index. A partial save outside its source uses `DocumentWindowTargetMismatch`
with target `Path` and source `SourceName`. A changed tile size uses `DocumentWindowTileSizeMismatch`,
`FieldPath`, `ActualNumber` and `ExpectedNumber`. Each refused save keeps existing file bytes unchanged.

Manifest admission uses `DocumentManifestMissing`, `Path` and `FileName`, or `DocumentManifestReadFailed`
with source context and the original read cause. Non-object tile entries use `SchemaObjectRequired` with
array `Index` and `FieldPath`. Invalid sculpt cell sizes distinguish `ValueNonfinite` from `ValueOutsideRange`
with the submitted `ActualNumber`. Asynchronous residency reads preserve a typed tile refusal's reason and
identifiers. A read failure without a typed cause uses `DocumentTileReadFailed` and the requested `Tile`.
A nonpositive residency apply budget uses `MapResidencyArgumentException`, `ValueOutsideRange`,
`ParamName = "config"`, `FieldPath = "MaxLoadsPerUpdate"`, `ActualInteger` and inclusive `MinimumInteger` one.

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
uses `DocumentHashSchemeUnsupported`, `ActualVersion` and `ExpectedVersion`, because a partial save
carries stored hashes through verbatim and cannot upgrade what it cannot read.

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

Negative load or decor radii and a degenerate unload band throw `MapResidencyArgumentException`, derived
from `ArgumentException`, with `Reason = ValueOutsideRange`, `ParamName = "config"`, the failed `FieldPath`,
`ActualInteger` and inclusive `MinimumInteger`. The unload minimum is `OuterRadius + 1`.
Duplicate tile index coordinates use `IdentityDuplicate`, `SubjectKind = "tile index entry"`, `Tile` and
known `SourceName`. A tile read failure uses `DocumentTileReadFailed`, `Tile`, `SourceName`, full `Path`
and pinned `ExpectedHash`.

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

Resolver, root, prefab, identity registry, runtime layer, surface scalar and cell admission, foliage codec, asset-manifest, asset-closure, light, support and native adapter admission refusals keep the exact `MapDocumentException` type. Inspect its nullable
`MapDocumentRefusal Reason` and applicable identifiers instead of classifying its message. The public
message-only constructors remain available to consumers and leave `Reason` null. Every engine construction
supplies a reason. Validation adapters forward their first typed finding and retain the ordered `Findings` snapshot.
`MapDocumentRefusal` numeric values are stable from version 23.5.0. New members go at the end and existing values
never move, so a mod or tool built against one engine version keeps working on a later one.

Surface scalar and cell checks preserve their original messages. `FieldPath` names the failed unit part,
frame enumeration, rectangle axis, cell coordinate or required surface identity. Denominator checks carry
`ActualInteger` when the computed common denominator fits in `long`, plus the checked inclusive limits.
A wider `Int128` denominator leaves `ActualInteger` null. Null height, cell and presence arrays use
`SchemaValueRequired`, while a wrong array length uses `SchemaArrayLengthMismatch` with `ActualCount`
and `ExpectedCount`. Corner access includes the final corner at `Width`, `Depth`, while cell access ends
at `Width - 1`, `Depth - 1`.

`ValidateLocalFindings` returns an owned read-only snapshot of every local shape, cell-field, padding,
capacity, dependency and subdivision finding in the same order as the unchanged `ValidateLocal` string
projection. Cell fields and metadata retain their array `Index` and field-specific `FieldPath`. Capacity
findings carry the collection name in `Budget`, its `ActualCount` and inclusive `Limit`. Dependency
corners and subdivision cells retain `CellX`, `CellZ` and `Patch`. A subdivision also carries `Edge`.
Missing items or owners use `SchemaValueRequired`, out-of-patch coordinates use `SurfaceAddressOutOfPatch`,
and unsupported edge values use `SchemaEnumUnsupported`. Repeated corner or cell-edge keys use
`IdentityDuplicate`. Invalid owner-address denominators and subdivision segment counts use
`ValueOutsideRange` with the actual value and inclusive limits. Segments admit both 2 and 64.
The encoder consumes these typed findings. Other forwarding adapters still expose prose.

`SplitSwNe` distinguishes an out-of-range rotation (`ValueOutsideRange`, 0 through 3) from an undefined
cut or topology (`SchemaEnumUnsupported`). It carries the failing `FieldPath`, `ActualInteger` and,
for an enumeration, `EnumType`, selecting the first failed branch in predicate order.
A forced diagonal contradicting `DiagonalHalf` rotation uses `SurfaceForcedTopologyConflict` with the
submitted topology. This scalar API has no patch or slot-cell context and does not invent it.

Surface codec record admission carries typed null-item, duplicate-id, text, enum, cardinality, version
and unsupported-kind refusals. `FieldPath` is local to the object checked by a helper, not a reconstructed
JSON path. Unsupported decoded kinds retain `ActualText`, while unsupported runtime records retain
`RecordType`. Chain vertices admit 2 through 4097, slot lists admit 1 through 4096, and slot values admit
0 through 4095 in strictly increasing order. `OrderInvalid` retains `Index`, `ActualInteger` and
`PreviousInteger`, while a value outside the slot domain uses `ValueOutsideRange`. Prism versions
carry `ActualVersion` and the owned `SupportedVersions` snapshot. Record-specific guards also retain
`RecordId` when available to that guard.

The encoder and decoder forward the first local finding's reason and applicable identifiers and attaches the full
ordered snapshot in `Findings`. Decoded patch-key mismatches carry `Patch` and `ExpectedPatch`, and
rectangle refusals carry the patch and dimensions. The trailing-value guard carries its requested patch.
Codec closed-schema guards use the shared object, member, array and required-value reasons.
Canonical decimal slots and addresses use `SchemaInt64Malformed` with `FieldPath`, `ActualText` and the
signed 64-bit limits. Base64 token-length mismatches retain character counts in `ActualCount` and
`ExpectedCount` plus `ExpectedByteCount`. Invalid encoding uses `SchemaBase64Malformed`, while a
successful decode with the wrong byte count uses `SchemaArrayLengthMismatch` and `ActualByteCount`.
Malformed JSON or scalar conversion uses `SchemaMalformed` with the requested `Patch` and reader
location when available. No helper reconstructs a JSON path or byte count from prose.

The 1,048,576-byte codec ceiling uses `WorkCapacityExceeded`, `Budget = "SurfacePatchEncodedBytes"`
and `Limit`. `ActualByteCount` is present only when the reader or writer knows the attempted byte count.
Early text and collection checks instead retain `ActualCount` and the local `FieldPath` when known.
An array beyond its own count cap uses that field name as `Budget`, with its `ActualCount` and `Limit`.

Missing water body lookups and removals use `ReferenceMissing`, the requested `EntityId` and `SubjectKind`
`water body`. Missing water media use `ReferenceMissing`, `EntityId` and `SubjectKind` `water medium`.
An unsupported edit reports `OperationKindUnsupported` with `Operation` and its runtime
`ActualText`. Containment distinguishes an absent cover (`ReferenceMissing`) from a present cover of
another type (`ReferenceKindMismatch`), retaining `RecordId`, `Patch`, `PatchStatus` when known and
`ExpectedRecordType`. The latter also carries `RecordType`.
`MapWaterContainmentReport.Findings` is an owned read-only snapshot of known typed refusals in report
order. It carries the original reason and identifiers through caught failures and adds the water body
as `OwnerId` when that identifier is unset. Surface-level arithmetic refusals retain body `EntityId`, `FieldPath` and `ActualNumber`.
Alias traversal retains `SpaceAliasCycle` with `SpaceId`, `RecordId` and `Patch`.
Water medium and body findings retain `EntityId` and `FieldPath`. Unsupported medium kinds also carry
`EnumType` and `ActualInteger`. A null body medium uses `SchemaValueRequired`, while an unresolved key uses
`ReferenceMissing`. Invalid ring vertices retain the single captured `Address` that failed validation and its
denominator identifiers. Reader-thrown refusals stay inside the same finding boundary and retain an
address only when one was captured or supplied by the reader. Wade speed and surface level distinguish `ValueNonfinite` from
`ValueOutsideRange`, retaining `ActualNumber` and the checked numeric limits. An exactly
unrepresentable finite surface level uses `GeometryNotRepresentable`.
Occupied-water admission carries `AcquireStatus` on both `GeometryNotRepresentable` and
`AcquisitionIncomplete`. Missing witness facts additionally retain their `Patch`, `PatchStatus` and
`SubjectKind`. A scope that does not prove the window complete uses `FieldPath = "Scope"`.
Compiler, containment and query statuses use typed findings and producer context. `Errors` retains the
ordered developer prose projection, including failures from consumer-built message-only exceptions.
Domain validation keeps those consumer failures in its string errors without inventing a finding, and domain
compilation preserves the original message-only exception as its inner cause.

The water edit adapter forwards its first document-level water finding and retains every finding in
`MapDocumentException.Findings`. Domain and cover fields use their actual failing path and stored
index. Frame failures reuse the concrete lattice reason with a `Domain.Frame` path prefix. A finite
corner beyond the world limit carries `ValueOutsideRange` with `PositionX`, `PositionZ` and the
numeric limits. Arithmetic overflow instead carries `GeometryNotRepresentable` and `Address`.
Nested geometry and work-budget findings retain their reason and measured identifiers. Material
closure reuses `ReferenceMissing` for absent resources and `ReferenceKindMismatch` for wrong kinds,
retaining body `EntityId`, `ResourceId`, `ExpectedResourceKind` and the available `ActualResourceKind`.
Checks keep the full body-id order on both identity paths and carry all findings without changing
their joined diagnostic text.

| Reason | Refused rule | Identifiers when known |
| --- | --- | --- |
| `PhysicsRegistrationOriginInvalid` | Physics registration at a nonintegral or out-of-world origin | `FieldPath` `Origin`, `PositionX`, `PositionY`, `PositionZ`, inclusive absolute coordinate `Limit` |
| `PhysicsLegacyMoveContextUnsupported` | A legacy movement context for a native contact world | `ResolverVersion` |
| `PhysicsWaterOwnerConflict` | A static owner colliding with a compiled water body owner | `OwnerId`, `BodyId` |
| `PhysicsWaterBoundsMissing` | Compiled water regions without body bounds | `BodyId` |
| `PhysicsWaterFragmentBoundsMissing` | Occupied water regions without fragment bounds | `BodyId` |
| `EditorScatterLayerReferenced` | Editor command removal of a referenced scatter layer, carried by `MapEditorCommandException` | `DocumentId`, `LayerId`, `EntityId`, `SubjectKind`, `FieldPath`, owned `SourceIds` |
| `EditorLegacySpawnEditProtected` | Native legacy spawn changes outside their admitted adoption transition | `DocumentId`, `ResolverVersion`, `Operation`, owned `SourceIds` in spawn then player-spawn order |
| `EditorMarkerAssetsFailed` | A failed host marker-asset callback | `DocumentId`, callback `FieldPath`, `Operation`, original `InnerException` |
| `EditorPrefabSessionCancelled` | Work on a cancelled detached prefab session | `AssetId`, `Operation` |
| `EditorRetainedReferenceRemoved` | A retained record referring to a removed child | `DocumentId`, `RecordId`, `TargetId`, target `ChildKind`, reference `FieldPath` |
| `ToolAssetRootRequired` | Detached prefab work or publication without a manifest root | `AssetId` when known, `Operation` |
| `WaterContainmentUnverified` | A publication report without a concrete finding that cannot prove water containment | Known findings take precedence |
| `ToolImmutableResourceConflict` | Different content at an immutable resource address | `Path`, `ResourceId`, `ExpectedHash`, `ActualHash` |
| `ToolResourceWriteFailed` | Resource publication failed through I/O or access admission | `Path`, `ResourceId`, `ResourceRoot`, `Operation`, original `InnerException` |
| `ToolManifestSerializationFailed` | Manifest serialization failed without a known lower refusal | `ResourceId`, `Operation`, original `InnerException` |
| `ToolStagedMutationRequired` | A native command without exactly one staged session mutation | `DocumentId`, `Operation`, `CommandType` |
| `ToolNativeDocumentRequired` | Native tool work through an analytic document | `DocumentId`, `Operation`, `FieldPath`, known `CommandType` |
| `ToolPublishedClosureMismatch` | A staged closure different from its supplied publication closure | supplied `ExpectedHash`, staged `ActualHash`, `DocumentId`, `Operation` |
| `ToolNestedMutationUnsupported` | A nested native session mutation | `DocumentId`, `Operation` |
| `ToolPublicationPathInvalid` | A rooted or escaping publication path | submitted `Path`, `StoragePath`, `DocumentForm`, known `ResourceRoot` |
| `ToolTiledSessionRequired` | A window move without tiled source storage, carried by `MapSessionException` | `DocumentId`, `Path`, `Operation`, `FieldPath`, `DocumentForm`, `ExpectedDocumentForm` |
| `ToolUnsavedChanges` | A window move discarding dirty content without permission, carried by `MapSessionException` | `DocumentId`, `Path`, `Operation`, `FieldPath` `IsDirty`, `DocumentForm` |
| `FoliagePreviewEditUnsupported` | A tool dry run for an edit other than setting a layer | `EditType` |
| `FoliagePreviewLayerNew` | A tool differential preview for a new layer | `LayerId` |
| `ToolMutationEmpty` | A tool terrain or scatter rule mutation with no supplied field, carried by `MapMutationArgumentException` | `Operation`, known `LayerId` and `Index`, original `ParamName` |
| `ToolFeatureTranslationUnsupported` | A terrain feature without a supported duplicate translation, carried by `MapMutationException` | `DocumentId`, `Index`, `FeatureType`, `Operation` |
| `ToolRectangleIncomplete` | A foliage or marker query, or sculpt clear rectangle supplied only in part | `FieldPath`, `Operation`, known `LayerId` |
| `FoliageQueryLayerRequired` | A foliage density or preview query without a layer | `FieldPath` `layerId` |
| `ToolExtractionParentAmbiguous` | A prefab extraction selection with multiple authored parents | owned read-only `SpaceIds`, `DocumentId` |
| `ToolPrefabSessionRequired` | Prefab editing or export without an open local session | `Operation` |
| `ToolLegacySpawnSurfaceRequired` | A native legacy spawn query without its support surface owner | `FieldPath`, ordered `SourceIds`, `DocumentId`, `Operation` |
| `ToolLegacySpawnSupportUnresolved` | A native legacy spawn query with unsupported points | `SurfaceId`, ordered `SourceIds`, ordered `Findings` retaining each source and `SupportStatus` |
| `ToolEffectiveResourcesMissing` | A native query without resolved effective resources | `Operation` |
| `RenderLookDirectionZero` | A zero view direction, carried by `MapRenderArgumentException` | submitted `Eye` and `Target` |
| `RenderFailed` | Capture failed without a known document finding, carried by `MapRenderException` | `Backend`, `Operation`, original `InnerException`, known lower document reasons retain their `Finding` |
| `ToolStorageFormChanged` | Expected native source storage disappeared or changed form | `Path`, actual `DocumentForm`, `ExpectedDocumentForm`, `Operation` |
| `ToolParentDirectoryMissing` | A staged save path without a parent directory | `Path`, `Operation` |
| `ToolDocumentRequired` | Session work without an open document, carried by `MapSessionException` | `Operation` |
| `ToolColliderMissing` | Measurement or editing of an asset without a collider | `AssetId` |
| `ToolColliderResourceShared` | A height edit to a resource used by another asset | `AssetId`, `OtherAssetId`, `ResourceId` |
| `ToolColliderShapeUnsupported` | Height edits to a collider other than boxes or compound boxes | `AssetId`, `ResourceId`, `ShapeType` |
| `ToolColliderBoxTilted` | Height edits to a tilted compound box | `AssetId`, `ResourceId`, child `Index` |
| `ToolColliderPublicationMismatch` | Published resources differ from the verified collider plan | `AssetId`, `ResourceId`, `ExpectedHash`, `ActualHash` |
| `WaterBodiesPreventLegacyMode` | Switching to legacy global water while bounded bodies remain | `FieldPath`, `ActualCount` |
| `DocumentHashSchemeUnsupported` | Whole authored identity or window loading under an unsupported pinned tile hash scheme | `FieldPath`, `ActualVersion`, owned `SupportedVersions` for identity or `ExpectedVersion` for window loading, source context |
| `StoragePatchKeyMismatch` | A surface dictionary entry whose key differs from its payload key | dictionary `ExpectedPatch`, payload `Patch` |
| `StorageSaveSnapshotChanged` | A prepared surface payload changed before its write | `Patch`, prepared `ExpectedHash`, encoded `ActualHash` |
| `StorageEmbeddingChanged` | An embedded payload whose encoded length changed after measurement | `ExpectedByteCount`, `ActualByteCount` |
| `StorageReadChanged` | Additional bytes after a surface storage read's captured length | `ExpectedHash`, `ExpectedByteCount`, minimum observed `ActualByteCount`, `FieldPath` `ByteCount` |
| `StorageDirectoryPagesOverlap` | Overlapping surface storage page covers | `FieldPath`, `SurfaceId`, `Index`, `OtherIndex` |
| `StorageReferencedPatchUnloaded` | A partial-save outgoing reference needing an unloaded patch | `Patch`, `PatchStatus` `Unloaded` |
| `StorageReverseKnowledgeUnknown` | A partial save without the reverse directory or index knowledge needed for its edit | changed `Patch`, searched `SurfaceId`, directory `ExpectedHash` |
| `StorageReverseDependantUnloaded` | A partial save with a nonresident reverse dependant | dependant `Patch`, changed `ExpectedPatch`, incident `RecordId` |
| `StoragePartialEditUnsupported` | A partial surface declaration, structure or record edit | `FieldPath` `Surfaces`, `Structure` or `Records`, changed `Patch` when known |
| `PinnedContentUnavailable` | Required pinned tile, directory, index or payload content whose read failed without a more specific document cause | `PatchStatus`, `Tile`, `Patch`, `SubjectKind`, `ExpectedHash` |
| `DocumentVersionUnsupported` | A root document format version other than the current writable version | `FieldPath`, `ActualVersion`, `SupportedVersions` |
| `DocumentTerrainFeatureUnknown` | A terrain feature discriminator absent from the supplied registry | discriminator `ActualText`, `EntityId`, `SubjectKind`, `FieldPath`, `Index` |
| `DocumentTileOwnershipMismatch` | An entity or sculpt tile whose owner differs from its containing document tile | document `Tile`, `EntityId`, `SubjectKind`, `FieldPath`, `Index`, positions or `SculptTile`, `SourceName`, `Path` |
| `WaterBoundedModeRequired` | A water edit while bounded water is off | `FieldPath` |
| `WaterMediumInUse` | Removing a medium still used by a body | using body `EntityId`, medium key `ActualText`, `FieldPath` |
| `WaterCoversAlias` | Covering a space alias instead of its owner | body `EntityId`, alias `RecordId`, owner `SpaceId`, `Patch` |
| `WaterNoSpaceFootprint` | A domain meeting no covered footprint with positive area | body `EntityId`, covered `SourceIds` |
| `WaterSpacesDisconnected` | Covered spaces without a connection strictly below the surface | body `EntityId`, covered `SourceIds` |
| `WaterNoCoveredSpace` | A body with an empty cover list | body `EntityId`, `FieldPath`, `ActualCount` |
| `WaterDomainPiecesOverlap` | Positive overlap between two pieces of one domain | body `EntityId`, `FieldPath`, `Index`, `OtherIndex` in rects-then-rings order |
| `WaterBodiesOverlap` | Positive overlap between bodies covering the same space | body `SourceIds`, `SpaceId` |
| `WaterDomainEmpty` | A domain with no rectangle or ring pieces | `FieldPath`, `ActualCount`, effective body `EntityId` |
| `WaterDomainZeroArea` | A domain piece whose vertices are collinear | `FieldPath`, `SubjectKind`, `Index`, effective body `EntityId` |
| `WaterDomainSelfIntersects` | A ring with intersecting edges or a fold back | `FieldPath`, `SubjectKind`, `Index`, effective body `EntityId` |
| `WaterDomainTriangulationFailed` | A piece whose triangulation cannot complete | `FieldPath`, `SubjectKind`, `Index` |
| `SchemaMalformed` | Invalid JSON, a deserialization failure without a known structured cause, malformed surface codec, embedded scalar/value or GLB encoding, or tile sculpt data | `RecordType`, `SubjectKind`, `ActualInteger`, `Patch`, `FieldPath`, `SourceName`, `Tile`, `Path`, `AssetId`, `ResourceId`, `LineNumber`, `BytePositionInLine` |
| `SchemaMemberDuplicate` | A repeated or case-variant JSON member | `FieldPath` of the containing object or the known duplicate field, `ResourceId`, `SourceName`, `Tile`, `Path` |
| `SchemaMemberMissing` | A missing required native, prefab or support member, or a missing or non-integer `formatVersion` | `FieldPath`, `ResourceId`, `SourceName`, `Tile`, `Path` |
| `SchemaMemberNull` | A native member that cannot be null | `FieldPath`, `SourceName`, `Tile`, `Path` |
| `SchemaMemberUnknown` | A member outside a closed schema object | `FieldPath`, `ResourceId`, `SourceName`, `Tile`, `Path`, `LayerId`, `Index` |
| `SchemaObjectRequired` | A document, manifest, tile, prefab payload, marker-options, surface codec or foliage value that must be an object | `FieldPath`, `SourceName`, `Tile`, `Path`, `LayerId`, `Index` |
| `SchemaValueRequired` | A missing or misplaced typed resource discriminant, null deserialization result, required native, indoor-span, manifest, prefab or foliage value, missing prism domain, missing marker edit values, blank marker-adoption identity text or blank marker consumer configuration, or a missing attachment anchor or standalone descriptor pin, a null codec record or required record text, a missing point-bound opening reference or surface, or missing acquired metadata, frame, payload, empty range or surface identity | `MarkerId`, `FieldPath`, `SourceName`, `Tile`, `Path`, `AssetId`, `ResourceId`, `ExpectedSchema`, `SurfaceId`, `PlacementId`, `LayerId`, `Index`, `EntityId`, `WorldId`, `ChildKind`, `Child`, `InstanceId`, `Patch`, `ActualAnchor`, `SubjectKind`, `RecordId`, `SpaceId` |
| `SchemaArrayRequired` | A foliage or surface codec member that must be an array | `FieldPath`, `Index` |
| `SchemaBooleanRequired` | A foliage member that must be a boolean | `FieldPath`, `LayerId`, `Index` |
| `SchemaBase64Malformed` | An invalid surface codec token or invalid or noncanonical foliage density base64 | `FieldPath`, `LayerId`, `Index`, `FoliageChunk`, `ExpectedByteCount` |
| `SchemaArrayLengthMismatch` | Decoded foliage density with the wrong byte length, embedded records in a prefab surface geometry patch, or a patch height, cell or presence array with the wrong length, or an embedding allocator returning another byte length | `SubjectKind`, `FieldPath`, `LayerId`, `Index`, `FoliageChunk`, `ActualByteCount`, `ExpectedByteCount`, `Child`, `SurfaceId`, `Patch`, `ActualCount`, `ExpectedCount` |
| `SchemaInt64Malformed` | Surface codec text outside canonical signed 64-bit decimal representation | `FieldPath`, `ActualText`, `MinimumInteger`, `MaximumInteger` |
| `SchemaPayloadVersionUnsupported` | An unsupported asset-reference, resolver-identity, support, foliage, prefab asset, local light, local marker, prism or storey payload version | `FieldPath`, `MarkerId`, `ResourceId`, `Index`, `ActualVersion`, `SupportedVersions`, `MinimumInteger`, `RecordId`, `SpaceId`, `EntityId`, `InstanceId` |
| `ValueNonfinite` | A nonfinite manifest source-unit scale, foliage scalar, playable-bound component, placement support-search value, resolved support height, adoption height or yaw, or marker position/yaw, or a nonfinite frame-local position, effective coordinate freeze or embedded scalar | `RecordType`, `SubjectKind`, `FieldPath`, `MarkerId`, `AssetId`, `LayerId`, `PlacementId`, `Index`, `ActualNumber` (null for nonfinite marker values, nonfinite for prefab scalar guards) |
| `ValueOutsideRange` | A finite nonpositive manifest source-unit scale, foliage integer outside its domain, negative acquired page count, support-search extent or high-water mark, nonpositive numeric identity, nonpositive lattice unit parts, an effective coordinate outside the installed metre range, an address denominator outside its domain, a rotation or subdivision segment count, chain or slot-list cardinality, or a slot cell outside 0 through 4095 | `FieldPath`, `AssetId`, `LayerId`, `PlacementId`, `EntityId`, `SubjectKind`, `Index`, `ActualNumber`, `ActualInteger`, `MinimumNumber`, `MinimumInteger`, `MaximumInteger`, `NumericId`, `HighWaterMark`, `RecordId`, `Patch`, `ActualCount` |
| `IdentityDuplicate` | A repeated advertised foliage chunk key, marker, adoption source or destination, asset or resource closure identity, support patch or record identity, surface, numeric, codec record, acquired patch or acquired record identity, corner or cell-edge metadata key, world binding, transfer or attachment producer, a standalone owner colliding with a prefab binding, or a repeated extraction reference or resulting local key, or a repeated topology reference, boundary side or adjacent slot cell | `MarkerId`, `MarkerIds`, `FieldPath`, `Index`, `EntityId`, `SubjectKind`, `SourceId`, `AssetId`, `ResourceId`, `SurfaceId`, `PlacementId`, `RecordId`, `Patch`, `NumericId`, `HighWaterMark`, `WorldId`, `ChildKind`, `Child`, `InstanceId`, `RelatedRecordId`, `EnumType`, `ActualInteger`, `PreviousInteger`, `LayerId`, `FoliageChunk` |
| `IdentityReserved` | A marker reusing a retained prefab reservation, a placement reusing a live or retained instance ID, a live standalone owner using a retired transfer's world identity, or a planned binding colliding with another owner's world or numeric identity | `MarkerId`, `EntityId`, `WorldId`, `ChildKind`, `Child`, `InstanceId`, `NumericId`, `SubjectKind` |
| `AuthoredOwnershipMismatch` | A marker carrying fields forbidden by its payload or height mode, a transfer inconsistent with its source or standalone owner, an attachment inconsistent with its owner, transfer or record anchor, a prefab placement carrying a numeric ID its instance binding owns, or a prefab-owned interior submitted as standalone, or a topology reference with the wrong anchor, an edge crossing surface owners, an authored chain carrying a source patch, or differing prism and storey instance ownership | `MarkerId`, `FieldPath`, `EntityId`, `WorldId`, `ChildKind`, `Child`, `InstanceId`, `PlacementId`, `RecordId`, `SpaceId`, `Patch`, `ActualAnchor`, `NumericId`, `ExpectedNumericId`, `ActualCount`, `ExpectedCount`, `SubjectKind`, `RelatedRecordId`, `SurfaceId`, `ActualText` |
| `ReferenceMissing` | A standalone marker removal target, asset, resource, prefab or dependency missing from its closure, or an undeclared effective surface or composed read surface owner or requested placement, attachment transfer, standalone record target, interior anchor, scatter layer or companion layer missing from its required scope, an absent effective extraction target, or an undeclared asset child removal, or an undeclared topology target, vertex surface, edge patch, chain source, footprint lattice or requested water body | `MarkerId`, `AssetId`, `ResourceId`, `DependencyId`, `ExpectedResourceKind`, `EntityId`, `WorldId`, `ChildKind`, `Child`, `InstanceId`, `PlacementId`, `RecordId`, `SpaceId`, `Patch`, `DocumentId`, `LayerId`, `SurfaceId`, `SubjectKind`, `FieldPath`, `Index`, `ReferenceScope`, `RelatedRecordId` |
| `AssetClosureRequired` | Foliage edit preflight or marker-backed editor work without a verified asset closure | `DocumentId`, callback `FieldPath` when known |
| `DigestMismatch` | Foliage pack density, acquired patch semantic pins, resource bytes, an attached child's descriptor digest, a stored surface semantic aggregate or a verified tile's lists differing from its pin | `DigestKind`, `SourceName`, `Tile`, `ResourceId`, `Path`, `EntityId`, `WorldId`, `ChildKind`, `Child`, `InstanceId`, `PlacementId`, `SurfaceId`, `SubjectKind`, `ExpectedHash`, `ActualHash`, `FoliageChunk`, `Index` |
| `BoundsInvalid` | Invalid prefab patch dimensions, manifest local bounds, playable bounds, reversed exact rectangles or invalid surface patch rectangles | `FieldPath`, `Width`, `Depth`, `AssetId`, `SurfaceId`, `Patch`, `CellX`, `CellZ`, `Index`, `MinimumNumber`, `MaximumNumber` |
| `AcquisitionContractMismatch` | An incoherent acquired payload, discovery fact, read key or page observation, session pin mismatch, or inconsistent incidence contract | `Patch`, `ExpectedPatch`, `PatchStatus`, `FieldPath`, `ExpectedSnapshot`, `ActualSnapshot`, `ExpectedHash`, `ActualHash`, `ActualCount`, `ExpectedCount`, `ActualInteger`, `PreviousInteger`, `MinimumInteger` |
| `AcquisitionSnapshotChanged` | Discovery or later session facts with changed snapshot, root or reservations, or a foliage context with mismatched effective resources | `DocumentId`, `FoliageStatus`, `Operation`, `FieldPath`, `ExpectedSnapshot`, `ActualSnapshot`, `ExpectedHash`, `ActualHash` |
| `AcquisitionUnsupported` | A producer without bounded scoped acquisition support | `SourceType` |
| `LegacyExteriorSpaceRequired` | Legacy exterior coverage on a non-Exterior space | `RecordId`, `SpaceId`, `SurfaceId`, `Patch`, `FieldPath` |
| `LegacyExteriorOpenTopRequired` | Legacy exterior coverage without an open-top upper bound | `RecordId`, `SpaceId`, `SurfaceId`, `Patch`, `FieldPath` |
| `SurfaceLegacyRecipeRequired` | Legacy coverage or effective sampling through an incompatible lower surface recipe | `RecordId`, `SpaceId`, `SurfaceId`, `Patch`, `FieldPath` |
| `SpacePrismParentMismatch` | A prism parent differing from its Room space's parent | `RecordId`, `SpaceId`, `RelatedRecordId`, `FieldPath` |
| `SpacePrismParentCycle` | A cycle in point or wide prism parent traversal | `RecordId`, `SpaceId`, `RelatedRecordId` |
| `SpacePrismParentFootprintMissing` | A prism's required parent without occupied geometry at the query point | `RecordId`, `SpaceId`, `RelatedRecordId` |
| `SpacePrismSeparationInvalid` | A prism's unordered or collapsed authored or effective vertical extent | `RecordId`, `SpaceId`, `FieldPath`, submitted `MinimumNumber` and `MaximumNumber` when finite |
| `SpacePrismOutsideParent` | A prism footprint outside its occupied parent footprint | `RecordId`, `SpaceId`, `RelatedRecordId` |
| `SpacePrismParentSeparationInvalid` | A prism without positive vertical separation inside its refined parent bounds | `RecordId`, `SpaceId`, `RelatedRecordId` |
| `SpaceOccupiedOverlap` | Positive occupied overlap between unrelated spaces | `SpaceId` and peer `RelatedRecordId` for prism pieces, or footprint `RecordId`, peer footprint `RelatedRecordId`, both `SlotCell` and `RelatedSlotCell`, and peer space `EntityId` for footprint cells |
| `SpaceSupportCoincident` | Coincident physical support from independent authored owners | footprint `RecordId`, peer footprint `RelatedRecordId`, `SpaceId`, `SlotCell`, `RelatedSlotCell`, or physics `SurfaceId` and `OtherSurfaceId`, or collider `PlacementId` and `SurfaceId` |
| `SpaceStoreyTransferMissing` | An unpacked prism without matching live record and storey transfers | `RecordId`, `InstanceId`, storey `EntityId`, `FieldPath` |
| `SpaceBoundaryGap` | An uncovered interval on an occupied column boundary | footprint `RecordId`, `SpaceId`, `SlotCell` |
| `SpaceBoundaryOverlap` | Overlapping authored intervals on an occupied column boundary | footprint `RecordId`, `SpaceId`, `SlotCell` |
| `SpaceCoverageMissingBound` | Lower or upper bounds not covering a footprint cell's whole area | footprint `RecordId`, `SpaceId`, `SlotCell` |
| `SpaceCoverageSeparationInvalid` | Nonpositive separation between a footprint cell's refined bounds | footprint `RecordId`, `SpaceId`, `SlotCell` |
| `SpacePortalAirIntervalInvalid` | A portal band outside an endpoint space's occupied air interval | portal `RecordId`, endpoint `SpaceId`, segment `Index` |
| `SpacePortalHeightInvalid` | A portal column with a nonpositive clear height | `RecordId`, `Index`, `ActualNumber` |
| `SpaceChainUnresolved` | An unresolved chain without a specific producer finding | `RecordId`, `Patch` when known |
| `PhysicsWallChainUnresolved` | An unresolved terrain physics wall chain without a specific producer finding | chain `RecordId` and anchor `Patch` |
| `SpaceRoomOccupancyInvalid` | A Room with zero or multiple prism domains, an interior add targeting an occupied prism space, or occupancy through a lattice footprint | `SpaceId`, `RecordId`, `DocumentId`, `FieldPath`, `ActualCount`, `ExpectedCount` |
| `SpaceParentCycle` | A cycle in space ancestry | `SpaceId` |
| `SpaceAliasCycle` | A cycle in space alias traversal | `SpaceId` |
| `GeometryVertexSequenceInvalid` | A portal interval with fewer than two vertices, seam pairs not covering both complete edge vertex sets, or strip vertices without ordered XZ correspondence | `RecordId`, `Index`, `FieldPath`, `SurfaceId`, `ActualCount`, `ExpectedCount`, `MinimumInteger` when known |
| `SpaceChainHeightKindMismatch` | A surface-edge vertex with an authored height, or an authored vertex without one | `RecordId`, `Index`, `FieldPath`, `ActualInteger` when present |
| `SpacePortalOpenTopInvalid` | An open-top portal with a non-Exterior endpoint | `RecordId`, `SpaceId`, `FieldPath`, `EnumType`, `ActualInteger` |
| `SpacePortalMembershipMismatch` | An endpoint space not listing its portal exactly once on the required side, or a space listing another endpoint's side | `RecordId`, `SpaceId`, `RelatedRecordId`, actual endpoint `EntityId`, `FieldPath`, `ActualCount`, `ExpectedCount` |
| `SpaceLinkMembershipMismatch` | An endpoint space not listing its vertical link exactly once, or a link listed by an unrelated space | `RecordId`, `SpaceId`, `RelatedRecordId`, `FieldPath`, `ActualCount`, `ExpectedCount` |
| `SpaceAliasPeerInvalid` | An alias targeting another alias or a space with a different parent or kind | `RecordId`, `SpaceId`, `RelatedRecordId`, first failed `FieldPath` |
| `SpaceOpenTopInvalid` | An open-top lower bound, an open top on a non-Exterior space, or an open top carrying a surface or opening | `RecordId`, `SpaceId`, `SurfaceId`, `RelatedRecordId`, first failed `FieldPath` |
| `SpaceBoundRoleMismatch` | A bound kind or surface role incompatible with the lower or upper bound role | `RecordId`, `SpaceId`, `SurfaceId`, `FieldPath`, `EnumType`, `ActualInteger` |
| `SpaceOpeningCellsInvalid` | A horizontal opening over a present cell | `RecordId`, `Patch`, `FieldPath`, `Index`, `ActualInteger`, `CellX`, `CellZ` |
| `SpaceOpeningOwnershipInvalid` | A prepared opening demand for a cell outside that opening's authored slot set | `RecordId`, `Patch`, `SlotCell` |
| `ContextDemandUnprepared` | A bound context read outside its prepared demands, carried by `MapBoundContextException` deriving from `InvalidOperationException` | `Operation`, `Patch`, opening `RecordId`, `SlotCell`, exact requested `Face` when applicable |
| `GeometryNotRepresentable` | A collapsed effective cell, exact triangle, unrepresentable surface emission, water-domain corner, boundary arithmetic, support-seat overflow or backend placement pose | `SurfaceId`, `Patch`, `CellX`, `CellZ`, `SlotCell`, `RecordId`, `SpaceId`, `PlacementId`, `MarkerId`, `SupportStatus`, `ActualNumber`, `Operation`, `FieldPath`, `Index`, `OtherIndex`, `SubjectKind` when known |
| `GeometryVertexHeightMismatch` | Different exact heights for a shared compiled, opening, boundary or seam vertex | `SurfaceId`, `Patch`, `RecordId`, `Address`, `Index`, `FieldPath` when known |
| `FrameRadiusExceeded` | Local frame radius, including effective resource conversion, or checked anchor subtraction overflow | `Patch` when available, `Limit`, local `PositionX`/`PositionZ` only after successful subtraction |
| `PatchUnavailable` | A requested effective, seam or corner-owner patch, record, point-bound source or membership demand, or occupied bound is unavailable | `RecordId`, `SpaceId`, boundary `OwnerId`, `FieldPath`, `SurfaceId`, `Patch`, `ExpectedPatch`, `Address`, `Index`, `PatchStatus`, `SlotCell`, `ExpectedRecordType` when known |
| `SurfaceLegacySampleOutOfCell` | A legacy sample outside its selected cell or patch | `SurfaceId`, `Patch`, `SlotCell` |
| `SurfacePaintPhysicalCompileUnsupported` | Physical-face compilation of a paint-only surface | `SurfaceId`, `Patch` |
| `SurfaceNativeNoDrawUnsupported` | A native cell carrying the legacy NoDraw flag | `SurfaceId`, `Patch`, `SlotCell`, `FieldPath` |
| `SurfaceSeamPositionMismatch` | Paired seam or corner vertices at different exact world XZ positions | `RecordId`, `SurfaceId`, `Patch`, `ExpectedPatch`, `Address`, `Index`, `CellX`, `CellZ`, `FieldPath` when known |
| `SurfaceSeamVertexUndeclared` | A paired vertex or corner owner absent from its declared source geometry | `RecordId`, `SurfaceId`, `Patch`, `ExpectedPatch`, `Address`, `Index`, `FieldPath` when known |
| `SurfaceSeamEdgeInvalid` | A collapsed or diagonal seam source edge | `RecordId`, `Patch`, `SurfaceId`, `FieldPath` |
| `SurfaceSupportRoleInvalid` | A support resource surface outside the native support-floor policy | `ResourceId`, `SurfaceId`, `Index`, `FieldPath`, `EnumType`, `ActualInteger` |
| `SurfaceWallHeightInverted` | A wall strip lower vertex above its matching upper vertex | `RecordId`, `Index` |
| `AcquisitionIncomplete` | An operation whose captured geometry or residency does not prove its required view | `SurfaceId`, `AcquireStatus`, `FieldPath`, `Patch`, `PatchStatus`, `SubjectKind` |
| `SurfacePaintTargetInvalid` | A paint-only physical target or a whole-face override with incompatible role or declared target | `SurfaceId`, `OtherSurfaceId`, `FieldPath`, `EnumType`, `ActualInteger`, `ActualText` when known |
| `SurfacePaintPriorityMisplaced` | Nonzero priority on a non-paint surface | `SurfaceId`, `FieldPath`, `ActualInteger` |
| `SurfacePaintPriorityConflict` | Equal-priority overlapping paint with different materials | `SurfaceId`, `OtherSurfaceId`, priority in `ActualInteger` |
| `SurfacePaintBoundaryInvalid` | An open or nonmanifold material boundary | `SurfaceId` |
| `SurfacePaintCrossesPhysicalFace` | A whole-face paint material or presence boundary crossing a physical face | `SurfaceId`, `OtherSurfaceId`, `Patch`, face `Index`, `FieldPath` |
| `WorkCapacityExceeded` | Prefab and light payload bytes, codec array counts or patch geometry, foliage codec counts or density token sizes beyond their limits, reservation keys beyond the candidate cap, surface codec work beyond its encoded byte or array-count budget, or local corner-dependency or edge-subdivision metadata beyond its count budget, or compiled faces beyond MaxFacesPerPatch or support resource bytes beyond MaxPayloadBytes, or water-domain piece, vertex or pair-check work beyond its budget, or prefab expansion, foliage context preflight and embedding byte/depth budgets | `Operation`, `SubjectKind`, `Patch`, `FieldPath`, `Budget`, `ActualCount`, `ActualByteCount`, `Limit`, `LayerId`, `Index`, `FoliageChunk` |
| `SurfacePresencePaddingNonzero` | Nonzero bits beyond the authored cell count in the last presence word | `Patch`, `FieldPath`, `Index` |
| `SurfaceSubdivisionAbsentCell` | An edge subdivision on an absent cell | `Patch`, `FieldPath`, `Index`, `CellX`, `CellZ`, `Edge` |
| `SurfaceSubdivisionInteriorEdge` | An edge subdivision adjoining another present cell inside the patch | `Patch`, `FieldPath`, `Index`, `CellX`, `CellZ`, `Edge` |
| `SurfaceForcedTopologyConflict` | A forced topology contradicting the diagonal-half rotation | `FieldPath`, `ActualInteger`, `EnumType` |
| `SurfacePatchTrailingBytes` | A patch payload with values remaining after its root | Requested `Patch` |
| `SurfacePatchKeyMismatch` | A decoded patch key differing from the requested key | `Patch`, `ExpectedPatch` |
| `OrderInvalid` | Foliage pack keys, reservation keys or slot cells outside unique canonical order, or a backwards step in a topology slot-cell list | `FieldPath`, `ActualCount`, `Index`, `ActualInteger`, `PreviousInteger`, `RecordId`, `Patch`, `FoliageChunk` |
| `OperationKindUnsupported` | An unsupported decoded or runtime topology record kind | `RecordId`, `FieldPath`, `ActualText` or `RecordType` |
| `SurfaceUnitUnreduced` | An authored lattice unit whose positive numerator and denominator have a common factor | `Numerator`, `Denominator` |
| `SurfaceAddressOutOfPatch` | A patch-local cell or corner coordinate outside the valid rectangle | `Patch`, `Width`, `Depth`, `CellX`, `CellZ`, `FieldPath`, `ActualInteger`, `MinimumInteger`, `MaximumInteger` |
| `DigestMalformed` | A foliage pack, asset-reference, materialized source, prefab or surface semantic digest outside lowercase SHA-256 representation | `FieldPath`, `ResourceId`, `SurfaceId`, `AssetId`, `PlacementId`, `ActualHash`, `Index` |
| `AssetKindUnsupported` | An undefined resource kind | `ResourceId`, `FieldPath`, `ActualResourceKind` |
| `AssetDependencyCycle` | A cycle in the resource dependency graph | `ResourceIds` of all resources left unresolved |
| `AssetPayloadNotCaptured` | A borrowed payload absent from the verified closure | `ResourceId`, `ExpectedResourceKind` when the API requires a kind |
| `AssetPayloadUnreadable` | Resource bytes that cannot be read or decoded | `AssetId`, `ResourceId`, `ExpectedHash`, `Path`, original `InnerException` when known |
| `AssetDescriptorMismatch` | A native mesh descriptor that is not the supplied closure's own | `AssetId` |
| `AssetGlbExternalUri` | An external buffer or image URI in a native mesh | `AssetId`, `ResourceId`, `FieldPath`, `Uri` |
| `AssetCollisionTrailingBytes` | Bytes after the decoded collision shape | `AssetId`, `ResourceId`, `ActualByteCount` of trailing bytes |
| `AssetCollisionEmptyPoints` | A collision hull or mesh with no points | `AssetId`, `ResourceId`, `ShapeType` |
| `AssetCollisionNestedMesh` | A triangle mesh nested in a compound collider | `AssetId`, `ResourceId` |
| `SchemaEnumUnsupported` | An undefined foliage, prefab, marker height-mode, support-binding, lattice-frame, cell, subdivision-edge, cell topology, topology-record or acquired metadata/status enumeration value | `MarkerId`, `EnumType`, `ActualInteger`, `FieldPath`, `EntityId`, `ChildKind`, `Patch`, `Index`, `CellX`, `CellZ`, `Edge`, `PatchStatus`, `LayerId`, `ActualText` |
| `AuthoredReferenceKindInvalid` | A light owner, roof reference, placement override or numeric binding of the wrong child kind, or a vertical link geometry owner of a forbidden topology type | `EntityId`, `Child`, `ChildKind`, `ExpectedChildKind`, `WorldId`, `NumericId`, `ReferenceScope`, `ExpectedReferenceScope`, `FieldPath`, `SubjectKind`, `RecordId`, `RelatedRecordId`, `RecordType`, `Patch` |
| `MarkerHeightChoiceInvalid` | A height choice without exactly one authority, or a Support marker whose finite Y is not canonical +0 | `MarkerId`, `FieldPath`, finite `ActualNumber` |
| `MarkerSupportUnresolved` | A marker seat query that is not Supported | `MarkerId`, typed `SupportStatus` |
| `SupportUnresolved` | A placement seat query that is not Supported | `PlacementId`, typed `SupportStatus` |
| `SupportProviderHeightMissing` | Support supplied by another missing-Y placement | `PlacementId`, `ProviderPlacementId` |
| `SupportBindingMissing` | A missing authored placement binding | `PlacementId` |
| `SupportSpaceSearchMissing` | A space binding without reference Y or both search bounds | `PlacementId`, `SpaceId` |
| `SupportSpaceMissing` | An undeclared support space at the selection lookup | `SpaceId`, projected to MissingGeometry by the query |
| `SupportAdoptionChoiceMissing` | Adoption without exactly one height or binding choice | `PlacementId`, a nonfinite explicit height instead uses `ValueNonfinite` and `FieldPath` |
| `MarkerNativeDocumentRequired` | Marker editing or adoption without a native document | `Operation` |
| `MarkerPrefabOwnershipProtected` | Direct editing of a live prefab-owned marker | `MarkerId`, `InstanceId` |
| `MarkerAdoptionChoiceSetMismatch` | Adoption choices not covering exactly the source spawns | `SourceIds`, `ActualCount`, `ExpectedCount` |
| `MarkerAdoptionChoiceMissing` | A source spawn without a destination and height choice | `SourceId`, `SubjectKind` |
| `MarkerNameDuplicate` | A repeated effective or adoption destination marker name | `MarkerName`, destination `MarkerId` or colliding `MarkerIds`, ordered `Findings` for metadata collisions |
| `PrefabCapturedUpdateRequired` | An asset update submitted without the two captured closures | `Operation = "PrepareAssetUpdate"`, submitted `RecordType` |
| `PrefabInstanceMissing` | An edit of an absent or removed instance | `InstanceId` |
| `PrefabChildAlreadyRemoved` | A remove override submitted for an already removed child | `InstanceId`, `Child` |
| `PrefabOverrideDuplicate` | Initial overrides repeating one typed child | `InstanceId`, `Child`, `Index`, `FieldPath` |
| `PrefabExtractionReferenceInvalid` | An extraction reference outside the World scope | `FieldPath`, `Index`, `EntityId`, `ChildKind`, `ReferenceScope` |
| `PrefabExtractionEmpty` | An extraction without selected children | `Operation` |
| `PrefabExtractionAttachmentIncomplete` | A materialized owner selected without every live associated child | `PlacementId`, local `Child`, `WorldId` |
| `PrefabExtractionNotLossless` | A partial room, floor or roof selection | `EntityId` and `ChildKind` of the missing World reference |
| `PrefabAssetPinMismatch` | A live instance or requested asset update digest differing from its captured pin | `InstanceId` when known, `ResourceId`, `ExpectedHash`, `ActualHash`, expected and actual resource kinds and `FieldPath` when checked by the update request |
| `PrefabAddedChildExists` | An add override repeating a declared child | `InstanceId`, `Child` |
| `AuthoredValueMismatch` | An acquired or resident patch key differing from its requested or dictionary key, an acquisition surface lookup returning a different identity, an edit parent not naming an authored space at its declared anchor, a previous, replacement or add key differing from its required child, or an effective reference outside the World namespace, or a chain vertex on a different source surface or a bound carrying a conflicting opening or surface field | `InstanceId`, `SpaceId`, `Patch`, `Child`, `ExpectedChild`, `EntityId`, `ReferenceScope`, `ExpectedReferenceScope`, `ChildKind`, `ExpectedChildKind`, `ExpectedPatch`, `PatchStatus`, `SurfaceId`, `ActualText`, `FieldPath`, `SubjectKind`, `RecordId`, `RelatedRecordId` |
| `PrefabPreviousOverrideNestedRemove` | A remove override whose Previous is another remove | `Child`, `FieldPath` |
| `PrefabPayloadIdMismatch` | A resource ID or child key differing from its payload identity or kind | `ResourceId` for a resource mismatch, `Child` for the submitted key, `EntityId` for the payload identity, `ChildKind` and `SubjectKind` for the payload kind, `FieldPath` |
| `PrefabPlacementTransformDuplicated` | A placement child with an additional local transform | `Child`, `FieldPath` |
| `PrefabLocalAnchorInvalid` | A record anchor whose surface slot is not Local | `Child`, `RecordId`, `FieldPath` |
| `PrefabLightOwnerInvalid` | A leaf light owner other than null or the Local placement token `$owner` | `ResourceId`, `FieldPath` |
| `AuthoredValueRequired` | A required removed-child override or initial override value missing, or a parent-dependent instance or local `$parent` remap without its supplied parent space | `InstanceId`, `Child`, `Index`, `FieldPath`, `ReferencePath`, `EntityId`, `ChildKind`, `RecordId`, `Patch` |
| `PrefabNumericIdSpaceExhausted` | Binding allocation beyond the last signed 64-bit identity | `InstanceId`, `HighWaterMark` |
| `PrefabReferenceScopeMissing` | A reference occurrence with neither an explicit nor an inherited scope | `ReferencePath`, `EntityId`, `ChildKind`, `RecordId`, `Patch` |
| `PrefabEffectiveLightOwnerMissing` | An effective light whose world placement owner is absent | `PlacementId` for the missing owner, `WorldId` and `ChildKind` for the light |
| `PrefabMaterializedFrameMismatch` | Two materialized rows for one placement with different mathematical frames | `AssetId`, `PlacementId`, `FieldPath`, `ActualTransform`, `ExpectedTransform` |
| `PrefabMaterializedAnchorMismatch` | A materialized record anchored outside its row's captured surfaces | `AssetId`, `PlacementId`, `EntityId`, `Child`, `ChildKind`, `FieldPath`, `Patch` |
| `PrefabPaintTargetRequired` | An effective paint override without a target surface | `SurfaceId` |
| `PrefabWorldCoordinateLimitExceeded` | A static world position outside the inclusive axis limit of 1,000,000 metres | `OwnerId`, binary64 `PositionX`, `PositionY`, `PositionZ`, `Limit` in metres |
| `AssetStoragePathReserved` | A resource path owned by the selected map storage writer | `ResourceId`, normalized `Path`, `StoragePath`, `DocumentForm` |
| `AssetTypedPayloadOpaque` | An opaque resource supplied to a typed light or support consumer | `ResourceId`, `ExpectedSchema`, `SubjectKind` |
| `ReferenceKindMismatch` | A referenced resource or topology record of a different kind than required | `EntityId`, `AssetId`, `ResourceId`, `ExpectedResourceKind`, `ActualResourceKind`, `RecordId`, `RelatedRecordId`, `Patch`, `PatchStatus`, `SurfaceId`, `RecordType`, `ExpectedRecordType`, `SpaceId`, `FieldPath`, `EnumType`, `ActualInteger` |
| `NumericIdHighWaterExceeded` | A placement, prefab binding or retired transfer above the reserved mark | `EntityId`, `SubjectKind`, `PlacementId`, `FieldPath`, `NumericId`, `HighWaterMark` |
| `ResolverContractMismatch` | The document resolver, support recipe or caller's resolver options cannot serve the requested operation | `PayloadVersion`, `ResolverVersion`, `OptionsResolverVersion`, `ExpectedResolverVersion`, `SupportRecipe`, `ExpectedSupportRecipe`, `FieldPath`, `SubjectKind` |
| `SupportLegacyHeightRequired` | Resolver-1 resolution without an analytic support callback | `ResolverVersion`, `FieldPath` `legacySupportHeight` |
| `CompleteDocumentRequired` | Complete prefab expansion, authored-support adoption, whole authored identity or native validation over a partial document | `DocumentId`, `Operation` |
| `DocumentLoadedIndexRequired` | Overwriting an existing tiled document without its tile index | target `Path` |
| `DocumentManifestMissing` | A tiled directory without its required manifest | `SourceName`, `Path`, `FileName` |
| `DocumentManifestReadFailed` | A manifest that exists but cannot be read | `SourceName`, `Path`, original cause |
| `DocumentStaleWindow` | A partial window whose manifest changed or became unreadable | `Path`, pinned `ExpectedHash`, `ActualHash` when readable |
| `DocumentStorageFormConflict` | A file at a requested tiled-save path or an occupied tiled conversion target | `Path`, `DocumentForm`, `ExpectedDocumentForm` |
| `DocumentUnloadedDestinationTile` | Content moved into an indexed tile that the window did not load | `Tile`, `EntityId`, `SubjectKind`, `SculptTile` when known |
| `DocumentWindowTargetMismatch` | A partial save outside its source directory | target `Path`, source `SourceName` |
| `DocumentWindowTileSizeMismatch` | A partial window whose tile size changed | `Path`, `FieldPath`, `ActualNumber`, `ExpectedNumber` |
| `DocumentSaveLockUnavailable` | A tiled save while another writer holds the directory lock | normalized directory `Path`, original cause |
| `PhysicsSweepShapeUnsupported` | An interaction envelope cannot sweep its source shape | `PlacementId`, `ShapeType` |
| `PhysicsInteractionSourceMissing` | A placement without a selection volume or collider | `PlacementId`, `AssetId` |
| `PhysicsShapeUnsupported` | A shape unsupported by bounds calculation | `ShapeType` |
| `PhysicsGridAlignmentInvalid` | A fractional storage size, world size mismatch, misaligned origin or world grid index coverage outside signed 32-bit range | `FieldPath`, `StorageTileSize`, checked `ActualNumber` and `ExpectedNumber`, or `GridKind`, `Axis`, `AxisOrigin`, `StorageTiles`, `FirstIndex`, `LastIndex`, source bounds and signed index limits |
| `PhysicsGridIndexOutOfRange` | A storage tile whose navigation or server block index cannot be represented | `GridKind`, `Axis`, `TileIndex`, `StorageTiles`, `StorageTileSize`, `AxisOrigin`, known `ActualInteger`, signed limits, failed `FieldPath` and numeric bounds |
| `PhysicsNavIndexOutOfRange` | A computed navigation tile index outside signed 32-bit range | `Axis`, `FirstIndex`, `LastIndex`, `AxisOrigin`, inclusive `MinimumInteger` and `MaximumInteger`, source interval `MinimumNumber` and `MaximumNumber` when known |
| `NavigationTilesNotAdjacent` | Navigation tile bounds that do not meet at one edge | `Tile` and `OtherTile` on `MapNavigationArgumentException`, `FieldPath` |
| `DocumentTileReadFailed` | A tile file that cannot be read | `Tile`, `SourceName`, `Path`, pinned `ExpectedHash`, original cause |
| `DocumentTileNotIndexed` | A requested tile absent from the occupied index | `Tile`, `SourceName` |
| `DocumentReadFailed` | An unreadable document source | `SourceName`, `Path`, original cause |
| `DocumentMigrationMissing` | An older document without its next migration | `SourceName`, `FieldPath`, source `ActualVersion`, next `TargetVersion` |
| `DocumentMigrationReturnedNull` | A registered migration returning no root object | `SourceName`, `FieldPath`, source `ActualVersion` |
| `DocumentSaveSourceMissing` | A save-back path with no existing form | `Path`, `DocumentForm` `None` |
| `DocumentSaveFormInvalid` | A requested form that cannot be written | `Path`, submitted `DocumentForm` |
| `DocumentAssetRootsMismatch` | Native document roots differing from the verified asset closure | submitted `ResourceIds` and `ExpectedResourceIds`, both owned read-only snapshots in their producer order |
| `DocumentMigrationSourceVersionInvalid` | A native, surface, prefab or marker migration source outside its supported source versions | `FieldPath`, `ActualVersion`, owned `SupportedVersions` |
| `PrefabStateUnreadable` | A producer failure while reading prefab state without a concrete lower refusal | `DocumentId`, original `InnerException` on the throwing adapter |


Reference-slot admission carries the owning local `Child` or known support `ResourceId` separately from
the referenced `EntityId`, `ChildKind`, `RecordId` and `Patch`. `ReferencePath` is the canonical JSON
pointer emitted by the typed walker. `FieldPath` identifies the `$.references` collection. Null slots
use `SchemaValueRequired`, duplicate paths `IdentityDuplicate`, and a slot-count mismatch
`SchemaArrayLengthMismatch` with `ActualCount` and `ExpectedCount`. A missing slot or local target uses
`ReferenceMissing`, a wrong child kind `AuthoredReferenceKindInvalid`, and an inconsistent target ID
`AuthoredValueMismatch`. Undefined scopes use `SchemaEnumUnsupported`. Ownership, scope-pair, local
anchor and pose disagreements use `AuthoredOwnershipMismatch`, with `ActualAnchor` when checked.
Remapped support patches retain their typed `Patch` and `SurfaceId` on an `IdentityDuplicate` refusal.
Binding exhaustion publishes no reservation, and allocating the final signed 64-bit ID is accepted.

Direct prefab asset and light validation also carries structured scalar and child refusals. Local light
intensity and colour must be nonnegative. Radius and uniform pose scale must be strictly positive.
`ValueOutsideRange` carries the refused `ActualNumber` and `MinimumNumber` 0, exclusive for radius and
scale. `ValueNonfinite` carries the first nonfinite `ActualNumber`, including NaN or infinity. Compound
guards select their first failing branch in predicate order. `FieldPath` is relative to the checked
asset, child, light or patch, when the caller supplied that location. Missing text uses
`SchemaValueRequired`, and unsupported asset and light versions carry `ActualVersion` and a read-only
`SupportedVersions` snapshot containing 1.

A prefab surface patch with a different surface owner uses `AuthoredOwnershipMismatch`, with `Child`,
`SurfaceId` and `Patch`. Embedded records instead use `SchemaArrayLengthMismatch`, with `ActualCount`
and `ExpectedCount` 0. Placement numeric identity and prism instance ownership also use
`AuthoredOwnershipMismatch`, with the local `Child` and the submitted `NumericId` or `InstanceId`.
An implicit child collision uses `IdentityDuplicate`, with `Child`, `EntityId`, `ChildKind` and
`SubjectKind` `implicit child`. Local annotation and roof targets use `ReferenceMissing`, with
`EntityId` and `ChildKind`. Leaf water
material kind uses `ReferenceKindMismatch`, with `ResourceId`, both resource kinds and `Child`.
An undeclared leaf dependency uses `ReferenceMissing`, `ResourceId` and `SubjectKind` `prefab dependency`.
Prefab and marker entry points use `OperationKindUnsupported` for a consumer-defined edit subtype, retaining
`RecordType` and `Operation = "Prepare"`. An asset update through `Prepare` instead uses
`PrefabCapturedUpdateRequired`, naming the required `PrepareAssetUpdate` operation. Room extraction requires exactly
one prism and retains the `SpaceId`, `ActualCount` and `ExpectedCount` 1 on `SpaceRoomOccupancyInvalid`.
Prefab and light byte admission uses `WorkCapacityExceeded`, `Budget = "PayloadBytes"`, measured
`ActualByteCount`, inclusive `Limit`, `FieldPath = "ByteLength"` and `ResourceId` when the caller knows it.
Codec collection limits retain the actual count and JSON path. `Budget` is the collection name for an individual
array and `Aggregate.<name>` for its accumulated count. Patch-cell and corner work uses
`Budget = "GeometryElements"` with its accumulated count. These guards accept their exact named limits.
Cross-array reference, patch, record and geometry totals name `Aggregate.ReferenceEdges`, `Aggregate.Patches`,
`Aggregate.Records` or `Aggregate.GeometryElements` in predicate order, retaining the crossing array's `FieldPath`.
Unsupported child and override codec discriminators use `OperationKindUnsupported` with `Child`, `ActualText` and
`FieldPath = "$.type"`. Unsupported runtime subtypes retain `Child` and `RecordType` instead.

The closed marker support-binding codec throws `MapPrefabJsonException`, derived from `JsonException`, with the
same `MapDocumentRefusal` members. Document and resource wrappers preserve its concrete reason, identifiers and
reader coordinates. Its writer reports the actual binding-shape finding and retains known `SurfaceId` and `SpaceId`.
It keeps the released shape admission, separate from the stricter semantic support-binding validator.

| Reason | Refused binding JSON rule | Identifiers when known |
| --- | --- | --- |
| `SchemaObjectRequired` | A binding value that is not an object | `FieldPath` |
| `SchemaMemberMissing` | Missing kind or a required member of that kind | `FieldPath` |
| `SchemaValueRequired` | Wrong kind or identity value type, or a missing writer member | `FieldPath`, `SurfaceId`, `SpaceId` |
| `SchemaMemberUnknown` | A member outside the selected kind | `FieldPath` |
| `SchemaMemberDuplicate` | A repeated member | `FieldPath` |
| `SchemaMemberNull` | A member explicitly set to null | `FieldPath` |
| `SchemaEnumUnsupported` | An unsupported binding kind | `FieldPath`, `EnumType`, submitted `ActualText` or writer `ActualInteger` |
| `AuthoredOwnershipMismatch` | A writer member belonging to the other binding kind | `FieldPath`, `SurfaceId`, `SpaceId` |
| `ValueNonfinite` | A binding member whose number is not representable as a finite float | `FieldPath`, parsed `ActualNumber` when available |

`MapPrefabValidation.ValidateFindings` returns the ordered immutable findings behind the unchanged string API.
Its throwing adapter takes the first concrete finding and preserves the full ordered snapshot and original cause.
The full snapshot also survives a state-read exception wrapping an aggregate document refusal.
Known JSON and document refusals retain their reason and identifiers. An unclassified producer failure uses
`PrefabStateUnreadable` with the document ID and original cause, without inventing a property path.
Missing root or asset child collections use `SchemaValueRequired`. Over-cap collections use `WorkCapacityExceeded`
with `Budget`, captured `ActualCount`, inclusive `Limit`, `FieldPath` and known `DocumentId` or `AssetId`.
Unsupported runtime children and topology records use `OperationKindUnsupported` with their type and local identity.
Undefined standalone descriptor kinds reuse `SchemaEnumUnsupported` with their type, value and subject identity.
`AttachmentKind` distinguishes the typed attachment namespace on a child-kind refusal. The shared source-context
copy preserves it too. Prefab migration distinguishes absent and incorrectly typed versions with
`SchemaMemberMissing` and `SchemaValueRequired`, retaining its accepted-version snapshot.
Exact-enum readers and writers use `MapPrefabJsonException` with `SchemaEnumUnsupported`, `EnumType` and the submitted
text or numeric value. Binding numeric readers distinguish a wrong JSON type from `ValueNonfinite`.

Asset-local materialization checks carry the owning `AssetId`, local `PlacementId` and known `FieldPath`.
An unadvertised resource edge uses `AuthoredOwnershipMismatch` with `ResourceId` and `AttachmentKind`.
Resource and captured-definition pin failures use `DigestMismatch`, `DigestKind` (`resource bytes` or
`captured definition`), `ExpectedHash` from the pin and `ActualHash` from the verified resource or definition.
Missing definitions use `ReferenceMissing`. Repeated edges and multiply produced children use
`IdentityDuplicate`, with the edge's resource and attachment kind or the child's typed key respectively.
Local reference guards distinguish scope, kind and missing text. Mathematical frames distinguish the
first nonfinite field (`ValueNonfinite`, with `ActualNumber` unset) from nonpositive uniform scale
(`ValueOutsideRange`, with finite `ActualNumber` and exclusive `MinimumNumber` 0).
Captured record anchors remain `Patch` values, separate from mathematical `ActualTransform` and
`ExpectedTransform`. Source wrappers and typed findings preserve both frames. Asset-local checks do
not invent a world `InstanceId`. Reconciliation uses `ReferenceMissing`, `AuthoredValueMismatch` and
`PrefabAddedChildExists`, retaining the local `Child` and a differing target as `ExpectedChild`.
Captured-descriptor reuse uses `ReferenceMissing` for missing pins, surfaces, record anchors and lights,
or `DigestMismatch` with `DigestKind` `captured descriptor` and both digests. It retains `WorldId`,
local `Child`, `ChildKind`, `PlacementId`, `ResourceId`, `AttachmentKind` and the known anchor `Patch`.
Effective support installation identifies duplicate surfaces or records through `IdentityDuplicate`
and missing anchors through `ReferenceMissing`. Missing support slots retain their `ReferencePath`,
resource and local placement. Effective surface lookups retain the missing `SurfaceId`.
Effective mathematical pose composition also uses the first failed scalar's `ValueNonfinite` or
`ValueOutsideRange` reason, with a pose-relative `FieldPath`. Nonfinite `ActualNumber` stays unset.
A finite nonpositive scale carries `ActualNumber` and exclusive `MinimumNumber` 0.
World-position refusals carry the full checked binary64 position and its static `OwnerId`, which
may name a placement or terrain chunk. Each axis accepts both positive and negative coordinates
exactly at `Limit`. The owner is independent of prefab instance, child and frame identifiers.

Prefab resolver admission distinguishes a missing identity (`SchemaValueRequired`), an unsupported
identity payload (`SchemaPayloadVersionUnsupported`) and an incompatible resolver or support recipe
(`ResolverContractMismatch`). Set, expansion, building and storey payload versions identify their actual
field and supported version 1. A missing storey owner uses `ReferenceMissing` with `InstanceId` and
`EntityId`. Missing bindings name the required `Child` and `InstanceId`. Liveness and source-inventory
mismatches use `AuthoredOwnershipMismatch` with `WorldId` when known. Transfer counts use
`ActualCount` and `ExpectedCount`, zero for a dormant source and exactly one for a transferred child.
Precise binary64 poses use the same first-failed-field scalar rules as float poses. Malformed digest
representation uses `DigestMalformed` with the submitted `ActualHash` rather than parsing the message.


Override key mismatches keep the submitted payload or previous `Child` separate from `ExpectedChild`.
Effective-reference admission first checks World scope, then the required child kind, then target
existence. These branches use `AuthoredValueMismatch`, `AuthoredReferenceKindInvalid` and
`ReferenceMissing`, retaining both actual and expected scope/kind and the referenced `EntityId`.
Numeric bindings first require positive `NumericId`, with `ValueOutsideRange` and `MinimumInteger` 1.
A positive numeric identity on another child kind uses `AuthoredReferenceKindInvalid`.

Unique collection admission uses `SchemaValueRequired` for a null collection or item, with `SubjectKind`
and the known item `Index`. Duplicate admission uses `IdentityDuplicate` with that role and index.
Its identifiers retain the actual key namespaces:

| Key | Typed identifiers |
| --- | --- |
| String identity or name | `EntityId` |
| Local child | `Child`, `ChildKind`, `EntityId` |
| Surface patch | `Patch`, `SurfaceId` |
| Source transfer | `InstanceId`, `Child`, `ChildKind`, `EntityId` |
| Attachment transfer | `PlacementId`, `AttachmentKind`, `ResourceId` |
| Scoped roof reference | `ReferenceScope`, `ChildKind`, `EntityId` |
| Standalone transform | `ChildKind`, `WorldId`, `EntityId` |

Equal text in different namespaces remains admissible. No identifier is parsed from its prose.
Duplicate null string identities retain the duplicate admission point, before later required-text validation.
Prefab validation and its throwing adapter preserve each concrete finding instead of classifying their prose.

Manifest source-unit scales must be finite and strictly positive. A nonfinite scale leaves `ActualNumber`
and `MinimumNumber` null. A finite nonpositive scale carries `ActualNumber` and `MinimumNumber` 0,
with that minimum exclusive. Indoor-span findings identify the first missing required subfield in
predicate order through `FieldPath`, including the parent-space identity, anchor surface identity and
individual domain-tag entries. They retain `SurfaceId` and the surface `Index`.

Identity registry refusals keep `EntityId` and `WorldId` distinct from the prefab-local `Child` and the
source `InstanceId`. `ChildKind` is the typed world ID namespace, so equal text in different kinds is
not a collision. `SubjectKind` names the checked role, such as `prefab binding`, `prefab transfer`,
`standalone owner`, `attachment producer`, `attachment owner`, `attachment transfer`, `attachment anchor`
or `standalone descriptor pin`. A missing anchor uses `SchemaValueRequired`, a missing transfer uses
`ReferenceMissing`, and an existing but inconsistent owner or anchor uses `AuthoredOwnershipMismatch`.
For record anchors, `Patch` is the submitted anchor and `ActualAnchor` is the standalone record's
actual anchor, if found. An anchor field path is relative to the attached child (`$.anchor`).
A missing descriptor pin identifies the document's `$.nativeResourceTransforms` collection.
`NumericId` is the placement's submitted value and `ExpectedNumericId` is its transfer's value.
The active-transfer owner count must equal `ExpectedCount` 1, with `ActualCount` carrying the measured count.
Descriptor comparison uses the standalone pin as `ExpectedHash` and the attachment as `ActualHash`.
Unavailable identifiers stay null. Collection identifiers remain owned read-only snapshots.

Runtime layer lookup uses `ReferenceMissing`, `DocumentId`, `LayerId` and `EntityId`, with `SubjectKind`
`scatter layer` or `companion layer`. Interior admission carries its `RecordId`, `SpaceId` and requested
`Patch`, plus the submitted `InstanceId` for a standalone-ownership refusal. Foliage edit preflight
uses `AssetClosureRequired` with `DocumentId`. Context preflight exposes its concrete `Finding` on
`MapFoliageContextMeasure` and on a refused capture result. Its five context budgets use
`WorkCapacityExceeded`, `FoliageStatus`, the limit's name in `Budget`, measured `ActualCount`, inclusive
`Limit` and `DocumentId`, plus `ActualByteCount` for source bytes. Partial residency uses
`CompleteDocumentRequired`. The preflight throwing adapter forwards that finding without changing its text.
Prefab expansion preflight reports the first failing `MaxInstances`, `MaxChildren`, `MaxReferenceEdges`,
`MaxPatches`, `MaxRecords`, `MaxGeometryElements`, `MaxBindings`, `MaxOverrides`, `MaxTransfers` or
`MaxPayloadBytes` budget in the same order as its guard. Checked-count overflow names
`PrefabExpansionCount` with `Limit = long.MaxValue` and leaves the unrepresentable `ActualCount` null.
The generation-context throwing adapter forwards its concrete finding and every ordered validator finding.
Editor transactions forward the first validator finding's reason and identifiers and the full ordered `Findings`
snapshot, with the released joined diagnostic or first water diagnostic unchanged. Native snapshots and staging
use `CompleteDocumentRequired` with `DocumentId` and `Operation`. Missing verified closures use
`AssetClosureRequired` with the operation and known `CommandType` or `SubjectKind`. An unsupported native
command uses `OperationKindUnsupported` with its `CommandType`. Refused staging leaves the live document and
history unchanged.
Native placement admission uses `SchemaValueRequired` for a blank identity before `IdentityDuplicate` for a
collision. Its lookup uses `ReferenceMissing`. These carry `DocumentId`, `EntityId`, `SubjectKind` `placement`
and `FieldPath` `Placements.Id`. Viewport companion lookup uses `ReferenceMissing` with the companion `LayerId`,
missing host `EntityId`, `DocumentId` and `FieldPath` `CompanionLayers.HostLayer`.

`ResolverVersion` is the document's declared version. `OptionsResolverVersion` is the version supplied
by the caller, and `ExpectedResolverVersion` is the version the operation requires. A guard accepting
several versions leaves the expected version null. Recipe identifiers retain `MapSupportRecipe` values.
`SourceName` identifies source context when supplied. Unavailable identifiers remain null.

Closed-member admission identifies a JSON field through `FieldPath` in `System.Text.Json.JsonException.Path`
format: `$` is the root, `.name` is a plain member, `['name']` is a member containing special characters,
and `[index]` is an array element. For example, `$.nativeAssets[0].id` identifies an ordinary member,
while `$['notes.extra']` and `$['notes[extra]']` identify literal names containing `.` and `[`.
Native and prefab duplicate admission identifies the containing object because the JSON member dictionary does not expose a
typed duplicate name. A tile reader supplies the document `Tile` and its file `Path`, while `SourceName`
keeps the directory context. JSON failures use the reader's path, zero-based `LineNumber` and zero-based
UTF-8 `BytePositionInLine` when available. No identifier is reconstructed from error prose. A JSON wrapper
containing a known structured document refusal preserves that refusal's reason, identifiers and original
object as its cause through the same source-copy method. A prefab file JSON failure also carries its known
`ResourceId`, including when deserialization has already wrapped the JSON failure as a document refusal.
The copy keeps an existing resource identity and source name when the wrapper has no new source name.

Prefab preflight paths start at the supplied payload root and retain array positions. Child and override
converter member paths start at that converter's object, with placement fields below `$.value`. Duplicate
prefab members identify their containing object. Null prefab members use `SchemaValueRequired`, while native
required-member nulls use `SchemaMemberNull`. Marker option and adoption schema guards use the same paths.
Plain JSON converter failures, including binding shape and exact enum checks, use `SchemaMalformed` with
the reader's path until the converter supplies its own structured cause. Payload-version validators,
marker authoring rules and other aggregate validation adapters retain their existing contracts until
all of their producing rules are typed.

`MapDocumentFinding` is an immutable record with `Reason`, developer prose in `Message` and the same
typed context. Resolver, native-metadata and prefab diagnostic producers retain these findings beside the existing string API's
prose projection. `MapDocumentException.Findings` can carry the full ordered finding list as an owned
read-only snapshot. Neither a change to the input list nor a cast of the exposed collection can change
that snapshot. Reassigning a nonempty collection on a finding, even from itself, creates a distinct snapshot
identity for record equality. A `with` copy without collection reassignment retains equality.
A source wrapper using the source-copy method preserves the reason, all identifiers and the
original refusal as the cause. `NativeDocumentService.Verify` uses this copy method, adding the storage
`SourceName` and `ResourceRoot` while retaining the released wrapper text and the original refusal.
Light and support JSON wrappers preserve known structured causes and otherwise use `SchemaMalformed`.
Light wrappers carry `ResourceId`. Their released prefix formatting still checks whether the quoted
resource name appears in the developer prose. Support root checks use
`SchemaObjectRequired`, `SchemaMemberMissing`, `SchemaMemberUnknown` and `SchemaMemberDuplicate`,
with `ResourceId` and `FieldPath`. Duplicate patches carry `IdentityDuplicate` and `Patch`. Duplicate
records carry `RecordId`, `Patch` and `SubjectKind` `support resource record`. Unsupported support payload
versions carry `SchemaPayloadVersionUnsupported`, `ActualVersion` and the owned `SupportedVersions`
snapshot. A support version that is not an integer uses `SchemaValueRequired` with no `ActualVersion`,
so the decoder's internal fallback is never presented as a supplied version. Required support collections
carry `SchemaValueRequired` and the first missing `FieldPath`.
Support patch admission distinguishes a null payload (`SchemaValueRequired`), a key mismatch
(`AuthoredValueMismatch`) and an undeclared owner (`ReferenceMissing`). It carries `ResourceId`,
`SurfaceId`, the submitted `Patch` when present and the dictionary's required `ExpectedPatch`.
Wall strips carry `RecordId` on facing, count, ordered-XZ, height and exact-representation refusals.
Cardinality uses `ValueOutsideRange` with the inclusive 2..4097 limits, while different chain lengths
use `SchemaArrayLengthMismatch` with actual and expected counts. Both name the failed `FieldPath`.
Height inversion uses `SurfaceWallHeightInverted` with the vertex `Index`. Exact arithmetic and normal
failures use `GeometryNotRepresentable` with `Operation`, and normal failures also name the segment
`Index`. Unresolved chain adapters preserve a known producer finding, including its reason and identifiers.
A chain without an inner refusal uses the adapter's `SpaceChainUnresolved` or `PhysicsWallChainUnresolved` reason.

`MapSeamValidator.ValidateFindings` and `ValidateCornerDependencyFindings` expose complete ordered
read-only findings. The existing string validators retain their prose projection. Paint priority admission
uses `SurfacePaintPriorityMisplaced` with the surface, field and submitted priority. Partial paint target
checks distinguish a missing surface (`ReferenceMissing`) from a paint-only target
(`SurfacePaintTargetInvalid`). Unavailable captured patches keep `PatchUnavailable`, `Patch` and
`PatchStatus`. Equal-priority conflicts identify both paint surfaces and their common priority.

Lattice-unit JSON checks throw `MapSurfaceJsonException`, a `JsonException` subtype sharing
`MapDocumentRefusal`. Object, numeric type, duplicate, unknown and missing-member checks retain
their relative `FieldPath`. Constructor failures preserve the scalar reason, numerator and denominator
or actual integer and minimum, with the original scalar refusal as their cause. Document JSON wrappers
preserve the known reason and identifiers while adding source context.
Patch-key JSON uses the same carrier for object, duplicate, unknown and required-value rules, with
the first failed `FieldPath` and `SurfaceId` only after it is read. Noncanonical or out-of-int64 slot
strings use `SchemaInt64Malformed` with the unmodified `ActualText`. Writer admission retains the
submitted surface identity. Whole-face paint failures identify the paint `SurfaceId` and physical
`OtherSurfaceId`. Material and presence crossings use `SurfacePaintCrossesPhysicalFace`, the target
`Patch`, face `Index` and `FieldPath` `Material` or `Presence`. All original source-preservation and
atomicity guarantees remain. Exact override overflow uses `GeometryNotRepresentable` and
`Operation = "paint override"`.

Asset-manifest field paths start at the payload root, retaining asset, resource and dependency array
positions. Direct reference and resource checks start at the supplied object's root. Required-value
findings retain the first failed field in predicate order. Native playable bounds distinguish nonfinite
components from finite unordered or empty extents, and support-search findings distinguish nonfinite
values from finite negative extents. `MinimumNumber` and `MaximumNumber` carry the submitted pair when
bounds are refused. Native metadata and manifest admission set `ActualNumber` only for finite values.
Payload checks requiring version 1 carry `SupportedVersions` as an owned read-only snapshot. A check
requiring only a positive version carries `MinimumInteger` 1 instead. Native metadata's string diagnostics
and admission points are unchanged. A missing surface frame uses `SchemaValueRequired`. Undefined
surface role, presence policy and support-binding kind values use `SchemaEnumUnsupported`, retaining
`EnumType`, `ActualInteger` and the first failed field.

`MapDocumentValidator.ValidateFindings` exposes the complete ordered read-only snapshot behind `Validate`.
Whole-document load/save, tiled manifest/load/save, native-bound validation and marker-adoption adapters
forward its first finding and retain every finding. Numeric reservation and allocation do the same for
numeric findings. A supplied reservation distinguishes nonpositive IDs (`ValueOutsideRange`, with
`NumericId`, `ActualInteger` and minimum 1) from duplicates (`IdentityDuplicate` with `NumericId`).
No reason is inferred from aggregate prose. Effective prefab expansion forwards the first numeric
finding and the complete ordered snapshot. Foliage generation-context and finalization adapters retain
the first concrete `Finding`, the ordered `Findings` snapshot and the original caught refusal when present.
The result owns its read-only findings. Stale or mismatched effective resources use
`AcquisitionSnapshotChanged` with `DocumentId`, `FieldPath` and known expected/actual snapshot values.
Prefab codec limits retain `Operation` `MapPrefabCapacity`, so foliage classification preserves their
capacity projection, including a missing collection rejected by those compound guards. Numeric count
overflow keeps its prior projection. A typed missing-reference identifier containing `limit exceeded`
or `overflow` stays `Invalid` instead of selecting a capacity category from its spelling.
Consumer-built message-only refusals from supplied prefab collections keep their legacy foliage
preflight status and have no typed finding.

`MapTopologyReferenceValidator.ValidateFindings` returns the complete ordered reference-integrity
findings as a read-only list. `Validate` retains their unchanged string projection. Admission carries
typed metadata, patch-local, identity, strip-facing, prism and ancestry findings. Record lookups distinguish missing
targets (`ReferenceMissing`), wrong anchors (`AuthoredOwnershipMismatch`, with requested `Patch` and
`ActualAnchor`) and wrong record types (`ReferenceKindMismatch`, with `RecordType`). They retain the
owning `RecordId` or separately supplied `SurfaceId`, and the target `RelatedRecordId`. Vertex checks
retain missing surface identity or the original denominator refusal. Chain checks distinguish enum,
cardinality, source ownership, height kind and source surface, with the vertex `Index` when applicable.
Portals retain the non-Exterior endpoint, interval count or missing endpoint membership. Vertical links
retain a forbidden geometry owner's `RelatedRecordId` and `RecordType`, or either endpoint's
membership count. Prism payload versions use
`SchemaPayloadVersionUnsupported`, missing Room references use `ReferenceMissing`, and a referenced
non-Room space uses `ReferenceKindMismatch`. A differing parent uses `SpacePrismParentMismatch` with
`RelatedRecordId`. Frame wrappers preserve the original scalar reason and identifiers. Space listings
retain their `SpaceId`, the portal or link `RelatedRecordId`, and the actual portal endpoint `EntityId`
when resolved. Alias checks identify the first incompatible peer field. Room footprints use
`SpaceRoomOccupancyInvalid` with their `RecordId`, separately from the Room prism count check.
Footprints forward the original legacy-recipe finding with their existing display suffix.

Bound findings identify the first incompatible lower or upper field. A missing surface uses
`SchemaValueRequired`, an undeclared one uses `ReferenceMissing`, an undefined bound kind uses
`SchemaEnumUnsupported`, and a conflicting opening uses `AuthoredValueMismatch`. Incompatible bound
kinds and surface roles use `SpaceBoundRoleMismatch`. An open-top bound outside its allowed role
uses `SpaceOpenTopInvalid`. Slot lists distinguish count limits (`ValueOutsideRange` with
`ActualCount` and limits 1 through 4096), adjacent duplicates (`IdentityDuplicate`), backwards order
(`OrderInvalid`), cells outside 0 through 4095 (`ValueOutsideRange`), in-slot cells outside the patch
(`SurfaceAddressOutOfPatch`) and openings over present cells (`SpaceOpeningCellsInvalid`). Cell
findings retain `RecordId`, `Patch`, `Index` and `ActualInteger`, with the genuine `PreviousInteger`
only when a preceding entry exists. Patch-address findings also retain local coordinates and dimensions.
Topology findings are complete. Whole-document and native-bound adapters retain their typed findings.

Structural document findings identify required or duplicate names with `FieldPath`, `EntityId`,
`SubjectKind` and the containing item `Index`. Missing companion hosts additionally retain the referenced
`LayerId`. Scalar findings use `ValueOutsideRange` or `ValueNonfinite` with the failed field and finite
actual values and limits. The existing validation predicates and diagnostic order remain unchanged.
Root format admission uses `DocumentVersionUnsupported` with `ActualVersion` and `SupportedVersions`.
Per-content findings retain placement, spawn and player-spawn identities and field paths. Unknown
terrain types carry the captured discriminator in `ActualText`. Missing layer filters retain their
referenced `LayerId`, item identity and array path. Sculpt bounds findings retain `SculptTile`,
the first escaping axis, actual extent and document limits. Tile-size findings carry the positive
minimum or the full sculpt-span minimum. Native paint priority reuses `SurfacePaintPriorityMisplaced`
with `SurfaceId`, `Index`, `FieldPath` and `ActualInteger`.
Per-tile adapters retain source document `Tile`, `SourceName` and `Path` on each finding. `SculptTile`
keeps sculpt coordinates separate from that document tile, including through source-context wrappers.

Prepared bound-context reads use `MapBoundContextException` with `ContextDemandUnprepared` as their
`Reason`, with the requested operation and patch, face or opening-cell identifiers. Existing
`InvalidOperationException` catches still receive these refusals. Preparation distinguishes an
undeclared surface or opening (`ReferenceMissing`), unavailable patch payload (`PatchUnavailable`),
an opening on another patch (`AuthoredOwnershipMismatch` with `ActualAnchor`) and a cell outside its
opening's ownership (`SpaceOpeningOwnershipInvalid`). Opening face counting distinguishes invalid
slot indices (`ValueOutsideRange` with limits 0 through 4095), cells outside the patch
(`SurfaceAddressOutOfPatch`) and present cells (`SpaceOpeningCellsInvalid`), retaining `Patch` and
`SlotCell`. A missing footprint lattice retains `SurfaceId`, `Patch`, `RecordId` and `SpaceId`.
Context preparation keeps the finding for its first selected capacity refusal while draining later
footprints. Later per-cell budget failures retain their own findings without replacing that context.
Missing occupied lower or upper geometry uses `PatchUnavailable` with `SpaceId`, boundary `OwnerId`
and `FieldPath` `Lower` or `Upper`. Prism record-count refusal uses `WorkCapacityExceeded` with
`Budget` `MaxRecordReads`, the measured `ActualCount` and inclusive `Limit`.
Prism parent traversal distinguishes a non-Room space (`ReferenceKindMismatch`, with `EnumType` and
`ActualInteger`), a differing parent (`SpacePrismParentMismatch`), a parent cycle
(`SpacePrismParentCycle`) and missing parent occupied geometry (`SpacePrismParentFootprintMissing`).
Depth and geometry work refusals retain the actual `Budget`, `ActualCount` and inclusive `Limit`.
These budgets are `MaxRecordDepth`, `MaxRecordReads`, `MaxInspectedFaces`, `MaxRefinementFaces` and
`MaxRefinementVertices`. An incomplete occupied acquisition carries `AcquisitionIncomplete` and
`AcquireStatus`. Wide partition status results retain an available original typed `Finding` through
narrowing. Their status and prose remain unchanged. Throwing adapters preserve the concrete producer
reason and identifiers.
Occupied partition classification retains `WaterDomainPairChecks` capacity provenance even when an
intermediate refinement projected the failure to `Invalid`.
Membership keeps a compiler-rejected legacy surface frame as `MissingGeometry`. Actual legacy recipe
eligibility failures keep their `Invalid` projection.

`MapChainResolution.Finding` is an additive init property. Its existing positional constructor,
deconstruction, status and `Detail` remain unchanged. Caught document refusals retain their original
reason and identifiers. Arithmetic overflow supplies `GeometryNotRepresentable`. Chain adapters in
apertures, membership, boundary coverage, terrain edits, physics and wall-strip compilation forward
that finding. They use the unresolved-chain reason only when no typed cause exists. Same-family
wrappers retain the original exception and every identifier through the common copy operation.
Lookup refusals distinguish unavailable records from wrong record kinds, retaining the requested
`RecordId`, anchor `Patch`, `PatchStatus`, actual `RecordType` and `ExpectedRecordType`.
Space traversal distinguishes alias cycles (`SpaceAliasCycle`) from ancestry cycles (`SpaceParentCycle`),
with the repeated `SpaceId`. Point-bound admission uses `SchemaValueRequired` with the footprint
`RecordId`, `SpaceId` and `FieldPath` for a missing opening reference or bound surface.
Unavailable source-cell and membership-demand patches retain `Patch` and `PatchStatus`, with the
chain `RecordId` or demanded `SlotCell` when known. A portal chain with no segment or height at the
query point uses `GeometryVertexSequenceInvalid` with the chain `RecordId` and `FieldPath` `Vertices`.
Prism payload admission selects version, lower finiteness, upper finiteness and separation in that
order. Aperture admission retains portal interval or repeated-vertex context and the column index
for an invalid height. Empty aperture geometry retains its record and anchor. Provenance membership
is an internal invariant: every recorded reference was already resolved from the same immutable
acquired facts that own the witness.

Prism admission distinguishes Room kind (`ReferenceKindMismatch`), parent disagreement
(`SpacePrismParentMismatch`) and lattice or multiple-prism occupancy (`SpaceRoomOccupancyInvalid`).
Prism-count findings retain `ActualCount` and `ExpectedCount`. Storey lookup uses `ReferenceMissing`
with the prism `RecordId`, `SpaceId` and storey `EntityId`. Storey admission checks payload version
before instance ownership, using `SchemaPayloadVersionUnsupported` or `AuthoredOwnershipMismatch`.
It retains the failed `FieldPath`, owning `InstanceId` and submitted owner `ActualText` when checked.
Prism containment findings identify the prism, its space and parent. Peer-overlap findings identify
both spaces. Strip-context findings preserve the original chain reason and identifiers.
These findings accompany the released string diagnostics. Whole-document and native-bound adapters
forward complete typed snapshots with the same first finding and diagnostic order.
Boundary findings distinguish uncovered intervals, overlapping authored bands, portal sequence
mismatches and portal air intervals. Missing footprint bounds and nonpositive separation retain
the footprint `RecordId`, `SpaceId` and `SlotCell`. Forwarded topology and seam findings retain
their producer's identifiers. Boundary source chains that omit an intermediate edge vertex use
`GeometryVertexSequenceInvalid` with `RecordId`, `Patch`, `FieldPath` `Vertices` and segment `Index`.
The internal coverage collector keeps known typed findings in diagnostic order while the remaining
refinement and water producers finish carrying every failure.
Coincident supports and peer cell overlaps retain both footprint identities and both cells through
`RecordId`, `RelatedRecordId`, `SlotCell` and `RelatedSlotCell`. Their concrete producer findings
survive the coverage collector. Diagnostic ordering uses producer-owned record context, including
prism diagnostics whose deeper refinement or water cause is not yet typed. It does not classify prose.
Topology producers also supply private sort subjects independently of their public identifiers.
Coverage retains the first ordinal record owner named by each diagnostic, then orders by cell and text.
Space-only findings and reference diagnostics naming several records keep that same ordering.

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
`Refresh`) and every tile file read refuse a member that a closed structure does not declare with
`SchemaMemberUnknown` and its `FieldPath`. Member names match case-insensitively, as the serializer reads them.
Before migration, a repeated or case-variant member anywhere in a document, manifest or tile refuses with
`SchemaMemberDuplicate` and the containing object's `FieldPath`, including open feature
payloads. Earlier engines let the last repeated value win. Unknown members of closed structures were previously
dropped, so a legacy file carrying one (a root `notes` field, say) now fails to load until the member is removed.
`terrain.features` items stay open, so a registered feature type keeps its own fields.

Depends on `KhaozEngine.Primitives`, `KhaozEngine.Serialization`, `KhaozEngine.Content`, and
`KhaozEngine.Terrain`. GPU-free. In the `Foundation` umbrella. The GUI editor (`KhaozEngine.MapEditor`)
and the `ke-mapedit` MCP tool are later frontends over this model, see
[`docs/design/MAP-EDITOR-DESIGN.md`](../docs/design/MAP-EDITOR-DESIGN.md).

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
