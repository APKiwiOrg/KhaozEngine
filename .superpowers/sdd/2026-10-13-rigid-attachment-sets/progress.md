# SDD ledger - plan: docs/superpowers/plans/2026-10-13-rigid-attachment-sets.md

Owner approved the written spec, implementation plans and Codex subagent-driven execution on 2026-10-13. No release tag is authorized.
Engine worktree: /Users/antonio/KhaozEngine/.worktrees/rigid-attachment-sets
Branch: feature/rigid-attachment-sets
Execution base: 38da0bc41c145ffed36a5164da8bed5899e93213
Fetched main is contained. Engine version 20.35.1 is ahead of latest tag v20.35.0. Recheck at integration.

## Preflight scan

| Tasks | Producer and consumer or internal check | Result |
| --- | --- | --- |
| E1 | CPU definitions, strict JSON, source and GLB tests match created files and API | Consistent. Invalid caller arguments differ from parsed data errors. |
| E2 | Binding and immutable-byte importer tests match material extraction and renderer package | Consistent. Skin order and literal-world assertions are independent. |
| E3 | Cache, leases and rollback tests match scene resource lifetime changes | Consistent. Bound view properties are introduced here, draw methods in E4. |
| E4 | Draw descriptors, identity and actual renderer tests match submission contract | Consistent. GPU facts require hosted three-backend proof. |
| E1/E2 | Definitions and canonical part order feed the binding, both update catalog/solution/architecture | Sequential ownership. No interface mismatch. |
| E1/E3 | Source snapshots, hashes and embedded policy feed the transactional cache | Verify all CPU data before new GPU allocations. |
| E1/E4 | Semantic hash and stable IDs feed material draw identities, README updated again | Explicit little-endian revision words, no frame hashes. |
| E2/E3 | Pure binding and byte importer feed loaded/bound views and cache | E3 introduces resource-owning facade and borrowed views. |
| E2/E4 | Skeleton identity and complete validated pose feed descriptor writing | Full-pose validation prevents partial draw submission. |
| E3/E4 | Bound view and scene capability feed Queue/WriteDraws and disposed checks | Cache lifetime owns resources, caller owns equipment incarnation. |

Ruling: The task-brief helper only recognizes headings named Task N, but the approved plan uses E1 to E4. Extract the exact E1 section and shared CPU constraints into its standard brief path without changing the approved plan. Cost if wrong: correct the brief extraction before dispatch.
Ruling: Full solution verification runs once at engine integration, with focused task red/green checks throughout. This follows the approved global constraint and shared no-local-stress rule. Cost if wrong: an unrelated regression may be discovered later in the integration pass.

## Execution

Task 1: in progress, BASE 38da0bc41c145ffed36a5164da8bed5899e93213. Implementer /root/attachments_e1, native Codex Work gpt-6.1-sol xhigh. Brief task-1-brief.md, report task-1-report.md.

Task 1: implementation 0bdd22eda5e044bef4cf5fac4ccd12a48a4e0124. Independent CPU verification exit 0, 92 passed, no skips. Inspected package/architecture/README diff and clean diff check. Review /root/attachments_e1_review in progress.

Task 1: review requests fix F1, malformed Unicode bypasses InvalidDataException/source context. Parent inspected catch and decoder call, verified the reachable gap. Fix round 1/5 pending. Implementation 0bdd22eda pushed successfully. Windows filesystem proof deferred to hosted gate.

Task 1: fix round 1/5 (1 addressed, 0 open, commits 0bdd22eda..d8a52d832). Reviewer approved. Independent focused verification exit 0, 45 passed.
Task 1: complete (commits 38da0bc41..d8a52d832, review clean). Both implementation and fix are pushed to feature/rigid-attachment-sets.
Task 2: prepared, BASE d8a52d832f55d8f5d01c7d007b27bdedb268ed3f.

Task 2: in progress with /root/attachments_e2, native Codex Work gpt-6.1-sol xhigh. Brief task-2-brief.md, report task-2-report.md. Main owns planning documents and integration.

