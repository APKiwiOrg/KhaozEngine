# MapDoc oracle harness

This non-packable test project is deliberately outside `KhaozEngine.slnx`. No engine or test
project references it. Full and selective public CI must never select it. Architecture tests
enforce those boundaries, including the permitted test-only TileWorld dependency.

## Public format-4 recorder

The recorder uses only synthetic fixtures from `KhaozEngine.MapDoc.Tests`. It records released
resolver-v1 placements, callback coordinates and exact float bit patterns before the format
changes. Confirm the resolver source still matches the released R1 implementation before recording:

```bash
git diff a87038f5a HEAD -- KhaozEngine.MapDoc/MapResolver.cs KhaozEngine.MapDoc/MapAuthoredIdentity.cs
```

Use the shared verification slot assigned by the scheduling owner. From the repository root:

```bash
dotnet build KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj -c Release -m:1
KHAOZ_R2_RECORD_DIR="$PWD/KhaozEngine.MapDoc.Tests/Fixtures/FormatFour" dotnet test KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj -c Release --no-build --filter FullyQualifiedName~FormatFourRecorder --logger 'console;verbosity=detailed'
dotnet format KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj --verify-no-changes --no-restore
```

Recording requires `KHAOZ_R2_RECORD_DIR`, format 4, and an absent or empty destination. It refuses
to overwrite a recording. Missing inputs fail rather than skip. Run the recorder once, pin its
printed digest in `FormatFourFixtures.PinnedFixtureDigest`, and commit the synthetic output.
Do not regenerate the recording to make a later compatibility test pass.

The digest covers every recorded file in ordinal relative-path order. Paths use `/`, and each
UTF-8 manifest line is `relativePath TAB lowercase-sha256 LF`. The manifest itself is not a file
in the fixture directory. Expectation tests read the committed copy from their output directory.

## Private oracle boundary

KhaozEngine is public and its shipped-source oracle is private. These rules restate the D1 boundary the
harness enforces:

- Public CI runs only synthetic fixtures. This project is the one place the exhaustive shipped comparison
  runs, outside `KhaozEngine.slnx`, and public PR CI never sees its credentials, the private checkout or this
  project.
- Every private test body runs inside `PrivateOracleEntry.Run`, the one entry boundary. It reads exactly the
  four named variables, validates them, creates the report securely, runs the body and maps every exception.
- Missing or unsafe inputs fail and are never skipped or waived.
- The report is created securely or the body never runs.
- Every failure route is sanitized. Bodies use only `report.Check` and `report.Record`, never `Assert` on
  source-derived values, `ITestOutputHelper`, the console or `[InlineData]` with source data.
- Nothing private enters this repository, its CI or its logs of record: no source bytes, per-path
  provenance, coordinates, derived geometry, counts, digests of private content or detailed reports. The
  engine records only pass or fail and the private record reference.

The private oracle entry is separate from the public recorder and uses Unix permission checks.
It has no discovery-time input reads. Never commit private shipped
source, derived geometry, coordinate inventories, private digests, snapshots or detailed reports
to this public repository. Do not add private credentials or source access to public CI.

Only approved public synthetic fixtures may be recorded here. The synthetic guard cases use a
deliberate public secret marker and temporary files, not a shipped world. They can be selected with
`FullyQualifiedName~PrivateOracleGuardTests` after a separately scheduled build/test window.

Actual private acceptance reads exactly `KHAOZ_R2_SHIPPED_SOURCE`, `KHAOZ_R2_ORACLE_PROVENANCE`,
`KHAOZ_R2_ORACLE_PROVENANCE_SHA256` and `KHAOZ_R2_ORACLE_REPORT`, in that order. The provenance
digest is checked before parsing. Its relative path list and aggregate digest bind the extraction.
The inventory chooses exactly one attested `world.json` path, without a game-specific directory or
unattested search. An untagged checkpoint is represented by an explicitly empty `tag` with its
full commit recorded. Comparison, units and oracle-equivalence metadata may be structured JSON.

Reports must be outside a git work tree, in an existing directory with mode `0700`. They are
created exclusively with mode `0600`. Source details and exception messages belong only in that
report. Failure messages expose only fixed reasons, category/count and the report digest, with
no inner exception. The entry owns report completion. Bodies call `Record` and `Check`, leaving
`ThrowIfAnyFailed` and disposal to the entry. Premature disposal followed by failure is a closed
report error and fails with the fixed secure-report message. Final report links are refused,
including dangling links. Parent directory links are resolved before containment and mode checks.
Missing or mismatched private inputs fail, never skip. Actual private source
selection, extraction and execution belong to the controller's separate acceptance step.

## Exhaustive terrain comparison

`ShippedTerrainComparisonTests` enters through `PrivateOracleEntry.Run`. `ShippedSourceInventory.ReadRegions`
loads the world named by the single attested `world.json` path under the verified extraction root and returns
its regions in signed region order. `ShippedTerrainComparison.CompareRegion` links the public
`LegacyOracleConverter` and calls only its assertion-free members. For every region and every plane it
checks, through `report.Check` only:

- exact corners against the stored lattice, with derived upper planes recorded as derived
- exact cell bytes, including the reserved Bridge bit
- equal legacy triangle and native support-face counts, every vertex within 0.00001 m and every normal
  component within 0.000001
- the legacy fallback cell set
- shared corners across every signed region seam
- no topology records

The document must use tile size 1 and four planes. The provenance `comparison` object must name
`spikePoint` as `{ "worldX": <metres>, "worldZ": <metres> }` on plane 0. An absent `spikePoint` is a
`spike-point-named` failure, and a malformed one fails the run. Each run records exactly one `spike-point`
outcome, carried by one region: no loaded region owns the point, the owning tile draws no ground, or the
point was compared. When it is compared, the native support height from `MapSurfaceCompiler.ExactHeight` must
equal the legacy plane-0 movement triangle within 0.00001 m. A world with no regions fails the run.
Per-plane counts and maximum errors are recorded only in the private report. An exceedance is a reported
failure carried privately, never a tolerance change.

## Bounded water comparison

`ShippedWaterComparisonTests.ShippedWater_IsExpressibleWithBoundedBodies` reads the same private inputs as
the terrain comparison through `PrivateOracleEntry.Run`. `ShippedSourceInventory.ReadWorld` loads the
attested world and its catalogs. Catalog entries resolve against the world directory and must name files
in the verified provenance. Missing or unverified inputs fail through the existing private entry boundary.
The harness links `LegacyWaterConverter` and `LegacyWaterComparison` without the synthetic oracle world.

The test records only counts through `report.Record`, covering bodies, rectangles, cells, boundary samples,
boundary ownership changes and floors above the water surface. Comparison failures are checked only through
`report.Check`, with their details confined to the private report.

Run once per pinned source. The controller runs the comparison at the candidate gate against the historical
game commit `74f57ee2` and current game main, each with its own provenance. The controller records provenance
SHA-256s, report SHA-256s and counts in `R4-WATER-ORACLE.md` in the private Grimhollow program folder.
Private counts, source paths and digests never enter this repository. Implementers build this test and run
only the synthetic harness guards, leaving the shipped comparison to the controller.

The harness requires its own explicit build, test and format verification because the solution
does not include it. An unfiltered harness run is not a substitute for the separately gated
recorder, synthetic guard and private inventory runs.

## Building extraction contract

