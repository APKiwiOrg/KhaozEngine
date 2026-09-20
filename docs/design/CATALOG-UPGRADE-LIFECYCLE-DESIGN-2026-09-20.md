# Catalog upgrade lifecycle

Status: accepted, in implementation. Origin: https://github.com/APKiwiOrg/Grimhollow/issues/259

## 1. Problem

A game build registers a new content type or needs new rows. A fresh install seeds the current bundle and
works. An existing catalog, local or hosted, still holds the older content. The strict runtime load then
refuses, correctly and fail closed, and the host exits before it opens a socket. In the verified case the
Grimhollow solo server exited 3 for a missing `harvest_profile` type while the client showed connection
retries. Fresh installs and the full test suite had passed, because nothing booted an older populated catalog.

An explicit offline command repaired that catalog without touching player state or operator tuning. What is
missing is a normal lifecycle: versioned upgrade definitions shipped with the application, applied before the
strict load, with durable history.

## 2. Non-goals

- No reseeding, no database deletion, no separate sync service. `catalog-reset` tooling is unrelated and
  the upgrade path never calls it.
- No work during NuGet restore or compilation. Upgrades run in the host process at startup or in a deploy
  step.
- No game content in the engine. The engine owns orchestration, history and checks. Each game owns its
  definitions.
- The player journal is out of scope. An upgrade never reads or writes journal or player tables.

## 3. Where migration history lives

Engine package versions and published catalog version numbers are not migration history. A version number
says how many publishes happened, not which upgrades ran.

| Criterion | A: ledger table in schema v2 | B: convention over version notes and audit | C: sidecar file |
|---|---|---|---|
| History atomic with the change | 10 | 7 | 2 |
| Boot-time read cost | 10 | 6 | 8 |
| Hosted rollout cost | 5 | 10 | 1 |
| Blast radius | 5 | 9 | 8 |
| Supports value patches later | 10 | 5 | 5 |
| Operator visibility | 8 | 7 | 3 |
| Total | 48 | 44 | 27 |

Decision: A. Option B cannot tell "applied, then tuned back" from "never applied", which is the
defaults-reapplied failure this work exists to prevent. The cost of A is one schema migration, which follows
the journal's v1 to v2 precedent.

## 4. Schema version 2

Both providers move `CurrentVersion` to 2 with `RequiredMigration` named
`catalog-v2-content-upgrade-ledger`. The one new table is `catalog_content_upgrade`:

| Column | Meaning |
|---|---|
| `upgrade_id` | Primary key. The definition's stable id, ordinal comparison, 1 to 128 characters. |
| `upgrade_order` | The definition's order at the time it was recorded. |
| `disposition` | `applied`, `adopted` or `baseline`, enforced by a check. |
| `version_number` | The version the upgrade published, or the active version for the other two. |
| `actor`, `operator_id` | Who ran it, same caps as the audit table. |
| `recorded_at_utc` | When. |

- `applied` means the runner published the definition's edits. The row is written inside the publish commit
  transaction, so the version and its history entry commit together or not at all.
- `adopted` means the planner found the content already present, for example a catalog repaired by the older
  explicit command. No version is published.
- `baseline` means the catalog was seeded from a bundle that already contained the content.

Migration follows the journal per provider and per mode. Read `SqliteJournalSchema.cs` and
`SqlServerJournalSchema.cs` first and mirror them. SQLite migrates a version 1 file in place, in one
transaction, when opened under `AutoCreate`. Under `ValidateOnly` a version 1 database is refused with a
message naming the migration. SQL Server ships `CatalogSchemaV2.sql` as the operator script and the fresh
create path produces version 2 directly. The migration adds one table and changes nothing else, so rows,
history, audit, the open draft, the pin and the store epoch all survive it byte for byte.

## 5. The ledger seam

`IContentAuthoringStore` keeps its fixed member list. The ledger is a separate interface in
`KhaozEngine.Catalog.Authoring`, implemented by the in-memory, SQLite and SQL Server stores:

```csharp
public interface IContentUpgradeLedger
{
    Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default);

    Task RecordUpgradeAsync(ContentUpgradeStamp stamp, ContentUpgradeDisposition disposition,
        string actor, string operatorId, CancellationToken cancellationToken = default);
}
```

`RecordUpgradeAsync` accepts only `Adopted` and `Baseline`. It writes the ledger row and one audit row in one
transaction, and recording an id that is already present is a no-op rather than an error.

`ContentPublishRequest` gains an optional init-only `Upgrade` property of type `ContentUpgradeStamp?`. It is
a property and not a positional parameter so existing callers stay source and binary compatible. When it is
set, `CommitPublishAsync` inserts the `applied` row inside its one transaction. The primary key makes a
second publish of the same upgrade fail the commit with nothing changed, which is the concurrency guarantee.
A new audit action `content-upgrade` marks these rows.

