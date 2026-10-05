# Engine baseline whitespace cleanup

Owner approved this separate cleanup as OA10 on 2026-10-05. Implements #1308, unblocks R1.
Use subagent-driven development with one serial mechanical implementer and one fresh scoped reviewer.

## Contract and scope

Worktree /Users/antonio/KhaozEngine/.worktrees/wa-format-baseline, branch fix/wa-format-baseline.
Base de78df336571d9a5db97b7b9b4f07c842f11556b, fetched engine main. No production behavior change,
version bump, changelog edit, release tag, shared-feed pack, or other worktree writes.
Only the 273 source paths in the committed inventory are allowed:
../wa-r1-document-identity/docs/superpowers/plans/proofs/2026-10-05-world-authoring-r1-full-format-baseline.json.
Read the files_with_counts keys. They are all unchanged from this base. R1's five repaired MapDoc
files are deliberately outside this cleanup. Their format diagnostics can remain on this branch until
the controller merges the cleanup into R1. No full baseline suite or whole-solution format proof here.

## Task 1: Apply and prove the exact whitespace cleanup

1. Guard pwd, branch, HEAD, clean status and inventory source SHA before edits. Read AGENTS.md.
   Verify each inventory path equals its content at base. Preserve unrelated files.
2. Create the gitignored local-feed directory. All restore, formatter, tests and guard processes
   use /tmp/grimhollow-orch/slot-run.sh serially. Restore the solution once if project assets are absent.
   A slot exit 75 ran no target. A bounded slot-only retry is allowed, never a test loop.
3. The existing 3530-diagnostic full-format log is the red proof, no duplicate failing run:
   /tmp/grimhollow-orch/logs/wa-r1-full-20261005/02-format.log.
4. Run dotnet format whitespace KhaozEngine.slnx --no-restore --include followed by the exact
   inventory paths. Build the argument vector programmatically to avoid shell expansion and length
   mistakes. Do not run the general semantic formatter or format files outside the allowlist.
5. Inspect every changed path. Prove no path outside the allowlist changed, no file was added/deleted,
   and every changed source is identical to base after removal of ASCII whitespace. Also use
   git diff --ignore-all-space --ignore-blank-lines --exit-code on those paths. Do not rely on these
   alone for literal safety: check token/literal/comment content, especially raw/multiline strings
   and preprocessor directives. Any content-affecting change is a blocker, not an approved repair.
   No dependency, project, editorconfig, build property or test-expectation changes.
6. Run the same formatter with --verify-no-changes and the identical include list through the slot.
   Require exit 0. Run the five repository guards serially through one slot hold. OA11 now approves exactly the five baseline values recorded in Outcome below. No other ratchet
   change or exemption is approved. Report any guard failure without widening this task.
7. Commit explicit changed paths with subject style fix(format): normalize baseline whitespace.
   No attribution. Do not push, merge or close issues. No helper agents, full suite, stress or client.
8. Write an ignored report in .superpowers/sdd/2026-10-05-engine-format-baseline-cleanup/task-1-report.md.
   Return DONE or a concrete blocker, exact SHA, path count, command/exit/log evidence and proof method.
   Save the list of changed files and whitespace/literal checks in that same SDD directory.

## Controller verification and integration

Controller independently inspects diff, exact path membership, non-whitespace equality and actual exits,
pushes the verified commit, and requests one fresh scoped review. Review cannot run another broad audit.
Merge the accepted cleanup into the R1 worktree, not into main first. Preserve both histories.
Then R1 runs full Release build, full solution format verification, the non-LiveSocket full suite and
required guards serially. A new successful run is justified by the merged source changes.
No main integration occurs until the combined tree is green. Follow the existing engine finishing and
pack ritual, with owner-only release tags. No Grimhollow engine pin change.

## Outcome

- OA10 approves the 273-file whitespace-only scope and separate worktree.
- Created from current engine main de78df336. No implementation or new check has run yet.


### Partial result and exact baseline adjustment proposal, 2026-10-05

- Worker returned partial with a concrete KESIZE blocker. Pushed 77ab2ec6e modifies 268 of 273
  allowed paths. Controller independently verified the path set and zero non-whitespace differences,
  inspected the Roslyn comparator and actual slot exits, and checked the held patch applies cleanly.
- All 273 formatted files passed token, literal, comment/directive, round-trip and parse-error-count
  preservation checks. 627,175 tokens include 35,327 literals and 35 raw/verbatim strings. Only
  leading indentation on DocumentationCommentExteriorTrivia is normalized, with the marker preserved.
  This corrected the first helper check, which failed solely on doc-comment indentation.