`ShippedBuildingInputs.Read` and `ShippedBuildingComparison.Compare` are test-only. The shipped building
entry runs through the existing private entry boundary. It has no discovery-time reads. Implementers select
only `ShippedBuildingInputGuardTests` and `PrivateOracleGuardTests`, never the shipped building fact.
The architecture guard pins exactly eight direct engine references, MapDoc, MapDoc.Physics, MapDoc.Render3D,
Physics.Bepu, TileWorld, TileWorld.Netcode, TileWorld.Physics and TileWorld.Render3D. Both render edges
are test-only. The source and native recorders create no device, window or provider. The harness remains outside the solution and public CI and is
not packable.

The controller freezes the source and creates a fresh extraction and its provenance outside git. Nothing here searches a game
checkout, infers building membership, restamps a nominal definition or repairs incomplete extraction data.
The extraction contains both the frozen source and an explicit native comparison scaffold. This is a
comparison procedure, not the R11 lossless importer or game adoption acceptance.

### Provenance

Use the existing `ShippedSourceProvenance` camel-case object, with every field present:

| Field | Required value |
|---|---|
| `sourceRepository` | Nonempty source repository identity |
| `tag` | Exact source tag, or an explicitly empty string for an untagged checkpoint |
| `commit` | Full lowercase 40-character source commit |
| `sourceEnginePin` | Nonempty frozen engine pin |
| `extractionPathspec` | Nonempty relative extraction pathspec list |
| `paths` | Complete list of `{ "path": "relative/file", "sha256": "lowercase hash" }` |
| `aggregateSha256` | SHA-256 of the aggregate manifest defined below |
| `aggregateRule` | `sha256 of UTF-8 lines path TAB sha256 LF, ordinal by path` |
| `units` | Non-null JSON describing the source units |
| `rowOrientation` | Nonempty source row orientation |
| `comparison` | The building object described below |
| `oracleEquivalence` | Non-null JSON describing the claimed comparison scope and deferred acceptance |

Sort `paths` ordinally by path. Hash the UTF-8 concatenation of `path`, a tab, its lowercase SHA-256 and
LF for each file. The provenance's own byte digest is the existing outer environment digest. Include
exactly one attested `world.json`, all of its listed region files, every catalog named by it, each nominated
source prefab definition, the retained private generator and helpers, the native scaffold, every native root
manifest and its full resource graph,
and each original mesh, LOD, texture, collider, selection and light dependency read by the comparison.
The extraction may contain unrelated source leaves. The explicit selected inventory limits this comparison.
All files in the extraction must appear in `paths`, even files outside the selected buildings.

### Native scaffold

The native scaffold is generated mechanically by a private script from the frozen source plus the checked
membership manifest. Never hand-edit generated scaffold output. Retain the exact generator under
`generator/build-scaffold.py` in the extraction, together with any imported helper scripts. List each script
in provenance `paths` with its SHA-256, include those entries in the existing aggregate digest and retain
the resulting generator digest with the private acceptance evidence. Private script contents never enter
this public repository. Regenerate and re-attest output after any source, manifest or generator change.

`nativeDocumentPath` names an attested monolithic MapDoc document. Accepted format-7 R5 building scaffolds
and format-8 building correspondences retained by R6 marker extractions still load through the pure
version-only migrations to the current format 9. The reader checks the loaded current format. Do not
regenerate accepted R5 or R6 inputs merely to change the format number. Accepted R6 marker scaffolds
declare format 8 and new scaffolds declare the current format. The generator uses the released
`MapDocumentFile`, payload-1 prefab files and native manifest format. It must declare resolver 2,
`AuthoredBindingsV2`, its native asset roots, valid bounds and complete surfaces and topology. Its active
packed prefab instances must exactly match `buildings`. Preserve instance poses, complete bindings and
actual overrides. `Removed` and `Unpacked` instances are refused in this packed comparison. Every active
placement binding has the original numeric source ID. New parent instances have separate string IDs.
There are no extra native placement leaves outside the selected inventory.

The scaffold supplies the native geometry to compare and the authored semantic conversion that the old
world does not carry. Local rooms and storeys belong to their instances. Record children have declared
local anchor surfaces and complete typed reference slots. Empty local anchor patches are allowed when
parent support supplies the physical floor. World exterior records and explicit shared concealment groups
stay in the scaffold. Do not merge occupied room owners to express a concealment group.

For each selected floor's region and plane, include the `plane-<plane>` surface and patch produced by
`LegacyOracleConverter.ToNative`. Its stored lattice, cell bytes, presence and untransformed geometry must
match that conversion. The comparator checks the candidate floor before regenerating its source floor
bytes with R2, retaining the scaffold's topology records and recomputing semantic digests. It compares
source ground triangles separately with the fixed vertex and normal tolerances. It then builds reloaded,
independent native heads through `MapWorldBuilder`.

The native root closure includes immutable original mesh bytes and independently declared collider and
selection resources. Native physical shells are compared against `TileWorldColliders.Build` with the
pinned source options. At least one checked doorway leaf supplies a native compound shell. A doorway can
name several original frame leaves, and one opening can correspond to several source records. No source
collision algorithm is copied into this harness.

### Comparison object

Property names are case-sensitive. All properties in each described object are required, including
nullable properties. Unknown and duplicate comparison properties are refused. Arrays may be empty where
that concern has no authored records, except selected leaves, doorway correspondences and the required
probe classes. Counts below are invented examples, not acceptance values. Supply the real expectations
only in private provenance.

```json
{
  "schemaVersion": 1,
  "nativeDocumentPath": "native.json",
  "sourceCollisionOptions": {
    "wallThickness": 0.1,
    "blockedHeight": 3,
    "walkSurfaceThickness": 0.1
  },
  "expectations": {
    "definitions": 1, "buildings": 1, "originalLeaves": 4,
    "connectedInteriors": 1, "roofs": 2, "openings": 1,
    "doorwayRecords": 1, "dedicatedLights": 1
  },
  "definitions": [{
    "id": "cabin", "path": "definitions/cabin.json", "resourceId": "cabin",
    "keys": [
      { "key": "wall", "objectIndex": 0 },
      { "key": "roof", "objectIndex": 1 },
      { "key": "lamp", "objectIndex": 2 },
      { "key": "shelter", "objectIndex": 3 }
    ]
  }],
  "selectedLeafIds": [1, 2, 3, 4],
  "buildings": [{
    "id": "b", "definitionId": "cabin", "leafIds": [1, 2, 3, 4],
    "roomIds": ["b/room"], "storeyIds": ["b/ground"], "overrides": []
  }],
  "leaves": [], "floors": [], "rooms": [], "groups": [],
  "roofs": [], "lights": [], "doorwayRecords": [], "doorways": [], "probes": []
}
```

The empty arrays in this header example are filled with the rows below. The complete executable invented
extraction is `ShippedBuildingGuardFixtures.Create("valid")`. The `actual-override` fixture adds a named
placement tag override while keeping the nominal source prefab unchanged. `shared-concealment` proves two
instance-owned rooms joined by one explicit group, with emitting props counted separately from indoor lights.
`curved-selection` preserves an original KECL cylinder selection, including its base-aligned geometry.
`quarter-turn` checks equivalent source boxes and native compound geometry under a rotated original leaf.
`yaw-full-turn` places a leaf a full turn from its source yaw, and `derived-selection` gives the colliderless
lamp no authored selection and its exact derived box.

`definitions[].path` is a source `TilePrefabFile` definition. `resourceId` identifies the matching verified
native prefab resource. Logical IDs and verified `prefab/<escape(id)>/<sha256>` IDs are both accepted by
the released reader. `keys` bijectively maps every zero-based source prefab object index to its native
Placement child key. Removed or customized members are represented by the actual instance's overrides,
not guessed by restamping the definition. `buildings[].leafIds` is that actual instance's exhaustive original
leaf list. Its `roomIds` and `storeyIds` are effective world IDs. Its `overrides` is the exhaustive list of
`{ "kind": "Placement", "key": "lamp", "operation": "placement" }` rows. Kinds use the exact native child
enum names. Operations are `replace`, `placement`, `transform`, `add` or `remove`. Full typed override
values live in the native document. The reader verifies the inventory against those values.

