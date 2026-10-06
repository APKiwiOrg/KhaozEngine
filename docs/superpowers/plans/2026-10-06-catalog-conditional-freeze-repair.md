# Catalog Conditional Freeze Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close catalog #1271 cross-version marker interference with an atomic guarded row freeze and a release conditioned on the publisher's recorded base.

**Architecture:** Add `IContentConditionalDraftFreeze` beside the existing authoring and text companions. All three stores implement both operations atomically, and both pipelines require the capability before acting. The text commit keeps its complete-draft confirmation, and the runner retains its existing contention classification.

**Tech Stack:** C#, .NET, xUnit, in-memory catalog, Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, existing normal GitHub CI.

**Spec:** [Approved catalog conditional freeze and release design](../../design/CATALOG-CONDITIONAL-FREEZE-RELEASE-DESIGN-2026-10-06.md), OA14 and OA15 in [DECISIONS](/Users/antonio/Grimhollow/.worktrees/world-authoring/docs/superpowers/programs/world-authoring/DECISIONS.md). OA15 approves planning and the next-free-minor direction, currently `20.28.0`. Owner approval of this full plan and production execution are still pending. The execution method is serial SDD under [HANDOFF](/Users/antonio/Grimhollow/.worktrees/world-authoring/docs/superpowers/programs/world-authoring/HANDOFF.md).

## Global Constraints

