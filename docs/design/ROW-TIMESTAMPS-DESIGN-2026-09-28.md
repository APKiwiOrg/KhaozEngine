# Row timestamps design (2026-09-28)

## Goal

Every engine table records when each row was created, and every table whose rows change after insert also
records when each row last changed. The first consumer is Grimhollow, which needs a character's creation time
for its player stats and its own `accounts` timestamps, and which is pinned and waiting on this change.

## The rule

1. **Every table has a creation time.** It means the moment the row was inserted. The store sets it in the
   insert statement from the same clock it uses for its other times (the store's `TimeProvider`, or the value
   the existing statement already binds as `@now` or `@at`), and nothing ever writes it again.
2. **Where a column is already exactly the insert time, it is the creation time.** No twin is added. Examples
   are `journal_event.committed_at_utc` and `catalog_version.published_at_utc`.
3. **A table whose rows change after insert also has an update time.** It equals the creation time on insert
   and every statement that changes the row sets it.
4. **Names and types follow the package.** Journal, catalog and accounts use `created_at_utc` and
   `updated_at_utc` as `datetimeoffset(7)` on SQL Server. Commerce and the legacy `world_store` already use
   `created_at` and `updated_at` as `DATETIME2`, so their new columns match that. SQLite columns use the
   encoding their package already uses for time.
5. **A legacy row gets an exact time or NULL, never a guess.** New creation and update columns are nullable
   for that reason. A migration fills a legacy row only from a time the database can prove was that row's
   insert or last change.

The database default is not the source. SQLite refuses `ALTER TABLE ADD COLUMN` with a non-constant default,
and the stores already stamp every time from their own clock so tests can control it.

## Exception

`journal_snapshot` holds one row per stream that compaction overwrites whole. Its `created_at_utc` is when the
snapshot it holds was taken, and it is rewritten on every replace. An `updated_at_utc` would duplicate it on
every write. The only fact missing is when the stream was first compacted, which nothing needs. The table is
documented as the rule's one exception.

## Inventory

### Journal, schema version 3

| Table | Changes after insert | Today | Change |
|---|---|---|---|
| `journal_metadata` | yes | `updated_at_utc` | add `created_at_utc` |
| `journal_stream` | yes | `updated_at_utc` | add `created_at_utc` |
| `journal_event` | no, deleted by compaction | `committed_at_utc` is the insert time | none |
| `journal_operation` | no, deleted by purge | `committed_at_utc` is the insert time | none |
| `journal_operation_stream` | no | nothing | add `created_at_utc` |
| `journal_snapshot` | replaced whole | `created_at_utc` | none, the exception |
| `journal_projection` | yes | `updated_at_utc` | add `created_at_utc` |

`journal_operation.retention_started_at_utc` is set at insert (and was backfilled once by the version 2
migration). No normal path updates the row.

### Catalog, schema version 3

| Table | Changes after insert | Today | Change |
|---|---|---|---|
| `catalog_metadata` | yes | `updated_at_utc` | add `created_at_utc` |
| `catalog_type` | yes, `MERGE` | nothing | add both |
| `catalog_version` | no | `published_at_utc` is the insert time | none |
| `catalog_family` | no | `created_in_version` only | add `created_at_utc` |
| `catalog_family_block` | yes, `next_free_id` | `reserved_in_version` only | add both |
| `catalog_row` | yes, `replaced_in_version` | `valid_from_version` only | add both |
| `catalog_row_field` | no | nothing | add `created_at_utc` |
| `catalog_id_high_water` | yes, `MERGE` | nothing | add both |
| `catalog_draft` | yes | `opened_at_utc` is the insert time | add `updated_at_utc` |
| `catalog_draft_edit` | yes | `edited_at_utc`, rewritten on update, so it is the update time | add `created_at_utc` |
| `catalog_draft_edit_field` | no | nothing | add `created_at_utc` |
| `catalog_audit` | no | `occurred_at_utc` is the insert time | none |
| `catalog_remap_rule` | no | `introduced_in` only | add `created_at_utc` |
| `catalog_chunk` | no | `version_number` only | add `created_at_utc` |
| `catalog_content_upgrade` | no | `recorded_at_utc` is the insert time | none |

A catalog version number is not a time, so versioned rows still get a real creation time.

### Accounts, commerce and world store

These packages widen in place with guarded column adds rather than a versioned schema, and keep doing so.

