# J0 coverage and ordinary DQ reconciliation

## Exact inputs and boundary

Runtime integration revision is `2075584da1a84b77f1ac110b4b091df7db668ae6`. Its first parent is `27d5d0e20c316ef946762009698c49ef72716f8b`, the approved coverage plan over runtime `8be362eecff2a28c6798e6f59bbb116fe1eb8268`. Its second parent is accepted ordinary DQ `ad86b4a6193800de118ad9da9e824683aa3e7b9c`. The common ancestor is `10659a82c766d2d4ec354d9f215af3c890c7c477`. DQ-R1 `c4c8b48a2bba9e58d829b95f93c5708207842d29` is an ancestor of that DQ input.

This reconciles the complete accepted DQ history with coverage. It imports no filtered prototype or renderer-baseline branch, no S4.1 types or typed coverage layout, and no private asset payload. Ordinary DQ remains active. Covered DQ refuses until S4.7.

## Retained interfaces

- `RetainSkinningSource` remains the single upload-time clone for both profiles. It retains vertices, indices, inverse binds, rest poses, rig and immutable profile. Coverage annotation validation happens before resource allocation, and the retained mesh carries the same validated material/source provenance into `_coverageHostUploads` and ordinary execution/bounds/history.
- Profile-bearing replacement uploads retain distinct handle/source generations. DQ-R1 source snapshots, bounds, previous-pose identity and source words remain independent of caller mutation.
- `PreparedSkinningStore`, DQ palette/evaluator/bounds mathematics and immutable profile semantics are unchanged from the accepted DQ input. LBS scalar graphs, legacy constructor signatures and semantics, attachment JSON and 144 output hashes are unchanged.
- Coverage conformation and selected FINAL surfaces keep their existing LBS routing across material, cascade, point and outline consumers, including current-source restoration and Submitted/Abandoned lifetime.
- Attachment definitions/cache/fit identities retain DQ profile selection. Coverage host bind snapshots and rigid socket semantics are preserved. Copied attachment descriptors retain their selected profile.
- Generic skinning rejection and coverage transition rejection retain separate existing producers. Fused/split resolve consume both masks with independent descriptor-pair cache identity. Resolve substitutes the same persistent zero texture for either absent input. Each producer retains its own clear and first resolving view lifetime. S4.5b owns their later unification.

## Accepted DB rulings

The owner performance ledger recorded DB-1 to DB-6 on 2026-10-21. These remain constraints, not completed J0 proofs.

| Ruling | Retained requirement |
| --- | --- |
| DB-1 | A separate exact carry reference needs a continuous no-clip domain proof. Current-pose exact jobs keep frozen expectations. |
| DB-2 | Every admitted pose needs a pre-certified usable alternative. Uncovered recovery and expired anchors are outside the guarantee. |
| DB-3 | GPU cost is a hard per-frame ceiling, including synchronized attacks and equips. Averages cannot pass it. |
| DB-4 | Armour content must carry certified coverage of allowed poses. Unsupported blends or layers block admission. |
| DB-5 | Measure collar and replacement appearance within authored caps. Numerical appearance limits need owner acceptance. Positive clipping tolerance remains zero. |
| DB-6 | Projected-size refresh tiers may use hysteresis and combined colour, shadow and outline demand. Every consuming pass stays safe. |

## Frozen sources and proof paths

- `KhaozEngine.Render.Tests/Fixtures/coverage-reference-v1/outputs.sha256`, 72 cases and 144 output hashes.
- `KhaozEngine.Render.Tests/Fixtures/dual-quaternion/rigid-words.json` and `blender-rigid/expected.json`.
- `docs/design/DUAL-QUATERNION-BOUNDS-2026-10-22.md`, ordinary DQ only.
- `docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md` and `EQUIPMENT-COVERAGE-GPU-ARITHMETIC-PROOF.md`, frozen LBS authority.
- `docs/design/EQUIPMENT-COVERAGE-SKINNED-SUPPORTS-DQ-AMENDMENT-2026-10-23.md` and its implementation plan, future precise DQ and posed-support authority.
- Accepted ordinary DQ native parent proof is hosted run `39875075453` at `65e4d3be5`, accepted through `ad86b4a61`. This worker does not rerun another checkout or transfer that proof to the new integration SHA.

## Conflict dispositions

Raw conflicts, index stages and combined diff were recorded before resolving any hunk in `/tmp/khaozengine-coverage-dq-j0/conflict-*`. The per-path map follows.

