# Catalog conditional freeze release

Status: **DRAFT FOR OWNER REVIEW. Not approved.** Written under owner ruling OA14 ("Proceed with
conditional-release design") for [#1271](https://github.com/APKiwiOrg/KhaozEngine/issues/1271). This is a design,
not an implementation plan and not a release decision. No version is changed and no tag is authorized.

Base: `fix/wa-catalog-freeze` at `ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc`. Every line reference below is to that
commit.

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
| `IContentTextAuthoringStore.FreezeChangesAsync` in all three stores (for example `InMemoryContentAuthoringStore.Text.cs:122-149`) | The companion freeze clears stale markers, refuses a base that is not active and writes the marker in one gate or transaction. Its marker therefore always equals the caller's expected base. |
| `InMemoryContentAuthoringStore.TextPublish.cs:61`, `SqliteContentAuthoringStore.TextPublish.cs:57`, `SqlServerContentAuthoringStore.TextPublish.cs:64` | The text commit refuses when the marker is not the active version or the draft is not `SameDraft` as the plan's frozen draft. |
| `Publish/ContentPublishCommit.cs:113-116` | Every publish on a store with the text companion takes the text route, so every engine store hits that check. |
| `Upgrade/ContentUpgradeFault.cs:44-52` | `IsContention` excludes `TextStateMismatchReason`. |
| `Publish/ContentPublisher.cs:242-268` | The row route checks the expected base against a baseline read earlier, then calls `FreezeDraftAsync`. The check and the write are not atomic. |
| `Upgrade/ContentUpgradeTextRoute.cs:95-106` | The runner freezes a plan that carries no text with `FreezeDraftAsync(active)`, even on a store that has the companion. |
| `*Store.Freeze.cs` `FreezeDraftAsync` in all three stores | The row-only freeze overwrites the marker with any non-negative base and never compares it with the active version. |

The last three rows are a finding this design adds. **Conditional release alone does not close the cross-version
race.** A runner whose plan carries no text can call `FreezeDraftAsync(1)` after a rival committed version 2 and
froze its next draft at 2. The overwrite turns the rival's marker into a stale 1. The runner's re-proof then
fails, and its release of base 1 clears the marker it just wrote. The rival's commit meets the same
`text-state-mismatch`. The hosted concurrency test's definitions add rows through `UpgradeFixtures.Adds`
(`KhaozEngine.Catalog.Tests/Upgrade/UpgradeFixtures.cs:106-115`). If those plans carry no text, which the
implementation confirms, this path is reachable in the very test that failed. Section 5.3 closes it.

## 2. Scope and non-goals

In scope: an atomic release conditioned on the base version the publisher recorded, its public contract, both
pipeline call sites, the runner's row-only freeze, compatibility for custom stores and decorators, version
disposition and deterministic proof.

Not selected, per OA14: a durable owner token, a schema version or migration, treating any `text-state-mismatch`
as contention, a new contention reason (R2 in the report), process-local locks, test serialization or relaxed
assertions. The unconditional release stays, for explicit host recovery only.

## 3. Decision: the API shape

### 3.1 The constraint that decides it

No combination of existing members can release a marker conditionally and atomically. `ClearDraftFreezeAsync` is
unconditional. `FreezeDraftAsync` overwrites. `TryDiscardChangesAsync` refuses a frozen draft. Reading the draft and
then clearing is the forbidden check-then-act. So **every safe option needs new store code**, and a store compiled
against 20.27 cannot supply it. No option is simultaneously safe, source and binary compatible, and behavior
compatible for an existing custom store. The choice is where the incompatibility lands.

One existing member does offer a safe partial release. `ReadPublishBaselineAsync` atomically clears markers that
name a base other than the active version (`IContentAuthoringStore.cs:233-237`). That covers a publisher whose base
moved, but never a same-base failure such as an invalid candidate, which would then leave the draft frozen until
host recovery. It appears below as option A2.

### 3.2 Options and their concrete behavior

| Option | Source | Binary | Runtime for a store or decorator without the new code |
|---|---|---|---|
| **A. Additive companion** `IContentDraftFreezeRelease`, required by both pipelines, typed refusal when absent | Compatible. Nothing existing changes. | Compatible. Old assemblies load and run. | A publish or an upgrade run with pending work refuses before it writes anything. Everything else, including boot reads and runs with nothing pending, is unchanged. |
| **A2.** Companion as in A, but stores without it fall back to the baseline read's stale sweep | Compatible | Compatible | Base-moved failures release correctly. A same-base failure leaves the draft frozen, so an operator whose publish was refused for an invalid candidate cannot edit it until a host recovers it. Each failure also pays a full baseline read. |
| **A3.** Companion as in A, plus an explicit host opt-in to the old unconditional release | Compatible | Compatible | Old behavior, defect included, for hosts that opt in. This is the fallback OA14 rules out, so it is listed only to record why it is absent. |
| **B. Default interface method** on `IContentAuthoringStore` with a default that reports "unsupported", plus a default capability property | Compatible, except that an implementer already holding a public method of the same signature silently becomes the implementation | Compatible | Same refusal as A. The default cannot be a real release because no existing member can implement one, so a second member must say whether the first one works. The two can disagree. |
| **C. Required member** on `IContentAuthoringStore` | Breaks every implementer with CS0535, including test doubles that never publish | An old implementer throws `TypeLoadException` when its type loads, even on a host that only boots | After recompiling, full fix everywhere. The compiler forces every decorator to forward it. |
| **I. Internal capability** for the engine's three stores only | Compatible | Compatible | A third-party store can never gain the fix and must be refused permanently. |

The existing default member on `IContentAuthoringStore` (`IContentVersionHashSource.GetVersionHashesAsync` at
`:134-139`) is not precedent for B. That member is derived entirely from `GetVersionAsync`. This one cannot be.

### 3.3 Scores

Scores are design judgments from 1 to 10, not measurements. Weights reflect a release-blocking correctness fix on a
public provider seam.

| Criterion | Weight | A | A2 | A3 | B | C | I |
|---|---:|---:|---:|---:|---:|---:|---:|
| Safety: no unconditional or check-then-act fallback, no accidental implementation | 4 | 10 | 9 | 6 | 7 | 10 | 9 |
| Source and binary compatibility | 3 | 10 | 10 | 10 | 9 | 1 | 10 |
| Runtime blast radius limited to where safety requires it | 2 | 7 | 4 | 8 | 7 | 2 | 2 |
| Decorator correctness and discoverability | 2 | 6 | 6 | 6 | 4 | 10 | 3 |
| Honest version classification and cost | 2 | 6 | 5 | 7 | 6 | 3 | 5 |
| Fit with house precedent and a focused change | 2 | 9 | 4 | 5 | 5 | 5 | 3 |
| **Weighted total out of 100** | | **84** | 69 | 71 | 66 | 55 | 61 |

Totals are the weighted sums out of 150, scaled to 100 and rounded.

Notes on the scores:

- A follows two shipped precedents. `IContentTextAuthoringStore` is a capability companion that pipelines
  detect by type, and `PackVersionPointers.Resolve` refuses an incapable pack store at the top of a publish
  rather than after files are written.
- A3 scores well on blast radius only because it keeps the bug. A2 trades the cross-version fix for a frozen-draft
  wedge on legacy stores that surfaces much later than a refusal would.
- C is the strongest contract, but it breaks every store that never publishes, including the two consumer test
  doubles found in section 8, and its major version buys nothing A lacks for the engine's own stores.
- B makes the capability look present on every store, and its default must be paired with a flag that a careless
  override can contradict.

### 3.4 Recommendation

**Option A.** Ship an additive companion, implement it atomically in all three engine stores, require it in both
pipelines, refuse with a typed reason before any write when it is absent, and never fall back to an unconditional
or check-then-act release. This is the smallest honest transition. It is source and binary compatible. **It is not
behavior compatible** for a custom store or decorator that publishes or runs pending upgrades without the
companion. Such a store is refused rather than left with the defect, and section 9 treats that as a version
question.

## 4. Public contract

### 4.1 The companion

```csharp
namespace KhaozEngine.Catalog.Authoring;

public interface IContentDraftFreezeRelease : IContentAuthoringStore
{
    Task<bool> ReleaseDraftFreezeAsync(int frozenForBaseVersion, CancellationToken cancellationToken = default);
}
```

The contract for `ReleaseDraftFreezeAsync`:

1. **It is one atomic compare-and-clear.** Inside the store's own gate or transaction, it clears the marker if and
   only if a draft is open and its marker equals `frozenForBaseVersion`. No read outside that step decides the
   write.
2. It returns `true` when it cleared a marker and `false` otherwise. A second call is a no-op that returns `false`.
3. It never touches the draft's base version, edits, text state, language introductions or opener, and it writes
   no audit row. It does not read or compare the active version. A marker naming an older base is stale and cannot
   belong to a publisher that can still commit, so clearing it is safe.
4. `catalog_draft.updated_at_utc` moves only when the call clears a marker, as the existing freeze statements do.
5. It throws `ArgumentOutOfRangeException` for a negative base. Base 0 is valid, because the first publish
   freezes at 0.
6. It honors its token like every member. The pipelines pass `CancellationToken.None`.
7. A type that declares the interface guarantees items 1 to 4 for every instance. A decorator over an arbitrary
   inner store must not declare it unless it can forward to an inner store that also does.

The extension of `IContentAuthoringStore` mirrors `IContentTextAuthoringStore`. The release is meaningful only on
the store that holds the marker.

### 4.2 Store implementations

- In-memory: under `_gate`, clear only when `_draft is { FrozenForBaseVersion: int f } && f == frozenForBaseVersion`,
  through the existing `Reframe` with a null marker.
- SQLite, in one transaction under the lease, answered by the row count:

  ```sql
  UPDATE catalog_draft SET frozen_for_base_version = NULL, updated_at_utc = $now
  WHERE draft_key = 1 AND frozen_for_base_version = $base;
  ```

- SQL Server, inside the existing `WriteAsync` scope: the same statement against `dbo.catalog_draft` with `@base`
  and `@now`.

No column, index, schema version or migration changes. `frozen_for_base_version` already holds the value.

### 4.3 Existing members re-documented, not changed

- `ClearDraftFreezeAsync` keeps its signature and its unconditional behavior. Its documentation changes from "a
  publish runs this on every exit path" to "explicit host recovery only, after proving no publisher is live". No
  engine pipeline calls it after this change. `CatalogRefusal.cs:86` and `KhaozEngine.Server.Admin/README.md:365`
  already describe it as the host recovery lever and stay valid.
- `FreezeDraftAsync` keeps its signature and overwrite behavior. Its documentation gains the warning that the
  write does not compare the base with the active version, and that the companion freeze is the atomic route.

### 4.4 New refusal identifiers

- `ContentAuthoringException.FreezeReleaseUnavailableReason = "freeze-release-unavailable"`, raised by
  `ContentPublishCommit.PublishAsync` when its store lacks the companion.
- `ContentUpgradeCodes.FreezeReleaseUnsupported = "KECU0017"`, the runner's `Unsupported` report for the same case.
  `KECU0016` is the highest code in use (`ContentUpgradeDiagnostic.cs:100`).

The admin console's `catalog-publish` reaches the store's own `PublishAsync`, and every engine store builds its
`ContentPublishCommit` over itself, so the console never meets the refusal on an engine store. The implementation
confirms that the default branch of `CatalogRefusal` (`:174`) carries an unknown reason through. It adds a mapping
only if that branch loses it.

## 5. Pipeline behavior

### 5.1 `ContentPublishCommit`

- The constructor resolves `_store as IContentDraftFreezeRelease` into a private field and does not throw, so
  callers that construct the type only for `WriteAsync` or `SweepAsync` are unaffected.
- `PublishAsync` refuses with `freeze-release-unavailable` **before its `try`**, so a refused publish reads
  nothing, freezes nothing, writes no file and releases nothing.
- The base it releases is `request.ExpectedBaseVersion`, which is known before any store call. It is exactly the
  marker either route writes. The text route's `FreezeChangesAsync` writes the active version only when it equals
  the expectation, and the row route's `ContentPublisher.FreezeAsync` checks the expectation against the
  baseline before calling `FreezeDraftAsync` with that same number.
- `ClearTheFreezeAsync` becomes a call to `ReleaseDraftFreezeAsync(request.ExpectedBaseVersion,
  CancellationToken.None)`, still in the `finally` and still swallowing its own failure.

Narrowing the `finally` to "only after a freeze was attempted" was considered. On the text route nothing precedes
the freeze, and on the row route it would only exclude baseline-read faults. Skipping the release after a typed
refusal from the freeze would rely on every custom store refusing before it writes. Neither is selected. The
same-base residue in section 7 covers what remains.

### 5.2 `ContentUpgradeRun`

- The runner checks for the companion **after its gates find at least one unrecorded definition and before its
  first draft write**. A store without it stops the run with `Unsupported` and `KECU0017`, with nothing written. A
  run with nothing pending, and `RecordBaselineAsync`, never ask, so a host that only boots is unaffected. This
  placement matters for the consumer doubles in section 8, which script boot failures and never publish.
- `bool _frozenForPublish` becomes `int? _frozenForBaseVersion`, set to the exact value passed to
  `Route.FreezeAsync` immediately before that call (`ContentUpgradeRun.Publish.cs:216`). It is captured from the
  call argument and never re-read from `Active`, which moves during a run. A lost freeze acknowledgement is still
  released, as today.
- `ReleaseFreezeAsync` reads and nulls the field, then calls `ReleaseDraftFreezeAsync(captured,
  CancellationToken.None)`. Both call sites stay where they are, the obstruction release at `:144` and the
  `finally` at `:185`.
- The successful exit still clears the field without releasing, because the store's own publish took the marker
  over and released it at the same base.

### 5.3 The runner's row-only freeze

`ContentUpgradeTextRoute.FreezeAsync` freezes through `FreezeChangesAsync(active)` whenever the store has the text
companion, whether or not the plan carries text. `FreezeDraftAsync` remains the route only for stores without the
companion. On every engine store the runner's freeze then refuses a moved base with `base-version-moved`, which is
already contention, and never overwrites a newer marker.

Consequences the implementation must verify:

- `ContentUpgradeTextMatch.IsPlan` must accept the complete snapshot draft of a plan with no text exactly as it
  accepts the row-only read-back today.
- A draft with no work is refused with `no-open-draft` at the runner's freeze instead of at the store's publish.
  Both are contention.
- A base whose text provenance cannot be proven is refused at the runner's freeze instead of one step later inside
  the store's publish. The refusal and its classification are unchanged.
- Each publish attempt pays one extra baseline and baseline-text read. That cost is acceptable on the boot and
  deploy paths.

The rejected alternative is to make `FreezeDraftAsync` refuse a base that is not active in the three engine stores.
That changes the documented behavior of an existing member. It also breaks tests that plant stale markers through
it, for example `CatalogPublishActionTests.cs:80`, and it would not bind custom stores either.

### 5.4 Lifecycle

`B` is the base a publisher recorded. A newer marker names `B+1` or later.

| Event | Behavior after this change |
|---|---|
| Success | The commit consumed the draft. The publish `finally` releases `B`, which is a no-op on a later draft frozen at a newer base. The runner owes nothing. |
| Refused because the base moved, at the freeze or at the commit's number check | The release of `B` cannot touch a newer marker. It clears only a stale `B` marker, which the stale sweep would also clear. |
| Same-base failure after the freeze: invalid candidate, pack write fault, text chunk mismatch, provider fault | The release of `B` clears the marker naming `B`. That is this publisher's, or a same-base rival's (section 7). |
| Cancellation | Released on `CancellationToken.None`, unchanged. Same-base residue as the row above. |
| Double cleanup, the runner's `finally` and the pipeline's `finally` | Both release `B`. The second returns `false` unless another freeze at `B` landed in between (section 7). |
| Lost freeze acknowledgement | `B` was captured before the call, so the release runs. It clears the marker if it was written, and is a no-op or same-base residue if not. |
| Lost commit acknowledgement | The version is live. The release of `B` cannot clear a newer marker. The runner reads the ledger and adopts, unchanged. |
| The release itself fails or its acknowledgement is lost | The publish swallows it, unchanged, and the runner surfaces it as today. A marker left naming `B` is cleared by the next publish at `B` (it overwrites and releases `B` on exit), by the stale sweep once the base moves, or by host recovery. |
| Process crash after the freeze | No release. Recovery is unchanged: the next publish at `B`, the stale sweep, or explicit host recovery. |
| An old publisher finishes after a new draft was opened or frozen | Its release of `B` is a no-op on any marker naming a newer base, so the new publisher's commit finds its own marker. This is the #1271 case. |
| Host recovery | `ClearDraftFreezeAsync`, unconditional and explicit, only after proving no publisher is live. |

## 6. Protections preserved

- The complete-draft confirmation in all three text commits is unchanged, including the marker check, so nothing
  edited during a freeze gap is ever published.
- Typed stale-plan errors are unchanged: `base-version-moved` from the freeze and from the number confirmation,
  and `text-state-mismatch` for a draft that changed. No new contention reason is added.
- Ledger rows, version numbers, row ids, carried ids and family blocks are untouched. The release writes only the
  marker column and its timestamp.
- Operator edits are never discarded by a release. `TryDiscardChangesAsync` still deletes only an exact unfrozen
  match, and the runner still classifies a merged draft as operator work.
- Stale-marker sweeps in `ReadPublishBaselineAsync` and `FreezeChangesAsync` are unchanged.

## 7. Residue

**Same-base release.** The marker is one value with no owner identity, so two publishers frozen at the same active
base `B` are indistinguishable. If publisher P leaves without committing while publisher Q sits between its freeze
and its commit, P's release clears the marker Q relies on. P can leave that way through cancellation, a pack write
fault, a provider fault, a candidate that fails validation (possible when replicas run different builds), a
same-base refusal before its freeze that still reaches the `finally` (for example `no-open-draft` or
`text-provenance-unknown`), or the runner's obstruction release, which is residue 3 in
`KhaozEngine.Catalog.Authoring/README.md`.

- Effect on a console publish Q: a typed `text-state-mismatch`. The draft is intact and a republish works.
- Effect on a runner Q: it discards its own unfrozen draft, finds no ledger row, and reports `KECU0009` because the
  reason is not contention. A boot in that deploy fails and a restart retries.
- Safety: during the gap, edits to Q's draft are accepted, and Q's complete-draft check then refuses its commit.
  Nothing unreviewed is published, no edit is lost and no version is duplicated.
- Likelihood: two same-base publishers plus a non-committing exit inside the other's window. The observed failures
  were cross-version, which this design closes.

Closing this residue requires either R2 (a distinct changed-frozen-draft reason that the runner treats as
contention) or a durable owner token. Neither is selected. Section 12 lists whether to file either as a follow-up.

**Row route of a custom store without the text companion.** `ContentPublisher.FreezeAsync` still checks the base
before `FreezeDraftAsync` writes it. A lagging publisher on such a store can overwrite a newer marker with a stale
one. No engine store takes this route. The README records it as a limitation of stores without the companion.

**Contract trust.** The pipelines trust a declared companion. A decorator that declares it without an atomic inner
release reintroduces the defect for its own deployment.

## 8. Custom stores, decorators and known consumers

A custom store gains the fix by declaring `IContentDraftFreezeRelease` and implementing the atomic statement in
section 4.2. A decorator declares it only when it can forward to a store that does. It requires that at
construction or ships two decorator types. A decorator that forwards `PublishAsync` to an inner engine store is
already safe inside that publish, because the inner store builds its `ContentPublishCommit` over itself. It still
needs the companion when it is handed to `ContentUpgradeRunner.RunAsync`, because the runner releases through the
object it was given.

In-repo doubles that implement `IContentAuthoringStore` and drive a pipeline, found with
`git grep -P` (plain `git grep -E` ignores `\b` on macOS and under-reports):

- `KhaozEngine.Catalog.Tests/Upgrade/UpgradeStoreDoubles.cs:16` `ForwardingContentAuthoringStore` and its
  subclasses, including `ContentUpgradeFreezeReleaseTests.cs:210` `ForwardingUpgradeStore` and `:354`
  `CountsFreezeReleasesStore`, which counts `ClearDraftFreezeAsync` calls today.
- `KhaozEngine.Server.Tests/Catalog/ForwardingContentAuthoringStore.cs:13` and its subclasses.
- `KhaozEngine.Catalog.Tests/Publish/TextStoreDoubles.cs:16` `RowOnlyStoreView` and `:234` `TextStoreDouble`. The
  legacy-route tests in `TextLegacyCompatibilityTests.cs` and `ContentAuthoringTextStoreConformance.cs:294` publish
  through `RowOnlyStoreView`.
- `KhaozEngine.Server.Tests/Catalog/WriteRefusingAuthoringStore.cs:23` and `UpgradeDraftRaceStore.cs`.

Read-only consumer check, at Grimhollow `d191cd01` (also `.worktrees/world-authoring` `51837c4b` and
`.worktrees/grand-world` `40f74288`) and Ruinborne `4da9ba36`. This does not exhaust external implementations.

- Production code in both games constructs only the engine's SQLite, SQL Server and in-memory stores
  (`GrimhollowCatalogDatabase.cs:161-162`, `RuinborneCatalogDatabase.cs:145-147`). Under option A it needs no change.
- `Grimhollow.Tests/Server/CatalogUpgradeRaceStore.cs:7` decorates `IContentAuthoringStore` and
  `IContentUpgradeLedger` without the text companion, and `ConcurrentCatalogUpgradeTests.cs:24-25` and `:79` hand it
  to the runner with pending work. Under option A those tests report `KECU0017` until the double declares and
  forwards the companion. That is a one-member change at Grimhollow's next engine pin, and a handoff issue is
  needed. Under option C it is a compile error.
- `ScriptedAuthoringStore.cs:34` in both games implements `IContentAuthoringStore` for boot and seed failure
  scripts, and its `ClearDraftFreezeAsync` throws because it is never reached. Under option A it is unaffected
  because the runner asks only before a draft write. Under option C both games would need a new member.

## 9. Version disposition

Facts: `Directory.Build.props:25` stages `20.27.1`, untagged. The newest tag is `v20.27.0`, which is immutable. The
staged changelog says the 20.27.1 repair leaves "shipped APIs" unchanged. Repository SemVer is additive minor, fix
patch and breaking major, and a staged version is ridden.

- This change adds public API (one interface, one reason constant, one diagnostic code). It cannot be a patch, so
  the batch cannot ship as `20.27.1` with that number.
- It also refuses a conforming 20.27 custom store or decorator that publishes or runs pending upgrades without the
  companion. That is an incompatible runtime change to `ContentPublishCommit.PublishAsync` and
  `ContentUpgradeRunner.RunAsync` for those implementers.
- Precedent cuts both ways. 20.0.0 shipped catalog seam concurrency changes, found by the same
  `ContentUpgradeConcurrencyTests`, as a major. 20.27.0 shipped a fail-closed stricter load of previously accepted
  files as a minor. 20.20.0's companion refused legacy routes only for text, which was new state, so no previously
  working configuration broke.

**Recommendation: 21.0.0** is the correct disposition under the repository's stated SemVer, because a previously
working implementer stops working. **20.28.0** is defensible only as an explicit owner ruling that refusing an unsafe
custom configuration is hardening in the 20.27.0 sense. In that case the changelog must say "behavior change for
custom catalog stores" and name the migration. Either way the staged entry is re-staged, not tagged, and its
validation-repair text rides into the new entry. Re-read `main`, the version and the tags after the owner decides.
This design changes no version and authorizes no tag.

## 10. Deterministic tests and evidence

There is no timing machinery. Every pause is a `TaskCompletionSource` that a test decorator awaits at an async entry
point, for example `CommitTextPublishAsync`, `FreezeChangesAsync` or `GetOpenDraftAsync`. Each interleaving is forced
once. Nothing loops, and no stress workflow runs without the owner's separate permission. The existing
20-iteration `ContentUpgradeConcurrencyTests` stay unchanged as the statistical net, and their summary comment at
`:27-29` is corrected to name separate replicas.

"In-memory" means one shared `InMemoryContentAuthoringStore`, the only way that store can be shared. "SQLite
replicas" means two `SqliteContentAuthoringStore` instances on one database file, which reproduces separate
connections like the hosted failure.

| Id | Location | Forced sequence | Expected after the fix | Red before the fix |
|---|---|---|---|---|
| T1 | `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.Freeze.cs`, new virtual facts. In-memory and SQLite through their conformance classes. SQL Server through one-line `[CatalogSqlServerFact]` overrides in `SqlServerContentAuthoringStoreConformanceTests.cs` | Seed version 1, edit, freeze at 1. Release 0, then release 1, then release 1 again. Publish to 2, edit, freeze at 2, release 1. No draft, release 5. Negative base. | `false` with marker 1 standing and `ApplyEditsAsync` refused `publish-in-progress`, then `true` with edits allowed, then `false`. Marker 2 survives the release of 1, and `ClearDraftFreezeAsync` still clears it unconditionally. `false` with no throw. `ArgumentOutOfRangeException`. Edits, text and audit count are identical before and after every call. | Not applicable, new API |
| T1t | `ContentAuthoringTextStoreConformance.cs` with matching SQL Server override, plus `SqliteCatalogRowTimestampTests.cs` and `SqlServer/SqlServerCatalogRowTimestampTests.cs` | Text-bearing draft frozen through `FreezeChangesAsync`, released at its base and at another base | Text state and introductions are untouched. `updated_at_utc` moves only on a `true` release. | Not applicable |
| T2 | New `KhaozEngine.Catalog.Tests/Publish/ConditionalFreezeReleasePublishTests.cs`, theory over in-memory and SQLite replicas | Old publisher, expecting 1, parks at its commit. A second publisher at 1 commits version 2. An edit opens draft D2. A new publisher, expecting 2, freezes and parks at its commit. The old publisher resumes and is refused, and its `finally` completes. The new publisher resumes. | Old is refused `base-version-moved`. D2's marker is still 2 after the old `finally`. New commits version 3. No draft remains. | New is refused `text-state-mismatch` |
| T2r | Same file | Two publishers freeze one draft at the same base. The first is cancelled inside its pack write while the second parks at its commit. An edit is applied in the gap. | Pins the documented residue: the second is refused `text-state-mismatch`, nothing is published, the draft keeps every edit including the gap edit. | Same result, a residue pin rather than a regression |
| T3 | New `KhaozEngine.Catalog.Tests/Upgrade/ConditionalFreezeReleaseUpgradeTests.cs`, theory over in-memory and SQLite replicas, with decorators that build `ContentPublishCommit` over themselves (pattern of `UpgradeStoreDoubles.cs:604`) | The hosted sequence from the report: runner B parks at its first commit on base 1. Runner A recovers B's draft (`KECU0010`), publishes 2, freezes the second upgrade at 2 and parks at its commit. B resumes and is held at its first `GetOpenDraftAsync` after the refusal, which is after both of its releases. A resumes, then B. | Both reports succeed. A has `KECU0010` and no `KECU0009`. B adopts both upgrades (`KECU0011`). Three versions, ledger rows Applied at 2 and 3, no draft, row ids 1 and 2. | A reports `KECU0009` |
| T3o | Same file | A runner freezes its draft at 1 and parks before its read-back. A console publish commits that draft as 2, then a new draft is frozen at 2 and parks at its commit. The runner resumes, fails its re-proof and takes the obstruction release. | The obstruction release of 1 leaves marker 2 standing. The console publish commits version 3. | The console publish is refused `text-state-mismatch` |
| T4 | Same file | A runner whose plan carries no text parks after writing its draft, before its freeze. A rival publishes 2, freezes D2 at 2 and parks at its commit. The runner resumes its freeze at 1. | The runner's freeze is refused `base-version-moved` and it stands off. D2's marker is still 2. The rival commits 3. The runner then adopts or replans. | The rival is refused `text-state-mismatch` (proves section 5.3) |
| T5 | `ConditionalFreezeReleasePublishTests.cs` and `ConditionalFreezeReleaseUpgradeTests.cs` | `ContentPublishCommit` over a store view without the companion. The runner over a ledger-bearing decorator without it, once with one pending upgrade and once with none. A decorator that declares and forwards it. | Publish refused `freeze-release-unavailable` with no marker, no pack object, no version and an unchanged audit count. Pending run reports `Unsupported` `KECU0017` with nothing written. The run with nothing pending reports exactly what it reports today. The forwarding decorator behaves exactly as the inner store. | Not applicable |
| T6 | `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.UpgradeRace.cs`, with a SQL Server override | T3 expressed as a conformance fact | Same as T3, on SQL Server in the existing catalog SQL Server CI job (`.github/workflows/ci.yml:141-170`) | Not run locally |

Existing tests that change: `ContentUpgradeFreezeReleaseTests.cs` counts the conditional release instead of
`ClearDraftFreezeAsync` and asserts that pipelines call `ClearDraftFreezeAsync` zero times. The doubles in section 8
declare and forward the companion, except one view kept without it as the T5 proof. Legacy-route tests that need a
capable row-only view use a forwarding variant.

Evidence required before integration:

1. The `git grep -P` inventory of implementers at the implementation head.
2. One red run of T2, T3, T3o and T4 against current behavior, with the refusal line captured in a log. Those
   tests need decorators that do not yet declare the companion, so the red run precedes the API task or uses a
   temporary test-only variant that is removed.
3. One green run of each focused test after its task, run serially through the shared slot.
4. One full Release build, test and format run at the finish, with exit codes.
5. Hosted CI green on every leg, including the Metal SQLite leg and the catalog SQL Server job. Unchanged workflow
   reruns are not evidence.

## 11. Implementation task boundaries

At design granularity. The implementation plan follows only after this design is approved. Tasks run serially and
only one builds or tests at a time.

1. **Contract and stores.** New `KhaozEngine.Catalog.Authoring/IContentDraftFreezeRelease.cs`, the three
   `*ContentAuthoringStore.Freeze.cs` partials and the class declarations, T1 and T1t with the SQL Server
   overrides, and the XML documentation for `ClearDraftFreezeAsync` and `FreezeDraftAsync`.
2. **Publish pipeline.** `Publish/ContentPublishCommit.cs`, the reason constant in `ContentAuthoringException.cs`,
   T2, T2r, the publish half of T5 and the publish doubles.
3. **Upgrade runner.** `Upgrade/ContentUpgradeRun.Publish.cs`, `Upgrade/ContentUpgradeTextRoute.cs`, the capability
   check in `ContentUpgradeRun` or `ContentUpgradeRunner`, `KECU0017` in `ContentUpgradeDiagnostic.cs`, T3, T3o, T4,
   the runner half of T5, T6 and the upgrade doubles.
4. **Docs and release text.** `KhaozEngine.Catalog.Authoring/README.md` (the publish window and residue section,
   around `:826-879`, including the inaccurate "A rival RUNNER loses nothing" line), a check of
   `KhaozEngine.Server.Admin/README.md:365` and `CatalogRefusal.cs:86`, `docs/USING-KHAOZENGINE.md` if it describes
   the release, the full Markdown sweep for both member names, and `CHANGELOG.md` under the version the owner
   chooses, with the version bump in that same commit.

Out of scope for all four: schema changes, an owner token, R2, consumer repository edits and any release tag.

## 12. Owner decisions

1. **API shape.** Recommended: option A. Alternatives with their costs are in section 3.
2. **Version.** Recommended: 21.0.0 under strict repository SemVer. The alternative is 20.28.0 by explicit owner
   ruling. 20.27.1 is not available for this batch. v20.27.0 is never moved.
3. **Section 5.3 in scope.** Recommended: include it, because without it the hosted test can still fail through a
   stale row-only freeze. The alternative is to file it separately and accept a known remaining cross-version path.
4. **Same-base residue.** Recommended: accept it as documented in section 7 and have the root file one follow-up
   issue that weighs R2 against an owner token. The alternative is to bring R2 into this batch.
5. **Consumer handoff.** Recommended: the root files a Grimhollow issue for `CatalogUpgradeRaceStore` to declare
   and forward the companion at the next engine pin.

## 13. Remaining risks

- No deterministic reproduction exists yet. T2, T3 and T4 must go red before the fix to prove the diagnosis. If T3
  passes before the fix, the diagnosis is incomplete and implementation stops.
- External implementers are unknown. The refusal is loud and typed, but it is a behavior change wherever they
  exist.
- The section 5.3 change moves some refusals earlier and adds one baseline read per runner publish attempt. Both
  are verified by T3 and T4 and by the existing upgrade suites.
- The same-base residue can still fail a replica boot under cancellation or fault interleavings.
- SQL Server coverage depends on the existing CI job. A local proof is not available on the development Mac.
