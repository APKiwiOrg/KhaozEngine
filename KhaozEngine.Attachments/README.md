# KhaozEngine.Attachments

GPU-free, BCL-only definitions for rigid and skinned mesh parts. Rigid parts attach to named joints,
while skinned parts declare garment meshes that use the host body's pose. Reference this package
explicitly from headless publishers or consumers. It is not included in Foundation or Server.

`AttachmentSetDefinition` copies parts, orders them by positive stable ID and exposes read-only
parts and distinct mesh references. Repeated mesh keys must declare the same lowercase SHA-256.
Parts derive from `AttachmentPartDefinition` as `RigidAttachmentPartDefinition` or
`SkinnedAttachmentPartDefinition`. `ValidateReferences` requires every mesh hash and every explicit
rigid joint to resolve. Unknown programmatic part kinds are refused.
Joint names and mesh keys require well-formed UTF-16 and retain their ordinal identity without
Unicode normalization. Supplementary characters and literal U+FFFD remain valid.

`AttachmentSetJson.Parse(utf8, sourceName)` accepts formats 1 and 2. Format 1 contains only rigid parts
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

The example hash is a placeholder. Hash the exact self-contained GLB bytes. `local` contains 16
row-major finite numbers and must be affine and invertible. Unknown or duplicate members, missing
fields, unsupported versions and invalid parts throw `InvalidDataException` with source context.
Invalid constructor arguments throw `ArgumentException`.

`Write(definition)` emits format 1 whenever all parts are rigid, even after parsing format 2.
Existing canonical format-1 bytes and semantic hashes stay unchanged. A set containing skinned parts
writes format 2. Property order is `id, mesh, joint, local` in format 1 and `id, kind, mesh` followed
by rigid `joint, local` in format 2. Signed zero is normalized without changing other float values.
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

Version 21 removes the rigid-only set type names without compatibility aliases. See the
[migration record](../docs/design/SKINNED-ATTACHMENT-PARTS-DESIGN-2026-10-15.md).

## Optional equipment coverage companion

`EquipmentCoverageDefinition` and `EquipmentCoverageJson` remain GPU-free. They describe explicitly
requested host conformation beside an unchanged attachment JSON v1/v2 definition. `Read(utf8, identity)`
accepts strict format 1. Unknown/duplicate fields, nonfinite values, invalid ranges or matrices and
unsupported versions throw `InvalidDataException` with asset context. `Write` fixes member order, sorts
positive command IDs and source ranges and normalizes negative zero. `SemanticSha256` uses a distinct
coverage hash domain and differs from the fetched file byte hash.

The root contains `formatVersion`, exact-byte `attachmentSha256`, canonical `hostMapSha256` and `commands`.
Each `EquipmentCoverageCommand` names a positive `id`, rigid `partId`, ordinal `hostRegion`, 16-number
row-major `coverageLocal`, `inwardSupport` source ranges (`mesh`, `primitive`, `firstTriangle`, `count`),
`collarWidth`, `minZ`, `maxZ`, `clearance` and `maxWorldRecession`. Coverage positive Z points outward.
Collar and caps use coverage coordinates. Clearance and recession are world-space distances.
The companion supports at most four commands and 1,024 support triangles per command. A renderer resolves
the selected actual inward sheet, host regions, transforms and source identity. This leaf does not infer
anatomy or validate the consumer's motion domain.

After a GLB change, re-export and review the source ranges and host map, refresh declared mesh hashes,
then regenerate companions against the exact attachment bytes and host-map semantic digest. Matching
part or region names do not make old coverage compatible. Absent coverage retains existing attachment
bytes and behavior. Requested missing or malformed coverage must fail explicitly.
See the [authoring and consumer contract](../docs/EQUIPMENT-COVERAGE.md) and the
[compiled complete example](../docs/examples/EquipmentCoverageConsumer.cs).