Each `leaves` row has this shape:

```json
{
  "sourceId": 3, "buildingId": "b", "key": "lamp", "worldId": "b/lamp",
  "assetId": "lamp", "meshPath": "assets/lamp.mesh",
  "selectionPath": "assets/lamp.selection",
  "metadata": {
    "source": "invented/lamp", "license": "CC0", "category": "props",
    "textured": false, "sourceUnitsToMetres": 1,
    "renderBounds": { "min": [-1, 0, -1], "max": [1, 2, 1] },
    "lodBounds": null, "lightBounds": null
  }
}
```

`sourceId` is a positive signed 64-bit JSON integer. It must occur exactly once in `selectedLeafIds`,
`leaves`, the owning building list and its native numeric binding. A source roof that belongs to no nominated
building, such as a free-standing shelter, uses a null `buildingId` and a `key` equal to its `worldId`. Only an
inventoried unlinked roof may stand alone: its `roofs` row has a null `linkId` and no rooms or groups, and the
native scaffold carries it as a standalone placement with the original numeric ID plus an unlinked World roof
inventory entry and no roof link. No prefab definition is fabricated for it. It counts toward `originalLeaves`
and `roofs`, never toward `buildings` or `definitions`. Its transform, mesh, collider, selection and numeric ID
compare like any other leaf, and Auto must keep it visible. A standalone roof with concealment links refuses,
and a standalone leaf that is not a roof refuses as an unassigned building leaf. `worldId`, `key` and `assetId` identify
that effective leaf without changing its original ID. `meshPath` must equal the source archetype's
`meshRef`. `selectionPath` is the original authored KECL selection, or null when no selection was authored.
Metadata is the frozen original asset metadata. Bounds use finite XYZ arrays in asset source units.
`category`, `lodBounds` and `lightBounds` may be null. `renderBounds` is required.

When the frozen source has no per-model metadata, the generator derives the native asset `source`, `license`
and `category` mechanically and records the rule in the provenance `oracleEquivalence` object. `source` is
`<sourceRepository>@<commit>:<repo path of the mesh>`. `license` is the identifier read from the frozen
repository `LICENSE` file, an SPDX identifier when it states one, else `LicenseRef-<repository name>`, and that
`LICENSE` file is listed in provenance `paths`. `category` is the source archetype's category when the catalog
declares one, else null. The comparator checks these strings exactly like any other metadata.

A leaf with no authored `selectionPath` and a nonempty source collider capture must have no native selection.
A leaf with neither has a derived native selection, because the source picked it by its model box and a native
asset needs a selection or a collider. That selection is exactly one KECL box whose local min and max are the
leaf's `metadata.renderBounds` in source units. It compares through the authored selection geometry path under
the legacy matrix, as check `derived-selection`. A missing selection, an extra primitive or a different box fails.
The picking delta stays recorded as deferred R11 acceptance.

Leaf yaw compares as an angle. The wrapped difference `Math.IEEERemainder(native - source, 2 * Math.PI)` must
be within the normal tolerance, so an effective yaw a full turn from the source passes and a quarter turn fails.
Scale compares directly. The comparison never normalizes MapDoc yaw. Mesh bytes, metadata,
ordered tags, kind, asset and IDs compare independently. Source furniture differences require an explicit
actual-instance override. The original light-emitting placement remains in this leaf list.

| Array | Exact row fields and meaning |
|---|---|
| `floors` | `{ "buildingId": "b", "x": 0, "z": 0, "plane": 0, "underlay": 1, "overlay": 0, "paintId": null }`. Source tile indices and exact ushort material IDs. `paintId` is an effective paint ID, or null for an unset outdoor notch. The corresponding native canonical cell must retain world material where paint is unset |
| `rooms` | `{ "id": "b/room", "buildingId": "b", "storeyId": "b/ground", "cells": [{ "x": 0, "z": 0, "plane": 0, "y": 1 }] }`. Exhaustive original cell-centre membership for every supplied plane/Y layer. Y is a finite world height. Each layer must exactly cover the effective occupied room's cell centres, including transformed or polygon footprints |
| `groups` | `{ "id": "shared", "rooms": ["b/room", "c/room"] }`. Explicit effective concealment component links. Connected-interior count measures these room components, not merged occupied owners or portal traversal |
| `roofs` | `{ "leafId": 2, "linkId": "b/roof-link", "rooms": ["b/room"], "groups": [], "aboveY": 3 }`. Exhaustive original roof inventory. References and effective world above-height compare exactly. An unlinked shelter uses null `linkId` and empty room/group lists |
| `lights` | `{ "id": "b/indoor", "ownerLeafId": 3, "dedicated": true, "position": [2.5, 1, -0.5], "colour": [1, 1, 1], "intensity": 1, "radius": 2, "castShadows": false }`. Exhaustive effective world emissions. `dedicated` controls only the dedicated-light expectation. Emitting props use false and are still fully compared. Owner leaves must survive |
| `doorwayRecords` | `{ "id": "frame", "leafId": 1, "key": "wall" }`. Exhaustive original frame-record inventory, tied to the exact source leaf and definition key. Each record belongs to exactly one checked opening |
| `doorways` | `{ "id": "opening", "buildingId": "b", "leafIds": [1], "recordIds": ["frame"], "radius": 0.1, "length": 1, "clear": [[-1, 0.1, -1.5], [1, 0.1, -1.5]], "jamb": [[-1, 0.1, -0.5], [1, 0.1, -0.5]] }`. One distinct physical opening, its original frame leaves and source record correspondences. Paths contain at least two world XYZ foot positions. `length` is the capsule cylindrical segment. Source opening must be clear, jamb must block, and both native answers must match |
| `probes` | `{ "kind": "centre", "position": [0.5, 1, -0.5], "sourceSpaceId": "b/room", "spaceId": "b/room", "hiddenRoofIds": [2] }`. World point, original semantic correspondence, expected native occupied owner and exact Auto-concealed roof leaf IDs. Either space ID may be null for Outside |

Supply `centre`, `edge` and `storey` probe rows. A `boundary` row separately names an exact ownership
change under the one-owner policy. A changed source/native owner on any other probe fails. Boundary
changes are recorded privately as awaiting R11 acceptance. The native owner must still equal `spaceId`.
The roof policy checks Auto, AlwaysVisible and AlwaysHidden, including colour, caster, camera boom and
sky picking flags. Auto keeps unlinked shelters visible. Statics and head results must remain unchanged.

### Resolution and refusals

All provenance file names use `/`, are relative to the extraction root and contain no empty, dot or
parent segments, absolute roots, backslashes, colons or control characters. Prefab, mesh, native resource,
selection and native document paths use that same root. Catalog paths in `world.json` resolve relative to
its directory. A catalog may use `../catalog.json` only when normalization stays within the extraction and
the normalized file is attested. Ground material textures resolve the same way against the directory of the
catalog file that declared them, as the game resolves them. A texture such as `../textures/x.png` is valid only
inside that resolution, and only when the owning catalog and the normalized result both stay within the
extraction and the result is attested. A raw unresolved texture path is never read. A rooted texture or one
whose resolution escapes the extraction is refused. Every component of a dependency path must be free of symlinks, including
dangling links. There is no fallback search. The reader snapshots and hashes every attested file, checks
manifest dependencies before source loaders, and checks the extraction again after those loaders finish.
Native closure reloads use captured bytes rather than rereading files.