Game planning status updated and committed at 347b9d22 on feature/masonry-stone-armour. Docs-only checks and commit guards passed. No game implementation or release adopted.

Task 2: initial RED reached compile failure CS0419 from six existing XML cref links made ambiguous by the new overload.
Ruling: E2 may qualify all six affected LoadPartsWithMaterials XML references with the string signature, including PropLoader.cs and Scene3D.cs outside the original file list. The additive overload requires this to preserve warnings-as-errors. No behavior changes or suppression. Cost if wrong: revert the narrow documentation links, no runtime change. Behavioral RED remains required after compilation succeeds.

Ruling: Extend the E2 XML-reference correction to the seventh and last unqualified cref, in TileWorld.Render3D/GltfMeshResolver.cs. The same additive-overload compatibility requirement applies to every affected reference. Cost if wrong: revert that documentation-only qualification. Worker search found no further unqualified references.

Task 2: implementation 1191c4b4cc9c6c022b6d7b7d0702581733f29e71. Independent focused tests exit 0, 55 passed, no skips. Parent inspected binding source and diff check. Reviewer /root/attachments_e2_review in progress, including a concrete no-frame-allocation concern for ArrayPool fallback above 128 parts.

Task 2: review requests fix F1, ArrayPool fallback above 128 parts can allocate in frame composition. Fix round 1/5 pending. Parent and reviewer independently confirmed the branch and uncapped valid part count. Implementation 1191c4b4c pushed successfully.

Task 2: fix round 1/5 (1 addressed, 0 open, commits 1191c4b4c..1daf344c3). Reviewer approved.
Task 2: complete (commits d8a52d832..1daf344c3, review clean). Parent independently verified 55 initial tests and 21 binding cases after the correction, both exit 0. Final corrected commit pushed.
Task 3: prepared, BASE 1daf344c3cebfe420cffd3872695c54bedbe934b.

Task 3: in progress with /root/attachments_e3, native Codex Work gpt-6.1-sol xhigh. Brief task-3-brief.md, report task-3-report.md. Engine main and version reconciliation remain at final integration.

E3 staging encountered a transient index.lock while parent read-only git status refreshed the index. Lock vanished naturally and no lock was removed. Parent will use GIT_OPTIONAL_LOCKS=0 for read-only status checks during worker writes.

Task 3: implementation 2d0e2a889e877305077de763b1c1dc593b00f725. Parent independently ran 24 focused resource tests, exit 0, no skips, and inspected cache/upload/surface-map source plus clean diff check. Reviewer /root/attachments_e3_review is in progress.

Task 3: complete (commits 1daf344c3..2d0e2a889, review clean). Pushed. Parent resolved reviewer cross-task verification items against unchanged E1/E2 source, earlier reviews and full ArchitectureTests evidence.
Task 4: prepared, BASE 2d0e2a889e877305077de763b1c1dc593b00f725.

Task 4: in progress with /root/attachments_e4, native Codex Work gpt-6.1-sol xhigh. Brief task-4-brief.md and report task-4-report.md. Worker owns local E4 implementation and CPU/real-Metal proofs. Parent owns push, current-main/version reconciliation and exact-commit hosted three-backend gate. No release authorized.

E4 interim verification: CPU log reports 8 passed, exit 0. Native Metal log reports 8 passed, exit 0. Parent visually inspected artifacts/rest-set.png, posed-set.png, shadow-casting.png, shadow-omitted.png and posed-outline.png. Five two-material parts remain visible under independent poses, shadows appear only in the enabled capture, and outlines surround all posed parts. Worker is strengthening source-order identity coverage and final backend/coverage diagnostics before commit. No final task approval or hosted proof claimed yet.

