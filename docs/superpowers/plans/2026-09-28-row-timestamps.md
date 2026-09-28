# Row Timestamps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every engine table records when each row was created, and every table whose rows change also records when each row last changed, released as KhaozEngine 20.14.0.

**Architecture:** Journal and catalog each move to schema version 3 on SQLite and SQL Server through a named in-place migration that follows the existing version 1 to 2 pattern (strict shape validation, version 2 expectations derived from version 3, `AutoCreate` migrates behind the application lock, `ValidateOnly` and `ReadOnly` refuse by migration name). Accounts, commerce and the legacy world store keep their guarded column adds. Stores stamp the new columns from the clock they already bind for their other times. The only API change is additive: `JournalStreamEntry` and `AccountRecord` gain the two times.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, xUnit.

**Spec:** `docs/design/ROW-TIMESTAMPS-DESIGN-2026-09-28.md`

## Global Constraints

- Rule 1: a creation time is set in the insert statement from the value the statement already binds for time (`@now`, `$now`, `@at`) or the store's `TimeProvider`, and is never written again.
- Rule 2: an existing column that is exactly the insert time is the creation time. Do not add a twin (`journal_event`, `journal_operation`, `journal_snapshot`, `catalog_version`, `catalog_audit`, `catalog_content_upgrade`, `wallet_ledger`).
- Rule 3: an update time equals the creation time on insert, and every statement that changes the row sets it.
- Rule 4 naming: journal, catalog and accounts use `created_at_utc` and `updated_at_utc`, `datetimeoffset(7)` on SQL Server. Commerce and `world_store` use `created_at` and `updated_at`, `DATETIME2`. SQLite columns use the encoding the package already uses for time (the journal uses integer Unix milliseconds).
- Rule 5: every new column is NULLABLE. A migration fills a legacy row only from a time the database proves, otherwise NULL.
- Migration names, verbatim: `sqlserver-journal-v3-row-timestamps`, `sqlite-journal-v3-row-timestamps`, `catalog-v3-row-timestamps`.
- No index is added.
- New behaviour goes in new partial files. Run `scripts/check-file-size.sh` before each commit and never raise a baseline.
- Tests run in Release: `dotnet test <project> -c Release`. A SQL Server fact that skips proves nothing.
- Commit subjects are `area(scope): summary`. No em dashes, en dashes or prose semicolons in shipped text.
- Stage explicit paths and commit with an explicit pathspec.

## Review Focus

1. A version 1 database opened under `AutoCreate`. It must chain version 1 to 2 to 3 in one open and end validated at 3. Test in Tasks 1, 2, 4 and 5.
2. A second process opening the same version 2 database while the first is migrating. The application lock must make exactly one migrate and the other validate at 3. Test in Task 2 and Task 5 (SQL Server, where the lock exists).
3. A migration interrupted after the columns are added but before `schema_version` moves. A reopen must finish the migration rather than refuse the half-shape as malformed. Test in Tasks 1, 2, 4 and 5.
4. An upsert that UPDATES an existing projection or `world_store` row. It must keep the original creation time. Test in Tasks 1, 2 and 7.
5. A backfill over a stream whose snapshot was compacted. It must leave `created_at_utc` NULL and never take an event or operation time. Test in Tasks 1 and 2.

---

### Task 1: Journal SQLite schema version 3

**Files:**
- Modify: `KhaozEngine.WorldStore.Sqlite/SqliteJournalSchema.cs` (make it `partial`, `CurrentVersion = 3`, `RequiredMigration = "sqlite-journal-v3-row-timestamps"`, version 3 table shapes, version 2 shapes derived)
- Create: `KhaozEngine.WorldStore.Sqlite/SqliteJournalSchema.VersionThree.cs` (version 2 to 3 migration statements and backfill)
- Modify: `KhaozEngine.WorldStore.Sqlite/SqliteMutationJournalStore.Writes.cs` (stream, operation stream and projection inserts), `SqliteMutationJournalStore.Maintenance.cs` and any reset path that inserts `journal_metadata`
- Test: `KhaozEngine.Server.Tests/WorldStore/Journal/SqliteJournalRowTimestampTests.cs`

**Interfaces:**
- Produces: SQLite columns `journal_metadata.created_at_utc`, `journal_stream.created_at_utc`, `journal_operation_stream.created_at_utc`, `journal_projection.created_at_utc`, all nullable INTEGER Unix milliseconds. Task 3 reads `journal_stream.created_at_utc` and `updated_at_utc`.

