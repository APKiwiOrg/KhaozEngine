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