- Execution is blocked until root independently verifies this plan and records OWNER plan approval. No production implementation or tag is authorized by this planning commit.
- Work only in `/Users/antonio/KhaozEngine/.worktrees/wa-catalog-freeze` on `fix/wa-catalog-freeze`, with explicit per-task base SHA and owned paths. Workers do not push, merge, tag, clean up or spawn helpers.
- Source baseline is `ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc`. Planning started at `cf0ed48a2fed34af4d06f016177d0554d8ade9c1`, whose four later commits change only `docs/INDEX.md` and the approved design.
- Task 0 uses existing APIs only and owns the single red-first baseline reproduction. An intermediate red commit is allowed only with its named failures, unchanged source proof and owned-red ledger. No shipping or integration until every regression is green.
- The companion extends `IContentAuthoringStore` and declares exactly `Task<ContentDraft> FreezeDraftForBaseAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)` and `Task<bool> ReleaseDraftFreezeForBaseAsync(int frozenForBaseVersion, CancellationToken cancellationToken = default)`.
- New row freeze runs no stale sweep. Active comparison, draft/row-work check, representability, marker write and returned complete snapshot share one gate or transaction, in that order.
- Refused new row freeze writes neither marker nor timestamp, including an existing stale marker with a moved expected base. A successful call replaces a stale marker directly. Repeating the same marker leaves its timestamp alone.
- Negative bases throw `ArgumentOutOfRangeException`. Base `0` is valid. Missing or zero-row-work draft refuses `no-open-draft`. Unrepresentable mixed work or text-bearing fork refuses `text-unrepresented`.
- Conditional release compares only the stored marker to the recorded base, not the active version. It returns `true` only on clear, stamps only on clear, changes no edits/text/introductions/opener/base/audit and honors cancellation.
- Both row pipeline paths use the new operation and its returned draft. Text freezes remain `FreezeChangesAsync`, with their existing separate stale maintenance documented and unchanged.
- Capture the exact expected base before calling freeze so a lost acknowledgement is released. Cleanup uses `CancellationToken.None`, with publish swallowing release faults and the runner retaining its current fault behavior.
- Keep legacy `FreezeDraftAsync` overwrite and `ClearDraftFreezeAsync` unconditional behavior for explicit recovery/setup. No engine pipeline calls either after Task 3.
- Preserve whole-text-commit marker and `SameDraft` checks, stale-plan/refusal classification, `TryDiscardChangesAsync`, ledger/version/row-id/family rules and operator edits. No `text-state-mismatch` blanket retry or `IsContention` change.
- No owner token, schema/index/migration change, row-commit-confirmation expansion, R2, game wrapper, consumer pin or navigation bake.
- Incapable pending Apply stops `Unsupported` with `KECU0017` after pending-work detection and before the draft gate, adoption included. Preview keeps its result and adds one `KECU0017` note. `KECU0001`, `KECU0002`, `KECU0003` and `KECU0012` precedence stays intact.
- Every declaring decorator requires a capable inner store at construction and forwards both methods. Winning race participants expose the entire text companion and build the commit over themselves, with actual text-route counters asserted.
- Force each interleaving once with asynchronous one-shot gates outside every store gate, transaction and lease. No sleeps, spinners, stress or retry loop. Do not expand or repeat the existing statistical concurrency suite.
- All heavy and documentation guard commands run serially through `/tmp/grimhollow-orch/slot-run.sh`. Exit `75` means no target ran and is not evidence. SQL Server runs only in existing normal remote CI, never a local server setup.
- KESIZE has an 800-line cap for unlisted sources. No baseline growth or exemption without owner approval. Put new behavior in cohesive new types/partials, never an arbitrary split or suppression.
- OA15 selects a flagged custom-store runtime behavior break and migration in the next free minor. Root rechecks main/tags before bumping, folds the staged publication repair into the minor, and pairs version and matching changelog in one commit.
- Root coordinates the pivot thread before engine code becomes pinable, records the answer in Outcome/program, and owns integration. Private feed proof precedes merge. Shared-feed pack is guarded on the same verified SHA after main push. Tags always need separate owner approval.
- Follow existing issues only: [game adoption #467](https://github.com/APKiwiOrg/Grimhollow/issues/467), [same-base text-route boot liveness #1312](https://github.com/APKiwiOrg/KhaozEngine/issues/1312), and [row-only edit loss #1311](https://github.com/APKiwiOrg/KhaozEngine/issues/1311). Do not create duplicates or implement them here.
- Commit subjects use `area(scope): summary`. No em/en dashes, prose semicolons or assistant attribution. No new dependencies or cross-test-project references.

## Review Focus

1. Moved expected base with an already stale marker must refuse without maintenance writes. Task 1 `ConditionalFreeze_MovedBasePreservesStaleMarkerAndTimestamp` checks marker `0` at active `2`, expected `1`, including audit and relational timestamps.
2. An older publisher's cleanup must not clear a newer winner's marker. Task 0 T2 and T3 go red on the baseline, Task 2 T2 and Task 3 T3 go green, with real `CommitTextPublishAsync` calls counted.
3. A row-only or Grimhollow-shaped wrapper must not overwrite a newer marker, and freeze-return planning must include a gap edit. Task 0 T2p/T4/T4r, Task 2 `RowPrepare_IncludesAnEditBeforeAtomicFreeze` and Task 3 T4/T4r pin both row paths.
4. Lost freeze/commit acknowledgements, cancellation and duplicate cleanup must use the captured base and preserve later drafts. Tasks 2 and 3 lifecycle facts plus Task 0 T3o and T2r pin cleanup behavior and the accepted same-base text refusal.
5. An incapable pending Apply must refuse before any draft read or adoption write, while Preview stays useful and earlier gates win. Task 3 T5 theories assert outcomes, codes, ordered read calls and a whole-store footprint, including `AlreadySatisfied`.

---

## File responsibilities and size disposition

All paths below are relative to the engine worktree. New paths are explicitly marked. The approved design's `*Store.Freeze.cs` additions are placed in cohesive `*.ConditionalFreeze.cs` partials, which carry the new interface declaration. This changes placement only, not API or behavior.

| Task | Files | Responsibility |
| --- | --- | --- |
| 0 | New `KhaozEngine.Catalog.Tests/ConditionalFreeze/OnceGate.cs`, `KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceStore.cs`, `KhaozEngine.Catalog.Tests/ConditionalFreeze/RowFreezeRaceStore.cs`, `KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceFixture.cs`, `KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRacePackStore.cs` | One-shot sequencing, complete text/ledger forwarding and counters, row views, backend lifetime/seed/set, cancellation and pack-write observations |
| 0 | New `KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeReleasePublishTests.cs`, `KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeReleaseUpgradeTests.cs` | T2/T2p/T2r and T3/T3o/T4/T4r, same test bodies from baseline to fix |
| 1 | New `KhaozEngine.Catalog.Authoring/IContentConditionalDraftFreeze.cs` | Public two-member atomic contract |
| 1 | New `KhaozEngine.Catalog.Authoring/InMemoryContentAuthoringStore.ConditionalFreeze.cs`, `KhaozEngine.Catalog.Sqlite/SqliteContentAuthoringStore.ConditionalFreeze.cs`, `KhaozEngine.Catalog.SqlServer/SqlServerContentAuthoringStore.ConditionalFreeze.cs` | Store-specific lifecycle under existing gate/transaction, interface on each partial declaration |
| 1 | `KhaozEngine.Catalog.Authoring/IContentAuthoringStore.cs` | Legacy-member XML warnings, signatures/behavior unchanged |
| 1 | New `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.ConditionalFreeze.cs`, `KhaozEngine.Server.Tests/Catalog/ContentAuthoringTextStoreConformance.ConditionalFreeze.cs` | T1 and T1t virtual facts extending existing freeze/text conformance suites |
| 1 | `KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringStoreConformanceTests.cs`, `KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringTextConformanceTests.cs` | One-line gated overrides for every new virtual fact |
| 1 | New `KhaozEngine.Catalog.Tests/Sqlite/SqliteCatalogRowTimestampTests.ConditionalFreeze.cs`, `KhaozEngine.Server.Tests/Catalog/SqlServer/SqlServerCatalogRowTimestampTests.ConditionalFreeze.cs` | Marker/timestamp transitions using existing partial fixture clocks and SQL readers |
| 2 | `KhaozEngine.Catalog.Authoring/Publish/ContentPublishCommit.cs`, `KhaozEngine.Catalog.Authoring/Publish/ContentPublisher.cs`, `KhaozEngine.Catalog.Authoring/ContentAuthoringException.cs` | Publish capability refusal, guarded row preparation, conditional finally and reason constant |
| 2 | New `KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeCapabilityPublishTests.cs`, `KhaozEngine.Catalog.Tests/ConditionalFreeze/LegacyFreezeStoreView.cs` | Publish/standalone Prepare refusal and atomic-return planning assertions |
| 2 | Task 0 doubles, `KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs`, `KhaozEngine.Server.Tests/Catalog/ForwardingContentAuthoringStore.cs` | Required-inner companion forwarding, including all runner race participants before publish becomes strict |
| 2 | New `KhaozEngine.Catalog.Tests/Publish/ConditionalRowOnlyStoreView.cs`, `KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs`, `KhaozEngine.Catalog.Tests/Authoring/TextLegacyCompatibilityTests.cs`, `KhaozEngine.Server.Tests/Catalog/ContentAuthoringTextStoreConformance.cs` | Capable row-only variant for legacy-route tests, preserve incapable `RowOnlyStoreView` for T5 |
| 3 | `KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeTextRoute.cs`, `KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeRun.Publish.cs`, `KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeRunner.cs`, `KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeDiagnostic.cs` | Guarded runner freeze, captured-base lifecycle, ordered capability gate and diagnostic |
| 3 | New `KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeCapabilityUpgradeTests.cs`, `KhaozEngine.Catalog.Tests/ConditionalFreeze/LegacyFreezeLedgerView.cs`, Task 0 upgrade test file | T5 runner/read precedence and lifecycle coverage |
| 3 | `KhaozEngine.Catalog.Tests/Upgrade/ContentUpgradeFreezeReleaseTests.cs`, `KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs`, `KhaozEngine.Catalog.Tests/Upgrade/TextUpgradeDoubles.cs`, `KhaozEngine.Catalog.Tests/Upgrade/DraftRaceStore.cs`, `KhaozEngine.Server.Tests/Catalog/UpgradeDraftRaceStore.cs` | Migrate fault hooks, release counters and direct doubles to the actual new runner call |
| 3 | New `KhaozEngine.Server.Tests/Catalog/ConditionalFreezeRaceStore.cs`, existing `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.UpgradeRace.cs`, `KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringStoreConformanceTests.cs` | T6 server-local text/ledger adapter and T3/T4 conformance with gated SQL Server overrides |
| 3 | `KhaozEngine.Server.Tests/Catalog/WriteRefusingAuthoringStore.cs`, `KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs`, `KhaozEngine.Catalog.Tests/Upgrade/ContentUpgradeConcurrencyTests.cs` | New write attempts counted/refused, helper inventory migration, correct replica comment only |
| 4 | `KhaozEngine.Catalog.Authoring/README.md`, `KhaozEngine.Catalog.Authoring/IContentTextAuthoringStore.cs`, `docs/USING-KHAOZENGINE.md`, `README.md`, `Directory.Build.props`, `CHANGELOG.md`, `docs/INDEX.md`, this plan | Living API/maintenance/migration/rollout text, guarded declarations, minor and matching changelog, final Outcome |
| 4 | Inspect `KhaozEngine.Server.Admin/Catalog/CatalogRefusal.cs`, `KhaozEngine.Server.Admin/README.md` | Default refusal preserves unknown reason, recovery description stays valid. Change only if the inspection finds a stale description or loses the reason |

Measured on the unchanged source baseline: publish commit/publisher 398/477 lines, runner/route/publish partial 280/158/394, old store freeze partials 214/230/231. Upgrade forwarding fixture 620, text double 170, freeze-release fixture 377, publish text double 307. Server forwarding/race/write-refusal fixtures 201/284/241. Row/text conformance 92/431, SQL Server row conformance 479, SQLite/SQL Server timestamp partial roots 496/523. None has a `.filesize-baseline` entry. New types/partials stay under 800, existing forwarding edits stay below the cap, and `.filesize-baseline` is not a planned edit. Reinspect sizes at each task base. A changed ratchet is a root gate, not permission to raise it.

## Serial execution, evidence and review procedure

Root first confirms plan approval, the pivot coordination barrier and actual branch identity. Keep the design investigation workspace `.superpowers/sdd/2026-10-06-catalog-conditional-freeze-design` separate from implementation progress. The skill creates the plan-specific execution workspace below. Owner approval of this plan is required before root dispatches Task 0's test-writing/run work, and no production changes start until its red proof is reviewed.

```bash
cd /Users/antonio/KhaozEngine/.worktrees/wa-catalog-freeze
test "$(pwd -P)" = "/Users/antonio/KhaozEngine/.worktrees/wa-catalog-freeze"
test "$(git branch --show-current)" = "fix/wa-catalog-freeze"
git rev-parse HEAD
git status --short
git worktree list
mkdir -p local-feed
mkdir -p /tmp/grimhollow-orch/catalog-conditional-freeze
bash /Users/antonio/dotfiles/memory/store/skills/subagent-driven-development/scripts/sdd-workspace docs/superpowers/plans/2026-10-06-catalog-conditional-freeze-repair.md
```

Expected identity guards exit 0. A clean status is required at dispatch, with only the controller's documented progress artifacts allowed. Verify the returned workspace's `plan-path` and any prior completion SHA before dispatching anything. Read HANDOFF if the runner is absent. Restore only absent scripts, never replace a differing live runner. The supplied runner SHA-256 is `c5210ccc108e7b6f5d5b72328db6e6fcfef972ae8ea18701e17343b4035f66f7`.

For each task, record `catalog_task_base=$(git rev-parse HEAD)` before its fresh implementer. Extract its numbered brief. Append the Global Constraints, Review Focus cases assigned to it, shared harness contracts and serial commands as needed, since `task-brief` extracts only the task section.

```bash
bash /Users/antonio/dotfiles/memory/store/skills/subagent-driven-development/scripts/task-brief docs/superpowers/plans/2026-10-06-catalog-conditional-freeze-repair.md 0
```

Use the same command with the next task's literal number, not a worker fan-out. Root calls current `gauge:assign_route` before each implementer/reviewer dispatch and uses each returned slot. Follow the program's approved model/effort rules and per-dispatch authorization, not inherited defaults. Each brief names the worktree, branch, task base, owned files, synchronous commands and `task-N-report.md`. Workers do not integrate or recruit helpers.

All verification commands below run from this worktree, synchronously, one at a time. Use the displayed log name for its first run and a fresh suffix after an actual fix. Record command, SHA, exit code, test counts, named failure and log/TRX path in the report and durable Outcome. Exit 75 requires returning to root with "nothing ran". Do not automatically retry a failed target. The slot runner waits internally, so root remains responsive through short tool yields.

After each commit, root independently checks its existence, branch ancestry, full task-base-to-head diff and actual exits. Generate a review package with the recorded base, not `HEAD~1`.

```bash
bash /Users/antonio/dotfiles/memory/store/skills/subagent-driven-development/scripts/review-package docs/superpowers/plans/2026-10-06-catalog-conditional-freeze-repair.md "$catalog_task_base" HEAD
```

A fresh task reviewer receives brief, report and review-package paths, and returns both spec compliance and quality verdicts. Reviewer does not repeat unchanged test runs. Follow the installed SDD bounded fix loop with the original implementer and scoped re-reviews. Record every ruling and unresolved finding with its cost, never silently waive a load-bearing objection. Record Task 0's expected red verdict explicitly. Task 1 and Task 2 may advance only with their own green tests and the enumerated remaining Task 0 reds, never an unexplained red. Final shipping gates require zero owned reds.

### Task 0: Reproduce both unsafe lifecycle halves on the frozen source

**Files:** Create the seven Task 0 paths from the file map. No existing source, test, project, version or workflow edits.

**Interfaces:** Consume the released `IContentAuthoringStore`, all seven `IContentTextAuthoringStore` members, `IContentUpgradeLedger`, `ContentPublishCommit`, `ContentPublisher` and `IContentIdPersistence`. Produce test-only `OnceGate` with `Task Entered`, `Task PauseAsync(CancellationToken cancellationToken = default)`, `void Resume()`, and `void Dispose()`. Produce `FreezeRaceStore(IContentAuthoringStore inner, IContentIdPersistence ids, ContentTypeRegistry registry, IPackStore packs)` (text plus ledger, commit over itself) and `RowFreezeRaceStore(IContentAuthoringStore inner, IContentIdPersistence ids, ContentTypeRegistry registry, IPackStore packs, bool forwardPublish)` (rows plus ledger, selectable self-built row commit or inner-forwarded publish). Expose `int TextCommitCalls`, `int RowCommitCalls`, `int LegacyFreezeCalls`, `int LegacyClearCalls` and `int DraftReadCalls` on adapters, read-only to tests. Produce `FreezeRaceFixture(bool sqlite)` with `ContentTypeRegistry Registry`, `IContentAuthoringStore Inner`, `IPackStore Packs`, `Task SeedAsync()`, `Task<FreezeRaceStore> TextParticipantAsync()` and `Task<RowFreezeRaceStore> RowParticipantAsync(bool forwardPublish)`. Seed initializes the catalog before use, participant factories initialize SQLite replicas with ValidateOnly, and disposal owns those replicas. Neither declares or calls the future companion. Add read-only `IReadOnlyList<int?> ReleaseBases`, `IReadOnlyList<bool?> ReleaseResults` and `IReadOnlyList<CancellationToken> ReleaseTokens`. A legacy clear records its actual token and null for the absent base argument/result. Task 2 fills the same observations from the new method's actual arguments/return, so unchanged test bodies can assert the fixed values after their deciding winner assertion.

- [ ] **Step 1: Establish the exact executable baseline before writing tests.**

```bash
git diff --exit-code ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc HEAD -- . ':(exclude)docs/**'
git diff --name-status ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc HEAD
```

First command must exit 0. Second may list only documentation, including this plan. This reconciles the spec's "on top of ca13d62d7" with later docs-only commits, without resetting, checking out old source or losing approvals. If any executable input changed, stop and report a baseline mismatch to root before a production edit. Save these outputs and actual HEAD.

- [ ] **Step 2: Build test-only harness and seven failing/characterization theories.**

`FreezeRaceFixture` owns the registry, file/pack lifetime, `IContentAuthoringStore` participants and matching id persistence. Use `PublishFixtures.Registry(Thing, Other)`, seed `old_row` value 11 as version 1. Publish race D1 updates that row to 22. D2 updates it to 33 with row edits only. Upgrade race target uses `UpgradeFixtures.Row(Thing, 1, "old_row", 11)`, `Row(Other, 1, "new_row", 22)`, `Row(Other, 2, "second_new_row", 33)` and `UpgradeFixtures.Adds` with ids `UpgradeHarness.FirstId`/`SecondId`. Build each add plan with `definition.Plan(new ContentUpgradeContext(1, await inner.ExportBundleAsync(1), registry))` and assert `Assert.Empty(plan.TextEdits)`. This pins the public input that makes the internal `CarriesText` false without accessing an internal member. In-memory participants share one store. SQLite participants use separate initialized store instances on one file and the same pack root.

`FreezeRaceStore.PublishAsync` constructs `new ContentPublishCommit(this, packs, new ContentPublisher(this, ids, registry))`. Forward every text member and ledger member, count calls at `CommitTextPublishAsync`, and count `CommitPublishAsync` separately. Use one decorator per participant. A winning commit must assert text count 1 and row count 0. Row-only T2p intentionally builds over itself and counts its row commit, while T4r intentionally forwards publish to the inner engine store, matching the game wrapper.

Use `TaskCompletionSource` with `RunContinuationsAsynchronously`. Each gate arms one matching base/upgrade/ordinal. Clear its armed state before awaiting so later legitimate runner attempts cannot re-enter it. Await an entry gate before invoking the inner call, or an exit gate after the inner await has returned and disposed its lock/transaction/lease. Store no lease in the gate. The test-only pack adapter forwards its pointer half and pruning interface as applicable, so it does not change commit construction or sweeping. Dispose/resume every outstanding gate and await participant tasks in `finally`, with a bounded `WaitAsync(TimeSpan.FromSeconds(30))` watchdog that fails loudly, never advances the scenario on time. Print participant failures/report lines through `ITestOutputHelper` before final assertions.

| Test name | Forced ordering and baseline pause | Fixed pause to add later | Named baseline failure |
| --- | --- | --- | --- |
| `T2_OlderPublishReleasePreservesNewerTextWinner` | P1 freeze at 1, commit-entry hold. P2 commits 2. D2 opens, P3 freeze at 2 and commit-entry hold. Finish P1 and both its refusal/cleanup, inspect D2, finish P3 | Same text commit entries and conditional release of 1 | P3 `text-state-mismatch` |
| `T2p_RowPublishFreezePreservesNewerTextWinner` | P1 row publish has baseline 1 and reaches entry of its first `GetOpenDraftAsync` in Prepare. P2 commits 2, row-only D2 opens, P3 freezes at 2 and holds commit. Finish P1, inspect D2, finish P3 | Entry of P1 `FreezeDraftForBaseAsync(1)`, never a separate fixed draft read | P3 `text-state-mismatch` |
| `T2r_SameBaseCancellationStillRefusesChangedTextDraft` | P1 same-base freeze, hold in a real pack `PutAsync`. P2 freezes same draft and holds text commit. Cancel P1, await cleanup, add gap_row value 77, finish P2 | Same sequence, conditional release still clears same-base marker | Green characterization on both, P2 `text-state-mismatch` |
| `T3_TwoRunnersPreserveNewerTextWinner` | B first upgrade commit-entry hold at 1. A recovers B draft, commits 2, second upgrade text commit-entry hold at 2. Resume B, hold B's first failure-resolution `GetOpenDraftAsync` after both releases. Finish A, then B | Same recovery-read gate, conditional cleanup and guarded runner row freeze | A `KECU0009`, containing `text-state-mismatch` |
| `T3o_AfterRunnerFreezePreservesNewerTextWinner` | R row plan written and legacy `FreezeDraftAsync(1)` exit hold, before separate read-back. P1 commits it as 2. Row-only D2, P2 text commit-entry hold at 2. Resume R, hold its first release exit, finish P2 then R | Exit after guarded freeze returned its own atomic draft, first conditional release exit | P2 `text-state-mismatch` |
| `T4_BeforeRunnerFreezePreservesNewerTextWinner` | R row plan written, entry of `FreezeDraftAsync(1)` hold. P1 commits 2, row-only D2, P2 text commit-entry hold at 2. Resume R, hold its first release exit, finish P2 then R | Entry of guarded row freeze, first conditional release exit | P2 `text-state-mismatch` |
| `T4r_ForwardingRowRunnerPreservesNewerTextWinner` | T4 with row-only R plus ledger and inner-forwarded publish. P1/P2 retain full text forwarding and self-built commits | Same through forwarded guarded row freeze | P2 `text-state-mismatch` |

The T3 recovery-read gate is armed only after B's failed self-built publish unwound. Assert two cleanup calls were observed before this gate signals. The release-exit gate in T3o/T4/T4r signals only after the actual first legacy clear completed. Do not mistake an old read-back gate for a fixed freeze exit.

Assertions below are the final test-body expectations, with tasks/participants from the table. Observe and log failures before asserting marker preservation, so every baseline log contains the deciding refusal rather than only a marker comparison.

```csharp
// T2/T2p and T3o/T4/T4r winning console participant.
Assert.Equal(1, winner.TextCommitCalls);
Assert.Equal(0, winner.RowCommitCalls);
Assert.Null(winnerFailure);
Assert.Equal(2, markerAfterOlderExit);
Assert.Equal(3, winnerResult!.VersionNumber);
Assert.Null(await inner.GetOpenDraftAsync());
// T2/T2p only, after the deciding winner assertion.
Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, olderFailure.Reason);

// T3, both final reports and persisted history.
Assert.Equal(2, runnerA.TextCommitCalls);
Assert.Equal(1, runnerB.TextCommitCalls);
Assert.Equal(0, runnerA.RowCommitCalls);
Assert.Equal(0, runnerB.RowCommitCalls);
Assert.True(a.Success, string.Join(" | ", a.Lines));
Assert.True(b.Success, string.Join(" | ", b.Lines));
Assert.Contains(a.Diagnostics, d => d.Code == "KECU0010");
Assert.DoesNotContain(a.Diagnostics, d => d.Code == "KECU0009");
Assert.Equal(2, b.Diagnostics.Count(d => d.Code == "KECU0011"));
Assert.Equal(new int?[] { 1, 1 }, runnerB.ReleaseBases);
Assert.Equal(new bool?[] { false, false }, runnerB.ReleaseResults);
Assert.All(runnerB.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
Assert.Equal(3, (await inner.ListVersionsAsync()).Count);
Assert.Equal(new[] { 2, 3 }, ledger.OrderBy(r => r.VersionNumber).Select(r => r.VersionNumber));
Assert.All(ledger, r => Assert.Equal(ContentUpgradeDisposition.Applied, r.Disposition));
Assert.Equal(new[] { 1, 2 }, rows.Rows.Select(r => r.Id));
Assert.Equal(new[] { "new_row", "second_new_row" }, rows.Rows.Select(r => r.Key.ToString()));
Assert.Null(await inner.GetOpenDraftAsync());

// T2r, no claim to fix the deferred same-base case.
Assert.Equal(ContentAuthoringException.TextStateMismatchReason, sameBaseFailure.Reason);
Assert.Equal(1, await inner.GetActiveVersionAsync());
Assert.Single(await inner.ListVersionsAsync());
Assert.Equal(new[] { "old_row", "gap_row" }, held.Changes.Edits.Select(e => e.Key.ToString()));
Assert.Equal(22, held.Changes.Edits[0].Fields[0].Value.Number);
Assert.Equal(77, held.Changes.Edits[1].Fields[0].Value.Number);
Assert.False(held.IsFrozen);
Assert.Equal(1, sameBaseWinner.TextCommitCalls);
```

T3o/T4/T4r use one upgrade with the committed carried identity. P1 is an ordinary console publish with no Upgrade stamp, so R ultimately records one adoption after version 3. P2's separate row-only edit updates old_row and leaves the newly added upgrade row unchanged. Assert one ledger row for that definition, adopted at version 3, no `KECU0009`, and no duplicate version. Do not drop operator edits in the later draft to make R finish.

- [ ] **Step 3: Run the baseline reproduction once through the shared slot.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t0-red" /tmp/grimhollow-orch/catalog-conditional-freeze/t0-red.log -- dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~ConditionalFreezeReleasePublishTests|FullyQualifiedName~ConditionalFreezeReleaseUpgradeTests" --logger "trx;LogFileName=t0-red.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t0-red --logger "console;verbosity=normal"
```

Expected exit 1, exactly twelve failed cases (six races times two backends) and two T2r passes. No compile failures, timeouts, representability refusal, harness route bypass or other failure qualifies. Capture T2/T2p/T3o/T4/T4r `text-state-mismatch` and T3 `KECU0009` refusal lines for each backend. If any required race passes, or T3 fails for a different reason, stop production work and report the unresolved diagnosis.

- [ ] **Step 4: Commit the owned red baseline and obtain its fresh task review.**

```bash
git add KhaozEngine.Catalog.Tests/ConditionalFreeze/OnceGate.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceStore.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/RowFreezeRaceStore.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceFixture.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRacePackStore.cs KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeReleasePublishTests.cs KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeReleaseUpgradeTests.cs
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t0-commit" /tmp/grimhollow-orch/catalog-conditional-freeze/t0-commit.log -- git commit --only -m "test(catalog): reproduce cross-version freeze interference" -- KhaozEngine.Catalog.Tests/ConditionalFreeze/OnceGate.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceStore.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/RowFreezeRaceStore.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceFixture.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRacePackStore.cs KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeReleasePublishTests.cs KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeReleaseUpgradeTests.cs
```

After commit, run the exact source safety guard below. It must exit 0.

```bash
git diff --exit-code ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc HEAD -- . ':(exclude)docs/**' ':(exclude)KhaozEngine.Catalog.Tests/ConditionalFreeze/OnceGate.cs' ':(exclude)KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceStore.cs' ':(exclude)KhaozEngine.Catalog.Tests/ConditionalFreeze/RowFreezeRaceStore.cs' ':(exclude)KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceFixture.cs' ':(exclude)KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRacePackStore.cs' ':(exclude)KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeReleasePublishTests.cs' ':(exclude)KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeReleaseUpgradeTests.cs'
```

Record the test-only SHA, red TRX, named failures and no-integration status. Reviewer checks the whole Task 0 range and actual text dispatch. No new API or production work precedes this accepted proof.

### Task 1: Add the atomic store capability and conformance

**Files:** Task 1 rows in the file map. Existing text freeze, legacy freeze/release implementations, publish and runner code remain unchanged.

**Interfaces:** Produce the exact public companion in Global Constraints. Store partials reuse `_gate`, `Protected`, `Reframe`, `RequireRowOnlyRepresentable` for memory, `_connection.EnterAsync`, `ReadActiveAsync`, `ReadDraftAsync`, `RequireRowOnlyRepresentableAsync`, `RequireDraftAsync` for SQLite, and `WriteAsync<T>`, `ReadActiveVersionAsync`, `ReadDraftAsync`, `RequireRowOnlyRepresentableAsync`, `RequireDraftAsync` for SQL Server.

- [ ] **Step 1: Write T1/T1t virtual facts and timestamp assertions using the new declared signatures.**

Name row facts `ConditionalFreeze_ValidatesBaseAndReturnsCompleteDraft`, `ConditionalFreeze_MovedBasePreservesStaleMarkerAndTimestamp`, `ConditionalRelease_ClearsOnlyMatchingBase`, `ConditionalFreeze_RejectsNegativeBaseAndEmptyDraft`. Name text facts `ConditionalFreeze_RefusesUnrepresentedTextBeforeWriting` and `ConditionalRelease_PreservesTextAndIntroductions`. Timestamp partials use `ConditionalFreeze_StampsOnlyMarkerChanges`. Each SQL Server virtual fact gets a `[CatalogSqlServerFact]` override, including both text facts in the separate text conformance class.

Seed version 1, a row draft, and compare the returned draft to `GetOpenDraftAsync` by all public fields and edit/text values, not object identity. Reuse existing fixture helpers, with a test-local `AssertDraftEqual(ContentDraft expected, ContentDraft actual)` covering base, opener/time/note, marker, ordered row values and text state. Record audit count before each operation.

```csharp
var safe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(store);
var moved = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(0));
Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, moved.Reason);
Assert.Null((await store.GetOpenDraftAsync())!.FrozenForBaseVersion);
ContentDraft frozen = await safe.FreezeDraftForBaseAsync(1);
Assert.Equal(1, frozen.FrozenForBaseVersion);
AssertDraftEqual(frozen, (await store.GetOpenDraftAsync())!);
AssertDraftEqual(frozen, await safe.FreezeDraftForBaseAsync(1));
Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(0));
Assert.True(await safe.ReleaseDraftFreezeForBaseAsync(1));
Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
Assert.Equal(auditBefore, (await store.ListAuditAsync(default, 0, 0, 1000)).Count);