- [ ] **Step 1: Write the failing tests** in `SqliteJournalRowTimestampTests`, each opening a temp file store with a manual clock at `2026-09-06T00:00:00Z`:
  - `Fresh_store_creates_version_3_with_nullable_creation_columns`: `journal_metadata.schema_version == 3`, and `PRAGMA table_info` shows the four columns, `notnull == 0`.
  - `Every_row_written_carries_its_creation_time`: initialize a stream at T0, advance 1 min, commit with a projection at T1, advance, commit again updating that projection at T2, compact. Assert `journal_stream.created_at_utc == T0` and `updated_at_utc == T2`, the projection `created_at_utc == T1` and `updated_at_utc == T2`, every `journal_operation_stream` row's `created_at_utc` equals its operation's `committed_at_utc`, and no row in any table has a NULL creation time.
  - `Version_2_database_migrates_and_backfills_exactly`: build a version 2 file with the retained version 2 DDL, insert stream A (initialization snapshot `through_version = 0` at T0), stream B (snapshot replaced to `through_version = 5`, plus a retained `before 0, after 0, 0 events` operation), and a projection. Open under the default mode. Assert version 3, A's `created_at_utc == T0`, B's is NULL, `journal_operation_stream.created_at_utc` equals each operation's `committed_at_utc`, the projection's is NULL.
  - `Version_1_database_chains_to_version_3`.
  - `Half_migrated_database_finishes_on_reopen`: apply only the column adds from the version 3 migration to a version 2 file, reopen, assert version 3.
  - `ValidateOnly_and_ReadOnly_refuse_version_2_naming_the_migration`: message contains `sqlite-journal-v3-row-timestamps`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~SqliteJournalRowTimestampTests"`
Expected: FAIL, schema version is 2.

- [ ] **Step 3: Implement the version 3 schema, migration and backfill.** The migration adds the four nullable columns, runs the two backfills below, then sets `schema_version = 3` last, all in one transaction. A reopen that finds the columns present at version 2 skips the adds.

```sql
UPDATE journal_stream SET created_at_utc =
  (SELECT s.created_at_utc FROM journal_snapshot s
   WHERE s.stream_key = journal_stream.stream_key AND s.through_version = 0);
UPDATE journal_operation_stream SET created_at_utc =
  (SELECT o.committed_at_utc FROM journal_operation o
   WHERE o.operation_id = journal_operation_stream.operation_id);
```

- [ ] **Step 4: Stamp the new columns in every insert.** The stream, operation stream and projection inserts bind the statement's existing `$now`. The projection upsert sets `created_at_utc` only in its INSERT arm and never in `ON CONFLICT ... DO UPDATE`. `journal_metadata` insert sets it with its `updated_at_utc` value.

- [ ] **Step 5: Run the new tests and the existing SQLite journal suite**

Run: `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.WorldStore"`
Expected: PASS. Existing version 1 to 2 tests that asserted `CurrentVersion` or the migration name are updated to the chained expectation, not deleted.

- [ ] **Step 6: Check file sizes and commit**

```bash
scripts/check-file-size.sh
git add KhaozEngine.WorldStore.Sqlite KhaozEngine.Server.Tests/WorldStore
git commit -m "worldstore(sqlite): journal schema version 3 with row creation times" -- KhaozEngine.WorldStore.Sqlite KhaozEngine.Server.Tests/WorldStore
```

### Task 2: Journal SQL Server schema version 3

**Files:**
- Create: `KhaozEngine.WorldStore.SqlServer/JournalSchemaV3.sql` (embedded, add to the csproj beside V1 and V2)
- Create: `KhaozEngine.WorldStore.SqlServer/SqlServerJournalSchema.VersionThree.cs` (`VersionTwoMigrationSql`, version 3 expected columns and defaults, the version 2 sets derived with `Without`)
- Modify: `KhaozEngine.WorldStore.SqlServer/SqlServerJournalSchema.cs` (`CurrentVersion = 3`, `RequiredMigration = "sqlserver-journal-v3-row-timestamps"`, `SchemaSql` loads V3, migration chain), `SqlServerJournalSchema.ReadOnly.cs`
- Modify: `KhaozEngine.WorldStore.SqlServer/SqlServerJournalWriteBatch.cs` (stream, operation stream and projection inserts), and the reset path that re-inserts `journal_metadata`
- Modify: `KhaozEngine.WorldStore.SqlServer/SqlServerMutationJournalStore.cs` (`VersionTwoMigrationSqlForTest` beside the version 1 accessor)
- Test: `KhaozEngine.Server.Tests/WorldStore/Journal/SqlServerJournalRowTimestampTests.cs`, all `[SqlServerFact]`, in `SqlServerMutationJournalCollection`

