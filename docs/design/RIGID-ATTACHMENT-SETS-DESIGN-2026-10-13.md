# Rigid attachment sets

Status: implemented and integrated into main at `a80fab6c4`, staged for `20.38.0`. The owner approved exact tag `v20.38.0` at that commit for a separate release chat.
This document records the design rationale. The package READMEs and consumer guide describe current APIs.
Package availability is determined by release tags, with consumer adoption gated on an approved release.

## Current API supersession

The [skinned attachment design](SKINNED-ATTACHMENT-PARTS-DESIGN-2026-10-15.md) supersedes this
record for current set APIs in staged 21.0.0. Sets now use kind-neutral names and may mix rigid
parts and skinned garments. This document's audit, historical gate decisions and original plan
remain a record of the rigid feature. They do not waive any skinned-branch verification gate.
Use the living consumer guide and package READMEs for current signatures.

## Implementation checkpoint, 2026-10-14

All four implementation tasks and their scoped reviews are complete. The final review corrections are
included. The feature was fast-forwarded to main and pushed at `a80fab6c4`. Actual main push checks
passed. The separately approved exact-tag release remains with the engine-rooted release chat.

At `f037fd0fc0448771c9484b1b08111aaf9e5ba6e0`, the combined Release build passed with zero warnings and
errors. The full solution suite reported 31,031 passed, zero failed and 1,361 conditional skips across
37 test projects. Whole-tree governance checks passed. A strict local Metal run passed all eight attachment
GPU cases with no skips. Private packaging produced 107 packages and 105 symbol packages. The two new
package DLLs match the tested build, their repository commit is correct and their dependency edges match
the package contract.

