# Catalog conditional freeze and release

Status: **APPROVED FOR IMPLEMENTATION PLANNING under OA15, 2026-10-06. Reviews 1 and 2 reconciled,
including C1 and C2. The full implementation plan still requires owner approval.** Written under owner
ruling OA14 ("Proceed with conditional-release design") for
[#1271](https://github.com/APKiwiOrg/KhaozEngine/issues/1271). This is a design, not an implementation plan and not
a release-tag authorization. OA15 selects the next-free-minor direction, currently 20.28.0.
No version is changed or reserved and no tag is authorized.

Base: `fix/wa-catalog-freeze` at `ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc`. Every line reference below is to that
commit.

Revision 1 answers design review 1:

- F1. The companion now carries both halves of the lifecycle: a guarded atomic row-only freeze and the conditional
  release. Both the runner's row-only freeze and `ContentPublisher`'s freeze go through it. The first draft's
  section 5.3, which sent row-only runner plans through `FreezeChangesAsync`, is withdrawn. Options and scores are
  reassessed, including guarding the existing `FreezeDraftAsync` (option G).
- F2. Every race reproduction uses decorators that forward the whole text companion and build the publish over
  themselves. Section 11 names the gate each test hits on the baseline and on the fix, and adds row-only variants.
- F3. The version is presented as an owner choice between `20.28.0` and `21.0.0`, grounded in house precedent.
- F4. A task 0 owns the red-first proof. The runner gate, its precedence, Preview and adoption behavior are now
  specified and pinned by T5.

## 1. Problem and verified evidence

A failed publish releases the draft freeze with `IContentAuthoringStore.ClearDraftFreezeAsync`, which clears
whatever marker stands. An older-base publisher that lost the version can therefore clear the marker a newer-base
publisher set on the next draft. The newer publisher's complete text commit then refuses with
`text-state-mismatch`, which the upgrade runner does not count as contention, so it reports `KECU0009` and a boot
fails. The hosted in-memory run 37380090926 and SQLite run 37380091240 fit this sequence. No deterministic
reproduction has run yet, and no catalog loss or duplicate publication was observed.

Each claim below was checked against the code rather than adopted from the investigation report.

| Source | Verified fact |
|---|---|
| `KhaozEngine.Catalog.Authoring/IContentAuthoringStore.cs:208-220` | The contract says the release clears the marker "whatever it holds" and that a publish runs it on every exit path. Host recovery may call it only after proving no publisher is live. |
| `InMemoryContentAuthoringStore.Freeze.cs:52-68`, `SqliteContentAuthoringStore.Freeze.cs:78-99`, `SqlServerContentAuthoringStore.Freeze.cs:84-104` | All three stores clear any non-null marker. The SQL is `WHERE draft_key = 1 AND frozen_for_base_version IS NOT NULL`. |
| `Publish/ContentPublishCommit.cs:151-158`, `:217-226` | The publish pipeline's `finally` calls the unconditional release on every exit, on `CancellationToken.None`, and swallows its failure. |
| `Upgrade/ContentUpgradeRun.Publish.cs:144`, `:179-186`, `:216`, `:239-249` | The runner releases on its obstruction path and in its `finally`. Its flag is set before the freeze call, so a freeze whose acknowledgement was lost is still released. |
| `IContentTextAuthoringStore.FreezeChangesAsync` in all three stores (`InMemoryContentAuthoringStore.Text.cs:122-149`, `SqliteContentAuthoringStore.Text.cs:103-152`, `SqlServerContentAuthoringStore.Text.cs:102-152`) | The companion freeze clears stale markers, refuses a base that is not active and writes the marker in one gate or transaction. Its marker therefore always equals the caller's expected base. |
| `InMemoryContentAuthoringStore.TextPublish.cs:59-64` and the SQLite and SQL Server equivalents | The text commit refuses when the marker is not the active version or the draft is not `SameDraft` as the plan's frozen draft. |
| `InMemoryContentAuthoringStore.Publish.cs:92-112` | The row-only commit confirms the version number and never reads the marker or compares the draft. |
| `Publish/ContentPublishCommit.cs:113-116` | A publish takes the text route only when the object the commit was built over is an `IContentTextAuthoringStore`. Every engine store builds its commit over itself, so every engine publish hits the marker check. |
| `Upgrade/ContentUpgradeFault.cs:44-52` | `IsContention` excludes `TextStateMismatchReason`. |
| `Publish/ContentPublisher.cs:242-269` | The row route checks the expected base against a baseline read earlier, reads the draft, then calls `FreezeDraftAsync`. The check, the read and the write are three separate steps. |
| `Upgrade/ContentUpgradeTextRoute.cs:95-107` | The runner freezes a plan that carries no text with `FreezeDraftAsync(active)` on every store, text capable or not, then reads the draft back with a separate `GetOpenDraftAsync`. |
| `*Store.Freeze.cs` `FreezeDraftAsync` in all three stores | The row-only freeze checks row-only representability, then overwrites the marker with any non-negative base and never compares it with the active version. |

**Conditional release alone does not close the cross-version race.** A runner whose plan carries no text can call
`FreezeDraftAsync(1)` after a rival committed version 2 and froze its next draft at 2. The overwrite turns the
rival's marker into a stale 1, so the rival's text commit already refuses with `text-state-mismatch`, and the
runner's release of base 1 then clears the marker it wrote. The same check-then-overwrite sits in
`ContentPublisher.FreezeAsync` for any publish that takes the row route. The hosted concurrency test's definitions
add rows through `UpgradeFixtures.Adds` (`KhaozEngine.Catalog.Tests/Upgrade/UpgradeFixtures.cs:106-115`), and the
runners there use the engine stores directly (`ContentUpgradeConcurrencyTests.cs:95-96`, `:145-146`). If those
plans carry no text, which task 0 confirms, this path is reachable in the very test that failed. Section 5 closes
both the release and the freeze.

## 2. Scope and non-goals

In scope: an atomic freeze guarded by the expected base and an atomic release conditioned on the recorded base,
their public contract, both pipelines' call sites, the upgrade runner's capability gate and Preview behavior,
compatibility for custom stores and decorators, version disposition and deterministic proof.

Not selected, per OA14: a durable owner token, a schema version or migration, treating any `text-state-mismatch`
as contention, a new text-refusal contention reason, widened fault handling, process-local locks, test
serialization or relaxed assertions. The unconditional release and the unconditional freeze stay public, for
explicit host recovery, legacy stores and test setup.

## 3. Decision: the API shape

### 3.1 The constraint that decides it

No combination of existing members can release a marker conditionally and atomically, or freeze a draft only
when the expected base is still active. `ClearDraftFreezeAsync` is unconditional. `FreezeDraftAsync` overwrites.
`TryDiscardChangesAsync` refuses a frozen draft. Reading first and then writing is the forbidden check-then-act.
`FreezeChangesAsync` is guarded, but it exists only on text-capable stores and also reads the baseline text. So
**every safe option needs new store code**, and a store compiled against 20.27 cannot supply it. No option is
simultaneously safe, source and binary compatible, and behavior compatible for an existing custom store or
decorator. The choice is where the incompatibility lands.

One existing member offers a safe partial release. `ReadPublishBaselineAsync` atomically clears markers that name a
base other than the active version (`IContentAuthoringStore.cs:233-237`). That covers a publisher whose base moved,
never a same-base failure such as an invalid candidate, and it does nothing for the freeze. It appears below as
option A2.

### 3.2 Options and their concrete behavior

| Option | Source | Binary | Runtime for a store or decorator without the new code |
|---|---|---|---|
| **A. Additive companion with both halves**, `IContentConditionalDraftFreeze`: a guarded row-only freeze and a conditional release, required by both pipelines, typed refusal when absent | Compatible. Nothing existing changes. | Compatible. Old assemblies load and run. | A publish, a standalone `ContentPublisher.PrepareAsync` and an upgrade Apply with pending work refuse before they write anything. Boot reads, Preview and runs with nothing pending are unchanged. |
| **A-r. Release-only companion** (the first draft), plus runner row-only plans on text-capable stores sent through `FreezeChangesAsync` | Compatible | Compatible | Same refusal set as A. A store or decorator that declares the release but has no text companion still freezes through the unguarded `FreezeDraftAsync`, and so does `ContentPublisher`'s row route, so declaring the capability does not mean the race is closed for it. |
| **A2.** Companion as in A, but stores without it fall back to the baseline read's stale sweep for release and to `FreezeDraftAsync` for freeze | Compatible | Compatible | Base-moved failures release correctly. The unguarded freeze remains on legacy stores. A same-base failure leaves the draft frozen until host recovery. |
| **A3.** Companion as in A, plus an explicit host opt-in to the old unconditional calls | Compatible | Compatible | Old behavior, defect included, for hosts that opt in. This is the fallback OA14 rules out, so it is listed only to record why it is absent. |
| **G.** Release-only companion, plus the three engine stores' `FreezeDraftAsync` changed to refuse a base that is not active | Compatible | Compatible | Any decorator that forwards `FreezeDraftAsync` to an engine store gains the guard without declaring anything. A custom store's own `FreezeDraftAsync` stays unguarded. Every caller that plants a marker at a non-active base through the released member is refused, which changes a documented contract. |
| **B. Default interface methods** on `IContentAuthoringStore` that report "unsupported", plus a default capability property | Compatible, except that an implementer already holding a public method of the same signature silently becomes the implementation | Compatible | Same refusal as A. The defaults cannot be real implementations because no existing member can supply them, so a third member must say whether the first two work. They can disagree. |
| **C. Required members** on `IContentAuthoringStore` | Breaks every implementer with CS0535, including test doubles that never publish | An old implementer throws `TypeLoadException` when its type loads, even on a host that only boots | After recompiling, full fix everywhere. The compiler forces every decorator to forward both. |
| **I. Internal capability** for the engine's three stores only | Compatible | Compatible | A third-party store can never gain the fix and must be refused permanently. |

On option G. Guarding the engine stores' `FreezeDraftAsync` **would** bind every decorator that forwards that
member to an engine store, including Grimhollow's `CatalogUpgradeRaceStore`, without the decorator declaring a
freeze capability. It is not recommended for three reasons. It changes the documented "OVERWRITES whatever marker
was there" contract of a released member (`IContentAuthoringStore.cs:199-201`) that tests and hosts use to plant and
restore markers (`CatalogPublishActionTests.cs:80`, `PublishCrashSafetyTests.cs:250`, `:262`,
`ContentUpgradeRecoveryTests.cs:451`, among 26 references in the test projects, forwarding doubles included). It binds no custom store. It still needs a new
member for the release, so it does not avoid the companion, it only splits the safety between a changed old member
and a new one.

The existing default member on `IContentAuthoringStore` (`IContentVersionHashSource.GetVersionHashesAsync` at
`:134-139`) is not precedent for B. That member is derived entirely from `GetVersionAsync`. These cannot be.

### 3.3 Scores

Scores are design judgments from 1 to 10, not measurements. Weights reflect a release-blocking correctness fix on a
public provider seam.

| Criterion | Weight | A | A-r | A2 | A3 | G | B | C | I |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Safety: no unconditional or check-then-act path left in a store that declares the capability, no accidental implementation | 4 | 10 | 7 | 7 | 5 | 8 | 7 | 10 | 9 |
| Source and binary compatibility | 3 | 10 | 10 | 10 | 10 | 10 | 9 | 1 | 10 |
| Runtime blast radius limited to where safety requires it | 2 | 7 | 7 | 4 | 8 | 4 | 7 | 2 | 2 |
| Decorator correctness and discoverability | 2 | 7 | 4 | 6 | 6 | 7 | 4 | 10 | 3 |
| Honest version classification and cost | 2 | 6 | 6 | 5 | 7 | 4 | 6 | 3 | 5 |
| Fit with house precedent and a focused change | 2 | 8 | 8 | 4 | 5 | 4 | 5 | 5 | 3 |
| **Weighted total out of 100** | | **84** | 72 | 64 | 68 | 67 | 66 | 55 | 61 |

Totals are the weighted sums out of 150, scaled to 100 and rounded.

Notes on the scores:

- A follows two shipped precedents. `IContentTextAuthoringStore` is a capability companion that pipelines detect by
  type and that also carries its own freeze, commit and discard. `PackVersionPointers.Resolve` refuses an incapable
  pack store at the top of a publish rather than after files are written. A store that declares A is closed for
  the cross-version path on both routes, text and row, so the capability means one thing.
- A-r drops on safety and on decorators because its capability is narrower than the safety it implies. A decorator
  that forwards the release alone passes the gate and still overwrites newer markers.
- G scores well on decorators because forwarding decorators get the freeze guard for free, and poorly on blast
  radius, version honesty and fit because it rewrites a released contract and its test callers.
- A3 scores well on blast radius only because it keeps the bug. A2 keeps the unguarded freeze on legacy stores and
  trades a typed refusal for a frozen-draft wedge that surfaces much later.
- C is the strongest contract, but it breaks every store that never publishes, including the two consumer test
  doubles found in section 9, and its major version buys nothing A lacks for the engine's own stores.

### 3.4 Recommendation

**Option A.** Ship an additive companion holding both the guarded row-only freeze and the conditional release,
implement it atomically in all three engine stores, require it in both pipelines, refuse with a typed reason
before any write when it is absent, and never fall back to an unconditional or unvalidated call. This is the
smallest honest transition. It is source and binary compatible. **It is not behavior compatible** for a custom store
or decorator that publishes or applies pending upgrades without the companion. Such a store is refused rather than
left with the defect, and section 10 treats that as a version question.

## 4. Public contract

### 4.1 The companion

```csharp
namespace KhaozEngine.Catalog.Authoring;

public interface IContentConditionalDraftFreeze : IContentAuthoringStore
{
    Task<ContentDraft> FreezeDraftForBaseAsync(int expectedBaseVersion, CancellationToken cancellationToken = default);

    Task<bool> ReleaseDraftFreezeForBaseAsync(int frozenForBaseVersion, CancellationToken cancellationToken = default);
}
```

The name says what both members share: a freeze conditioned on a base version, taken and given back by the same
publisher. The extension of `IContentAuthoringStore` mirrors `IContentTextAuthoringStore`. Both members are
meaningful only on the store that holds the marker.

### 4.2 `FreezeDraftForBaseAsync`

The row-only freeze, guarded. Unlike the existing text freeze, this new operation runs no preflight
stale-marker sweep. It checks the active base and representability before its single marker write.

1. **It is one atomic step.** Inside the store's own gate or one transaction, in this order: compare
   `expectedBaseVersion` with the active version, check the draft, run the existing row-only representability
   check, write the marker, and read the draft back. No read outside that step decides the write.
2. **A moved base is refused with nothing written.** When `expectedBaseVersion` is not the active version it throws
   `ContentAuthoringException` with `BaseVersionMovedReason`, the same refusal and message shape as
   `FreezeChangesAsync`. A marker naming the active version is left exactly as it is.
3. **An empty or missing draft is refused.** No open draft, or an open draft with no row edits
   (`ContentDraft.EditCount == 0`), throws with `NoOpenDraftReason`. This keeps the refusal
   `ContentPublisher.FreezeAsync` gives today for a draft with no row edits, including a draft that holds only text.
4. **Representability is unchanged.** A draft holding text intents or introductions, or a fork of a row that holds
   text at the active version, is refused with `TextUnrepresentedReason` before any marker is written, exactly as
   `FreezeDraftAsync` does today (`InMemoryContentAuthoringStore.Freeze.cs:42`, `SqliteContentAuthoringStore.Freeze.cs:44-48`,
   `SqlServerContentAuthoringStore.Freeze.cs:50-54`).
5. **The write.** The marker becomes `expectedBaseVersion`. A marker already naming that base, which is a same-base
   rival's or an interrupted run's, is overwritten with the same value (section 8). A stale marker is
   replaced directly only after every check succeeds. There is no separate sweep before or within this call.
6. **The return value** is the open draft exactly as `GetOpenDraftAsync` would return it at that moment, read in
   the same gate or transaction after the write. It is never null, and its `FrozenForBaseVersion` equals
   `expectedBaseVersion`. On a text-capable store it carries the draft's text state, as `GetOpenDraftAsync` does.
7. **Timestamps.** `catalog_draft.updated_at_utc` moves when the marker value changes and stays when the marker
   already named this base, the rule the existing freeze statements follow (`CASE WHEN frozen_for_base_version IS
   $base`). A refused call leaves both the marker and timestamp unchanged, including a stale marker.
8. **Arguments.** A negative base throws `ArgumentOutOfRangeException`. Base 0 is valid, because the first publish
   freezes at 0.
9. **Cancellation.** It honors its token like every member. A cancelled or faulted call either wrote nothing or
   wrote the marker and lost its acknowledgement. Callers therefore record the base before the call and release it
   on every exit, as section 5 requires.

### 4.3 `ReleaseDraftFreezeForBaseAsync`

1. **It is one atomic compare-and-clear.** Inside the store's own gate or transaction, it clears the marker if and
   only if a draft is open and its marker equals `frozenForBaseVersion`. No read outside that step decides the
   write.
2. It returns `true` when it cleared a marker and `false` otherwise. A second call is a no-op that returns `false`.
3. It never touches the draft's base version, edits, text state, language introductions or opener, and it writes
   no audit row. It does not read or compare the active version. A marker naming an older base is stale and cannot
   belong to a publisher that can still commit, so clearing it is safe.
4. `catalog_draft.updated_at_utc` moves only when the call clears a marker.
5. A negative base throws `ArgumentOutOfRangeException`. Base 0 is valid.
6. It honors its token. The pipelines pass `CancellationToken.None`.

### 4.4 Declaring the companion

A type that declares the interface guarantees both contracts for every instance. A decorator declares it only when
its constructor requires an inner store that declares it, and it forwards both members to that store. The
constructor check is what makes the declaration true. Forwarding one member and inheriting nothing for the other
does not compile, so the risk is a decorator over an arbitrary inner store, which the constructor check rules out.

### 4.5 Store implementations

- In-memory, under `_gate`. Freeze: the active check, the empty check,
  `RequireRowOnlyRepresentable(open, nameof(FreezeDraftForBaseAsync))`, then `_draft = Reframe(open,
  open.BaseVersion, open.Changes, expectedBaseVersion)` and return the protected copy `GetOpenDraftAsync` returns.
  Release: clear only when `_draft is { FrozenForBaseVersion: int f } && f == frozenForBaseVersion`, through the
  existing `Reframe` with a null marker.
- SQLite, under the lease. Freeze: one transaction, with no preflight sweep, that reads the active
  version, reads the draft, runs `RequireRowOnlyRepresentableAsync`, runs the same `UPDATE` the existing freeze runs
  with `$base` set to the expected base, and reads the draft back before commit. Release, answered by the row
  count:

  ```sql
  UPDATE catalog_draft SET frozen_for_base_version = NULL, updated_at_utc = $now
  WHERE draft_key = 1 AND frozen_for_base_version = $base;
  ```

- SQL Server: the same two shapes inside the existing `WriteAsync` scope, with `@base` and `@now`.
  Do not copy the preflight sweep from the existing `FreezeChangesAsync`.

No column, index, schema version or migration changes. `frozen_for_base_version` already holds the value.

### 4.6 Existing members re-documented, not changed

- `ClearDraftFreezeAsync` keeps its signature and its unconditional behavior. Its documentation changes from "a
  publish runs this on every exit path" to "explicit host recovery only, after proving no publisher is live". After
  this change no engine pipeline calls it. `CatalogRefusal.cs:86` and `KhaozEngine.Server.Admin/README.md:365`
  already describe it as the host recovery lever and stay valid.
- `FreezeDraftAsync` keeps its signature and overwrite behavior, for stores without the companion, host tooling and
  test setup. Its documentation gains the warning that it does not compare the base with the active version and
  that no engine pipeline calls it after this change. `FreezeDraftForBaseAsync` is the guarded route.

### 4.7 New refusal identifiers

- `ContentAuthoringException.ConditionalFreezeUnavailableReason = "conditional-freeze-unavailable"`, raised by
  `ContentPublishCommit.PublishAsync` and by `ContentPublisher.PrepareAsync` when their store lacks the companion.
- `ContentUpgradeCodes.ConditionalFreezeUnsupported = "KECU0017"`, the runner's `Unsupported` stop in Apply and its
  note in Preview. `KECU0016` is the highest code in use (`ContentUpgradeDiagnostic.cs:100`).

The admin console's `catalog-publish` reaches the store's own `PublishAsync`, and every engine store builds its
`ContentPublishCommit` over itself, so the console never meets the refusal on an engine store. The implementation
confirms that the default branch of `CatalogRefusal` (`:174`) carries an unknown reason through, and adds a mapping
only if that branch loses it.

## 5. Pipeline behavior

### 5.1 `ContentPublishCommit`

- The constructor resolves `_store as IContentConditionalDraftFreeze` into a private field and does not throw, so
  callers that construct the type only for `WriteAsync` or `SweepAsync` are unaffected.
- `PublishAsync` refuses with `conditional-freeze-unavailable` **before its `try`**, so a refused publish reads
  nothing, freezes nothing, writes no file and releases nothing.
- The text route is unchanged. It freezes through the atomic `FreezeChangesAsync(request.ExpectedBaseVersion)`.
- The row route's freeze moves into `ContentPublisher` (5.2).
- The base it releases is `request.ExpectedBaseVersion`, known before any store call, so a freeze whose
  acknowledgement was lost is still released. It is exactly the marker either route writes: `FreezeChangesAsync`
  writes the active version only when it equals the expectation, and `FreezeDraftForBaseAsync` writes the
  expectation only when it is active.
- `ClearTheFreezeAsync` becomes `ReleaseDraftFreezeForBaseAsync(request.ExpectedBaseVersion,
  CancellationToken.None)`, still in the `finally` and still swallowing its own failure.

Narrowing the `finally` to "only after a freeze was attempted" was considered. On the text route nothing precedes
the freeze, and on the row route it would only exclude baseline-read faults. Skipping the release after a typed
refusal from the freeze would rely on every custom store refusing before it writes. Neither is selected. The
same-base residue in section 8 covers what remains.

### 5.2 `ContentPublisher`

- `FreezeAsync` keeps its first check, `request.ExpectedBaseVersion` against `baseline.VersionNumber`, which costs
  nothing and keeps today's message.
- It then calls `FreezeDraftForBaseAsync(baseline.VersionNumber, cancellationToken)` on its own store and returns
  that draft. The separate `GetOpenDraftAsync` read and the `FreezeDraftAsync` call (`ContentPublisher.cs:257-268`)
  are removed.
- A store without the companion is refused with `conditional-freeze-unavailable` before the draft is read. Inside
  `ContentPublishCommit` this is unreachable for the commit's own store, which already refused. It matters for a
  standalone `PrepareAsync` caller and for a publisher built over a different object than its commit. There is no
  fallback to `FreezeDraftAsync`.

**What the returned draft fixes, and what it does not.** Today the row route plans the draft it read **before**
the freeze. An edit landing between that read and the freeze is not in the plan, and the row commit removes the
plan's targets from the draft by target (`InMemoryContentAuthoringStore.Freeze.cs:138-165`), so a gap edit to a
target the plan already holds is removed unpublished. Planning the draft returned from the atomic freeze closes
that gap for every store that declares the companion. It does **not** make the row commit verify the draft or the
marker (`InMemoryContentAuthoringStore.Publish.cs:92-112`). A same-base rival that overwrites and then releases the
marker during the row route's window can still let an edit in, and the row commit will not notice. That is
same-base residue on the row route only (section 8). Engine stores never take the row route through their own
publish, so it concerns custom row-only stores and row-only views.

### 5.3 `ContentUpgradeRun` freeze and release

- `ContentUpgradeTextRoute` resolves `store as IContentConditionalDraftFreeze` next to its text companion.
- `FreezeAsync` keeps the text route: a plan carrying text freezes through `FreezeChangesAsync(active)` and returns
  the snapshot's draft. **Every other plan, on every store, freezes through `FreezeDraftForBaseAsync(active)` and
  returns its draft.** The separate `GetOpenDraftAsync` read-back after a row-only freeze is removed. There is no
  fallback to `FreezeDraftAsync`. A store without the companion never reaches this call, because the gate in 5.4
  stopped the run, and the route throws `InvalidOperationException` if it is reached anyway, as `Companion()` does
  for text today.
- `bool _frozenForPublish` becomes `int? _frozenForBaseVersion`, set to the exact value passed to
  `Route.FreezeAsync` immediately before that call (`ContentUpgradeRun.Publish.cs:216`). It is captured from the
  call argument and never re-read from `Active`, which moves during a run. A lost freeze acknowledgement is still
  released.
- `ReleaseFreezeAsync` reads and nulls the field, then calls `ReleaseDraftFreezeForBaseAsync(captured,
  CancellationToken.None)`. Both call sites stay where they are, the obstruction release at `:144` and the
  `finally` at `:185`.
- The successful exit still clears the field without releasing, because the store's own publish took the marker
  over and released it at the same base.

Consequences the implementation must verify against the existing upgrade suites:

- A row-only freeze over a draft with no row edits is refused with `no-open-draft`, which is contention, where
  today `FreezeDraftAsync` succeeds and the read-back fails `IsPlan` into the obstruction path.
- A runner whose base moved is refused at its own freeze with `base-version-moved`, contention, instead of
  overwriting the newer marker and failing `IsPlan` on the read-back.
- `ContentUpgradeTextMatch.IsPlan` sees the same draft shape `GetOpenDraftAsync` returns today, so its rules do not
  change. The first draft's extra baseline text read per publish attempt is gone.

### 5.4 Runner gate, precedence and Preview

The gate sits in `ContentUpgradeRunner.GatesAsync` **immediately after gate 4 finds pending work**
(`ContentUpgradeRunner.cs:163-170`) and before gate 5 reads the open draft. Precedence, with nothing reordered among
the existing gates:

1. `KECU0001` no ledger, `KECU0002` no catalog, `KECU0003` catalog ahead of build, `KECU0012` up to date. All
   unchanged and all ahead of the new gate, so a host that only boots, and `RecordBaselineAsync`, never meet it.
2. **New:** the store lacks `IContentConditionalDraftFreeze`.
   - Apply stops with `Unsupported` and `KECU0017`, naming the store type and the migration. Nothing is read
     beyond the ledger list and the active version, and nothing is written: no draft, no marker, no ledger row, no
     version.
   - Preview adds `KECU0017` as a note that changes no outcome, worded for Preview ("an apply on this store is
     refused before any write"), and continues. The precedent for one code with mode-specific wording is
     `KECU0010` (`ContentUpgradeRun.Drafts.cs:128-134`).
3. `KECU0004` operator draft, `KECU0005` pinned elsewhere, `KECU0007` baseline moved, then Preview or Apply. All
   unchanged for a capable store, and unchanged in Preview for an incapable one.

Why after gate 4 and not after gate 7. Gate 5 adds `KECU0010` for a recovered draft, and its Apply wording says the
run "publishes that draft as it stands" (`ContentUpgradeRun.Drafts.cs:133-134`). An Apply stopped after that line
would contradict its own report. Placing the store-type refusal at the first point where pending work is known
mirrors the ledger gate, the other store-type refusal, and leaves every catalog-state diagnosis to stores that can
act on it. The cost is that an incapable store's Apply reports `KECU0017` instead of, say, `KECU0005` when both
apply. Both say "nothing was changed".

**Adoption is refused too, deliberately.** An `AlreadySatisfied` plan writes only a ledger row
(`ContentUpgradeRun.cs:270-281`) and needs no freeze, so the gate refuses some runs that would never have frozen
a draft. The runner cannot know that for any definition after the first without publishing the first, because a
later plan reads the published result of the one before it. Refusing up front keeps "nothing was written" true for
every refused run. The alternative, a lazy check at the first publish, lets adoption rows land and then stops
mid-run. It is the owner's choice (section 13), and this design recommends the up-front gate.

Preview stays useful on a legacy store. It plans the first pending definition, reports `WouldPublish` or
`WouldAdopt` exactly as today, keeps `KECU0013` and its outcome, and carries the `KECU0017` note so an operator
learns before an Apply that it will be refused.

### 5.5 Lifecycle

`B` is the base a publisher recorded. A newer marker names `B+1` or later.

| Event | Behavior after this change |
|---|---|
| Success | The commit consumed the draft. The publish `finally` releases `B`, which is a no-op on a later draft frozen at a newer base. The runner owes nothing. |
| Base moved before the freeze, either route, runner or console | Both routes refuse with `base-version-moved` and preserve any active-base marker. The new row freeze writes nothing. The unchanged text freeze may first clear a stale marker and update its timestamp, including when it later refuses or is cancelled. It never clears the active-base marker. The release of `B` is a no-op on a newer marker. |
| Base moved after the freeze | The commit's number confirmation refuses. The release of `B` clears only this publisher's marker if it still names `B`, never a newer one. |
| Same-base failure after the freeze: invalid candidate, pack write fault, text chunk mismatch, provider fault | The release of `B` clears the marker naming `B`. That is this publisher's, or a same-base rival's (section 8). |
| Cancellation | Released on `CancellationToken.None`, unchanged. Same-base residue as the row above. |
| Double cleanup, the runner's `finally` and the pipeline's `finally` | Both release `B`. The second returns `false` unless another freeze at `B` landed in between (section 8). |
| Lost freeze acknowledgement | `B` was captured before the call, so the release runs. It clears the marker if it was written, and is a no-op or same-base residue if not. |
| Lost commit acknowledgement | The version is live. The release of `B` cannot clear a newer marker. The runner reads the ledger and adopts, unchanged. |
| The release itself fails or its acknowledgement is lost | The publish swallows it, unchanged, and the runner surfaces it as today. A marker left naming `B` is cleared by the next publish at `B`, by the stale sweep once the base moves, or by host recovery. |
| Process crash after the freeze | No release. Recovery is unchanged: the next publish at `B`, the stale sweep, or explicit host recovery. |
| An old publisher resumes after a new draft was opened or frozen | Its freeze, if it has not frozen yet, is refused. Its release of `B` is a no-op on any marker naming a newer base. The new publisher's commit finds its own marker. This is the #1271 case. |
| Host recovery | `ClearDraftFreezeAsync`, unconditional and explicit, only after proving no publisher is live. |

## 6. Guarantees and their limits

**The guarantee.** Among publishers running the updated `ContentPublishCommit`, `ContentPublisher` and
`ContentUpgradeRunner` against stores that declare the companion, directly or through a decorator that meets 4.4:

- no publisher writes a marker for a base that is not the active version, on the text route or the row route, and
- no publisher clears a marker naming a base other than the one it recorded.

So a marker naming the active version can be written or cleared only by a publisher that recorded that version, by
the stale sweep, which never touches it, or by explicit host recovery. The cross-version clear behind #1271 cannot
happen between such publishers, whatever their mix of runners and console publishes, text and row-only plans.

**What it does not cover.**

- **Same-base publishers.** Section 8.
- **Direct calls to the legacy members.** `FreezeDraftAsync` and `ClearDraftFreezeAsync` stay public and
  unconditional. Host code, tools or tests that call them bypass the guarantee. `ClearDraftFreezeAsync` is the
  documented recovery lever and is safe only after proving no publisher is live.
- **Mixed engine versions on one catalog.** A replica still on 20.27 or earlier runs the unconditional release and
  the unguarded row-only freeze. The guarantee holds once every writer to a catalog runs the updated engine. A
  rolling deploy that overlaps old and new replicas keeps the old exposure for the overlap. The changelog says so.
- **Contract trust.** The pipelines trust a declared companion. A decorator that declares it without an atomic
  inner implementation reintroduces the defect for its own deployment.
- **The row commit.** It still does not verify the draft or the marker. Section 5.2 states what that leaves.

## 7. Protections preserved

- The complete-draft confirmation in all three text commits is unchanged, including the marker check, so nothing
  edited during a freeze gap is ever published through the text route.
- Typed stale-plan errors are unchanged: `base-version-moved` from either freeze and from the number confirmation,
  and `text-state-mismatch` for a draft that changed. No new contention reason is added and `IsContention` is not
  touched.
- Ledger rows, version numbers, row ids, carried ids and family blocks are untouched. The release writes only the
  marker column and its timestamp.
- Operator edits are never discarded by a freeze or a release. `TryDiscardChangesAsync` still deletes only an exact
  unfrozen match, and the runner still classifies a merged draft as operator work.
- Row-only representability refusals are unchanged and still come before any marker is written.
- Stale-marker sweeps in `ReadPublishBaselineAsync` and `FreezeChangesAsync` are unchanged.

## 8. Residue

**Same-base release.** The marker is one value with no owner identity, so two publishers frozen at the same active
base `B` are indistinguishable. If publisher P leaves without committing while publisher Q sits between its freeze
and its commit, P's release clears the marker Q relies on. P can leave that way through cancellation, a pack write
fault, a provider fault, a candidate that fails validation (possible when replicas run different builds), a
same-base refusal before its freeze that still reaches the `finally` (for example `no-open-draft` or
`text-provenance-unknown`), or the runner's obstruction release, which is residue 3 in
`KhaozEngine.Catalog.Authoring/README.md`. Two same-base freezes also overwrite each other with the same value,
which is harmless by itself.

- Effect on a console publish Q: a typed `text-state-mismatch`. The draft is intact and a republish works.
- Effect on a runner Q: it discards its own unfrozen draft, finds no ledger row, and reports `KECU0009` because the
  reason is not contention. A boot in that deploy fails and a restart retries.
- Safety on the text route: during the gap, edits to Q's draft are accepted, and Q's complete-draft check then
  refuses its commit. Nothing unreviewed is published, no edit is lost and no version is duplicated.
- On the row route the commit does not check, so a gap edit can be removed unpublished or carried into the next
  draft unpublished, as today. Only custom row-only stores and row-only views take that route.
- Likelihood: two same-base publishers plus a non-committing exit inside the other's window. The observed failures
  were cross-version, which this design closes.

A distinct changed-frozen-draft reason treated as contention could improve the **text-route runner's
retry and boot outcome**. It would not prevent marker theft or repair the row-only commit, which raises no
such refusal. The row-only edit-loss limitation is tracked as [#1311](https://github.com/APKiwiOrg/KhaozEngine/issues/1311)
and requires its own commit-confirmation or ownership repair.
A durable owner token is a broader ownership direction, not a design supplied by this batch.
**None of these follow-ups is implemented or selected here.** Accepting the text-route boot limitation
does not imply that a text retry would close the separately recorded row-only risk.

## 9. Custom stores, decorators and known consumers

A custom store gains the fix by declaring `IContentConditionalDraftFreeze` and implementing both members as in
4.2 to 4.5. A decorator follows 4.4. A decorator that forwards `PublishAsync` to an inner engine store is already
safe inside that publish, because the inner store builds its `ContentPublishCommit` over itself. It still needs the
companion when it is handed to `ContentUpgradeRunner.RunAsync`, because the runner freezes and releases through
the object it was given.

In-repo doubles that implement `IContentAuthoringStore` and drive a pipeline (search with `rg`, which honors `\b`):

- `KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs:16` `ForwardingContentAuthoringStore` and its
  subclasses, including `CrashAfterCommitStore` (`:570-605`), which builds its commit over itself at `:604`, and
  `ContentUpgradeFreezeReleaseTests.cs:210` `ForwardingUpgradeStore` with `:354` `CountsFreezeReleasesStore`,
  which counts `ClearDraftFreezeAsync` calls today.
- `KhaozEngine.Catalog.Tests/Upgrade/TextUpgradeDoubles.cs:16` `TextUpgradeStore`, which forwards the text
  companion and the ledger over the in-memory store only, and `DraftRaceStore.cs:38`.
- `KhaozEngine.Server.Tests/Catalog/ForwardingContentAuthoringStore.cs:13` and its subclasses.
- `KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs:16` `RowOnlyStoreView` and `:234` `TextStoreDouble`. The
  legacy-route tests in `TextLegacyCompatibilityTests.cs` and `ContentAuthoringTextStoreConformance.cs:294` publish
  through `RowOnlyStoreView`.
- `KhaozEngine.Server.Tests/Catalog/WriteRefusingAuthoringStore.cs:23` and `UpgradeDraftRaceStore.cs:36`.

Read-only consumer check, at Grimhollow `d191cd01` (also `.worktrees/world-authoring` `51837c4b` and
`.worktrees/grand-world` `40f74288`) and Ruinborne `4da9ba36`. This does not exhaust external implementations.

- Production code in both games constructs only the engine's SQLite, SQL Server and in-memory stores
  (`GrimhollowCatalogDatabase.cs:161-162`, `RuinborneCatalogDatabase.cs:145-147`). Under option A it needs no change.
- `Grimhollow.Tests/Server/CatalogUpgradeRaceStore.cs:7-11` decorates `IContentAuthoringStore` and
  `IContentUpgradeLedger` without the text companion. It forwards `FreezeDraftAsync` (`:122-123`),
  `ClearDraftFreezeAsync` (`:125-126`) and `PublishAsync` to the real store (`:51-58`), and
  `ConcurrentCatalogUpgradeTests.cs:24-25` and `:79` hand it to the runner with pending work. Under option A those
  tests report `KECU0017` until the double changes at Grimhollow's next engine pin. **The handoff needs all three:**
  declare `IContentConditionalDraftFreeze`, forward both `FreezeDraftForBaseAsync` and
  `ReleaseDraftFreezeForBaseAsync`, and require the inner store to declare it in the constructor, the way `:11`
  already casts the ledger. Forwarding the release alone would leave its runner freezing through nothing at all,
  since the runner no longer calls `FreezeDraftAsync`. Forwarding `IContentTextAuthoringStore` as well is optional.
  It would make those tests exercise the production text route, but safety does not depend on it.
- `ScriptedAuthoringStore.cs:34` in both games implements `IContentAuthoringStore` for boot and seed failure
  scripts, and its `ClearDraftFreezeAsync` throws because it is never reached. Under option A it is unaffected,
  because the runner asks only after pending work is found and those scripts fail earlier. Under option C both
  games would need two new members.

## 10. Version disposition

Facts: `Directory.Build.props:25` stages `20.27.1`, untagged. The newest tag, locally and on `origin`, is
`v20.27.0`, which is immutable and is never moved. No `v20.28.0` or `v21.*` tag exists today. The staged entry
(`CHANGELOG.md:8-13`) is a publication repair that says shipped APIs are unchanged. The written rule is "SemVer is
additive minor, fix patch and breaking major" (`docs/CONTRIBUTOR-RULES.md:110-111`). It does not say whether a
runtime refusal of a previously working configuration is breaking.

What is settled:

- This change adds public API: one interface with two members, one reason constant and one diagnostic code. It
  cannot ship as a patch, so it cannot ride as `20.27.1`.
- It refuses a conforming 20.27 custom store or decorator that publishes or applies pending upgrades without the
  companion. Source and binary compatibility hold. Runtime behavior for those implementers does not.

What is the owner's choice, `20.28.0` or `21.0.0`:

- **`20.28.0`, the next free minor, with a flagged behavior change.** House practice ships fail-closed behavior
  changes on released types as minors with a named note: `TileActorHost.Add` refusing an oversized leash in 18.13.0
  (`CHANGELOG.md:4879`), the session eviction change in 17.38.0 (`:7565`) and the stricter document loading in
  20.27.0 (`:23`). The 20.0.0 major was driven by source-breaking removals and required members
  (`CHANGELOG.md:1585-1591`), not by a runtime refusal alone. Known consumers' production code is unaffected, and
  the refusal is typed and comes before any write. The cost is that an unknown external provider that publishes
  stops publishing after a minor update, which a strict SemVer reader would not expect.
- **`21.0.0`, the strict runtime compatibility reading.** A previously working implementer stops working, so a
  major is the loudest signal to external implementers whose existence nobody can rule out. The cost is a major
  for an additive, source and binary compatible change, which dilutes what a major means in this repository. The
  known consumers vendor a pinned engine, so the signal mostly reaches the implementers nobody has seen.

**Recommendation: `20.28.0`** with a "Behavior change for custom catalog stores" note that names both members, the
refusal, the Preview note and the decorator migration, plus the rollout note from section 6. Prefer `21.0.0` if the
owner knows of external provider implementers who update without reading the changelog, or if other breaking work
is already queued for a major. Either way the version bump and its changelog entry land in one commit, and this
design reserves nothing.

**Sequencing with the staged repair.** Under the ride rule, landing this on `main` before `20.27.1` is tagged folds
the publication repair into the new version. Grimhollow's next engine pin is waiting on that repair. If the owner
wants it published as `20.27.1`, that tag happens before this batch reaches `main`. That is a release decision for
the owner, not part of this design. Re-read `main`, the version and the tags when the owner decides.

## 11. Deterministic tests and evidence

There is no timing machinery. Every pause is a `TaskCompletionSource` that a test decorator awaits at an async entry
or exit point. Each interleaving is forced once. Nothing loops, and no stress workflow runs without the owner's
separate permission. The existing 20-iteration `ContentUpgradeConcurrencyTests` stay unchanged as the statistical
net, and their summary comment at `:27-29` is corrected to name separate replicas.

"In-memory" means one shared `InMemoryContentAuthoringStore`, the only way that store can be shared. "SQLite
replicas" means two `SqliteContentAuthoringStore` instances on one database file, which reproduces separate
connections like the hosted failure. Each publisher or runner gets its own decorator instance, so a pause belongs
to one participant.

### 11.1 Harness rules for the race reproductions

These rules exist so a test cannot pass on old code because nothing checked the marker.

- **H1. Text route.** Every decorator for a participant whose commit must check the marker declares and forwards
  the whole `IContentTextAuthoringStore`, and `IContentUpgradeLedger` for runners. The row-only decorators in
  `UpgradeStoreDoubles.cs` and `ContentUpgradeFreezeReleaseTests.cs:210` do not qualify. Their commits take the row
  route, which never reads the marker.
- **H2. Commit over the decorator.** Its `PublishAsync` builds `ContentPublishCommit` and `ContentPublisher` over
  the decorator itself (the `UpgradeStoreDoubles.cs:604` shape with the text companion added), so the pause at
  `CommitTextPublishAsync` is in the decorator. Each test asserts the paused participant's commit went through
  `CommitTextPublishAsync`, so a harness that silently took the row route fails instead of passing.
- **H3. Pause on the API actually called.** A pause "at the runner's freeze" covers `FreezeDraftAsync` on the
  baseline and `FreezeDraftForBaseAsync` once task 3 adds it. A pause "after the freeze" is at the exit of whichever
  freeze call returned, because the fixed row route has no separate read-back to pause on. A hold "after its first
  release" is at the exit of the participant's first `ClearDraftFreezeAsync` on the baseline or first
  `ReleaseDraftFreezeForBaseAsync` on the fix, which both paths reach, so the rival resumes only once the release
  under test has run.
- **H4. One body, two implementations.** Task 0 doubles use only the existing API. Later tasks add the companion
  declaration and forwarding to the doubles, never to test bodies, so the same assertions run red on the baseline
  and green on the fix.
- **H5. The red must be the named one.** Each red run must fail with the refusal named below. A test that passes
  on the baseline, or fails for any other reason, stops implementation: either the diagnosis is incomplete or the
  harness is wrong.
- **H6. Row-only newer drafts.** The newer draft D2 in T2p, T3o, T4 and T4r holds row edits only, so the baseline
  row-only freeze reaches its overwrite instead of refusing on representability, which would hide the defect.

### 11.2 The tests

| Id | Location | Forced sequence | Expected after the fix |
|---|---|---|---|
| T1 | `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.Freeze.cs`, new virtual facts. In-memory and SQLite through their conformance classes, SQL Server through one-line `[CatalogSqlServerFact]` overrides in `SqlServerContentAuthoringStoreConformanceTests.cs` | Freeze: seed version 1 and a row draft. Freeze for 0, for 1, for 1 again. Publish to 2, edit, freeze for 2, then freeze for 1. No draft, freeze for 1. At active 2, plant stale marker 0 with `FreezeDraftAsync`, freeze for expected 1 and then for active 2. Negative base. Release: with marker 1, release 0, then 1, then 1 again. With marker 2, release 1. No draft, release 5. Negative base. | Freeze for 0 refused `base-version-moved` with no marker. Freeze for 1 returns the draft frozen at 1, equal to `GetOpenDraftAsync`. The repeat returns the same draft with `updated_at_utc` unchanged. Freeze for 1 against marker 2 is refused and marker 2 and its timestamp stand. No draft refused `no-open-draft`. Expected 1 refuses with stale marker 0 and its timestamp unchanged. Expected 2 then replaces it and stamps the change. `ArgumentOutOfRangeException`. Release results `false`, `true`, `false`, marker 2 survives, `false`, `ArgumentOutOfRangeException`. `ClearDraftFreezeAsync` still clears marker 2 unconditionally. Edits and audit count unchanged by every call. |
| T1t | `ContentAuthoringTextStoreConformance.cs` with the SQL Server override, `SqliteCatalogRowTimestampTests.cs`, `SqlServer/SqlServerCatalogRowTimestampTests.cs` | Text-only draft, text-and-row draft and a text fork, each through `FreezeDraftForBaseAsync`. A text draft frozen through `FreezeChangesAsync` and released at its base and at another base. | Text-only refused `no-open-draft`. Text-and-row and the fork refused `text-unrepresented` with no marker. Text state and introductions untouched by releases. `updated_at_utc` moves only on a marker change or a `true` release. |
| T2 | New `KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeReleasePublishTests.cs`, theory over in-memory and SQLite replicas | P1 expecting 1 freezes and parks at its commit. P2 at 1 commits version 2. An edit opens D2. P3 expecting 2 freezes D2 and parks at its commit. P1 resumes, is refused, and its `finally` completes. P3 resumes. | P1 refused `base-version-moved`. D2's marker is still 2. P3 commits version 3. No draft remains. |
| T2p | Same file. P1 publishes through a row-only decorator that builds its commit over itself, the row route | P1's row route has read its baseline (1) and passed its expectation check, and parks at the entry of its freeze step (`GetOpenDraftAsync` on the baseline, `FreezeDraftForBaseAsync` on the fix). P2 commits version 2. An edit opens D2. P3, on the text route, freezes D2 at 2 and parks at its commit. P1 resumes and completes, then P3 resumes. | P1's freeze refused `base-version-moved`. D2's marker is still 2. P3 commits version 3. |
| T2r | Same file | P1 and P2 freeze one draft at the same base. P1 is cancelled inside its pack write while P2 parks at its commit. An edit is applied in the gap. | Pins the documented residue: P2 refused `text-state-mismatch`, nothing published, the draft keeps every edit including the gap edit. |
| T3 | New `KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeReleaseUpgradeTests.cs`, theory over in-memory and SQLite replicas | The hosted sequence: runner B parks at its first commit on base 1. Runner A recovers B's draft (`KECU0010`), publishes 2, freezes the second upgrade at 2 and parks at its commit. B resumes and is held at its first `GetOpenDraftAsync` after the refusal, which is after both of its releases. A resumes, then B. | Both reports succeed. A has `KECU0010` and no `KECU0009`. B adopts both upgrades (`KECU0011`). Three versions, ledger rows Applied at 2 and 3, no draft, row ids 1 and 2. |
| T3o | Same file | Runner R's row-only plan is written. R freezes at 1 and parks after its freeze. Console P1 expecting 1 commits that draft as 2. An edit opens D2. P2 expecting 2 freezes D2 and parks at its commit. R resumes and is held after its first release (H3). P2 resumes, then R. | R's own publish refused `base-version-moved` at `FreezeChangesAsync(1)`. Both of R's releases of 1 return `false`. D2's marker is still 2. P2 commits 3. R records its upgrade once, with no `KECU0009`. |
| T4 | Same file. R's decorator is text capable (the hosted configuration) | Same as T3o, but R parks **before** its freeze, after writing its draft. R resumes into its freeze at 1 and is held after its first release. P2 resumes, then R. | R's freeze refused `base-version-moved`. D2's marker is still 2. P2 commits 3. R records its upgrade once, with no `KECU0009`. |
| T4r | Same file. R's decorator is row-only plus ledger and forwards `PublishAsync` to the inner engine store, the Grimhollow `CatalogUpgradeRaceStore` shape. P1 and P2 follow H1 and H2 | Same as T4. | Same as T4. This is the F1 proof: a release-only companion would still fail it. |
| T5 | `ConditionalFreezeReleasePublishTests.cs` and `ConditionalFreezeReleaseUpgradeTests.cs` | Publish: `ContentPublishCommit` over a view without the companion. `ContentPublisher.PrepareAsync` alone over that view. Runner over a ledger-bearing view without the companion: nothing pending, pending in Preview, pending in Apply, pending in Apply with the first definition `AlreadySatisfied`, pending in Apply with an operator draft open, with a pin elsewhere, with a stale expected version. A ledgerless view. A decorator that declares and forwards the companion, and one whose constructor is given an inner store without it. | Publish refused `conditional-freeze-unavailable` with no read, no marker, no pack object, no version, unchanged audit and no release call. `PrepareAsync` refused the same way before reading the draft. Nothing pending: `KECU0012` exactly as today and no `KECU0017`. Preview: the same steps and `KECU0013` outcome a capable store reports, plus one `KECU0017` note, nothing written. Every Apply with pending work: `Unsupported` and `KECU0017` with no draft, marker, ledger row or version, including the adoption case and ahead of `KECU0004`, `KECU0005` and `KECU0007`. Ledgerless: `KECU0001` first, unchanged. The forwarding decorator behaves exactly as the inner store. The bad construction throws. |
| T6 | `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.UpgradeRace.cs`, with a SQL Server override | T3 and T4 expressed as conformance facts | Same as T3 and T4, on SQL Server in the existing catalog SQL Server CI job (`.github/workflows/ci.yml:141-170`). Not run locally. |

### 11.3 The gate each race test hits

"Baseline" is `ca13d62d7` behavior with the task 0 doubles. "Fix" is the same test body after the task that turns it
green.

| Id | Baseline: deciding calls and gate | Baseline result | Fix: deciding calls | Fix result |
|---|---|---|---|---|
| T2 | P1's `finally` calls `ClearDraftFreezeAsync`, clearing marker 2. P3's `CommitTextPublishAsync` finds no marker equal to active 2 (`TextPublish.cs:59-64`). | P3 refused `text-state-mismatch` | P1's `finally` calls `ReleaseDraftFreezeForBaseAsync(1)`, which returns `false` | P3 commits 3 |
| T2p | P1's row route calls `GetOpenDraftAsync`, which reads D2, then `FreezeDraftAsync(1)`, which overwrites marker 2. P3's text commit marker check. | P3 refused `text-state-mismatch` | P1's `FreezeDraftForBaseAsync(1)` refuses atomically. Its release of 1 returns `false`. | P3 commits 3 |
| T2r | `ClearDraftFreezeAsync` from P1. P2's text commit marker check. | P2 refused `text-state-mismatch` | `ReleaseDraftFreezeForBaseAsync(1)` returns `true` on P2's same-base marker | Same refusal. A residue pin, green on both |
| T3 | B's commit `finally` and B's runner `finally` both call `ClearDraftFreezeAsync`, clearing marker 2. A's `CommitTextPublishAsync` marker check. `IsContention` excludes the reason. | A reports `KECU0009` | B's two releases of 1 return `false`. A's runner freezes through `FreezeDraftForBaseAsync`. | Section 11.2 T3 |
| T3o | R's `FreezeDraftAsync(1)` returns, then R's separate `GetOpenDraftAsync` read-back sees D2. `IsPlan` fails and the obstruction release calls `ClearDraftFreezeAsync`, clearing marker 2. P2's text commit marker check. | P2 refused `text-state-mismatch` | R's `FreezeDraftForBaseAsync(1)` returned its own draft before the pause, so `IsPlan` passes. R's store publish `FreezeChangesAsync(1)` refuses. Both releases of 1 return `false`. | P2 commits 3 |
| T4 | R's `FreezeDraftAsync(1)` overwrites marker 2 with 1. P2's text commit already fails the marker check whatever R releases next. | P2 refused `text-state-mismatch` | R's `FreezeDraftForBaseAsync(1)` refuses atomically | P2 commits 3 |
| T4r | As T4, reached through `ContentUpgradeTextRoute.FreezeAsync:105` because R has no text companion | P2 refused `text-state-mismatch` | As T4, through the decorator's forwarded `FreezeDraftForBaseAsync` | P2 commits 3 |

T4 and T4r are red on the baseline for the overwrite alone, so they would stay red under a release-only fix. That is
what proves the freeze half is required.

### 11.4 Existing tests that change and evidence

`ContentUpgradeFreezeReleaseTests.cs` counts the conditional release instead of `ClearDraftFreezeAsync`, and every
pipeline test asserts zero calls to `ClearDraftFreezeAsync` and `FreezeDraftAsync`. The doubles in section 9 declare
and forward the companion with the constructor check of 4.4, except one view kept without it as the T5 proof.
Legacy-route tests that need a capable row-only view use a forwarding variant.

Evidence required before integration:

1. An `rg` inventory of `IContentAuthoringStore` implementers at the implementation head.
2. Task 0's single red run of T2, T2p, T3, T3o, T4 and T4r against the baseline, each with its refusal line
   captured in a log, and T2r green.
3. One green run of each focused test after its task, run serially through the shared slot.
4. One full Release build, test and format run at the finish, with exit codes.
5. Hosted CI green on every leg, including the Metal SQLite leg and the catalog SQL Server job. Unchanged workflow
   reruns are not evidence.

## 12. Implementation task boundaries

At design granularity. The implementation plan follows only after this design is approved. Tasks run serially and
only one builds or tests at a time.

0. **Red-first reproductions, no production change.** Write the H1 and H2 doubles and T2, T2p, T2r, T3, T3o, T4
   and T4r against the existing API only, on top of `ca13d62d7`. Run them once and record the red lines (evidence
   item 2). Commit the tests on the fix branch. The branch is not merged until they are green, and a pushed CI run
   of this commit alone is expected red and is not evidence either way.
1. **Contract and stores.** New `KhaozEngine.Catalog.Authoring/IContentConditionalDraftFreeze.cs`, both members in
   the three `*ContentAuthoringStore.Freeze.cs` partials and the class declarations, T1 and T1t with the SQL Server
   overrides, and the XML documentation for `ClearDraftFreezeAsync` and `FreezeDraftAsync`. Pipelines are
   untouched, so the task 0 tests stay red for their original reasons.
2. **Publish pipeline.** `Publish/ContentPublishCommit.cs`, `Publish/ContentPublisher.cs`, the reason constant in
   `ContentAuthoringException.cs`, the publish half of T5, and the companion declaration with forwarding on every
   race double, including the runner doubles. That last part keeps T3, T3o, T4 and T4r failing for their task 0
   reason rather than for the new refusal. T2 and T2p turn green. T2r stays as it was.
3. **Upgrade runner.** `Upgrade/ContentUpgradeTextRoute.cs`, `Upgrade/ContentUpgradeRun.Publish.cs`, the gate and
   Preview note in `ContentUpgradeRunner.GatesAsync`, `KECU0017` in `ContentUpgradeDiagnostic.cs`, the runner half
   of T5, T6, the H3 pause on the new freeze member and the remaining upgrade doubles. T3, T3o, T4 and T4r turn
   green.
4. **Docs and release text.** `KhaozEngine.Catalog.Authoring/README.md` (the publish window and residue section
   around `:826-879`, including the inaccurate "A rival RUNNER loses nothing" line), a check of
   `KhaozEngine.Server.Admin/README.md:365` and `CatalogRefusal.cs:86`, `docs/USING-KHAOZENGINE.md` if it describes
   the freeze, a Markdown sweep for all four member names, and `CHANGELOG.md` under the version the owner chooses,
   with the version bump in that same commit.

Out of scope for every task: schema changes, an owner token, R2, widened fault handling, consumer repository edits
and any release tag.

## 13. Owner decisions

1. **API shape.** Recommended: option A, the two-member companion. The nearest alternatives are A-r with corrected
   claims and a decorator that must also forward the text companion, or G, which changes the released
   `FreezeDraftAsync`.
2. **Version.** Recommended: `20.28.0` with a flagged behavior change. `21.0.0` is the strict reading and is
   defensible. `20.27.1` cannot carry new public API. `v20.27.0` is never moved. Whether `20.27.1` is tagged before
   this lands is a separate release decision.
3. **Runner gate placement.** Recommended: refuse every Apply with pending work up front, adoption included. The
   alternative is a lazy check at the first publish that lets adoption rows land first.
4. **Preview on a legacy store.** Recommended: keep the preview result and add the `KECU0017` note. The
   alternative is a silent Preview, which leaves the operator to find the refusal in the Apply.
5. **Same-base residue.** Recommended: keep this batch focused on cross-version interference and track the
   text-route boot/refusal limitation separately from the preexisting row-only edit-loss risk. A distinct
   text-refusal retry is an alternative only for the text-route runner outcome. It cannot close row-only
   edit loss. Expanding either repair needs a separate scope decision, with no owner-token design implied.
6. **Consumer handoff.** Recommended: the root files a Grimhollow issue for `CatalogUpgradeRaceStore` to declare the
   companion, forward both members and require it of its inner store at construction, at the next engine pin.

## 14. Remaining risks

- No deterministic reproduction exists yet. Task 0 must go red with the named refusals before any production
  change. If T3 passes on the baseline, the diagnosis is incomplete and implementation stops.
- External implementers are unknown. The refusal is loud and typed, but it is a behavior change wherever they
  exist.
- Mixed engine versions on one catalog keep the old exposure until every writer is updated.
- The runner's row-only freeze now refuses a moved base and an empty draft at its own freeze rather than later.
  T3 to T4r and the existing upgrade suites verify the classification.
- The same-base residue can still fail a replica boot under cancellation or fault interleavings.
- SQL Server coverage depends on the existing CI job. A local proof is not available on the development Mac.


## Controller disposition of review 2

C1 is corrected by selecting the strict contract for the new guarded row freeze. It has no stale
sweep and refuses before any marker or timestamp write. A successful call replaces a stale marker
directly. T1 pins stale-marker plus moved-base refusal. The unchanged text freeze keeps its existing
preflight maintenance, and the lifecycle table explicitly covers its sweep-only refusal or
cancellation outcome. Unsupported-capability gates still run before any store call.

C2 is corrected by separating text-route boot/retry behavior from the preexisting unchecked
row-commit edit-loss risk, now #1311. A distinct text-refusal retry cannot repair that row commit.
No repair of the deferred residue is bundled into this design.

Controller verified both findings against the cited code and revised these exact contracts. The
review accepted F1 to F4 apart from these corrections. No new broad review or implementation was
started. This draft is ready for owner design review, with runtime red proof still required by task 0.


## Execution clarification T3-C3

The two-runner T3 outcome means B accepts both existing results, not two concurrent-adoption
diagnostics. The unchanged first adoption rereads active version3. B then finds the second
definition AlreadySatisfied. Assert one KECU0011 and two steps in definition order, Applied at
version2 from the existing ledger followed by Adopted with no new PublishedVersion. Both reports
succeed, B attempts one text commit, and the original persisted ledger/version/row assertions
remain unchanged. This corrects an impossible diagnostic-count expectation without changing
production behavior or the cross-version safety contract.