**Interfaces:**
- Produces: SQL Server columns with the same names as Task 1, `datetimeoffset(7) NULL`. Task 3 reads them.

- [ ] **Step 1: Write the failing tests**, the same six as Task 1 against SQL Server, using `SqlServerJournalManualTimeProvider` and the existing fresh-database pattern in `SqlServerMutationJournalStoreTests`. Add `Two_opens_race_one_migration`: two stores open the same version 2 database concurrently under `AutoCreate`, both succeed, `schema_version == 3`, and the backfilled values are the single-migration values.

- [ ] **Step 2: Run to verify they fail**

Run: `KE_SQLSERVER_TEST_CONNSTRING='<disposable database>' dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~SqlServerJournalRowTimestampTests"`
Expected: FAIL. Without a SQL Server here, the facts skip. This task is then proven by the CI job from Task 8, and the worker says so in its report rather than claiming a pass.

- [ ] **Step 3: Implement.** The migration mirrors Task 1 in T-SQL (`ALTER TABLE ... ADD ... NULL`, guarded by `COL_LENGTH` so a half-migrated database finishes, the two backfills as `UPDATE ... FROM` joins, then `schema_version = 3` and `updated_at_utc`), inside the existing application lock and transaction. The projection upsert sets `created_at_utc` only in its `IF @@ROWCOUNT = 0` INSERT arm.

- [ ] **Step 4: Run the SQL Server journal facts if a server is reachable, and the full non-SQL suite regardless**

Run: `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.WorldStore"`
Expected: PASS, SQL Server facts skipped when no server is set.

- [ ] **Step 5: Check file sizes and commit**

```bash
scripts/check-file-size.sh
git commit -m "worldstore(sqlserver): journal schema version 3 with row creation times" -- KhaozEngine.WorldStore.SqlServer KhaozEngine.Server.Tests/WorldStore
```

### Task 3: Stream listing reports creation and update times

**Files:**
- Modify: `KhaozEngine.WorldStore/Journal/JournalStreamListing.cs` (`JournalStreamEntry`)
- Modify: `KhaozEngine.WorldStore/Journal/InMemoryMutationJournalStore.Listing.cs` and the in-memory stream record it lists from, `KhaozEngine.WorldStore.Sqlite/SqliteMutationJournalStore.Listing.cs`, `KhaozEngine.WorldStore.SqlServer/SqlServerMutationJournalStore.Listing.cs`
- Test: `KhaozEngine.Server.Tests/WorldStore/Journal/MutationJournalStreamListingConformance.cs`

**Interfaces:**
- Consumes: the `journal_stream` columns from Tasks 1 and 2.
- Produces:
  - `public JournalStreamEntry(string streamKey, long headVersion, DateTimeOffset? createdAtUtc, DateTimeOffset? updatedAtUtc)`, keeping `JournalStreamEntry(string streamKey, long headVersion)`, which leaves both times null rather than inventing one.
  - `public DateTimeOffset? CreatedAtUtc { get; }` and `public DateTimeOffset? UpdatedAtUtc { get; }`. A non-null value must be UTC (offset zero) or the constructor throws `ArgumentException`. The three stores always fill `UpdatedAtUtc`.

- [ ] **Step 1: Write the failing conformance test** `Stream_listing_reports_when_each_stream_was_created_and_last_changed`: initialize stream `a` at the harness clock T0, advance 1 minute, initialize `b` at T1, advance, commit one event to `a` at T2. List. Assert `a` has `CreatedAtUtc == T0` and `UpdatedAtUtc == T2`, and `b` has `CreatedAtUtc == T1` and `UpdatedAtUtc == T1`. It runs for in-memory, SQLite and (gated) SQL Server through the existing subclasses.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~Stream_listing_reports"`
Expected: FAIL, the constructor does not exist.

