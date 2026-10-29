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
Bad containers, hierarchy, names, references, accessor ranges and nonfinite fit data throw
`InvalidDataException` with `sourceName` context. Invalid source-name arguments throw
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