| Table | Changes after insert | Today | Change |
|---|---|---|---|
| `accounts` (engine layout, Grimhollow's table) | yes | nothing | add `created_at_utc`, `updated_at_utc` as owned columns |
| `wallet_ledger` | no | `created_at` | none |
| `wallet_balance` | yes | `updated_at` | add `created_at` |
| `grant_schedule` | yes | `next_available_utc` only | add `created_at`, `updated_at` |
| `world_store` (legacy) | yes, upsert | `updated_at` | add `created_at` |

The `accounts` layout is Grimhollow's `dbo.accounts`, adopted in place. The two new columns become owned
columns of that layout, so Grimhollow's own store writes the same names and types.

## Migration

Journal and catalog follow the version 1 to 2 pattern exactly, on both backends:

- Named migrations `sqlserver-journal-v3-row-timestamps`, `sqlite-journal-v3-row-timestamps` and
  `catalog-v3-row-timestamps`.
- `AutoCreate` migrates in place behind the existing application lock. `ValidateOnly` and `ReadOnly` refuse a
  version 2 database by naming the migration. The metadata row's `schema_version` moves to 3 last, inside the
  migration.
- A fresh database gets the version 3 DDL directly. The version 2 DDL stays, as version 1 does today, and the
  validators derive their version 2 expectations from version 3 the way version 1 is derived from version 2.
- The migration adds nullable columns, runs the backfill below, and moves the version. It adds no index.

### Backfill

- `journal_stream.created_at_utc`. Every stream is created by `InitializeAsync`, which in one statement inserts
  the stream, an initialization snapshot at `through_version = 0` and an initialization operation whose
  `journal_operation_stream` row has `before_version = 0`, `after_version = 0` and `event_count = 0`. Its
  creation time is therefore exactly either:
  - the snapshot's `created_at_utc` while that snapshot has never been replaced (`through_version = 0`), or
  - the initialization operation's `journal_operation.committed_at_utc` while that operation is retained.
  Otherwise it is NULL.
- `journal_operation_stream.created_at_utc` is its operation's `committed_at_utc`, which is written in the same
  statement. A row whose operation was purged cannot exist, because purge deletes both.
- `catalog_row`, `catalog_row_field`, `catalog_chunk` and `catalog_remap_rule` are written in the publish
  transaction that inserts their version, so each takes that version's `published_at_utc`
  (`valid_from_version`, `version_number` and `introduced_in` respectively). The `catalog_row.updated_at_utc` of
  a closed row takes the `published_at_utc` of its `replaced_in_version`, and an open row's takes its creation
  time.
- Every other new column on an existing row is NULL.

## API

Additive only. Every game repository has test fakes of `IMutationJournalStore`, so no member is added to it.

- `JournalStreamEntry` from `IMutationJournalStreamListing` gains `CreatedAtUtc` (`DateTimeOffset?`) and
  `UpdatedAtUtc` (`DateTimeOffset`), through a new constructor that keeps the existing one. The SQL Server,
  SQLite and in-memory stores fill both.
- `AccountRecord` gains optional `CreatedAtUtc` and `UpdatedAtUtc` (`DateTimeOffset?`) with null defaults, so
  existing constructions compile unchanged.
- Commerce balance reads gain the same pair where a read record exists for the row.

## Verification

- Version 2 to version 3 migration on both backends for journal and catalog, including each backfill case:
  an unreplaced initialization snapshot, a retained initialization operation, a compacted and purged stream
  that stays NULL, and catalog rows taking their version's publish time.
- `ValidateOnly` and `ReadOnly` refusing version 2 by migration name, and fresh version 3 creation.
- A timestamp guard per backend: drive every write path of the store with a fake clock and assert, from the
  database, that every row of every table carries a creation time, and that every update moved the update time
  and left the creation time alone.
- The listing returning both times, and accounts, commerce and world store column adds on old-shape tables.
- SQL Server facts run in CI. The catalog and accounts jobs already exist. A new job runs the journal, world
  store and commerce SQL Server facts against a disposable SQL Server service, failing when a selected fact
  skips, the same standing as the existing two.

## Rollout

- Engine 20.14.0, a minor: two schema versions and new API surface. The CHANGELOG names the three migrations
  and the one way door.
- One way door. Once a database is on version 3, a build on an older engine refuses it, so rolling a game back
  past its pin bump needs a database restore. Version 2 was the same.
- A consumer's hosted catalog opens `ValidateOnly`, so its deploy runs its catalog schema migration step first.
  A consumer's hosted journal that opens `AutoCreate` migrates on first boot behind the application lock.