- [ ] **Step 3: Implement** the constructor, properties and all three listings. The in-memory store records the creation time at initialization from its `TimeProvider`.

- [ ] **Step 4: Run the listing conformance**

Run: `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~Stream_listing"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -m "worldstore(journal): stream listing reports creation and update times" -- KhaozEngine.WorldStore KhaozEngine.WorldStore.Sqlite KhaozEngine.WorldStore.SqlServer KhaozEngine.Server.Tests/WorldStore
```

### Task 4: Catalog SQLite schema version 3

**Files:**
- Modify: `KhaozEngine.Catalog.Sqlite/SqliteCatalogSchema.cs` (partial, `CurrentVersion = 3`, `RequiredMigration = "catalog-v3-row-timestamps"`), `SqliteCatalogSchemaInventory.cs`, `SqliteCatalogSchemaValidation.cs`
- Create: `KhaozEngine.Catalog.Sqlite/SqliteCatalogSchema.VersionThree.cs`
- Modify: every SQLite catalog write statement for the tables in the spec's catalog inventory
- Test: `KhaozEngine.Catalog.Tests/Sqlite/SqliteCatalogRowTimestampTests.cs`

**Interfaces:**
- Produces: the catalog columns in the spec's catalog table, nullable, in the catalog package's existing SQLite time encoding.

- [ ] **Step 1: Write the failing tests** with a manual clock:
  - `Fresh_catalog_is_version_3_with_nullable_timestamp_columns`.
  - `Every_catalog_write_path_stamps_its_rows`: seed or import a bundle, open a draft, add and then edit a row, create a family and commit a block, publish. Assert every row in every table in the spec's catalog inventory has a non-NULL creation time, every updated row's update time moved to the clock at that write, and its creation time did not.
  - `Version_2_catalog_migrates_and_backfills_from_publish_time`: a version 2 catalog with two published versions, one row replaced in version 2. Assert `catalog_row`, `catalog_row_field`, `catalog_chunk` and `catalog_remap_rule` rows take their version's `published_at_utc`, a closed row's `updated_at_utc` is the `published_at_utc` of its `replaced_in_version`, an open row's `updated_at_utc` equals its `created_at_utc`, and families and blocks are NULL.
  - `Version_1_catalog_chains_to_version_3`, `Half_migrated_catalog_finishes_on_reopen`, `ValidateOnly_refuses_version_2_naming_catalog_v3_row_timestamps`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~SqliteCatalogRowTimestampTests"`
Expected: FAIL.

- [ ] **Step 3: Implement** the migration (add columns, backfill, move the version last) and the write stamps. `catalog_draft` gains `updated_at_utc` set on every draft update. `catalog_draft_edit` gains `created_at_utc`, set on insert only, while `edited_at_utc` keeps its role as the update time.

- [ ] **Step 4: Run the catalog suite**

Run: `dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release`
Expected: PASS.

- [ ] **Step 5: Check file sizes and commit**

```bash
scripts/check-file-size.sh
git commit -m "catalog(sqlite): schema version 3 with row timestamps" -- KhaozEngine.Catalog.Sqlite KhaozEngine.Catalog.Tests
```

### Task 5: Catalog SQL Server schema version 3

**Files:**
- Create: `KhaozEngine.Catalog.SqlServer/CatalogSchemaV3.sql` (embedded), `KhaozEngine.Catalog.SqlServer/SqlServerCatalogSchema.VersionThree.cs`
- Modify: `SqlServerCatalogSchema.cs`, `SqlServerCatalogSchemaExpectations.cs`, `SqlServerCatalogSchemaValidation.cs`, every `SqlServerContentAuthoringStore.*.cs` and `SqlServerCatalogReset.cs` statement that writes a table in the spec's catalog inventory
- Test: `KhaozEngine.Server.Tests/Catalog/SqlServer/SqlServerCatalogRowTimestampTests.cs`, `[CatalogSqlServerFact]`

- [ ] **Step 1: Write the failing tests**, the Task 4 set against SQL Server, plus `Two_opens_race_one_migration`.
- [ ] **Step 2: Run to verify they fail**

Run: `KE_CATALOG_SQLSERVER='<disposable -catalog-test- database>' dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~SqlServerCatalogRowTimestampTests"`
Expected: FAIL, or skipped without a server, in which case the existing `catalog-sqlserver` CI job is the proof.

