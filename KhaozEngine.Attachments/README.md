# KhaozEngine.Attachments

GPU-free attachment definitions, outfit resolution and chain simulation. Rigid parts attach to named joints.
Skinned parts follow the host body's pose and can lift over lower layers or carry swinging chains. Reference
this package explicitly from headless publishers or consumers. Its only engine dependency is the dependency-free
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
any listing order share their canonical bytes and `SemanticSha256`. Equality of skinned parts compares region runs
and authored motion and lift by value. Region names need well-formed UTF-16 and keep their ordinal
identity. The [outfit resolver](#outfit-coverage) checks those names against the body's
[region map](#body-region-maps).

## Attachment set JSON

`AttachmentSetJson.Parse(utf8, sourceName)` accepts formats 1 through 6. Format 1 contains only rigid parts
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

### Format 4

Format 4 adds optional root `colliders` and optional skinned part `motion` to the layered format. Formats 1 to 3
refuse either member as unknown. Rigid parts never accept `motion`.

```json
{
  "formatVersion": 4,
  "layer": "outerwear",
  "covers": [],
  "colliders": [
    { "joint": "upper", "start": [0, 1.05, 0], "end": [0, 1.4, 0], "radius": 0.14 }
  ],
  "parts": [
    {
      "id": 1,
      "kind": "skinned",
      "mesh": {
        "key": "parts/garment.glb",
        "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
      },
      "regions": [["a", 4]],
      "motion": {
        "kind": "chains",
        "chains": [
          {
            "joints": ["left0", "left1"], "tip": [0, -0.25, 0],
            "stiffness": 6, "damping": 3, "gravity": 1, "trail": 0.7, "angleLimit": 1.4, "radius": 0.02
          },
          {
            "joints": ["right0"], "tip": [0, -0.25, 0],
            "stiffness": 6, "damping": 3, "gravity": 1, "trail": 0.7, "angleLimit": 1.4, "radius": 0.02
          }
        ],
        "links": [[0, 1]]
      }
    }
  ]
}
```

Set `SkinnedAttachmentPartDefinition.Motion` to a `ChainMotionDefinition` to declare chains, or leave it null for a
part that only follows the body. A part declares 1 to 32 chains and each chain orders 1 to 16 garment joint names
from its anchor toward its tip. Names are nonblank, well-formed UTF-16 and ordinally unique across the part's
chains. The tip is a finite, nonzero vector in the last joint's rest frame. Every chain includes all six settings.
Stiffness, damping and radius are finite and nonnegative. Gravity is 0 to 10 and trail is 0 to 1, both inclusive.
The angle limit is greater than zero and at most pi radians. An optional link pairs distinct zero-based chain
indices. A part can list up to 64 links and each pair can appear only once in either direction.

Use `new AttachmentSetDefinition(parts, layer, covers, colliders)` to add up to `MaxColliders` (8) capsules while
the set is worn. Colliders use the same joint, points and radius as the [body collider file](#body-colliders).
The set owns copies of its motion and colliders. A layer is required when either is present.

`AttachmentSetJson.MotionFormatVersion` is 4. A 24.4.x reader checks root members before it checks the version.
It refuses a format 4 set with a root `colliders` member, even an empty array, as `JsonPropertyUnknown` for
`colliders`, and any other format 4 set as `FormatVersionUnsupported`. Earlier readers refuse the first unfamiliar root member, which is `layer` in canonical
bytes. Move every reader to 24.5.0 or later before publishing format 4.

### Format 5

Format 5 adds optional root `standoff` and optional skinned part `lift` to format 4. A set with either needs a
layer. Formats 1 to 4 refuse these members as unknown, and rigid parts refuse `lift` in every format.

- `"standoff":[["belly",0.008],["chest",0.006]]` declares each region's outer surface height above the bare skin
  at rest, in metres. A set can declare at most 128 unique, nonblank region names with well-formed UTF-16. Each
  height must be finite and between 0 and 0.25 m inclusive. A region can have standoff without being in `covers`.
- `"lift":{"clearance":0.001}` declares the gap a skinned part keeps above lower layers, in metres. The
  clearance member is required and must be finite and between 0 and 0.05 m inclusive. A skinned part can
  declare both `motion` and `lift`.

Use `new AttachmentSetDefinition(parts, layer, covers, colliders, standoff)` with `RegionStandoff` entries to
declare standoff. The set owns the entries and exposes them in ordinal region name order. Set the skinned part's
`Lift` to a `PartLiftDefinition(ClearanceMetres)` to declare lift. `AttachmentSetDefinition.MaxStandoffEntries`
is 128. `AttachmentSetJson.LiftFormatVersion` is 5. Publish format 5 after every reader has moved to 24.7.0
or later. A 24.5.x or 24.6.x reader raises `JsonPropertyUnknown` for root `standoff` when present, even as an
empty array, and `FormatVersionUnsupported` for version 5 otherwise.

### Format 6

Format 6 adds optional `linkStretch` beside `links` in a skinned part's chain motion. It declares how far
the spacing of each linked chain pair can depart from rest as a fraction of rest spacing, in either direction.
The value must be finite, greater than zero and at most one, and the part must declare at least one link.
Omit it for an unbounded link. Formats 4 and 5 refuse the member as unknown.

Set `ChainMotionDefinition.LinkStretch` to declare the bound in code. Set definitions take only chain
indices in each `ChainLink`. The wearer binding copies the part's bound to `ChainLink.Stretch` on the rig
links. When constructing a `ChainRig` directly, set `Stretch` on each link that needs a bound.
`AttachmentSetJson.LinkStretchFormatVersion` is 6.

### Canonical set bytes

The example hash is a placeholder. Hash the exact self-contained GLB bytes. `local` contains 16
row-major finite numbers and must be affine and invertible. Unknown or duplicate members, missing
fields, unsupported versions and invalid parts throw `AttachmentSetDataException` with `SourceName`.
Invalid constructor definitions throw `AttachmentSetArgumentException`. Both carry `AttachmentSetRefusal`
and the identifiers listed in [Set refusals](#set-refusals). Root members are checked against the union of
every format's root members before the version is read, and against the declared format's own list after. A
document with a `layer` and no `formatVersion` is therefore refused as `JsonPropertyMissing` for the version,
not as `JsonPropertyUnknown` for the layer, because the version decides what every other member means.

`Write(definition)` emits the lowest format that expresses the definition. `linkStretch` requires format 6,
standoff or lift requires at least format 5, and motion or colliders require at least format 4. A layered set
without those additions writes format 3, even when all its parts are rigid. A set without a layer writes format 1
if all its parts are rigid, even after parsing format 2, and format 2 if it contains skinned parts. Existing
canonical format 1 to 5 bytes and semantic hashes stay unchanged.

Property order is `id, mesh, joint, local` in format 1 and `id, kind, mesh` followed by rigid `joint, local` in
format 2. Format 3 writes `formatVersion, layer, covers, parts` at the root and appends `region` to a rigid part
and `regions` to a skinned part. Format 4 writes nonempty `colliders` between `covers` and `parts`, in ordinal
joint, start, end and radius order. A skinned part writes `motion` after `regions`. Motion writes `kind, chains`
and then `links` when nonempty. Each chain writes `joints, tip, stiffness, damping, gravity, trail, angleLimit,
radius`. Chain and joint order are preserved. Links put the lower index first and sort by the two indices.
Format 5 writes nonempty `standoff` after `covers` and any `colliders`, with entries in ordinal region name order.
A skinned part writes `lift` after `regions` and any `motion`. Format 6 writes `linkStretch` after `links`.

Signed zero is normalized without changing other float values. The writer emits canonical UTF-8 with no trailing
newline. Its SHA-256 is `SemanticSha256`. Source whitespace, part, collider and standoff listing order, and link
listing order and direction do not change that revision.

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
`WriteTriangleRegions(part, regionPerTriangle)` fills one region index per triangle and needs a span of exactly that
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

## Body colliders

`BodyCollidersJson.Parse(utf8, sourceName)` reads format 1:

```json
{"formatVersion":1,"rig":"rig-v1:<sha256>","colliders":[{"joint":"upper","start":[0,1,0],"end":[0,2,0],"radius":0.125}]}
```

`rig` is the body's complete [rig identity](#body-rig-identity). A `ColliderDefinition` names a host joint and
describes a capsule in the body's rest model space, in metres. Equal endpoints describe a sphere. Each point
has three finite coordinates, the radius is finite and positive, and the joint name uses well-formed UTF-16.
`new BodyColliderSet(rigIdentity, colliders)` applies the same content rules and copies zero to 32 colliders.
The [renderer binding](../KhaozEngine.Attachments.Render3D/README.md#hanging-chains-and-wearer-motion)
keeps the authored radius in the bind pose and applies the largest axis scale of the composed rest-to-world
transform in a posed frame.

`Colliders` is read-only and orders the capsules by ordinal joint name, then the start X, Y and Z, the end X, Y
and Z, and the radius. `BodyCollidersJson.Write(set)` emits UTF-8 with the member order shown above, without
whitespace or a trailing newline. It writes signed zero as `0`. `Sha256` is the lowercase SHA-256 of those bytes.
Changing the order of the input list keeps the same bytes and hash.

Refusals throw `BodyCollidersDataException`, derived from `MalformedDataException`, with a `BodyCollidersRefusal
Reason` and `SourceName` from `Parse`. A collider index refers to the input order. An escaped lone surrogate in a
joint name has an index and path, with no decoded `JointName`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `FormatVersionUnsupported` | a format other than 1. | `JsonPath`, `FormatVersion` when Int32 |
| `JsonPropertyUnknown`, `JsonPropertyDuplicate`, `JsonPropertyMissing` | an unknown, repeated or absent member. | `JsonPath`, `PropertyName`, `ColliderIndex` within a collider |
| `JsonValueShapeInvalid` | a value of the wrong JSON shape, such as a point with the wrong number of coordinates. | `JsonPath`, `ColliderIndex` within a collider |
| `JsonMalformed` | malformed JSON or invalid UTF-8. | No additional identifiers |
| `RigIdentityInvalid` | a rig that is not `rig-v1:` and a lowercase SHA-256. | `JsonPath`, `RigIdentity` |
| `JointNameEmpty`, `JointNameMalformedUtf16` | a blank or malformed joint name. | `JsonPath`, `ColliderIndex`, `JointName` when available |
| `PointInvalid` | a nonfinite coordinate. | `JsonPath`, `ColliderIndex`, `JointName` |
| `RadiusInvalid` | a nonfinite or nonpositive radius. | `JsonPath`, `ColliderIndex`, `JointName` |
| `TooManyColliders` | more than 32 colliders. | `JsonPath`, `ColliderCount` |

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

The resolver checks covered regions, rigid part regions, skinned run regions and standoff regions against the
body's region map. A name the body does not declare throws `OutfitDataException`, derived from
`MalformedDataException`, with an `OutfitRefusal Reason`.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `RegionUnknown` | a covered region, rigid part region, skinned run region or standoff region the body does not declare. | `WornIndex`, `PartId` (null for covered and standoff regions), `RunIndex` (skinned runs only), `RegionName` |

## Outfit lift

`OutfitLift.Resolve(coverage, meshes)` uses the body regions and worn set snapshot captured by
`OutfitCoverage.Resolve`. Supply one `LiftingPartMesh(WornIndex, PartId, Materials)` for each skinned part
with `Lift`. Supply one `LiftMaterialMesh(Positions, Indices, Directions, Bearings)` per material group in
upload order. Indices are local to that group. Directions and bearings line up with its rest positions.
A missing, extra, duplicate or wrong-shaped mesh is refused with `ArgumentException`.

`AttachmentGlb.LiftBasis(glb, sourceName)` validates a skinned GLB and reads its `_LIFT_DIRECTION` and
`_LIFT_BEARING` attributes. Every primitive supplies an unnormalized FLOAT VEC3 direction and FLOAT SCALAR
bearing for each position. The direction is the skin's outward normal in body rest space and must be finite
and within 0.001 of unit length. The bearing is a finite, nonnegative distance in metres to the part's
underside. At bitwise identical positions, treating both signed zeros as zero, the direction components and
bearing must agree within 0.0001, including across primitive and material splits. `LiftBasis.Vertices` is an
immutable list in flattened mesh, primitive and vertex order. Each `LiftBasisVertex` carries `MaterialPart`
and `IndexInPart` in upload order, with materials grouped by first use.

`HeightBelow(wornIndex, region)` is the highest effective standoff among visible sets on strictly lower
layers, or null when there is no contributor. `EffectiveStandoff(wornIndex, region)` is the visible set's
declared standoff plus its greatest lift in that region, or null when it does not contribute. Region indices
refer to the body map. Each vertex uses the highest height below of all regions that touch its position
and moves along its normalized direction.
`LiftedPositions(wornIndex, partId)` returns rest positions in upload order by material, or null when the
part has no moved vertex. `HasLift` is true when any part moved. Resolve at bind or run the same calculation
offline for fitted poses. See the [lift rule](../docs/design/ATTACHMENT-LIFT-DESIGN.md#the-rule).

## Coverage claim check

A piece's `Covers` hides the skin in those regions, so a false claim opens a hole in the body.
`CoverageClaimCheck.Verify(bodyMeshes, body, piece, pieceMeshes, maxDistanceMetres)` checks the claim offline: at
the rest pose the fixed part of the piece must lie over every triangle of every region it claims. A tool runs it when
a piece is made. It casts one ray per claimed body triangle against the fixed piece triangles, which suits low poly
meshes, so it is never meant to run per frame and builds no acceleration structure.

Geometry arrives as `CoverageClaimMesh(Positions, Indices)`, plain `ReadOnlyMemory<Vector3>` positions and a
`ReadOnlyMemory<uint>` triangle list, so publishers and tools stay GPU free.

- `bodyMeshes` follow the region map's part order, one mesh per part with the part's triangle count, because the map
  tags triangles by part and position and a mesh that disagrees would be checked against the wrong regions.
- `pieceMeshes` are already in the body's rest frame. A rigid part is placed by its joint's rest transform and its
  local transform before the call, since the check has no rig to place it with. For a piece with motion, give one
  mesh per definition part in any order, combining any material groups within that part. Set each mesh's init-only
  `PartId` to the corresponding definition part ID. Zero leaves the ID unstated. The check ignores `PartId` on
  body meshes and on pieces without motion.
- Each part with motion needs a nonempty `Swinging` mask with one `bool` per position. An empty mask on a part
  without motion means all its vertices are fixed. The check skips a piece triangle if any of its vertices swings.
  The hidden body regions are fixed when the outfit binds, so a moving piece triangle could swing away and leave a hole.
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
- A piece with no layer, as formats 1 and 2 read, claims nothing. It is honest and its report has no region entries.

For a skinned part, pass the joint names from its `ChainMotionDefinition.Chains` to
`AttachmentGlb.SwingingVertices(glb, sourceName, chainJoints)` and use the returned flags as the mask. The helper
validates the bytes with the skinned embedded GLB policy. It returns flags in flattened skinned import order (mesh,
primitive, vertex). A vertex swings if it has a positive weight on any named joint. Joint names use ordinal matching.
Names absent from the skin match no vertices.

The `CoverageClaimReport` lists each claimed region in the body map's region order as a `CoverageClaimRegion` with
its `Name`, `TriangleCount`, `DegenerateCount`, `UncoveredCount` and the `Uncovered` triangles as
`CoverageClaimTriangle(PartIndex, TriangleIndex)`, in part then triangle order, so a tool can show them. `Honest` is
true when no claimed triangle is uncovered. `IgnoredSwingingTriangleCount` counts the piece triangles skipped across
all piece meshes, even outside the claimed regions. The ray test runs in double precision, so float geometry neither
overflows nor underflows and only a triangle of truly zero area counts as degenerate.

A null argument throws `ArgumentNullException`. An invalid maximum distance throws `ArgumentOutOfRangeException`.
A nonempty `Swinging` mask with a length different from its mesh's position count throws `ArgumentException`.
The refusals below throw `CoverageClaimDataException`, derived from `MalformedDataException`, with a
`CoverageClaimRefusal Reason`. `MeshKind` says whether a refused or missing mesh is a `Body` or `Piece` mesh.
`MeshIndex` is the mesh's zero-based position when it is present, which for a body mesh is its map part.

| Reason | What it refuses | Other identifiers |
|---|---|---|
| `BodyMeshCountMismatch` | a body mesh count other than the map's part count. | `ExpectedMeshCount`, `ActualMeshCount` |
| `BodyTriangleCountMismatch` | a body mesh whose triangle count differs from its map part's. | `MeshKind`, `MeshIndex`, `ExpectedTriangleCount`, `ActualTriangleCount` |
| `RegionUnknown` | a covered region, or a standoff region in the standoff check, that the body's map does not declare. | `RegionName` |
| `IndexCountNotTriangles` | a mesh whose index count is not a multiple of three. | `MeshKind`, `MeshIndex`, `IndexCount` |
| `IndexOutOfRange` | an index past the mesh's positions. | `MeshKind`, `MeshIndex`, `IndexPosition`, `VertexIndex`, `VertexCount` |
| `PositionNotFinite` | a position with a NaN or infinite component. | `MeshKind`, `MeshIndex`, `VertexIndex` |
| `SwingMaskMissing` | a piece mesh without the mask required by its part's motion. | `MeshKind`, `MeshIndex`, `PartId`, `MeshKey` |
| `PieceMeshPartIdMissing` | a piece mesh with `PartId` zero when the definition has motion. | `MeshKind`, `MeshIndex`, `PartId` (zero) |
| `PieceMeshPartIdUnknown` | a piece mesh naming a part ID absent from a definition with motion. | `MeshKind`, `MeshIndex`, `PartId` |
| `PieceMeshPartIdDuplicate` | a second piece mesh naming the same part ID in a definition with motion. | `MeshKind`, `MeshIndex` (the second mesh), `PartId`, `MeshKey` |
| `PiecePartMeshMissing` | a part with no mesh in a piece with motion. | `MeshKind`, `PartId`, `MeshKey` |

The check has its own refusal family rather than `OutfitDataException`, because it resolves no worn list, so the
outfit refusal's `WornIndex` would have nothing to name.

## Standoff claim check

`StandoffClaimCheck.Verify(bodyMeshes, body, piece, pieceMeshes)` checks declared standoffs offline in the
body's rest frame. Use the [coverage claim check](#coverage-claim-check) geometry, part IDs and per-vertex
swing masks. It measures each fixed vertex against the nearest nondegenerate body triangle, using the signed
distance along the triangle's outward normal. Triangles within 1 micrometre of the nearest distance compete by
height. Equal heights choose the first triangle in body part and triangle order. A body without nondegenerate
triangles yields no measurements.

`StandoffClaimReport.Regions` lists measured regions in body map order as `StandoffClaimRegion` values with
`Name`, `HighestHeightMetres`, `VertexCount` and nullable `DeclaredStandoffMetres`. `UnderstatedRegions` lists
measurements above their declaration. `UndeclaredRegions` lists positive heights where the set declares no
standoff. The check validates covered and standoff region names and uses the same `CoverageClaimDataException`
refusals for invalid region names or mesh data as the coverage check.

## Chain simulation

Build a `ChainRig` with an anchor count, ordered chains and optional `ChainLink` pairs. Each chain names
its anchor index and supplies one to sixteen `ChainJointRest` transforms in parent-to-child order. The
first joint stays on its anchor and can turn. The tip is a point in the last joint's local frame, so
even a one-joint chain can swing. A link keeps corresponding movable joints and both virtual tips near
their rest spacing. The link springs have a natural frequency of 10 radians per second. All links
read the same substep poses. When several links touch a joint or tip, it receives their average
correction.
The rig owns its rest data and can be shared. Create a `ChainSimulation` for each wearer with a fixed
collider count.

Pose the wearer first, then call `Step` with the world-space anchors, capsule endpoints and radii,
the wearer matrix and elapsed seconds. Equal capsule endpoints describe a sphere. Anchor matrices must
be finite, invertible affine transforms. The wearer matrix must be finite and affine. Distances are in
metres, Y is up, and local transforms compose as scale, rotation, translation in that order.

The solver uses damped Verlet particles with implicit midpoint springs. Stiffness is the nonnegative
natural frequency in radians per second for a segment returning to its rest direction. Damping is a
nonnegative velocity decay rate per second.
Gravity scales world gravity from 0 to 10. Half of the gravity displacement goes into the position
prediction and half into the stored previous position, so the next substep carries the full velocity
change even when its duration differs. Trail ranges from 0, carrying the chain with the wearer's
rigid movement, to 1, giving the chain full inertia. The bend limit is greater than zero and at most
pi radians. The nonnegative chain radius adds clearance against capsules. `Tune` applies settings
starting at the next substep.

Substeps interpolate the input poses and end on the current frame, so chains follow their anchors
through fast movement. A hitch may drop integration time to stay within the step budget while still
reaching the current pose. `MaxStepSeconds` is greater than zero and at most 1/30 s.
`MaxStepsPerFrame` is from 1 through 64. Segment lengths use the posed rest scale. Capsule correction keeps the
root and segment lengths fixed and can exceed the bend limit to clear a collider. The first step,
`Reset()`, a teleport or a long gap places the chain at rest with zero velocity. `Held` blends from the
last shape to rest without stepping physics. On resume, physics starts from the held shape with zero
velocity. The displayed joint swings blend from that shape toward the live solution over
`HoldBlendSeconds`. Each frame continues from the last displayed swing, so an anchor turning through
a half turn keeps a continuous blend. During the blend, display can still intersect a collider the
live solution clears.
Use `MotionLevels.Assign` to select the nearest wearers within a range and budget, with ties in input order.

Read `JointsFromAnchor` in rig joint order or call `ComposeJointWorlds` with the latest anchors and an
output span. The solver stores its results relative to each anchor so callers can draw on a new body
pose without stepping again. The results start at the rest hang. Stepping and composing use buffers
allocated at construction.

| `ChainSimulationOptions` property | Default |
| --- | --- |
| `MaxStepSeconds` | 1/120 s |
| `MaxStepsPerFrame` | 8 |
| `TeleportDistance` | 2 m |
| `LongestGapSeconds` | 0.5 s |
| `HoldBlendSeconds` | 0.25 s |
| `Gravity` | (0, -9.81, 0) m/s² |

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
| `JsonValueShapeInvalid` | a JSON value shape rejected by a framework accessor, or a `covers` entry, region run or format 4 to 6 member of the wrong shape. | `InnerException` or `JsonPath` with the applicable indices |
| `LayerMissing` | a region-tagged format without a layer, or a constructed set with region tags or covered regions and no layer. | `JsonPath` and `PropertyName` from JSON, `PartId` for region tags, or `ParamName` for covered regions |
| `LayerUnknown` | a layer that names no defined layer. | `JsonPath` and `LayerName`, or `Layer` |
| `CoveredRegionDuplicate` | a covered region listed more than once. | `CoverIndex`, `RegionName` |
| `PartRegionMissing` | a part without its `region` or `regions` in a set with a layer. | `PartId`, and `JsonPath` and `PropertyName` from JSON |
| `PartRegionRunEmpty` | a skinned part's run of fewer than one triangle. | `PartId`, `RunIndex`, `RegionName` |
| `RegionNameEmpty`, `RegionNameMalformedUtf16` | a blank or malformed covered or part region name. | `RegionName`, with `CoverIndex` or `PartId` and `RunIndex` |
| `RegionTriangleCountOverflow` | a skinned part whose runs add up past Int32. | `PartId`, `RunIndex`, `RegionName`, `ActualTriangleCount` |
| `MotionKindUnsupported` | a motion kind the reader cannot represent. | `PartId`, `PartIndex`, and `Kind` from JSON or `ActualType` from the constructor |
| `ChainsEmpty`, `TooManyChains` | a motion with fewer than 1 or more than 32 chains. | `PartId`, `PartIndex`, `ActualCount`, `ExpectedCount` |
| `ChainJointsEmpty`, `TooManyChainJoints` | a chain with fewer than 1 or more than 16 joints. | `PartId`, `PartIndex`, `ChainIndex`, `ActualCount`, `ExpectedCount` |
| `ChainJointNameEmpty`, `ChainJointNameMalformedUtf16`, `ChainJointDuplicate` | a blank, malformed or repeated joint name in a part's chains. | `PartId`, `PartIndex`, `ChainIndex`, `JointIndex`, `JointName` when available |
| `ChainTipInvalid` | a tip without three finite components or with all three zero. | `PartId`, `PartIndex`, `ChainIndex` |
| `ChainSettingOutOfRange` | a chain setting outside its range. | `PartId`, `PartIndex`, `ChainIndex`, `SettingName` |
| `ChainLinkInvalid`, `ChainLinkDuplicate` | a link that fails to pair distinct existing chains or repeats an existing pair. | `PartId`, `PartIndex`, `LinkIndex` |
| `LinkStretchOutOfRange`, `LinkStretchWithoutLinks` | a part's bound is outside its finite range or the part has no links. | `PartId`, `PartIndex`, `SettingName` |
| `ChainLinkStretchInDefinition` | a constructor link carries its own rig bound. | `PartId`, `PartIndex`, `LinkIndex` |
| `TooManyLinks` | a part with more than 64 links. | `PartId`, `PartIndex`, `ActualCount`, `ExpectedCount` |
| `ColliderJointNameEmpty`, `ColliderJointNameMalformedUtf16` | a collider with a blank or malformed joint name. | `ColliderIndex`, `JointName` when available |
| `ColliderPointInvalid`, `ColliderRadiusInvalid` | a collider with non-finite endpoint components or a radius that is non-finite or nonpositive. | `ColliderIndex`, `JointName` |
| `TooManyColliders` | a set with more than 8 colliders. | `ActualCount`, `ExpectedCount` |
| `MotionWithoutLayer` | motion or colliders on a set with no layer. | `PartId` and `PartIndex` for motion, plus `PropertyName` from JSON |
| `StandoffWithoutLayer` | standoff on a set with no layer. | `PropertyName` from JSON or `ParamName` from the constructor |
| `LiftWithoutLayer` | lift on a set with no layer. | `PartId`, `PartIndex`, plus `PropertyName` from JSON |
| `StandoffRegionNameEmpty`, `StandoffRegionNameMalformedUtf16`, `StandoffRegionDuplicate` | a blank, malformed or repeated standoff region name. | `StandoffIndex`, `RegionName` when available |
| `StandoffValueOutOfRange` | a standoff outside its finite range. | `StandoffIndex`, `RegionName` |
| `TooManyStandoffEntries` | a set with more than 128 standoff entries. | `ActualCount`, `ExpectedCount` |
| `LiftClearanceOutOfRange` | a lift clearance outside its finite range. | `PartId`, `PartIndex` |

JSON refusals above for formats 4 through 6 include `JsonPath`. Chain, joint, link, collider and standoff indices are
zero-based and refer to input order. Counts report the supplied size and the required minimum or allowed maximum.
For a collider or standoff sequence without a known count, construction stops at the first item past the cap,
so `ActualCount` is the number read, one more than the cap, and a lower bound on the sequence's length.

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
| `LiftAttributeMissing` | a lifting primitive missing a lift attribute. | `JsonPath`, `AttributeName` |
| `LiftAttributeShapeMismatch` | a lift accessor with the wrong shape, encoding, normalization or count. | `JsonPath`, `AttributeName` |
| `LiftDirectionInvalid` | a non-finite lift direction or a length outside unit tolerance. | `JsonPath`, `AttributeName`, `VertexIndex` in the primitive |
| `LiftBearingInvalid` | a non-finite or negative lift bearing. | `JsonPath`, `AttributeName`, `VertexIndex` in the primitive |
| `CoincidentLiftDataMismatch` | equal positions with different lift data outside tolerance. | `JsonPath`, `AttributeName`, later `VertexIndex` and earlier `OtherVertexIndex` in flattened order |

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