// Active 2, row draft, legacy marker 0. New refusal must not sweep it.
await store.FreezeDraftAsync(0);
ContentDraft before = (await store.GetOpenDraftAsync())!;
var staleMoved = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, staleMoved.Reason);
AssertDraftEqual(before, (await store.GetOpenDraftAsync())!);
Assert.Equal(0, (await store.GetOpenDraftAsync())!.FrozenForBaseVersion);
Assert.Equal(2, (await safe.FreezeDraftForBaseAsync(2)).FrozenForBaseVersion);
Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
Assert.Equal(2, (await store.GetOpenDraftAsync())!.FrozenForBaseVersion);
await store.ClearDraftFreezeAsync();
Assert.Null((await store.GetOpenDraftAsync())!.FrozenForBaseVersion);

// Empty store first freeze supports base zero. No-draft test uses current base.
Assert.Equal(0, (await emptySafe.FreezeDraftForBaseAsync(0)).FrozenForBaseVersion);
Assert.False(await noDraftSafe.ReleaseDraftFreezeForBaseAsync(5));
Assert.Equal("no-open-draft", (await Assert.ThrowsAsync<ContentAuthoringException>(
    () => noDraftSafe.FreezeDraftForBaseAsync(currentBase))).Reason);
await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => safe.FreezeDraftForBaseAsync(-1));
await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => safe.ReleaseDraftFreezeForBaseAsync(-1));
```

Also test same-active marker 2 with expected 1, a draft containing zero rows but text work (`no-open-draft`), mixed rows/text/introductions (`text-unrepresented`), and a row fork whose active source carries text (`text-unrepresented`). Verify no marker or audit change in each refusal. Freeze a text draft through unchanged `FreezeChangesAsync`, release wrong then matching base, and compare its text intents and introductions before/after.

SQLite timestamp test reuses `ManualClock`, `T0` and `DraftTimes`. SQL Server partial uses its existing clock and `DateTimeOffset` tuple. Tick the clock explicitly, never wait for real time.

```csharp
var beforeTimes = DraftTimes(database);
clock.Tick();
await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1)); // active 2, stale marker 0
Assert.Equal(beforeTimes, DraftTimes(database));
var replacedAt = clock.Tick();
await safe.FreezeDraftForBaseAsync(2);
Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
clock.Tick();
await safe.FreezeDraftForBaseAsync(2);
Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
clock.Tick();
Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
var releasedAt = clock.Tick();
Assert.True(await safe.ReleaseDraftFreezeForBaseAsync(2));
Assert.Equal((beforeTimes.Item1, releasedAt), DraftTimes(database));
clock.Tick();
Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(2));
Assert.Equal((beforeTimes.Item1, releasedAt), DraftTimes(database));
```

- [ ] **Step 2: Run the new contracts red.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t1-red" /tmp/grimhollow-orch/catalog-conditional-freeze/t1-red.log -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~ConditionalFreeze_|FullyQualifiedName~ConditionalRelease_" --logger "trx;LogFileName=t1-red.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t1-red
```

