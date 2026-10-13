# Rigid attachment sets

Status: consumer-approved scope, with package and API choices proposed for implementation-plan review.
The [implementation plan](../superpowers/plans/2026-10-13-rigid-attachment-sets.md) is the execution contract.
This is planned behaviour, not a description of a shipped API.

## Purpose

Represent one authored visual as several rigid mesh parts following named joints of one posed skeleton.
A consumer supplies the character pose and selects the asset. The engine validates the definition,
loads its resources, binds its joints and submits every material primitive with a distinct motion identity.

The first consumer needs independently moving thigh and shin plates plus a pelvis attachment. The engine
contract has no inventory, item, skill, equipment-slot or particular skeleton dependency. The approved
consumer scope is recorded in the
[Grimhollow design](https://github.com/APKiwiOrg/Grimhollow/blob/fe89a288b37a1175f92e1af63628781049eef930/docs/superpowers/specs/2026-10-13-masonry-stone-armour-design.md).

## Existing capability

`BoneSocket.ComposeRigid`, `ContractJointMap`, `MotionKey` and `RigidInstanceDraw` already provide the
underlying transforms and rendering. `GltfLoader` already decodes multiple material groups. Reuse them.
The new capability is an authored definition, strict loading, binding and consistent set submission.

Palettes are untagged matrix spans. A binding therefore retains the actual `Skeleton` instance and requires
the caller to supply that instance with each palette. Equal palette length does not establish equal bone
order. A material group is a separate draw, so a part containing two materials needs two motion keys.

## Package ownership

- `KhaozEngine.Attachments` is a GPU-free, BCL-only package for immutable definitions, JSON, resource
  reference validation, source keys and embedded rigid GLB preflight. Headless publishers reference it
  directly. It is not added to Foundation or Server by default.
- `KhaozEngine.Attachments.Render3D` depends on that package and Render3D. It owns rig binding, uploaded
  resource lifetime, per-scene caching and draw submission. Add it to Game3D.
- The existing Render3D dependency allowlist and its ban on Serialization/ECS dependencies stay intact.
  A byte-memory overload for the existing material-part importer and transactional surface-map loading
  remain in Render3D, where those underlying operations already belong.

This split lets a server inspect definitions without loading graphics assemblies. It follows the existing
separate renderer-arm pattern, with no new third-party library.

## Definition and resource identity

The root fields are `formatVersion` and `parts`. Format 1 contains a nonempty `parts` array. Each part has a positive stable `id`, a `mesh` reference with
`key` and lowercase SHA-256, a named `joint`, and a 16-number row-major `local` affine matrix. Parts are
canonicalized by ID. The mesh hash covers the exact self-contained GLB bytes, including embedded images.
Repeated references to one key must agree on its hash.

Reject unknown versions or members, duplicate JSON members or part IDs, invalid asset keys or hashes,
missing fields, non-finite/singular/non-affine matrices, unresolved references and unsupported geometry.
Keys are relative forward-slash paths with no rooted path, backslash, empty segment, dot segment or URI.
The directory source also rejects symlink/reparse-point traversal. The embedded GLB policy refuses skin
data and URI-bearing buffers/images before invoking the importer. Malformed lengths are refused before slicing.

Parse into owned immutable values before disposing the JSON document. Canonical output sorts parts by ID
and writes a fixed property order. Its SHA-256 is the semantic revision. Whitespace and source-array order
do not change it. Mesh-byte changes do, because their declared hashes are part of the definition.

## Binding and drawing

The binding resolves a contract node, skin-bone index and `BodyAlignment` for every part before becoming
usable. Mesh node transforms have already been baked by the glTF importer. A part's local transform is
its authored correction in body axes at the target joint. Composition is
`BoneSocket.ComposeRigid(local * BodyAlignment(node), palette[bone], characterWorld)`.

Precompute and validate the whole set's posed transforms before submitting any draw. The binding rejects
another skeleton, a wrong-length palette, invalid joint bases and invalid model transforms. It shares the
body's current palette and does not advance animation. Live and death poses use the same path.

The caller supplies a `RigidInstanceDraw` template. Binding replaces Mesh, World and Motion and preserves
the other fields. The convenience overload constructs the descriptor with its existing constructor,
not `default`. Fully hidden characters are omitted by the caller, as with existing scene draws.

The explicit descriptor-writing API also serves recording scenes and outline submission. A narrow optional
scene capability owns upload leases and accepts complete draw descriptors. Its Scene3D adapter is the
default, and the tile-world scene forwards the same capability. The consumer's character does not need to
downcast its scene facade to the concrete renderer.

Identity is nested from caller instance key, semantic revision, stable part ID and material ordinal.
Material order is stable for fixed GLB bytes. A changed GLB changes the semantic revision. A source array
reorder changes neither identity nor draw ordering. `MotionKey.None` stays untracked. The consumer changes
the instance key when its equipment incarnation changes.

## Loading and lifetime

One cache belongs to one scene. It loads each unique `(mesh key, hash)` once and shares that upload among
its sets. It verifies hashes and parses all CPU assets before starting uploads. Definitions and bound
views do not own a second copy of the GPU resources.

The cache owns the handles it creates. A failed load rolls back newly created meshes and textures without
unloading previously cached assets. Successful resources are released once at cache disposal, meshes
before their textures, through the scene's deferred retirement APIs. Repeated disposal is harmless.
Dispose the cache before its scene and stop using bound views first. Get, bind or queue after cache
disposal fails explicitly. No loads, hashing or allocations occur in frame submission.

## Acceptance

Tests must cover strict JSON and resource references, immutable input capture, reordered skin-bone indices,
turned rest joints, independently posed parts, wrong-skeleton same-size palettes, material subdraw identity,
reordering and replacement, failure cleanup, default draw options, shadows and dissolve.

Read actual packed instance data through the existing headless scene/recording seams. Add real-render
comparisons against independently positioned reference geometry, including a baked glTF node transform.
Matrix tests alone are not acceptance. Verify the render branch on the hosted Metal, Direct3D 11 and Vulkan
workflow. New tests can use absolute/control-image assertions rather than inventing local golden baselines.

An engine release and consumer adoption follow normal owner-authorized release gates. This document
authorizes no release tag, live client run or stress test.
