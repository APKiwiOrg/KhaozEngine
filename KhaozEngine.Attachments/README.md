# KhaozEngine.Attachments

GPU-free, BCL-only definitions for rigid mesh parts attached to named joints. Reference this package
explicitly from headless publishers or consumers. It is not included in Foundation or Server.

`RigidAttachmentSetDefinition` copies parts, orders them by positive stable ID and exposes read-only
parts and distinct mesh references. Repeated mesh keys must declare the same lowercase SHA-256.
`ValidateReferences` requires every joint and exact mesh hash to resolve.
Joint names and mesh keys require well-formed UTF-16 and retain their ordinal identity without
Unicode normalization. Supplementary characters and literal U+FFFD remain valid.

`RigidAttachmentSetJson.Parse(utf8, sourceName)` accepts format 1 with these fields:

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

The example hash is a placeholder. Hash the exact self-contained GLB bytes. `local` contains 16
row-major finite numbers and must be affine and invertible. Unknown or duplicate members, missing
fields, unsupported versions and invalid parts throw `InvalidDataException` with source context.
Invalid constructor arguments throw `ArgumentException`.

`Write(definition)` emits canonical UTF-8 with a fixed property order. Its SHA-256 is
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