Expected nonzero from the not-yet-defined companion. This is the contract task's compile red, not substitute evidence for Task 0's runtime red.

- [ ] **Step 3: Implement both members in the three new store partials and XML contract.**

Declare `IContentConditionalDraftFreeze` on each new partial. Memory checks cancellation before entering its gate and before the single mutation, validates active and row work, runs representability, uses `Reframe`, and returns `Protected(_draft)`. SQLite holds one lease and one transaction for all checks/write/snapshot, without calling `ClearStaleFreezeAsync`. SQL Server uses one existing serializable `WriteAsync<T>` scope. Reuse the legacy freeze UPDATE timestamp expression. Conditional clear is the existing clear shape with `frozen_for_base_version = $base` or `@base`, return affected-row count != 0. No call to the unconditional members to emulate either operation.

Legacy XML retains overwrite behavior and warns that freeze does not check active. Clear is explicit host recovery only after proving no publisher is live. Document exact refusals, argument/cancellation/lost-ack contract, successful stale replacement and absence of stale maintenance for the new row member.

- [ ] **Step 4: Run focused contracts green, serially.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t1-green-server" /tmp/grimhollow-orch/catalog-conditional-freeze/t1-green-server.log -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~ConditionalFreeze_|FullyQualifiedName~ConditionalRelease_" --logger "trx;LogFileName=t1-green-server.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t1-green-server
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t1-green-sqlite" /tmp/grimhollow-orch/catalog-conditional-freeze/t1-green-sqlite.log -- dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~ConditionalFreeze_StampsOnlyMarkerChanges" --logger "trx;LogFileName=t1-green-sqlite.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t1-green-sqlite
```

Both exit 0 with nonzero executed in-memory/SQLite facts. SQL Server facts skip locally and remain open CI gates. Task 0 race reds remain owned and unchanged, no need to rerun them here.

- [ ] **Step 5: Commit only the Task 1 files and obtain a fresh task review.**


```bash
git add KhaozEngine.Catalog.Authoring/IContentConditionalDraftFreeze.cs KhaozEngine.Catalog.Authoring/InMemoryContentAuthoringStore.ConditionalFreeze.cs KhaozEngine.Catalog.Sqlite/SqliteContentAuthoringStore.ConditionalFreeze.cs KhaozEngine.Catalog.SqlServer/SqlServerContentAuthoringStore.ConditionalFreeze.cs KhaozEngine.Catalog.Authoring/IContentAuthoringStore.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.ConditionalFreeze.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringTextStoreConformance.ConditionalFreeze.cs KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringStoreConformanceTests.cs KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringTextConformanceTests.cs KhaozEngine.Catalog.Tests/Sqlite/SqliteCatalogRowTimestampTests.ConditionalFreeze.cs KhaozEngine.Server.Tests/Catalog/SqlServer/SqlServerCatalogRowTimestampTests.ConditionalFreeze.cs
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t1-commit" /tmp/grimhollow-orch/catalog-conditional-freeze/t1-commit.log -- git commit --only -m "feat(catalog): add conditional draft freeze lifecycle" -- KhaozEngine.Catalog.Authoring/IContentConditionalDraftFreeze.cs KhaozEngine.Catalog.Authoring/InMemoryContentAuthoringStore.ConditionalFreeze.cs KhaozEngine.Catalog.Sqlite/SqliteContentAuthoringStore.ConditionalFreeze.cs KhaozEngine.Catalog.SqlServer/SqlServerContentAuthoringStore.ConditionalFreeze.cs KhaozEngine.Catalog.Authoring/IContentAuthoringStore.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.ConditionalFreeze.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringTextStoreConformance.ConditionalFreeze.cs KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringStoreConformanceTests.cs KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringTextConformanceTests.cs KhaozEngine.Catalog.Tests/Sqlite/SqliteCatalogRowTimestampTests.ConditionalFreeze.cs KhaozEngine.Server.Tests/Catalog/SqlServer/SqlServerCatalogRowTimestampTests.ConditionalFreeze.cs
```

Commit command exits 0 with hooks enabled. Omit an unchanged owned path only after documenting that disposition, never add unrelated paths.
 Report signatures, atomic scope, test exits/counts and still-owned Task 0 reds.

### Task 2: Require the capability in publishing and release the recorded base

**Files:** Task 2 rows in the map, plus Task 0 adapters. Keep the runner production code unchanged.

**Interfaces:** Consume Task 1's companion. Produce `ContentAuthoringException.ConditionalFreezeUnavailableReason = "conditional-freeze-unavailable"`. Existing public publish/Prepare signatures stay unchanged. Race adapters gain read-only `int GuardedFreezeCalls` and populate Task 0's existing nullable release observations from actual companion calls. Test-only `LegacyFreezeStoreView` implements the released authoring seam independently and records every member call without declaring any new capability. Test `ConditionalRowOnlyStoreView` extends the existing incapable `RowOnlyStoreView`, requires a capable inner and forwards both new members.

- [ ] **Step 1: Add publish capability, returned-snapshot and lifecycle tests.**

Name facts `IncapablePublish_RefusesBeforeAnyStoreCall`, `IncapablePrepare_RefusesBeforeReadingDraft`, `RowPrepare_IncludesAnEditBeforeAtomicFreeze`, `Publish_LostFreezeAcknowledgementReleasesRecordedBase`, `Publish_CancellationReleasesWithNone`, `Publish_ReleaseFailurePreservesOriginalFailure`, and `Publish_LostCommitAcknowledgementPreservesNewerMarker`. Keep T2/T2p/T2r bodies unchanged.

Incapable view instruments every store member, including baseline/draft reads and release. Capture footprint from the inner, not the view, so measuring does not contaminate call counts. The Prepare test receives a baseline pre-read from the inner. Create the test-only incapable/capable views, constructor checks, real old/fixed gap hooks and release observation forwarding before the red run. Make only the old row view's draft-read and legacy-freeze members virtual for those hooks. Add companion forwarding to every Task 0 participant before the new publish refusal can be implemented. This is test preparation, no production publish behavior changes. Pack observations reset after seed. Construction itself succeeds, and independent `WriteAsync`/`SweepAsync` do not gain a capability gate. A good companion decorator matches inner behavior. Construction over an incapable inner throws `ArgumentException`, not a lazy cast on the first freeze.

```csharp
var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => commit.PublishAsync(PublishFixtures.Request(1)));
Assert.Equal("conditional-freeze-unavailable", refused.Reason);
Assert.Empty(view.Calls);
Assert.Empty(packs.Writes);
Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
var prepareRefused = await Assert.ThrowsAsync<ContentAuthoringException>(
    () => publisher.PrepareAsync(PublishFixtures.Request(1), baselineFromInner));
