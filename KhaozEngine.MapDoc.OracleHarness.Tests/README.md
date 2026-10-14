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
The architecture guard pins exactly five direct engine references. The harness remains outside the
solution and public CI and is not packable.

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

`nativeDocumentPath` names an attested monolithic format-7 MapDoc document. The generator uses the released
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