The reader refuses missing, stale, unattested, escaped or linked input, incomplete native dependency graphs,
wrong schema/format/payload, unsupported shape data, duplicate JSON members or identities, unknown definition
keys, unassigned leaves, incomplete instance binding/override lists, missing rooms/storeys, partial room
layers, dangling roofs/groups/lights/doorways and missing probe classes. The R2 floor adapter requires
metre-sized source tiles. Room closure refuses more than 1,048,576 candidate cells per layer before scanning.
Native expansion and query limits remain the released limits. It never truncates a room to pass a limit.
Expectation mismatches and geometry/semantic differences are failures, not missing-input skips.

Derived vertices compare at 0.00001 m and normal components at 0.000001. Geometry occurrences match
bijectively within those component tolerances, without depending on exact float sorting at rotations.
Collider and selection occurrences retain primitive type. Box primitives retain their oriented quad faces
and corners together, including within compounds. Meshes retain the shared vertex mapping, triangle
connectivity and cyclic winding. The mesh vertex mapping is chosen from coordinates before topology is
checked, so two vertex occurrences at one position can make an equivalent renumbered mesh refuse. That is a
conservative false refusal, never a false pass. Cross-shape equivalence is refused. Every exceeded component is
recorded with its source correspondence and exact tolerance in the private report. Integers, ordered tags,
identity and metadata compare exactly. Native revised-T4 envelopes compare between heads and after reload.
The private generator measures original mesh-local visual bounds from the frozen mesh data and records
those bounds in each leaf's attested `metadata.renderBounds`, with `sourceUnitsToMetres` applied once.
Picking evidence uses these source bounds and the released `TileObjectPlacement` transform. It records
`sourceBounds`, `nativeBounds`, signed `boundsDelta` (native minus source), original local bounds and the
source matrix. Deterministic unit-axis rays through source, native and union centres record their world
origin, direction, maximum distance, both hit/distance answers, `hitChanged` and nullable `distanceDelta`.
Source queries use the released `RayMath.IntersectObbY` seam that `TileObjectRaycast` uses. Native queries
use `MapShapeQueries.Raycast` against the actual interaction envelope. No rendering assembly is needed.
The source picking delta is labelled `deferred R11 acceptance` and is recorded separately from parity
failures. A nonzero picking difference does not make leaf geometry parity fail. Legacy visual-AABB picking
is never called lossless. Doorway checks prove physical apertures only, not walking, route or renderer integration.

The existing four-variable contract and secure report lifecycle are unchanged. A report needs an existing
0700 parent outside git and an absent path, created exclusively at 0600. The entry sanitizes exceptions and
final failure text. Only synthetic guard fixtures may expose their invented detail while diagnosing a
failed public guard. Actual source IDs, geometry, inventory, digests and report contents remain private.

## Marker extraction contract

`ShippedMarkerComparisonTests` uses `PrivateOracleEntry.Run`. Discovery and static initialization read no
process input. Implementers run only `ShippedMarkerInputGuardTests`, `PrivateOracleGuardTests` and
`ShippedBuildingInputGuardTests`, with explicit filters. Never run this project unfiltered.

The four input variables and their order stay unchanged. They are `KHAOZ_R2_SHIPPED_SOURCE`,
`KHAOZ_R2_ORACLE_PROVENANCE`, `KHAOZ_R2_ORACLE_PROVENANCE_SHA256` and `KHAOZ_R2_ORACLE_REPORT`.
Use every provenance field and the aggregate rule defined above. Freeze current source, including its
full commit and engine pin. Historical inventories are not expectations for this comparison.

The extraction attests exactly one `world.json`, all listed regions and catalogs, the source prefab
definitions needed for ownership, the retained generator and all imported helpers, both manifests below,
the monolithic native scaffold at format 8 or the current format, and every native root manifest and
transitive resource. Retain the generator in private source git before generating. Do not hand-edit
scaffold output. Regenerate and re-attest after changing any source, policy, script or dependency.
Source-support-only scaffolds need no invented physical shell. Every resource advertised by a declared
native root must still be present.

All paths below are attested extraction-relative paths. Containment, symlink refusal, verified byte
capture and post-loader rechecks apply. There is no checkout search or fallback. Native reloads use
captured bytes even if original files change later. Temporary tiled reloads stay under the secure report
parent and are removed after comparison. The report parent must exist at 0700 outside git, with an absent
report path. The boundary creates that report exclusively at 0600 and sanitizes every public failure.

### Marker comparison schema 1

Every listed property is required, including nullable properties. Objects are closed, property names
are case-sensitive, and duplicate or case-variant members refuse. Unknown rules, missing or extra
correspondences and duplicate source names or destination IDs refuse. Arrays use authored order where
order matters. These are field shapes, with invented examples only.

The provenance `comparison` object has exactly these fields:

```json
{
  "schemaVersion": 1,
  "nativeDocumentPath": "native.json",
  "roleManifestPath": "roles.json",
  "markerManifestPath": "markers.json",
  "expectations": {
    "total": 6, "playerStart": 1, "spawn": 2, "landmark": 1, "label": 2,
    "negativeX": 1, "supportBound": 6, "enabled": 5
  }
}
```

Expectations are nonnegative integer counts. `total` is checked against the independent source region
inventory, not the number of generated native rows. Other counts come from exhaustive source-to-role
correspondences and standing points. Disabled markers remain in the inventory.

The source world must use tile size 1, and every source marker must lie on plane 0. The reader refuses
any other tile size, and any marker on another plane, because the server ground and walk surfaces
describe plane 0 only. At tile size 1 a standing point's world X is `TileWorldSpace.WorldX(tileX, 1)`,
which equals tile X, the source integer X plus `offsetX`. Its world Z is `TileWorldSpace.WorldZ(tileZ, 1)`,
which equals minus tile Z, the source integer Z plus `offsetZ`.

The role manifest has exactly `schemaVersion`, `generator`, `sourcePrefabs`, `buildingManifestPath`,
`supports` and `markers`. `schemaVersion` is 1. `generator` has exactly `path` and `helpers`. Its path is
`generator/build-scaffold.py`, and `helpers` lists unique attested helper paths. `sourcePrefabs` lists
unique attested `TilePrefabFile` definition paths. `buildingManifestPath` is null when no R5 semantic
correspondence is retained. Otherwise it names an attested, complete R5 building comparison object with
the building contract above, which the released building reader validates against the same extraction.

Each `supports` row has exactly these fields:

```json
{
  "name": "ground-owner",
  "binding": { "kind": "Surface", "surfaceId": "plane-0" },
  "source": { "kind": "terrain", "plane": 0, "objectId": null }
}
```

Support names are unique and every declared support is used. A Surface binding has exactly `kind` and
`surfaceId`. A Space binding has exactly `kind`, `spaceId`, `referenceY`, `searchBelow` and `searchAbove`.
The kind is `Surface` or `Space`, IDs are nonempty strings, reference Y is finite, and the search extents
are finite, nonnegative and at most 1000000 metres. Null or foreign binding members refuse. These are
world-owner bindings in the Task 1 shape, including when authored markers expand from local bindings.
`source.kind` is `terrain` or `object`. Terrain uses null `objectId`. Object uses the positive original
ID of exactly one source object, whose catalog archetype must declare standing surfaces on the named
plane. An absent or nonpositive ID refuses.

Each role `markers` row has exactly these fields:

```json
{
  "sourceName": "arrival", "id": "arrival-id", "role": "entry-role",
  "projection": "player-start", "offsetX": 0.5, "offsetZ": 0.5,
  "supportName": "ground-owner", "heightMode": "Support", "heightRule": "terrain",
  "yaw": 0.371, "enabled": true, "archetype": null
}
```