Assert.Equal("conditional-freeze-unavailable", prepareRefused.Reason);
Assert.Empty(view.Calls);
Assert.Throws<ArgumentException>(() => new ConditionalRowOnlyStoreView(incapableInner));

// Land value 33 at the baseline draft-read exit or fixed guarded-freeze entry, after value 22 was staged.
// The fixed Prepare must plan the value returned by the atomic operation.
Assert.Equal(33, plan.Candidate.Rows(UpgradeFixtures.Thing)[0].Fields[0].Number);
Assert.Equal(1, safeView.GuardedFreezeCalls);
Assert.Equal(0, safeView.LegacyFreezeCalls);
Assert.Equal(0, safeView.DraftReadCalls);
```

The existing `ContentPublishPlan.Candidate` and `ContentSnapshot.Rows(ContentTypeId)` supply this assertion. Pin literal 33, not a value derived from the submitted edit.

Lost-freeze-ack double forwards the real freeze and then throws once. Assert held marker is null after failure, release arguments `[1]`, cleanup token cannot cancel, no version 2, no lost edit. Lost-commit-ack double forwards a real text commit to 2, stages a newer row draft, freezes at 2 and then throws. Assert active 2 and marker 2 survive the older finally. Pack-write fault/invalid candidate/cancellation retain their original exceptions and release only base 1. Count both legacy calls as zero after the fix.

```csharp
Assert.Equal(new int?[] { 1 }, older.ReleaseBases);
Assert.All(older.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
Assert.Equal(0, older.LegacyFreezeCalls);
Assert.Equal(0, older.LegacyClearCalls);
Assert.Equal(2, await inner.GetActiveVersionAsync()); // lost commit acknowledgement case
Assert.Equal(2, (await inner.GetOpenDraftAsync())!.FrozenForBaseVersion);
Assert.Equal(new bool?[] { false }, older.ReleaseResults);
Assert.Same(originalPublishFailure, observedPublishFailure); // release-fault case
```


- [ ] **Step 2: Run the publish red tests once.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t2-red" /tmp/grimhollow-orch/catalog-conditional-freeze/t2-red.log -- dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~ConditionalFreezeCapabilityPublishTests" --logger "trx;LogFileName=t2-red.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t2-red
```

Expected exit 1 with the old publish reading/writing instead of capability refusal or planning the old value. Tests use literal reason text before the new constant exists. Compile errors do not qualify as this task's behavior red.

- [ ] **Step 3: Change publish lifecycle and forward every race participant.**

Resolve `store as IContentConditionalDraftFreeze` once in `ContentPublishCommit` without throwing in its constructor. `PublishAsync` refuses before its try and before any store call. Its finally invokes the companion release with `request.ExpectedBaseVersion` and `CancellationToken.None`, still swallowing its own fault. Preserve text dispatch and complete text confirmation.

`ContentPublisher.PrepareAsync` refuses an incapable own store before its draft read. `FreezeAsync` keeps request-vs-baseline check, then returns the awaited `FreezeDraftForBaseAsync(baseline.VersionNumber, cancellationToken)`. Remove its separate draft read and legacy freeze.

Complete constructor-checked companion forwarding to Task 0 text/row adapters and both existing forwarding bases. Their two new methods are virtual where existing race subclasses need overrides. Task 0 test bodies remain unchanged. Adapters keep baseline old gates and add equivalent fixed gates: row Prepare entry is the new freeze entry, runner exit is the guarded freeze's returned snapshot, first release exit works for either legacy or conditional member, and counters record arguments/result/token of actual releases. All runner participants must declare the companion now so the new publish gate cannot create a different red failure.

Keep `RowOnlyStoreView` incapable. Move only legacy publishing tests to `ConditionalRowOnlyStoreView`. `TextStoreDouble` declares checked companion forwarding now too, so existing text publishing tests keep their route. Server text conformance's nested row-only view gains the constructor-checked companion through its forwarding base. Both still hide the text companion, so text representability checks remain exercised.

- [ ] **Step 4: Run publish green and preserved legacy-route tests.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t2-green" /tmp/grimhollow-orch/catalog-conditional-freeze/t2-green.log -- dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~ConditionalFreezeCapabilityPublishTests|FullyQualifiedName~ConditionalFreezeReleasePublishTests|FullyQualifiedName~TextLegacyCompatibilityTests" --logger "trx;LogFileName=t2-green.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t2-green
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t2-green-server" /tmp/grimhollow-orch/catalog-conditional-freeze/t2-green-server.log -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~InMemoryContentAuthoringTextConformanceTests|FullyQualifiedName~SqliteContentAuthoringTextConformanceTests" --logger "trx;LogFileName=t2-green-server.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t2-green-server
```

Both exit 0 with executed publish/legacy cases. T2 and T2p green, T2r remains the same accepted refusal. Runner-only T3/T3o/T4/T4r remain the owned reds until Task 3. Do not misreport this as the complete repair.

- [ ] **Step 5: Commit only Task 2 owned paths and obtain fresh task review.**


```bash
git add KhaozEngine.Catalog.Authoring/Publish/ContentPublishCommit.cs KhaozEngine.Catalog.Authoring/Publish/ContentPublisher.cs KhaozEngine.Catalog.Authoring/ContentAuthoringException.cs KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeCapabilityPublishTests.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/LegacyFreezeStoreView.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceStore.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/RowFreezeRaceStore.cs KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs KhaozEngine.Server.Tests/Catalog/ForwardingContentAuthoringStore.cs KhaozEngine.Catalog.Tests/Publish/ConditionalRowOnlyStoreView.cs KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs KhaozEngine.Catalog.Tests/Authoring/TextLegacyCompatibilityTests.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringTextStoreConformance.cs
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t2-commit" /tmp/grimhollow-orch/catalog-conditional-freeze/t2-commit.log -- git commit --only -m "fix(catalog): guard publishing and condition freeze release" -- KhaozEngine.Catalog.Authoring/Publish/ContentPublishCommit.cs KhaozEngine.Catalog.Authoring/Publish/ContentPublisher.cs KhaozEngine.Catalog.Authoring/ContentAuthoringException.cs KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeCapabilityPublishTests.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/LegacyFreezeStoreView.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/FreezeRaceStore.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/RowFreezeRaceStore.cs KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs KhaozEngine.Server.Tests/Catalog/ForwardingContentAuthoringStore.cs KhaozEngine.Catalog.Tests/Publish/ConditionalRowOnlyStoreView.cs KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs KhaozEngine.Catalog.Tests/Authoring/TextLegacyCompatibilityTests.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringTextStoreConformance.cs
```

Commit command exits 0 with hooks enabled. Omit an unchanged owned path only after documenting that disposition, never add unrelated paths.
 Record publish test exits, text-route counts, zero legacy calls and remaining runner reds.

### Task 3: Guard runner freeze, lifecycle and capability precedence

**Files:** Task 3 rows from the map. Task 0 test bodies remain unchanged.

**Interfaces:** Consume Task 1 companion. `ContentUpgradeTextRoute.FreezeAsync(ContentUpgradePlan plan, int active, CancellationToken cancellationToken)` returns `Task<ContentDraft>`. Resolve the conditional companion beside `_text`, with an internal companion resolver that throws `InvalidOperationException` if reached without the gate. Produce `ContentUpgradeCodes.ConditionalFreezeUnsupported = "KECU0017"`. Produce the server-local `ConditionalFreezeRaceStore(IContentAuthoringStore inner, IContentIdPersistence ids, ContentTypeRegistry registry, IPackStore packs)`, requiring the inner text, ledger and conditional companions and exposing the same one-shot sequencing and real commit/release counters as the catalog adapter.

- [ ] **Step 1: Add runner T5/lifecycle and T6 conformance assertions.**

Runner facts/theories: `IncapableApply_StopsBeforeDraftGate` for pending publish, first `AlreadySatisfied`, operator draft, off-active pin and stale expected version. `IncapablePreview_KeepsPlanAndAddsCapabilityNote` covers `WouldPublish` and `WouldAdopt`, plus unchanged existing Preview gate outcomes. `IncapableRunner_PreservesEarlierGatePrecedence` covers ledgerless, no catalog, catalog ahead and no pending. `Runner_LostFreezeAcknowledgementReleasesCapturedBase`, `Runner_DuplicateCleanupCannotReleaseNewerBase` and `Runner_ReleaseFailureRetainsExistingFaultBehavior` complete RF4.

Create the test-only view before the red run. The new `LegacyFreezeLedgerView` extends Task 2's incapable `LegacyFreezeStoreView`, requires the inner ledger and records/forwards its two members. Require this read-recording legacy view distinct from the capable adapters. Do not let inheritance accidentally declare the capability. Counts and footprints are taken from/reset around the inner. For stopped Apply, the store-call sequence is active-version and ledger-list only, no draft/pin/baseline/plan read or write, and the planner is never invoked.

```csharp
Assert.Equal(ContentUpgradeOutcome.Unsupported, apply.Outcome);
Assert.Contains(apply.Diagnostics, d => d.Code == "KECU0017");
Assert.DoesNotContain(apply.Diagnostics, d => d.Code is "KECU0004" or "KECU0005" or "KECU0007" or "KECU0010");
Assert.Equal(new[] { "GetActiveVersionAsync", "ListUpgradesAsync" }, legacy.Calls);
Assert.Equal(0, plannerCalls);
Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
Assert.Equal(beforeDraftMarker, (await inner.GetOpenDraftAsync())?.FrozenForBaseVersion);

Assert.Equal(capablePreview.Outcome, preview.Outcome);
Assert.Equal(ContentUpgradeOutcome.PreviewOnly, preview.Outcome);
Assert.Equal(capablePreview.Steps.Select(s => s.State), preview.Steps.Select(s => s.State));
Assert.Equal(capablePreview.Steps.SelectMany(s => s.ChangeLines), preview.Steps.SelectMany(s => s.ChangeLines));
Assert.Contains(preview.Diagnostics, d => d.Code == "KECU0013");
Assert.Single(preview.Diagnostics.Where(d => d.Code == "KECU0017"));
Assert.Contains("an apply on this store is refused before any write", preview.Diagnostics.Single(d => d.Code == "KECU0017").Message);
Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
```

Early precedence theories assert literal `KECU0001`, `KECU0002`, `KECU0003`, `KECU0012` and no `KECU0017`. `RecordBaselineAsync` remains usable and preserves its current baseline write behavior without the new gate. Preview still diagnoses operator draft/pin/moved expectation with their earlier code plus the capability note.

For T3o assert actual runner publish attempted unchanged text `FreezeChangesAsync(1)` and refused `base-version-moved`, both observed releases of 1 returned false, newer marker 2 survived. T4/T4r assert runner guarded freeze refused before marker/timestamp mutation. T3 asserts B's two release arguments `[1, 1]` and results `[false, false]`. All final race results use the unchanged Task 0 bodies. Put the duplicate-release assertions shown below in Task 0 after its deciding success assertion from the start, using its nullable observation contract. The lost-ack and release-fault assertions belong to the new lifecycle facts. Do not add a second run of T3 just to inspect its cleanup.

```csharp
Assert.Equal(new int?[] { 1, 1 }, runnerB.ReleaseBases); // T3 double cleanup
Assert.Equal(new bool?[] { false, false }, runnerB.ReleaseResults);
Assert.All(runnerB.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
Assert.Equal(0, runnerB.LegacyFreezeCalls);
Assert.Equal(0, runnerB.LegacyClearCalls);
Assert.Equal(new int?[] { 1 }, lostFreezeAckRunner.ReleaseBases);
Assert.All(lostFreezeAckRunner.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
Assert.Null((await lostFreezeAckInner.GetOpenDraftAsync())!.FrozenForBaseVersion);
Assert.Equal(ContentUpgradeOutcome.Failed, releaseFaultReport.Outcome);
Assert.Contains(releaseFaultReport.Diagnostics, d => d.Code == "KECU0009");
```
Pin legacy freeze/clear count 0 in each capable pipeline test.

T6 adds `ConditionalFreeze_RunnersRecoverWithoutCrossVersionCleanup` and `ConditionalFreeze_StaleRunnerCannotOverwriteNewerMarker` as virtual facts in `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.UpgradeRace.cs`, with `[CatalogSqlServerFact]` overrides. Use server-local `ConditionalFreezeRaceStore`, complete text/ledger forwarding, self-built commit and one-shot gates matching T3/T4. Consume `OpenAsync`, `PackOf`, `Ids`, `Registry`, `RaceSet`, `RaceFirstId`, `RaceSecondId` and existing server fixture helpers, not a test-assembly reference. T6's T3 case uses the full existing RaceSet, which adds rows `two`/`three` with ids 2/3 after seeded `one` id 1. Assert rows `[1,2,3]`, versions 1/2/3, applied ledger versions 2/3 and no draft. T6's T4 case uses a single definition adding `two` id 2, then the newer console changes `one` to 77. Assert rows `[1,2]`, active 3, one Adopted ledger record at 3, no draft and an actual winning text commit. SQL Server operations open/dispose their existing per-call scopes, and all decorator pauses are outside them.

- [ ] **Step 2: Run runner behavior red before changing its code.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t3-red" /tmp/grimhollow-orch/catalog-conditional-freeze/t3-red.log -- dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~ConditionalFreezeCapabilityUpgradeTests|FullyQualifiedName~ConditionalFreezeReleaseUpgradeTests" --logger "trx;LogFileName=t3-red.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t3-red
```

Expected exit 1. Gate tests find existing writes/adoption or old gate outcome instead of KECU0017. Remaining four races retain the named Task 0 failures. No capability-refusal/harness failures are acceptable substitutes.

- [ ] **Step 3: Implement runner gate and captured-base lifecycle.**

Insert capability Apply stop/Preview note immediately after `pending.Count == 0` returns in `ContentUpgradeRunner.GatesAsync`, before `ResolveOpenDraftAsync`. Apply message names store type, required companion and both methods. Preview message contains the literal phrase asserted above. Do not reorder or widen existing gates.

For text plans keep `FreezeChangesAsync(active)`. All other plans return `FreezeDraftForBaseAsync(active)`, including text-capable stores. Remove separate row read-back. Replace `bool _frozenForPublish` with `int? _frozenForBaseVersion`. In `FreezeForPublishAsync` copy `Active` to a local and set the field immediately before passing that same local to Route. Release reads and nulls the field before awaiting the companion, and passes `CancellationToken.None`. The obstruction and finally call sites stay put. Successful publish nulls the field because the inner publish completed its lifecycle.

Do not change `ContentUpgradeFault.IsContention`, text commit confirmation or row commit. A moved/empty guarded row freeze now enters existing contention handling at the freeze instead of the old read-back obstruction.

Migrate existing double fault hooks to guarded freeze's exit, not obsolete `GetOpenDraftAsync` read-back: `CancelsTheReadBackStore`, `FreezeCommitsThenFailsStore`, `TheReadBackFaultsStore` and `CountsFreezeReleasesStore`. Keep their proof intent, update names/comments to describe the new snapshot boundary, count conditional release rather than clear, and assert actual token/base. Migrate `CountingContentAuthoringStore` release counters in `KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs` likewise.

Inventory all implementers with `rg -n '\bIContentAuthoringStore\b' KhaozEngine.Catalog.Tests KhaozEngine.Server.Tests`. Task 2 forwarding bases already cover their subclasses, `TextUpgradeStore`, `OldWrapperStore` and `DraftRaceStore`. `OldWrapperStore` must keep its old reconstructed DTO behavior while its companion forwards the genuine complete guarded snapshot. Direct `UpgradeDraftRaceStore` declares/forwards both methods with constructor check. `WriteRefusingAuthoringStore` requires a capable inner and treats both new methods as write attempts that throw, so read-only admin checks remain sensitive. Add the two method names to `RowOnlyStoreView.WriteMembers`. `TextStoreDouble` always declares and forwards both members over its concrete in-memory inner, with an explicit constructor capability check, with no accidental capability on the incapable base view.

Correct the statistical concurrency suite's comment to distinguish one in-memory catalog from separately connected provider replicas. Do not change its iterations, assertions or scheduling.

- [ ] **Step 4: Run all catalog/runner focused proofs green, then server conformance, serially.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t3-green" /tmp/grimhollow-orch/catalog-conditional-freeze/t3-green.log -- dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Catalog.Upgrade&FullyQualifiedName!~ContentUpgradeConcurrencyTests|FullyQualifiedName~ConditionalFreeze" --logger "trx;LogFileName=t3-green.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t3-green
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t3-green-server" /tmp/grimhollow-orch/catalog-conditional-freeze/t3-green-server.log -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Catalog" --logger "trx;LogFileName=t3-green-server.trx" --results-directory /tmp/grimhollow-orch/catalog-conditional-freeze/t3-green-server
```

Both exit 0, every Task 0 race executes green, T2r retains its exact residue refusal. Assert nonzero executed T6 memory/SQLite cases. SQL Server is skipped locally, not proven.

```bash
rg -n 'ClearDraftFreezeAsync|FreezeDraftAsync' KhaozEngine.Catalog.Authoring/Publish KhaozEngine.Catalog.Authoring/Upgrade
```

Expected no executable calls. Remaining documentation references must describe legacy/recovery, not pipeline usage. An empty rg exits 1 and means the desired absence here. Inspect matches, do not suppress them blindly.

- [ ] **Step 5: Commit exact Task 3 paths and obtain fresh task review.**


```bash
git add KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeTextRoute.cs KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeRun.Publish.cs KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeRunner.cs KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeDiagnostic.cs KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeCapabilityUpgradeTests.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/LegacyFreezeLedgerView.cs KhaozEngine.Catalog.Tests/Upgrade/ContentUpgradeFreezeReleaseTests.cs KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs KhaozEngine.Catalog.Tests/Upgrade/TextUpgradeDoubles.cs KhaozEngine.Catalog.Tests/Upgrade/DraftRaceStore.cs KhaozEngine.Server.Tests/Catalog/UpgradeDraftRaceStore.cs KhaozEngine.Server.Tests/Catalog/ConditionalFreezeRaceStore.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.UpgradeRace.cs KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringStoreConformanceTests.cs KhaozEngine.Server.Tests/Catalog/WriteRefusingAuthoringStore.cs KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs KhaozEngine.Catalog.Tests/Upgrade/ContentUpgradeConcurrencyTests.cs
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t3-commit" /tmp/grimhollow-orch/catalog-conditional-freeze/t3-commit.log -- git commit --only -m "fix(catalog): guard upgrade freeze and capability precedence" -- KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeTextRoute.cs KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeRun.Publish.cs KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeRunner.cs KhaozEngine.Catalog.Authoring/Upgrade/ContentUpgradeDiagnostic.cs KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeCapabilityUpgradeTests.cs KhaozEngine.Catalog.Tests/ConditionalFreeze/LegacyFreezeLedgerView.cs KhaozEngine.Catalog.Tests/Upgrade/ContentUpgradeFreezeReleaseTests.cs KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs KhaozEngine.Catalog.Tests/Upgrade/TextUpgradeDoubles.cs KhaozEngine.Catalog.Tests/Upgrade/DraftRaceStore.cs KhaozEngine.Server.Tests/Catalog/UpgradeDraftRaceStore.cs KhaozEngine.Server.Tests/Catalog/ConditionalFreezeRaceStore.cs KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.UpgradeRace.cs KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringStoreConformanceTests.cs KhaozEngine.Server.Tests/Catalog/WriteRefusingAuthoringStore.cs KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs KhaozEngine.Catalog.Tests/Upgrade/ContentUpgradeConcurrencyTests.cs
```

Commit command exits 0 with hooks enabled. Omit an unchanged owned path only after documenting that disposition, never add unrelated paths.
 Include inventory disposition for every forwarder, lifecycle tokens/results, green counts, API call search and remaining remote gates. No source shipping claim until final checks and CI.

### Task 4: Document migration, reconcile version and complete verification

**Files:** Task 4 paths from the map. Full Markdown sweep may identify an additional stale reference, which root assigns as a named owned path before changing it. Do not expand behavior or consumer scope.

**Interfaces:** Living docs name both companion signatures, `conditional-freeze-unavailable`, `KECU0017`, the up-front adoption refusal and useful Preview. Version remains a root integration decision within OA15's next-free-minor direction.

- [ ] **Step 1: Check documentation red and reconcile main/version through root.**

```bash
rg -n 'FreezeDraftAsync|ClearDraftFreezeAsync|FreezeDraftForBaseAsync|ReleaseDraftFreezeForBaseAsync|IContentConditionalDraftFreeze|KECU0017|rival RUNNER|publish window' --glob '*.md' --glob '!docs/superpowers/plans/2026-10-06-catalog-conditional-freeze-repair.md' .
git fetch origin
git rev-parse main origin/main
git tag --sort=-v:refname | head -10
git show main:Directory.Build.props
git show main:CHANGELOG.md | head -24
```

Documentation red is the verified stale living claim that the pipeline uses the legacy freeze/release and a rival runner "loses nothing". Save the exact lines. History designs remain history and need no rewrite. No new runtime test is needed for prose/version work.

Root contacts the pivot thread and records its response before any pinable engine change, rechecks current local/fetched main and tags, and reconciles current main into this task branch before selecting the version. Current verified facts are main/origin `ca13d62d7`, staged `20.27.1`, newest immutable `v20.27.0`. These are not a reservation. If still current and untagged, use `20.28.0` and fold the two publication-repair bullets into its newest entry. If another minor is taken, choose the next free minor within OA15. If the repair shipped separately, preserve its released history. Any main change invalidating Task 0's diagnosed source or the approved API is reported to root as a design/baseline gate, not silently reconciled by redesign.

- [ ] **Step 2: Update living contract and release text.**

Authoring README covers the atomic row snapshot, both cleanup paths, capability requirement, Apply/adoption and Preview precedence, constructor-checked decorators, explicit recovery and accepted same-base residue. Replace the inaccurate rival-runner safety claim. State text-route boot liveness follows #1312 and row-only unchecked-commit edit loss follows #1311. A text retry would not repair the latter.

Document unchanged `ReadPublishBaselineAsync` and `FreezeChangesAsync` stale sweeps, including timestamp changes that may precede a later text-freeze refusal/cancellation. Do not promise those legacy maintenance calls are write-free. New row freeze explicitly has no sweep.

Changelog includes a "Behavior change for custom catalog stores" bullet naming both new members, typed publish/Prepare refusal, pending Apply refusal including adoption, useful Preview note and decorator constructor migration. State source/binary compatibility and runtime behavior incompatibility explicitly. Mixed engines sharing a catalog retain the old exposure until all writers upgrade. Include #1271 and the staged publication repair without claiming total same-base safety.

Update every guarded package-reference declaration in root README and USING, and newest changelog heading to the selected version, with props in the same commit. Inspect Admin `CatalogRefusal` default branch: it already creates `CatalogErrorPayload` with the unchanged reason, so no new mapping is planned. Verify Admin README's host-only recovery wording remains accurate.

- [ ] **Step 3: Run documentation guards serially and commit version with matching changelog.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t4-dashes" /tmp/grimhollow-orch/catalog-conditional-freeze/t4-dashes.log -- sh scripts/check-dashes.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t4-prose" /tmp/grimhollow-orch/catalog-conditional-freeze/t4-prose.log -- sh scripts/check-prose.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t4-size" /tmp/grimhollow-orch/catalog-conditional-freeze/t4-size.log -- sh scripts/check-file-size.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t4-instructions" /tmp/grimhollow-orch/catalog-conditional-freeze/t4-instructions.log -- sh scripts/check-agent-instructions.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t4-doc-versions" /tmp/grimhollow-orch/catalog-conditional-freeze/t4-doc-versions.log -- bash scripts/check-doc-versions.sh
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t4-diff" /tmp/grimhollow-orch/catalog-conditional-freeze/t4-diff.log -- git diff --check
```

Every command must exit 0. Do not bypass a hook or raise the size baseline. Force-add this ignored plan, stage only assigned docs/XML/props paths, and use an explicit commit pathspec. Subject is `release(20.28.0): document conditional catalog freeze migration` if that is still the selected version, substituting the actual version otherwise. Do not include a release tag.

```bash
git add -f docs/superpowers/plans/2026-10-06-catalog-conditional-freeze-repair.md
git add KhaozEngine.Catalog.Authoring/README.md KhaozEngine.Catalog.Authoring/IContentTextAuthoringStore.cs docs/USING-KHAOZENGINE.md README.md Directory.Build.props CHANGELOG.md docs/INDEX.md
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:t4-commit" /tmp/grimhollow-orch/catalog-conditional-freeze/t4-commit.log -- git commit --only -m "release(20.28.0): document conditional catalog freeze migration" -- KhaozEngine.Catalog.Authoring/README.md KhaozEngine.Catalog.Authoring/IContentTextAuthoringStore.cs docs/USING-KHAOZENGINE.md README.md Directory.Build.props CHANGELOG.md docs/INDEX.md docs/superpowers/plans/2026-10-06-catalog-conditional-freeze-repair.md
```

Substitute root's actual selected version in the subject. Append only explicitly assigned sweep paths that really changed. Expected commit exit 0 with all hooks enabled.


- [ ] **Step 4: Root obtains Task 4 review and one fresh whole-branch review.**

The Task 4 reviewer reads its full base-to-head package. Then one fresh final reviewer reads `ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc..HEAD`, including baseline harness, all task interfaces, Global Constraints, five Review Focus cases, task reports, rulings and deferred items. If reconciliation introduced new source, record the actual source baseline and why the approved proof remains valid before the review. Handle material findings with one assigned fix wave and scoped re-review per SDD. Never hide early red proof or earlier task commits with `HEAD~1`.

- [ ] **Step 5: Run complete final checks once on the reconciled reviewed branch.**

```bash
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:final-build" /tmp/grimhollow-orch/catalog-conditional-freeze/final-build.log -- dotnet build KhaozEngine.slnx -c Release
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:final-test" /tmp/grimhollow-orch/catalog-conditional-freeze/final-test.log -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:final-format" /tmp/grimhollow-orch/catalog-conditional-freeze/final-format.log -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

All exits 0, build zero warnings, tests no failures, format no changes. Ordinary GPU/env-gated skips are named rather than called proof. Existing statistical concurrency tests execute only as part of this required once-at-finish suite, with no additional iterations or loop run. Repeat a covered check only after new changes/failure/main reconciliation. Preexisting failures block completion and are reported to root without unrelated fixes.

- [ ] **Step 6: Prove private pack, then hand integration and normal CI to root.**

```bash
catalog_verified_sha=$(git rev-parse HEAD)
mkdir -p /tmp/grimhollow-orch/catalog-conditional-freeze/private-feed
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:private-pack" /tmp/grimhollow-orch/catalog-conditional-freeze/private-pack.log -- env KHAOZENGINE_FEED=/tmp/grimhollow-orch/catalog-conditional-freeze/private-feed sh scripts/pack-local-feed.sh
```

Expected exit 0. Inspect the produced nupkg nuspec repository commit and version for all three changed store packages, requiring `catalog_verified_sha` and the selected minor. This branch feed is intentionally unmerged, so `check-local-feed --strict` would report UNSAFE before origin/main contains it and is not the right private-proof assertion.

Root alone may push the fully green reviewed branch for normal CI. Record the exact run head SHA and all job conclusions. Require all triggered legs green, including the Metal SQLite leg and `catalog-sqlserver`, with new SQL Server facts actually executed, not skipped. A workflow rerun without changed code or an unrelated green SHA is not evidence. No stress workflow is authorized.

- [ ] **Step 7: Root integrates, pushes main and packs that same SHA behind the guards.**

After final fetch, if main changed, root reconciles it in this branch, obtains relevant review and reruns checks invalidated by that diff. Record the final verified SHA. Integrate by fast-forward where possible so the main pack builds the verified commit. Worker stops at verified commit and does not run these commands.

```bash
cd /Users/antonio/KhaozEngine
test "$(git branch --show-current)" = "main"
git merge --ff-only fix/wa-catalog-freeze
test "$(git rev-parse HEAD)" = "$catalog_verified_sha"
git push origin main
git fetch origin
test "$(git rev-parse origin/main)" = "$catalog_verified_sha"
test -z "$(git status --porcelain)"
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:main-pack" /tmp/grimhollow-orch/catalog-conditional-freeze/main-pack.log -- sh scripts/pack-local-feed.sh
bash /tmp/grimhollow-orch/slot-run.sh "catalog-freeze:main-feed" /tmp/grimhollow-orch/catalog-conditional-freeze/main-feed.log -- sh scripts/check-local-feed.sh --strict
```

Expected all exits 0. Recheck no concurrent main movement before using the SHA gate. Validate shared-feed package version and nuspec commit equal the verified/main/origin SHA. Record any historical feed failure separately, do not delete or overwrite another lane's bytes to force green. Root records actual integration/CI/feed evidence in the implementation report and program, then reconciles Outcome updates. Any later tracked engine edit changes the pack SHA and must get its own source-equality/documentation verification and guarded pack before being called the final head. Root supplies the integration evidence and supplies #467 handoff. No game changes and no tag. A later tag always requires owner authorization and the canonical tag script.

## Outcome

Status on 2026-10-06: **full implementation plan prepared for independent root verification and OWNER approval**. Serial SDD is preserved. No task is executed, no code/test/version changes made, no production execution or release authorized by this plan.

| Gate | Required durable record | Current state |
| --- | --- | --- |
| Plan | Planning commit SHA, self-review coverage, documentation guard commands/exits/logs | Written by planner in `.superpowers/sdd/2026-10-06-catalog-conditional-freeze-design/implementation-plan-report.md` |
| Approval | Root verification and explicit OWNER full-plan approval | Pending |
| Coordination | Pivot thread reply, engine code/pinable barrier ruling | Pending root action |
| Task 0 | Exact source-diff exit, twelve named reds/two residue passes, test-only SHA, fresh review | Pending |
| Tasks 1 to 3 | Task bases/SHAs, focused green exits/counts, constructor/forwarder inventory, fresh reviews | Pending |
| Task 4 | Rechecked main/tags, chosen minor, matching changelog, docs sweep/guard exits | Pending |
| Final | Whole-branch review, full Release build/test/format exits, private-feed version/SHA | Pending |
| Remote | Exact-head normal CI, Metal SQLite and executed SQL Server facts | Pending |
| Integration | Verified branch/main/origin SHA, main push, guarded shared pack and strict feed proof | Pending root action |
| Release/adoption | Separate owner tag approval, immutable release tag, #467 consumer adoption | Not authorized here |
| Residue | #1312 text-route boot liveness, #1311 row-only edit loss | Deferred, separate scope |

Self-review performed by this planner against the full approved spec: sections 4 to 7 map to Tasks 1 to 3, compatibility/rollout/version to Tasks 2 to 4, H1 to H6 and T1 to T6 to the named tests, both review-2 corrections to RF1/RF4 and the separate residue issues. No helper reviewer is spawned. Runtime red proof, full-plan approval, pivot coordination, remote SQL Server/Metal evidence, final version recheck and integration remain explicit gates. Unresolved spec conflicts must be reported to root rather than silently changed.
