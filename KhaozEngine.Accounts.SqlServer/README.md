# KhaozEngine.Accounts.SqlServer

SQL Server and Azure SQL backend for `KhaozEngine.Accounts` (`IAccountStore`) over `Microsoft.Data.SqlClient`. The
production and shared account store, on the same contract as `InMemoryAccountStore` and
`KhaozEngine.Accounts.Sqlite`.

Opt-in, in NO umbrella. It references `KhaozEngine.Accounts` and the SQL Server driver, and nothing below it takes
the driver.

```csharp
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.SqlServer;

var accounts = new SqlServerAccountStore(connectionString, whitelistOnCreate: false,
    new SqlServerAccountStoreOptions(SchemaMode: AccountSchemaMode.ValidateOnly));
var bans = new AccountBanStore(accounts, TimeProvider.System);
await bans.LoadAsync();   // the first call, so the bootstrap runs here
```

`whitelistOnCreate` is required, with no default, and `WhitelistOnCreate` reports what the instance was built
with. `SqlServerAccountStoreOptions` names the schema (`dbo`), the table (`accounts`) and the schema mode
(`AutoCreate`). The schema must already exist.

## Lazy bootstrap and the two schema modes

The constructor validates the names and opens nothing. The first call opens a connection and brings the table to
the engine layout, so an auto-paused Azure SQL database costs that one caller a wait instead of costing the host
its start. A failed bootstrap is retried by the next call.

- **`AutoCreate`** creates the table when absent and adds a missing `ban_reason` or `ban_until` when present, in
  one transaction under an exclusive application lock, then checks the result. It needs DDL rights.
- **`ValidateOnly`** reads `sys.columns` and refuses a missing table or a mismatched one, with no DDL and no lock.
  The runtime identity then needs only `SELECT`, `INSERT` and `UPDATE` on the table, which is what
  `SECURITY-BASELINE.md` asks of a runtime identity. Run once under `AutoCreate` with a migration identity first.

Both modes end with the same check: every owned column present, `nvarchar` for the text columns at no less than
the limits, `bit` for the flags and `datetimeoffset` for the expiry. The message names columns, never rows.

## The table

One flat table, Grimhollow's layout, so an existing `dbo.accounts` is adopted in place:

| Column | Fresh table | Meaning |
|---|---|---|
| `subject` | `NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY` | `{ProviderId}:{ProviderSubject}` |
| `display_name` | `NVARCHAR(128) NULL` | the name last seen, null when none was given |
| `whitelisted` | `BIT NOT NULL` | past the whitelist gate |
| `banned` | `BIT NOT NULL` | a ban is filed, lapsed or not |
| `ban_reason` | `NVARCHAR(256) NULL` | the filed reason |
| `ban_until` | `DATETIMEOFFSET(7) NULL` | the expiry in UTC, null for permanent |

The widths are the `AccountStoreRules` limits, which the store also applies before every write. The ensure is
additive only, guarded by `OBJECT_ID` and `COL_LENGTH`. Nothing is renamed, dropped or backfilled, and no column
the store does not own is read or written, so a game's own column (Grimhollow's `debug`) keeps its values and its
default. Every write returns the row through `OUTPUT`, so a table with an enabled trigger is not supported.

## Legacy rows and collation

- **`display_name NOT NULL`.** Grimhollow's table declares it. On such a table a sign-in with no name stores an
  empty string, and an empty string reads back as `null`. A fresh table is nullable and stores what it is given.
- **`banned = 1` with no reason** reads as a ban with an empty reason, because `AccountBan.Reason` is never null.
- **A `ban_until` with an offset** reads back as the same instant in UTC, offset zero.
- **Collation.** A fresh table pins `Latin1_General_100_BIN2` on `subject`. An existing table keeps its collation,
  usually the case-insensitive database default. The store still answers exactly: an exact match compares the
  subject's bytes, beside the plain equality an index seeks on, and every ordering names the binary collation, so
  listings run in ordinal order on any table. What a case-insensitive table cannot do is hold two subjects that
  differ only in case, and a first sign-in that would collide that way is refused with `InvalidOperationException`.
  Discord snowflakes are digits, so Grimhollow's rows never meet it.

## Concurrency

A fresh pooled connection per call and no in-process gate. Find-or-create is one `MERGE ... WITH (HOLDLOCK)`,
whose key-range lock stops two simultaneous first sign-ins from both inserting, retried on a deadlock. Concurrent
first sign-ins produce one row whether the racers are threads or hosts.

The schema and table names are configuration, never SQL. Each must be a plain identifier (ASCII letters, digits and
underscores, not starting with a digit, at most 128 characters) and is bracket-quoted. Every value is a bound,
typed parameter. The store logs nothing, and no message names a subject, a display name, a reason or the connection
string. A duplicate-key failure is rethrown without the provider's exception, whose message quotes the key.

## Testing this backend

The shared conformance suite and the Grimhollow layout fixtures run against this backend only when
`KE_ACCOUNTS_SQLSERVER` holds a connection string whose initial catalog contains `-accounts-test-`, because the
fixtures create and drop tables in it. CI's `accounts-sqlserver` job runs them against a disposable
`mcr.microsoft.com/mssql/server:2022-latest` service and fails unless every fact executed. Locally, start a
throwaway container with that image, create a database such as `khaoz-accounts-test-local`, point the variable at
it, and remove the container afterwards.