## 6. Definitions

```csharp
public sealed class ContentUpgradeDefinition
{
    public string Id { get; }
    public int Order { get; }
    public string Description { get; }
    public Func<ContentUpgradeContext, ContentUpgradePlan> Plan { get; }
}
```

- `Id` is stable forever and never reused. `Order` is unique and strictly increasing within a set.
- `ContentUpgradeContext` carries the baseline version number, the baseline exported as a `ContentBundle`
  and the frozen registry. A planner is a pure function of that context. It performs no I/O against the
  store.
- `ContentUpgradePlan` is one of three shapes: edits with human-readable change lines, already satisfied, or
  refused with a reason.
- `ContentUpgradeSet` validates ids and orders at construction and is what a game ships in its server
  assembly.

Rules a planner must follow, which the engine documents and the template enforces:

1. Detect by identity, never by value. A row that exists under the committed id and key is satisfied whatever
   its field values are, because those values may be operator tuning.
2. A value patch names the old shipped default it replaces and leaves any other value alone.
3. A partial state is refused rather than completed by guesswork.

`ContentUpgradePlanBuilder` and `ContentUpgradeChecks` generalise the checks Grimhollow's butchering plan
already proved: the target bundle matches the registry, every baseline type is schema compatible with the
target, an identity is either absent or present under both the same id and the same key, and plain id
allocation will issue exactly the committed ids.

## 7. The runner

`ContentUpgradeRunner.RunAsync(store, registry, set, options)` returns a `ContentUpgradeReport`. Options carry
the mode (`Preview` or `Apply`), an optional expected version, the actor and operator, and the running build
ordinals.

1. The store must implement `IContentUpgradeLedger`, otherwise the outcome is `Unsupported`.
2. No active version gives `NoCatalog`. The runner never seeds. A host that seeds a fresh catalog from the
   current bundle calls `ContentUpgradeRunner.RecordBaselineAsync` straight after, which records every shipped
   definition as `baseline`. A crash between the two is safe: the next run finds each definition satisfied and
   records it as `adopted`.
3. A ledger id the shipped set does not know means the catalog is ahead of this build. Outcome
   `CatalogAheadOfBuild`, nothing changed.
4. Pending means shipped and not in the ledger. None pending gives `UpToDate` with zero writes. No draft, no
   version, no audit row.
5. An open draft that the upgrade actor created for a pending upgrade is an interrupted run. The runner clears
   the freeze, discards it and continues. Any other open draft is operator work: outcome `OperatorDraftOpen`,
   draft untouched.
6. A pin on another version gives `PinnedElsewhere`. A pin on the active version does not block the publish.
   The pin is never moved, and the report carries a `PinHeld` diagnostic naming the version to repin to.
7. A supplied expected version that differs from the active version gives `BaselineMoved`.
8. Each pending definition runs in ascending order and publishes as its own version, so immutable history
   shows each upgrade separately and an interruption between two upgrades resumes at the second. The minimum
   builds rise to at least the running build and never fall.
9. A failed publish discards the runner's own draft and re-reads the ledger. If a concurrent runner applied the
   same upgrade the run continues. An exception after the commit point is resolved the same way, by reading
   the ledger rather than assuming.
10. `Preview` writes nothing. It plans the first pending definition exactly and lists the rest as pending,
    because a later plan depends on the published result of an earlier one.

Every refusal carries a stable diagnostic code, a message that names the catalog, the upgrade id and the
action an operator or developer takes next, and the report renders through the existing content boot line
prefix. Hosts map a failed report to `ContentBootResult.ContentFailureExitCode`.

## 8. Host integration contract

- Local arm: open under `AutoCreate`, seed and record the baseline when empty, otherwise run `Apply`, then
  perform the strict load. A failed report stops the host with the report text.
- Hosted arm: the dependent server never upgrades implicitly. A deploy step runs the game's upgrade command
  in `Preview`, then `Apply` with the expected version, before the server starts. A server that boots against
  a catalog with pending upgrades refuses with the pending ids and the command to run.
- Solo client: a host that fails before listening hands its report to the client, which shows it on screen
  instead of retrying a join that cannot succeed. The launching mechanism is game owned.

## 9. Required regression coverage

The engine suite covers, on the in-memory store, SQLite and SQL Server behind the existing
`KE_CATALOG_SQLSERVER` gate: schema migration of a populated version 1 database, ledger conformance, fresh
install baseline, an older populated catalog upgraded and then booted through `ContentBoot`, tuned data
preserved, repeated runs with zero writes, interruption at each step, two concurrent publishers, operator
drafts and pins, catalog ahead of build, planner refusal, incompatible baseline types, and pack generation
for the new type. Each catalog game adds an upgrade-and-boot test that starts its real server host against an
older populated catalog.