Task 4: local implementation and review complete (commits 2d0e2a889..0e6b9953a, local review clean). Pushed. Independent final submission run exit 0, 9 passed. Hosted all-three-backend gate remains pending with integration. Do not redispatch E4 implementation unless a new verified finding requires it.
Final integration: current origin/main merged with no changes, local main equals origin/main, version 20.35.1 ahead of latest tag v20.35.0. Riding staged version and adding attachment changelog entry, with approval status corrected in design/plan docs.

Final review /root/attachments_final_review dispatched for e980c5ab18c2461ee123d24907e84c3cc6fd2528..23ef8cbf6e0ab8207b77ad675c69514b0b93c8b6, package review-e980c5ab1..23ef8cbf6.diff. Full solution suite and hosted three-backend gate intentionally wait for review/fix completion so they run on the final source. Workflow cross-platform-gpu is active, ID 296707773. Gateway searches found no workflow tools, so gh is the available fallback. Use legs=all,bake=false,tier=push.

Final review requires F1 malformed UTF-16 identity validation and F2 stale design-index status. One final fix wave is prepared in final-fix-brief.md. All declined-to-judge items explicitly resolved to approved scope/lifecycle boundaries or still-pending parent verification gates. No findings silently parked.

Final fix wave /root/attachments_final_fix dispatched, native Codex Work gpt-6.1-sol xhigh. BASE 23ef8cbf6e0ab8207b77ad675c69514b0b93c8b6. Brief final-fix-brief.md, report final-fix-report.md. Full verification and hosted GPU gate remain after this fix and one scoped re-review.

Final fix wave complete at 9a369c6a9fab1e53fa53cbc24cda6e57289a4523. Final scoped re-review APPROVED, both findings addressed. Parent independently verified all 112 CPU attachment tests, exit 0.
Concurrent main advanced to 69d235aa2b1b50b3ae457897b37605bc164f0030 with staged 20.36.0 and slope-aware tile ground. Merge 99ab34a31 reconciles it. Sole conflict was CHANGELOG.md, resolved by preserving both feature entries. No attachment source conflict. Guarded version declarations match 20.36.0. Final full and backend checks will run after this reconciliation.
Index status is made procedural before final verification so it remains accurate when gates complete, without asserting a release.

Final verification source: bea1df79d597856ba912159a786e55d29ddf326f, pushed. Origin/main 69d235aa2 is contained. Version 20.36.0, latest tag v20.35.0. Full build running in exec session 94892 (integration-build.log). Whole-tree guard chain running in exec session 73599. Hosted dispatch requested once for cross-platform-gpu.yml with legs=all, bake=false, tier=push. Pre-dispatch run list was empty. Discover and verify returned run HEAD before watching, never repeat dispatch from assumption.

Full Release build PASSED, exit 0, zero warnings/errors, about 177 seconds. Full solution test suite started through build-slot heavy. Hosted GPU run 38390021327 is in_progress and its headSha matches bea1df79d597856ba912159a786e55d29ddf326f exactly. URL https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38390021327 . gh run watch writes integration-backends.log, no redispatch.

Active process recovery: full suite exec session 30156, hosted watcher exec session 90595, whole-tree guards exec session 73599. Build session 94892 completed successfully. Verification head bea1df79d597856ba912159a786e55d29ddf326f. Do not start duplicate suites or workflows while these run.

Whole-tree guards completed, exit 0: dashes, prose, file-size, instructions, doc versions and diff check. Informational existing prose baseline reduction (274 vs 276) and expected Scene3D extraction reduction (2357 vs 2372) were not baseline changes. Full-suite and hosted watcher sessions remain active until their exit codes are collected.

Full Release solution suite PASSED, exit 0, about 249 seconds. 36 project summaries: 30702 passed, 0 failed, 1361 conditional/environment skips. Native GPU verification is separate.

Concurrent release collision: v20.36.0 was annotated and pushed at 69d235aa2 while verification ran. Private pack guard refused the branch at 20.36.0 before writing packages, exit 1. No override used. Current main still declares 20.36.0, so this additive package feature is staged as 20.37.0. Attachment changelog entries moved into the new heading, and the released 20.36.0-and-older history was byte-compared and preserved. Four version/documentation files changed, no runtime source changes. Hosted run 38390021327 was cancelled as superseded. Wait for terminal cancellation before another dispatch on this ref. Full build/suite and pack proof will use the new revision.

