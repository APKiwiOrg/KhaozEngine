# R1 release resize-test repair

Scope: engine #1309, the existing hosted release-test scheduling gap. No runtime behavior change.
Worktree /Users/antonio/KhaozEngine/.worktrees/wa-r1-ci-resize, branch fix/wa-r1-ci-resize.
Base is released v20.27.0/a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af.
Use one implementation worker and one fresh scoped reviewer. Main integration belongs to controller.

## Evidence

Tag workflow 37369358693 attempt 2 built successfully and failed only
D3D11ThreadingContractTests.AConcurrentResize_RacingAPresent_AppliesWholeAndOnlyAtTheBoundary.
The final assertion at line 404 reports no queued resize was applied. The test signals queueing before
its first QueueResize and lets the parent finish 400 presents before stopping the producer. Starting
a thread does not prove it produced a request. A coalesced size equal to current size is another
vacuous-progress possibility. The test file is unchanged from pre-R1 main de78df336.

Full failure log: /tmp/grimhollow-orch/logs/wa-r1-combined-20261006/09-ci-attempt2-failed.log.
The first release attempt failed to acquire a hosted runner and is a distinct infrastructure failure.
No further unchanged CI retry is planned. Never retag v20.27.0 or repack its released bytes from new HEAD.

## Task 1: Deterministic resize/present interleaving

Own KhaozEngine.Render.Tests/Gpu/D3D11ThreadingContractTests.cs and
KhaozEngine.Render.Tests/Gpu/FakeD3D11SwapchainSurface.cs only. A cohesive test-only helper may be added
if needed, but no production source, package/version, workflow or assertion-threshold edits.

1. Guard path/branch/HEAD/status. Inspect the exact failing test, fake surface and shipped
   D3D11Swapchain.QueueResize/Present/ApplyPendingResize code before designing the schedule.
2. Replace scheduler-luck progress with a bounded controlled interleaving. A small optional hook on the
   fake native Present can pause it while a foreign producer queues known non-current sizes. Release
   that boundary only after observing the producer's successful work. Prove no native resize occurs
   on the queueing thread or before the present boundary.
3. Preserve meaningful submit-lock ownership, whole width/height pairing, last-request coalescing and
   Present -> ReleaseAttachments -> ResizeBuffers -> CreateAttachments order checks. Guarantee a
   nonzero actual resize and assert expected progress, not merely a thread-start notification.
4. Use finite blocking coordination and diagnostic timeouts, with cleanup that releases gates and
   joins the worker even when assertions fail. No sleep-based synchronization, busy loops, spinners,
   extra CPU load or longer stress window. Do not suppress, skip or weaken the test.
5. Keep the independent update/submit test unchanged. Reconcile comments that described the old
   resize stress window or claimed historical mutation proofs that do not apply to the new fixture.
   The hosted failure is the red evidence. A single controlled local repro is optional if it adds
   evidence, not a repeat-until-failing loop or a test of a copy of the implementation.
6. All restore/build/test/format/guard targets go serially through /tmp/grimhollow-orch/slot-run.sh.
   Restore only as needed in this fresh worktree. Run one focused D3D11 threading/swapchain selection
   that covers the helper callers, changed-file formatting and required guards. No full solution
   suite yet. No hosted stress workflow without separate owner permission.
7. Inspect final diff, commit explicit paths with area(scope): summary, no attribution, em/en dashes
   or prose semicolons. Do not push, merge, tag, pack, edit issues or touch other worktrees.
8. Write .superpowers/sdd/2026-10-06-r1-release-resize-test-repair/task-1-report.md with exact commits,
   schedule explanation, retained failure detectors, commands/exits/logs and remaining limitations.

## Controller finish

Independently verify the commit, coordination correctness and actual exit evidence. Push the verified
work, obtain a fresh scoped review and run appropriate combined full checks after reconciliation.
A publication-repair version/tag decision follows the verified result. Any new tag requires the owner.
Neither this plan nor the repair worker may overwrite v20.27.0 or its matching local package bytes.

## Outcome

- Release attempt 2 failure and scheduling evidence are filed as #1309.
- Separate repair worktree created from current main and v20.27.0, with no source edits yet.
- R2 design continues independently in the planning worktree and does no engine builds/tests.
