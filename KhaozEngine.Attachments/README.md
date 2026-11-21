# KhaozEngine.Attachments

GPU-free definitions for rigid and skinned mesh parts. Rigid parts attach to named joints,
while skinned parts declare garment meshes that use the host body's pose. Reference this package
explicitly from headless publishers or consumers. Its only engine dependency is the dependency-free
Primitives package, which supplies `MalformedDataException`. It is not included in Foundation or Server.

`AttachmentSetDefinition` copies parts, orders them by positive stable ID and exposes read-only
parts and distinct mesh references. Repeated mesh keys must declare the same lowercase SHA-256.
Parts derive from `AttachmentPartDefinition` as `RigidAttachmentPartDefinition` or
`SkinnedAttachmentPartDefinition`. `ValidateReferences` requires every mesh hash and every explicit
rigid joint to resolve. Unknown programmatic part kinds are refused.
Joint names and mesh keys require well-formed UTF-16 and retain their ordinal identity without
Unicode normalization. Supplementary characters and literal U+FFFD remain valid.

A worn set also declares its `AttachmentLayer` and the body regions it covers. Build one with
`new AttachmentSetDefinition(parts, layer, covers)`. The layers are `Body`, `Underclothes`, `Garment`, `Armour`
and `Outerwear`, ordered from the skin outward, and a higher layer hides the regions it covers on the body and on
every lower layer. `Layer` is null and `Covers` is empty for a set built without a layer, which is every set read
from formats 1 and 2. In a set with a layer, every `RigidAttachmentPartDefinition` names the region it sits on in
`Region`, and every `SkinnedAttachmentPartDefinition` tags its triangles in `Regions` as `NamedRegionRun(Region,
TriangleCount)` runs in triangle order. Both are init only properties, empty by default, so existing constructors
and deconstruction are unchanged. A set without a layer refuses parts that carry either tag.