Replacement verification head fcf49bf5d3f94ddfbcacd25e426af3c8da267eef is pushed, staged version 20.37.0. Full build exec session 57593 writes integration-20.37-build.log. Superseded hosted run 38390021327 is terminal cancelled, all three primary legs cancelled, sync probe passed, no primary leg test failure reported. Replacement hosted run dispatched once only after terminal cancellation, awaiting its run ID.

Replacement hosted GPU run is 38392043530, URL https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38392043530 . Its headSha matches fcf49bf5d3f94ddfbcacd25e426af3c8da267eef. Watching through gh run watch, log integration-20.37-backends.log. Do not duplicate this run.

20.37.0 full Release build PASSED, exit 0, zero warnings/errors, about 204 seconds. Full suite on fcf49bf5d now running, integration-20.37-suite.log. Hosted watcher is exec session 98884, run 38392043530, integration-20.37-backends.log.

Active final revision sessions: suite 13947, hosted watcher 98884 (run 38392043530). Build 57593 completed successfully. Guard chain 73599 completed successfully on reconciled source, with subsequent four-file version update separately guarded. No source edits are active.

20.37.0 full suite PASSED on fcf49bf5d, exit 0, about 625 seconds. 36 summaries: 30702 passed, 0 failed, 1361 conditional skips. Private cumulative pack proof started in /tmp/khaozengine-attachments-fcf49bf5d using --no-build --no-restore. No release or game pin change.

Private cumulative pack proof PASSED, exit 0, about 76 seconds: 107 nupkg and 105 snupkg (212 files) in /tmp/khaozengine-attachments-fcf49bf5d. Checked new core and renderer nuspec versions/repository commit/readme, DLL hashes equal built files, core has zero direct dependencies, renderer has exactly Attachments and Render3D. Game3D and TileWorld.Render3D package edges include the renderer arm. All inspected engine dependency versions are 20.37.0. Tree remains clean.

Hosted Metal job 115278006262 PASSED in 20m24s. Downloaded and sanitized ANSI-containing logs before inspection, then confirmed all 8 RigidAttachmentSetGoldenTests cases passed. Vulkan sync job 115278006397 PASSED in 7m6s. Confirmed actual INFO VulkanValidationLogHost line armed the sync rung and console sink, so the no-validation-message notice reflects a clean producer-backed run. Primary Vulkan and D3D11 remain active.

Concurrent main advanced to ca9cb259f with native gestures and bottom sheets, still staged 20.37.0. Reconciled with only CHANGELOG/INDEX conflicts, preserving both features. Explorer source map is incoming-gestures-impact.md.
Ruling: Retain hosted attachment GPU proof on fcf49bf5d after the input/GUI-only merge because the rendering sources and headless fixture/backend creation paths are unchanged. Re-run the combined full build/suite, focused real-Metal attachment proof and private packaging. Cost if wrong: an indirect untraced Windowing initialization interaction would need a fresh hosted matrix. Exact source comparison and the traced absence of AppWindow from these headless paths bound that risk.

Combined revision is f037fd0fc0448771c9484b1b08111aaf9e5ba6e0, staged 20.37.0, pushed. Reconciled build session 67617 writes integration-gestures-build.log. Guard chain session 97984. Hosted source-identity ruling retained run 38392043530 on unchanged rendering revision fcf49bf5d. Next: full suite and focused Metal on f037, then fresh pack proof and integration after hosted results.