`role` is caller vocabulary. `projection` is exactly `player-start`, `spawn`, `landmark` or `label` for
category counts. Offsets are finite tile-unit offsets from the recorded source tile, including footprint
and role policy. Yaw is finite radians. Enabled is a Boolean. Archetype is null for tag-driven identity,
otherwise a nonempty authored string. There are no inferred roles, offsets, yaws, enabled flags,
archetypes, bindings or height rules.

`heightRule` is exactly `terrain` or `standing` and names the authoritative server rule. `terrain` is the
`Ground` sampler of the released `TileWorldColliders`, built from the source terrain alone. It reads the
drawn tile triangles. `standing` is the higher of that ground and the released `TileWalkSurfaces.TryHeightAt`
on the marker's plane. The server stands bodies on the collider ground plus walk surfaces. The bilinear
`TileDocumentGroundHeight` is the client presenter's drawing rule. It differs from the drawn triangles on a
twisted tile, so it is not the comparison rule.

`heightMode` is `Support` for every actual private import. The shipped entry calls `RequireBoundImports`
before comparison and refuses any other mode. `Explicit` exists only to exercise comparator diagnostics
with invented synthetic inputs. It never relaxes the private import policy.

The marker manifest has exactly `schemaVersion` (1) and `markers`. Each row has exactly these fields:

```json
{
  "sourceName": "arrival", "x": -2, "z": 1, "plane": 0,
  "regionX": -1, "regionZ": 0, "supportName": "ground-owner",
  "spaceId": null, "parents": []
}
```

Source coordinates, signed region ownership and plane must equal the source record. The reader verifies
the world's marker index against every region marker, including exact ordered tags. Each source name
occurs once in both manifests. `supportName` must agree with the role manifest and name a support on
that plane. `spaceId` is null for Outside or the exact occupied world-space ID. `parents` is its ordered
parent chain without duplicates. Authored room, exterior, parent, topology and support dependencies must
be present in the scaffold. A binding must never reassign a home to an owner excluded by game policy.

### Marker comparison evidence

The comparator checks exhaustive identity, name, tag sequence, role, enabled state, archetype, standing
XZ, height, yaw, binding, value provenance, witness-bearing support, occupied owner and parent chain.
It also checks role, tag, typed projection and half-open rectangle queries, including disabled rows and
negative X. The engine filters exact native positions, so each head's rectangle edges and expected set
come from that head's own native points. A native point inside the source tolerance can still lie a
quantum below its source float, as a prefab-expanded marker can. Source fidelity stays the separate
position check. Object support is queried separately with only the named original owner, through the
released source sampler. This prevents another object's higher floor from concealing a wrong owner.

All XYZ and height components use 0.00001 metre tolerance. Wrapped yaw uses
`Math.IEEERemainder(native - source, 2 * Math.PI)` with 0.000001 radian tolerance. Identity, names,
roles, archetypes, enabled flags, bindings and tag order compare exactly. Tolerances are fixed.

Four fresh heads, two monolithic and two tiled, have matching native TileSize, equal identities and
annotation results, and independent mutable state. Every head is checked against source expectations.
Label standing-point offsets are recorded as deferred R11 acceptance. They are not a claim that the
old corner convention was preserved exactly. Generator output is a comparison scaffold, not the R11
importer or game acceptance.

Private reports retain category counts and detailed failures. Public output contains sanitized pass or
fail, fixed failure categories and the secure report reference only. No private source inventory,
coordinates, tags, geometry or digest belongs in engine git, public CI or logs.

## Foliage extraction contract

`ShippedFoliageComparisonTests.ShippedFoliage_PreservesEveryByteCandidatePoseAndDecision` uses
`PrivateOracleEntry.Run`. Discovery and static initialization read no process input, and the fact refuses
without its four inputs. Implementers run only `ShippedFoliageInputGuardTests`, `PrivateOracleGuardTests`,
`ShippedBuildingInputGuardTests` and `ShippedMarkerInputGuardTests`, with explicit filters. Never run this
project unfiltered, and never set a private input variable for a public run.

The seventh direct reference, TileWorld.Render3D, is test-only. Expected rows come from the released legacy
sampler, `TileFoliageSurface.SampleDetailed`, through the Task 3 channel kernel
`GroundCoverDistribution.GenerateTraced`. Its released wrapper `Sample` delegates without arithmetic
change, and the Render.Tests literals frozen at f1cd6656b stay the preservation oracle. Headless
construction of the sampler starts no window or GPU state. The valid public guard checks that no window
or GPU backend assembly loads. Actual rows come only from the native `MapFoliageDistribution`. No native
adapter writes an expected row.

### Foliage closure

The extraction attests exactly one `world.json`, every listed region and catalog, the retained
`generator/build-scaffold.py` and every imported helper, the correspondence manifest, the monolithic
current-format native scaffold, the complete tiled store with its foliage region packs, and every native
root manifest and transitive resource. Retain the generator and helpers in private git first. Two fresh
extractions must be byte identical without hand edits. The generator self-check compares the original
footprint, door, ordinal roof and normal rules before attestation. A discrepancy stops for a controller
ruling on the rule, never a tolerance. The provenance records the full source commit, an explicit or empty
tag, the engine pin and the generator commit. There is no checkout search, fallback or unattested helper.

The reader verifies the provenance digest before parsing, captures every attested byte once and rechecks
the extraction after the released world, catalog and closure loaders. Each tiled head is reloaded from
its own private copy of the captured store bytes under the secure report parent, verified as a whole
store and removed before use. A harness-only hook between the released loaders and that recheck lets a
public guard prove the recheck refuses a change the loaders could have observed. Private source tile size
1 is what the released terrain conversion requires. It is not a native engine limit.

### Foliage comparison schema 1

Every listed property is required, including nullable ones. Objects are closed, names are case-sensitive,
duplicate or case-variant members refuse, and every path is an attested extraction-relative path. Field
shapes only, with invented examples.

```json
{
  "schemaVersion": 1,
  "nativeDocumentPath": "native.json",
  "nativeTiledManifestPath": "native-tiled/map.json",
  "foliageManifestPath": "foliage.json",
  "buildingManifestPath": null,
  "expectations": {
    "layers": 2, "rasterBytes": 1045, "storedChunks": 3, "sourceOwners": 9,
    "models": 3, "ownedCandidates": 32768, "accepted": 4096, "rejected": 28672
  }
}
```

Expectations are nonnegative integers counted from the independently enumerated source over the compared
regions: every source region, then every absent region that a layer raster's closed sample rectangle
reaches. `rasterBytes` sums width times height over the source layers. `storedChunks` counts the 32 by 32
chunks holding a nonzero source byte, which is exactly what every native head advertises. `sourceOwners`
counts every source object with solid collision, a roof archetype or a door tag. `models` sums the source
layers' models. Owned, accepted and rejected candidates come from the actual legacy sampler, never from
native results. `buildingManifestPath` is required and must be null. Private foliage bindings are Surface
only, so no clearing target depends on the R5 room and building correspondence.

The correspondence manifest has exactly `schemaVersion` (1), `generator`, `layers` and `owners`.

