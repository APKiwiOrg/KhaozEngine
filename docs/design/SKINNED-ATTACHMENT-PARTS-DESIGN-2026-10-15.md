# Skinned attachment parts

Status: integrated into main for 21.0.0 through PR #1414, after exact-head draft PR checks, the
unfiltered hosted GPU proof and whole-branch review. The 21.0.0 release also carries the work
previously staged as 20.41.0. Consumers adopt it from the released tag.

The consumer requirement is skinned stone armour straps. The engine contracts and acceptance
cases are recorded below. This record supersedes the
[rigid attachment design](RIGID-ATTACHMENT-SETS-DESIGN-2026-10-13.md) for current set APIs without
changing its historical audit or original plan. Current use belongs in the
[consumer guide](../USING-KHAOZENGINE.md#attachment-sets) and
[renderer package reference](../../KhaozEngine.Attachments.Render3D/README.md).

## Why garments belong in an attachment set

A rigid part follows one joint. A garment crosses body regions whose vertices follow several
joints, so a single rigid transform cannot keep it seated through the host's motion. One set
therefore needs both rigid pieces and skinned garments, sharing the host pose and presentation.
The engine owns that general capability. Game art, fit, inventory and content upgrades remain
with the consumer. The approved consumer keeps stone rigid and makes leather skinned.

## Breaking names and compatible asset bytes

Set-level names are kind-neutral. The old names are removed without compatibility aliases:

| Removed name | Current name |
| --- | --- |
| `RigidAttachmentSetDefinition` | `AttachmentSetDefinition` |
| `RigidAttachmentSetJson` | `AttachmentSetJson` |
| `RigidAttachmentBinding` | `AttachmentSetBinding` |
| `RigidAttachmentSetCache` | `AttachmentSetCache` |
| `LoadedRigidAttachmentSet` | `LoadedAttachmentSet` |
| `BoundRigidAttachmentSet` | `BoundAttachmentSet` |
| `IRigidAttachmentScene` | `IAttachmentScene` |

### Migrating from 20.x

Beyond the renames above:

- `IAttachmentScene` implementers, test fakes included, add `UploadSkinnedAttachment`,
  `DrawSkinned`, `DrawMeshOutlineDissolved` and `DrawSkinnedOutlineDissolved`.
- `AttachmentSetDefinition.Parts` holds abstract `AttachmentPartDefinition` values. Code that
  reads `Joint` or `Local` pattern-matches `RigidAttachmentPartDefinition` first.
- `AttachmentSetJson.CurrentFormatVersion` changed from 1 to 2. Code that meant format 1 uses
  `AttachmentSetJson.RigidOnlyFormatVersion`.
- `GltfLoader.LoadPartsWithMaterials(ReadOnlyMemory<byte>, string)`,
  `GltfLoader.LoadSkinnedPartsWithMaterials(ReadOnlyMemory<byte>, string)` and
  `GltfLoader.LoadSkinned(ReadOnlyMemory<byte>, string)` throw `InvalidDataException` for a malformed glTF.
  The message names the identity and the importer's diagnostic is the inner exception. Catch that instead of a
  SharpGLTF type.

`AttachmentPartDefinition` has two concrete kinds, `RigidAttachmentPartDefinition` and
`SkinnedAttachmentPartDefinition`. The rigid kind keeps its existing joint and local correction.
The skinned kind carries only its stable ID and mesh reference. Its skin declares joints and binds.

Format 1 still parses as rigid. Format 2 requires `kind` on every part with exactly `rigid` or
`skinned`. Unknown and duplicate members are refused. Canonical writing chooses format 1 whenever
all parts are rigid, even after reading format 2. Existing canonical rigid bytes and semantic
SHA-256 revisions therefore stay unchanged. A mixed or skinned set writes format 2.

The public rename is breaking even though rigid asset bytes remain compatible. At reconciliation,
main staged untagged 20.41.0 and the newest release was v20.40.0. Controller ruling T9-a stages
21.0.0 and carries the staged nameplate entry into it, leaving exactly one staged version.

## Validate before import and bind against actual host data

Both part policies require a self-contained glTF 2.0 GLB with no external URIs or duplicate JSON
keys. A garment requires one embedded buffer, exactly one skin referenced by every mesh node,
unique non-blank skin joint names and at most 128 joints. Every primitive has paired `JOINTS_0`
and `WEIGHTS_0`, with four influences maximum. Actual accessors are decoded and checked before
importer normalization. All-zero weights, out-of-range joints, further influence sets, animations,
morph targets and mesh weights are refused.

Rigid and garment material decoding retain the same material factors, vertex colours and maps.
Each material group keeps its own geometry and texture ownership. A cache checks requested kinds
before reusing a warm upload, so a prior rigid request cannot bypass garment validation or vice versa.
Loads and uploads are transactional. A failed later part preserves earlier cached resources.

Mixed sets use `loaded.Bind(map, hostMesh)`. The host mesh must carry the exact mapped skeleton.
The map alone has no inverse binds, so rest-pose inversion cannot prove garment compatibility.
Each garment joint resolves ordinally against host skin bones, including reordered subsets.
A named node outside the skin does not qualify. Every inverse-bind element must be finite and
match its actual host counterpart within inclusive absolute tolerance `1e-4` across all 16 elements.
Binding precomputes garment-to-host indices once. Rigid-only `loaded.Bind(map)` remains valid and
refuses sets containing garments explicitly.

## Submission, ownership and rendering parity

A bound view allocates its worlds, descriptors and one frame palette per skinned part at binding.
Every material group of that garment borrows the same palette. Each frame copies host joint model
matrices into garment skin order. They are not skin matrices with inverse binds already applied.
Scene3D applies garment inverse binds and records current and previous palettes for motion history.

Each material group emits one `Scene3D.Draw` or `Scene3D.DrawSkinned`. The template replaces mesh,
world or model, and motion. Every other rigid field carries over, including tint, material,
dissolve and edge, casting, shadow-only, inverted shadow dissolve and dissolve complement.
CPU and GPU skinning preserve the same appearance, shadow policy and outline presentations.

Use `Queue` or write into `Span<AttachmentDraw>`. The mixed descriptor identifies its part and
material index and selects a rigid or skinned draw. `BoneMatrices` borrows the bound view's scratch.
Consume it synchronously before that view's next preparation or submission, or cache disposal.
Rigid-only descriptor writing into `Span<RigidInstanceDraw>` remains supported. A mixed set refuses
it with `InvalidOperationException` before writing. Capacity, pose and template validation complete
before any output write or scene call, including an invalid later part. Unused output tails remain
unchanged.

Motion identity retains the caller incarnation key, canonical definition revision, stable part ID
and material ordinal. Reordering source parts preserves keys. Mesh replacement changes the revision.
`MotionKey.None` remains untracked. One caller-created `scene.BeginMeshOutline` group may contain
the body and all rigid or skinned attachment groups. Dissolve and complement follow both existing
outline presentations.

Valid steady-state submission performs no asset reads, hashing, joint-name searches, animation
advancement or managed allocation. Cache and bound views belong to one scene's render thread and
must not have overlapping calls. Views borrow uploads and throw after cache disposal. Dispose the
cache before its scene.

## Verification and integration boundary

The implementation plan maps format compatibility, actual accessor refusal, host binding, atomic
submission, allocation and rendering acceptance to named tests. Final local verification includes
the Release solution build, one full headless suite, format verification and whole-tree guards.
Conditional GPU skips in that local suite are reported separately from hosted GPU evidence.

The draft PR must have green `ci` and PR-tier `cross-platform-gpu` checks on a head containing
current main. Its merge tree must equal that head tree. One unfiltered hosted dispatch of the same
head runs all three backends with `legs=all`, `bake=false` and `tier=push`. New and existing attachment
facts must pass without skips on Metal, D3D11 and Vulkan. Vulkan synchronization validation must
pass with its producer armed. Any red check needs attribution and an orchestrator decision.
Historical rigid-feature exceptions are not a waiver. Main movement invalidates the gate.

Pre-merge package proof uses a private temporary feed. Packages and symbols must carry 21.0.0,
the committed proof head and tested assembly bytes. Dependency edges remain governed by the
[dependency reference](../DEPENDENCY-SEAMS.md#attachment-definitions-a-gpu-free-leaf).
The orchestrator owns whole-branch review, integration, main push checks and the guarded shared-feed
pack. Consumer adoption waits for owner approval of the exact major tag and the separate
engine-rooted release handoff with independently verified release evidence.