- [ ] **Step 3: Implement**, mirroring Task 4 in T-SQL with `COL_LENGTH` guards, inside the existing lock.
- [ ] **Step 4: Run** the non-SQL catalog and server suites, and the SQL facts when a server is reachable. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
scripts/check-file-size.sh
git commit -m "catalog(sqlserver): schema version 3 with row timestamps" -- KhaozEngine.Catalog.SqlServer KhaozEngine.Server.Tests/Catalog
```

### Task 6: Account creation and update times

**Files:**
- Modify: `KhaozEngine.Accounts/AccountRecord.cs`, the in-memory account store in `KhaozEngine.Accounts`
- Modify: `KhaozEngine.Accounts.SqlServer/SqlServerAccountSchema.cs` (two owned columns, guarded add, layout check), `SqlServerAccountStore.cs`
- Modify: `KhaozEngine.Accounts.Sqlite/SqliteAccountSchema.cs`, `SqliteAccountStore.cs`
- Test: `KhaozEngine.Accounts.Tests/AccountStoreConformance.Timestamps.cs` (partial of the conformance), and the SQL Server layout fixtures under `KhaozEngine.Accounts.Tests/SqlServer`

**Interfaces:**
- Produces: `AccountRecord(string Subject, string? DisplayName, bool Whitelisted, AccountBan? Ban)` gains `public DateTimeOffset? CreatedAtUtc { get; init; }` and `public DateTimeOffset? UpdatedAtUtc { get; init; }`, both null by default, so existing constructions compile. Columns `created_at_utc` and `updated_at_utc`, `DATETIMEOFFSET(7) NULL` on SQL Server.

- [ ] **Step 1: Write the failing conformance tests** with the store's clock:
  - `Created_account_reports_its_creation_time`: create at T0, read, `CreatedAtUtc == T0 == UpdatedAtUtc`.
  - `Changing_an_account_moves_only_its_update_time`: ban at T1, rename at T2, read, `CreatedAtUtc == T0`, `UpdatedAtUtc == T2`.
  - SQL Server layout fixture `Grimhollow_layout_table_gains_the_two_columns_and_keeps_debug`: an existing Grimhollow-shaped `dbo.accounts` with a `debug` column and rows is widened, old rows read with NULL times, `debug` values are unchanged.
- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test KhaozEngine.Accounts.Tests/KhaozEngine.Accounts.Tests.csproj -c Release`
Expected: FAIL for in-memory and SQLite, and SQL Server skipped without `KE_ACCOUNTS_SQLSERVER` (the `accounts-sqlserver` CI job proves it).
- [ ] **Step 3: Implement.** If the store has no clock, add `TimeProvider TimeProvider { get; init; } = TimeProvider.System` to its options.
- [ ] **Step 4: Run** the accounts suite. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git commit -m "accounts(store): record account creation and update times" -- KhaozEngine.Accounts KhaozEngine.Accounts.SqlServer KhaozEngine.Accounts.Sqlite KhaozEngine.Accounts.Tests
```

### Task 7: Commerce and world store creation times

**Files:**
- Modify: `KhaozEngine.Commerce.SqlServer/SqlServerWalletStore.cs`, `KhaozEngine.Commerce.Sqlite/SqliteWalletStore.cs` (`wallet_balance.created_at`, `grant_schedule.created_at` and `updated_at`, guarded adds, stamped writes)
- Modify: `KhaozEngine.WorldStore.SqlServer/SqlServerWorldStore.cs`, `KhaozEngine.WorldStore.Sqlite/SqliteWorldStore.cs` (`world_store.created_at`, guarded add, set in the insert arm of the upsert only)
- Test: the existing commerce and world store test classes in `KhaozEngine.Server.Tests/Commerce` and `KhaozEngine.Server.Tests/WorldStore/WorldStoreTests.cs`

- [ ] **Step 1: Write the failing tests** for both backends (SQL Server gated by `KE_COMMERCE_SQLSERVER` and `KE_SQLSERVER_TEST_CONNSTRING`):
  - `World_store_upsert_keeps_the_first_creation_time`: save key K, save K again later, read the row's `created_at` and assert it is the first save's time and `updated_at` is the second.
  - `World_store_table_from_an_older_build_gains_created_at`: an old-shape table with a row is widened on construction, and the row's `created_at` is NULL.
  - `Wallet_balance_and_grant_schedule_stamp_creation_and_update`: first credit creates the balance row with `created_at == updated_at`, a later credit moves only `updated_at`, and the same for a grant schedule claim.
- [ ] **Step 2: Run to verify they fail.** Expected: FAIL for SQLite, SQL Server skipped locally.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~Commerce|FullyQualifiedName~KhaozEngine.Tests.WorldStore"`. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git commit -m "worldstore(commerce): creation times on wallet, grant and world store rows" -- KhaozEngine.Commerce.SqlServer KhaozEngine.Commerce.Sqlite KhaozEngine.WorldStore.SqlServer/SqlServerWorldStore.cs KhaozEngine.WorldStore.Sqlite/SqliteWorldStore.cs KhaozEngine.Server.Tests
```

### Task 8: CI proves the journal, world store and commerce SQL Server facts

**Files:**
- Modify: `.github/workflows/ci.yml` (new job `server-sqlserver`, a copy of `accounts-sqlserver`)

- [ ] **Step 1: Add the job.** A disposable `mcr.microsoft.com/mssql/server:2022-latest` service on GitHub-hosted `ubuntu-26.04` (never self-hosted, the repository is public), its own CI-only SA password, two databases created by `sqlcmd` (`khaoz-journal-test-ci`, `khaoz-commerce-test-ci`), `KE_SQLSERVER_TEST_CONNSTRING` and `KE_COMMERCE_SQLSERVER` pointed at them, the same affected-paths gate on `KhaozEngine.WorldStore[^/]*/`, `KhaozEngine.Commerce[^/]*/`, `Directory.Packages.props` and the workflow, and the same executed-equals-total trx check the accounts job uses. The filter selects the SQL Server journal, world store and commerce test classes.
- [ ] **Step 2: Validate the workflow**

Run: `actionlint .github/workflows/ci.yml` if installed, otherwise `python3 -c "import yaml,sys;yaml.safe_load(open('.github/workflows/ci.yml'))"`
Expected: no errors.
- [ ] **Step 3: Commit**

```bash
git commit -m "ci(sqlserver): run the journal, world store and commerce SQL Server facts" -- .github/workflows/ci.yml
```

### Task 9: Rule, docs and version 20.14.0

**Files:**
- Modify: `docs/CONTRIBUTOR-RULES.md` (one bullet under Engine code and test contracts stating the rule and pointing at the design)
- Modify: `KhaozEngine.WorldStore/README.md`, `KhaozEngine.WorldStore.Sqlite/README.md`, `KhaozEngine.WorldStore.SqlServer/README.md`, `KhaozEngine.Catalog.Sqlite/README.md`, `KhaozEngine.Catalog.SqlServer/README.md`, `KhaozEngine.Accounts/README.md`, the two account backend READMEs, the two commerce backend READMEs, `docs/USING-KHAOZENGINE.md` where it describes the listing, `AccountRecord` or schema versions
- Modify: `Directory.Build.props` (`20.14.0`), `CHANGELOG.md`, every declaration `scripts/check-doc-versions.sh` guards, `docs/INDEX.md` status for the design
- Modify: `docs/design/ROW-TIMESTAMPS-DESIGN-2026-09-28.md` only to correct facts the implementation changed

- [ ] **Step 1: Fetch and re-read** `origin/main`'s version and tags. If 20.14.0 is taken, use the next free minor.
- [ ] **Step 2: Write the CHANGELOG entry**: the three migration names, the exact-or-NULL backfill, the one way door (a database on version 3 is refused by an older engine), the additive API, and that consumers with a hosted `ValidateOnly` catalog run their catalog schema migration step first.
- [ ] **Step 3: Doc sweep** with `git grep -w -e JournalStreamEntry -e AccountRecord -e schema_version -e CurrentVersion -e "journal schema"` across Markdown and package READMEs. Correct stale prose.
- [ ] **Step 4: Run the guards**

Run: `scripts/check-doc-versions.sh && scripts/check-file-size.sh`
Expected: both pass.
- [ ] **Step 5: Full verification**

Run: `dotnet build KhaozEngine.slnx -c Release && dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`
Expected: zero warnings, all pass.
- [ ] **Step 6: Commit**

```bash
git commit -m "release(20.14.0): row timestamps across every engine table" -- docs CHANGELOG.md Directory.Build.props <each README touched>
```