```json
{
  "schemaVersion": 1,
  "generator": { "path": "generator/build-scaffold.py", "helpers": ["generator/helper.py"] },
  "layers": [{
    "sourceLayerId": "source-layer", "nativeLayerId": "cover-a",
    "support": { "kind": "Surface", "surfaceId": "plane-0" },
    "compatibilitySupports": [
      { "sourcePlane": 0, "nativeSurfaceId": "plane-0" }, { "sourcePlane": 1, "nativeSurfaceId": "plane-1" }
    ],
    "models": [{ "sourceArchetypeId": "reed-a", "nativeAssetId": "native-reed-a" }],
    "materials": [{ "sourceMaterialId": 1, "nativeResourceId": "grass-material", "kind": "Ordinary" }],
    "ruleIds": ["cover-solid-tree"], "maskIds": [], "ownerIds": ["1"],
    "candidateConvention": "world-lattice-v1"
  }],
  "owners": [{
    "sourceObjectId": "1", "sourcePlane": 0, "sourceArchetypeId": "tree",
    "nativeOwner": { "scope": "World", "kind": "Placement", "id": "owner-1" },
    "seatedSupport": { "kind": "Surface", "surfaceId": "plane-0" },
    "targets": [{ "nativeLayerId": "cover-a", "support": { "kind": "Surface", "surfaceId": "plane-0" } }],
    "ruleIds": ["cover-solid-tree"], "maskIds": []
  }]
}
```

Every binding is the closed Surface shape. A Space binding or any other private import shape refuses.
Layers correspond bijectively with the source layers and the native layers. Each layer's support is the
compatibility row of its source plane. The native ordinal table must hold exactly one row per
compatibility row, with the source plane as its ordinal. The reader never trusts a table ordinal as an
expected source value. It checks each row's surface independently: a metre and centimetre SupportFloor
with one patch per source region whose plane is authored, each with the semantic patch digest of the
released `LegacyOracleConverter.ToNative` conversion of that region and plane.
Models are the source layer's ordered archetypes and the native layer's ordered assets. Materials ascend by
source ID, agree in kind with the source catalog, name Material resources, equal the native layer's table
and cover every visible material on the layer's plane. `ruleIds` and `maskIds` are exactly the native
layer's rules and masks. `ownerIds` are exactly the owners whose native placement a layer rule matches or
whose mask the layer holds, and each names an owner row. Obsolete rule ordering fields refuse as unknown
members.

Owners correspond bijectively with source objects that have solid collision, a roof archetype or a door
tag, including transformed footprint centres. Each imports as a world Placement. Its seated support is the
compatibility row of the original object's plane in every layer that lists it. A support-bound placement's
resolved seat must name that surface. An explicit-height owner needs exactly one Surface owner support
correspondence per listing layer, naming that surface, and none elsewhere. Every matched rule or mask must
match the original object's own property, solid for Solid, roof for RoofAbove and a door tag for Door. A
Solid or RoofAbove footprint transformed through the owner's effective pose must cover exactly the original
tiles. A Door clears around the original footprint centre. Per owner and layer, the reader recomputes
applicability twice. From the source, Solid and Door apply on the layer's own plane and a roof only from a
strictly higher plane. Natively, an enabled matched rule or mask applies by equal seated and layer ordinals
for Solid and Door and a strictly higher seated ordinal for RoofAbove. Any difference refuses. `targets`
lists exactly the layers the source owner clears, so a higher roof lists only the layers below it, and a
same-tag peer on the layer's own plane lists none. No native placement a layer rule matches may be absent
from the owners.

### Foliage comparison evidence

Four heads, a client and a server of each storage form, each capture one full generation context that
every region and chunk call reuses. Settings and every raster byte compare before any sample work, and each
head advertises exactly the nonzero source chunks. Each compared region is then compared over its released
half-open area. In an absent source region the released build generates nothing. Inside the raster native
decides its domain before its raster there, so those rows expect OutsideDomain, and off the raster they
expect RasterZero. A capacity refusal streams four smaller half-open rectangles, which own exactly the same
candidates, and off-raster rows compare as zero raster. Every owned candidate address has exactly one row
on each side.

A row compares its seven channel values, the native reason the legacy stage maps to, its layer and its
support owner exactly. In the Task 7 decide order only a refused point has no support owner, so every
compared row, off-raster RasterZero and OutsideDomain included, names the bound Surface. An accepted pair
also compares its stable address and versioned key, the mapped model, the visible
material and its resource correspondence, and the model's material references. Finite checks run before
any tolerance. Position compares within 0.00001 m per component, root offset included. Orientation compares
the full surface-aligned rotation angle `2*acos(min(1,abs(dot(qSource,qNative))))` within 0.000001 radians,
normalized and evaluated in binary64 for comparison only. Scale, from the trace's scale channel, and
thinning rank compare exact binary32 bits. Every head's row stream digests to the same value.

Fixed failure categories are `foliage-context`, `foliage-settings`, `foliage-byte`, `foliage-chunk`,
`foliage-query`, `foliage-candidate`, `foliage-channel`, `foliage-decision`, `foliage-missing-sample`,
`foliage-extra-sample`, `foliage-key`, `foliage-model`, `foliage-material`, `foliage-nonfinite`,
`foliage-position`, `foliage-orientation`, `foliage-scale`, `foliage-rank`, `foliage-head` and one
`expectation-` category per expectation field. The private report holds one summary with layers, bytes,
chunks, scanned and owned candidates, accepted and rejected rows, each rejection stage, model choices,
support owners, head matches and failure counts, then each failed category with at most 16 retained
details. Successes stream as aggregates and the full candidate corpus is never retained. Public output is
the sanitized category and report digest only. This is a comparison, neither R11 importer acceptance nor
game adoption.

## Scene extraction contract

`ShippedSceneInputs.Read`, `ShippedSceneManifest.Read`, `LegacySceneRecorder.Record` and
`ShippedSceneComparison.Compare` are test-only. The private fact is
`ShippedScene_DrawSetsAndStreamingPreserveEveryResource`. It has no discovery-time reads and enters
through `PrivateOracleEntry.Run`. Implementers select only `ShippedSceneInputGuardTests` and
`PrivateOracleGuardTests`, plus the explicitly named building/marker/foliage guard regression filter.
Never run this project unfiltered or use a broad ShippedScene filter. Only the controller runs the
private fact, once per fully gated candidate, into a fresh absent report path.

Scope is full resource and typed submission comparison, complete versus streamed union, lifetime and
replacement. It is comparison scaffolding, not R11 conversion, importer acceptance, appearance or game
adoption. R8b owns private appearance. Correspondence maps keys only. It cannot contain expected
geometry, bounds, transforms or draw lists. Original catalogs and source routes supply expected data.
Native data never supplies the expected inventory. Each head validates its own closure/source/scope/
policy/generation stamp. Local generation counters need not agree between independent heads.

The full entry and the seam guards share `ShippedSceneComparison.CompareDrawSets` for keyed,
consuming one-to-one submission matching. Legacy prop emission retains `GroundObjectIds` and
`RoofObjectIds`, mapped owner/part/LOD keys, original meshes/material maps, transforms and all channels.
No coincident owner can borrow another owner's draw. Missing, duplicate and unmatched submissions
fail. Independent source canonical faces and water domains establish required batches, with prepared
keys and anchors serving only as checked transport correspondences. Native Write output never
selects the expected frame roster. Every expected terrain, light, water and foliage write is required
before its complete typed frame values are compared.
Submitted terrain triangles must cover their independently matched source faces without holes or overlap.
Their positions and normals bind to the source plane. Decoded material slots and weights bind to source
underlay, overlay cuts and the released legacy feather boundary, independently of render triangulation order.