[Hosted run 38392043530](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38392043530) tested
`fcf49bf5d3f94ddfbcacd25e426af3c8da267eef`. All eight attachment GPU cases passed on Metal, Direct3D 11
and Vulkan. The complete Metal, primary Vulkan and Vulkan synchronization validation jobs passed.
The synchronization log contains the armed validation producer, confirming the meaning of its
no-validation-message annotation. The run failed because the Windows job failed 14 existing rows tracked by
[#1382](https://github.com/APKiwiOrg/KhaozEngine/issues/1382),
[#1316](https://github.com/APKiwiOrg/KhaozEngine/issues/1316) and
[#1157](https://github.com/APKiwiOrg/KhaozEngine/issues/1157). This is feature-specific proof, not a green
full matrix or a waiver of those failures.

The five failures under #1382 were subsequently fixed on main at
`0898a431058bb3d998f2c65daddf6512c01fb3e3`. Its separate
[native Windows proof](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38405463669) passed all
25 artifact cases with no skips, including the five original failures. That issue is closed. The fixture
and shadow failures under #1316 and #1157 remain owned by their separate workstreams.

The subsequent input and GUI merge into `f037fd0fc` leaves the attachment, renderer, backend and headless
fixture paths unchanged. Its combined local build, suite, Metal proof and packaging were rerun. This
historical checkpoint did not contain later foliage work. The subsequent integration below reconciles
it. No engine release or dependent consumer adoption is authorized here.

## Replacement integration gate, owner decision 2026-10-14

The owner replaced the full Windows matrix gate with a baseline gate. Main and released `v20.36.0`
fail the same Windows rows in [run 38373207663](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38373207663).
The #1157 workstream has had no work since 7 October. This decision supersedes the earlier wait for #1157.

The owner amended the trigger gate after confirming that feature-branch pushes do not run these
workflows. A draft PR to main must have green `ci` and PR-tier `cross-platform-gpu` checks on the
exact reconciled head. That head must contain current main so its PR merge tree is identical. Main
movement requires reconciliation, a push and fresh head checks. One full hosted `cross-platform-gpu`
dispatch must test that same head using `legs=all`, `bake=false` and `tier=push`, the route used by
run 38392043530. After the fast-forward and push, actual main push checks must pass. A failed main
check is reported without a revert or force operation. All eight attachment cases must pass without
skips on Metal, D3D11 and Vulkan. Vulkan and its synchronization validation leg must pass.

The owner subsequently admitted two exact non-attachment rows after static attribution. The measured
Metal `WarmSignalResolveAllocatesNothing(Mesh)` path uses no changed Render3D or attachment type.
Its per-thread 7,872-byte assertion is documented in [#1394](https://github.com/APKiwiOrg/KhaozEngine/issues/1394),
in the [#1391](https://github.com/APKiwiOrg/KhaozEngine/issues/1391) allocation family. The Windows
HTTPS admin row reaches only the unchanged numeric tile server and framework TLS path, with no
renderer, asset loader, attachment or physics factory. [#1396](https://github.com/APKiwiOrg/KhaozEngine/issues/1396)
records its 20-second timeout and its pass in earlier run 38392043530. Neither mechanism is claimed
resolved. Their owning workstreams retain repair responsibility.

The exact allowed set is the eight Windows #1316 path-fixture rows, the Windows #1157 point-shadow
boundary assertion, that one Metal #1394 Mesh row and that one Windows #1396 HTTPS row. Any further
non-attachment failure requires static attribution, an issue with evidence and an owner ruling before
merging. Any attachment failure blocks integration. No rerun or assertion change was used.

The unchanged workflow produces TRX only for Windows. Metal and Vulkan produce detailed
`suite-output.log` artifacts. Windows TRX and the two actual log artifacts prove their exact row sets.

No shadow code, shader, tolerance, #1316 repair or proof-only workflow change is included. Those
workstreams retain ownership. The integration contains main `ee6016aae` and follows released
`20.37.0` with staged additive version `20.38.0`. The separate engine-rooted release chat owns the approved exact tag `v20.38.0` at `a80fab6c4`.

### Reconciled local checkpoint

The reconciled source is `be9889d4fa34728e3b3ab63cc8e89aa5ed442ef2`, containing main
`ee6016aae2473dadcb1c8a3fdcdaff4bc2371392`. The Release build and the affected render rebuild passed
with zero warnings and errors. The focused proof passed 112 core and 47 renderer cases without skips.
The full solution suite passed 31,573 cases with zero failures and 1,374 conditional skips across
37 projects. Strict native Metal with API validation passed all eight attachment cases without skips.
The new captures preserve independent poses, enabled and omitted shadows, and outlines.

Private packaging passed with 107 nupkg and 105 snupkg files. Both attachment DLL hashes match the
tested build. All repository commit fields equal the tested source, all engine dependency versions
are `20.38.0`, the core has no direct dependencies, the renderer has exactly Attachments and Render3D,
and both umbrella edges include the renderer arm. Whole-tree guards and doc versions passed. The
format sweep found one initializer whitespace row, corrected without a token change and reverified.

The local Metal TRX SHA256 is `7bd4979285c9ae6009e06265f0c3c2f45be7e1b886660726cd3eebc077231032`.
The fresh hosted gate and merge-resolution review are complete, as recorded below.

## Integrated verification checkpoint, 2026-10-14

[PR #1393](https://github.com/APKiwiOrg/KhaozEngine/pull/1393) is merged. Main was fast-forwarded and
pushed to `a80fab6c4caa1de66cb8d643e9ca998a567e993d`, the exact gated head. Its PR merge tree equals
its head tree. Final fetch found no main movement or version collision. The changelog heading is
`## 20.38.0`, with released `20.37.0` history preserved byte for byte.

| Run | Event and proof | Conclusion |
| --- | --- | --- |
| [38461567233](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38461567233) | PR ci | Success |
| [38461566855](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38461566855) | PR cross-platform-gpu | Success |
| [38461566879](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38461566879) | PR Windows console regression | Success |
| [38461600502](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38461600502) | One full hosted dispatch on the exact head | Failure, accepted only for the documented Metal and Windows rows |
| [38473512217](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38473512217) | Actual main push ci | Success |
| [38473512218](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38473512218) | Actual main push cross-platform-gpu | Success |
| [38473512305](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38473512305) | Actual main push Windows console regression | Success |

Full dispatch 38461600502 passed all eight attachment cases on each backend, 24 total with no skips.
Vulkan job 115517954152 passed, with 37 successful project summaries and no failed rows. Vulkan sync
job 115517953624 passed. Its log confirms the armed validation producer with zero validation errors.
Metal job 115517953708 failed only the documented #1394 Mesh row. Windows job 115517953906 failed
exactly the ten rows below. Its TRX counters contain no aborted, error or runner-timeout outcomes.

| Issue | Windows failed row |
| --- | --- |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeStorageNamespaceTests.NativeStorage_GuardRefusesEveryClosureReferenceBeforeReadingIt(reserved: "mesh", absolute: True)` |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeStorageOwnershipTests.NativeStorage_TiledOpenRefusesResourceInsideItsNamespaceBeforePublishing(mesh: "tiled-world/tiles/rock.glb", absolute: True)` |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeManifestAdapterTests.Adapter_RejectsStaleOrMissingMeshAndLodFiles` |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeLifecycleTests.NativeLifecycle_TiledIdentityAndDecimalTransportSurviveConversionAndReopen` |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeStorageOwnershipTests.NativeStorage_ConversionChecksTheTiledTargetNamespaceForAMonolithicSource` |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeLifecycleTests.NativeLifecycle_PartialNativeOpenRefusesWithoutReplacingSession` |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeStorageOwnershipTests.NativeStorage_MissingTiledStorageRefusesSaveAndRetileWithoutRecreatingIt` |
| #1316 | `KhaozEngine.Tests.MapDoc.NativeManifestAdapterTests.Adapter_ResolvesResourceIdsToVerifiedPathsNotAssetIds` |
| #1157 | `KhaozEngine.Tests.Gpu.PointShadowFilterGpuTests.TheSoftEdgeCrossesACubeFaceBoundaryWithoutAStep` |
| #1396 | `KhaozEngine.Tests.ServerAdminEndpoint.TileWorldServerAdminEndpointTests.The_https_endpoint_serves_a_tile_world_server` |

The single fresh merge-resolution review was approved with no findings. Independent checks confirmed
inherited foliage and shadow source preservation, unchanged workflows, proof-only commit absence,
released changelog identity and whitespace-only initializer correction. Static attribution of both
additional baseline rows was separately verified. No implementation was redispatched.

| Retained artifact | SHA256 |
| --- | --- |
| Windows TRX ZIP 11849231951 | `f4c064afa31b72f0c229a5f0bb96adb072e1cef04c5d8e8a92fd095df858c791` |
| Windows Render TRX | `b33801e825350c47adb09d830707c1fbf2e9b2d04c0dd8733558f2133462d50e` |
| Windows Server TRX | `e366bd16b6ac881594585e7ca35de0a6c4751ede1ebb7920051b81dc73874130` |
| Metal log ZIP 11847334337 | `edf77e3805e8664feac9b1d5e5c8d6361991203e78602ac26b74d5ab4f9272e8` |
| Metal raw suite-output.log | `0c7b78b44cc26644c570f26eb18bf7eef03506f3f5100d2909bb27aed01ef011` |
| Vulkan log ZIP 11852652775 | `d1d36ac61bc49dcd1aeb6f661c10a8506782429aa9893e30fafa0e701ecb2459` |
| Vulkan raw suite-output.log | `c61eb9edbc5f60973be9eec0022e22c690ea08abaf7bb4e3cc77ecec6906d52d` |
| Vulkan sync ZIP 11846691378 | `5e60093e64bea1ffeb9f8ed1133fdc989de2ef0386ded15416fb9f218c220642` |

Artifact ZIP digests match GitHub's recorded digests. The earlier retained Windows TRX shows the
HTTPS row passed in 0.1387199 seconds in run 38392043530. Its server TRX SHA256 is
`99c8bc1bead1069ed114fbfd178e4a2763164f69af2cba32e31837fa4f9a79b5`.

Shared main packaging passed through the sanctioned wrapper, refreshing 107 packages and
105 symbol packages from exact `a80fab6c4`. Its temporary `.local-feed.pack.n8A6mu` directory is gone,
and the root checkout was confirmed clean. Package provenance and dependency checks passed.

The owner approved tag `v20.38.0` at exact `a80fab6c4` through a separate engine-rooted release chat.
All three actual main push checks passed at the approved commit. That chat owns the release. Further main pushes are held until its tag report.
This audit checkpoint remains on `feature/rigid-attachment-sets` during that hold. The structured
[audit record](../verification/2026-10-14-rigid-attachment-sets-integration.json) retains run conclusions,
allowed row mappings, package provenance and artifact hashes. The integration
lane created no tag or release. Consumer adoption remains with the release handoff.

## Purpose

Represent one authored visual as several rigid mesh parts following named joints of one posed skeleton.
A consumer supplies the character pose and selects the asset. The engine validates the definition,
loads its resources, binds its joints and submits every material primitive with a distinct motion identity.

The first consumer needs independently moving thigh and shin plates plus a pelvis attachment. The engine
contract has no inventory, item, skill, equipment-slot or particular skeleton dependency. The approved
consumer scope remains owned by Grimhollow.

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
