# KhaozEngine.Accounts.Sqlite

SQLite backend for `KhaozEngine.Accounts` (`IAccountStore`) over `Microsoft.Data.Sqlite`. The zero-infra dev, test
and single-node account store, on the same contract as `InMemoryAccountStore` and `KhaozEngine.Accounts.SqlServer`.

Opt-in, in NO umbrella. It references `KhaozEngine.Accounts`, `KhaozEngine.Sqlite` and the SQLite driver, and
nothing below it takes the driver.

```csharp
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.Sqlite;

using var accounts = new SqliteAccountStore("Data Source=accounts.db", whitelistOnCreate: false);
var bans = new AccountBanStore(accounts, TimeProvider.System);
await bans.LoadAsync();
```

`whitelistOnCreate` is required, with no default, and `WhitelistOnCreate` reports what the instance was built
with. Pass `new AccountTableOptions("grim_accounts")` to use a table other than `accounts`.

## The table

One flat table, Grimhollow's layout, so an existing Grimhollow database is adopted in place:

| Column | Fresh table | Meaning |
|---|---|---|
| `subject` | `TEXT NOT NULL COLLATE BINARY PRIMARY KEY` | `{ProviderId}:{ProviderSubject}` |
| `display_name` | `TEXT NULL` | the name last seen, null when none was given |
| `whitelisted` | `INTEGER NOT NULL` | 1 when past the whitelist gate |
| `banned` | `INTEGER NOT NULL` | 1 when a ban is filed, lapsed or not |
| `ban_reason` | `TEXT NULL` | the filed reason |
| `ban_until` | `TEXT NULL` | the expiry as round-trip UTC text (`"o"`), null for permanent |

SQLite's `TEXT` has no width. The 128, 128 and 256 limits are `AccountStoreRules`, applied before every write.

The constructor creates the table when it is absent. When it is present, the table must carry the first four
columns, and a missing `ban_reason` or `ban_until` is added as a nullable column. Nothing is renamed, dropped or
backfilled, and no column the store does not own is read or written, so a game's own column (Grimhollow's `debug`)
keeps its values and its default. The ensure runs in one immediate transaction and is idempotent.

The subject key must compare as `BINARY`. The constructor refuses, and changes nothing in, a table whose primary key
or unique index on `subject` alone uses another collation, such as `NOCASE` or `RTRIM`. Find-or-create's
`ON CONFLICT(subject)` matches under the key's own collation, so over a `NOCASE` key holding `oidc:Alice` a sign-in
as `oidc:alice` would overwrite Alice's display name and return her account. A `BINARY` key over a column declared
with another collation is adopted, because the key is what the upsert matches on. The refusal names the collation
and no subject.

## Legacy rows

- **`display_name NOT NULL`.** Grimhollow's table declares it. On such a table a sign-in with no name stores an
  empty string, and an empty string reads back as `null`. A fresh table is nullable and stores what it is given.
- **`banned = 1` with no reason** reads as a ban with an empty reason, because `AccountBan.Reason` is never null.
- **A `ban_until` with an offset** reads back as the same instant in UTC, offset zero.

## Concurrency and ordering

One held connection that is never pooled, through `SqliteStoreConnection`, with every operation serialized behind
its lease. Find-or-create is one `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` statement, so concurrent first
sign-ins produce one row in this process or across processes on one file. Every writing statement returns the
row as it stands afterwards from the same statement. Find-or-create runs in a transaction and checks the returned
subject ordinally against the one it minted. A mismatch, which only a table changed after the store opened can
produce, rolls the write back and throws `InvalidOperationException` quoting neither subject.

Every comparison and ordering names `COLLATE BINARY`, which is SQLite's default, so subjects are case sensitive and
listings run in code-point order even over a column declared with another collation. SQLite compares UTF-8 bytes,
which matches .NET ordinal order for every subject short of one mixing a supplementary character with a
`U+E000` to `U+FFFF` one at the same position.

The table name is configuration, never SQL. It must be a plain identifier (ASCII letters, digits and underscores,
not starting with a digit, at most 128 characters) and is quoted. Every value is a bound parameter. The store logs
nothing, and no message names a subject, a display name, a reason or the connection string.

Dispose the store to close the database. The connection is never pooled, so the file is released on dispose.