`LegacySceneRecorder.RecordFoliage` independently traces the source sampler for every logical
generation whose source model reach intersects the declared window and motion padding. Owners proven
outside the legacy sampler's closed raster domain are excluded before generation. Empty generations
inside that domain are retained regardless of density or eligibility. Selection uses only source
layer settings and attested source model extents. Generation multisets, complete sample key multisets
and per-generation row counts are checked in both directions. Source matrices, ranks, model bindings
and padded bounds are checked against observed payloads. Both explicit plants and generated models compare every original part's
albedo, normal, roughness and alpha cutoff. Hidden roof expectations retain Shadow, Sky and Physical
while removing Colour, Selection and Camera. Camera and sky flags are checked independently.

Public full-entry guards omit a whole generation or one row, suppress each non-rigid category's Write
while retaining registration/inventory, remove a coincident owner, add a distinct part, change only a
roof camera/sky flag, and change each foliage material map. These guards use invented inputs and a
harness-only observation boundary. They never set process variables or invoke the private fact.

The controller retains generator/helper sources, their build pin and all comparison inputs privately.
Include exactly one attested source world manifest, every indexed region, catalogs, prefabs, membership,
roles, foliage correspondence, original meshes and every LOD, texture, collider, selection and light
input. Include the complete native tiled manifest, every page/patch/record/foliage pack and asset/material graph.
The tiled store is the required, independently verified bootstrap. A native monolithic document is optional.
Declare its attested path to require full native authored identity and head parity, or set `nativeDocumentPath` to null.
Parity covers native roots, authored tile content and surface payload facts without monolithic serialization.
Include live source render/roof/cluster/foliage/light policy and native policy. Every file participates in the existing ordinal path TAB SHA256 LF aggregate. Verify the
provenance byte digest before parsing. Reads use captured attested bytes with no ambient dependency
lookup. The released file readers are followed by a complete source recheck. Missing inputs fail.

### Closed scene manifest, schema 1

Every object has exactly the named case-sensitive fields. Unknown, missing, duplicate and case-variant
members refuse. The root may additionally declare the optional `limits` object below. All lists are required, including empty categories. String IDs are nonempty and ordinal.
Paths are attested extraction-relative paths, without roots, drives, backslashes, dot segments or links.
Source and native keys are unique, with complete source coverage checked before draw comparison.

| Row | Exact fields and constraints |
| --- | --- |
| Root | `schemaVersion`, `generator`, `nativeDocumentPath`, `nativeTiledManifestPath`, `buildingManifestPath`, `markerManifestPath`, `foliageManifestPath`, `assetSelections`, `materials`, `placements`, `lights`, `roofs`, `water`, `foliage`, `windows`, `sequence`, `policy`, `expectations`. `schemaVersion` is 1. `nativeDocumentPath` is an attested path or null, and `nativeTiledManifestPath` is required and non-null |
| limits (optional) | Required `scene` and `residency`, with optional `queries`, `decode`, `assets`, `foliage`, `foliageContext`, `water`, `refinement` and `maxProofBytes`. Every declared family object is complete. Null refuses. Every value is a positive JSON integer within its field's range. Unknown, missing, duplicate and case-variant fields refuse |
| limits.scene | Exactly `maxTiles`, `maxMetadataRows`, `maxMemberships`, `maxMembershipsPerResource`, `maxScannedBytes`, `maxMetadataBytes`, `maxSelectedResources`, `maxPatchScopes`, `maxPreparedFaces`, `maxPreparedBytes`, matching `MapSceneLimits`. Byte fields are int64, all other fields are int32 |
| limits.queries (optional) | Exactly `maxCandidatePatches`, `maxInspectedFaces`, `maxSupportIntersections`, `maxPageReads`, `maxRecordReads`, `maxRecordDepth`, matching `MapQueryLimits`. All fields are positive int32 values. Omission preserves the existing query defaults |
| limits.residency | Exactly `maxWorkers`, `maxQueuedPlans`, `maxQueuedBytes`, `maxAppliesPerUpdate`, `maxUploadBytesPerUpdate`, `maxDisposalsPerUpdate`, `maxResidentCpuBytes`, `maxResidentGpuBytes`, `maxPlanBytes`, `maxActiveWindows`, matching `MapRenderBudgets`. Byte fields are int64, all other fields are int32 |
| limits.decode (optional) | Exactly `maxPeakBytes`, `maxVertices`, `maxIndices`, `maxImagePixels`, `maxImages`, matching `GltfDecodeLimits`. Peak bytes are int64, counts are int32 |
| limits.assets (optional) | Exactly `maxDecodedBytes`, `maxParts`, `maxLods`, matching `MapRenderAssetLimits`. Decoded bytes are int64, counts are int32. Applies to complete selected sets, including cached pieces |
| limits.foliage (optional) | Exactly `maxCandidates`, `maxSamples`, `maxPrepareScopes`, `maxFaces`, `maxRuleCandidates`, `maxMaskCandidates`, `maxQueryChunks`, `maxQuerySamples`, `maxQueryRetainedBytes`, `maxDifferentialCandidates`, `maxChangeRows`, `maxComparisonPreparedChunks`, `maxComparisonRetainedBytes`, matching `MapFoliageLimits`. Query sample and byte totals, differential candidates and change rows are int64. Other values are int32 |
| limits.foliageContext (optional) | Exactly `maxSourceUtf8Bytes`, `maxChunkDescriptors`, `maxPlacements`, `maxSurfacePatches`, `maxPaintTargets`, matching `MapFoliageContextLimits`. Source bytes are int64, other values are int32 |
| limits.water (optional) | Exactly `maxPieces`, `maxPieceVertices`, `maxFootprintCells`, `maxBoundFaces`, `maxPairChecks`, `maxTriangles`, `maxBoundCells`, matching the scalar members of `MapWaterCompileLimits`. All are int32. Bound reads use `limits.refinement` when declared |
| limits.refinement (optional) | Exactly `maxSourceFacesPerBound`, `maxPairChecks`, `maxRefinementFaces`, `maxRefinementVertices`, `maxContextPatches`, `maxContextFaces`, matching `MapRefinementLimits`. All are int32. Applies to partial paint, nested support membership and water bound reads |
| limits.maxProofBytes (optional) | Positive int64 extent-proof metadata allowance. Omission retains the validator's default |
| generator | `{path,helpers}` with unique attested paths, including every retained helper and pinned helper-build input |
| assetSelections | `{sourceArchetypeId,nativeAssetIds}`. Unique source catalog IDs, each with nonempty unique native asset catalog IDs. Required for every declared source selection, including unplaced selections and foliage policy model IDs. Observed placement and foliage bindings must be included. Extra catalog mappings are permitted |
| materials | `{sourceMaterialId,nativeResourceId}`. Source IDs are positive ushort values |
| placements | `{sourceObjectId,nativePlacementId,lods}`. Object IDs are positive canonical decimal int64 strings |
| lods | `{sourcePath,nativeResourceId}` in original base/LOD order, checked against the original catalog |
| lights | `{sourceObjectId,sourceProfileId,nativeLightId}`. Multiple profile instances require independent original source declarations |
| roofs | `{sourceObjectId,nativePlacementId}`, checked against retained building links/groups and original roof inventory |
| water | `{sourceBodyKey,nativeBodyId,nativeSpaceIds}` with unique space IDs |
| foliage | `{sourceLayerId,nativeLayerId}`, checked against the full retained R7 correspondence |
| windows | `{id,minX,minY,minZ,maxX,maxY,maxZ,spaceIds,motionPaddingMetres}`. Bounds are finite ordered doubles, padding is explicit and nonnegative, space IDs are null or unique |
| sequence | `{operation,windowId}`. Operation is `Load`, `Unload` or `Recenter`, referring to valid named windows with valid transitions |
| policy | `{sourcePolicyPath,nativePolicyPath,probes}`, two attested files and nonempty probes |
| probes | `{id,windowId,observerX,observerY,observerZ,roofMode,timeSeconds,windSeed,focusX,focusY,focusZ}`. Coordinates/time are finite, seed is int32, roof mode is `Auto`, `AlwaysVisible` or `AlwaysHidden` |
| expectations | Exactly nonnegative integer `terrainFaces`, `placements`, `parts`, `lods`, `lights`, `roofs`, `waterRegions`, `foliageSamples`, `windows`, `sequenceSteps`. Counts are independently derived from source, never copied from native output |