The set owns its region data. `Covers` holds unique names in ordinal order, and a skinned part's runs are copied
with adjacent runs of one region merged, so two sets that tag every triangle alike and cover the same regions in
any listing order share their canonical bytes and `SemanticSha256`. Two skinned parts are equal when their runs
are equal run by run. Region names need well-formed UTF-16 and keep their ordinal identity. A name is only a name
in the set: whether the body has that region is checked when an [outfit is resolved](#outfit-coverage) against the
body's [region map](#body-region-maps).

`AttachmentSetJson.Parse(utf8, sourceName)` accepts formats 1, 2 and 3. Format 1 contains only rigid parts
and has no `kind` member:

```json
{
  "formatVersion": 1,
  "parts": [
    {
      "id": 1,
      "mesh": {
        "key": "parts/plate.glb",
        "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
      },
      "joint": "joint",
      "local": [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    }
  ]
}
```

Format 2 requires `kind` on every part, using exactly `"rigid"` or `"skinned"`. Rigid parts retain
`joint` and `local`. Skinned parts contain only `id`, `kind` and `mesh`:

```json
{
  "formatVersion": 2,
  "parts": [
    {
      "id": 1,
      "kind": "skinned",
      "mesh": {
        "key": "parts/garment.glb",
        "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
      }
    }
  ]
}
```

Format 3 is format 2 with region coverage. The root adds `layer`, one of `body`, `underclothes`, `garment`,
`armour` and `outerwear`, and `covers`, an array of unique region names that may be empty. A rigid part adds
`region`, and a skinned part adds `regions` as `[name, triangleCount]` pairs. All four are required in format 3 and
refused as unknown members in formats 1 and 2:

```json
{
  "formatVersion": 3,
  "layer": "garment",
  "covers": ["a", "b"],
  "parts": [
    {
      "id": 1,
      "kind": "skinned",
      "mesh": {
        "key": "parts/garment.glb",
        "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
      },
      "regions": [["a", 3], ["b", 1]]
    }
  ]
}
```

`AttachmentSetJson.CurrentFormatVersion` stays 2 and `RegionsFormatVersion` is 3. A public constant is compiled
into its callers, so changing the value of an existing one would silently change what their code means.

An engine at 24.3.0 or earlier checks root members against `formatVersion` and `parts` before it reads the
version, so it refuses a format 3 set as `JsonPropertyUnknown` on its first format 3 member, `layer` in canonical
bytes, not as `FormatVersionUnsupported`. Publish format 3 only after every reader is on 24.4.0 or later.

The example hash is a placeholder. Hash the exact self-contained GLB bytes. `local` contains 16
row-major finite numbers and must be affine and invertible. Unknown or duplicate members, missing
fields, unsupported versions and invalid parts throw `AttachmentSetDataException` with `SourceName`.
Invalid constructor definitions throw `AttachmentSetArgumentException`. Both carry `AttachmentSetRefusal`
and the identifiers listed in [Set refusals](#set-refusals). Root members are checked against the union of
every format's root members before the version is read, and against the declared format's own list after. A
document with a `layer` and no `formatVersion` is therefore refused as `JsonPropertyMissing` for the version,
not as `JsonPropertyUnknown` for the layer, because the version decides what every other member means.

`Write(definition)` emits the lowest format that expresses the definition. A set with a layer writes
format 3, even when all its parts are rigid. Otherwise a set whose parts are all rigid writes format 1, even after
parsing format 2, and a set containing skinned parts writes format 2. Existing canonical format 1 and 2 bytes and
semantic hashes stay unchanged. Property order is `id, mesh, joint, local` in format 1 and `id, kind, mesh` followed
by rigid `joint, local` in format 2. Format 3 writes `formatVersion, layer, covers, parts` at the root and appends
`region` to a rigid part and `regions` to a skinned part. Signed zero is normalized without changing other float values.
The writer emits canonical UTF-8 with no trailing newline. Its SHA-256 is
`SemanticSha256`. Source whitespace and part-array order do not change that revision.
Keep each part ID stable across source reordering. The renderer derives temporal identities from
that semantic revision, the part ID and material ordinal. A changed declared mesh hash changes
the revision, including changes to embedded material images.

Implement `IAttachmentAssetSource.Read(key)` for another byte source. The directory implementation
returns a fresh snapshot and refuses symlinks or reparse points, including in root ancestors.
Keys are relative forward-slash paths without URIs, backslashes, empty segments or dot segments.
Keep the source tree stable while reading. Link checks do not provide an operating-system sandbox
against concurrent filesystem replacement.

Call `AttachmentGlb.ValidateRigidEmbedded(bytes, sourceName)` before importing. It checks GLB 2
headers and chunk bounds, requires embedded buffers and images, and refuses skin, animation and
morph data. It does not replace the renderer's full glTF geometry validation.

Call `AttachmentGlb.ValidateSkinnedEmbedded(bytes, sourceName)` for skinned parts before importing.
It shares the GLB 2 envelope, embedded-resource and recursive duplicate-key checks. Exactly one
embedded buffer and one skin are required. Every mesh node must reference that skin. Joint indices
and ordinal joint names must be unique, with non-blank names and at most 128 joints. Joints must share
a common ancestor, which may be a joint or another node. Node hierarchy cycles, multiple parents,
repeated children and malformed child references are refused. Animations,
morph targets, node or mesh weights and influence sets beyond `JOINTS_0` and `WEIGHTS_0` are refused.

Every primitive requires paired VEC4 joint and weight accessors with matching POSITION counts.
The policy decodes all four joint indices, including zero-weight lanes, and refuses out-of-range
indices, negative or non-finite weights and all-zero weight vertices before importer normalization.
Unsigned byte and unsigned short joints, float or normalized unsigned byte/short weights, strided
views and sparse overrides are supported. Accessor and sparse ranges are checked against the declared
embedded buffer before reading. Optional inverse-bind accessors must contain one finite MAT4 per
joint. Host joint-name mapping and bind-pose compatibility are renderer-level checks.

## Equipment coverage companions

These sidecars describe offline table inputs. Runtime selection and the blocking verification contract
are documented in the living [equipment coverage guide](../docs/EQUIPMENT-COVERAGE.md). The consumer
owns state-key semantics and sampled evidence does not provide a continuous proof.

`EquipmentCoverageJson.Read(utf8, identity)` reads strict equipment coverage format 1, separate
from attachment JSON v1/v2. `EquipmentCoverageDefinition` binds an exact attachment-file SHA-256
to a host-map semantic SHA-256. It owns 1 to 4 commands with unique positive IDs. Each command
names a rigid part and host region, a finite affine frame with positive determinant, disjoint
source support ranges totaling at most 1024 triangles, and finite coverage bounds and distances.
Part, region and support resolution against actual assets belongs to their binding layer.

`Write(definition)` emits canonical UTF-8 without whitespace, with commands sorted by ID, support
ranges sorted by source ordinal and signed zero normalized. `SemanticSha256(definition)` hashes
`KhaozEngine.EquipmentCoverage/1\n` followed by those canonical bytes. This domain-separated
identity differs from a fetched file's byte hash. Attachment JSON canonical bytes are unchanged.

Malformed data throws `EquipmentCoverageDataException`, derived from `MalformedDataException`.
Its public `EquipmentCoverageRefusal Reason`, caller `Identity` and rooted JSON `Path` identify
the refusal without parsing messages. Count refusals expose `Count` and inclusive `Limit`.
Identifier refusals carry `CommandId`, `PartId` or source ordinals where representable.
`IdentifierValue` also retains unsigned authored IDs above UInt32. Wrappers preserve all fields
and the typed inner refusal. Null arguments and a blank caller identity retain BCL exceptions.
Streaming preflight refuses duplicate and unknown members and excess array entries before DOM
allocation or reading their values. Numeric overflow, malformed digests and unsupported versions
have distinct reasons.

Unknown names of at most 64 decoded UTF-16 code units retain their complete `MemberName` and
member `Path`, independent of UTF-8 or Unicode-escaped spelling. Longer names have null `MemberName`,
`MemberNameOmitted = true` and the actual containing-object `Path`. Diagnostic decoding uses a fixed
buffer and never publishes a fabricated name or a partial name.

## Body region maps

A body region map names up to 128 regions of one body and tags every triangle of the body's skinned
material parts with one of them. `RegionSet` is a 128 bit value holding region indices 0 to 127, so a
set of covered regions copies without allocating. `With` and `Contains` refuse an index outside that
range with `ArgumentOutOfRangeException`.

`BodyRegionMapJson.Parse(utf8, sourceName)` reads format 1:

```json
{"formatVersion":1,"rig":"rig-v1:<sha256>","regions":["a","b"],"parts":[{"triangles":4,"runs":[[0,3],[1,1]]}]}
```

`rig` is the body's complete [rig identity](#body-rig-identity). `parts` follow the body GLB's skinned
material part order. Each part declares its triangle count and lists `[region, triangleCount]` runs in
triangle order, which must add up to it. `BodyRegionMap` exposes `RigIdentity`, `Regions`, `Parts` and
`TryRegion(name, out index)` with ordinal names. `TriangleCount(part)` is the sum of a part's runs.
`WriteTriangleRegions(part, span)` fills one region index per triangle and needs a span of exactly that
length. Both refuse a part index outside `Parts` with `ArgumentOutOfRangeException`. Adjacent runs of one
region merge, so `Write(map)` is canonical and `Sha256` is the lowercase SHA-256 of those bytes.

Refusals throw `BodyRegionMapDataException`, derived from `MalformedDataException`, with a
`BodyRegionMapRefusal Reason` and `SourceName` from `Parse`. The `BodyRegionMap(rigIdentity, regions, parts)`
constructor applies every content rule except `TriangleCountMismatch`, because only JSON declares a part's
triangle count. `Parse` builds the map through that constructor, so a constructor refusal from `Parse` has
`SourceName` but no `JsonPath`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `FormatVersionUnsupported` | a format other than 1. | `JsonPath`, `FormatVersion` |
| `JsonPropertyUnknown`, `JsonPropertyDuplicate`, `JsonPropertyMissing` | an unknown, repeated or absent member. | `JsonPath`, `PropertyName` |
| `JsonValueShapeInvalid` | a value of the wrong JSON shape, such as a run that is not an integer pair. | `JsonPath` |
| `JsonMalformed` | malformed JSON syntax. | No additional identifiers |
| `RigIdentityInvalid` | a rig that is not `rig-v1:` and a lowercase SHA-256. | `RigIdentity` |
| `RegionNameEmpty`, `RegionNameMalformedUtf16`, `RegionNameDuplicate` | a blank, malformed or repeated region name. | `RegionIndex`, `RegionName` |
| `TooManyRegions` | more than 128 regions. | `RegionCount` |
| `RegionIndexOutOfRange` | a run naming an undeclared region. | `PartIndex`, `RunIndex`, `RegionIndex`, `RegionCount` |
| `RunEmpty` | a run of fewer than one triangle. | `PartIndex`, `RunIndex` |
| `TriangleCountMismatch` | runs that do not add up to the part's declared triangle count. | `PartIndex`, `DeclaredTriangleCount`, `ActualTriangleCount` |
| `TriangleCountOverflow` | a part whose runs add up past Int32 at some run, from the constructor or from `Parse` through it. | `PartIndex`, `RunIndex`, `ActualTriangleCount` |

## Outfit coverage

`OutfitCoverage.Resolve(body, worn)` works out what an outfit hides on one body from the body's region map and
the worn set definitions, in the order the caller wears them. It is a pure function with no renderer, so a tool can
explain an outfit offline. Call it when the outfit changes, never per frame. It allocates, and the result keeps no
reference to the caller's list.

The rule, with the reason for each part:

- `BodyHidden` is the union of `Covers` over every worn set that has a layer, a `Body` layer set included. Any
  layered piece sits on top of the skin, so skin drawn under it would only show through where the piece is thin or
  moves. A `Body` layer piece replaces the body's own look over the regions it covers.
- `HiddenFor(wornIndex)` is the union of `Covers` over worn sets whose layer is strictly higher. A piece is drawn
  over everything below its layer, so only a higher layer can hide it. Two pieces on one layer never hide each
  other, because neither is on top: hiding either would open a hole that nothing else fills.
- A set with no layer, as formats 1 and 2 read, covers nothing and `HiddenFor` is empty for it. It declares no
  regions, so the rule has nothing to hide it by, and every set worn before format 3 keeps drawing exactly as it did.
- `RigidPartHidden(wornIndex, partId)` is true when `HiddenFor` holds the region the rigid part sits on. A rigid
  part is one unsplit mesh on one joint, so it hides whole. A rigid part of a set with no layer is never hidden.
- `SkinnedPartRegions(wornIndex, partId)` lists a skinned part's runs in triangle order as `RegionRun` with body
  region indices, the shape `BodyRegionMap.Parts` uses. A run is hidden when `HiddenFor` contains its region. The
  drawing layer builds its index lists from these runs and the hidden set without resolving names again. It is
  empty for a part of a set with no layer.

`WornCount` is the number of worn sets. A worn index outside it throws `ArgumentOutOfRangeException`, and a part ID
the set has no rigid or skinned part for throws `ArgumentException`. A null body, list or worn set throws
`ArgumentNullException`.

Every region name a worn set uses is resolved here: its covered regions, each rigid part's region and each skinned
run's region. A name the body does not declare throws `OutfitDataException`, derived from `MalformedDataException`,
with an `OutfitRefusal Reason`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `RegionUnknown` | a covered region, rigid part region or skinned run region the body does not declare. | `WornIndex`, `PartId` (null for a covered region), `RunIndex` (skinned runs only), `RegionName` |

## Coverage claim check

A piece's `Covers` hides the skin in those regions, so a false claim opens a hole in the body.
`CoverageClaimCheck.Verify(bodyMeshes, body, piece, pieceMeshes, maxDistanceMetres)` checks the claim offline: at
the rest pose the piece must lie over every triangle of every region it claims. A tool runs it when a piece is made.
It casts one ray per claimed body triangle against every piece triangle, which suits low poly meshes, so it is never
meant to run per frame and builds no acceleration structure.

Geometry arrives as `CoverageClaimMesh(Positions, Indices)`, plain `ReadOnlyMemory<Vector3>` positions and a
`ReadOnlyMemory<uint>` triangle list, so publishers and tools stay GPU free and never need a renderer mesh. The rules,
with the reason for each:

- `bodyMeshes` follow the region map's part order, one mesh per part with the part's triangle count, because the map
  tags triangles by part and position and a mesh that disagrees would be checked against the wrong regions.
- `pieceMeshes` are already in the body's rest frame. A rigid part is placed by its joint's rest transform and its
  local transform before the call, since the check has no rig to place it with.
- The ray starts at each claimed body triangle's centroid and runs along its outward normal, taken from counter
  clockwise winding as glTF front faces. The centroid is the one point that always lies inside the triangle, and the
  outward normal is the direction a piece worn over the skin lies in.
- A piece triangle is hit from either side. A garment's inner surface faces the skin, and that is the surface an
  outward ray meets first, so a back face test would call a well made garment uncovered.
- A hit counts when its distance is from 0 to `maxDistanceMetres`, both ends included. A piece surface behind the
  skin does not cover it, because the skin would be drawn over it. The consumer supplies the distance and the engine
  sets no default, because how far a piece may stand off the skin depends on the game's art and scale. It must be
  finite and greater than zero, or `ArgumentOutOfRangeException` is thrown. The distance is taken from the hit
  triangle's plane.
- The ray and triangle test is watertight (Woop, Benthin and Wald), so a ray that crosses the piece's surface always
  hits at least one of its triangles, even where it lands on an edge or a vertex that triangles share, and whatever
  their index order. Mirrored bodies and garments put centroid rays exactly on a seam edge, and a test with exact
  barycentric bounds rounds both halves of the seam the same way and misses both. The test moves every vertex into the
  ray's frame by the same arithmetic in every triangle and uses edge side tests that change sign exactly when an edge
  is walked the other way, so it needs no tolerance: a ray that passes outside the piece's outline by more than
  rounding error never counts as a hit. Shared vertices must have identical positions. A real crack between two
  piece triangles is a hole, and rays through it are uncovered. An unwelded mirror seam whose two halves round
  their seam vertices to different positions is a crack, and so is a T junction, where one triangle's vertex sits
  on another triangle's edge, because the edges on either side are no longer the same edge. The check reports a
  ray that lands in either as uncovered. Weld the seam and split the long edge at the junction's vertex.
- A zero area body triangle has no normal. It is counted in its region's `TriangleCount` and `DegenerateCount` and
  never makes a claim dishonest, because it cannot show through anything. A sliver, a triangle of nearly zero area,
  does get a normal, but its direction is only as good as its winding and its rounded positions, so a sliver may test
  along a noisy direction.
- A piece with no layer, as formats 1 and 2 read, claims nothing. It is honest with an empty report.

The `CoverageClaimReport` lists each claimed region in the body map's region order as a `CoverageClaimRegion` with
its `Name`, `TriangleCount`, `DegenerateCount`, `UncoveredCount` and the `Uncovered` triangles as
`CoverageClaimTriangle(PartIndex, TriangleIndex)`, in part then triangle order, so a tool can show them. `Honest` is
true when no claimed triangle is uncovered. The ray test runs in double precision, so float geometry neither
overflows nor underflows and only a triangle of truly zero area counts as degenerate.

A null argument throws `ArgumentNullException`. Every other refusal throws `CoverageClaimDataException`, derived from
`MalformedDataException`, with a `CoverageClaimRefusal Reason`. `MeshKind` says whether a refused mesh is a `Body`
or `Piece` mesh and `MeshIndex` is its zero-based position in that list, which for a body mesh is its map part.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `BodyMeshCountMismatch` | a body mesh count other than the map's part count. | `ExpectedMeshCount`, `ActualMeshCount` |
| `BodyTriangleCountMismatch` | a body mesh whose triangle count differs from its map part's. | `MeshKind`, `MeshIndex`, `ExpectedTriangleCount`, `ActualTriangleCount` |
| `RegionUnknown` | a covered region the body's map does not declare. | `RegionName` |
| `IndexCountNotTriangles` | a mesh whose index count is not a multiple of three. | `MeshKind`, `MeshIndex`, `IndexCount` |
| `IndexOutOfRange` | an index past the mesh's positions. | `MeshKind`, `MeshIndex`, `IndexPosition`, `VertexIndex`, `VertexCount` |
| `PositionNotFinite` | a position with a NaN or infinite component. | `MeshKind`, `MeshIndex`, `VertexIndex` |

The check has its own refusal family rather than `OutfitDataException`, because it resolves no worn list, so the
outfit refusal's `WornIndex` would have nothing to name.

## Set refusals

`AttachmentSetArgumentException` derives from `ArgumentException` for constructor admission.
`AttachmentSetDataException` derives from `MalformedDataException` for JSON and reference admission.
Both require a non-nullable `AttachmentSetRefusal Reason`. Constructor refusals retain `ParamName`.
Plain null-argument and source-name checks retain their BCL types.

`AttachmentSetJson.Parse` throws `AttachmentSetDataException` for every content refusal. Its wrapper
preserves a known set reason and every identifier, adds `SourceName`, prefixes the developer message
with `sourceName: message` and keeps the original as `InnerException`. A constructor refusal's
`ParamName` is also copied onto the data exception. Native JSON syntax failures use `JsonMalformed`,
and framework value-access failures use `JsonValueShapeInvalid`. Catch `MalformedDataException`
where callers previously caught `InvalidDataException` for set data. The sealed BCL type cannot be a base.
`AttachmentSetCache.Get` propagates these JSON refusals before mesh reads or uploads.

For schema checks, `JsonPath` uses `$` for the root and identifies the refused value. Missing,
duplicate and unknown properties identify their containing object plus `PropertyName`. `PartIndex`,
`ComponentIndex`, `CoverIndex` and `RunIndex` are zero-based. `ActualCount` is null when a local transform is not
an array. `ActualHash` is null for a missing mesh reference. Messages remain developer prose.

Every JSON refusal also carries `SourceName`. Direct constructor and reference checks have no source name.

| Reason | Refuses | Identifiers |
|---|---|---|
| `PartsEmpty` | a set with no parts. | `PartCount` |
| `PartNull` | a null part in the set. | `PartIndex` |
| `PartIdInvalidOrDuplicate` | a zero or repeated part id. | `PartId`, `PartIndex` |
| `PartKindUnsupported` | an unsupported attachment-part runtime kind. | `PartId`, `ActualType` |
| `MeshKeyInvalid` | an unsafe or malformed mesh asset key. | `PartId`, `MeshKey` |
| `MeshHashInvalid` | a mesh hash that is not lowercase SHA-256. | `PartId`, `MeshKey`, `DeclaredHash` |
| `JointNameEmpty` | a rigid part without a joint name. | `PartId` |
| `JointNameMalformedUtf16` | a rigid joint name with malformed UTF-16. | `PartId`, `JointName` |
| `LocalTransformInvalid` | a non-finite non-affine or singular local transform. | `PartId` |
| `MeshHashConflict` | one mesh key declared with conflicting hashes. | `PartId`, `MeshKey`, `DeclaredHash`, `ExistingHash` |
| `JointReferenceUnresolved` | a joint outside the provided ordinal reference set. | `PartId`, `JointName` |
| `MeshReferenceMissingOrHashMismatch` | a mesh absent from the reference closure or with a different hash. | `PartId`, `MeshKey`, `ExpectedHash`, `ActualHash` |
| `FormatVersionUnsupported` | an unsupported attachment JSON version. | `JsonPath`, `FormatVersion` |
| `PartsNotArray` | a parts member that is not an array. | `JsonPath` |
| `JsonPartKindUnsupported` | an unsupported authored attachment kind. | `JsonPath`, `Kind` |
| `JsonPartIdInvalid` | a part id outside the unsigned integer representation. | `JsonPath` |
| `LocalTransformLengthMismatch` | a local transform with other than sixteen numeric entries. | `JsonPath`, `ActualCount`, `ExpectedCount` |
| `LocalTransformComponentInvalid` | a non-finite or non-numeric local transform component. | `JsonPath`, `ComponentIndex` |
| `JsonObjectRequired` | a schema value that is not an object. | `JsonPath` |
| `JsonPropertyDuplicate` | a repeated property in an attachment JSON object. | `JsonPath`, `PropertyName` |
| `JsonPropertyUnknown` | an unknown property at an attachment JSON schema location. | `JsonPath`, `PropertyName` |
| `JsonPropertyMissing` | a required property absent from attachment JSON. | `JsonPath`, `PropertyName` |
| `JsonStringRequired` | a non-string attachment schema member. | `JsonPath`, `PropertyName` |
| `JsonMalformed` | malformed JSON syntax while retaining the caller source name. | `InnerException` |
| `JsonValueShapeInvalid` | a JSON value shape rejected by a framework accessor, or a `covers` entry or region run of the wrong shape. | `InnerException`, or `JsonPath` with `CoverIndex` or `PartId` and `RunIndex` |
| `LayerMissing` | format 3 without a layer, or a part with a region tag in a set built without a layer. | `JsonPath` and `PropertyName`, or `PartId` |
| `LayerUnknown` | a layer that names no defined layer. | `JsonPath` and `LayerName`, or `Layer` |
| `CoveredRegionDuplicate` | a covered region listed more than once. | `CoverIndex`, `RegionName` |
| `PartRegionMissing` | a part without its `region` or `regions` in a set with a layer. | `PartId`, and `JsonPath` and `PropertyName` from JSON |
| `PartRegionRunEmpty` | a skinned part's run of fewer than one triangle. | `PartId`, `RunIndex`, `RegionName` |
| `RegionNameEmpty`, `RegionNameMalformedUtf16` | a blank or malformed covered or part region name. | `RegionName`, with `CoverIndex` or `PartId` and `RunIndex` |
| `RegionTriangleCountOverflow` | a skinned part whose runs add up past Int32. | `PartId`, `RunIndex`, `RegionName`, `ActualTriangleCount` |

## GLB refusals

`ValidateRigidEmbedded`, `ValidateSkinnedEmbedded` and `ComputeRigIdentity` preserve the originating
container, accessor, hierarchy, rigid geometry, skinned geometry or rig identity family. Catch
`MalformedDataException` for these malformed-data refusals and inspect the concrete type's non-nullable
`Reason`. This changes the catch contract from `InvalidDataException`, which is sealed.

Each family carries `SourceName`. A public wrapper copies its reason and every identifier into the
same family, prefixes the developer message with the source name and keeps the original as
`InnerException`. JSON syntax failures use `AttachmentGlbContainerRefusal.JsonMalformed`. Framework
JSON shape and numeric conversion failures use `ValueShapeInvalid`, retaining the original error.
`JsonPath` is the canonical schema location, with indexes carried separately where applicable.
Property names outside ASCII identifier syntax use escaped JSON string brackets, so a literal dot
in a property name cannot be confused with a nested property.
Accessor `Index` is a nullable `long` so it preserves both table references and unsigned sparse indexes.
Messages are developer prose and are not a contract.

### AttachmentGlbContainer

`AttachmentGlbContainerDataException` carries `AttachmentGlbContainerRefusal`. Every reason includes `SourceName`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `HeaderInvalid` | an invalid GLB magic version or declared total length. | `ByteLength`, `DeclaredLength`, `Version` |
| `ChunkHeaderTruncated` | a truncated GLB chunk header. | `Offset`, `AvailableBytes` |
| `ChunkLengthInvalid` | an unaligned or out-of-bounds GLB chunk length. | `Offset`, `DeclaredLength`, `AvailableBytes` |
| `FirstChunkNotJson` | a GLB whose first chunk is not nonempty JSON. | `ChunkType`, `ChunkLength` |
| `UnexpectedChunk` | an unexpected or duplicate post-JSON GLB chunk. | `Offset`, `ChunkType` |
| `JsonRootNotObject` | a GLB JSON root that is not an object. | None |
| `AssetVersionUnsupported` | missing or unsupported glTF asset version metadata. | `AssetVersion` |
| `EmbeddedBufferLengthMismatch` | a declared buffer inconsistent with the BIN chunk. | `DeclaredLength`, `BinaryLength` |
| `MultipleEmbeddedBuffers` | more than one embedded buffer. | `BufferCount` |
| `PropertyNotArray` | a GLB array property with another JSON shape. | `PropertyName` |
| `ArrayEntryNotObject` | a non-object entry in a GLB object array. | `PropertyName`, `EntryIndex` |
| `ExternalResourceUri` | a resource URI instead of embedded bytes. | `ResourceKind` |
| `DuplicateJsonProperty` | a duplicate property anywhere in the GLB JSON tree. | `JsonPath`, `PropertyName` |
| `JsonMalformed` | malformed GLB JSON syntax with source context. | `InnerException` |
| `ValueShapeInvalid` | a framework JSON shape or numeric conversion failure with source context. | `InnerException` |

### AttachmentAccessor

`AttachmentAccessorDataException` carries `AttachmentAccessorRefusal`. Every reason includes `SourceName`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `TypeMismatch` | an accessor with another vector or matrix shape. | `JsonPath`, `AccessorIndex`, `ExpectedType`, `ActualType` |
| `NormalizedNotBoolean` | a non-boolean normalized flag. | `JsonPath`, `AccessorIndex` |
| `EncodingUnsupported` | an unsupported accessor component or normalized encoding. | `JsonPath`, `ComponentType`, `Normalized` |
| `CountNotPositive` | an accessor with zero elements. | `JsonPath`, `Count` |
| `OffsetWithoutBufferView` | a nonzero accessor offset without a buffer view. | `JsonPath`, `ByteOffset` |
| `SparseCountOutsideAccessor` | an empty or oversized sparse override table. | `JsonPath`, `SparseCount`, `AccessorCount` |
| `SparseIndexEncodingUnsupported` | sparse indices with an unsupported component type. | `JsonPath`, `ComponentType` |
| `SparseIndexNotIncreasingInRange` | sparse indices outside the accessor or not strictly increasing. | `JsonPath`, `EntryIndex`, `Index`, `PreviousIndex`, `AccessorCount` |
| `CompressionUnsupported` | compressed fit geometry in rig-v1. | `JsonPath`, `BufferViewIndex`, `ExtensionName` |
| `BufferIndexNotZero` | a view referencing another embedded buffer. | `JsonPath`, `BufferIndex` |
| `BufferViewOutsideBuffer` | a buffer-view range outside the embedded buffer. | `JsonPath`, `ByteOffset`, `ByteLength`, `BufferLength` |
| `ByteStrideInvalid` | an unsupported or misaligned accessor stride. | `JsonPath`, `ByteStride`, `ElementSize`, `StrideAllowed` |
| `RangeOrAlignmentInvalid` | an accessor range or alignment outside its view. | `JsonPath`, `ByteOffset`, `RequiredBytes`, `ViewLength`, `Alignment` |
| `PropertyMissing` | a required accessor or container property absent from JSON. | `JsonPath`, `PropertyName` |
| `ObjectRequired` | a required object with another JSON shape. | `JsonPath`, `PropertyName` |
| `NonNegativeIntegerRequired` | a JSON index that is not a supported nonnegative integer. | `JsonPath` |
| `ReferenceInvalid` | a missing out-of-range or non-object table entry. | `JsonPath`, `TableName`, `Index`, `EntryCount` |

### AttachmentHierarchy

`AttachmentHierarchyDataException` carries `AttachmentHierarchyRefusal`. Every reason includes `SourceName`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `NodesNotArray` | a node table with another JSON shape. | `JsonPath` |
| `NodeNotObject` | a non-object node in the hierarchy. | `JsonPath`, `NodeIndex` |
| `ChildrenNotArray` | a child list with another JSON shape. | `JsonPath`, `NodeIndex` |
| `ChildIndexOutsideNodes` | a child node index outside the hierarchy. | `JsonPath`, `NodeIndex`, `ChildIndex`, `NodeCount` |
| `MultipleParents` | multiple parents or repeated child references. | `JsonPath`, `NodeIndex`, `ParentIndex` |
| `Cycle` | a cycle anywhere in the node hierarchy. | `JsonPath`, `NodeIndex` |
| `JointsHaveDifferentRoots` | skin joints that do not share one tree root. | `JsonPath`, `ExpectedRootIndex`, `ActualRootIndex` |

### AttachmentRigidGeometry

`AttachmentRigidGeometryDataException` carries `AttachmentRigidGeometryRefusal`. Every reason includes `SourceName`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `SkinPresent` | skin data in rigid attachment geometry. | No additional identifiers |
| `AnimationPresent` | animation in rigid attachment geometry. | No additional identifiers |
| `NodeDeformationPresent` | a rigid node carrying skin or morph data. | `NodeIndex`, `HasSkin`, `HasWeights` |
| `MeshMorphWeightsPresent` | mesh morph weights in rigid geometry. | `MeshIndex` |
| `PrimitiveMorphTargetsPresent` | primitive morph targets in rigid geometry. | `MeshIndex`, `PrimitiveIndex` |
| `SkinAttributePresent` | joint or weight vertex attributes in rigid geometry. | `MeshIndex`, `PrimitiveIndex`, `AttributeName` |

### AttachmentSkinnedGeometry

`AttachmentSkinnedGeometryDataException` carries `AttachmentSkinnedGeometryRefusal`. Every reason includes `SourceName`.

`ValidateSkin` shares these reasons with rig identity, whose joint limit is independent of the renderer limit.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `EmbeddedBufferCountMismatch` | skinned geometry without exactly one embedded buffer. | `BufferCount` |
| `SkinCountMismatch` | skinned geometry without exactly one object skin. | `SkinCount` |
| `AnimationPresent` | animation in a skinned attachment. | No additional identifiers |
| `MorphWeightsPresent` | node or mesh morph weights in a skinned attachment. | `JsonPath`, `ElementIndex` |
| `MeshNodeSkinMismatch` | a mesh node not referencing skin zero. | `NodeIndex`, `SkinIndex` |
| `JointCountOutsideRange` | a skin joint table outside its allowed count. | `JsonPath`, `JointCount`, `MaximumJointCount` |
| `JointNodeDuplicate` | repeated skin joint node indices. | `JointOrdinal`, `NodeIndex` |
| `JointNameEmpty` | an unnamed skin joint. | `JointOrdinal`, `NodeIndex` |
| `JointNameDuplicate` | repeated ordinal joint names. | `JointOrdinal`, `JointName` |
| `InverseBindCountMismatch` | a bind accessor whose count differs from the skin joint count. | `BindCount`, `JointCount` |
| `InverseBindNonFinite` | a non-finite inverse-bind matrix. | `JointOrdinal`, `ComponentIndex` |
| `MorphTargetsPresent` | morph targets in a skinned primitive. | `JsonPath` |
| `ExtraInfluenceAttribute` | unsupported influence sets beyond JOINTS_0 and WEIGHTS_0. | `JsonPath`, `AttributeName` |
| `VertexAttributeCountMismatch` | position joint and weight accessors of different lengths. | `JsonPath`, `PositionCount`, `JointCount`, `WeightCount` |
| `JointIndexOutsideSkin` | a vertex joint index beyond the skin joint count. | `JsonPath`, `VertexIndex`, `Lane`, `Index`, `JointCount` |
| `WeightInvalid` | a non-finite or negative skin influence weight. | `JsonPath`, `VertexIndex`, `Lane`, `Weight` |
| `WeightsAllZero` | a vertex with no positive skin weight. | `JsonPath`, `VertexIndex` |

### RigIdentity

`RigIdentityDataException` carries `RigIdentityRefusal`. Every reason includes `SourceName`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `EmbeddedBufferCountMismatch` | rig identity without exactly one embedded buffer. | `BufferCount` |
| `SkinCountMismatch` | rig identity without exactly one object skin. | `SkinCount` |
| `MeshNodeSkinMismatch` | a fitted mesh node referencing another skin. | `NodeIndex`, `SkinIndex` |
| `MeshNodeHasNoMesh` | a skinned node without mesh geometry. | `NodeIndex` |
| `MorphWeightsUnsupported` | node or mesh morph weights in rig-v1. | `JsonPath`, `ElementIndex` |
| `SkinnedGeometryMissing` | rig identity without fitted skinned mesh geometry. | No additional identifiers |
| `PrimitivesEmpty` | a fitted mesh without primitive geometry. | `MeshIndex`, `PrimitiveCount` |
| `PrimitiveShapeOrMorphUnsupported` | a non-object or morph-bearing fitted primitive. | `MeshIndex`, `PrimitiveIndex` |
| `CompressionUnsupported` | Draco-compressed fitted primitive geometry. | `PrimitiveIndex`, `ExtensionName` |
| `PrimitiveModeInvalid` | a fitted primitive mode outside the glTF mode domain. | `PrimitiveIndex`, `Mode` |
| `InfluenceAttributeMalformed` | a noncanonical influence-set attribute suffix. | `PrimitiveIndex`, `AttributeName` |
| `FirstInfluenceSetMissing` | fitted geometry missing influence set zero. | `PrimitiveIndex` |
| `WeightsAllZero` | a fitted vertex with no positive influence weight. | `PrimitiveIndex`, `VertexIndex` |
| `VertexIndexOutsidePositionCount` | an index outside fitted POSITION data. | `PrimitiveIndex`, `Index`, `VertexCount` |
| `JointIndexOrWeightInvalid` | an out-of-range joint or invalid weight in fitted geometry. | `VertexIndex`, `Lane`, `JointIndex`, `Weight`, `JointCount` |
| `VertexAccessorCountMismatch` | fitted attributes with differing vertex counts. | `ActualCount`, `PositionCount` |
| `FitValueNonFinite` | a non-finite fitted accessor value. | `JsonPath`, `VertexIndex`, `ComponentIndex` |
| `MatrixAndTrsBothPresent` | a rest transform authored as both matrix and TRS. | `NodeIndex` |
| `RestTransformLengthMismatch` | a rest transform with the wrong number of components. | `NodeIndex`, `ExpectedCount`, `ActualCount` |
| `RestTransformComponentInvalid` | a non-numeric or non-finite rest transform component. | `NodeIndex`, `ComponentIndex` |

## Body rig identity

Use `AttachmentGlb.ComputeRigIdentity(glbBytes, sourceName)` to identify what a garment fits against.
It returns `rig-v1:` followed by a lowercase SHA-256 digest. A headless publisher and a client can
call the same API without importing a renderer. Persist and compare the complete prefixed string.
A field that previously accepted only a bare file SHA-256 needs versioned-identity validation and
regenerated fit evidence. Keep separate file-integrity hashes for exact asset bytes.

The input is a GLB 2 with one embedded buffer, exactly one skin and at least one mesh node using
that skin. The reader covers ordered joint names, joint hierarchy, rest local transforms, inverse
bind matrices, and every skinned mesh primitive's positions, optional normals, joint indices,
weights and optional vertex indices. All paired `JOINTS_n` and `WEIGHTS_n` sets are included.
Joint and mesh-node ancestors contribute their rest frames, including non-joint ancestors.
This reader has no 128-joint renderer limit.

Animations, materials, images, textures, UVs, tangents, vertex colors, accessor bounds, extras,
unskinned meshes, unrelated nodes and unused bytes do not contribute. Image URIs are never read.
Adding, removing or retiming clips leaves the identity unchanged. Morph targets and weights,
compressed geometry and unsupported accessor encodings are refused rather than omitted.
Encoding refusal is scoped to covered primitives and accessors. Required extensions affecting only
ignored appearance or animation data do not change fit identity.
Bad containers, hierarchy, references, names, transforms and fit data use the structured malformed-data
refusals in the GLB tables above. Shared skin checks retain `AttachmentSkinnedGeometryRefusal`, including
joint names and inverse binds. Rig-specific checks use `RigIdentityRefusal`. Both retain `SourceName`.
Invalid source-name arguments throw
`ArgumentException`. The rigid and skinned attachment validation APIs keep their existing policies,
including their animation and external-image refusal.

### rig-v1 encoding

The SHA-256 input is the following binary stream. `I(n)` is a signed 32-bit little-endian integer.
`T(s)` is `I(UTF8 byte count)` followed by the UTF-8 bytes, without a terminator or Unicode
normalization. Counts and flags are nonnegative. Parent `-1` denotes a root.

Assign canonical node IDs by visiting each joint in skin order, then each skinned mesh node in
source node-array order. For each visit, assign unseen ancestors first, root to leaf, then the
node itself. Ignore all other nodes. Source node, mesh, accessor and buffer-view indices are not
written. Joint names are ordinal and must be unique, non-blank and well-formed UTF-16. Non-joint
node names do not contribute.

1. `T("KhaozEngine.Attachments/rig-v1")`.
2. Joint count, then each joint's `T(name)` and canonical node ID.
3. Canonical node count, then each node's canonical parent ID and rest frame. A frame begins with
   `I(0)` for TRS or `I(1)` for matrix. TRS writes translation's 3 numbers, rotation's 4 numbers and
   scale's 3 numbers. Matrix writes its 16 numbers in source glTF order. Each number is `T(canonical
   decimal)`. Matrix and TRS cannot coexist and remain distinct encodings.
4. The inverse-bind accessor described below, in joint order. An omitted accessor writes the same
   descriptor and IEEE-754 float bytes as explicit identity MAT4 values, one per joint.
5. Skinned mesh-node count, then each mesh node's canonical ID and primitive count. Mesh instances
   follow source node-array order and primitives follow source primitive-array order.
6. Each primitive writes its mode (`I(4)` by default), POSITION accessor, normal-present flag and
   optional NORMAL accessor, influence-set count, then each set's numeric suffix and JOINTS/WEIGHTS
   accessors in that order. Sets are sorted by numeric suffix and set 0 is required. Finally write
   the indices-present flag and optional indices accessor. Flags are `I(0)` or `I(1)`.

An accessor writes `T(type)`, component type, element count and normalized flag, followed by its
effective raw element bytes in element order. Supported shapes are float VEC3 positions/normals,
float MAT4 inverse binds, unsigned byte/short VEC4 joints, float or normalized unsigned byte/short
VEC4 weights, and unsigned byte/short/int SCALAR indices. All counts, joint indices, weights and
vertex indices are validated. All-zero total weights across the sets are refused.

Sparse overrides select raw elements before hashing. An absent base supplies zero bytes. Stride
padding, accessor offsets and sparse storage layout do not contribute. Binary floats are never
reserialized, so their authored bits, including signed zero, remain significant. Equivalent dense,
strided and sparse layouts agree when their effective element bytes and descriptors agree.

JSON transform numbers are encoded from decimal tokens using integer and string operations. A
nonzero number becomes its sign, coefficient digits without leading or trailing zeroes, `e`, and
the decimal exponent without a plus sign or leading zeroes. Zero becomes `0`, including signed
zero. For example, `1.25`, `125e-2` and `0.0125e2` all become `125e-2`. Omitted translation, rotation
and scale use glTF's `(0,0,0)`, `(0,0,0,1)` and `(1,1,1)` defaults. Parsing a number to check that
it is finite does not supply its hash encoding. No transform decomposition or geometric-equivalence
normalization occurs. Changing this stream requires a new identity prefix.

Version 21 removes the rigid-only set type names without compatibility aliases. See the
[migration record](../docs/design/SKINNED-ATTACHMENT-PARTS-DESIGN-2026-10-15.md).