- Included-file formatter verification passed for all 273 formatted files. Five files were held
  back after KESIZE rejected their formatter-added line splits. The 268 committed files retain those
  exact verified bytes. File-size recheck then passed. Other four repository guards passed.
- Git's whitespace-insensitive line diff remains nonzero when formatting splits or joins lines.
  Its raw result is recorded, not claimed green. Byte-stripped equality and token/trivia comparison
  are the preservation evidence. No new full build or suite has run yet.
- Five requested baseline adjustments are 2115 to 2141 (EditorCommandsTests), 1793 to 1808
  (EditorToolTests), 4415 to 4431 (MapEditorSceneTests), 1601 to 1632 (GoldenSnapshotTests), and
  1296 to 1300 (Room2DGui). Total growth is 92 formatter-generated lines, with no added behavior.
- AGENTS.md explicitly requires owner approval for baseline growth. OA10 and the brief did not
  authorize ratchet changes. No baseline was changed and no guard was bypassed.
- Exact held source patch, proposed five-entry ratchet patch and proof hashes are under
  proofs/2026-10-05-format-baseline. Proposed ratchet patch is review material only, not applied.
- Recommend owner approval of these exact five baseline updates. After approval, finish the held
  files, verify the included formatter and guards, obtain one fresh scoped review, merge into R1,
  and run combined full checks. No main merge, pack, tag or game pin change has occurred.


### OA11, exact formatter-only file-size baselines, 2026-10-06

Owner answered "yes" to the five explicit KESIZE baseline increases needed to retain the formatter's
line splits. Approval is limited to these .filesize-baseline values and the already-proven held
whitespace patch. It does not approve behavior changes, other baseline growth, guard bypasses or tags.

| File | Old | Approved |
| --- | ---: | ---: |
| KhaozEngine.MapEditor.Tests/MapEditor/EditorCommandsTests.cs | 2115 | 2141 |
| KhaozEngine.MapEditor.Tests/MapEditor/EditorToolTests.cs | 1793 | 1808 |
| KhaozEngine.MapEditor.Tests/MapEditor/MapEditorSceneTests.cs | 4415 | 4431 |
| KhaozEngine.Render.Tests/Gpu/GoldenSnapshotTests.cs | 1601 | 1632 |
| KhaozEngine.Showcase/Room2DGui.cs | 1296 | 1300 |

Apply the exact proposed-ratchet.json and held-whitespace.json patch payloads preserved in cleanup
commit 8a9130d41. Verify all 273 formatted paths, token/literal preservation and required guards.
A fresh scoped review and combined R1 full verification still precede main integration.


### OA11 completion verified, cleanup review active, 2026-10-06

- Completion commit da6f46af192745016bb7feaa538dc2960b5a6c46 is verified and pushed. It changes exactly
  the five held source files and the five approved .filesize-baseline values. Previous 268 source
  files are unchanged. Cleanup now covers all 273 allowlisted files. Tree was clean.
- Controller verified every source against de78df336 after removing ASCII whitespace, every fresh
  base-snapshot file against git, exact baseline patch equivalence and each approved line count.
  Actual final token proof has zero mismatches, round-trip failures or error-count deltas across
  627,175 tokens, including 35,327 literal tokens and 35 raw/verbatim strings.
- Final all-273 included-file formatter and all five repository guards exit 0 through the shared
  slot. Formatter still emits the generic workspace-loading warning. Full combined R1 build/format/
  suite are pending. No guard bypass, extra baseline growth or behavior change was introduced.
- Proof/log hashes are cleanup docs/superpowers/plans/proofs/2026-10-05-format-baseline/oa11-proof.json.
  Logs are /tmp/grimhollow-orch/logs/wa-format-baseline-oa11-20261006. Report is cleanup SDD
  task-1-oa11-report.md. Historical partial report remains task-1-report.md.
- One fresh Gauge-selected Claude Max 20x Opus 5.5 high reviewer is active, task
  `node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-format-baseline-review1-20261006`.
  Cleanup SDD has review-1-brief.md, review-1-dispatch.json and review-3c15218a7..da6f46af1.diff.
  Review covers the complete 273-file transform, proof method and exact OA11 baselines, with no broad
  engine audit or test rerun. Root will persist its final report and verify any findings.
- Next is accepted cleanup merge into R1, current-main reconciliation and required full combined
  verification. Main, pack, tags and game adoption remain unchanged. No owner action is needed now.