The optional limits profile is part of the comparison object, covered by the provenance file's required
SHA-256 attestation before parsing. Changing any allowance requires a fresh attestation. Its scene limits
apply at source capture and every direct or streamed acquisition. Its residency budgets apply to every
complete window and the streaming sequence. Declared query limits follow every window into native dependency
closure, foliage support and owner seats, water, cluster cells and roof membership. Query refusals retain
their limit, demand and allowance through scene and residency results. Refusals include the limit name, requested amount and
allowance in typed capacity findings and diagnostic messages. Bounded discovery reports the first excess
demand, without scanning the remainder. No limit truncates a comparison to make it pass. Bounded completion exhaustion reports the final
transition capacity and findings. Ordinary upload pressure remains pending while the existing completion
loop can still make progress.

Omitting `limits` preserves existing behavior. Scene limits use the unchanged engine defaults. The harness
retains its existing 67,108,864-byte per-plan allowance and its 256-active-window streaming allowance,
with engine defaults for its other residency budgets and for omitted query limits. Each declared family
supplies its complete set of values. Omitted preparation and decode families preserve existing per-call
defaults, including allowances derived from remaining scene and residency memory. Explicit ceilings clamp
those remaining allowances. Shared residency byte admission enforces the declared foliage-context
policy. Foliage sample retention is aggregate across the captured context cache, with a 64 MiB default
on omission. Explicit larger allowances remain available through distribution and cache admission.
A cache retention refusal reports retained bytes plus the next reservation against the declared total.
The profile covers all 65 scalar limits in the nine registered families,
plus the extent-proof metadata byte allowance. Full input expansion and foliage correspondence capture
use the same declared query and context limits as window preparation. Decode limits follow extent
scratch work and every selected asset, material and composition decode into residency.

Linked building and marker paths retain their existing full comparison objects and their transitive
manifest dependencies. The foliage path retains the full R7 manifest. A participating category requires
its linked correspondence, an absent category requires null. Retain accepted earlier scaffold format
policies in those existing readers. The required scene tiled store and any declared monolithic document use current format 9.

Load introduces an inactive named window. Unload removes an active window. Recenter introduces the
new named window while preserving other active memberships, so load A/load B/unload A/recenter C/
unload B/unload C ends with zero live resources. The sequence must end empty.

A policy file has exactly `placements`, `animatedAssets`, `foliage`, `water`, `lights`, `modelExtents`. Placements has
`layers` and `defaultDrawRadius`. Layer rows have exactly the released `MapPlacementLayer` serialized
properties `id`, `assetIds`, `lodDistances`, `lodCrossfadeWidth`, `drawRadius`, `hlodDistance`,
`hlodCrossfadeWidth`, `hlodWeldCell`, `castsShadows`. Source selections name original catalog archetypes and
native selections name mapped catalog assets. Correspondence comes from the attested `assetSelections` rows,
independently of placements and both policies. Missing mappings refuse, and equal IDs are never assumed.
Declared selections may have no placements. Their attested
base/LOD meshes are still resolved from the catalogs, and they emit no draws. Foliage and water are complete closed camel-case serializations
of the candidate's `GroundCoverRenderOptions` and `WaterLook`, including nullable members and vector
fields. Stable profiles/casting are model registration inputs. Frame policy excludes stable values.
`modelExtents` is the explicit list of released `MapFoliageModelExtent` rows with `assetId`,
`boundsInSourceUnits`, `sourceUnitsToMetres`, `motionPaddingMetres`. Source extents come from original
meshes and the retained source motion policy, native extents name mapped assets. No padding is inferred.
Light extractor rows are `{sourceObjectId,sourceProfileId,offset,colour,intensity,radius,castShadows}`,
with finite vectors, positive radius, nonnegative intensity and unique source object/profile pairs.
An optional `shadowExclusion` retains the source emitter's declared rule. Its closed shapes are
`{"kind":"None"}`, `{"kind":"NearSphere","radius":0.625}` or
`{"kind":"FixtureBox","min":{"x":-1,"y":-1,"z":-1},"max":{"x":1,"y":1,"z":1},"margin":0.125}`.
Sphere radius and box margin are finite nonnegative world metres. Box corners are finite, strictly
ordered owner-local metres. Every source light has its mapped owner. The independent source expectation
uses the declared sphere, no exclusion, or the eight transformed fixture corners padded after world
transformation. It does not infer a fixture from owner meshes for an explicit rule. An absent or null
declaration preserves the original owner-mesh envelope fallback. Non-shadow-casting emitters submit no shadow.
The retained extractor derives them from the original live source policy. Legacy routes independently
execute the source settings, which are checked against native settings after key mapping.
Static shadow cache keys are ID-derived transport values. The mapped effective native light ID
determines its key through the released mapper policy. Capture preparation, the sink and diagnostics
preserve that key, without a persisted key field or a fixed illustrative numeric literal. Explicit plant
frame draw radius uses `placements.defaultDrawRadius`, matching the legacy prop route, with both density
factors fixed to one. Generated cover keeps `foliage.drawRadius`. Both retain the cover fade and wind settings.

Probe focus coordinates are either all explicit or all null. Explicit values become a checked framed
point and are compared as actual anchor-local submission inputs. Scope probes with omitted focus use the single TopDown
`MapCaptureFocus` support resolver. View requests retain their real framed-eye default. The comparator cannot infer a separate focus from the observer.
Midpoint probes must retain complementary 0/1 LOD phases, never two opaque overlapping tiers.

Geometry/translation tolerance is 0.00001 m, normal component and rotation-angle tolerance are
0.000001. Other scalar bits remain exact. Streaming duplicates require identical payloads. Compare
canonical terrain/material/paint coverage independently of render-only triangulation order, every
placement part/LOD/pose, selected/HLOD identities, full light owner/descriptors, four roof channels,
water body/space/free regions/area/level with closed regions excluded, and foliage keys/matrices/ranks/
padded bounds/model bindings. The complete tiled store loads and verifies independently for each head. It supplies two heads.
When declared, the monolithic form also loads independently twice, supplying two additional parity heads. Stream successes
into aggregates and retain bounded private discrepancy examples, never a full sample corpus.

Before attestation, retain one non-attesting diagnostic draft with every discrepancy kind/count and
bounded examples. A semantic rule difference needs a controller ruling, never a looser tolerance.
After correction, produce two fresh byte-identical extractions and audit closure/build pins. Only
after exact-candidate CI and the full three-backend gate does the controller run the private fact.
Public evidence is pass/fail and the private record reference. No private paths, names, coordinates,
counts, hashes, meshes, pixels or detailed reports enter public git, CI or logs.

The consumer [native evidence table](../KhaozEngine.MapDoc.Render3D/README.md#native-scene-verification)
names per-location frozen generation, precision, warm allocation, prepared foliage and six native
capture families. Actual wire/capture tests exercise submitted foliage focus and derived light keys.
The snapshot diagnostic callback borrows the submitted scene synchronously before rendering,
including warm-up. CPU runs with skipped GPU cases do not replace the exact-candidate complete
Metal, Direct3D 11 and Vulkan gate or the controller's private acceptance procedure.