| Path | Resolution |
| --- | --- |
| `KhaozEngine.Attachments.Render3D/AttachmentSetBinding.cs` | Preserve coverage ownership, retained binds and conformer checks with DQ profile/cache/fit identity. |
| `KhaozEngine.Attachments.Render3D/AttachmentSetCache.cs` | Preserve coverage ownership, retained binds and conformer checks with DQ profile/cache/fit identity. |
| `KhaozEngine.Attachments.Render3D/BoundAttachmentSet.cs` | Preserve coverage ownership, retained binds and conformer checks with DQ profile/cache/fit identity. |
| `KhaozEngine.Attachments.Render3D/SkinnedAttachmentBinding.cs` | Preserve coverage ownership, retained binds and conformer checks with DQ profile/cache/fit identity. |
| `KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs` | Preserve both rejection producers and union their inputs at fused/split resolve. Defer owner unification to S4.5b. |
| `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt` | Union independently accepted shader entries. Resolve shared temporal entries against reconciled source with artifact proof left to root. |
| `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt` | Union independently accepted shader entries. Resolve shared temporal entries against reconciled source with artifact proof left to root. |
| `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt` | Union independently accepted shader entries. Resolve shared temporal entries against reconciled source with artifact proof left to root. |
| `KhaozEngine.Render.Tests/Render3D/TemporalResolveShaderTests.cs` | Preserve both rejection producers and union their inputs at fused/split resolve. Defer owner unification to S4.5b. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalResolve.cs` | Preserve both rejection producers and union their inputs at fused/split resolve. Defer owner unification to S4.5b. |
| `KhaozEngine.Render3D/Internal/ShaderSources.TemporalSplit.cs` | Preserve both rejection producers and union their inputs at fused/split resolve. Defer owner unification to S4.5b. |
| `KhaozEngine.Render3D/Models/GltfLoader.Skinned.cs` | Thread explicit profile alongside legacy optional coverage collector. |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.Motion.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Rendering/TemporalResolveRenderer.cs` | Preserve both rejection producers and union their inputs at fused/split resolve. Defer owner unification to S4.5b. |
| `KhaozEngine.Render3D/Rendering/TemporalSplitResolve.cs` | Preserve both rejection producers and union their inputs at fused/split resolve. Defer owner unification to S4.5b. |
| `KhaozEngine.Render3D/Scene3D.DrawDescriptors.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Scene3D.MotionTarget.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Scene3D.SkinnedDrawRecords.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Scene3D.SkinnedMeshLoading.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Scene3D.SkinnedSubmission.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs` | Compose coverage FINAL surface routing and restoration checks with accepted DQ prepared/source/profile consumer routing. |
| `KhaozEngine.Render3D/Scene3D.TemporalResolve.cs` | Preserve both rejection producers and union their inputs at fused/split resolve. Defer owner unification to S4.5b. |
| `docs/CROSS-PLATFORM.md` | Retain coverage and ordinary DQ documentation and index entries. |
| `docs/INDEX.md` | Retain coverage and ordinary DQ documentation and index entries. |
| `docs/USING-KHAOZENGINE.md` | Retain coverage and ordinary DQ documentation and index entries. |

## Post-merge compatibility correction

The once-only full suite reported nine stale coverage binding assertions at the previous final texture slot. The new layout keeps ordinary rejection last and coverage immediately before it. All four assertions retain exact same-instance checks at the coverage slot. A separate behavioral red then caught the old split diagnostics accessor selecting the first cached input pair after switching to another pair. It now selects the same active pair as Run. The corrective Release rebuild/test passed all 110 covering cases with zero skips. The full suite is not repeated, as required by the J0 brief.

Both accepted inputs declare 21.11.0. J0 retains those declarations and invents no version. At start, local main was `989eba495ae7c0f9ff15973889481e3db91fd27a`, declared 21.12.1 and had nearest reachable tag `v21.12.1`. Observed origin/main was `f2e5ef6a64046bbe39cf5ea866f9440ae476b89b`. The owner must re-read main and tags at eventual integration. No main reconciliation, package feed write, release or tag is part of J0.

## Actual verification

Every local build, test, format, shader-identity emission and repository guard used build-slot. GPU and measurement authorization variables were absent. The complete command arrays, integer exits, log hashes, original full-suite failures and frozen manifest hashes are in [the proof manifest](2026-10-23-coverage-dq-integration.json). Logs and TRX files remain under `/tmp/khaozengine-coverage-dq-j0/`.

| Check | Actual outcome |
| --- | --- |
| Coverage parent baseline | 6 passed, 0 skipped, exit 0. |
| Initial import build | Exit 1. Two coverage call sites needed the accepted profile arguments before behavioral red could compile. |
| Retained-source behavioral red | 1 passed, 1 failed, 0 skipped, exit 1. The ordinary snapshot dropped coverage registration after caller mutation and ordinary submissions. |
| Expanded admission red | 1 passed, 5 failed, 0 skipped, exit 1. Stale vertex/index/bind metadata and DQ profile admission were not refused. |
| Retained-source/admission green | 6 passed, 0 skipped, exit 0. |
| Broad focused reconciliation | 745 passed, 27 native/opt-in facts skipped, exit 0. All eight new cases passed. Two earlier test-import compiler failures are retained in the manifest. |
| Release solution build | Exit 0, zero warnings and errors. |
| Once-only full headless suite | 41,372 passed, 9 failed, 1,573 skipped, exit 1 across 41 assemblies. Every failure was a stale coverage texture-slot assertion. Console and TRX outcomes agree. |
| Selected-cache diagnostics red | 1 passed, 1 failed, 0 skipped, exit 1. The split accessor returned the first cached pair instead of the selected pair. |
| Corrective Release rebuild/test | 110 passed, 0 failed, 0 skipped, exit 0. Covers all original failures, source identity/admission, cache selection, ordinary rejection and temporal shader contracts. |
| Whole-solution final format verification | Exit 0, formatted 0 of 9,218 files. Diagnostic run had no workspace warning. The initial format application reported a generic workspace-load warning before the solution build. |
| Whole-tree guards | Dashes, prose, file size, instruction budget, doc versions and diff checks all exit 0. No baseline was raised. |

The full suite ran once. Its nine failures remain recorded rather than replaced by a second full run. Only the covering check reran after the compatibility corrections, as the brief requires. No geometry, pixel expectation, legacy hash, assertion tolerance or admission domain was weakened.

The CPU-only shader emission updated only the ten shared temporal fragment identities in each SPIR-V, MSL and HLSL table. The full suite verified those tables without update flags. New shader resources retain both rejection inputs. This does not establish native output equality at the integrated revision.

Root independently reviews and pushes this reconciliation, then owns its exact reviewed native correctness gate. [Vulkan validation issue 1539](https://github.com/APKiwiOrg/KhaozEngine/issues/1539) remains separate from word equality. S4.1 and all subsequent runtime, private fit, cost and release gates remain pending.