Hosted D3D11 job FAILED with 14 known baseline rows (#1382, #1316, #1157). All 8 attachment GPU cases PASSED in downloaded TRX, no skips. Full details and prior restrictions are in hosted-baseline-failures.md. No shaders, tolerances, filters or baselines changed. #1157 further diagnostics/waiver are not authorized by its existing workstream. Combined f037 build and whole-tree guards passed, full solution suite now running in integration-gestures-suite.log.

Combined f037fd0fc full solution suite PASSED, exit 0, about 336 seconds. Full summary counts are in integration-gestures-suite.log. Focused strict Metal proof now running. New current main is 978ca5102 with foliage motion work. Do not claim f037 contains it or reuse older render proof across that source change. Further reconciliation waits for the current baseline gate disposition to avoid redundant full suites while main changes.

Known proper #1382 repair delegated separately to /root/windows_support_cost in /Users/antonio/KhaozEngine/.worktrees/windows-support-cost, fix/windows-support-cost-artifact, base 978ca5102. No shadow investigation or waiver authorized.

Combined f037 suite totals: 37 project summaries, 31031 passed, 0 failed, 1361 conditional skips. Strict local Metal attachment rerun PASSED, exit 0, 8 passed with zero skips, integration-gestures-metal.log. Private f037 pack is running in session 66602.

Private f037 packaging PASSED, exit 0, 212 files (107 packages, 105 symbol packages). Independently checked both attachment DLL hashes against the tested build, nuspec repository commit and version, core dependency absence, renderer core/Render3D edges and both umbrella edges.

Downloaded the still-active primary Vulkan job log. Its eight RigidAttachmentSetGoldenTests cases all explicitly PASSED between 12:23:15Z and 12:23:18Z. All three backend feature cases now have 8/8 proof. The primary Vulkan job remains active and the full Windows job is red, so the full-matrix gate is not green. Full partial log is hosted-vulkan.log. No new GPU dispatch.

Durable checkpoint pushed: engine docs 754377a9c, game status docs ae82a915. Engine implementation/render verification head remains f037fd0fc. Windows #1382 repair 0898a4310 is reviewed clean, independently verified 25/25 and pushed to fix/windows-support-cost-artifact. Separate full local verification is now running there, full-build session 45197. A proof-only worktree/branch fix/windows-support-cost-proof at the same base is preparing one finite native Windows 25-case workflow. Never merge its ci.yml replacement. Primary Vulkan still active. Latest downloaded log shows TemporalFollowPhase cases taking 6m48s and 20m36s, so lack of frequent completed rows alone does not prove a hang.

Separate #1382 repair full verification passed at 0898a4310: Release build zero warnings/errors, full suite 30969 passed, zero failures, 1368 conditional skips across 36 projects. One native Windows run is active at https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38405463669 on proof-only head 02aca727b350a5d2c3050b84a021752fa56d6189. Source guard proves all non-workflow paths equal fix 0898a4310. Do not redispatch. After Windows proof, integrate only the reviewed fix branch, never the proof workflow.

#1382 is resolved and closed. Fix 0898a431058bb3d998f2c65daddf6512c01fb3e3 fast-forwarded to main and pushed. Native Windows proof run 38405463669 PASSED, artifact 11818552147 SHA256 4441e9b4a0bd5075a969c745938278c390616a50371bb1a149c7639497809169, independently checked all25pass0skip including original5. Review and full local checks passed. The separate proof-only branch remains unmerged. Repair audit copied with hash verification to /Users/antonio/KhaozEngine/.superpowers/finished/windows-support-cost-2026-10-14 before normal merged-branch cleanup.

Owner scope question is pending: wait for existing #1157 workstream (recommended) or take over its bounded design-only investigation. No approval to extend that investigation has arrived. Continue neither shader/GPU diagnosis nor a baseline waiver from silence.

Hosted run 38392043530 is terminal FAILURE because Windows failed. Primary Vulkan job 115278006347 completed SUCCESS at 2026-10-13T14:04:18Z. Final downloaded log independently confirms all eight attachment GPU cases passed. Metal and Vulkan sync also passed. Sync annotation is producer-backed: the INFO VulkanValidationLogHost line is present in the retained sync log. No new attachment failure, no rerun or waiver. Windows failure remains the existing 14 rows from the older tested source, five of which are now repaired by main commit 0898a4310 (#1382 closed). Remaining #1316/#1157 gates and pending owner scope choice are unchanged.

Final hosted-result checkpoint eb7f055f1 is committed and pushed. Staged dash/prose/size/instruction guards, doc-version checks and diff inspection passed. All hosted jobs are terminal. No active verification command remains. The task still waits for the owner scope decision on #1157, current-main reconciliation and affected verification before engine integration. No release or adoption authorized.

Owner decision, 2026-10-14: wait for the existing #1157 workstream. Stone armour stays at its integration gate. Do not take over the shadow investigation, launch further GPU experiments, change shaders/tolerances or waive the gate. The prior scope question is answered. Resume integration after dependency resolution and verify current main, tags and relevant proof first. No release or game adoption approval is implied.

## Integration resumed, owner decision 2026-10-14

The owner supersedes the earlier wait for #1157 with the baseline gate. Main and released v20.36.0
fail the same Windows D3D11 rows in run 38373207663, and #1157 has had no work since 7 October.
On the exact reconciled head, push ci and cross-platform-gpu must be green. One hosted full dispatch
using legs=all,bake=false,tier=push must pass all eight attachment GPU cases on Metal, D3D11 and
Vulkan, and pass Metal, Vulkan and Vulkan synchronization validation. Windows may fail only the
eight #1316 MapDoc path-fixture rows and the #1157 point-shadow boundary assertion, proven from
downloaded TRX. Any other failure is a blocker to investigate and fix properly.

#1157 and #1316 stay with their existing workstreams. No shadow/shader/tolerance changes, #1316
repair or proof-only workflow commits are authorized for this integration. No tag or release.

Pre-write guard confirmed clean feature/rigid-attachment-sets at eb7f055f1, equal to origin. Fetched
main and origin/main both equal ee6016aae. Latest tag v20.37.0, both sides declare released 20.37.0.
Reconciliation has only CHANGELOG and INDEX textual conflicts. Preserve all released main history
and both index entries. Source auto-merge preserves foliage and attachment changes. Stage 20.38.0
for the additive capability. Fresh local and exact-head hosted proofs plus a Codex merge review follow.

Reconciled merge commit d18bb66c2280a70b93b40b9ee7525d97b2fd2905 contains ee6016aae. Released
20.37.0 changelog history is byte-identical to main, workflow tree is identical to main, and only the
attachment surface-map extraction and qualified XML links differ in Scene3D. No shadow source edit.
Release build exit 0, zero warnings/errors. Focused CPU proof exit 0, 112 core plus 47 renderer tests,
no skips. Whole-tree dashes/prose/size/instruction/doc guards and diff check passed.

Format sweep found one WHITESPACE row at BoundRigidAttachmentSet.cs line 67, where two record
initializer fields shared a line. Split the fields onto separate lines. Token-normalized source is
identical, focused format verification exit 0. Full solution suite is still running on the same
behavioral source, with no reported failures. Rebuild the affected render project before strict Metal
and package proof. No test loop or new implementation dispatch.

Owner trigger amendment, 2026-10-14. Feature-branch push checks are impossible under the unchanged
workflow triggers. Open a draft PR to main and require green ci and PR-tier cross-platform-gpu on
the exact head intended for fast-forward. It must contain current main so the PR merge ref has the
same tree. Main movement requires reconciliation, push and fresh head checks. A separate single
full hosted cross-platform-gpu dispatch tests that same head under the baseline rules. After
fast-forward and push, actual main push ci and cross-platform-gpu must pass. Report a failure, never
revert or force without asking. Confirm the PR is merged, or close it with one line naming main SHA.

Local source be9889d4fa34728e3b3ab63cc8e89aa5ed442ef2. Full suite exit 0, 37 summaries, 31573 passed,
0 failed, 1374 conditional skips. Affected render rebuild exit 0, zero warnings/errors. Strict Metal
exit 0, eight passed with no skips, Apple M2 Max through MTLDebugDevice with API validation active.
Local Metal TRX SHA256 7bd4979285c9ae6009e06265f0c3c2f45be7e1b886660726cd3eebc077231032.
Inspected rest/posed, casting/omitted-shadow and posed-outline captures. Private pack exit 0,
107 nupkg plus 105 snupkg. Package proof exit 0. All nuspec repository commits equal be9889d4f and
all engine dependency versions are 20.38.0. Both attachment DLLs match the tested build. Core has
no dependencies, renderer exactly Attachments and Render3D, both umbrella edges include renderer.
Core DLL SHA256 821c9368f3dd91203af3b32b2d88dba0d42265709db5508dac4b0c5b33064bf7.
Renderer DLL SHA256 4ca656d53f2fb22d7865d0172fda8674309bdd12b83fef6ee7b074e4d24a8265.
Logs integration-20261014-{build,focused,suite,render-build,metal,pack,format-fix}.log and
package-proof JSON retain evidence. No code changes after these proofs are planned. Exact-head
hosted baseline gate and one Codex merge-resolution review are next. No tag or release.

## Exact-head hosted gate and integration, 2026-10-14

Gated head a80fab6c4caa1de66cb8d643e9ca998a567e993d. Draft PR #1393 to main registered with T3.
PR merge tree equals head tree eccb02ce8429d5c3aab5ec5a61b1cf52b3126398. PR ci 38461567233, PR GPU
38461566855 and PR Windows-console 38461566879 all succeeded. Full dispatch 38461600502 ran once
using legs=all,bake=false,tier=push on the same head. All 24 attachment cases passed without skips.
Vulkan full 115517954152 succeeded, 37 project summaries, zero failed rows. Sync 115517953624
succeeded, with the actual armed validation producer and zero errors.

Owner accepted only two additional non-attachment rows after bounded static attribution, with no
new runs or source fixes. Metal WarmSignalResolveAllocatesNothing(Mesh), 7872 bytes, belongs to
#1394 in #1391 family. Fixture and reached Locomotion/Physics/Bepu path are unchanged from main,
with no changed Render3D/attachment type. Windows HTTPS endpoint test's first /admin/online GET
at line 164 timed out at 20 seconds, recorded in #1396. Numeric tile rig has no asset loader, renderer,
attachment, physicsFactory or consumer tick hooks. Concrete engine handler returns publishedOnline.
Its TLS progress and timeout mechanism remain unproven. Earlier retained run 38392043530 TRX
proves this exact row passed in 0.1387199 seconds. Issue comments retain both attributions and hashes.

Allowed set is eight Windows #1316 path fixtures, Windows #1157 point-shadow boundary, exactly
one Metal #1394 Mesh allocation row and exactly one Windows #1396 HTTPS row. Nothing else is
automatically admitted. Any further row requires evidence, static attribution and owner ruling.
Windows final TRX verifies exactly ten allowed failures, eight attachment passes and no aborted/error/
runner-timeout counters. Metal actual suite log verifies one allowed failure and eight attachment
passes. The workflow emits TRX only for Windows, detailed suite logs for Metal/Vulkan. No TRX data
or hash was invented for those two backends. Full workflow conclusion is failure under this accepted
baseline gate. Actual main push checks still require green.

Fresh Codex merge reviewer /root/attachment_merge_review approved with no findings. Parent verified
source preservation claims, both saved diffs, released history identity, workflow equality, proof-only
commit absence and the whitespace correction. Explorer /root/metal_row_attribution supplied bounded
static call/type maps for the two additional rows. Parent independently verified critical constructors,
handlers and project edges. No implementation redispatch, test loop, shader/tolerance edit or rerun.

Final fetch found main/origin ee6016aae, released version 20.37.0 and free 20.38.0. Root main and
feature trees were clean. Main fast-forwarded and pushed to exact a80fab6c4. PR #1393 is confirmed
MERGED at that exact commit. Actual main push ci 38473512217, GPU 38473512218 and Windows console
38473512305 are active. Shared main packaging is active through build-slot heavy. No tag or release.

Windows ZIP 11849231951 SHA256 f4c064afa31b72f0c229a5f0bb96adb072e1cef04c5d8e8a92fd095df858c791.
Windows Render TRX SHA256 b33801e825350c47adb09d830707c1fbf2e9b2d04c0dd8733558f2133462d50e.
Windows Server TRX SHA256 e366bd16b6ac881594585e7ca35de0a6c4751ede1ebb7920051b81dc73874130.
Metal ZIP 11847334337 SHA256 edf77e3805e8664feac9b1d5e5c8d6361991203e78602ac26b74d5ab4f9272e8.
Metal raw suite log SHA256 0c7b78b44cc26644c570f26eb18bf7eef03506f3f5100d2909bb27aed01ef011.
Vulkan ZIP 11852652775 SHA256 d1d36ac61bc49dcd1aeb6f661c10a8506782429aa9893e30fafa0e701ecb2459.
Vulkan raw suite log SHA256 c61eb9edbc5f60973be9eec0022e22c690ea08abaf7bb4e3cc77ecec6906d52d.
Sync ZIP 11846691378 SHA256 5e60093e64bea1ffeb9f8ed1133fdc989de2ef0386ded15416fb9f218c220642.
All downloaded current ZIP digests match GitHub metadata.

Pending documentation edits record completed hosted proof and the exact integration, while the
feature HEAD remains a80fab6c4 for the required main push gates. Commit this audit only after their
terminal results are recorded. Do not dispatch another full workflow. No tag or release.

Owner release hold, 2026-10-14. Exact v20.38.0 at a80fab6c4 is approved for a separate engine-rooted
release chat, thread mcp:c5decd51-706e-4ac5-ac59-ad69e2e5255b. This lane remains no-tag/no-release.
Push nothing further to main before that chat reports the tag. Completion ledger/design edits and
audit preservation stay on feature/rigid-attachment-sets or held, and land only after that report.

Shared root main pack completed exit 0, 429.936 seconds, refreshed 212 files (107 nupkg, 105 snupkg)
for 20.38.0. Its exact staging directory .local-feed.pack.n8A6mu is gone. Root clean at a80fab6c4.
The orchestrator and release chat confirmed root availability, with no active pack/build from this
lane. Main package proof exit 0, all 107 repository stamps equal a80fab6c4, engine edges 20.38.0,
core has no direct dependencies, renderer exactly core/Render3D, both umbrella references present.
Main core DLL SHA256 9424186f7656c18cc5a277d6af171f1caa957617b956a2a1fcd89645f3bd6536.
Main renderer DLL SHA256 69d0933b4ac624d14541e66fb7b7a4d5c1cc24795585091130482a7aa1aea3d7.
Actual main ci 38473512217 and Windows console 38473512305 succeeded. Push GPU 38473512218 has
D3D11/Vulkan success and Metal active. Do not move main or rerun a gate while it is pending.

Actual main push gates completed. ci 38473512217, cross-platform-gpu 38473512218 and Windows
console 38473512305 all terminal SUCCESS at exact a80fab6c4, independently checked event=push
and exact head SHA from saved JSON. Release chat and Grimhollow orchestrator notified directly.
Root main remains held. Audit commit is feature-only until the release chat reports v20.38.0.
Design checkpoint and docs index now record successful integration and all amended gate evidence.
Structured audit docs/verification/2026-10-14-rigid-attachment-sets-integration.json preserves
conclusions, full allowed row mapping, package proof and hashes. This lane created no tag/release.

Broad informational feed scan completed exit 0, 7 staged versions, 0 unsafe, 47 released and 9
drifted historical versions. Those exact nine older versions are already tracked by open #1211.
No duplicate issue or repair. Current 20.38.0 alone independently passed all 107 repository stamps,
DLL/dependency proof and staged-on-origin-main checks. This is not a release blocker for a80fab6c4.
